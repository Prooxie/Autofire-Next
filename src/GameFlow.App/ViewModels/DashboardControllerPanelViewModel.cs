namespace GameFlow.App.ViewModels;

/// <summary>
/// One live controller panel on the dashboard, backed by a slot. Wraps a
/// physical/virtual pair of <see cref="ControllerVisualStateViewModel"/>
/// instances the shell pump feeds the slot's latest snapshots into every
/// tick — a genuine side-by-side comparison per slot, at the same size
/// and style as the top-level pair, rather than a small virtual-only card.
/// </summary>
public sealed class DashboardControllerPanelViewModel : ViewModelBase
{
    public DashboardControllerPanelViewModel(
        string slotId, string title,
        ControllerVisualStateViewModel physicalVisual,
        ControllerVisualStateViewModel virtualVisual,
        string virtualBadgeLabel)
    {
        SlotId = slotId;
        this.title = title;
        PhysicalVisual = physicalVisual;
        VirtualVisual = virtualVisual;
        VirtualBadgeLabel = virtualBadgeLabel;
    }

    public string SlotId { get; }

    private string title;
    public string Title { get => title; set => SetProperty(ref title, value); }

    private string lightColor = "#00000000";
    /// <summary>Hex colour of the slot's lightbar (#AARRGGBB), transparent when off.</summary>
    public string LightColor
    {
        get => lightColor;
        set
        {
            if (SetProperty(ref lightColor, value))
            {
                VirtualVisual.LightColor = value;
                OnPropertyChanged(nameof(HasLightColor));
            }
        }
    }

    /// <summary>
    /// True only when the lightbar actually shows a colour. Drives the
    /// chip's visibility in the dashboard header row — when lighting is
    /// off, the chip's fill is fully transparent but its 1px border
    /// still rendered, leaving a small empty outlined box floating at
    /// the top-right of every controller panel. Hidden entirely instead.
    /// </summary>
    public bool HasLightColor =>
        !string.IsNullOrWhiteSpace(lightColor)
        && !lightColor.StartsWith("#00", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(lightColor, "Transparent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a pinned LAYOUT-ONLY panel: a physical device the user
    /// pinned from the sidebar to inspect its layout and connections —
    /// no slot, no virtual half. The panel's SlotId carries the pinned
    /// device id with a "phys:" prefix.
    /// </summary>
    public bool IsPhysicalOnly { get; init; }

    /// <summary>Virtual half + badge visibility — hidden for layout-only panels.</summary>
    public bool ShowVirtual => !IsPhysicalOnly;

    /// <summary>
    /// Column span for the physical side's cell in the (physical, virtual)
    /// two-column row. 2 when there's no virtual half to show next to it
    /// (claims the full row instead of leaving the other half visibly
    /// empty — a *,* Grid doesn't collapse a hidden column's width on
    /// its own), 1 otherwise.
    /// </summary>
    public int PhysicalColumnSpan => IsPhysicalOnly ? 2 : 1;

    private bool isDemoPreview;
    /// <summary>
    /// True while the slot runs on the demo waveform. A demo slot has no
    /// meaningful physical side (there IS no physical device — the
    /// waveform enters the pipeline directly), so the panel hides the
    /// physical half entirely and the VIRTUAL surface takes the full
    /// row: demo is a preview of the OUTPUT, shown big.
    /// </summary>
    public bool IsDemoPreview
    {
        get => isDemoPreview;
        set
        {
            if (SetProperty(ref isDemoPreview, value))
            {
                OnPropertyChanged(nameof(ShowPhysical));
                OnPropertyChanged(nameof(VirtualColumn));
                OnPropertyChanged(nameof(VirtualColumnSpan));
                OnPropertyChanged(nameof(PhysicalColumnWidth));
            }
        }
    }

    /// <summary>Physical half visibility — hidden while the demo waveform drives the slot.</summary>
    public bool ShowPhysical => !isDemoPreview;

    /// <summary>Grid column for the virtual surface: 0 (full row start) in demo mode, 1 otherwise.</summary>
    public int VirtualColumn => isDemoPreview ? 0 : 1;

    /// <summary>Column span for the virtual surface: the full row in demo mode.</summary>
    public int VirtualColumnSpan => isDemoPreview ? 2 : 1;

    /// <summary>
    /// Width of the physical column: collapses to zero while the demo
    /// waveform drives the slot, letting the virtual surface take the
    /// whole row. Bound column WIDTHS instead of Grid.Column/ColumnSpan
    /// attached-property bindings, which proved unreliable inside the
    /// panel item template — a zero-width star column is unambiguous.
    /// </summary>
    public Avalonia.Controls.GridLength PhysicalColumnWidth =>
        isDemoPreview ? new Avalonia.Controls.GridLength(0)
                      : new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);

    private string outputStatus = string.Empty;

    /// <summary>
    /// The slot sink's status line (its DisplayName). Shown on the panel
    /// only when it signals a problem — see <see cref="HasOutputWarning"/> —
    /// so "why is there no output" (not elevated, driver missing,
    /// creation failed) is answered on screen, not just in the log.
    /// </summary>
    public string OutputStatus
    {
        get => outputStatus;
        set
        {
            if (SetProperty(ref outputStatus, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasOutputWarning));
                OnPropertyChanged(nameof(OutputWarningText));
            }
        }
    }

    /// <summary>True when the status text signals a non-working output.</summary>
    public bool HasOutputWarning =>
        outputStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
        || outputStatus.Contains("no output", StringComparison.OrdinalIgnoreCase)
        || outputStatus.Contains("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Concise dashboard copy for a non-working output. The complete sink
    /// diagnostic remains available through <see cref="OutputStatus"/> as a
    /// tooltip, but paths and setup details should never cover controller art.
    /// </summary>
    public string OutputWarningText
    {
        get
        {
            if (outputStatus.Contains("not found", StringComparison.OrdinalIgnoreCase)
                && outputStatus.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase))
            {
                return "HIDMaestro isn't installed, so virtual output is unavailable.";
            }

            if (outputStatus.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            {
                return "Administrator access is required for virtual output.";
            }

            if (outputStatus.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Virtual output failed to start.";
            }

            return "Virtual output is unavailable.";
        }
    }

    /// <summary>Width of the virtual column: collapses for pinned layout-only panels.</summary>
    public Avalonia.Controls.GridLength VirtualColumnWidth =>
        IsPhysicalOnly ? new Avalonia.Controls.GridLength(0)
                       : new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);

    private GameFlow.Core.Enums.ControllerVisualStyle virtualStyle = GameFlow.Core.Enums.ControllerVisualStyle.Auto;
    /// <summary>
    /// The style this slot's virtual panel renders — resolved by the
    /// shell from the slot's output template (its explicit HIDMaestro
    /// catalog profile when set, its kind otherwise), so each panel's
    /// theme follows that slot's selected output controller.
    /// </summary>
    public GameFlow.Core.Enums.ControllerVisualStyle VirtualStyle { get => virtualStyle; set => SetProperty(ref virtualStyle, value); }

    /// <summary>This slot's physical (input) side of the comparison.</summary>
    public ControllerVisualStateViewModel PhysicalVisual { get; }

    /// <summary>This slot's virtual (output) side of the comparison.</summary>
    public ControllerVisualStateViewModel VirtualVisual { get; }

    /// <summary>
    /// "VIRTUAL" (localized), set once from the shell's current language
    /// when this panel is created. Marks the emitted device clearly —
    /// it's real hardware to every other application on the system, just
    /// not to this one (see the hardware-signature filtering that hides
    /// it from GameFlow's own input list).
    /// </summary>
    public string VirtualBadgeLabel { get; }
}
