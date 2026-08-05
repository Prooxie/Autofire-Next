using System.Collections.ObjectModel;
using System.Windows.Input;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Models;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Theme-engine extension for <see cref="ControllerVisualStateViewModel"/>.
///
/// <para>
/// This is a <see langword="partial"/> companion file. The legacy view-model
/// in the sibling .cs file owns the per-tick property fan-out and the
/// programmatic-art bindings; this partial adds the theme-engine surface area
/// the new <see cref="GameFlow.App.Views.ThemeSurface"/> binds against:
/// </para>
///
/// <list type="bullet">
/// <item><see cref="AvailableThemeVariants"/> — every theme installed for the current style;</item>
/// <item><see cref="SelectedThemeVariant"/> — the user's pick (drives <see cref="ActiveTheme"/>);</item>
/// <item><see cref="IsPhysicalView"/> — when true, only the base layer renders (no active overlays);</item>
/// <item><see cref="PanelBackgroundBrush"/> — user background preset; empty follows the app theme (see <see cref="PanelOverlayBrush"/>);</item>
/// <item><see cref="ClickAtCommand"/> — emits a logical-coordinate click for the host's click-to-map path.</item>
/// </list>
/// </summary>
public sealed partial class ControllerVisualStateViewModel
{
    // ─── Theme registry (injected after construction) ──────────────────────

    /// <summary>
    /// Theme registry the VM consults to find a matching theme for the
    /// current style. Set by the host once at wiring time, then forgotten
    /// about — refreshes happen explicitly via
    /// <see cref="RefreshActiveTheme"/>.
    ///
    /// <para>
    /// Optional: when <see langword="null"/> (the default, e.g. under
    /// design-time data or unit tests), <see cref="ActiveTheme"/> stays
    /// null and the programmatic XAML art renders as before.
    /// </para>
    /// </summary>
    public ThemeRegistry? ThemeRegistry
    {
        get => themeRegistry;
        set
        {
            if (ReferenceEquals(themeRegistry, value)) { return; }
            themeRegistry = value;
            RefreshActiveTheme();
        }
    }
    private ThemeRegistry? themeRegistry;

    /// <summary>
    /// True when this view-model drives the *physical* (input-side) panel.
    /// Set once at construction by <c>ShellViewModel</c>. The theme
    /// surface honours it by rendering only the base image (the first
    /// <c>image</c> child of the theme document) and skipping every
    /// <c>showhide</c>/<c>pbar</c>/active overlay — fulfilling the design
    /// rule that the physical controller is read-only (the actual model
    /// only, no live feedback overlays).
    /// </summary>
    public bool IsPhysicalView { get; private set; }

    /// <summary>
    /// Called by the host immediately after construction to mark this
    /// instance as the physical or virtual panel. Separated from the
    /// constructor so the existing two-argument constructor signature
    /// stays backwards-compatible.
    /// </summary>
    public void SetPanelKind(bool isPhysical)
    {
        IsPhysicalView = isPhysical;
        OnPropertyChanged(nameof(IsPhysicalView));
    }

    // ─── Theme variant list ────────────────────────────────────────────────

    /// <summary>
    /// Bound to the variant-picker ComboBox. Populated by
    /// <see cref="RefreshActiveTheme"/> whenever the registry or style
    /// changes. Always reflects only the variants matching the current
    /// visual style (e.g. switching from DualSense to Xbox replaces the
    /// list wholesale).
    /// </summary>
    public ObservableCollection<InstalledTheme> AvailableThemeVariants { get; } = [];

    /// <summary>
    /// True when at least one theme variant is installed for the
    /// current style — drives the variant ComboBox's IsVisible binding.
    /// Separate bool property (rather than binding directly to
    /// <c>AvailableThemeVariants.Count</c>) so the AXAML doesn't need
    /// an int→bool converter.
    /// </summary>
    public bool HasThemeVariants => AvailableThemeVariants.Count > 0;

    /// <summary>
    /// Backing field for <see cref="SelectedThemeVariant"/>. Mutated only
    /// through the setter so a UI selection cascades into
    /// <see cref="ActiveTheme"/> and the per-style preference in the
    /// host's settings.
    /// </summary>
    private InstalledTheme? selectedThemeVariant;

    /// <summary>
    /// The variant the user has chosen out of
    /// <see cref="AvailableThemeVariants"/>. Setting it republishes
    /// <see cref="ActiveTheme"/> and fires
    /// <see cref="ThemeVariantUserSelected"/> so the shell can persist
    /// the choice per controller-style.
    /// </summary>
    public InstalledTheme? SelectedThemeVariant
    {
        get => selectedThemeVariant;
        set
        {
            if (ReferenceEquals(selectedThemeVariant, value)) { return; }
            Log.Information(
                "SelectedThemeVariant changed on {Panel}: '{Old}' -> '{New}' (user pick via ComboBox).",
                IsPhysicalView ? "physical" : "virtual",
                selectedThemeVariant?.DisplayName ?? "(none)",
                value?.DisplayName ?? "(none)");

            selectedThemeVariant = value;
            OnPropertyChanged(nameof(SelectedThemeVariant));

            ActiveTheme = value;
            OnPropertyChanged(nameof(ActiveTheme));
            OnPropertyChanged(nameof(IsThemeActive));
            OnPropertyChanged(nameof(ActiveThemeDisplayName));

            ThemeVariantUserSelected?.Invoke(this, value);
        }
    }

    /// <summary>
    /// Fires when the user picks a variant from the ComboBox so the host
    /// (ShellViewModel) can persist the per-style preference. Not raised
    /// when the selection is set programmatically by
    /// <see cref="RefreshActiveTheme"/> — see the private flag inside.
    /// </summary>
    public event EventHandler<InstalledTheme?>? ThemeVariantUserSelected;

    // ─── Active theme (read by ThemeSurface) ───────────────────────────────

    /// <summary>
    /// Theme actually being painted right now. Driven by
    /// <see cref="SelectedThemeVariant"/>, but exposed separately so the
    /// surface doesn't have to know about the variant list.
    /// </summary>
    public InstalledTheme? ActiveTheme { get; private set; }

    /// <summary>
    /// True when an installed theme exists for the current style — used
    /// by the surface AXAML to gate the "no theme installed" placeholder
    /// text vs. the live theme renderer.
    /// </summary>
    public bool IsThemeActive => ActiveTheme is not null;

    /// <summary>Display name shown in the panel header when a theme is active.</summary>
    public string ActiveThemeDisplayName =>
        ActiveTheme?.DisplayName ?? string.Empty;

    /// <summary>
    /// Raw <see cref="ControllerSnapshot"/> the VM was last fed, exposed
    /// so <see cref="GameFlow.App.Views.ControllerSurface"/> can forward
    /// it to <see cref="GameFlow.App.Views.ThemeSurface"/> each tick.
    /// </summary>
    public ControllerSnapshot RawSnapshot => snapshot;

    /// <summary>
    /// Live virtual-output light colour in #AARRGGBB form. Theme lightbar
    /// nodes consume this value; transparent means the light is off.
    /// </summary>
    public string LightColor
    {
        get => lightColor;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "#00000000" : value;
            if (string.Equals(lightColor, normalized, StringComparison.OrdinalIgnoreCase)) { return; }
            lightColor = normalized;
            OnPropertyChanged(nameof(LightColor));
        }
    }
    private string lightColor = "#00000000";

    // ─── Background (transparency setting) ─────────────────────────────────

    /// <summary>
    /// Hex/named colour the surface paints behind the controller art.
    /// Defaults to the dark "#09111B" the original AXAML used; settable
    /// to "Transparent" for the see-through effect, or any other colour
    /// the user wants. Persisted by <see cref="ShellViewModel"/> through
    /// <see cref="GameFlow.Infrastructure.Configuration.IUserSettingsService"/>.
    /// </summary>
    private string panelBackgroundBrush = "#09111B";
    /// <summary>
    /// Brush painted OVER the theme-following panel base. Null (the
    /// "Theme default" preset, the legacy "#09111B" default, or
    /// "Transparent") lets the themed base show through so app-theme
    /// switches recolour the panel live; any other value is a forced
    /// colour (chroma green/blue, pure black, custom hex).
    /// </summary>
    public Avalonia.Media.IBrush? PanelOverlayBrush
    {
        get
        {
            var value = panelBackgroundBrush;
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value, "#09111B", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            try { return Avalonia.Media.Brush.Parse(value); }
            catch { return null; }
        }
    }

    public string PanelBackgroundBrush
    {
        get => panelBackgroundBrush;
        set
        {
            if (panelBackgroundBrush == value) { return; }
            panelBackgroundBrush = value ?? "";
            OnPropertyChanged(nameof(PanelBackgroundBrush));
            OnPropertyChanged(nameof(PanelOverlayBrush));
        }
    }

    // ─── Click-to-map plumbing ─────────────────────────────────────────────

    /// <summary>
    /// Command bound to
    /// <see cref="GameFlow.App.Views.ThemeSurface.Clicked"/>. Receives a
    /// <c>(x, y)</c> tuple in theme-local coordinates (passed as a boxed
    /// <see cref="ValueTuple{Double, Double}"/>) and resolves the hit
    /// to a logical control id via <see cref="ThemeHitTester.TryHit"/>.
    /// On a hit, it routes the element id through the existing
    /// <see cref="SelectElementCommand"/> pipeline so click-to-map shares
    /// the same selection path the programmatic art already uses.
    ///
    /// <para>
    /// The parameter type is deliberately <see cref="object"/> rather
    /// than a strongly-typed nullable tuple: CommunityToolkit's
    /// <c>RelayCommand&lt;T?&gt;</c> has corner-cases around boxed
    /// nullable value types that I'd rather not depend on.
    /// </para>
    /// </summary>
    public ICommand ClickAtCommand =>
        clickAtCommand ??= new RelayCommand<object?>(OnClickAt);
    private RelayCommand<object?>? clickAtCommand;

    private void OnClickAt(object? boxedPosition)
    {
        if (ActiveTheme is null) { return; }
        if (boxedPosition is not (double x, double y))
        {
            return;
        }

        var hit = ThemeHitTester.TryHit(ActiveTheme.Document, x, y);
        if (hit is not null && !string.IsNullOrWhiteSpace(hit.ElementId))
        {
            SelectElementCommand.Execute(hit.ElementId);
        }
    }

    // ─── Refresh hook called by the host ───────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="AvailableThemeVariants"/> from the registry
    /// for the current <c>visualStyle</c> and re-selects the variant
    /// that matches the host-supplied preference (or the first available
    /// when no preference is stored).
    /// </summary>
    public void RefreshActiveTheme()
    {
        var registry = themeRegistry;
        IReadOnlyList<InstalledTheme> variants = registry?.GetThemesForStyle(visualStyle) ?? [];

        // Replace AvailableThemeVariants in place to avoid losing the
        // ComboBox's binding identity. We do a small diff against the
        // current contents so the binding only re-binds when the set
        // actually changed.
        var changed = AvailableThemeVariants.Count != variants.Count;
        if (!changed)
        {
            for (var i = 0; i < variants.Count; i++)
            {
                if (!ReferenceEquals(AvailableThemeVariants[i], variants[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            AvailableThemeVariants.Clear();
            foreach (var v in variants)
            {
                AvailableThemeVariants.Add(v);
            }
            OnPropertyChanged(nameof(HasThemeVariants));
        }

        // Re-pick a variant: keep the existing one if it's still
        // available; otherwise prefer the host's remembered preference;
        // finally fall back to the first available variant.
        InstalledTheme? preferred = null;
        if (selectedThemeVariant is not null &&
            variants.Any(v => ReferenceEquals(v, selectedThemeVariant)))
        {
            preferred = selectedThemeVariant;
        }
        else if (preferredVariantId is not null)
        {
            preferred = variants.FirstOrDefault(v =>
                string.Equals(v.Id, preferredVariantId, StringComparison.OrdinalIgnoreCase));
        }
        preferred ??= variants.FirstOrDefault();

        // Assign without going through the public setter — we don't
        // want to fire the "user picked this" event for a programmatic
        // restoration.
        if (!ReferenceEquals(selectedThemeVariant, preferred))
        {
            selectedThemeVariant = preferred;
            ActiveTheme = preferred;
            OnPropertyChanged(nameof(SelectedThemeVariant));
            OnPropertyChanged(nameof(ActiveTheme));
            OnPropertyChanged(nameof(IsThemeActive));
            OnPropertyChanged(nameof(ActiveThemeDisplayName));
        }
    }

    /// <summary>
    /// Variant id the host wants restored on the next
    /// <see cref="RefreshActiveTheme"/> call.
    /// </summary>
    private string? preferredVariantId;

    /// <summary>
    /// Pushes the user's persisted variant preference into the VM.
    /// </summary>
    public void SetPreferredVariantId(string? variantId)
    {
        preferredVariantId = string.IsNullOrWhiteSpace(variantId) ? null : variantId;
    }
}
