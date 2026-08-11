using GameFlow.Infrastructure.Runtime;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class RuntimeCoordinatorSlotRebuildTests
{
    [Fact]
    public void DirtyLastSlotDisabled_BypassesDebounceSoCachedOutputsArePruned()
    {
        var shouldRebuild = RuntimeCoordinator.ShouldRebuildSlots(
            hasEnabledSlots: false,
            profileSwitched: false,
            slotsDirty: true,
            debounceElapsed: false);

        Assert.True(shouldRebuild);
    }

    [Fact]
    public void DirtyEnabledSlots_RemainDebounced()
    {
        var shouldRebuild = RuntimeCoordinator.ShouldRebuildSlots(
            hasEnabledSlots: true,
            profileSwitched: false,
            slotsDirty: true,
            debounceElapsed: false);

        Assert.False(shouldRebuild);
    }
}
