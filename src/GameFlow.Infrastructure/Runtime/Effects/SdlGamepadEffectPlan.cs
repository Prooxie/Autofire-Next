using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;

namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// Splits one desired controller state across SDL's two output routes.
/// Adaptive triggers require a device-specific effect report; motors and
/// the LED stay on SDL's portable APIs even when that report is present.
/// Keeping this policy pure makes the coexistence contract testable
/// without a controller or native SDL calls.
/// </summary>
internal readonly record struct SdlGamepadEffectPlan(
    AdaptiveTriggerSettings? LeftTrigger,
    AdaptiveTriggerSettings? RightTrigger,
    ushort LowFrequencyRumble,
    ushort HighFrequencyRumble,
    EffectColor? LedColor)
{
    public bool HasTriggerReport => LeftTrigger is not null || RightTrigger is not null;

    public bool HasActiveTriggerEffect =>
        IsActive(LeftTrigger) || IsActive(RightTrigger);

    /// <summary>
    /// A DS5 effect payload is valid only for SDL's PS5 driver. An Off-only
    /// payload is needed solely to release firmware state that this process
    /// previously activated; sending it to every pad can be interpreted as
    /// an unrelated controller's rumble structure.
    /// </summary>
    public bool ShouldSendTriggerReport(
        bool supportsAdaptiveTriggers,
        bool previouslyActivated) =>
        supportsAdaptiveTriggers &&
        HasTriggerReport &&
        (HasActiveTriggerEffect || previouslyActivated);

    /// <summary>
    /// Builds the final PS5 device write after SDL's portable rumble and LED
    /// APIs have synchronized their internal state. Keeping this composition
    /// on the plan is a test seam: a trigger-only regression cannot silently
    /// drop the unchanged motor/LED channels again.
    /// </summary>
    public bool TryWriteDualSenseReport(
        Span<byte> destination,
        DualSenseRumbleMode rumbleMode) =>
        DualSenseEffectEncoder.TryWrite(
            destination,
            LeftTrigger,
            RightTrigger,
            LedColor is { } led ? new LightColor(led.R, led.G, led.B) : null,
            LowFrequencyRumble,
            HighFrequencyRumble,
            rumbleMode);

    public static SdlGamepadEffectPlan Create(in ControllerEffectState state) => new(
        ToTriggerSettings(state.LeftTrigger),
        ToTriggerSettings(state.RightTrigger),
        ToRumbleMagnitude(state.LowFrequencyRumble),
        ToRumbleMagnitude(state.HighFrequencyRumble),
        state.LedColor);

    private static AdaptiveTriggerSettings? ToTriggerSettings(AdaptiveTriggerCommand? command)
    {
        if (command is not { } value)
        {
            return null;
        }

        return new AdaptiveTriggerSettings
        {
            Mode = value.Effect switch
            {
                AdaptiveTriggerEffect.Off => AdaptiveTriggerMode.Off,
                AdaptiveTriggerEffect.Constant => AdaptiveTriggerMode.Feedback,
                AdaptiveTriggerEffect.Section => AdaptiveTriggerMode.Weapon,
                AdaptiveTriggerEffect.Vibration => AdaptiveTriggerMode.Vibration,
                AdaptiveTriggerEffect.SlopeFeedback => AdaptiveTriggerMode.SlopeFeedback,
                AdaptiveTriggerEffect.MultiplePositionFeedback
                    => AdaptiveTriggerMode.MultiplePositionFeedback,
                AdaptiveTriggerEffect.MultiplePositionVibration
                    => AdaptiveTriggerMode.MultiplePositionVibration,
                _ => AdaptiveTriggerMode.Off,
            },
            StartPosition = value.StartPosition / 255f,
            EndPosition = value.EndPosition / 255f,
            Strength = value.Strength / 255f,
            FrequencyHz = value.FrequencyHz,
        };
    }

    private static ushort ToRumbleMagnitude(double value) =>
        (ushort)Math.Clamp(Math.Round(value * ushort.MaxValue), 0, ushort.MaxValue);

    private static bool IsActive(AdaptiveTriggerSettings? settings) =>
        settings is { Mode: not AdaptiveTriggerMode.Off };
}
