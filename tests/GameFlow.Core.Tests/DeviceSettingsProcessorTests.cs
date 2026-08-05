using System.Text.Json;
using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Core.Tests;

public sealed class DeviceSettingsProcessorTests
{
    [Fact]
    public void DeadzoneIsRadial_NotPerAxis()
    {
        // The whole point of radial shaping: a diagonal push and a
        // straight push should escape the deadzone at the SAME physical
        // distance. Per-axis deadzoning gives a square dead region, where
        // a diagonal of the same magnitude behaves differently — the
        // classic "diagonals feel wrong" bug.
        var settings = new StickSettings { Deadzone = 0.2f };

        // Magnitude 0.25 straight right — just outside the deadzone.
        var straight = DeviceSettingsProcessor.ApplyStick(new StickVector(0.25f, 0f), settings);
        // Magnitude 0.25 at 45° — same distance, diagonal.
        var diagonalComponent = 0.25f / MathF.Sqrt(2f);
        var diagonal = DeviceSettingsProcessor.ApplyStick(
            new StickVector(diagonalComponent, diagonalComponent), settings);

        Assert.True(straight.Magnitude > 0f, "straight push should clear the deadzone");
        Assert.True(diagonal.Magnitude > 0f, "diagonal push of equal magnitude should clear it too");
        Assert.Equal(straight.Magnitude, diagonal.Magnitude, precision: 4);
    }

    [Fact]
    public void InsideDeadzone_ReadsExactlyZero()
    {
        var settings = new StickSettings { Deadzone = 0.3f };
        var result = DeviceSettingsProcessor.ApplyStick(new StickVector(0.2f, 0.1f), settings);

        Assert.Equal(0f, result.X, precision: 5);
        Assert.Equal(0f, result.Y, precision: 5);
    }

    [Fact]
    public void DirectionIsPreservedThroughShaping()
    {
        var settings = new StickSettings { Deadzone = 0.1f };
        var input = new StickVector(0.6f, 0.3f); // 2:1 ratio
        var result = DeviceSettingsProcessor.ApplyStick(input, settings);

        // The magnitude changes, but the angle must not.
        var inputAngle = MathF.Atan2(input.Y, input.X);
        var resultAngle = MathF.Atan2(result.Y, result.X);
        Assert.Equal(inputAngle, resultAngle, precision: 4);
    }

    [Fact]
    public void FullAtRescalesSoAWornStickStillReachesMaximum()
    {
        // A stick that physically only reaches 0.8 should still deliver
        // a full 1.0 to the game once FullAt is set to match.
        var settings = new StickSettings { Deadzone = 0f, FullAt = 0.8f };
        var result = DeviceSettingsProcessor.ApplyStick(new StickVector(0.8f, 0f), settings);

        Assert.Equal(1f, result.Magnitude, precision: 3);
    }

    [Fact]
    public void JustOutsideDeadzone_StartsNearZeroNotAtTheDeadzoneValue()
    {
        // Output must rescale from 0 at the deadzone edge — not jump
        // straight to the raw magnitude, which would make the stick
        // lurch the instant it engages.
        var settings = new StickSettings { Deadzone = 0.25f };
        var result = DeviceSettingsProcessor.ApplyStick(new StickVector(0.26f, 0f), settings);

        Assert.InRange(result.Magnitude, 0f, 0.05f);
    }

    [Fact]
    public void AntiDeadzoneLiftsTheFloorPastAGamesOwnDeadzone()
    {
        var plain = new StickSettings { Deadzone = 0f };
        var lifted = new StickSettings { Deadzone = 0f, AntiDeadzone = 0.3f };

        var small = new StickVector(0.05f, 0f);
        var plainResult = DeviceSettingsProcessor.ApplyStick(small, plain);
        var liftedResult = DeviceSettingsProcessor.ApplyStick(small, lifted);

        Assert.True(liftedResult.Magnitude > plainResult.Magnitude);
        Assert.True(liftedResult.Magnitude >= 0.3f, "a real movement should land at or above the anti-deadzone floor");
    }

    [Theory]
    [InlineData(StickCurve.Precision, true)]   // squared -> smaller at mid-travel
    [InlineData(StickCurve.Aggressive, false)] // sqrt    -> larger at mid-travel
    public void CurvesBendMidTravelInTheExpectedDirection(StickCurve curve, bool expectSmallerThanLinear)
    {
        var linear = new StickSettings { Deadzone = 0f, Curve = StickCurve.Linear };
        var curved = new StickSettings { Deadzone = 0f, Curve = curve };
        var halfway = new StickVector(0.5f, 0f);

        var linearResult = DeviceSettingsProcessor.ApplyStick(halfway, linear).Magnitude;
        var curvedResult = DeviceSettingsProcessor.ApplyStick(halfway, curved).Magnitude;

        if (expectSmallerThanLinear)
        {
            Assert.True(curvedResult < linearResult);
        }
        else
        {
            Assert.True(curvedResult > linearResult);
        }
    }

    [Fact]
    public void CurvesPreserveBothEndpoints()
    {
        // Whatever the curve does in between, centre must stay centre and
        // full must stay full — otherwise the stick either drifts or can
        // no longer reach maximum.
        foreach (var curve in new[] { StickCurve.Linear, StickCurve.Precision, StickCurve.Aggressive })
        {
            var settings = new StickSettings { Deadzone = 0f, Curve = curve };
            Assert.Equal(0f, DeviceSettingsProcessor.ApplyStick(StickVector.Zero, settings).Magnitude, precision: 4);
            Assert.Equal(1f, DeviceSettingsProcessor.ApplyStick(new StickVector(1f, 0f), settings).Magnitude, precision: 3);
        }
    }

    [Fact]
    public void InvertFlipsEachAxisIndependently()
    {
        var settings = new StickSettings { Deadzone = 0f, InvertY = true };
        var result = DeviceSettingsProcessor.ApplyStick(new StickVector(0.5f, 0.5f), settings);

        Assert.True(result.X > 0f, "X untouched");
        Assert.True(result.Y < 0f, "Y inverted");
    }

    [Fact]
    public void TriggerDeadzoneAndSaturationRescale()
    {
        var settings = new TriggerSettings { Deadzone = 0.1f, FullAt = 0.9f };

        Assert.Equal(0f, DeviceSettingsProcessor.ApplyTrigger(0.05f, settings), precision: 4);
        Assert.Equal(0f, DeviceSettingsProcessor.ApplyTrigger(0.1f, settings), precision: 4);
        Assert.Equal(1f, DeviceSettingsProcessor.ApplyTrigger(0.9f, settings), precision: 3);
        Assert.Equal(1f, DeviceSettingsProcessor.ApplyTrigger(1.0f, settings), precision: 3);
    }

    [Fact]
    public void TriggerInvertMakesRestFullAndPullEmpty()
    {
        var settings = new TriggerSettings { Invert = true };

        Assert.Equal(1f, DeviceSettingsProcessor.ApplyTrigger(0f, settings), precision: 3);
        Assert.Equal(0f, DeviceSettingsProcessor.ApplyTrigger(1f, settings), precision: 3);
    }

    [Fact]
    public void DefaultSettingsAreIdentity_SoTheTickPathCanSkipThem()
    {
        Assert.True(DeviceSettingsProcessor.IsIdentity(DeviceSettings.Default));
    }

    [Fact]
    public void DefaultSettingsSurviveAJsonRoundTripAsIdentity()
    {
        // Tuning persists to disk, so a device saved at defaults comes back
        // as a NEW instance — never reference-equal to DeviceSettings.Default.
        // It has to take the same skip path and produce the same signal, or
        // one untuned pad feels different from another purely because one
        // had been written out and reloaded.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var revived = JsonSerializer.Deserialize<DeviceSettings>(
            JsonSerializer.Serialize(DeviceSettings.Default, options), options);

        Assert.NotNull(revived);
        Assert.False(ReferenceEquals(DeviceSettings.Default, revived));
        Assert.True(DeviceSettingsProcessor.IsIdentity(revived));

        // And the maths agrees with the flag: a small real movement passes
        // through untouched rather than being swallowed.
        var small = new StickVector(0.03f, 0f);
        Assert.Equal(small.Magnitude, DeviceSettingsProcessor.ApplyStick(small, revived.LeftStick).Magnitude, precision: 4);
    }

    [Fact]
    public void AnyRealTuningIsNotIdentity()
    {
        Assert.False(DeviceSettingsProcessor.IsIdentity(
            DeviceSettings.Default with { LeftStick = new StickSettings { Deadzone = 0.15f } }));
        Assert.False(DeviceSettingsProcessor.IsIdentity(
            DeviceSettings.Default with { RightTrigger = new TriggerSettings { Invert = true } }));
    }

    [Fact]
    public void ApplyLeavesButtonsAndTimestampUntouched()
    {
        // Conditioning is about analog shaping only — it must not disturb
        // anything else on the snapshot.
        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());
        buttons[Enums.ButtonId.South] = true;
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var snapshot = new ControllerSnapshot
        {
            Buttons = buttons,
            Timestamp = stamp,
            LeftStick = new StickVector(0.5f, 0f)
        };

        var result = DeviceSettingsProcessor.Apply(
            snapshot, DeviceSettings.Default with { LeftStick = new StickSettings { Deadzone = 0.1f } });

        Assert.True(result.IsPressed(Enums.ButtonId.South));
        Assert.Equal(stamp, result.Timestamp);
    }

    [Fact]
    public void SameDeviceCanBeTunedDifferentlyPerSlot()
    {
        // The core reason settings are keyed by slot AND device: one pad,
        // two slots, two different feels.
        var twitchy = new StickSettings { Deadzone = 0f, Curve = StickCurve.Aggressive };
        var precise = new StickSettings { Deadzone = 0f, Curve = StickCurve.Precision };
        var sameInput = new StickVector(0.5f, 0f);

        var a = DeviceSettingsProcessor.ApplyStick(sameInput, twitchy).Magnitude;
        var b = DeviceSettingsProcessor.ApplyStick(sameInput, precise).Magnitude;

        Assert.True(a > b, "the same physical input should produce different output per tuning");
    }
}
