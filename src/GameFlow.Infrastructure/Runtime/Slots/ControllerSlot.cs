using GameFlow.Core.Models.Rules;
using GameFlow.Infrastructure.Runtime.Templates;

namespace GameFlow.Infrastructure.Runtime.Slots;

/// <summary>
/// A single controller slot: a self-contained unit that maps one or more
/// assigned physical input devices through its own mapping pipeline into
/// one virtual controller described by <see cref="OutputTemplate"/>.
///
/// <para>This is the Phase 3 per-pad slot config
/// (SlotCreated / SlotEnabled / SlotControllerTypes + assigned devices),
/// adapted to Autofire: the per-slot output configuration is the
/// <see cref="DeviceOutputTemplate"/> introduced in Phase 2a, now owned
/// by the slot rather than keyed per physical device.</para>
///
/// <para>Mutable POCO so it round-trips through System.Text.Json and the
/// management UI (Phase 3c) can bind to it.</para>
/// </summary>
public sealed class ControllerSlot
{
    /// <summary>Stable identifier, assigned once at creation.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display position / ordering (0-based). Lower sorts first.</summary>
    public int Index { get; set; }

    /// <summary>Human-readable name (defaults from the output kind).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When false, the runtime skips this slot entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Catalog ids of the physical input devices feeding this slot. A slot
    /// may aggregate several devices (e.g. a stick + a throttle), matching
    /// the multi-device-per-slot model.
    /// </summary>
    public List<string> InputDeviceIds { get; set; } = [];

    /// <summary>
    /// The virtual controller this slot emits — output kind, lighting,
    /// rumble, FFB, adaptive triggers, generic shape. The slot owns its
    /// template; <see cref="OutputTemplate"/>.DeviceId carries the slot id.
    /// </summary>
    public DeviceOutputTemplate OutputTemplate { get; set; } = new();

    /// <summary>
    /// Ordered ids of the mapping profiles layered onto this slot. Empty
    /// runs a neutral empty profile (no remapping). Later entries are
    /// applied after earlier ones (their rules win on overlap).
    /// </summary>
    public List<string> ProfileIds { get; set; } = [];

    /// <summary>
    /// Deprecated single-profile field, kept so older slot files still
    /// deserialize; migrated into <see cref="ProfileIds"/> on load.
    /// </summary>
    public string? ProfileId { get; set; }

    /// <summary>
    /// This slot's touchpad configuration — the anchored stick, wedge
    /// D-pad, per-axis mouse, and the gesture bindings. Null means the
    /// slot's Touchpad tab was never opened, and no touchpad rule is
    /// contributed at all.
    ///
    /// <para>
    /// It lives on the SLOT rather than in a mapping profile because a
    /// touchpad is a property of the hardware feeding this slot, not of
    /// a set of remapping rules: profiles are layered, shared between
    /// slots and swapped per app, and a DualSense's touch surface should
    /// not stop working because the active profile changed. Being a
    /// <see cref="TouchpadMapRule"/> rather than a parallel settings type
    /// is what lets it be appended straight onto the composed profile —
    /// see <c>SlotRuntime.ResolveSlotProfileAsync</c> — so the mapping
    /// pipeline needs no special case for it.
    /// </para>
    /// </summary>
    public TouchpadMapRule? Touchpad { get; set; }

    /// <summary>Deep-ish copy for detached editing (template cloned too).</summary>
    public ControllerSlot Clone() => new()
    {
        Id = Id,
        Index = Index,
        Name = Name,
        Enabled = Enabled,
        InputDeviceIds = [.. InputDeviceIds],
        OutputTemplate = OutputTemplate.Clone(),
        ProfileIds = [.. ProfileIds],
        ProfileId = ProfileId,
        // Shared by reference on purpose: TouchpadMapRule is an immutable
        // record (as is every binding in it), so a detached copy can't be
        // mutated through this reference — edits produce a new instance
        // via `with` and are handed back through SlotRegistry.
        Touchpad = Touchpad,
    };
}
