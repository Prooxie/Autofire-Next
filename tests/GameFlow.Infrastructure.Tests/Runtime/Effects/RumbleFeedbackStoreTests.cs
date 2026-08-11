using GameFlow.Infrastructure.Runtime.Effects;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

public sealed class RumbleFeedbackStoreTests
{
    [Fact]
    public void LatestFeedbackWinsAndValuesAreClamped()
    {
        var store = new RumbleFeedbackStore();

        store.Set("slot", 0.1, 0.2);
        store.Set("slot", 4, -2);

        Assert.Equal(new RumbleFeedback(1, 0), store.Get("slot"));
    }

    [Fact]
    public void ClearReturnsSlotToSilence()
    {
        var store = new RumbleFeedbackStore();
        store.Set("slot", 1, 1);

        store.Clear("slot");

        Assert.Equal(default, store.Get("slot"));
    }
}
