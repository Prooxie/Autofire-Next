using GameFlow.Infrastructure.Runtime.Effects;

namespace GameFlow.Infrastructure.Runtime.Web;

/// <summary>
/// Routes the device-neutral effect stream to browser controllers before
/// the SDL collector handles hardware pads. Phones do not have SDL handles,
/// so without this bridge their otherwise valid <c>web-pad-*</c> writes are
/// silently skipped.
/// </summary>
internal static class WebControllerEffectBridge
{
    /// <summary>
    /// Browser vibration has no portable amplitude control, so the page turns
    /// motor strength into a duty cycle over this short window. A zero command
    /// stops the repeating pulse immediately.
    /// </summary>
    internal const int VibrationDurationMs = 250;

    /// <summary>
    /// Queues the motor portion of an effect state when the target is a web
    /// controller. Returns true when the device id belonged to this provider,
    /// even if that phone disconnected before the write arrived.
    /// </summary>
    internal static bool TryRoute(
        WebControllerHub hub,
        string deviceId,
        in ControllerEffectState state)
    {
        ArgumentNullException.ThrowIfNull(hub);

        var padIndex = WebControllerDeviceScanner.TryParsePadIndex(deviceId);
        if (padIndex < 0)
        {
            return false;
        }

        hub.QueueRumble(padIndex, new WebRumbleCommand(
            LowFrequency: ClampMotor(state.LowFrequencyRumble),
            HighFrequency: ClampMotor(state.HighFrequencyRumble),
            DurationMs: state.LowFrequencyRumble <= 0d && state.HighFrequencyRumble <= 0d
                ? 0
                : VibrationDurationMs));
        return true;
    }

    private static float ClampMotor(double value) => double.IsFinite(value)
        ? (float)Math.Clamp(value, 0d, 1d)
        : 0f;
}
