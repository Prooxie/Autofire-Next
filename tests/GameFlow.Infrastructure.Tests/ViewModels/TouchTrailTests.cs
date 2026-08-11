using GameFlow.App.Views;
using GameFlow.Core.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.ViewModels;

/// <summary>
/// Ageing and sampling for the touch trail. None of this is checkable by
/// looking at the app — a trail that never expires and a trail that
/// expires instantly both just look like "the effect is wrong".
/// </summary>
public sealed class TouchTrailTests
{
    private static TouchContact Finger(int slot, float x, float y, float pressure = 1f) =>
        new(slot, x, y, pressure);

    [Fact]
    public void AFingerLaysDownAPointImmediately()
    {
        var trail = new TouchTrail();

        trail.Record([Finger(0, 0.5f, 0.5f)], 0);

        Assert.Single(trail.Samples(0));
        Assert.True(trail.HasPoints);
    }

    [Fact]
    public void ADraggedFingerAccumulatesATrail()
    {
        var trail = new TouchTrail();

        for (var i = 0; i < 5; i++)
        {
            trail.Record([Finger(0, 0.1f + (i * 0.1f), 0.5f)], i * 0.05);
        }

        Assert.Equal(5, trail.Samples(0).Count);
    }

    [Fact]
    public void AStillFingerDoesNotStackPointsInOnePlace()
    {
        // Otherwise a resting finger fills the buffer with identical
        // points, which renders as one over-bright dot and leaves no
        // history to draw once it finally moves.
        var trail = new TouchTrail();

        for (var i = 0; i < 20; i++)
        {
            trail.Record([Finger(0, 0.5f, 0.5f)], i * 0.05);
        }

        Assert.Single(trail.Samples(0));
    }

    [Fact]
    public void SamplingIsRateLimitedIndependentlyOfTheTickRate()
    {
        // The surface can tick far faster than the trail needs.
        var trail = new TouchTrail();

        for (var i = 0; i < 10; i++)
        {
            trail.Record([Finger(0, 0.1f + (i * 0.05f), 0.5f)], i * 0.001);
        }

        Assert.True(trail.Samples(0).Count < 10);
    }

    [Fact]
    public void PointsExpireAfterTheirLifetime()
    {
        var trail = new TouchTrail();
        trail.Record([Finger(0, 0.2f, 0.5f)], 0);

        trail.Prune(TouchTrail.LifetimeSeconds + 0.01);

        Assert.Empty(trail.Samples(0));
        Assert.False(trail.HasPoints);
    }

    [Fact]
    public void ALiftedFingersTrailKeepsFadingRatherThanVanishing()
    {
        // The contact list empties the instant the finger comes up. If
        // that dropped the history the trail would disappear mid-fade.
        var trail = new TouchTrail();
        trail.Record([Finger(0, 0.2f, 0.5f)], 0);

        trail.Record([], 0.1);

        Assert.NotEmpty(trail.Samples(0));
        Assert.True(trail.HasPoints);
    }

    [Fact]
    public void OnlyTheExpiredEndOfATrailIsDropped()
    {
        var trail = new TouchTrail();
        trail.Record([Finger(0, 0.1f, 0.5f)], 0);
        trail.Record([Finger(0, 0.9f, 0.5f)], TouchTrail.LifetimeSeconds - 0.05);

        trail.Prune(TouchTrail.LifetimeSeconds + 0.01);

        var remaining = Assert.Single(trail.Samples(0));
        Assert.Equal(0.9, remaining.X, precision: 3);
    }

    [Fact]
    public void TrailsAreKeptPerFingerSlot()
    {
        var trail = new TouchTrail();

        trail.Record([Finger(0, 0.2f, 0.2f), Finger(1, 0.8f, 0.8f)], 0);

        Assert.Single(trail.Samples(0));
        Assert.Single(trail.Samples(1));
    }

    [Fact]
    public void LiftingAnEarlierFingerDoesNotDisturbTheOthersTrail()
    {
        // Slot-keyed rather than position-keyed: with list positions, the
        // second finger would inherit the first one's trail here.
        var trail = new TouchTrail();
        trail.Record([Finger(0, 0.1f, 0.1f), Finger(1, 0.9f, 0.9f)], 0);

        trail.Record([Finger(1, 0.85f, 0.85f)], 0.05);

        Assert.Equal(2, trail.Samples(1).Count);
        Assert.Equal(0.9, trail.Samples(1)[0].X, precision: 3);
    }

    [Fact]
    public void ATrailCannotGrowWithoutBound()
    {
        var trail = new TouchTrail();

        // A long circling drag, all inside one lifetime.
        for (var i = 0; i < 400; i++)
        {
            var angle = i * 0.1;
            trail.Record(
                [Finger(0, (float)(0.5 + (0.4 * Math.Cos(angle))), (float)(0.5 + (0.4 * Math.Sin(angle))))],
                i * 0.0005);
        }

        Assert.True(trail.Samples(0).Count <= 48);
    }

    [Fact]
    public void AgeRunsFromZeroWhenFreshToOneAtExpiry()
    {
        var sample = new TouchTrailSample(0.5, 0.5, 1, AtSeconds: 10);

        Assert.Equal(0, TouchTrail.NormalizedAge(sample, 10), precision: 3);
        Assert.Equal(1, TouchTrail.NormalizedAge(sample, 10 + TouchTrail.LifetimeSeconds), precision: 3);
        Assert.Equal(0.5, TouchTrail.NormalizedAge(sample, 10 + (TouchTrail.LifetimeSeconds / 2)), precision: 3);
    }

    [Fact]
    public void AgeIsClampedSoAStalePointCannotRenderInvertedOrOverbright()
    {
        var sample = new TouchTrailSample(0.5, 0.5, 1, AtSeconds: 10);

        Assert.Equal(1, TouchTrail.NormalizedAge(sample, 999), precision: 3);
        Assert.Equal(0, TouchTrail.NormalizedAge(sample, 0), precision: 3);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var trail = new TouchTrail();
        trail.Record([Finger(0, 0.5f, 0.5f)], 0);

        trail.Clear();

        Assert.Empty(trail.Samples(0));
        Assert.False(trail.HasPoints);
    }
}
