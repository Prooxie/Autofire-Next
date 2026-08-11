using System.Collections.Concurrent;

namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// Latest game-requested motor state per slot. HIDMaestro callbacks write
/// here from their output-reader threads; the effect producer reads from
/// its own timer, so neither side blocks the other.
/// </summary>
public sealed class RumbleFeedbackStore
{
    private readonly ConcurrentDictionary<string, RumbleFeedback> feedback =
        new(StringComparer.Ordinal);

    public void Set(string slotId, double lowFrequency, double highFrequency)
    {
        if (string.IsNullOrEmpty(slotId))
        {
            return;
        }

        feedback[slotId] = new RumbleFeedback(
            Math.Clamp(lowFrequency, 0d, 1d),
            Math.Clamp(highFrequency, 0d, 1d));
    }

    public RumbleFeedback Get(string slotId) =>
        !string.IsNullOrEmpty(slotId) && feedback.TryGetValue(slotId, out var value)
            ? value
            : default;

    public void Clear(string slotId)
    {
        if (!string.IsNullOrEmpty(slotId))
        {
            _ = feedback.TryRemove(slotId, out _);
        }
    }
}

public readonly record struct RumbleFeedback(double LowFrequency, double HighFrequency);
