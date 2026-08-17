using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace GameFlow.App.ViewModels;

/// <summary>
/// View-model for the Options / Settings dialog (Step 5 of the roadmap).
///
/// <para>
/// Exposes every user-editable setting persisted on
/// <see cref="AppSettings"/> as an observable property, plus the
/// <see cref="ApplyAsync"/> / <see cref="RestoreDefaults"/> /
/// <see cref="Reload"/> actions that write back to
/// <see cref="IUserSettingsService"/> and refresh the dialog from the
/// current persisted state.
/// </para>
///
/// <para>
/// Settings outside the dialog's purview (currently:
/// <see cref="AppSettings.ActiveProfileId"/>,
/// <see cref="AppSettings.SelectedCulture"/>, and
/// <see cref="AppSettings.SkippedUpdateVersion"/>) are preserved through
/// the apply round-trip so toggling something here doesn't accidentally
/// reset the active profile.
/// </para>
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly IUserSettingsService userSettings;
    private readonly ILocalizationService localization;
    private readonly ILogger<SettingsDialogViewModel> logger;

    /// <summary>
    /// Optional back-reference to the <see cref="ShellViewModel"/> so the
    /// Settings dialog can reuse the shell's existing Language + Theme
    /// bindings (no duplication of apply/persist logic). Set by
    /// <c>OpenSettingsAsync</c> just before <c>ShowDialog</c>.
    /// </summary>
    public ShellViewModel? Shell { get; set; }

    /// <summary>Log levels offered by the dialog dropdown, in order.</summary>
    public IReadOnlyList<LogLevel> AvailableLogLevels { get; } =
    [
        LogLevel.Trace,
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
        LogLevel.Critical,
    ];

    [ObservableProperty]
    private LogLevel selectedLogLevel = LogLevel.Information;

    [ObservableProperty]
    private bool rememberWindowSize = true;

    [ObservableProperty]
    private double windowWidth = 1280;

    [ObservableProperty]
    private double windowHeight = 800;

    [ObservableProperty]
    private string dashboardRefreshHzText = string.Empty;

    [ObservableProperty]
    private string pollingRateHzText = string.Empty;

    [ObservableProperty]
    private string profilesDirectoryOverride = string.Empty;

    [ObservableProperty]
    private string logsDirectoryOverride = string.Empty;

    [ObservableProperty]
    private bool checkForUpdatesOnStartup = true;

    [ObservableProperty]
    private bool checkRequirementsOnStartup = true;

    /// <summary>Status line shown at the bottom of the dialog after Apply.</summary>
    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>
    /// DSU / Cemuhook motion server controls. The shared singleton, so the
    /// live status line here is the same one the runtime is updating — and
    /// so the toggle is a single global switch. The server reads whichever
    /// slots are enabled; it is not a per-controller setting, which is why
    /// it lives in Settings rather than on a virtual controller.
    ///
    /// <para>
    /// This panel owns its own persistence (it writes on change rather
    /// than on Apply), so it is deliberately NOT part of
    /// <see cref="ApplyAsync"/> / <see cref="RestoreDefaults"/>.
    /// </para>
    /// </summary>
    public MotionServerPanelViewModel MotionServer { get; }

    /// <summary>
    /// The stream-overlay section: builds the URL for an OBS browser
    /// source. Like <see cref="MotionServer"/> it is outside
    /// <see cref="ApplyAsync"/> / <see cref="RestoreDefaults"/> — it
    /// persists nothing at all, so there is neither anything to apply nor
    /// anything to restore.
    /// </summary>
    public OverlayPanelViewModel Overlay { get; }

    // ─── Localised labels ─────────────────────────────────────────────────────

    /// <summary>Window title.</summary>
    public string TitleLabel => localization["SettingsDialogTitle"];
    public string DiagnosticsHeaderLabel => localization["SettingsDialogDiagnosticsHeader"];
    public string LogLevelLabel => localization["SettingsDialogLogLevelLabel"];
    public string LogLevelHintLabel => localization["SettingsDialogLogLevelHint"];
    public string WindowHeaderLabel => localization["SettingsDialogWindowHeader"];
    public string RememberWindowSizeLabel => localization["SettingsDialogRememberWindowSizeLabel"];
    public string WindowWidthLabel => localization["SettingsDialogWindowWidthLabel"];
    public string WindowHeightLabel => localization["SettingsDialogWindowHeightLabel"];
    public string PollingHeaderLabel => localization["SettingsDialogPollingHeader"];
    public string DashboardRefreshLabel => localization["SettingsDialogDashboardRefreshLabel"];
    public string PollingRateLabel => localization["SettingsDialogPollingRateLabel"];
    public string PollingHintLabel => localization["SettingsDialogPollingHint"];
    public string PathsHeaderLabel => localization["SettingsDialogPathsHeader"];
    public string ProfilesDirectoryLabel => localization["SettingsDialogProfilesDirectoryLabel"];
    public string LogsDirectoryLabel => localization["SettingsDialogLogsDirectoryLabel"];
    public string LogsPathHintLabel => localization["SettingsDialogLogsPathHint"];
    public string BrowseButtonLabel => localization["SettingsDialogBrowseButton"];
    public string StartupHeaderLabel => localization["SettingsDialogStartupHeader"];
    public string CheckUpdatesLabel => localization["SettingsDialogCheckUpdatesLabel"];
    public string CheckRequirementsLabel => localization["SettingsDialogCheckRequirementsLabel"];
    public string ApplyButtonLabel => localization["SettingsDialogApplyButton"];
    public string CancelButtonLabel => localization["SettingsDialogCancelButton"];
    public string RestoreDefaultsButtonLabel => localization["SettingsDialogRestoreDefaultsButton"];

    /// <summary>Path the dialog should show as the in-effect profiles dir.</summary>
    public string CurrentProfilesPath => AppPaths.ProfilesDirectory;

    /// <summary>Path the dialog should show as the in-effect logs dir.</summary>
    public string CurrentLogsPath => AppPaths.LogsDirectory;

    /// <summary>
    /// Constructs the view-model. Pre-populates every observable
    /// property from <see cref="IUserSettingsService.Current"/>.
    /// </summary>
    public SettingsDialogViewModel(
        IUserSettingsService userSettings,
        ILocalizationService localization,
        MotionServerPanelViewModel motionServer,
        OverlayPanelViewModel overlay,
        ILogger<SettingsDialogViewModel> logger)
    {
        this.userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));
        MotionServer = motionServer ?? throw new ArgumentNullException(nameof(motionServer));
        Overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Subscribe to culture changes so the dialog's labels follow
        // the user's language pick while the dialog is open. Every
        // localized label here is a computed-on-demand property
        // (=> localization["..."]), so we re-fire OnPropertyChanged
        // for each one when the culture changes — that's the signal
        // Avalonia's bindings need to re-call the getter and pick up
        // the new culture's translation.
        this.localization.CultureChanged += OnCultureChanged;

        // Diagnostic: capture the ambient UI culture and a sample
        // resolved label at construction time. If the user reports the
        // dialog is "stuck in the startup language", this single line
        // tells us which half of the system to blame:
        //   • culture == new + label == translated  → binding/render bug
        //   • culture == new + label == English      → PO lookup / fallback
        //   • culture == startup                     → SetCulture isn't
        //     reaching this thread's ambient culture before construction
        logger.LogInformation(
            "SettingsDialog VM constructed. CurrentUICulture={Culture}, ILocalizationService.CurrentCulture={SvcCulture}, sample TitleLabel='{Title}'.",
            System.Globalization.CultureInfo.CurrentUICulture.Name,
            localization.CurrentCulture,
            localization["SettingsDialogTitle"]);

        Reload();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleLabel));
        OnPropertyChanged(nameof(DiagnosticsHeaderLabel));
        OnPropertyChanged(nameof(LogLevelLabel));
        OnPropertyChanged(nameof(LogLevelHintLabel));
        OnPropertyChanged(nameof(WindowHeaderLabel));
        OnPropertyChanged(nameof(RememberWindowSizeLabel));
        OnPropertyChanged(nameof(WindowWidthLabel));
        OnPropertyChanged(nameof(WindowHeightLabel));
        OnPropertyChanged(nameof(PollingHeaderLabel));
        OnPropertyChanged(nameof(DashboardRefreshLabel));
        OnPropertyChanged(nameof(PollingRateLabel));
        OnPropertyChanged(nameof(PollingHintLabel));
        OnPropertyChanged(nameof(PathsHeaderLabel));
        OnPropertyChanged(nameof(ProfilesDirectoryLabel));
        OnPropertyChanged(nameof(LogsDirectoryLabel));
        OnPropertyChanged(nameof(LogsPathHintLabel));
        OnPropertyChanged(nameof(BrowseButtonLabel));
        OnPropertyChanged(nameof(StartupHeaderLabel));
        OnPropertyChanged(nameof(CheckUpdatesLabel));
        OnPropertyChanged(nameof(CheckRequirementsLabel));
        OnPropertyChanged(nameof(ApplyButtonLabel));
        OnPropertyChanged(nameof(CancelButtonLabel));
        OnPropertyChanged(nameof(RestoreDefaultsButtonLabel));
    }

    /// <summary>
    /// Unsubscribes from <see cref="ILocalizationService.CultureChanged"/>.
    /// Called by the dialog's <c>Closed</c> handler so a long-lived
    /// localization service doesn't hold this short-lived view-model
    /// alive via the event handler.
    /// </summary>
    public void Dispose()
    {
        localization.CultureChanged -= OnCultureChanged;
    }

    /// <summary>
    /// Reloads every observable property from the persisted settings
    /// snapshot. Called from the constructor and the Cancel button.
    /// </summary>
    public void Reload()
    {
        var s = userSettings.Current;
        SelectedLogLevel = s.LogLevel;
        RememberWindowSize = s.RememberWindowSize;
        WindowWidth = s.WindowWidth;
        WindowHeight = s.WindowHeight;
        DashboardRefreshHzText = s.DashboardRefreshHz?.ToString() ?? string.Empty;
        PollingRateHzText = s.PollingRateHz?.ToString() ?? string.Empty;
        ProfilesDirectoryOverride = s.ProfilesDirectoryOverride ?? string.Empty;
        LogsDirectoryOverride = s.LogsDirectoryOverride ?? string.Empty;
        CheckForUpdatesOnStartup = s.CheckForUpdatesOnStartup;
        CheckRequirementsOnStartup = s.CheckRequirementsOnStartup;
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// Resets every dialog field to its hard-coded default. Does NOT
    /// persist; the user still has to click Apply.
    /// </summary>
    [RelayCommand]
    public void RestoreDefaults()
    {
        var d = new AppSettings();
        SelectedLogLevel = d.LogLevel;
        RememberWindowSize = d.RememberWindowSize;
        WindowWidth = d.WindowWidth;
        WindowHeight = d.WindowHeight;
        DashboardRefreshHzText = string.Empty;
        PollingRateHzText = string.Empty;
        ProfilesDirectoryOverride = string.Empty;
        LogsDirectoryOverride = string.Empty;
        CheckForUpdatesOnStartup = d.CheckForUpdatesOnStartup;
        CheckRequirementsOnStartup = d.CheckRequirementsOnStartup;
        StatusMessage = localization["SettingsDialogRestoredStatus"];
    }

    /// <summary>
    /// Folds the dialog state into <see cref="AppSettings"/> and writes
    /// it back through <see cref="IUserSettingsService.ApplyAsync"/>.
    /// Returns <see langword="true"/> on success.
    /// </summary>
    [RelayCommand]
    public async Task<bool> ApplyAsync()
    {
        try
        {
            var current = userSettings.Current;
            var updated = current with
            {
                LogLevel = SelectedLogLevel,
                RememberWindowSize = RememberWindowSize,
                WindowWidth = ClampWindowDimension(WindowWidth, defaultValue: 1280),
                WindowHeight = ClampWindowDimension(WindowHeight, defaultValue: 800),
                DashboardRefreshHz = ParseOptionalInt(DashboardRefreshHzText, min: 30, max: 1000),
                PollingRateHz = ParseOptionalInt(PollingRateHzText, min: 30, max: 1000),
                ProfilesDirectoryOverride = string.IsNullOrWhiteSpace(ProfilesDirectoryOverride) ? null : ProfilesDirectoryOverride.Trim(),
                LogsDirectoryOverride = string.IsNullOrWhiteSpace(LogsDirectoryOverride) ? null : LogsDirectoryOverride.Trim(),
                CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
                CheckRequirementsOnStartup = CheckRequirementsOnStartup,
            };

            await userSettings.ApplyAsync(updated).ConfigureAwait(false);

            // The path-override change may have moved the in-effect
            // directories; refresh the read-only display rows so the user
            // can see the result.
            OnPropertyChanged(nameof(CurrentProfilesPath));
            OnPropertyChanged(nameof(CurrentLogsPath));

            StatusMessage = localization["SettingsDialogAppliedStatus"];
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not apply settings dialog changes.");
            StatusMessage = string.Format(localization["SettingsDialogApplyFailedStatus"], exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Refreshes every localised label property — invoked from the
    /// hosting dialog when the culture changes, in case the user opens
    /// settings, switches language somewhere else, and comes back.
    /// </summary>
    public void RefreshLocalisedLabels()
    {
        OnPropertyChanged(nameof(TitleLabel));
        OnPropertyChanged(nameof(DiagnosticsHeaderLabel));
        OnPropertyChanged(nameof(LogLevelLabel));
        OnPropertyChanged(nameof(LogLevelHintLabel));
        OnPropertyChanged(nameof(WindowHeaderLabel));
        OnPropertyChanged(nameof(RememberWindowSizeLabel));
        OnPropertyChanged(nameof(WindowWidthLabel));
        OnPropertyChanged(nameof(WindowHeightLabel));
        OnPropertyChanged(nameof(PollingHeaderLabel));
        OnPropertyChanged(nameof(DashboardRefreshLabel));
        OnPropertyChanged(nameof(PollingRateLabel));
        OnPropertyChanged(nameof(PollingHintLabel));
        OnPropertyChanged(nameof(PathsHeaderLabel));
        OnPropertyChanged(nameof(ProfilesDirectoryLabel));
        OnPropertyChanged(nameof(LogsDirectoryLabel));
        OnPropertyChanged(nameof(LogsPathHintLabel));
        OnPropertyChanged(nameof(BrowseButtonLabel));
        OnPropertyChanged(nameof(StartupHeaderLabel));
        OnPropertyChanged(nameof(CheckUpdatesLabel));
        OnPropertyChanged(nameof(CheckRequirementsLabel));
        OnPropertyChanged(nameof(ApplyButtonLabel));
        OnPropertyChanged(nameof(CancelButtonLabel));
        OnPropertyChanged(nameof(RestoreDefaultsButtonLabel));
    }

    /// <summary>
    /// Parses an optional integer. Whitespace -> null. Garbage -> null.
    /// Out-of-range values are clamped into [<paramref name="min"/>,
    /// <paramref name="max"/>].
    /// </summary>
    private static int? ParseOptionalInt(string text, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var parsed)
            ? Math.Clamp(parsed, min, max)
            : (int?)null;
    }

    /// <summary>
    /// Clamps a window dimension to a sane range. Negative values, zero,
    /// or anything past 8000 collapses back to <paramref name="defaultValue"/>.
    /// </summary>
    private static double ClampWindowDimension(double value, double defaultValue)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 200 || value > 8000)
        {
            return defaultValue;
        }
        return value;
    }
}
