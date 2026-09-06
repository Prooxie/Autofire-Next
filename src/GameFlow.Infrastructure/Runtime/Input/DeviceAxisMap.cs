namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// One analog output of the canonical controller model: the four stick
/// half-axes and the two triggers. These are the values a
/// <see cref="DeviceButtonMap.Axes"/> entry can redirect.
///
/// <para>
/// Sign convention, which every binding resolves INTO: X is positive to
/// the right, Y is positive UP (the snapshot's convention, not SDL's,
/// which is Y-down), and a trigger is 0 at rest and 1 fully pulled.
/// </para>
/// </summary>
public enum AnalogTarget
{
    LeftStickX,
    LeftStickY,
    RightStickX,
    RightStickY,
    LeftTrigger,
    RightTrigger,
}

/// <summary>What an <see cref="AnalogBinding"/> reads from the device.</summary>
public enum AnalogSourceKind
{
    /// <summary>
    /// Nothing drives this target; it reads a constant 0.
    ///
    /// <para>
    /// Not the same as leaving the target unbound. Unbound keeps
    /// whatever SDL's own mapping produced; <see cref="None"/> silences
    /// it. That distinction is the point on a pad whose right stick
    /// lands on the axes SDL believes are the triggers — rebinding the
    /// stick does not stop SDL from also reporting that motion as L2, so
    /// the trigger has to be silenced explicitly.
    /// </para>
    /// </summary>
    None = 0,

    /// <summary>A raw joystick axis, by SDL's physical axis index.</summary>
    Axis = 1,

    /// <summary>
    /// A raw joystick button, by index — an on/off ("push on, push off")
    /// source. Pads whose L2/R2 are plain switches, including most
    /// PS2-to-USB and PS2-to-PS3 converters, have no trigger axis to
    /// bind at all.
    /// </summary>
    Button = 2,
}

/// <summary>
/// How much of a raw axis's travel carries the signal, and where it
/// rests. Applied AFTER <see cref="AnalogBinding.Invert"/>, so each range
/// needs only its one canonical orientation.
/// </summary>
public enum AxisRange
{
    /// <summary>Rests centred, travels both ways: −1..+1. A stick axis.</summary>
    Full = 0,

    /// <summary>Rests at 0, travels positive only: 0..+1. The negative half is discarded.</summary>
    Half = 1,

    /// <summary>Rests at −1 and travels to +1, rescaled to 0..+1. How many pads report a trigger.</summary>
    Unipolar = 2,
}

/// <summary>
/// Where one <see cref="AnalogTarget"/> gets its value on a specific
/// physical device.
/// </summary>
/// <param name="Kind">Axis, button, or explicitly silenced.</param>
/// <param name="Index">Raw axis or button index, per <paramref name="Kind"/>. −1 when <see cref="AnalogSourceKind.None"/>.</param>
/// <param name="Range">Axis sources only — how the raw travel maps onto the target's range.</param>
/// <param name="Invert">
/// Negates the raw reading before <paramref name="Range"/> is applied
/// (for a button source, swaps pressed and released). Applying it first
/// is what lets a single <see cref="AxisRange.Half"/> cover an axis that
/// travels negative, and a single <see cref="AxisRange.Unipolar"/> cover
/// one that rests at +1 instead of −1.
/// </param>
public readonly record struct AnalogBinding(
    AnalogSourceKind Kind,
    int Index,
    AxisRange Range,
    bool Invert)
{
    /// <summary>A target driven to a constant 0 — see <see cref="AnalogSourceKind.None"/>.</summary>
    public static AnalogBinding Silenced { get; } = new(AnalogSourceKind.None, -1, AxisRange.Full, false);

    public static AnalogBinding FromAxis(int index, AxisRange range, bool invert = false) =>
        new(AnalogSourceKind.Axis, index, range, invert);

    public static AnalogBinding FromButton(int index, bool invert = false) =>
        new(AnalogSourceKind.Button, index, AxisRange.Full, invert);
}

/// <summary>
/// Resolves an <see cref="AnalogBinding"/> against one raw reading. Pure,
/// allocation-free, and deliberately unaware of SDL: the caller does the
/// single device read the binding's <see cref="AnalogBinding.Kind"/> calls
/// for and hands the value in, so this stays testable on a path that runs
/// per device per tick.
/// </summary>
public static class AxisMapEvaluator
{
    /// <summary>
    /// Resolves to the target's canonical value. Only the reading that
    /// matches <paramref name="binding"/>'s kind is consulted, so the
    /// caller may pass a default for the other.
    /// </summary>
    public static float Resolve(in AnalogBinding binding, short axisValue, bool buttonPressed)
    {
        switch (binding.Kind)
        {
            case AnalogSourceKind.Button:
                return (binding.Invert ? !buttonPressed : buttonPressed) ? 1f : 0f;

            case AnalogSourceKind.Axis:
                var value = Normalize(axisValue);
                if (binding.Invert)
                {
                    value = -value;
                }
                return binding.Range switch
                {
                    AxisRange.Half => Math.Clamp(value, 0f, 1f),
                    AxisRange.Unipolar => Math.Clamp((value + 1f) * 0.5f, 0f, 1f),
                    _ => Math.Clamp(value, -1f, 1f),
                };

            default:
                return 0f;
        }
    }

    /// <summary>
    /// SDL's signed 16-bit axis to −1..+1. <see cref="short.MinValue"/>
    /// is one step past +32767's mirror, so it is pinned rather than
    /// left to divide out past −1.
    /// </summary>
    public static float Normalize(short value) =>
        value == short.MinValue ? -1f : Math.Clamp(value / 32767f, -1f, 1f);
}

/// <summary>
/// Rising-edge detection over raw axes for the stick/trigger calibration
/// wizard — the analog counterpart to <see cref="ButtonCapture"/>.
///
/// <para>
/// Every decision is made against a baseline sampled when the prompt
/// appeared, which is what lets one movement answer two questions at
/// once: which control moved, and what shape its travel is. An axis
/// resting at an extreme while nothing touches it is a
/// <see cref="AxisRange.Unipolar"/> trigger; one resting near centre is a
/// stick half-axis or a <see cref="AxisRange.Half"/> trigger. That cannot
/// be told from the live value alone.
/// </para>
/// </summary>
public static class AxisCapture
{
    /// <summary>
    /// Raw travel, in SDL axis units, before a control counts as moved
    /// (~37% of full deflection). Far above any resting jitter or the
    /// drift of a worn potentiometer, so a noisy axis cannot answer a
    /// prompt nobody touched, and low enough that a partial pull or a
    /// stick pushed off-square still registers.
    /// </summary>
    public const int MoveThreshold = 12000;

    /// <summary>
    /// Resting distance from centre, in raw units, above which an axis
    /// reads as a <see cref="AxisRange.Unipolar"/> trigger parked at one
    /// end of its travel rather than as a centred axis.
    /// </summary>
    public const int RestingExtreme = 16000;

    /// <summary>
    /// How far, in raw units, a control may sit from its resting
    /// position and still count as released. Generous enough to absorb
    /// the overshoot of a stick snapping back past centre, and well
    /// under <see cref="MoveThreshold"/> so a released control can never
    /// read as a deliberate movement.
    /// </summary>
    public const int RestTolerance = 8000;

    /// <summary>
    /// Sample-to-sample change, in raw units, below which the controls
    /// count as not moving. Only jitter lives under this.
    /// </summary>
    public const int QuietTolerance = 1500;

    /// <summary>
    /// True when every axis sits within <see cref="RestTolerance"/> of
    /// its resting value.
    ///
    /// <para>
    /// This is what a prompt waits for before it starts listening.
    /// Letting go of a stick is itself a full-scale movement — larger,
    /// usually, than the deliberate push that answered the previous
    /// prompt — so a prompt that armed the instant it appeared would be
    /// answered by the release of the control before it, leaving no room
    /// to move to the next one.
    /// </para>
    /// </summary>
    public static bool IsAtRest(IReadOnlyList<short> rest, IReadOnlyList<short> now) =>
        MaxDeviation(rest, now) <= RestTolerance;

    /// <summary>
    /// True when nothing moved appreciably between two samples. Used to
    /// decide when the pad has settled enough for its resting position
    /// to be worth recording.
    /// </summary>
    public static bool IsQuiet(IReadOnlyList<short> previous, IReadOnlyList<short> now) =>
        MaxDeviation(previous, now) <= QuietTolerance;

    private static int MaxDeviation(IReadOnlyList<short> from, IReadOnlyList<short> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var worst = 0;
        var count = Math.Max(from.Count, to.Count);
        for (var index = 0; index < count; index++)
        {
            var a = index < from.Count ? from[index] : (short)0;
            var b = index < to.Count ? to[index] : (short)0;
            worst = Math.Max(worst, Math.Abs(b - a));
        }
        return worst;
    }

    /// <summary>
    /// The axis that moved furthest from its baseline, or null when
    /// nothing crossed <see cref="MoveThreshold"/>. Bound as a full
    /// −1..+1 stick half-axis, inverted when the control travelled
    /// negative — the prompt always asks for the canonical positive
    /// direction (right, or up), so the sign of the travel IS the
    /// orientation.
    /// </summary>
    public static AnalogBinding? DetectStickAxis(IReadOnlyList<short> baseline, IReadOnlyList<short> now)
    {
        var index = LargestMove(baseline, now, out var delta);
        return index < 0 ? null : AnalogBinding.FromAxis(index, AxisRange.Full, delta < 0);
    }

    /// <summary>
    /// The axis that moved furthest from its baseline, read as a
    /// trigger: <see cref="AxisRange.Unipolar"/> when it was resting at
    /// an extreme, <see cref="AxisRange.Half"/> when it was resting near
    /// centre. Null when nothing crossed <see cref="MoveThreshold"/>.
    /// </summary>
    public static AnalogBinding? DetectTriggerAxis(IReadOnlyList<short> baseline, IReadOnlyList<short> now)
    {
        var index = LargestMove(baseline, now, out var delta);
        if (index < 0)
        {
            return null;
        }

        var rest = index < baseline.Count ? baseline[index] : (short)0;
        return Math.Abs((int)rest) >= RestingExtreme
            ? AnalogBinding.FromAxis(index, AxisRange.Unipolar, rest > 0)
            : AnalogBinding.FromAxis(index, AxisRange.Half, delta < 0);
    }

    /// <summary>
    /// The first button newly pressed between two samples, bound as an
    /// on/off trigger, or null. A caller prompting for a trigger should
    /// prefer <see cref="DetectTriggerAxis"/> when both fire: an analog
    /// trigger usually reports a digital button too, and that button
    /// closes early in the pull, so committing on it would quietly
    /// reduce a pressure-sensitive trigger to a switch.
    /// </summary>
    public static AnalogBinding? DetectTriggerButton(
        IReadOnlySet<int> pressedBefore,
        IReadOnlySet<int> pressedNow)
    {
        ArgumentNullException.ThrowIfNull(pressedBefore);
        ArgumentNullException.ThrowIfNull(pressedNow);

        foreach (var index in pressedNow)
        {
            if (!pressedBefore.Contains(index))
            {
                return AnalogBinding.FromButton(index);
            }
        }
        return null;
    }

    private static int LargestMove(IReadOnlyList<short> baseline, IReadOnlyList<short> now, out int delta)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(now);

        var best = -1;
        var bestMagnitude = MoveThreshold - 1;
        delta = 0;

        for (var index = 0; index < now.Count; index++)
        {
            var from = index < baseline.Count ? baseline[index] : (short)0;
            var moved = now[index] - from;
            var magnitude = Math.Abs(moved);
            if (magnitude > bestMagnitude)
            {
                bestMagnitude = magnitude;
                best = index;
                delta = moved;
            }
        }

        return best;
    }
}
