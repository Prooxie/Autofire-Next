using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using GameFlow.Infrastructure.Runtime.Effects;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

public sealed class ControllerEffectProducerTests
{
    private static ResolvedAdaptiveTrigger Resolved(AdaptiveTriggerSettings settings) =>
        ResolvedAdaptiveTrigger.From(settings);

    [Fact]
    public void OffSettingsBecomeAnExplicitReleaseCommand()
    {
        var command = ControllerEffectProducer.ToCommand(Resolved(new AdaptiveTriggerSettings
        {
            Mode = AdaptiveTriggerMode.Off,
        }));

        Assert.Equal(AdaptiveTriggerEffect.Off, command.Effect);
    }

    [Fact]
    public void VibrationFrequencySurvivesTheTransportConversion()
    {
        var command = ControllerEffectProducer.ToCommand(Resolved(new AdaptiveTriggerSettings
        {
            Mode = AdaptiveTriggerMode.Vibration,
            StartPosition = 0.2f,
            EndPosition = 0.8f,
            Strength = 0.7f,
            FrequencyHz = 43,
        }));

        Assert.Equal(AdaptiveTriggerEffect.Vibration, command.Effect);
        Assert.Equal(43, command.FrequencyHz);
    }

    /// <summary>
    /// The rumble link changes the STRENGTH byte and nothing else about
    /// the transport conversion — the whole point of resolving in Core is
    /// that this layer cannot tell a linked trigger from a static one.
    /// </summary>
    [Fact]
    public void ALinkedTriggerConvertsThroughTheSamePathAsAStaticOne()
    {
        var settings = new AdaptiveTriggerSettings
        {
            Mode = AdaptiveTriggerMode.Weapon,
            StartPosition = 0.25f,
            EndPosition = 0.75f,
            Strength = 1.0f,
            FeedbackLink = TriggerFeedbackLink.Resistance,
        };

        var linked = ControllerEffectProducer.ToCommand(
            EffectSceneResolver.ResolveAdaptiveTrigger(settings, new EffectSceneContext(0, RumbleLevel: 0.5)));
        var equivalentStatic = ControllerEffectProducer.ToCommand(
            Resolved(settings with { Strength = 0.5f, FeedbackLink = TriggerFeedbackLink.None }));

        Assert.Equal(equivalentStatic, linked);
    }
}
