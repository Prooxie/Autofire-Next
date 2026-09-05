using System.Text.Json;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Runtime.Templates;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Slots;

/// <summary>
/// Owns the set of <see cref="ControllerSlot"/> definitions and persists
/// them to <see cref="AppPaths.SlotsFile"/>. This is the Phase 3a data
/// foundation: the multi-slot runtime (3b) reads enabled slots from here
/// to build one pipeline each, and the management UI (3c) mutates slots
/// through this registry.
///
/// <para>Capacity is capped at <see cref="MaxSlots"/> (16, matching
/// the MaxPads ceiling). All mutations persist immediately and raise
/// <see cref="SlotsChanged"/>. Thread-safe via a single lock; the change
/// event is raised outside the lock.</para>
/// </summary>
public sealed class SlotRegistry
{
    /// <summary>Maximum number of slots (mirrors MaxPads ceiling).</summary>
    public const int MaxSlots = 16;

    private readonly ILogger<SlotRegistry> logger;
    private readonly Lock gate = new();
    private List<ControllerSlot> slots = [];

    /// <summary>
    /// Detached, Index-ordered view of <see cref="slots"/>, rebuilt only
    /// when the registry actually changes.
    ///
    /// <para>
    /// <see cref="GetSlots"/> used to deep-clone every slot, sort them and
    /// materialise a fresh list on <b>every call</b>, under the lock. That
    /// is a read the runtime loop performs once per tick — up to 1000 times
    /// a second — plus once per frame from the effects producer and the DSU
    /// server, so a registry that changed a few times an hour was rebuilding
    /// its entire contents tens of thousands of times a second. Publishing a
    /// snapshot on mutation instead makes the common read allocation-free
    /// and lock-cheap while keeping the same contract: callers still get
    /// objects detached from the registry's own mutable state.
    /// </para>
    ///
    /// <para>
    /// The published instances are shared between readers and must be
    /// treated as read-only. Nothing mutates a slot outside this class;
    /// callers that need an editable copy use <see cref="GetSlot"/>, which
    /// still clones.
    /// </para>
    /// </summary>
    private IReadOnlyList<ControllerSlot> snapshot = [];

    /// <summary>Cached alongside <see cref="snapshot"/> so the runtime tick's "is there anything to run?" check costs a field read.</summary>
    private bool hasEnabledSlots;

    /// <summary>
    /// Backing file. Defaults to <see cref="AppPaths.SlotsFile"/>; the
    /// override exists so tests can exercise the registry without reading
    /// or overwriting the running user's real slot configuration — the
    /// same seam <see cref="DeviceSettingsStore"/> already uses.
    /// </summary>
    private readonly string filePath;

    public SlotRegistry(ILogger<SlotRegistry> logger, string? overridePath = null)
    {
        this.logger = logger;
        filePath = overridePath ?? AppPaths.SlotsFile;
        Load();
        lock (gate)
        {
            PublishSnapshotLocked();
        }
    }

    /// <summary>Raised after any successful mutation (create/delete/edit).</summary>
    public event EventHandler? SlotsChanged;

    /// <summary>Current number of slots.</summary>
    public int Count
    {
        get { lock (gate) { return slots.Count; } }
    }

    /// <summary>True if another slot can be created.</summary>
    public bool CanCreate
    {
        get { lock (gate) { return slots.Count < MaxSlots; } }
    }

    /// <summary>Returns a detached, Index-ordered snapshot of all slots.</summary>
    public IReadOnlyList<ControllerSlot> GetSlots()
    {
        lock (gate)
        {
            return snapshot;
        }
    }

    /// <summary>
    /// True when at least one slot is enabled. Read once per runtime tick
    /// to decide whether the multi-slot path runs at all, so it answers
    /// from the cached snapshot rather than materialising one.
    /// </summary>
    public bool HasEnabledSlots
    {
        get { lock (gate) { return hasEnabledSlots; } }
    }

    /// <summary>Returns a detached copy of one slot, or null.</summary>
    public ControllerSlot? GetSlot(string id)
    {
        lock (gate)
        {
            return slots.FirstOrDefault(s => s.Id == id)?.Clone();
        }
    }

    /// <summary>
    /// Creates a slot of the given output kind at the next free index, up
    /// to <see cref="MaxSlots"/>. Returns the created slot, or null if at
    /// capacity.
    /// </summary>
    public ControllerSlot? CreateSlot(VirtualControllerKind kind)
    {
        ControllerSlot created;
        lock (gate)
        {
            if (slots.Count >= MaxSlots)
            {
                return null;
            }

            int index = slots.Count == 0 ? 0 : slots.Max(s => s.Index) + 1;
            created = new ControllerSlot
            {
                Index = index,
                Name = DefaultName(kind, index),
                OutputTemplate = new DeviceOutputTemplate { OutputKind = kind, Enabled = true },
            };
            created.OutputTemplate.DeviceId = created.Id;
            slots.Add(created);
            Persist();
        }

        Raise();
        return created.Clone();
    }

    /// <summary>
    /// Creates a new slot that copies everything from <paramref name="sourceId"/>
    /// — enabled state, assigned input devices, output template, and
    /// layered profiles — except identity: a fresh id, the next free
    /// index, and "{name} (Copy)" so it's obviously a duplicate rather
    /// than silently identical in the list. Returns null if the source
    /// doesn't exist or the registry is already at capacity.
    /// </summary>
    public ControllerSlot? DuplicateSlot(string sourceId)
    {
        ControllerSlot created;
        lock (gate)
        {
            var source = slots.FirstOrDefault(s => s.Id == sourceId);
            if (source is null || slots.Count >= MaxSlots)
            {
                return null;
            }

            int index = slots.Count == 0 ? 0 : slots.Max(s => s.Index) + 1;
            created = source.Clone();
            created.Id = Guid.NewGuid().ToString("N");
            created.Index = index;
            created.Name = $"{source.Name} (Copy)";
            created.OutputTemplate.DeviceId = created.Id;
            slots.Add(created);
            Persist();
        }

        Raise();
        return created.Clone();
    }

    /// <summary>Removes a slot. Returns true if a slot was removed.</summary>
    public bool DeleteSlot(string id)
    {
        bool removed;
        lock (gate)
        {
            removed = slots.RemoveAll(s => s.Id == id) > 0;
            if (removed)
            {
                Normalize();
                Persist();
            }
        }

        if (removed)
        {
            Raise();
        }
        return removed;
    }

    /// <summary>Enables or disables a slot.</summary>
    public void SetEnabled(string id, bool enabled) =>
        Mutate(id, s => s.Enabled = enabled);

    /// <summary>Renames a slot.</summary>
    public void Rename(string id, string name) =>
        Mutate(id, s => s.Name = string.IsNullOrWhiteSpace(name)
            ? FallbackName(s.Index)
            : name.Trim());

    /// <summary>Default display name for a slot with no user-given name.</summary>
    private static string FallbackName(int index) => $"Virtual Controller {index + 1}";

    /// <summary>Assigns a physical input device to a slot (idempotent).</summary>
    public void AssignDevice(string id, string deviceId) =>
        Mutate(id, s =>
        {
            if (!string.IsNullOrWhiteSpace(deviceId) && !s.InputDeviceIds.Contains(deviceId))
            {
                s.InputDeviceIds.Add(deviceId);
            }
        });

    /// <summary>Removes a device assignment from a slot.</summary>
    public void UnassignDevice(string id, string deviceId) =>
        Mutate(id, s => s.InputDeviceIds.Remove(deviceId));

    /// <summary>Replaces a slot's output template wholesale.</summary>
    public void UpdateTemplate(string id, DeviceOutputTemplate template) =>
        Mutate(id, s =>
        {
            if (template is not null)
            {
                var clone = template.Clone();
                clone.DeviceId = s.Id;
                s.OutputTemplate = clone;
            }
        });

    /// <summary>
    /// Replaces the slot's touchpad configuration. Null clears it, so the
    /// slot stops contributing a touchpad rule entirely rather than
    /// contributing an all-off one.
    /// </summary>
    public void UpdateTouchpad(string id, GameFlow.Core.Models.Rules.TouchpadMapRule? touchpad) =>
        Mutate(id, s => s.Touchpad = touchpad);

    /// <summary>Replaces the slot's layered profile id list (in order).</summary>
    public void SetProfiles(string id, IEnumerable<string> profileIds) =>
        Mutate(id, s => s.ProfileIds = profileIds
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList());

    private void Mutate(string id, Action<ControllerSlot> apply)
    {
        bool changed = false;
        lock (gate)
        {
            var slot = slots.FirstOrDefault(s => s.Id == id);
            if (slot is not null)
            {
                apply(slot);
                Persist();
                changed = true;
            }
        }

        if (changed)
        {
            Raise();
        }
    }

    /// <summary>Compacts indices to 0..N-1 in current sort order.</summary>
    private void Normalize()
    {
        int i = 0;
        foreach (var slot in slots.OrderBy(s => s.Index))
        {
            slot.Index = i++;
        }
    }

    private static string DefaultName(VirtualControllerKind kind, int index)
    {
        // Per UX feedback: don't auto-name slots like "Generic 1" / "Xbox 360 1".
        // Leave blank so the user names it themselves (or leaves it empty).
        return string.Empty;
    }

    private void Raise() => SlotsChanged?.Invoke(this, EventArgs.Empty);

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<List<ControllerSlot>>(json, ProfileJsonOptions.Default);
            if (loaded is not null)
            {
                slots = loaded;
                // Migrate the deprecated single ProfileId into ProfileIds.
                foreach (var slot in slots)
                {
                    if (slot.ProfileIds.Count == 0 && !string.IsNullOrWhiteSpace(slot.ProfileId))
                    {
                        slot.ProfileIds = [slot.ProfileId!];
                    }
                    slot.ProfileId = null;
                    // Backfill: files written before default naming existed
                    // (or hand-edited) may carry empty names.
                    if (string.IsNullOrWhiteSpace(slot.Name))
                    {
                        slot.Name = FallbackName(slot.Index);
                    }
                    // Backfill: slots saved before per-slot output
                    // providers existed carry an empty OutputProvider,
                    // meaning "silently inherit the profile's default" —
                    // historically Preview, the confusing no-real-device
                    // footgun this whole restriction exists to close.
                    if (string.IsNullOrWhiteSpace(slot.OutputTemplate.OutputProvider))
                    {
                        slot.OutputTemplate.OutputProvider = OutputProviderPolicy.Resolve(null);
                    }
                    // Migration: on Windows, HIDMaestro is the ONLY
                    // output backend this build ships — retired vigem-*
                    // ids, the old per-slot "preview" choice, and any
                    // unknown value all migrate forward via
                    // OutputProviderPolicy instead of silently resolving
                    // to nothing (or to a preview sink that reads as
                    // "broken output"). Non-Windows resolves to preview
                    // the same way, since HIDMaestro can't run there.
                    else if (OutputProviderPolicy.RequiresMigration(
                                 slot.OutputTemplate.OutputProvider, OperatingSystem.IsWindows()))
                    {
                        var migrated = OutputProviderPolicy.Resolve(slot.OutputTemplate.OutputProvider);
                        logger.LogInformation(
                            "Slot {SlotId} used output provider '{OldProvider}' — migrated to '{NewProvider}' " +
                            "(the platform's sole output backend).",
                            slot.Id, slot.OutputTemplate.OutputProvider, migrated);
                        slot.OutputTemplate.OutputProvider = migrated;
                    }
                }
                Normalize();
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to load controller slots; starting empty.");
            slots = [];
        }
    }

    /// <summary>
    /// Republishes <see cref="snapshot"/> from the current slot list.
    /// Must be called with <see cref="gate"/> held, after every mutation.
    /// </summary>
    private void PublishSnapshotLocked()
    {
        var published = new List<ControllerSlot>(slots.Count);
        var anyEnabled = false;
        foreach (var slot in slots.OrderBy(s => s.Index))
        {
            published.Add(slot.Clone());
            anyEnabled |= slot.Enabled;
        }

        snapshot = published;
        hasEnabledSlots = anyEnabled;
    }

    private void Persist()
    {
        // Every mutation path persists, so this is the one place guaranteed
        // to run after a change with the lock held — republishing here means
        // a new mutation can never forget to refresh the snapshot.
        PublishSnapshotLocked();

        try
        {
            var json = JsonSerializer.Serialize(slots, ProfileJsonOptions.Default);
            File.WriteAllText(filePath, json);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save controller slots.");
        }
    }
}
