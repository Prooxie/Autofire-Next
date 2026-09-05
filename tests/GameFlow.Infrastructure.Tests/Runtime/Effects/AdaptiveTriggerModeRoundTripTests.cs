using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using GameFlow.Infrastructure.Runtime.Effects;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

/// <summary>
/// Covers a chosen adaptive-trigger mode surviving the trip to the pad.
/// </summary>
/// <remarks>
/// The producer narrowed seven modes onto four effect kinds and the plan
/// widened them back again, so the round trip was lossy in the middle of
/// the pipeline. Multiple-position feedback and Slope feedback both
/// arrived as plain Constant resistance, and Multiple-position vibration
/// as plain Vibration — with the UI still showing the mode that had been
/// picked and nothing logged to say otherwise. The firmware distinguishes
/// all of them and the DualSense encoder already writes distinct effect
/// ids, so the detail was thrown away for nothing.
/// </remarks>
public sealed class AdaptiveTriggerModeRoundTripTests
{
    private static AdaptiveTriggerSettings? RoundTrip(AdaptiveTriggerMode mode)
    {
        var command = ControllerEffectProducer.ToCommand(
            ResolvedAdaptiveTrigger.From(new AdaptiveTriggerSettings { Mode = mode }));

        var plan = SdlGamepadEffectPlan.Create(
            ControllerEffectState.Silent with { LeftTrigger = command });

        return plan.LeftTrigger;
    }

    [Theory]
    [InlineData(AdaptiveTriggerMode.Off)]
    [InlineData(AdaptiveTriggerMode.Feedback)]
    [InlineData(AdaptiveTriggerMode.Weapon)]
    [InlineData(AdaptiveTriggerMode.Vibration)]
    [InlineData(AdaptiveTriggerMode.SlopeFeedback)]
    [InlineData(AdaptiveTriggerMode.MultiplePositionFeedback)]
    [InlineData(AdaptiveTriggerMode.MultiplePositionVibration)]
    public void EveryModeSurvivesTheRoundTrip(AdaptiveTriggerMode mode) =>
        Assert.Equal(mode, RoundTrip(mode)!.Mode);

    /// <summary>
    /// The three that used to collapse. Kept separate from the sweep above
    /// so a regression names the modes rather than "one of seven".
    /// </summary>
    [Theory]
    [InlineData(AdaptiveTriggerMode.SlopeFeedback, AdaptiveTriggerMode.Feedback)]
    [InlineData(AdaptiveTriggerMode.MultiplePositionFeedback, AdaptiveTriggerMode.Feedback)]
    [InlineData(AdaptiveTriggerMode.MultiplePositionVibration, AdaptiveTriggerMode.Vibration)]
    public void ADistinctModeIsNotReducedToItsPlainVariant(
        AdaptiveTriggerMode chosen, AdaptiveTriggerMode collapsedTo) =>
        Assert.NotEqual(collapsedTo, RoundTrip(chosen)!.Mode);
}
