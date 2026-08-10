using System.Windows.Input;
using GameFlow.App.Services;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Localization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace GameFlow.App.ViewModels;

/// <summary>
/// View-model for the live controller surface panel.
///
/// Performance contract:
///   • Never calls OnPropertyChanged(string.Empty) — that forces Avalonia to
///     re-evaluate every binding on every timer tick.
///   • Properties are grouped into three change tiers:
///       Identity — device name / connection state  (rare)
///       Style    — button labels, colours          (rare, only on style change)
///       Live     — opacities, stick offsets, etc.  (per-tick, only when data moved)
///   • String-valued live properties are cached to avoid per-tick allocations.
/// </summary>
public sealed partial class ControllerVisualStateViewModel : ViewModelBase
{
    // ─── Static property-name lists ───────────────────────────────────────────
    private static readonly string[] IdentityProps =
    [
        nameof(PanelTitle), nameof(Title), nameof(DeviceName), nameof(IsConnected),
        nameof(ConnectionLabel), nameof(ConnectionBrush), nameof(PanelBorderBrush),
        nameof(MinimalSummaryText),
    ];

    private static readonly string[] StyleProps =
    [
        nameof(AccentBrush), nameof(AccentSoftBrush),
        nameof(ShellBrush), nameof(ShellHighlightBrush),
        nameof(SouthLegend), nameof(EastLegend), nameof(WestLegend), nameof(NorthLegend),
        nameof(SouthFaceBrush), nameof(EastFaceBrush), nameof(WestFaceBrush), nameof(NorthFaceBrush),
        nameof(LeftShoulderLabel), nameof(RightShoulderLabel),
        nameof(LeftTriggerLabel), nameof(RightTriggerLabel),
        nameof(BackLabel), nameof(StartLabel), nameof(GuideLabel),
        nameof(StyleLabel), nameof(ShowTouchpad), nameof(ShowShell),
        // Style-flag bindings consumed by ControllerSurface.axaml. These were
        // referenced from XAML before they existed on the VM (with
        // CompileBindings disabled they silently resolved to false), which
        // meant none of the four programmatic Viewbox blocks ever rendered.
        // Keeping them in StyleProps so the bindings refresh when the style
        // changes.
        nameof(IsXboxStyle), nameof(IsPs4Style), nameof(IsPs5Style),
        nameof(IsPs3Style), nameof(IsMinimalStyle),
        // Asset-pack overlay (step 4 of the roadmap). Refreshed alongside
        // the style flags because each style maps to a different image.
        nameof(OverlayImageSource), nameof(HasOverlayImage), nameof(ShowProgrammaticArt),
    ];

    private static readonly string[] LiveProps =
    [
        nameof(SouthGlowOpacity), nameof(EastGlowOpacity),
        nameof(WestGlowOpacity), nameof(NorthGlowOpacity),
        nameof(LeftShoulderOpacity), nameof(RightShoulderOpacity),
        nameof(LeftTriggerButtonOpacity), nameof(RightTriggerButtonOpacity),
        nameof(BackOpacity), nameof(StartOpacity), nameof(GuideOpacity),
        nameof(TouchpadOpacity), nameof(TouchpadLabel),
        nameof(LeftStickClickOpacity), nameof(RightStickClickOpacity),
        nameof(DpadUpOpacity), nameof(DpadDownOpacity),
        nameof(DpadLeftOpacity), nameof(DpadRightOpacity),
        nameof(LeftStickUpOpacity), nameof(LeftStickDownOpacity),
        nameof(LeftStickLeftOpacity), nameof(LeftStickRightOpacity),
        nameof(RightStickUpOpacity), nameof(RightStickDownOpacity),
        nameof(RightStickLeftOpacity), nameof(RightStickRightOpacity),
        nameof(LeftStickOffsetX), nameof(LeftStickOffsetY),
        nameof(RightStickOffsetX), nameof(RightStickOffsetY),
        nameof(LeftTriggerBarHeight), nameof(RightTriggerBarHeight),
        nameof(LeftTriggerPercentText), nameof(RightTriggerPercentText),
        nameof(LeftStickValueText), nameof(RightStickValueText),
        nameof(TriggerSummaryText), nameof(PressedButtonsText),
        nameof(TouchSummaryText), nameof(ShowTouchFinger1), nameof(ShowTouchFinger2),

        // Visual-state Fill/Background/Border brushes consumed by
        // ControllerSurface.axaml. These were referenced by the AXAML in
        // the dev branch without ever existing on the VM — every binding
        // resolved to default (transparent), which is why button presses
        // were invisible on the controller surface. Adding them here fires
        // OnPropertyChanged on each snapshot tick so the visualization
        // tracks button state.
        nameof(SouthFill), nameof(EastFill), nameof(WestFill), nameof(NorthFill),
        nameof(BackFill), nameof(StartFill), nameof(GuideFill), nameof(MuteFill),
        nameof(DpadUpFill), nameof(DpadDownFill), nameof(DpadLeftFill), nameof(DpadRightFill),
        nameof(TouchpadFill),
        nameof(LeftTriggerBackground), nameof(LeftTriggerBorder),
        nameof(RightTriggerBackground), nameof(RightTriggerBorder),
        nameof(LeftBumperBackground), nameof(LeftBumperBorder),
        nameof(RightBumperBackground), nameof(RightBumperBorder),
        nameof(LeftStickBorder), nameof(LeftStickFill), nameof(LeftStickDot),
        nameof(RightStickBorder), nameof(RightStickFill), nameof(RightStickDot),
        nameof(MinimalSummaryText),
    ];

    // ─── State ────────────────────────────────────────────────────────────────

    private readonly Action<string> onElementSelected;
    private readonly ILocalizationService? localization;
    private ControllerSnapshot snapshot = ControllerSnapshot.Empty("No controller detected");
    private ControllerVisualStyle visualStyle = ControllerVisualStyle.Auto;
    private string panelId = "physical";

    /// <summary>
    /// Lazy-loaded asset-pack overlay PNG for <see cref="visualStyle"/>.
    /// Refreshed when the style changes; <see langword="null"/> when no
    /// matching file is installed.
    /// </summary>
    private Bitmap? cachedOverlayImage;

    // Cached computed string values — updated only when the underlying data changes

    // ─── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs the view-model. <paramref name="localization"/> is
    /// optional so existing call sites that don't have access to the
    /// localizer (tests, design-time data) still work — when it's null,
    /// <see cref="StyleLabel"/> falls back to its original English text.
    /// </summary>
    public ControllerVisualStateViewModel(
        Action<string> onElementSelected,
        ILocalizationService? localization = null)
    {
        this.onElementSelected = onElementSelected;
        this.localization = localization;
        SelectElementCommand = new RelayCommand<string>(SelectElement);
    }

    public ICommand SelectElementCommand { get; }

    /// <summary>
    /// Forces a re-read of every label that depends on the localization
    /// service. Called by the hosting <c>ShellViewModel</c> from its
    /// culture-change refresh path so the panel's
    /// <see cref="StyleLabel"/> and <see cref="MinimalSummaryText"/>
    /// pick up the new culture without waiting for a style change or
    /// snapshot tick.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(StyleLabel));
        OnPropertyChanged(nameof(MinimalSummaryText));
    }

    // ─── Update (called every refresh tick from ShellViewModel) ───────────────

    public void Update(
        string panelId,
        string panelTitle,
        ControllerSnapshot newSnapshot,
        ControllerVisualStyle preferredStyle)
    {
        // ─── Live-feedback diagnostic (throttled) ────────────────────
        // Fires before the change-detection short-circuit below so that
        // even idle ticks produce a log line. This helps diagnose
        // "no live feedback" reports: if these entries show buttons=0
        // pressed while the user is actively pressing buttons, the
        // bug is upstream in the input source, not in the VM/bindings.
        if (++diagnosticTickCounter >= DiagnosticEveryNthTick)
        {
            diagnosticTickCounter = 0;
            if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            {
                var pressedCount = newSnapshot.Buttons.Count(kv => kv.Value);
                Serilog.Log.Debug(
                    "VisualState[{Panel}]: device={Device} VID/PID={Vid:X4}/{Pid:X4} style={Style} buttons={Pressed}/{Total} ls=({LX:F2},{LY:F2}) rs=({RX:F2},{RY:F2}) lt={LT:F2} rt={RT:F2}",
                    panelId,
                    newSnapshot.DeviceName,
                    newSnapshot.VendorId,
                    newSnapshot.ProductId,
                    visualStyle,
                    pressedCount,
                    newSnapshot.Buttons.Count,
                    newSnapshot.LeftStick.X,
                    newSnapshot.LeftStick.Y,
                    newSnapshot.RightStick.X,
                    newSnapshot.RightStick.Y,
                    newSnapshot.LeftTrigger,
                    newSnapshot.RightTrigger);
            }
        }

        var newStyle = ResolveVisualStyle(preferredStyle, newSnapshot);

        var identityChanged =
            newSnapshot.DeviceName != snapshot.DeviceName ||
            panelTitle != PanelTitle;

        var styleChanged = newStyle != visualStyle;

        var liveChanged =
            newSnapshot.LeftStick != snapshot.LeftStick ||
            newSnapshot.RightStick != snapshot.RightStick ||
            Math.Abs(newSnapshot.LeftTrigger - snapshot.LeftTrigger) > 0.005f ||
            Math.Abs(newSnapshot.RightTrigger - snapshot.RightTrigger) > 0.005f ||
            newSnapshot.TouchContactCount != snapshot.TouchContactCount ||
            ButtonsChanged(newSnapshot.Buttons, snapshot.Buttons);

        // Nothing changed at all — skip entirely (common when idle)
        if (!identityChanged && !styleChanged && !liveChanged)
        {
            return;
        }

        this.panelId = panelId;
        PanelTitle = panelTitle;
        snapshot = newSnapshot;
        visualStyle = newStyle;

        if (styleChanged)
        {
            // Load the optional asset-pack overlay PNG for the new style.
            // Returns null when the user hasn't installed the assets yet,
            // in which case the AXAML falls back to programmatic art.
            cachedOverlayImage = ControllerOverlayAssetLoader.TryLoad(newStyle);

            // Theme-engine: refresh the available-variant list and the
            // active theme. Defined in the .Theming.cs partial — safe
            // to call even when no ThemeRegistry has been injected yet
            // (it short-circuits to an empty list).
            RefreshActiveTheme();
        }

        if (liveChanged || styleChanged)
        {
            RebuildLiveCachedStrings();
            foreach (var prop in LiveProps)
            {
                OnPropertyChanged(prop);
            }
        }

        if (styleChanged)
        {
            foreach (var prop in StyleProps)
            {
                OnPropertyChanged(prop);
            }
        }

        if (identityChanged)
        {
            foreach (var prop in IdentityProps)
            {
                OnPropertyChanged(prop);
            }
        }
    }

    /// <summary>
    /// Counts ticks since the last diagnostic log emission. Reset to
    /// zero whenever a log line is written.
    /// </summary>
    private int diagnosticTickCounter;

    /// <summary>
    /// How many <see cref="Update"/> calls between diagnostic log
    /// lines. At the default 60 Hz dashboard refresh this works out
    /// to roughly one entry per second per panel — frequent enough
    /// to spot a live issue, sparse enough not to flood the log.
    /// </summary>
    private const int DiagnosticEveryNthTick = 60;

    // ─── Identity properties ──────────────────────────────────────────────────

    public string PanelTitle { get; private set; } = "Controller";

    /// <summary>
    /// Alias for <see cref="PanelTitle"/>. The AXAML binds
    /// <c>{Binding Title}</c> in places where this VM is consumed via a
    /// generic interface; exposing the alias keeps the bindings working
    /// without forcing the consumers to know the property's
    /// "PanelTitle" name.
    /// </summary>
    public string Title => PanelTitle;

    /// <summary>
    /// True for the emitted (virtual) half of a physical/virtual pair.
    /// Drives the panel's interaction affordance: hovering shows an
    /// outline like the physical side, but clicking the VIRTUAL panel
    /// opens that slot's settings editor. Set by whoever builds the
    /// pair; defaults false so a lone physical panel is never clickable
    /// by accident.
    /// </summary>
    public bool IsVirtualPanel
    {
        get => isVirtualPanel;
        set
        {
            if (SetProperty(ref isVirtualPanel, value))
            {
                OnPropertyChanged(nameof(IsEditable));
            }
        }
    }
    private bool isVirtualPanel;

    /// <summary>Slot this panel belongs to, so a click knows which slot to open. Empty for the top-level (non-slot) pipeline.</summary>
    public string SlotId
    {
        get => slotId;
        set
        {
            if (SetProperty(ref slotId, value))
            {
                OnPropertyChanged(nameof(IsEditable));
            }
        }
    }
    private string slotId = string.Empty;

    /// <summary>Only a virtual panel that actually belongs to a slot can be opened for editing.</summary>
    public bool IsEditable => IsVirtualPanel && !string.IsNullOrEmpty(SlotId);

    /// <summary>Raised when the user clicks an editable virtual panel; the shell opens the slot's settings.</summary>
    public event EventHandler<string>? EditRequested;

    /// <summary>Invoked by the view on click. No-op unless <see cref="IsEditable"/>.</summary>
    public void RequestEdit()
    {
        if (IsEditable)
        {
            EditRequested?.Invoke(this, SlotId);
        }
    }

    /// <summary>
    /// Single-line at-a-glance summary used by the minimal (no-shell)
    /// layout, where there's no controller silhouette to look at.
    /// Concatenates the device name with the trigger summary and the
    /// list of currently-pressed buttons so the user still has live
    /// feedback even without a graphical surface.
    /// </summary>
    public string MinimalSummaryText
    {
        get
        {
            var parts = new List<string>(3) { DeviceName };

            if (!string.IsNullOrWhiteSpace(TriggerSummaryText) && TriggerSummaryText != "0% / 0%")
            {
                parts.Add($"L2/R2 {TriggerSummaryText}");
            }

            if (!string.IsNullOrWhiteSpace(PressedButtonsText) && PressedButtonsText != "—")
            {
                parts.Add(PressedButtonsText);
            }

            return string.Join("  ·  ", parts);
        }
    }

    public string DeviceName =>
        string.IsNullOrWhiteSpace(snapshot.DeviceName) ? "No controller detected" : snapshot.DeviceName;

    public bool IsConnected =>
        !DeviceName.Contains("No controller", StringComparison.OrdinalIgnoreCase) &&
        !DeviceName.Contains("No compatible", StringComparison.OrdinalIgnoreCase) &&
        !DeviceName.Contains("Select a controller", StringComparison.OrdinalIgnoreCase) &&
        !DeviceName.Contains("disabled", StringComparison.OrdinalIgnoreCase) &&
        !DeviceName.Contains("Waiting", StringComparison.OrdinalIgnoreCase);

    public string ConnectionLabel => IsConnected ? "LIVE" : "IDLE";
    public string ConnectionBrush => IsConnected ? AccentBrush : "#64748B";
    public string PanelBorderBrush => IsConnected ? AccentBrush : "#334155";

    // ─── Style properties ─────────────────────────────────────────────────────

    public static string PanelBrush => "#09111B";

    public string AccentBrush => visualStyle switch
    {
        ControllerVisualStyle.Xbox            => "#22C55E",
        ControllerVisualStyle.Xbox360         => "#22C55E",
        ControllerVisualStyle.XboxOne         => "#22C55E",
        ControllerVisualStyle.XboxSeries      => "#22C55E",
        ControllerVisualStyle.PlayStation3    => "#0A6BCB",
        ControllerVisualStyle.PlayStation4    => "#00B7FF",
        ControllerVisualStyle.PlayStation5    => "#4F8CFF",
        ControllerVisualStyle.NintendoSwitch  => "#E60012",
        ControllerVisualStyle.SteamController => "#D75500",
        ControllerVisualStyle.SteamDeck       => "#1A9FFF",
        ControllerVisualStyle.Arcade          => "#FF3B30",
        ControllerVisualStyle.SimpleGamepad   => "#9CA3AF",
        _ => "#00E5FF"
    };

    public string AccentSoftBrush => visualStyle switch
    {
        ControllerVisualStyle.Xbox            => "#123722",
        ControllerVisualStyle.Xbox360         => "#123722",
        ControllerVisualStyle.XboxOne         => "#123722",
        ControllerVisualStyle.XboxSeries      => "#123722",
        ControllerVisualStyle.PlayStation3    => "#0A1E36",
        ControllerVisualStyle.PlayStation4    => "#0E2C46",
        ControllerVisualStyle.PlayStation5    => "#142742",
        ControllerVisualStyle.NintendoSwitch  => "#3B0708",
        ControllerVisualStyle.SteamController => "#3B1C00",
        ControllerVisualStyle.SteamDeck       => "#0B2740",
        ControllerVisualStyle.Arcade          => "#3B0F0C",
        ControllerVisualStyle.SimpleGamepad   => "#1F2937",
        _ => "#0F2530"
    };

    public string ShellBrush => visualStyle switch
    {
        ControllerVisualStyle.Xbox            => "#101B17",
        ControllerVisualStyle.Xbox360         => "#101B17",
        ControllerVisualStyle.XboxOne         => "#101B17",
        ControllerVisualStyle.XboxSeries      => "#101B17",
        ControllerVisualStyle.PlayStation3    => "#0C1422",
        ControllerVisualStyle.PlayStation4    => "#101A2B",
        ControllerVisualStyle.PlayStation5    => "#0F172A",
        ControllerVisualStyle.NintendoSwitch  => "#1A0E10",
        ControllerVisualStyle.SteamController => "#1A1208",
        ControllerVisualStyle.SteamDeck       => "#0D1A28",
        ControllerVisualStyle.Arcade          => "#1A0F0E",
        ControllerVisualStyle.SimpleGamepad   => "#0F1620",
        _ => "#0D1522"
    };

    public string ShellHighlightBrush => visualStyle switch
    {
        ControllerVisualStyle.Xbox            => "#16241E",
        ControllerVisualStyle.Xbox360         => "#16241E",
        ControllerVisualStyle.XboxOne         => "#16241E",
        ControllerVisualStyle.XboxSeries      => "#16241E",
        ControllerVisualStyle.PlayStation3    => "#0F1B30",
        ControllerVisualStyle.PlayStation4    => "#14253D",
        ControllerVisualStyle.PlayStation5    => "#152132",
        ControllerVisualStyle.NintendoSwitch  => "#22171A",
        ControllerVisualStyle.SteamController => "#221A10",
        ControllerVisualStyle.SteamDeck       => "#13243A",
        ControllerVisualStyle.Arcade          => "#221513",
        ControllerVisualStyle.SimpleGamepad   => "#161E2A",
        _ => "#131D2B"
    };

    public string StyleLabel => visualStyle switch
    {
        ControllerVisualStyle.Xbox            => Localized("StyleLabelXbox",            "Xbox layout"),
        ControllerVisualStyle.Xbox360         => Localized("StyleLabelXbox360",         "Xbox 360 layout"),
        ControllerVisualStyle.XboxOne         => Localized("StyleLabelXboxOne",         "Xbox One layout"),
        ControllerVisualStyle.XboxSeries      => Localized("StyleLabelXboxSeries",      "Xbox Series layout"),
        ControllerVisualStyle.PlayStation3    => Localized("StyleLabelPlayStation3",    "DualShock 3 layout"),
        ControllerVisualStyle.PlayStation4    => Localized("StyleLabelPlayStation4",    "DualShock 4 layout"),
        ControllerVisualStyle.PlayStation5    => Localized("StyleLabelPlayStation5",    "DualSense layout"),
        ControllerVisualStyle.NintendoSwitch  => Localized("StyleLabelNintendoSwitch",  "Switch layout"),
        ControllerVisualStyle.SteamController => Localized("StyleLabelSteamController", "Steam Controller layout"),
        ControllerVisualStyle.SteamDeck       => Localized("StyleLabelSteamDeck",       "Steam Deck layout"),
        ControllerVisualStyle.Arcade          => Localized("StyleLabelArcade",          "Arcade layout"),
        ControllerVisualStyle.SimpleGamepad   => Localized("StyleLabelSimpleGamepad",   "Simple gamepad layout"),
        ControllerVisualStyle.None            => Localized("StyleLabelMinimal",         "Minimal shell"),
        _                                     => Localized("StyleLabelAuto",            "Auto layout"),
    };

    /// <summary>
    /// Internal helper: looks up <paramref name="key"/> via the optional
    /// localization service and falls back to <paramref name="fallback"/>
    /// if the service is null OR the key is missing (the localizer
    /// returns the key string itself for unknown keys, so we detect that
    /// case and use the supplied default).
    /// </summary>
    private string Localized(string key, string fallback)
    {
        if (localization is null) { return fallback; }
        var hit = localization[key];
        return string.IsNullOrEmpty(hit) || string.Equals(hit, key, StringComparison.Ordinal)
            ? fallback
            : hit;
    }

    public bool ShowShell => visualStyle != ControllerVisualStyle.None;
    public bool ShowTouchpad => visualStyle is ControllerVisualStyle.PlayStation4 or ControllerVisualStyle.PlayStation5;

    // ─── Style-flag bindings (consumed by ControllerSurface.axaml) ────────────
    //
    // These previously lived in AXAML alone with CompileBindings disabled,
    // which meant they always evaluated to false at runtime — none of the
    // four programmatic Viewbox blocks ever rendered. Adding them here
    // restores the per-style art and gives the new asset-pack image
    // overlay something concrete to bind its IsVisible to.

    /// <summary>True when the resolved style is Xbox-family.</summary>
    public bool IsXboxStyle => visualStyle == ControllerVisualStyle.Xbox;
    public bool IsPs3Style => visualStyle == ControllerVisualStyle.PlayStation3;

    /// <summary>True when the resolved style is DualShock 4.</summary>
    public bool IsPs4Style => visualStyle == ControllerVisualStyle.PlayStation4;

    /// <summary>True when the resolved style is DualSense.</summary>
    public bool IsPs5Style => visualStyle == ControllerVisualStyle.PlayStation5;

    /// <summary>True when the resolved style is the minimal/no-shell layout.</summary>
    public bool IsMinimalStyle => visualStyle == ControllerVisualStyle.None;

    // ─── Asset-pack overlay (step 4 of the roadmap) ───────────────────────────

    /// <summary>
    /// Optional bitmap painted as the base layer of the controller
    /// surface, sourced from the AL2009man Gamepad-Asset-Pack. Set by
    /// <see cref="Update"/> via <see cref="ControllerOverlayAssetLoader"/>.
    /// <see langword="null"/> means no overlay is installed for the
    /// current style and the surface falls back to programmatic XAML art.
    /// </summary>
    public Bitmap? OverlayImageSource => cachedOverlayImage;

    /// <summary>
    /// True when an overlay image is loaded for the current style.
    /// The XAML <c>&lt;Image&gt;</c> layer binds <c>IsVisible</c> to this.
    /// </summary>
    public bool HasOverlayImage => cachedOverlayImage is not null;

    /// <summary>
    /// True when no overlay image is loaded — the existing programmatic
    /// XAML art (Path silhouettes etc.) should render instead. Defined
    /// as a positive form because Avalonia's binding pipeline does not
    /// natively support a "negate" expression on raw bool bindings.
    /// </summary>
    public bool ShowProgrammaticArt => cachedOverlayImage is null;

    private bool IsPlayStation => visualStyle is ControllerVisualStyle.PlayStation4 or ControllerVisualStyle.PlayStation5;

    public string SouthLegend => IsPlayStation ? "×" : "A";
    public string EastLegend => IsPlayStation ? "○" : "B";
    public string WestLegend => IsPlayStation ? "□" : "X";
    public string NorthLegend => IsPlayStation ? "△" : "Y";

    public string SouthFaceBrush => IsPlayStation ? "#4DAAFF" : "#22C55E";
    public string EastFaceBrush => IsPlayStation ? "#F97373" : "#EF4444";
    public string WestFaceBrush => IsPlayStation ? "#C084FC" : "#3B82F6";
    public string NorthFaceBrush => IsPlayStation ? "#93C5FD" : "#EAB308";

    public string LeftShoulderLabel => IsPlayStation ? "L1" : "LB";
    public string RightShoulderLabel => IsPlayStation ? "R1" : "RB";
    public string LeftTriggerLabel => IsPlayStation ? "L2" : "LT";
    public string RightTriggerLabel => IsPlayStation ? "R2" : "RT";
    public string BackLabel => visualStyle == ControllerVisualStyle.PlayStation5 ? "Create"
                              : IsPlayStation ? "Share" : "View";
    public string StartLabel => IsPlayStation ? "Options" : "Menu";
    public string GuideLabel => IsPlayStation ? "PS" : "Guide";

    // ─── Live properties — opacities ──────────────────────────────────────────

    public double SouthGlowOpacity => Active(snapshot.IsPressed(ButtonId.South));
    public double EastGlowOpacity => Active(snapshot.IsPressed(ButtonId.East));
    public double WestGlowOpacity => Active(snapshot.IsPressed(ButtonId.West));
    public double NorthGlowOpacity => Active(snapshot.IsPressed(ButtonId.North));
    public double LeftShoulderOpacity => Active(snapshot.IsPressed(ButtonId.LeftShoulder));
    public double RightShoulderOpacity => Active(snapshot.IsPressed(ButtonId.RightShoulder));
    public double LeftTriggerButtonOpacity => Active(snapshot.IsPressed(ButtonId.LeftTriggerButton));
    public double RightTriggerButtonOpacity => Active(snapshot.IsPressed(ButtonId.RightTriggerButton));
    public double BackOpacity => Active(snapshot.IsPressed(ButtonId.Back));
    public double StartOpacity => Active(snapshot.IsPressed(ButtonId.Start));
    public double GuideOpacity => Active(snapshot.IsPressed(ButtonId.Guide));
    public double TouchpadOpacity => Active(snapshot.TouchContactCount > 0 || snapshot.IsPressed(ButtonId.Touchpad));
    public double LeftStickClickOpacity => Active(snapshot.IsPressed(ButtonId.LeftStick));
    public double RightStickClickOpacity => Active(snapshot.IsPressed(ButtonId.RightStick));
    public double DpadUpOpacity => Active(snapshot.IsPressed(ButtonId.DpadUp));
    public double DpadDownOpacity => Active(snapshot.IsPressed(ButtonId.DpadDown));
    public double DpadLeftOpacity => Active(snapshot.IsPressed(ButtonId.DpadLeft));
    public double DpadRightOpacity => Active(snapshot.IsPressed(ButtonId.DpadRight));

    public double LeftStickUpOpacity => Active(snapshot.LeftStick.Y > 0.25f);
    public double LeftStickDownOpacity => Active(snapshot.LeftStick.Y < -0.25f);
    public double LeftStickLeftOpacity => Active(snapshot.LeftStick.X < -0.25f);
    public double LeftStickRightOpacity => Active(snapshot.LeftStick.X > 0.25f);
    public double RightStickUpOpacity => Active(snapshot.RightStick.Y > 0.25f);
    public double RightStickDownOpacity => Active(snapshot.RightStick.Y < -0.25f);
    public double RightStickLeftOpacity => Active(snapshot.RightStick.X < -0.25f);
    public double RightStickRightOpacity => Active(snapshot.RightStick.X > 0.25f);

    // ─── Live properties — Fill / Background / Border brushes ─────────────────
    //
    // All return strings (hex colours) so Avalonia's brush type-converter
    // can fold them into SolidColorBrush at binding time, matching the
    // pattern already established by AccentBrush / ShellHighlightBrush.
    //
    // Resting colours come from the cyan-on-deep-blue palette set by the
    // surrounding shell theme; pressed/active colours pull from the per-
    // style accent brushes so e.g. an Xbox A button glows green and a PS
    // X button glows blue.

    /// <summary>Resting colour for any unlit chrome element (face button, dpad arrow, etc.).</summary>
    private const string ChromeRestBrush = "#1E2A38";

    /// <summary>Background colour for an inactive trigger / bumper rectangle.</summary>
    private const string ChromeBackgroundBrush = "#0F1A28";

    /// <summary>Fill colour for the stick well — the dark circle the position dot moves inside.</summary>
    private const string StickWellBrush = "#0A1320";

    // Face buttons. Lit with the per-style face brush (Xbox = green/red/blue/yellow,
    // PlayStation = blue/red/purple/light-blue) when pressed; sit at chrome rest
    // colour otherwise.
    public string SouthFill => snapshot.IsPressed(ButtonId.South) ? SouthFaceBrush : ChromeRestBrush;
    public string EastFill  => snapshot.IsPressed(ButtonId.East)  ? EastFaceBrush  : ChromeRestBrush;
    public string WestFill  => snapshot.IsPressed(ButtonId.West)  ? WestFaceBrush  : ChromeRestBrush;
    public string NorthFill => snapshot.IsPressed(ButtonId.North) ? NorthFaceBrush : ChromeRestBrush;

    // Menu buttons. Use the accent brush since they're not colour-coded
    // per the controller's own palette.
    public string BackFill  => snapshot.IsPressed(ButtonId.Back)  ? AccentBrush : ChromeRestBrush;
    public string StartFill => snapshot.IsPressed(ButtonId.Start) ? AccentBrush : ChromeRestBrush;
    public string GuideFill => snapshot.IsPressed(ButtonId.Guide) ? AccentBrush : ChromeRestBrush;

    /// <summary>
    /// Fill for the DualSense mute button. <see cref="ButtonId"/> has no
    /// dedicated <c>Mute</c> entry, so this stays at chrome rest until /
    /// unless the snapshot model adds one.
    /// </summary>
    public static string MuteFill => ChromeRestBrush;

    // Dpad arrows.
    public string DpadUpFill    => snapshot.IsPressed(ButtonId.DpadUp)    ? AccentBrush : ChromeRestBrush;
    public string DpadDownFill  => snapshot.IsPressed(ButtonId.DpadDown)  ? AccentBrush : ChromeRestBrush;
    public string DpadLeftFill  => snapshot.IsPressed(ButtonId.DpadLeft)  ? AccentBrush : ChromeRestBrush;
    public string DpadRightFill => snapshot.IsPressed(ButtonId.DpadRight) ? AccentBrush : ChromeRestBrush;

    // Touchpad. Lit when any contact point is reported by the snapshot or
    // when the touchpad button itself is clicked.
    public string TouchpadFill =>
        (snapshot.TouchContactCount > 0 || snapshot.IsPressed(ButtonId.Touchpad))
            ? AccentSoftBrush
            : "#101824";

    // Triggers. Background ramps to the accent-soft palette as the trigger
    // pulls in; border switches to the bright accent once any pull is
    // detected, which gives a clear "is this trigger active at all" cue.
    public string LeftTriggerBackground  => snapshot.LeftTrigger  > 0.05f ? AccentSoftBrush : ChromeBackgroundBrush;
    public string LeftTriggerBorder      => snapshot.LeftTrigger  > 0.05f ? AccentBrush     : ChromeRestBrush;
    public string RightTriggerBackground => snapshot.RightTrigger > 0.05f ? AccentSoftBrush : ChromeBackgroundBrush;
    public string RightTriggerBorder     => snapshot.RightTrigger > 0.05f ? AccentBrush     : ChromeRestBrush;

    // Bumpers. Binary press state.
    public string LeftBumperBackground  => snapshot.IsPressed(ButtonId.LeftShoulder)  ? AccentSoftBrush : ChromeBackgroundBrush;
    public string LeftBumperBorder      => snapshot.IsPressed(ButtonId.LeftShoulder)  ? AccentBrush     : ChromeRestBrush;
    public string RightBumperBackground => snapshot.IsPressed(ButtonId.RightShoulder) ? AccentSoftBrush : ChromeBackgroundBrush;
    public string RightBumperBorder     => snapshot.IsPressed(ButtonId.RightShoulder) ? AccentBrush     : ChromeRestBrush;

    // Sticks. The well stays a solid dark colour at all times; the border
    // lights up on click (L3 / R3); the dot is the always-visible position
    // indicator and uses the accent so it stands out against the well.
    public string LeftStickBorder  => snapshot.IsPressed(ButtonId.LeftStick)  ? AccentBrush : ChromeRestBrush;
    public static string LeftStickFill    => StickWellBrush;
    public string LeftStickDot     => AccentBrush;
    public string RightStickBorder => snapshot.IsPressed(ButtonId.RightStick) ? AccentBrush : ChromeRestBrush;
    public static string RightStickFill   => StickWellBrush;
    public string RightStickDot    => AccentBrush;

    // ─── Live properties — analogue values ────────────────────────────────────

    public double LeftStickOffsetX => Math.Round(snapshot.LeftStick.X * 16d, 1);
    public double LeftStickOffsetY => Math.Round(-snapshot.LeftStick.Y * 16d, 1);
    public double RightStickOffsetX => Math.Round(snapshot.RightStick.X * 16d, 1);
    public double RightStickOffsetY => Math.Round(-snapshot.RightStick.Y * 16d, 1);

    public double LeftTriggerBarHeight => Math.Max(2d, Math.Round(Math.Clamp(snapshot.LeftTrigger, 0f, 1f) * 76d, 0));
    public double RightTriggerBarHeight => Math.Max(2d, Math.Round(Math.Clamp(snapshot.RightTrigger, 0f, 1f) * 76d, 0));

    // ─── Live properties — cached strings (avoid per-tick allocations) ────────

    public string LeftTriggerPercentText { get; private set; } = "0%";
    public string RightTriggerPercentText { get; private set; } = "0%";
    public string LeftStickValueText { get; private set; } = "+0.00, +0.00";
    public string RightStickValueText { get; private set; } = "+0.00, +0.00";
    public string TriggerSummaryText { get; private set; } = "0% / 0%";
    public string PressedButtonsText { get; private set; } = "—";
    public string TouchSummaryText { get; private set; } = "No touch";

    public string TouchpadLabel => snapshot.TouchContactCount switch
    {
        <= 0 => IsPlayStation ? "Touchpad" : "Surface",
        1 => "1 finger",
        _ => $"{snapshot.TouchContactCount} fingers"
    };

    public bool ShowTouchFinger1 => ShowTouchpad && snapshot.TouchContactCount >= 1;
    public bool ShowTouchFinger2 => ShowTouchpad && snapshot.TouchContactCount >= 2;

    // ─── Private helpers ──────────────────────────────────────────────────────

    private void RebuildLiveCachedStrings()
    {
        var lt = Math.Round(Math.Clamp(snapshot.LeftTrigger, 0f, 1f) * 100d, 0);
        var rt = Math.Round(Math.Clamp(snapshot.RightTrigger, 0f, 1f) * 100d, 0);
        LeftTriggerPercentText = $"{lt:0}%";
        RightTriggerPercentText = $"{rt:0}%";
        TriggerSummaryText = $"{lt:0}% / {rt:0}%";
        LeftStickValueText = $"{snapshot.LeftStick.X:+0.00;-0.00;+0.00}, {snapshot.LeftStick.Y:+0.00;-0.00;+0.00}";
        RightStickValueText = $"{snapshot.RightStick.X:+0.00;-0.00;+0.00}, {snapshot.RightStick.Y:+0.00;-0.00;+0.00}";

        TouchSummaryText = snapshot.TouchContactCount switch
        {
            <= 0 => "No touch",
            1 => "1 touch",
            _ => $"{snapshot.TouchContactCount} touches"
        };

        var pressed = snapshot.Buttons
            .Where(pair => pair.Value)
            .Select(pair => FormatButton(pair.Key))
            .Take(6)
            .ToArray();
        PressedButtonsText = pressed.Length == 0 ? "—" : string.Join(" · ", pressed);
    }

    private static bool ButtonsChanged(
        IReadOnlyDictionary<ButtonId, bool> a,
        IReadOnlyDictionary<ButtonId, bool> b)
    {
        foreach (var key in a.Keys)
        {
            var aVal = a.TryGetValue(key, out var av) && av;
            var bVal = b.TryGetValue(key, out var bv) && bv;
            if (aVal != bVal)
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void SelectElement(string? parameter)
    {
        if (!string.IsNullOrWhiteSpace(parameter))
        {
            onElementSelected($"{panelId}:{parameter}");
        }
    }

    /// <summary>
    /// Picks the visual style for a snapshot. When
    /// <paramref name="preferred"/> is anything other than
    /// <see cref="ControllerVisualStyle.Auto"/> it wins (the user
    /// chose explicitly). In Auto mode we resolve in two steps:
    /// <list type="number">
    /// <item><b>VID/PID lookup</b> via
    /// <see cref="ControllerHardwareCatalog"/> — deterministic,
    /// language-independent, works for any device the catalog
    /// knows about.</item>
    /// <item><b>Device-name heuristic</b> — substring match on the
    /// human-readable name. Only reached when VID/PID is 0 (no
    /// hardware id reported, e.g. SDL3 with a generic mapping) or
    /// the catalog has no entry for the device.</item>
    /// </list>
    /// </summary>
    private static ControllerVisualStyle ResolveVisualStyle(
        ControllerVisualStyle preferred,
        ControllerSnapshot snapshot)
    {
        if (preferred != ControllerVisualStyle.Auto)
        {
            return preferred;
        }

        // Step 1: hardware-id lookup
        var byHardware = GameFlow.Core.Models.ControllerHardwareCatalog.Resolve(snapshot.VendorId, snapshot.ProductId);
        if (byHardware != ControllerVisualStyle.Auto)
        {
            return byHardware;
        }

        // Step 2: name heuristic — kept for backwards compatibility
        // with sources that don't surface VID/PID. Keyboard/mouse first:
        // Raw Input device names carry the word, and those panels should
        // render their own layout art, never a gamepad silhouette.
        var deviceName = snapshot.DeviceName ?? string.Empty;
        // Windows localizes device names — cover the common spellings so
        // e.g. a Czech "Klávesnice" still renders the keyboard layout.
        if (ContainsAny(deviceName, "keyboard", "klávesnice", "klavesnice", "tastatur", "clavier", "teclado", "tastiera"))
        {
            return ControllerVisualStyle.Keyboard;
        }
        if (ContainsAny(deviceName, "mouse", "myš", "mys ", "maus", "souris", "ratón", "raton", "マウス"))
        {
            return ControllerVisualStyle.Mouse;
        }
        return deviceName.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("ps5", StringComparison.OrdinalIgnoreCase)
            ? ControllerVisualStyle.PlayStation5
            : deviceName.Contains("dualshock", StringComparison.OrdinalIgnoreCase) ||
              deviceName.Contains("wireless controller", StringComparison.OrdinalIgnoreCase) ||
              deviceName.Contains("ps4", StringComparison.OrdinalIgnoreCase)
            ? ControllerVisualStyle.PlayStation4
            : deviceName.Contains("xbox", StringComparison.OrdinalIgnoreCase)
            ? ControllerVisualStyle.Xbox
            : ControllerVisualStyle.Auto;
    }

    private static string FormatButton(ButtonId b)
    {
        return b switch
        {
            ButtonId.South => "South",
            ButtonId.East => "East",
            ButtonId.West => "West",
            ButtonId.North => "North",
            ButtonId.LeftShoulder => "L1/LB",
            ButtonId.RightShoulder => "R1/RB",
            ButtonId.LeftTriggerButton => "L2/LT",
            ButtonId.RightTriggerButton => "R2/RT",
            ButtonId.Back => "Back",
            ButtonId.Start => "Start",
            ButtonId.Guide => "Guide",
            ButtonId.LeftStick => "L3",
            ButtonId.RightStick => "R3",
            ButtonId.DpadUp => "D↑",
            ButtonId.DpadDown => "D↓",
            ButtonId.DpadLeft => "D←",
            ButtonId.DpadRight => "D→",
            ButtonId.Touchpad => "Touch",
            _ => b.ToString()
        };
    }

    private static double Active(bool isActive)
    {
        return isActive ? 1.0d : 0.20d;
    }
}
