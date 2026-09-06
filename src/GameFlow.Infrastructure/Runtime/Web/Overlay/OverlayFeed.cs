using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.HidMaestro;
using GameFlow.Infrastructure.Runtime.Slots;
using GameFlow.Infrastructure.Theming;

namespace GameFlow.Infrastructure.Runtime.Web.Overlay;

/// <summary>
/// Answers the two questions an overlay connection asks: which theme am
/// I drawing, and what is the controller doing right now.
///
/// <para>
/// It reads the same <see cref="SlotSnapshotStore"/> the dashboard reads,
/// so the overlay and the app show the same controller state by
/// construction rather than by a second copy of the input path kept in
/// agreement.
/// </para>
/// </summary>
public sealed class OverlayFeed(
    ThemeRegistry themes,
    SlotRegistry slots,
    SlotSnapshotStore snapshots)
{
    private readonly ThemeRegistry themes = themes;
    private readonly SlotRegistry slots = slots;
    private readonly SlotSnapshotStore snapshots = snapshots;

    /// <summary>What a connection asked for, parsed out of the query string.</summary>
    public sealed record Request(string? ThemeId, string? SlotId, bool Physical);

    /// <summary>
    /// Every slot that could be shown, for the app's URL builder and for
    /// the error message when a requested slot is gone.
    /// </summary>
    public IReadOnlyList<ControllerSlot> Slots() => slots.GetSlots();

    /// <summary>Themes the overlay can serve, in registry order.</summary>
    public IReadOnlyList<InstalledTheme> Themes() => themes.Themes;

    /// <summary>
    /// Resolves the theme to draw. An explicit <c>theme</c> wins; without
    /// one, the slot's own output kind picks a theme the way the
    /// dashboard's virtual panel does, so a URL with no theme still shows
    /// the right controller. Null when neither route finds anything —
    /// which for a fresh install with no themes is the honest answer.
    /// </summary>
    public InstalledTheme? ResolveTheme(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var explicitTheme = themes.GetThemeById(request.ThemeId);
        if (explicitTheme is not null)
        {
            return explicitTheme;
        }

        var slot = FindSlot(request.SlotId);
        if (slot is null)
        {
            return null;
        }

        var template = slot.OutputTemplate;
        var family = string.IsNullOrWhiteSpace(template.OutputProfileId)
            ? template.OutputKind
            : HidMaestroProfiles.ClassifyFamily(template.OutputProfileId);

        var style = HidMaestroProfiles.ResolveVisualStyle(family);
        // Same stand-in rule as the dashboard surfaces: an overlay that
        // draws a near-identical pad beats one that draws nothing on a
        // live stream.
        return themes.GetThemeForStyle(themes.ResolveRenderableStyle(style));
    }

    /// <summary>
    /// The current snapshot for a request. The physical side shows the
    /// pad in the streamer's hands; the virtual side shows what the game
    /// receives, which is what a viewer watching for input reads is
    /// usually after — hence virtual being the default.
    ///
    /// <para>
    /// A slot that no longer exists returns an empty snapshot rather than
    /// throwing: slots are created and deleted while a stream is live,
    /// and an overlay that goes neutral is a far better outcome than one
    /// that drops its socket mid-scene.
    /// </para>
    /// </summary>
    public ControllerSnapshot Snapshot(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var slot = FindSlot(request.SlotId);
        if (slot is null)
        {
            return ControllerSnapshot.Empty();
        }

        var pair = snapshots.Get(slot.Id);
        return request.Physical ? pair.Physical : pair.Virtual;
    }

    /// <summary>
    /// The slot's lightbar colour in <c>#AARRGGBB</c>, or null when
    /// lighting is off — matching what the dashboard hands its own
    /// surfaces. Only the virtual side has a light: it is an OUTPUT
    /// effect, and colouring the physical pad's lightbar with it would
    /// show the streamer a light their hardware is not displaying.
    /// </summary>
    public string? LightColor(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Physical)
        {
            return null;
        }

        var slot = FindSlot(request.SlotId);
        var template = slot?.OutputTemplate;
        return template is { LightingEnabled: true }
            ? $"#FF{template.LightR:X2}{template.LightG:X2}{template.LightB:X2}"
            : null;
    }

    /// <summary>
    /// The named slot, or the first one when the URL named none. That
    /// fallback is what makes a bare <c>/overlay</c> useful: the common
    /// case is one controller, and asking someone to find a slot GUID
    /// before they can see anything is a poor first run.
    /// </summary>
    private ControllerSlot? FindSlot(string? slotId)
    {
        var all = slots.GetSlots();
        if (all.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(slotId))
        {
            return all[0];
        }

        foreach (var slot in all)
        {
            if (string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }
        }

        return null;
    }
}
