using GameFlow.Infrastructure.Runtime.Effects;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

public sealed class EffectDispatchQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);

    private static EffectDispatchQueue NewQueue() => new(Interval);

    private static ControllerEffectState Rumble(double low, double high = 0d) =>
        new() { LowFrequencyRumble = low, HighFrequencyRumble = high };

    [Fact]
    public void APublishedStateIsDue()
    {
        var queue = NewQueue();
        queue.Publish("pad", Rumble(0.5));

        var due = queue.TakeDueWrites(T0);

        Assert.Single(due);
        Assert.Equal("pad", due[0].DeviceId);
        Assert.Equal(0.5, due[0].State.LowFrequencyRumble);
    }

    [Fact]
    public void OnlyTheNewestStatePerDeviceSurvives()
    {
        // The whole point: the transport is slower than the producer, so
        // superseded values must be dropped rather than queued. Queuing
        // them makes the pad lag further behind the longer a rumble runs.
        var queue = NewQueue();
        queue.Publish("pad", Rumble(0.1));
        queue.Publish("pad", Rumble(0.2));
        queue.Publish("pad", Rumble(0.9));

        var due = queue.TakeDueWrites(T0);

        Assert.Single(due);
        Assert.Equal(0.9, due[0].State.LowFrequencyRumble);
    }

    [Fact]
    public void AnUnchangedStateIsNotWrittenTwice()
    {
        // An idle pad is the common case and must cost nothing.
        var queue = NewQueue();
        queue.Publish("pad", Rumble(0.5));
        Assert.Single(queue.TakeDueWrites(T0));

        queue.Publish("pad", Rumble(0.5));
        Assert.Empty(queue.TakeDueWrites(T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ASecondWriteInsideTheIntervalIsHeldBack()
    {
        var queue = NewQueue();
        queue.Publish("pad", Rumble(0.2));
        Assert.Single(queue.TakeDueWrites(T0));

        queue.Publish("pad", Rumble(0.4));
        Assert.Empty(queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public void TheHeldBackWriteGoesOutOnceTheIntervalPasses()
    {
        var queue = NewQueue();
        queue.Publish("pad", Rumble(0.2));
        _ = queue.TakeDueWrites(T0);

        queue.Publish("pad", Rumble(0.4));
        Assert.Empty(queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(5)));

        var due = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(20));

        Assert.Single(due);
        // The value that goes out is the newest, not the one that was held.
        Assert.Equal(0.4, due[0].State.LowFrequencyRumble);
    }

    [Fact]
    public void GoingSilentIsNeverRateLimited()
    {
        // A motor that should stop must stop NOW. Making a stop wait behind
        // the rate limiter is the one failure a user cannot ignore — the
        // pad keeps buzzing after the game went quiet.
        var queue = NewQueue();
        queue.Publish("pad", Rumble(1.0));
        _ = queue.TakeDueWrites(T0);

        queue.Publish("pad", ControllerEffectState.Silent);
        var due = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(1));

        Assert.Single(due);
        Assert.True(due[0].State.IsSilent);
    }

    [Fact]
    public void DevicesAreRateLimitedIndependently()
    {
        var queue = NewQueue();
        queue.Publish("a", Rumble(0.5));
        Assert.Single(queue.TakeDueWrites(T0));

        // "b" has never been written, so it is not owed any interval.
        queue.Publish("b", Rumble(0.5));
        var due = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(1));

        Assert.Single(due);
        Assert.Equal("b", due[0].DeviceId);
    }

    [Fact]
    public void AFailedWriteIsRetriedRatherThanLost()
    {
        var queue = NewQueue();
        queue.Publish("pad", Rumble(0.7));
        var due = queue.TakeDueWrites(T0);

        queue.Requeue(due[0]);

        var retried = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(20));
        Assert.Single(retried);
        Assert.Equal(0.7, retried[0].State.LowFrequencyRumble);
    }

    [Fact]
    public void RetiringADeviceQueuesAFinalSilentWrite()
    {
        // Unassigning a slot mid-rumble would otherwise leave the motor
        // running with nothing left that would ever turn it off.
        var queue = NewQueue();
        queue.Publish("pad", Rumble(1.0));
        _ = queue.TakeDueWrites(T0);

        queue.Retire("pad");
        var due = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(1));

        Assert.Single(due);
        Assert.True(due[0].State.IsSilent);
    }

    [Fact]
    public void ARetiredDeviceIsForgottenAfterItsFinalWrite()
    {
        var queue = NewQueue();
        queue.Publish("pad", Rumble(1.0));
        _ = queue.TakeDueWrites(T0);
        queue.Retire("pad");
        _ = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(1));

        queue.PurgeRetired();

        Assert.Equal(0, queue.TrackedDeviceCount);
    }

    [Fact]
    public void RetiringADeviceThatNeverPlayedAnythingWritesNothing()
    {
        var queue = NewQueue();
        queue.Publish("pad", ControllerEffectState.Silent);
        queue.Retire("pad");

        Assert.Empty(queue.TakeDueWrites(T0));
        Assert.Equal(0, queue.TrackedDeviceCount);
    }

    [Fact]
    public void AnEmptyDeviceIdIsIgnoredRatherThanTracked()
    {
        var queue = NewQueue();
        queue.Publish("", Rumble(1.0));

        Assert.Equal(0, queue.TrackedDeviceCount);
        Assert.Empty(queue.TakeDueWrites(T0));
    }

    [Fact]
    public void ForgettingADeviceDropsItWithNoFinalWrite()
    {
        var queue = NewQueue();
        queue.Publish("pad", Rumble(1.0));
        queue.Forget("pad");

        Assert.Empty(queue.TakeDueWrites(T0));
    }

    [Fact]
    public void ChangingOnlyTheLedStillCountsAsAChange()
    {
        // Equality is over the whole state, so a colour change with
        // identical rumble must not be swallowed by the no-op check.
        var queue = NewQueue();
        var blue = new ControllerEffectState { LedColor = new EffectColor(0, 0, 255) };
        var red = new ControllerEffectState { LedColor = new EffectColor(255, 0, 0) };

        queue.Publish("pad", blue);
        Assert.Single(queue.TakeDueWrites(T0));

        queue.Publish("pad", red);
        var due = queue.TakeDueWrites(T0 + TimeSpan.FromMilliseconds(20));

        Assert.Single(due);
        Assert.Equal(new EffectColor(255, 0, 0), due[0].State.LedColor);
    }
}

public sealed class EffectColorTests
{
    [Theory]
    [InlineData("#FF8800", 255, 136, 0)]
    [InlineData("FF8800", 255, 136, 0)]
    [InlineData("#ff8800", 255, 136, 0)]
    [InlineData("#FFFF8800", 255, 136, 0)]  // ARGB — alpha ignored
    public void ValidHexParses(string hex, byte r, byte g, byte b)
    {
        Assert.True(EffectColor.TryParse(hex, out var color));
        Assert.Equal(new EffectColor(r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]
    [InlineData("nonsense")]
    [InlineData("#GGGGGG")]
    public void MalformedHexIsRejectedRatherThanThrowing(string? hex)
    {
        // This comes from a hand-editable settings file, so a bad value is
        // expected input, not an exceptional case.
        Assert.False(EffectColor.TryParse(hex, out _));
    }
}

public sealed class ControllerEffectStateTests
{
    [Fact]
    public void DefaultStateIsSilent()
    {
        Assert.True(ControllerEffectState.Silent.IsSilent);
        Assert.True(default(ControllerEffectState).IsSilent);
        Assert.Equal(AdaptiveTriggerEffect.Off, ControllerEffectState.Silent.LeftTrigger?.Effect);
        Assert.Equal(AdaptiveTriggerEffect.Off, ControllerEffectState.Silent.RightTrigger?.Effect);
    }

    [Fact]
    public void AnyActiveChannelMakesItNotSilent()
    {
        Assert.False(new ControllerEffectState { LowFrequencyRumble = 0.1 }.IsSilent);
        Assert.False(new ControllerEffectState { HighFrequencyRumble = 0.1 }.IsSilent);
        Assert.False(new ControllerEffectState { LedColor = new EffectColor(1, 1, 1) }.IsSilent);
        Assert.False(new ControllerEffectState
        {
            LeftTrigger = new AdaptiveTriggerCommand(AdaptiveTriggerEffect.Constant, 0, 0, 200)
        }.IsSilent);
        Assert.True(new ControllerEffectState
        {
            LeftTrigger = AdaptiveTriggerCommand.Release,
            RightTrigger = AdaptiveTriggerCommand.Release,
        }.IsSilent);
    }
}
