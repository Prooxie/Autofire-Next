using GameFlow.Core.Models;

namespace GameFlow.App.Views;

/// <summary>One recorded position of one finger, with the time it was taken.</summary>
public readonly record struct TouchTrailSample(double X, double Y, double Pressure, double AtSeconds);

/// <summary>
/// Short position history per finger, so a contact can be drawn as a
/// cooling ember trail rather than a dot that teleports.
///
/// <para>
/// Keyed on <see cref="TouchContact.FingerIndex"/> — the hardware's finger
/// SLOT — not on list position. A slot stays with one finger for as long
/// as it is down, so a trail follows the finger that made it. Keying on
/// list position would re-target every trail the moment an earlier finger
/// lifted and the list shifted up, which would look like the trails
/// swapping places.
/// </para>
///
/// <para>
/// Deliberately free of Avalonia types: the ageing and sampling rules are
/// the part that can be wrong in a way nobody notices until a trail
/// visibly stutters or never fades, so they are testable without a UI.
/// </para>
/// </summary>
public sealed class TouchTrail
{
    /// <summary>
    /// How long a point stays visible. Long enough to read as a trail
    /// behind a moving finger, short enough that it does not smear into
    /// an unreadable blob when someone circles the pad.
    /// </summary>
    public const double LifetimeSeconds = 0.55;

    /// <summary>
    /// Minimum gap between recorded points. The surface can tick far
    /// faster than the trail needs, and without a floor a still finger
    /// would fill the whole buffer with identical points and the trail
    /// would vanish the moment it moved.
    /// </summary>
    private const double MinimumSampleGapSeconds = 0.012;

    /// <summary>Cap per finger, so a long drag cannot grow without bound.</summary>
    private const int MaximumSamplesPerFinger = 48;

    /// <summary>
    /// Movement below this in normalized surface units is not a new point.
    /// Stops a resting finger from stacking points in one place, which
    /// would render as a single over-bright dot.
    /// </summary>
    private const double MinimumSampleDistance = 0.004;

    private readonly Dictionary<int, List<TouchTrailSample>> byFinger = [];

    /// <summary>True while any point is still fading — the surface has to keep repainting to animate it.</summary>
    public bool HasPoints { get; private set; }

    /// <summary>Adds the current contacts and drops anything past its lifetime.</summary>
    public void Record(IReadOnlyList<TouchContact> contacts, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        foreach (var contact in contacts)
        {
            if (!byFinger.TryGetValue(contact.FingerIndex, out var samples))
            {
                samples = [];
                byFinger[contact.FingerIndex] = samples;
            }

            if (ShouldRecord(samples, contact, nowSeconds))
            {
                samples.Add(new TouchTrailSample(contact.X, contact.Y, contact.Pressure, nowSeconds));

                if (samples.Count > MaximumSamplesPerFinger)
                {
                    samples.RemoveRange(0, samples.Count - MaximumSamplesPerFinger);
                }
            }
        }

        Prune(nowSeconds);
    }

    private static bool ShouldRecord(List<TouchTrailSample> samples, TouchContact contact, double nowSeconds)
    {
        if (samples.Count == 0)
        {
            return true;
        }

        var last = samples[^1];
        if (nowSeconds - last.AtSeconds < MinimumSampleGapSeconds)
        {
            return false;
        }

        var dx = contact.X - last.X;
        var dy = contact.Y - last.Y;
        return Math.Sqrt((dx * dx) + (dy * dy)) >= MinimumSampleDistance;
    }

    /// <summary>
    /// Drops expired points. Called by <see cref="Record"/>, and separately
    /// on frames where no finger is down — a lifted finger's trail still
    /// has to finish fading, and nothing would be recording it.
    /// </summary>
    public void Prune(double nowSeconds)
    {
        var live = false;

        foreach (var (finger, samples) in byFinger)
        {
            var cutoff = nowSeconds - LifetimeSeconds;

            var keepFrom = 0;
            while (keepFrom < samples.Count && samples[keepFrom].AtSeconds < cutoff)
            {
                keepFrom++;
            }

            if (keepFrom > 0)
            {
                samples.RemoveRange(0, keepFrom);
            }

            // A clock that jumped backwards would otherwise strand points
            // in the future and keep the trail alive forever.
            if (samples.Count > 0 && samples[^1].AtSeconds > nowSeconds + LifetimeSeconds)
            {
                samples.Clear();
            }

            if (samples.Count > 0)
            {
                live = true;
            }

            _ = finger;
        }

        HasPoints = live;
    }

    /// <summary>
    /// This finger's points, oldest first. Returns the live list rather
    /// than a copy — this is read once per finger per frame, and the
    /// allocation would be pure waste.
    /// </summary>
    public IReadOnlyList<TouchTrailSample> Samples(int fingerIndex) =>
        byFinger.TryGetValue(fingerIndex, out var samples) ? samples : [];

    /// <summary>Age as 0 (just recorded) to 1 (about to disappear).</summary>
    public static double NormalizedAge(TouchTrailSample sample, double nowSeconds) =>
        Math.Clamp((nowSeconds - sample.AtSeconds) / LifetimeSeconds, 0d, 1d);

    /// <summary>Forgets everything — used when the surface's theme or device changes.</summary>
    public void Clear()
    {
        byFinger.Clear();
        HasPoints = false;
    }
}
