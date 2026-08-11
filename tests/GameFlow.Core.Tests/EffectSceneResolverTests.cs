using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Core.Tests;

public sealed class LightSceneTests
{
    private static LightingSettings Mode(LightbarMode mode, string color = "#0066FF", float brightness = 1f) =>
        new() { Mode = mode, Color = color, Brightness = brightness };

    [Fact]
    public void OffMeansNoColourAtAll()
    {
        // Null rather than black: the writer skips the LED entirely, which
        // is not the same as driving it to zero on hardware that treats
        // "off" and "black" differently.
        Assert.Null(EffectSceneResolver.ResolveLight(Mode(LightbarMode.Off), new EffectSceneContext(0)));
    }

    [Fact]
    public void SolidUsesTheConfiguredColourUnchanged()
    {
        var result = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Solid, "#FF8800"), new EffectSceneContext(12.34));
        Assert.Equal(new LightColor(0xFF, 0x88, 0x00), result);
    }

    [Fact]
    public void SolidDoesNotDriftWithTime()
    {
        var a = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Solid), new EffectSceneContext(0));
        var b = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Solid), new EffectSceneContext(9999));
        Assert.Equal(a, b);
    }

    [Fact]
    public void BrightnessScalesWhateverTheModeProduced()
    {
        var full = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Solid, "#FFFFFF"), new EffectSceneContext(0));
        var half = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Solid, "#FFFFFF", brightness: 0.5f), new EffectSceneContext(0));

        Assert.Equal(255, full!.Value.R);
        Assert.InRange(half!.Value.R, 120, 136);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EachOfTheFirstFourPlayersGetsADistinctColour(int index)
    {
        var mine = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.PlayerNumber), new EffectSceneContext(0, PlayerIndex: index));

        for (var other = 0; other < 4; other++)
        {
            if (other == index) { continue; }
            var theirs = EffectSceneResolver.ResolveLight(
                Mode(LightbarMode.PlayerNumber), new EffectSceneContext(0, PlayerIndex: other));
            Assert.NotEqual(theirs, mine);
        }
    }

    [Fact]
    public void PlayerColoursWrapInsteadOfGoingDark()
    {
        // Sixteen slots are supported and only four colours exist. An
        // unlit pad reads as broken, so slot 4 reuses slot 0's colour.
        var first = EffectSceneResolver.ResolveLight(Mode(LightbarMode.PlayerNumber), new EffectSceneContext(0, PlayerIndex: 0));
        var fifth = EffectSceneResolver.ResolveLight(Mode(LightbarMode.PlayerNumber), new EffectSceneContext(0, PlayerIndex: 4));

        Assert.Equal(first, fifth);
        Assert.NotEqual(LightColor.Black, fifth!.Value);
    }

    [Fact]
    public void ANegativePlayerIndexStillProducesAValidColour()
    {
        var result = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.PlayerNumber), new EffectSceneContext(0, PlayerIndex: -1));

        Assert.NotNull(result);
        Assert.NotEqual(LightColor.Black, result!.Value);
    }

    [Fact]
    public void BreathingNeverFadesCompletelyToBlack()
    {
        // A lightbar that goes fully dark looks like the pad disconnected.
        for (var t = 0.0; t < 8.0; t += 0.05)
        {
            var c = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Breathing, "#FFFFFF"), new EffectSceneContext(t))!.Value;
            Assert.True(c.R > 0, $"breathing went black at t={t}");
        }
    }

    [Fact]
    public void BreathingActuallyVariesAndReachesNearFull()
    {
        var samples = new List<byte>();
        for (var t = 0.0; t < 4.0; t += 0.05)
        {
            samples.Add(EffectSceneResolver.ResolveLight(
                Mode(LightbarMode.Breathing, "#FFFFFF"), new EffectSceneContext(t))!.Value.R);
        }

        Assert.True(samples.Max() > 240, "breathing should reach close to full brightness");
        Assert.True(samples.Min() < 80, "breathing should dim noticeably");
    }

    [Fact]
    public void StrobeIsOnAndOffInRoughlyEqualMeasure()
    {
        var on = 0;
        var total = 0;
        for (var t = 0.0; t < 5.0; t += 0.01)
        {
            var c = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Strobe, "#FFFFFF"), new EffectSceneContext(t))!.Value;
            if (c.R > 0) { on++; }
            total++;
        }

        var duty = (double)on / total;
        Assert.InRange(duty, 0.45, 0.55);
    }

    [Fact]
    public void RainbowVisitsAllThreePrimariesOverOneCycle()
    {
        bool sawRed = false, sawGreen = false, sawBlue = false;
        for (var t = 0.0; t < 6.0; t += 0.02)
        {
            var c = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Rainbow), new EffectSceneContext(t))!.Value;
            if (c.R > 200 && c.G < 80 && c.B < 80) { sawRed = true; }
            if (c.G > 200 && c.R < 80 && c.B < 80) { sawGreen = true; }
            if (c.B > 200 && c.R < 80 && c.G < 80) { sawBlue = true; }
        }

        Assert.True(sawRed && sawGreen && sawBlue, "rainbow should pass through red, green and blue");
    }

    [Theory]
    [InlineData(100, 0x22, 0xCC, 0x44)]  // healthy: green
    [InlineData(61, 0x22, 0xCC, 0x44)]
    [InlineData(60, 0xFF, 0xAA, 0x22)]   // getting low: amber
    [InlineData(26, 0xFF, 0xAA, 0x22)]
    [InlineData(25, 0xFF, 0x22, 0x22)]   // nearly flat: red
    [InlineData(3, 0xFF, 0x22, 0x22)]
    public void BatteryColourTracksCharge(int percent, byte r, byte g, byte b)
    {
        var result = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.BatteryLevel), new EffectSceneContext(0, BatteryPercent: percent));

        Assert.Equal(new LightColor(r, g, b), result);
    }

    [Fact]
    public void AnUnknownBatteryIsNotShownAsEmpty()
    {
        // "No reading" and "about to die" are different things, and showing
        // red for the first would send the user hunting for a charger.
        var unknown = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.BatteryLevel), new EffectSceneContext(0, BatteryPercent: null))!.Value;

        Assert.NotEqual(new LightColor(0xFF, 0x22, 0x22), unknown);
    }

    [Fact]
    public void ChargingAnimatesRatherThanSittingStill()
    {
        var context = new EffectSceneContext(0, BatteryPercent: 40, IsCharging: true);
        var later = new EffectSceneContext(1.0, BatteryPercent: 40, IsCharging: true);

        Assert.NotEqual(
            EffectSceneResolver.ResolveLight(Mode(LightbarMode.BatteryLevel), context),
            EffectSceneResolver.ResolveLight(Mode(LightbarMode.BatteryLevel), later));
    }

    [Fact]
    public void RumbleReactiveBrightensWithRumbleAndStaysLitAtRest()
    {
        var idle = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.RumbleReactive, "#FFFFFF"), new EffectSceneContext(0, RumbleLevel: 0))!.Value;
        var shaking = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.RumbleReactive, "#FFFFFF"), new EffectSceneContext(0, RumbleLevel: 1))!.Value;

        Assert.True(idle.R > 0, "an idle pad should still be lit");
        Assert.True(shaking.R > idle.R);
    }

    [Fact]
    public void TriggerReactiveGoesRedOnlyWhileHeld()
    {
        var released = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.TriggerReactive, "#0066FF"), new EffectSceneContext(0, TriggerLevel: 0))!.Value;
        var held = EffectSceneResolver.ResolveLight(
            Mode(LightbarMode.TriggerReactive, "#0066FF"), new EffectSceneContext(0, TriggerLevel: 1))!.Value;

        Assert.Equal(new LightColor(0x00, 0x66, 0xFF), released);
        Assert.True(held.R > held.B);
    }

    [Fact]
    public void AMalformedColourFallsBackInsteadOfThrowing()
    {
        // Profiles are hand-editable, so a bad colour is expected input.
        var result = EffectSceneResolver.ResolveLight(Mode(LightbarMode.Solid, "not-a-colour"), new EffectSceneContext(0));
        Assert.NotNull(result);
    }
}

public sealed class RumbleResolutionTests
{
    [Fact]
    public void DisabledSilencesBothMotors()
    {
        var (low, high) = EffectSceneResolver.ResolveRumble(
            new RumbleSettings { Enabled = false }, 1.0, 1.0);

        Assert.Equal(0, low);
        Assert.Equal(0, high);
    }

    [Fact]
    public void DefaultsPassThroughUnchanged()
    {
        var (low, high) = EffectSceneResolver.ResolveRumble(new RumbleSettings(), 0.5, 0.25);

        Assert.Equal(0.5, low, precision: 4);
        Assert.Equal(0.25, high, precision: 4);
    }

    [Fact]
    public void GainAboveOneAmplifiesAWeakPadButStillClamps()
    {
        var (low, _) = EffectSceneResolver.ResolveRumble(new RumbleSettings { Gain = 2f }, 0.3, 0);
        Assert.Equal(0.6, low, precision: 4);

        var (loud, _) = EffectSceneResolver.ResolveRumble(new RumbleSettings { Gain = 2f }, 0.8, 0);
        Assert.Equal(1.0, loud, precision: 4);
    }

    [Fact]
    public void SwapMotorsExchangesTheTwoChannels()
    {
        var (low, high) = EffectSceneResolver.ResolveRumble(
            new RumbleSettings { SwapMotors = true }, 1.0, 0.0);

        Assert.Equal(0.0, low, precision: 4);
        Assert.Equal(1.0, high, precision: 4);
    }

    [Fact]
    public void PerMotorGainAppliesAfterTheSwap()
    {
        // Order matters: the gains name PHYSICAL motors, so a swapped pad
        // must still apply LowFrequencyGain to whatever now drives the
        // heavy motor.
        var (low, high) = EffectSceneResolver.ResolveRumble(
            new RumbleSettings { SwapMotors = true, LowFrequencyGain = 0f }, 0.0, 1.0);

        Assert.Equal(0.0, low, precision: 4);
        Assert.Equal(0.0, high, precision: 4);
    }

    [Fact]
    public void OutOfRangeInputIsClamped()
    {
        var (low, high) = EffectSceneResolver.ResolveRumble(new RumbleSettings(), 5.0, -3.0);

        Assert.Equal(1.0, low, precision: 4);
        Assert.Equal(0.0, high, precision: 4);
    }

    [Fact]
    public void NegativeGainCannotInvertTheSignal()
    {
        var (low, _) = EffectSceneResolver.ResolveRumble(new RumbleSettings { Gain = -2f }, 1.0, 0);
        Assert.Equal(0.0, low, precision: 4);
    }
}

/// <summary>
/// The rumble-linked adaptive triggers. None of this is observable by
/// looking at a pad — a trigger that resists at the wrong moment feels
/// like a trigger that does not work — so the decision is pinned here
/// rather than left to be judged by hand.
/// </summary>
public sealed class AdaptiveTriggerSceneTests
{
    private static EffectSceneContext AtRumble(double level) =>
        new(ElapsedSeconds: 0, RumbleLevel: level);

    private static AdaptiveTriggerSettings Weapon(
        TriggerFeedbackLink link = TriggerFeedbackLink.None,
        float strength = 0.8f,
        float amount = 1.0f) =>
        new()
        {
            Mode = AdaptiveTriggerMode.Weapon,
            StartPosition = 0.25f,
            EndPosition = 0.75f,
            Strength = strength,
            FrequencyHz = 23,
            FeedbackLink = link,
            FeedbackAmount = amount,
        };

    [Fact]
    public void NoLinkPassesEverySavedValueThroughUntouched()
    {
        // The default for every profile that existed before the link did.
        var settings = Weapon();

        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(settings, AtRumble(1.0));

        Assert.Equal(ResolvedAdaptiveTrigger.From(settings), resolved);
    }

    [Fact]
    public void ResistanceKeepsTheConfiguredShapeAndMovesOnlyTheStrength()
    {
        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Resistance, strength: 1.0f), AtRumble(0.5));

        Assert.Equal(AdaptiveTriggerMode.Weapon, resolved.Mode);
        Assert.Equal(0.25f, resolved.StartPosition);
        Assert.Equal(0.75f, resolved.EndPosition);
        Assert.Equal(23, resolved.FrequencyHz);
        Assert.Equal(0.5f, resolved.Strength, precision: 3);
    }

    [Fact]
    public void ResistanceReachesTheConfiguredStrengthAtFullRumbleAndNoFurther()
    {
        // The saved Strength is the ceiling, not a starting point: a link
        // that pushed past what the user tuned would make the slider a
        // suggestion.
        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Resistance, strength: 0.6f), AtRumble(1.0));

        Assert.Equal(0.6f, resolved.Strength, precision: 3);
    }

    [Fact]
    public void ResistanceFallsToFreeTravelWhenTheGameIsQuiet()
    {
        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Resistance), AtRumble(0));

        Assert.Equal(AdaptiveTriggerMode.Weapon, resolved.Mode);
        Assert.Equal(0f, resolved.Strength);
    }

    [Fact]
    public void MotorNoiseDoesNotHoldTheTriggerEngaged()
    {
        // A motor idling at 1/255 is not a game asking for anything.
        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Resistance), AtRumble(1 / 255d));

        Assert.Equal(0f, resolved.Strength);
    }

    [Fact]
    public void VibrationOverridesTheConfiguredModeWhileTheGameRumbles()
    {
        // One effect per trigger in firmware, so this replaces rather than
        // blends with the configured Weapon shape.
        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Vibration), AtRumble(1.0));

        Assert.Equal(AdaptiveTriggerMode.Vibration, resolved.Mode);
        Assert.Equal(1f, resolved.Strength, precision: 3);
        Assert.Equal(23, resolved.FrequencyHz);
    }

    [Fact]
    public void VibrationHandsBackToTheConfiguredEffectBetweenEvents()
    {
        // Otherwise a trigger tuned to resist would go slack the moment
        // the game stopped shaking, which reads as the effect breaking.
        var settings = Weapon(TriggerFeedbackLink.Vibration);

        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(settings, AtRumble(0));

        Assert.Equal(ResolvedAdaptiveTrigger.From(settings), resolved);
    }

    [Fact]
    public void AmountScalesHowFarTheGameCanMoveTheTrigger()
    {
        var half = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Vibration, amount: 0.5f), AtRumble(1.0));

        Assert.Equal(0.5f, half.Strength, precision: 3);
    }

    [Fact]
    public void AmountOfZeroLeavesTheStaticEffectRunning()
    {
        // Turning the link down to nothing must not silence the trigger —
        // it means "the game does not drive this", not "off".
        var settings = Weapon(TriggerFeedbackLink.Vibration, amount: 0f);

        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(settings, AtRumble(1.0));

        Assert.Equal(ResolvedAdaptiveTrigger.From(settings), resolved);
    }

    [Fact]
    public void DriveIsQuantizedSoImperceptibleChangesDoNotBecomeReports()
    {
        // Two rumble levels inside the same 1/16 step must resolve to the
        // identical effect, or the queue writes to the pad every frame.
        var a = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Vibration), AtRumble(0.50));
        var b = EffectSceneResolver.ResolveAdaptiveTrigger(
            Weapon(TriggerFeedbackLink.Vibration), AtRumble(0.52));

        Assert.Equal(a, b);
    }

    [Fact]
    public void ALinkedTriggerLeftInOffModeStaysOff()
    {
        // Mode picks the feel; the link only animates it. With no feel
        // configured there is nothing to animate, and inventing one would
        // make Off mean something different on a linked trigger.
        var resolved = EffectSceneResolver.ResolveAdaptiveTrigger(
            new AdaptiveTriggerSettings
            {
                Mode = AdaptiveTriggerMode.Off,
                FeedbackLink = TriggerFeedbackLink.Resistance,
            },
            AtRumble(1.0));

        Assert.Equal(AdaptiveTriggerMode.Off, resolved.Mode);
    }
}
