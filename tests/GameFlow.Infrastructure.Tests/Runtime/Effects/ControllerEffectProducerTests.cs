using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Effects;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

public sealed class ControllerEffectProducerTests
{
    [Fact]
    public void OffSettingsBecomeAnExplicitReleaseCommand()
    {
        var command = ControllerEffectProducer.ToCommand(new AdaptiveTriggerSettings
        {
            Mode = AdaptiveTriggerMode.Off,
        });

        Assert.Equal(AdaptiveTriggerEffect.Off, command.Effect);
    }

    [Fact]
    public void VibrationFrequencySurvivesTheTransportConversion()
    {
        var command = ControllerEffectProducer.ToCommand(new AdaptiveTriggerSettings
        {
            Mode = AdaptiveTriggerMode.Vibration,
            StartPosition = 0.2f,
            EndPosition = 0.8f,
            Strength = 0.7f,
            FrequencyHz = 43,
        });

        Assert.Equal(AdaptiveTriggerEffect.Vibration, command.Effect);
        Assert.Equal(43, command.FrequencyHz);
    }
}
