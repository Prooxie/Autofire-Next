namespace GameFlow.Infrastructure.Runtime;

/// <summary>
/// Optional capability for an output sink that receives rumble feedback
/// from the consuming game (the HIDMaestro sink raises this from its
/// output callback). Values are normalized 0–1 (low-frequency,
/// high-frequency), including an explicit (0, 0) stop.
/// </summary>
public interface IRumbleFeedbackSource
{
    event Action<double, double>? RumbleReceived;
}
