using GameFlow.Infrastructure.Runtime.HidMaestro;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class HidMaestroRumbleLifecycleTests
{
    [Fact]
    public void StopAndTeardown_PublishesZeroBeforeControllerIsReleased()
    {
        var calls = new List<string>();

        HidMaestroRumbleLifecycle.StopAndTeardown(
            (low, high) => calls.Add($"rumble:{low}:{high}"),
            () => calls.Add("teardown"));

        Assert.Equal(["rumble:0:0", "teardown"], calls);
    }

    [Fact]
    public void StopAndTeardown_StillReleasesControllerWhenFeedbackHandlerFails()
    {
        var tornDown = false;

        Assert.Throws<InvalidOperationException>(() =>
            HidMaestroRumbleLifecycle.StopAndTeardown(
                (_, _) => throw new InvalidOperationException("subscriber failed"),
                () => tornDown = true));

        Assert.True(tornDown);
    }
}
