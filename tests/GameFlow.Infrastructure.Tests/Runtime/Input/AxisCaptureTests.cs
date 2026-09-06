using System.Text.Json;
using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Runtime.Input;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Input;

/// <summary>
/// The analog half of per-device calibration.
///
/// <para>
/// The case that drove it: a PS2 pad behind a PS2-to-PS3 converter
/// presents the DualShock 3's VID/PID, so SDL applies the DualShock 3
/// mapping — but the converter puts the right stick on the axes that
/// mapping calls the triggers. Moving the right stick pulled L2, and the
/// right stick itself read dead. No amount of button remapping reaches
/// that, because an axis is not a button, and half of it is unreachable
/// even through the profile-level mapping rules: a trigger source is
/// clamped to 0..1, so a stick arriving on one loses its whole negative
/// half before any rule can see it.
/// </para>
/// </summary>
public sealed class AxisCaptureTests
{
    private const short Max = 32767;
    private const short Min = short.MinValue;

    private static HashSet<int> Buttons(params int[] indices) => [.. indices];

    // ─── Resolving a binding ──────────────────────────────────────────

    [Fact]
    public void AFullAxisKeepsBothHalves()
    {
        var binding = AnalogBinding.FromAxis(0, AxisRange.Full);

        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, 0, false), 3);
        Assert.Equal(-1f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
    }

    [Fact]
    public void InvertNegatesTheRawReading()
    {
        var binding = AnalogBinding.FromAxis(0, AxisRange.Full, invert: true);

        Assert.Equal(-1f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
    }

    [Fact]
    public void AHalfAxisRestsAtZeroAndDiscardsTheNegativeSide()
    {
        var binding = AnalogBinding.FromAxis(0, AxisRange.Half);

        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, 0, false), 3);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
    }

    [Fact]
    public void AnInvertedHalfAxisReadsTheNegativeSide()
    {
        // Invert applies before the range, which is what lets one Half
        // cover a trigger that travels negative — no second enum value
        // for the mirrored case.
        var binding = AnalogBinding.FromAxis(0, AxisRange.Half, invert: true);

        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
    }

    [Fact]
    public void AUnipolarAxisSpansItsWholeTravelAsZeroToOne()
    {
        var binding = AnalogBinding.FromAxis(0, AxisRange.Unipolar);

        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
        Assert.Equal(0.5f, AxisMapEvaluator.Resolve(binding, 0, false), 2);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
    }

    [Fact]
    public void AnInvertedUnipolarAxisRestsAtThePositiveEnd()
    {
        var binding = AnalogBinding.FromAxis(0, AxisRange.Unipolar, invert: true);

        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
    }

    [Fact]
    public void AButtonSourceIsFullyOnOrFullyOff()
    {
        // The "static push on, push off" trigger: a pad whose L2 is a
        // switch still has to produce a trigger value, or a game reading
        // the analog axis sees nothing at all.
        var binding = AnalogBinding.FromButton(7);

        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, 0, true), 3);
        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, 0, false), 3);
    }

    [Fact]
    public void AnInvertedButtonSourceIsNormallyClosed()
    {
        var binding = AnalogBinding.FromButton(7, invert: true);

        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, 0, true), 3);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, 0, false), 3);
    }

    [Fact]
    public void ASilencedTargetIgnoresEveryReading()
    {
        Assert.Equal(0f, AxisMapEvaluator.Resolve(AnalogBinding.Silenced, Max, true), 3);
    }

    [Fact]
    public void TheMostNegativeRawValueIsPinnedToMinusOne()
    {
        // short.MinValue is one step past +32767's mirror; dividing it
        // out lands at -1.00003 and escapes the clamp callers assume.
        Assert.Equal(-1f, AxisMapEvaluator.Normalize(Min));
    }

    // ─── Capturing a stick ────────────────────────────────────────────

    [Fact]
    public void RestingJitterDoesNotAnswerAPrompt()
    {
        Assert.Null(AxisCapture.DetectStickAxis([0, 0, 0, 0], [80, -120, 40, 0]));
    }

    [Fact]
    public void TheAxisThatMovedFurthestWins()
    {
        // A stick pushed off-square moves both of its axes. The larger
        // deflection is the one the prompt asked for.
        var binding = AxisCapture.DetectStickAxis([0, 0, 0, 0], [0, 0, Max, 14000]);

        Assert.Equal(AnalogSourceKind.Axis, binding?.Kind);
        Assert.Equal(2, binding?.Index);
    }

    [Fact]
    public void AStickThatTravelsNegativeIsCapturedInverted()
    {
        // "Push up" on nearly every pad drives Y to its negative end,
        // and the canonical Y is positive up.
        var binding = AxisCapture.DetectStickAxis([0, 0, 0, 0], [0, Min, 0, 0]);

        Assert.Equal(1, binding?.Index);
        Assert.True(binding?.Invert);
        Assert.Equal(AxisRange.Full, binding?.Range);
    }

    [Fact]
    public void AStickThatTravelsPositiveIsCapturedUpright()
    {
        var binding = AxisCapture.DetectStickAxis([0, 0, 0, 0], [Max, 0, 0, 0]);

        Assert.Equal(0, binding?.Index);
        Assert.False(binding?.Invert);
    }

    [Fact]
    public void MovementIsMeasuredFromTheBaselineNotFromCentre()
    {
        // An axis already parked off-centre when the prompt appeared —
        // a trigger at rest, or a stick someone is still holding — has
        // not moved, and must not answer.
        Assert.Null(AxisCapture.DetectStickAxis([Min, 0], [Min, 0]));
    }

    // ─── Capturing a trigger ──────────────────────────────────────────

    [Fact]
    public void ATriggerRestingAtCentreIsCapturedAsAHalfAxis()
    {
        var binding = AxisCapture.DetectTriggerAxis([0, 0], [0, Max]);

        Assert.Equal(1, binding?.Index);
        Assert.Equal(AxisRange.Half, binding?.Range);
        Assert.False(binding?.Invert);
    }

    [Fact]
    public void ATriggerRestingAtTheNegativeEndIsCapturedAsUnipolar()
    {
        var binding = AxisCapture.DetectTriggerAxis([Min, 0], [Max, 0]);

        Assert.Equal(0, binding?.Index);
        Assert.Equal(AxisRange.Unipolar, binding?.Range);
        Assert.False(binding?.Invert);
    }

    [Fact]
    public void ATriggerRestingAtThePositiveEndIsCapturedAsInvertedUnipolar()
    {
        var binding = AxisCapture.DetectTriggerAxis([Max, 0], [Min, 0]);

        Assert.Equal(AxisRange.Unipolar, binding?.Range);
        Assert.True(binding?.Invert);
    }

    [Fact]
    public void ACapturedUnipolarTriggerReadsZeroWhereItRested()
    {
        // End to end: whatever the capture decided has to resolve back
        // to 0 at the resting value it was captured from, or the pad
        // reports a trigger held down that nobody is touching.
        var binding = AxisCapture.DetectTriggerAxis([Min], [Max])!.Value;

        Assert.Equal(0f, AxisMapEvaluator.Resolve(binding, Min, false), 3);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(binding, Max, false), 3);
    }

    [Fact]
    public void ADigitalTriggerIsCapturedFromItsButton()
    {
        var binding = AxisCapture.DetectTriggerButton(Buttons(), Buttons(6));

        Assert.Equal(AnalogSourceKind.Button, binding?.Kind);
        Assert.Equal(6, binding?.Index);
    }

    [Fact]
    public void AButtonAlreadyHeldDoesNotAnswerATriggerPrompt()
    {
        Assert.Null(AxisCapture.DetectTriggerButton(Buttons(6), Buttons(6)));
    }

    // ─── Waiting for a release before listening ───────────────────────

    [Fact]
    public void ReleasingAControlIsNotAtRestUntilItHasActuallyReturned()
    {
        // The bug this guards: a prompt that starts listening while the
        // previous control is still held gets answered by the release.
        // Letting go of a stick is a full-scale movement — bigger than
        // the deliberate push that answered the last prompt — so the
        // next prompt was answered before the user could reach for
        // anything.
        short[] rest = [0, 0, 0, 0];

        Assert.False(AxisCapture.IsAtRest(rest, [Max, 0, 0, 0]));
        Assert.False(AxisCapture.IsAtRest(rest, [Min, 0, 0, 0]));
        Assert.True(AxisCapture.IsAtRest(rest, [0, 0, 0, 0]));
    }

    [Fact]
    public void AStickThatOvershootsPastCentreStillCountsAsReleased()
    {
        // A released stick snaps back and can cross centre before it
        // settles. Refusing to arm on that would read as the wizard
        // ignoring the controller.
        Assert.True(AxisCapture.IsAtRest([0, 0], [-4000, 3200]));
    }

    [Fact]
    public void ATriggerRestingAtAnExtremeIsAtRestThere()
    {
        // Rest is per-axis, not "near zero" — a unipolar trigger idles
        // at one end of its travel and is released the whole time.
        Assert.True(AxisCapture.IsAtRest([Min, 0], [Min, 0]));
        Assert.False(AxisCapture.IsAtRest([Min, 0], [Max, 0]));
    }

    [Fact]
    public void RestToleranceCannotSwallowADeliberateMovement()
    {
        // Anything inside the resting tolerance must stay below the
        // threshold that answers a prompt, or arming and capturing would
        // overlap and a released control could answer on its own.
        Assert.True(AxisCapture.RestTolerance < AxisCapture.MoveThreshold);
    }

    [Fact]
    public void OnlyJitterCountsAsQuiet()
    {
        Assert.True(AxisCapture.IsQuiet([0, 0], [120, -300]));
        Assert.False(AxisCapture.IsQuiet([0, 0], [0, 9000]));
    }

    [Fact]
    public void AxisCountsThatDisagreeAreComparedAgainstZero()
    {
        // A shorter reference must not throw or silently pass: it means
        // the device reported fewer axes when the reference was taken.
        Assert.True(AxisCapture.IsAtRest([], [0, 0]));
        Assert.False(AxisCapture.IsAtRest([], [0, Max]));
    }

    // ─── The converter case, end to end ───────────────────────────────

    [Fact]
    public void AScrambledRightStickResolvesCorrectlyOnceRebound()
    {
        // The reported failure: the converter puts the right stick on
        // axes 3 and 4, which SDL's DualShock 3 mapping believes are the
        // triggers.
        var map = new DeviceButtonMap
        {
            DeviceId = "sdl-gamepad-test",
            Axes =
            {
                [AnalogTarget.RightStickX] = AxisCapture.DetectStickAxis([0, 0, 0, 0, 0], [0, 0, 0, Max, 0])!.Value,
                [AnalogTarget.RightStickY] = AxisCapture.DetectStickAxis([0, 0, 0, 0, 0], [0, 0, 0, 0, Min])!.Value,
                [AnalogTarget.LeftTrigger] = AnalogBinding.Silenced,
            },
        };

        var x = map.Axes[AnalogTarget.RightStickX];
        var y = map.Axes[AnalogTarget.RightStickY];

        Assert.Equal(3, x.Index);
        Assert.Equal(4, y.Index);

        // Stick pushed right and up reads +1 on both, in the snapshot's
        // Y-up convention.
        Assert.Equal(1f, AxisMapEvaluator.Resolve(x, Max, false), 3);
        Assert.Equal(1f, AxisMapEvaluator.Resolve(y, Min, false), 3);

        // ...and pushed left and down, the half that a trigger source
        // would have thrown away.
        Assert.Equal(-1f, AxisMapEvaluator.Resolve(x, Min, false), 3);
        Assert.Equal(-1f, AxisMapEvaluator.Resolve(y, Max, false), 3);

        // The trigger the stick used to drive now reports nothing.
        Assert.Equal(0f, AxisMapEvaluator.Resolve(map.Axes[AnalogTarget.LeftTrigger], Max, true), 3);
    }

    // ─── Persistence ──────────────────────────────────────────────────

    [Fact]
    public void AnAxisMapSurvivesARoundTrip()
    {
        var map = new DeviceButtonMap
        {
            DeviceId = "sdl-gamepad-test",
            Buttons = { [ButtonId.South] = 2 },
            Hats = { [ButtonId.DpadUp] = new HatDirectionBinding(0, 1) },
            Axes =
            {
                [AnalogTarget.RightStickY] = AnalogBinding.FromAxis(4, AxisRange.Full, invert: true),
                [AnalogTarget.RightTrigger] = AnalogBinding.FromButton(9),
                [AnalogTarget.LeftTrigger] = AnalogBinding.Silenced,
            },
        };

        var json = JsonSerializer.Serialize(map, ProfileJsonOptions.Default);
        var loaded = JsonSerializer.Deserialize<DeviceButtonMap>(json, ProfileJsonOptions.Default)!;

        Assert.Equal(map.Axes[AnalogTarget.RightStickY], loaded.Axes[AnalogTarget.RightStickY]);
        Assert.Equal(map.Axes[AnalogTarget.RightTrigger], loaded.Axes[AnalogTarget.RightTrigger]);
        Assert.Equal(map.Axes[AnalogTarget.LeftTrigger], loaded.Axes[AnalogTarget.LeftTrigger]);
        Assert.Equal(2, loaded.Buttons[ButtonId.South]);
    }

    [Fact]
    public void AMapWrittenBeforeAnalogCalibrationExistedStillLoads()
    {
        // Persisted button maps predate the axes field. Tolerating its
        // absence is what keeps an existing device-button-maps.json
        // working after an upgrade.
        const string legacy = """
            { "deviceId": "sdl-gamepad-test", "buttons": { "South": 2 }, "hats": {} }
            """;

        var loaded = JsonSerializer.Deserialize<DeviceButtonMap>(legacy, ProfileJsonOptions.Default)!;

        Assert.Empty(loaded.Axes);
        Assert.False(loaded.IsEmpty);
        Assert.Equal(2, loaded.Buttons[ButtonId.South]);
    }

    [Fact]
    public void AMapCarryingOnlyAxesIsNotEmpty()
    {
        var map = new DeviceButtonMap
        {
            DeviceId = "sdl-gamepad-test",
            Axes = { [AnalogTarget.LeftStickX] = AnalogBinding.FromAxis(0, AxisRange.Full) },
        };

        Assert.False(map.IsEmpty);
        Assert.Equal(map.Axes[AnalogTarget.LeftStickX], map.Clone().Axes[AnalogTarget.LeftStickX]);
    }
}
