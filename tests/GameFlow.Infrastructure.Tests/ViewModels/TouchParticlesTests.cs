using GameFlow.App.Views;
using GameFlow.Core.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.ViewModels;

/// <summary>
/// Emission rate, population cap and ageing for the fingertip sparks.
///
/// <para>
/// The cap and the rate are the point. An effect that quietly emits ten
/// times too many particles looks identical in a screenshot and costs
/// frames in motion — which is exactly the complaint this effect was
/// added in response to, so it must not become the cause of it.
/// </para>
/// </summary>
public sealed class TouchParticlesTests
{
    private const int Seed = 20260811;

    private static TouchContact Finger(int slot = 0, float x = 0.5f, float y = 0.5f, float pressure = 1f) =>
        new(slot, x, y, pressure);

    [Fact]
    public void ATouchEmitsSparks()
    {
        var particles = new TouchParticles(Seed);

        particles.Emit([Finger()], 0);

        Assert.NotEmpty(particles.Particles);
        Assert.True(particles.HasParticles);
    }

    [Fact]
    public void EmissionIsGovernedByTheClockRatherThanTheTickRate()
    {
        // The dashboard runs at 60 Hz and may be configured faster. An
        // emitter tied to the tick would throw five times the sparks at
        // five times the tick rate and cost five times as much to draw.
        //
        // Not asserting equality: a coarse tick can only emit on its own
        // samples, so it rounds the interval up and emits slightly less
        // often. What matters is that the count tracks elapsed TIME, so a
        // 5x faster tick stays near the same population rather than
        // scaling with it.
        var fast = new TouchParticles(Seed);
        var slow = new TouchParticles(Seed);

        for (var i = 0; i < 100; i++)
        {
            fast.Emit([Finger()], i * 0.002);   // 500 Hz
        }

        for (var i = 0; i < 20; i++)
        {
            slow.Emit([Finger()], i * 0.01);    // 100 Hz, same 0.2 s
        }

        Assert.True(
            fast.Particles.Count < slow.Particles.Count * 2,
            $"fast tick emitted {fast.Particles.Count} against {slow.Particles.Count} for the same elapsed time");
    }

    [Fact]
    public void ThePopulationIsCapped()
    {
        var particles = new TouchParticles(Seed);

        // Five fingers held down for a long time.
        for (var i = 0; i < 2000; i++)
        {
            particles.Emit(
                [Finger(0), Finger(1), Finger(2), Finger(3), Finger(4)],
                i * 0.005);
        }

        Assert.True(particles.Particles.Count <= 120, $"was {particles.Particles.Count}");
    }

    [Fact]
    public void SparksBurnOutOnTheirOwn()
    {
        var particles = new TouchParticles(Seed);
        particles.Emit([Finger()], 0);

        particles.Prune(10);

        Assert.Empty(particles.Particles);
        Assert.False(particles.HasParticles);
    }

    [Fact]
    public void ALiftedFingersSparksKeepBurning()
    {
        // The contact list empties the instant the finger comes up; the
        // sparks it already threw still have to finish.
        var particles = new TouchParticles(Seed);
        particles.Emit([Finger()], 0);

        particles.Emit([], 0.05);

        Assert.True(particles.HasParticles);
    }

    [Fact]
    public void SparksDriftAwayFromWhereTheyWereThrown()
    {
        var particles = new TouchParticles(Seed);
        particles.Emit([Finger(x: 0.5f, y: 0.5f)], 0);

        var spark = particles.Particles[0];
        var moved = Math.Abs(spark.XAt(0.2) - spark.OriginX) + Math.Abs(spark.YAt(0.2) - spark.OriginY);

        Assert.True(moved > 0.001, "a spark that never moves is a dot");
    }

    [Fact]
    public void SparksLeaveInDifferentDirections()
    {
        // Radial emission is what separates this from a smear.
        var particles = new TouchParticles(Seed);

        for (var i = 0; i < 10; i++)
        {
            particles.Emit([Finger()], i * 0.03);
        }

        var directions = particles.Particles
            .Select(p => Math.Round(Math.Atan2(p.VelocityY, p.VelocityX), 2))
            .Distinct()
            .Count();

        Assert.True(directions > 3, $"only {directions} distinct directions");
    }

    [Fact]
    public void PressingHarderThrowsSparksFurther()
    {
        var light = new TouchParticles(Seed);
        var firm = new TouchParticles(Seed);

        light.Emit([Finger(pressure: 0.05f)], 0);
        firm.Emit([Finger(pressure: 1f)], 0);

        double Speed(TouchParticles p) =>
            p.Particles.Average(x => Math.Sqrt((x.VelocityX * x.VelocityX) + (x.VelocityY * x.VelocityY)));

        Assert.True(Speed(firm) > Speed(light));
    }

    [Fact]
    public void AgeRunsZeroToOneAndIsClamped()
    {
        var spark = new TouchParticle(0, 0.5, 0.5, 0.1, 0.1, BornAt: 5, Lifetime: 0.4, Scale: 1);

        Assert.Equal(0, spark.Age(5), precision: 3);
        Assert.Equal(1, spark.Age(5.4), precision: 3);
        Assert.Equal(0.5, spark.Age(5.2), precision: 3);
        Assert.Equal(1, spark.Age(9999), precision: 3);
        Assert.Equal(0, spark.Age(0), precision: 3);
    }

    [Fact]
    public void ParticlesAreKeyedToTheFingerThatThrewThem()
    {
        // Colour follows the finger slot, so this has to survive emission.
        var particles = new TouchParticles(Seed);

        particles.Emit([Finger(slot: 0), Finger(slot: 3)], 0);

        Assert.Contains(particles.Particles, p => p.FingerIndex == 0);
        Assert.Contains(particles.Particles, p => p.FingerIndex == 3);
    }

    [Fact]
    public void AClockThatJumpsBackwardsDoesNotStrandParticlesForever()
    {
        var particles = new TouchParticles(Seed);
        particles.Emit([Finger()], 100);

        particles.Prune(0);

        Assert.Empty(particles.Particles);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var particles = new TouchParticles(Seed);
        particles.Emit([Finger()], 0);

        particles.Clear();

        Assert.False(particles.HasParticles);
    }
}
