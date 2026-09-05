using GameFlow.Infrastructure.Runtime.Slots;
using GameFlow.Infrastructure.Runtime.Templates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Slots;

/// <summary>
/// <see cref="SlotRegistry.GetSlots"/> is read once per runtime tick — up to
/// 1000 times a second — plus once per frame by the effects producer and the
/// DSU server, so it publishes a snapshot on mutation instead of deep-cloning
/// the whole registry on every read.
///
/// <para>
/// These tests pin the two halves of that contract: the snapshot really is
/// reused between reads (so the optimisation cannot silently regress), and it
/// is still refreshed by every mutation and still detached from the
/// registry's own state (so the optimisation cannot leak internals or serve
/// stale data).
/// </para>
/// </summary>
public sealed class SlotRegistrySnapshotTests : IDisposable
{
    private readonly string slotsFile =
        Path.Combine(Path.GetTempPath(), $"gameflow-slots-{Guid.NewGuid():N}.json");

    private SlotRegistry CreateRegistry() =>
        new(NullLogger<SlotRegistry>.Instance, slotsFile);

    public void Dispose()
    {
        try { File.Delete(slotsFile); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Repeated_reads_return_the_same_snapshot_instance()
    {
        var registry = CreateRegistry();
        _ = registry.CreateSlot(VirtualControllerKind.Xbox360);

        // Reference equality is the point: a fresh list per read is exactly
        // the per-tick allocation this snapshot exists to remove.
        Assert.Same(registry.GetSlots(), registry.GetSlots());
    }

    [Fact]
    public void A_mutation_publishes_a_new_snapshot()
    {
        var registry = CreateRegistry();
        var slot = registry.CreateSlot(VirtualControllerKind.Xbox360);
        Assert.NotNull(slot);

        var before = registry.GetSlots();
        registry.Rename(slot!.Id, "Renamed");
        var after = registry.GetSlots();

        Assert.NotSame(before, after);
        Assert.Equal("Renamed", Assert.Single(after).Name);

        // The previously handed-out snapshot keeps the value it was
        // published with rather than mutating under a caller holding it.
        Assert.NotEqual("Renamed", Assert.Single(before).Name);
    }

    [Fact]
    public void The_snapshot_is_detached_from_the_registry()
    {
        var registry = CreateRegistry();
        var created = registry.CreateSlot(VirtualControllerKind.DualShock4);
        Assert.NotNull(created);

        var published = Assert.Single(registry.GetSlots());
        Assert.NotSame(created, published);

        // GetSlot still hands back an independently editable copy.
        var editable = registry.GetSlot(created!.Id);
        Assert.NotNull(editable);
        Assert.NotSame(published, editable);
    }

    [Fact]
    public void Snapshot_is_ordered_by_index()
    {
        var registry = CreateRegistry();
        var first = registry.CreateSlot(VirtualControllerKind.Xbox360);
        var second = registry.CreateSlot(VirtualControllerKind.DualShock4);
        var third = registry.CreateSlot(VirtualControllerKind.DualSense);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        registry.DeleteSlot(second!.Id);

        var slots = registry.GetSlots();
        Assert.Equal(2, slots.Count);
        Assert.Equal([0, 1], slots.Select(s => s.Index));
        Assert.Equal([first!.Id, third!.Id], slots.Select(s => s.Id));
    }

    [Fact]
    public void HasEnabledSlots_tracks_the_enabled_flag()
    {
        var registry = CreateRegistry();
        Assert.False(registry.HasEnabledSlots);

        var slot = registry.CreateSlot(VirtualControllerKind.Xbox360);
        Assert.NotNull(slot);
        Assert.True(registry.HasEnabledSlots);

        registry.SetEnabled(slot!.Id, false);
        Assert.False(registry.HasEnabledSlots);

        registry.SetEnabled(slot.Id, true);
        Assert.True(registry.HasEnabledSlots);

        registry.DeleteSlot(slot.Id);
        Assert.False(registry.HasEnabledSlots);
    }

    [Fact]
    public void Slots_reloaded_from_disk_are_published_without_a_mutation()
    {
        var registry = CreateRegistry();
        var slot = registry.CreateSlot(VirtualControllerKind.SwitchPro);
        Assert.NotNull(slot);

        // A second registry over the same file stands in for the next app
        // launch: the snapshot must be populated straight out of Load(),
        // not only after the first edit.
        var reloaded = CreateRegistry();

        Assert.Equal(slot!.Id, Assert.Single(reloaded.GetSlots()).Id);
        Assert.True(reloaded.HasEnabledSlots);
    }
}
