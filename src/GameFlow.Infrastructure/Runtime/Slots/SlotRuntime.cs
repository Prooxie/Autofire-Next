using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using GameFlow.Infrastructure.Profiles;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Slots;

/// <summary>
/// Drives the multi-slot runtime: one independent <see cref="SlotPipeline"/>
/// per enabled <see cref="ControllerSlot"/>. The coordinator pumps the
/// shared input source once per tick, then this orchestrator reads each
/// slot's assigned device and runs its pipeline → sink. Per-slot failures
/// are caught and logged so one bad slot can't stall the others.
///
/// <para>Created and ticked by <see cref="RuntimeCoordinator"/> only when
/// the input source supports <see cref="IMultiDeviceInputSource"/> and at
/// least one enabled slot exists; otherwise the coordinator keeps running
/// its original single pipeline.</para>
/// </summary>
public sealed class SlotRuntime : IAsyncDisposable
{
    private readonly SlotRegistry registry;
    private readonly IOutputSinkFactory outputSinkFactory;
    private readonly SlotSnapshotStore snapshotStore;
    private readonly IProfileRepository profileRepository;
    private readonly Input.IMouseOutputWriter mouseOutputWriter;
    private readonly DeviceSettingsStore deviceSettingsStore;
    private readonly Effects.RumbleFeedbackStore rumbleFeedbackStore;
    private readonly ILogger logger;

    private readonly List<SlotPipeline> pipelines = [];
    private readonly Dictionary<string, int> slotFailureCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Output sinks cached ACROSS rebuilds, keyed by slot id. Rebuilds
    /// happen on every slot edit (debounced to 400 ms), and recreating
    /// the sink each time meant destroying + recreating the virtual OS
    /// device each time — the visible symptom being controllers
    /// multiplying/blinking during ordinary editing. A cached sink is
    /// re-Configure()d instead; its emit-shape fingerprint keeps the
    /// live device untouched unless the emitted DEVICE actually changed
    /// (kind/profile/identity). Sinks are disposed only when their slot
    /// disappears, its provider changes, or the runtime shuts down.
    /// </summary>
    private readonly Dictionary<string, (string Provider, IOutputSink Sink)> sinkCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IRumbleFeedbackSource Source, Action<double, double> Handler)> rumbleSubscriptions =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Time base for per-slot demo previews — one continuous clock for
    /// the whole runtime so the waveform never jumps on slot edits or
    /// pipeline rebuilds.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch demoClock = System.Diagnostics.Stopwatch.StartNew();

    public SlotRuntime(SlotRegistry registry, IOutputSinkFactory outputSinkFactory,
        SlotSnapshotStore snapshotStore,
        IProfileRepository profileRepository, Input.IMouseOutputWriter mouseOutputWriter,
        DeviceSettingsStore deviceSettingsStore,
        Effects.RumbleFeedbackStore rumbleFeedbackStore,
        ILogger logger)
    {
        this.registry = registry;
        this.outputSinkFactory = outputSinkFactory;
        this.snapshotStore = snapshotStore;
        this.profileRepository = profileRepository;
        this.mouseOutputWriter = mouseOutputWriter;
        this.deviceSettingsStore = deviceSettingsStore;
        this.rumbleFeedbackStore = rumbleFeedbackStore;
        this.logger = logger;
    }

    /// <summary>True if there is at least one enabled slot to run.</summary>
    public bool HasEnabledSlots => registry.GetSlots().Any(s => s.Enabled);

    /// <summary>
    /// (Vid, Pid) signatures of every currently-active slot's virtual
    /// output device, deduplicated. RuntimeCoordinator combines this with
    /// the top-level sink's own signature and feeds the result to the
    /// input catalog every tick, so none of this app's own virtual
    /// outputs can ever be enumerated (and therefore selected) as an
    /// input device — see SlotPipeline.OutputHardwareSignature.
    /// </summary>
    public IReadOnlyList<(ushort Vid, ushort Pid, DateTimeOffset ActivatedAt)> GetActiveOutputSignatures() =>
        [.. pipelines
            .Select(p => (Sig: p.OutputHardwareSignature, At: p.OutputSignatureActivatedAt))
            .Where(x => x.Sig is not null && x.At is not null)
            .Select(x => (x.Sig!.Value.Vid, x.Sig.Value.Pid, x.At!.Value))
            .Distinct()];

    /// <summary>
    /// Tears down existing slot pipelines and builds fresh ones for the
    /// current enabled slots, using <paramref name="activeProfile"/> for
    /// the mapping pipeline and <paramref name="outputProvider"/> for each
    /// slot's sink. Each sink is configured from its slot's template.
    /// </summary>
    public async Task RebuildAsync(ProfileDocument activeProfile, string outputProvider)
    {
        var rebuildSw = System.Diagnostics.Stopwatch.StartNew();
        var enabledCount = 0;
        foreach (var s in registry.GetSlots()) { if (s.Enabled) enabledCount++; }
        logger.LogInformation(
            "Slot runtime: RebuildAsync starting (enabledSlots={EnabledSlots}, totalSlots={TotalSlots}, output={Output}).",
            enabledCount, registry.GetSlots().Count, outputProvider);

        await DisposePipelinesAsync();

        foreach (var slot in registry.GetSlots())
        {
            if (!slot.Enabled)
            {
                continue;
            }

            try
            {
                logger.LogInformation(
                    "Slot runtime: building pipeline for slot {SlotId} ({SlotName}) " +
                    "(profiles={ProfileCount}, devices={DeviceCount}).",
                    slot.Id, slot.Name, slot.ProfileIds.Count, slot.InputDeviceIds.Count);

                var profileSw = System.Diagnostics.Stopwatch.StartNew();
                var slotProfile = await ResolveSlotProfileAsync(slot);
                profileSw.Stop();
                logger.LogInformation(
                    "Slot runtime: ResolveSlotProfileAsync completed for slot {SlotId} in {ElapsedMs} ms.",
                    slot.Id, profileSw.ElapsedMilliseconds);

                // Per-slot output provider wins; an empty value means this
                // slot was saved before per-slot providers existed (or was
                // never explicitly set), so it inherits the profile-level
                // pick — the ONLY behavior available before this field
                // existed. Every slot's chosen backend is logged so it's
                // never a mystery which one actually ended up active.
                var slotOutputProvider = string.IsNullOrWhiteSpace(slot.OutputTemplate.OutputProvider)
                    ? outputProvider
                    : slot.OutputTemplate.OutputProvider;
                logger.LogInformation(
                    "Slot runtime: slot {SlotId} using output provider '{Provider}' ({Source}).",
                    slot.Id, slotOutputProvider,
                    string.IsNullOrWhiteSpace(slot.OutputTemplate.OutputProvider) ? "inherited from profile" : "set on slot");

                var pipeline = new ControllerMappingPipeline(slotProfile);
                var sink = await GetOrCreateSinkAsync(slot.Id, slotOutputProvider);
                if (sink is IConfigurableOutputSink configurable)
                {
                    configurable.Configure(slot.OutputTemplate);
                }

                pipelines.Add(new SlotPipeline(slot.Id, slot.InputDeviceIds, slot.OutputTemplate, pipeline, sink,
                    ownsOutputSink: false));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to build pipeline for slot {SlotId} ({SlotName}).", slot.Id, slot.Name);
            }
        }

        await PruneSinkCacheAsync();

        rebuildSw.Stop();
        logger.LogInformation(
            "Slot runtime built {Count} slot pipeline(s) in {ElapsedMs} ms.",
            pipelines.Count, rebuildSw.ElapsedMilliseconds);
        snapshotStore.Retain(pipelines.Select(p => p.SlotId));
    }

    /// <summary>
    /// Returns the cached sink for a slot when its provider is
    /// unchanged; otherwise disposes any stale sink and creates a fresh
    /// one for the new provider.
    /// </summary>
    private async ValueTask<IOutputSink> GetOrCreateSinkAsync(string slotId, string provider)
    {
        if (sinkCache.TryGetValue(slotId, out var cached))
        {
            if (string.Equals(cached.Provider, provider, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Sink;
            }

            logger.LogInformation(
                "Slot runtime: slot {SlotId} provider changed {Old} → {New}; recreating sink.",
                slotId, cached.Provider, provider);
            DetachRumbleFeedback(slotId);
            await DisposeSinkQuietlyAsync(slotId, cached.Sink);
            sinkCache.Remove(slotId);
        }

        var sink = outputSinkFactory.Create(provider);
        sinkCache[slotId] = (provider, sink);
        AttachRumbleFeedback(slotId, sink);
        return sink;
    }

    /// <summary>Disposes cached sinks whose slot no longer has a pipeline (deleted/disabled slots).</summary>
    private async Task PruneSinkCacheAsync()
    {
        var live = new HashSet<string>(pipelines.Select(p => p.SlotId), StringComparer.Ordinal);
        foreach (var slotId in sinkCache.Keys.ToList())
        {
            if (live.Contains(slotId))
            {
                continue;
            }
            var (_, sink) = sinkCache[slotId];
            sinkCache.Remove(slotId);
            DetachRumbleFeedback(slotId);
            await DisposeSinkQuietlyAsync(slotId, sink);
        }
    }

    private void AttachRumbleFeedback(string slotId, IOutputSink sink)
    {
        if (sink is not IRumbleFeedbackSource source)
        {
            return;
        }

        Action<double, double> handler = (low, high) => rumbleFeedbackStore.Set(slotId, low, high);
        source.RumbleReceived += handler;
        rumbleSubscriptions[slotId] = (source, handler);
    }

    private void DetachRumbleFeedback(string slotId)
    {
        if (rumbleSubscriptions.Remove(slotId, out var subscription))
        {
            subscription.Source.RumbleReceived -= subscription.Handler;
        }

        // A disposed or replaced virtual pad cannot send its final stop
        // after teardown, so clear any retained motor state explicitly.
        rumbleFeedbackStore.Clear(slotId);
    }

    private async ValueTask DisposeSinkQuietlyAsync(string slotId, IOutputSink sink)
    {
        try
        {
            await sink.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Error disposing cached sink for slot {SlotId}.", slotId);
        }
    }

    /// <summary>
    /// Pumps the input source once, then ticks every slot pipeline.
    /// Returns the first slot's result (input/output display names +
    /// frame) so the caller can keep the live-view snapshot updated, or
    /// null if nothing was processed.
    /// </summary>
    public async ValueTask<(string Input, string Output, ControllerFrameResult Result)?> TickAsync(
        IMultiDeviceInputSource input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        input.PumpForSlots();

        (string, string, ControllerFrameResult)? first = null;

        foreach (var pipeline in pipelines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var deviceIds = pipeline.InputDeviceIds;
                ControllerSnapshot snapshot;
                if (pipeline.Template.DemoPreview)
                {
                    // Demo preview: the slot runs on the built-in demo
                    // waveform instead of its assigned devices (works
                    // with zero devices assigned too). Everything
                    // downstream is real — profiles map it, panels
                    // animate, the virtual controller emits it.
                    snapshot = ApplyDeviceSettings(
                        pipeline.SlotId,
                        DeviceSettingsStore.SlotDefaultsDeviceId,
                        DemoInputSource.GenerateSnapshot(
                            demoClock.Elapsed.TotalSeconds, "Demo preview") with
                        { Timestamp = now });
                }
                else if (deviceIds.Count == 0)
                {
                    snapshot = ApplyDeviceSettings(
                        pipeline.SlotId,
                        DeviceSettingsStore.SlotDefaultsDeviceId,
                        ControllerSnapshot.Empty("No device assigned") with { Timestamp = now });
                }
                else if (deviceIds.Count == 1)
                {
                    snapshot = ApplyDeviceSettings(pipeline.SlotId, deviceIds[0], input.ReadDevice(deviceIds[0]));
                }
                else
                {
                    var snaps = new List<ControllerSnapshot>(deviceIds.Count);
                    foreach (var id in deviceIds)
                    {
                        // Conditioned per device BEFORE merging. Doing it
                        // after would apply one device's deadzone/curve to
                        // the combined result, which is wrong whenever the
                        // devices are tuned differently — and the whole
                        // point of per-device settings is that they can be.
                        snaps.Add(ApplyDeviceSettings(pipeline.SlotId, id, input.ReadDevice(id)));
                    }
                    snapshot = ControllerSnapshotMerger.Merge("Merged", snaps) with { Timestamp = now };
                }

                var result = await pipeline.ProcessAsync(snapshot, now, cancellationToken);
                mouseOutputWriter.MoveRelative(result.MouseDeltaX, result.MouseDeltaY);
                snapshotStore.Update(pipeline.SlotId, snapshot, result.VirtualSnapshot);
                snapshotStore.SetOutputStatus(pipeline.SlotId, pipeline.OutputDisplayName);
                // Lightbar removed: no per-tick SDL LED write to the physical
                // device (it could block the runtime loop on Bluetooth pads).
                first ??= ("Slot input", pipeline.OutputDisplayName, result);

                if (slotFailureCounts.TryGetValue(pipeline.SlotId, out var fails) && fails > 0)
                {
                    logger.LogInformation("Slot {SlotId} recovered after {Count} failure(s).", pipeline.SlotId, fails);
                    slotFailureCounts[pipeline.SlotId] = 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                int fails = slotFailureCounts.TryGetValue(pipeline.SlotId, out var prior) ? prior + 1 : 1;
                slotFailureCounts[pipeline.SlotId] = fails;
                if (fails == 1 || fails % 100 == 0)
                {
                    logger.LogWarning(exception, "Slot {SlotId} tick failed (failure #{Count}); other slots continue.", pipeline.SlotId, fails);
                }
            }
        }

        return first;
    }

    /// <summary>
    /// Applies this slot's tuning for one device (or the slot defaults when
    /// that device has no override). Short-circuits when the effective
    /// settings are all defaults — that's the common case, and this runs
    /// per device per tick at up to 1000 Hz.
    ///
    /// <para>
    /// The check is by VALUE, not by reference. Settings restored from
    /// device-settings.json are a fresh instance, never reference-equal to
    /// <see cref="DeviceSettings.Default"/>, so a reference fast path would
    /// only ever fire for the in-memory default — and the same untuned pad
    /// would condition differently depending on whether its settings came
    /// from memory or from disk.
    /// </para>
    /// </summary>
    private ControllerSnapshot ApplyDeviceSettings(string slotId, string deviceId, ControllerSnapshot snapshot)
    {
        var settings = deviceSettingsStore.GetEffective(slotId, deviceId);
        if (DeviceSettingsProcessor.IsIdentity(settings))
        {
            return snapshot;
        }
        return DeviceSettingsProcessor.Apply(snapshot, settings);
    }

    /// <summary>
    /// Loads and composes the slot's layered profiles, then appends the
    /// slot's own touchpad rule. Missing ids are skipped; an empty/
    /// all-missing set yields the neutral empty profile (no remapping) —
    /// slots are independent of the global active profile.
    /// </summary>
    private async Task<ProfileDocument> ResolveSlotProfileAsync(ControllerSlot slot)
    {
        if (slot.ProfileIds.Count == 0)
        {
            return WithSlotTouchpad(SlotProfileComposer.Empty, slot);
        }

        var layers = new List<ProfileDocument>(slot.ProfileIds.Count);
        foreach (var profileId in slot.ProfileIds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            logger.LogInformation(
                "Slot runtime: loading profile {ProfileId} for slot {SlotId}...",
                profileId, slot.Id);
            try
            {
                var doc = await profileRepository.LoadAsync(profileId);
                sw.Stop();
                logger.LogInformation(
                    "Slot runtime: profile {ProfileId} loaded for slot {SlotId} in {ElapsedMs} ms (found={Found}).",
                    profileId, slot.Id, sw.ElapsedMilliseconds, doc is not null);
                if (doc is not null)
                {
                    layers.Add(doc);
                }
                else
                {
                    logger.LogWarning("Slot {SlotId}: profile '{ProfileId}' not found; skipping layer.", slot.Id, profileId);
                }
            }
            catch (Exception exception)
            {
                sw.Stop();
                logger.LogWarning(exception,
                    "Slot {SlotId}: failed to load profile '{ProfileId}' after {ElapsedMs} ms.",
                    slot.Id, profileId, sw.ElapsedMilliseconds);
            }
        }

        return WithSlotTouchpad(SlotProfileComposer.Compose(layers), slot);
    }

    /// <summary>
    /// Appends the slot's touchpad rule to its composed profile, so the
    /// mapping pipeline picks it up like any other rule.
    ///
    /// <para>
    /// Appended LAST, after every profile layer, because the touchpad
    /// belongs to the slot's hardware rather than to a profile: swapping
    /// or reordering profiles must not change what the touch surface
    /// does. Any touchpad rule that a layered profile happens to carry
    /// still runs — both are in the list — but the slot's own rule is
    /// applied after it and so wins on the controls they share.
    /// </para>
    /// </summary>
    private static ProfileDocument WithSlotTouchpad(ProfileDocument composed, ControllerSlot slot)
    {
        if (slot.Touchpad is null)
        {
            return composed;
        }

        // The rule id is derived from the slot id rather than carried in
        // the saved rule, so a slot duplicated through the registry can't
        // hand its copy a colliding id — the pipeline keys per-rule
        // gesture and anchor state by that id, and two slots sharing one
        // would be two touch surfaces writing over each other's state.
        var rule = slot.Touchpad with { Id = $"slot-touchpad-{slot.Id}" };
        return composed with { Rules = [.. composed.Rules, rule] };
    }

    private async Task DisposePipelinesAsync()
    {
        foreach (var pipeline in pipelines)
        {
            try
            {
                await pipeline.DisposeAsync();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Error disposing slot pipeline {SlotId}.", pipeline.SlotId);
            }
        }
        pipelines.Clear();
        slotFailureCounts.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposePipelinesAsync();
        foreach (var (slotId, entry) in sinkCache.ToList())
        {
            DetachRumbleFeedback(slotId);
            await DisposeSinkQuietlyAsync(slotId, entry.Sink);
        }
        sinkCache.Clear();
    }
}
