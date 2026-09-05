namespace GameFlow.Infrastructure.Runtime.Slots;

/// <summary>
/// Optional capability for an <see cref="IOutputSink"/> whose backend needs
/// a single settling step after ALL of a rebuild's sinks have been created,
/// rather than anything per sink.
///
/// <para>
/// <see cref="SlotRuntime"/> invokes it exactly once per rebuild, on the
/// first sink that implements it, after every slot's sink exists and the
/// stale ones have been pruned. Implementations must therefore be
/// process-wide rather than per instance, and must be safe to call when
/// nothing actually changed.
/// </para>
///
/// <para>
/// It exists for the HIDMaestro sink: creating a second virtual controller
/// re-triggers Windows PnP driver-bind activity that overwrites the first
/// one's friendly name, so the names have to be re-applied once the whole
/// set is up. That is a property of the batch, not of any one sink, which is
/// why it cannot live in <c>Configure</c>.
/// </para>
/// </summary>
public interface IPostRebuildFinalizer
{
    /// <summary>Runs the backend's once-per-rebuild settling step. Must not throw.</summary>
    void FinalizeAfterRebuild();
}
