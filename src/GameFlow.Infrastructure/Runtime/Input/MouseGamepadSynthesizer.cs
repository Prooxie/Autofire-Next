using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Mouse input for one device since the last read: accumulated relative
/// movement (<see cref="Dx"/>/<see cref="Dy"/>, pixels) plus current
/// button levels. Movement is consumed (reset) on read so each pipeline
/// tick gets the delta since the previous tick.
/// </summary>
/// <param name="Dx">Accumulated horizontal movement since the last read (raw counts).</param>
/// <param name="Dy">Accumulated vertical movement since the last read (raw counts).</param>
/// <param name="Left">Left button is currently held.</param>
/// <param name="Right">Right button is currently held.</param>
/// <param name="Middle">Middle button (wheel click) is currently held.</param>
/// <param name="Button4">Side button 4 (back) is currently held.</param>
/// <param name="Button5">Side button 5 (forward) is currently held.</param>
/// <param name="WheelDelta">Accumulated vertical scroll since the last read,
/// in Windows wheel units (multiples of 120; positive = scroll up).</param>
public readonly record struct MouseFrame(
    int Dx, int Dy, bool Left, bool Right, bool Middle, bool Button4, bool Button5,
    int WheelDelta = 0);

/// <summary>Per-mouse-device source of movement + button state.</summary>
public interface IMouseStateSource
{
    /// <summary>Accumulated movement since the last call (resets) + current buttons.</summary>
    MouseFrame ReadMouseFrame(string deviceId);

    /// <summary>
    /// Combined frame across EVERY tracked mouse (summed deltas, OR'd
    /// buttons). Fallback for the composite-HID id mismatch — see
    /// <see cref="IKeyboardStateSource.GetPressedKeysAggregate"/>.
    /// </summary>
    MouseFrame ReadMouseFrameAggregate();
}

/// <summary>No-op default: every mouse reports no movement and no buttons.</summary>
public sealed class NullMouseStateSource : IMouseStateSource
{
    public MouseFrame ReadMouseFrameAggregate() => default;
    public MouseFrame ReadMouseFrame(string deviceId) => default;
}

/// <summary>
/// Turns a <see cref="MouseFrame"/> into a <see cref="ControllerSnapshot"/>:
/// movement drives the right stick (aim), LMB = right trigger (fire),
/// RMB = left trigger (aim-down-sights), middle = right-stick click,
/// side buttons = shoulders. Movement is divided by a sensitivity
/// (pixels for full deflection) and clamped; screen-Y is inverted so
/// moving the mouse up pushes the stick up.
/// </summary>
public static class MouseGamepadSynthesizer
{
    /// <summary>Pixels of movement (per tick) that equal full stick deflection.</summary>
    public const float DefaultSensitivity = 30f;

    public static ControllerSnapshot Synthesize(string deviceName, MouseFrame frame, float sensitivity = DefaultSensitivity)
    {
        if (sensitivity < 1f)
        {
            sensitivity = DefaultSensitivity;
        }

        float rx = Math.Clamp(frame.Dx / sensitivity, -1f, 1f);
        float ry = Math.Clamp(-frame.Dy / sensitivity, -1f, 1f); // screen Y down → stick up

        var buttons = ButtonMask.Empty;
        if (frame.Middle)  buttons[ButtonId.RightStick] = true;
        if (frame.Button4) buttons[ButtonId.LeftShoulder] = true;
        if (frame.Button5) buttons[ButtonId.RightShoulder] = true;

        return new ControllerSnapshot
        {
            DeviceName   = deviceName,
            RightStick   = new StickVector(rx, ry),
            LeftTrigger  = frame.Right ? 1f : 0f, // RMB → aim
            RightTrigger = frame.Left  ? 1f : 0f, // LMB → fire
            Buttons      = buttons,
        };
    }
}
