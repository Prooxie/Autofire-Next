using GameFlow.Infrastructure.Runtime.Effects;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

public sealed class SdlGamepadEffectPlanTests
{
    [Fact]
    public void AdaptiveTriggersDoNotReplacePortableRumbleOrLighting()
    {
        var state = new ControllerEffectState
        {
            LowFrequencyRumble = 0.75,
            HighFrequencyRumble = 0.25,
            LedColor = new EffectColor(1, 2, 3),
            LeftTrigger = new AdaptiveTriggerCommand(
                AdaptiveTriggerEffect.Vibration, 20, 200, 180, FrequencyHz: 37),
        };

        var plan = SdlGamepadEffectPlan.Create(state);

        Assert.True(plan.HasTriggerReport);
        Assert.InRange(plan.LowFrequencyRumble, 49150, 49152);
        Assert.InRange(plan.HighFrequencyRumble, 16383, 16385);
        Assert.Equal(new EffectColor(1, 2, 3), plan.LedColor);
        Assert.Equal(37, plan.LeftTrigger?.FrequencyHz);
    }

    [Fact]
    public void SilentStateCarriesExplicitTriggerReleasesAndZeroRumble()
    {
        var plan = SdlGamepadEffectPlan.Create(ControllerEffectState.Silent);

        Assert.True(plan.HasTriggerReport);
        Assert.False(plan.HasActiveTriggerEffect);
        Assert.Equal(GameFlow.Core.Models.AdaptiveTriggerMode.Off, plan.LeftTrigger?.Mode);
        Assert.Equal(GameFlow.Core.Models.AdaptiveTriggerMode.Off, plan.RightTrigger?.Mode);
        Assert.Equal(0, plan.LowFrequencyRumble);
        Assert.Equal(0, plan.HighFrequencyRumble);
    }

    [Theory]
    [InlineData(false, false, false)] // never send DS5 bytes to another controller type
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]  // an Off-only default has nothing to release
    [InlineData(true, true, true)]    // a prior active effect needs one release
    public void OffOnlyReportsAreCapabilityAndTransitionGated(
        bool supportsAdaptiveTriggers,
        bool previouslyActivated,
        bool expected)
    {
        var plan = SdlGamepadEffectPlan.Create(ControllerEffectState.Silent);

        Assert.Equal(
            expected,
            plan.ShouldSendTriggerReport(supportsAdaptiveTriggers, previouslyActivated));
    }

    [Fact]
    public void ActiveReportsStillRequireAPlayStation5Target()
    {
        var plan = SdlGamepadEffectPlan.Create(new ControllerEffectState
        {
            LeftTrigger = new AdaptiveTriggerCommand(
                AdaptiveTriggerEffect.Constant, 20, 180, 200),
        });

        Assert.True(plan.HasActiveTriggerEffect);
        Assert.False(plan.ShouldSendTriggerReport(supportsAdaptiveTriggers: false, previouslyActivated: false));
        Assert.True(plan.ShouldSendTriggerReport(supportsAdaptiveTriggers: true, previouslyActivated: false));
    }

    [Fact]
    public void AbsentTriggerCommandsDoNotCreateARawReport()
    {
        var plan = SdlGamepadEffectPlan.Create(default);

        Assert.False(plan.HasTriggerReport);
    }

    [Fact]
    public void TriggerChangeKeepsUnchangedRumbleAndLedInTheFinalDeviceReport()
    {
        static ControllerEffectState State(AdaptiveTriggerEffect effect) => new()
        {
            LowFrequencyRumble = 0.75,
            HighFrequencyRumble = 0.25,
            LedColor = new EffectColor(0x10, 0x20, 0x30),
            LeftTrigger = new AdaptiveTriggerCommand(effect, 30, 200, 180, 37),
        };

        var before = new byte[DualSenseEffectEncoder.EffectStateLength];
        var after = new byte[DualSenseEffectEncoder.EffectStateLength];
        Assert.True(SdlGamepadEffectPlan.Create(State(AdaptiveTriggerEffect.Constant))
            .TryWriteDualSenseReport(before, DualSenseRumbleMode.Enhanced));
        Assert.True(SdlGamepadEffectPlan.Create(State(AdaptiveTriggerEffect.Vibration))
            .TryWriteDualSenseReport(after, DualSenseRumbleMode.Enhanced));

        Assert.NotEqual(before[21], after[21]); // the requested trigger mode changed
        Assert.Equal(before[0] & 0x02, after[0] & 0x02); // audio haptics remain disabled
        Assert.Equal(before[38] & 0x04, after[38] & 0x04); // enhanced rumble remains enabled
        Assert.Equal(0x02, after[0] & 0x02);
        Assert.Equal(0x04, after[38] & 0x04);
        Assert.Equal(before[2], after[2]);
        Assert.Equal(before[3], after[3]);
        Assert.NotEqual(0, after[2] | after[3]);
        Assert.Equal(before[44], after[44]);
        Assert.Equal(before[45], after[45]);
        Assert.Equal(before[46], after[46]);
        Assert.Equal(0x04, after[1] & 0x04);
        Assert.Equal(0x10, after[44]);
        Assert.Equal(0x20, after[45]);
        Assert.Equal(0x30, after[46]);
    }
}
