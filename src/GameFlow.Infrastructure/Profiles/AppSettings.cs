using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Profiles;

/// <summary>
/// Persisted, user-mutable application settings. Stored as JSON at
/// <see cref="GameFlow.Infrastructure.Configuration.AppPaths.SettingsFile"/>
/// and loaded once at startup by
/// <see cref="GameFlow.Infrastructure.Configuration.UserSettingsService"/>.
///
/// <para>
/// Conceptually a sibling to <see cref="GameFlow.Infrastructure.Configuration.AppRuntimeOptions"/>:
/// the <c>RuntimeOptions</c> are immutable per session and come from
/// <c>appsettings.json</c> + environment variables; <c>AppSettings</c> are
/// owned by the user and edited through the in-app settings menu.
/// </para>
///
/// <para>
/// All properties are init-only so that callers (e.g. <see cref="ProfileSession"/>)
/// can use the <c>with</c> expression to produce updated copies. New fields
/// added in later versions get default values when the JSON file is older;
/// fields no longer recognised by the runtime are silently ignored on read,
/// so forward and backward compatibility are both safe.
/// </para>
/// </summary>
public sealed record AppSettings
{
    // ---------------------------------------------------------------------
    // Existing fields (kept for back-compat with previous JSON files).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Id of the profile that was last active. The <see cref="ProfileSession"/>
    /// loads this profile on startup; if the file is missing, it falls back to
    /// <see cref="ProfileDefaults.CreateSpeedrunnerDefault"/>.
    /// </summary>
    public string ActiveProfileId { get; init; } = "speedrunner-default";

    /// <summary>
    /// Two-letter ISO culture code (e.g. <c>"en"</c>, <c>"cs"</c>) used by the
    /// <see cref="Localization.LocalizationService"/> on startup.
    /// </summary>
    public string SelectedCulture { get; init; } = "en";

    // ---------------------------------------------------------------------
    // Diagnostics.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Minimum log level applied at runtime via Serilog's
    /// <c>LoggingLevelSwitch</c>. Changing this in the settings UI takes
    /// effect immediately for every sink (console + rolling file).
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    // ---------------------------------------------------------------------
    // Window / display.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Last persisted shell window width in DIPs. Restored on next launch
    /// when <see cref="RememberWindowSize"/> is <see langword="true"/>.
    /// </summary>
    public double WindowWidth { get; init; } = 1280;

    /// <summary>
    /// Last persisted shell window height in DIPs.
    /// </summary>
    public double WindowHeight { get; init; } = 800;

    /// <summary>
    /// Whether the last session ended with the window maximised.
    /// </summary>
    public bool WindowMaximised { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the shell saves window size on close and
    /// restores it on next launch. When <see langword="false"/>, the defaults
    /// above are used and the saved values are ignored.
    /// </summary>
    public bool RememberWindowSize { get; init; } = true;

    // ---------------------------------------------------------------------
    // Polling cadence.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Override for the dashboard refresh rate in Hz. When non-null this
    /// supersedes <c>AppRuntimeOptions.DashboardRefreshHz</c>. Valid range
    /// is 30–1000; values outside that range are clamped at apply time.
    /// </summary>
    public int? DashboardRefreshHz { get; init; }

    /// <summary>
    /// Override for the input polling rate in Hz. When non-null this
    /// supersedes the per-profile <c>PollingRateHz</c> for runtime
    /// scheduling but is NOT written back to the profile.
    /// </summary>
    public int? PollingRateHz { get; init; }

    // ---------------------------------------------------------------------
    // Path overrides — applied through AppPathOverrides on load.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Optional override for the directory that holds profile JSON files.
    /// Empty / null means "use the default under
    /// <see cref="GameFlow.Infrastructure.Configuration.AppPaths.BaseDirectory"/>."
    /// </summary>
    public string? ProfilesDirectoryOverride { get; init; }

    /// <summary>
    /// Optional override for the directory that holds rolling log files.
    /// Takes effect for the file sink on the next process restart; takes
    /// effect immediately for any UI action that reads
    /// <see cref="GameFlow.Infrastructure.Configuration.AppPaths.LogsDirectory"/>.
    /// </summary>
    public string? LogsDirectoryOverride { get; init; }

    // ---------------------------------------------------------------------
    // Updater preferences (consumed in step 3 of the roadmap).
    // ---------------------------------------------------------------------

    /// <summary>
    /// When <see langword="false"/>, the auto-update check at startup is
    /// suppressed. Toggled by the "Don't ask" choice in the update dialog.
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; init; } = true;

    /// <summary>
    /// Version string the user explicitly chose to skip ("Skip this update").
    /// The next-launch update check ignores any release whose version equals
    /// this value. Cleared when the user accepts a newer release.
    /// </summary>
    public string? SkippedUpdateVersion { get; init; }

    // ---------------------------------------------------------------------
    // Requirements preferences (consumed in step 3 of the roadmap).
    // ---------------------------------------------------------------------

    /// <summary>
    /// When <see langword="true"/>, the requirements check (HIDMaestro, etc.)
    /// runs at startup and prompts the user to install missing pieces. Set
    /// to <see langword="false"/> by the "Don't ask again" choice.
    /// </summary>
    public bool CheckRequirementsOnStartup { get; init; } = true;

    /// <summary>
    /// Whether the first-run setup walkthrough has already been shown.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/> so an existing installation
    /// that predates this field also gets the walkthrough once. That is
    /// deliberate: the walkthrough creates nothing until the user reaches
    /// its last step, so the cost of showing it to someone who did not
    /// need it is one dismissed window, while the cost of skipping it for
    /// someone who did is the empty dashboard this exists to prevent.
    /// </remarks>
    public bool SetupWalkthroughCompleted { get; init; }

    // ---------------------------------------------------------------------
    // DSU / Cemuhook motion server.
    // ---------------------------------------------------------------------

    /// <summary>
    /// When <see langword="true"/>, the DSU / Cemuhook server listens for
    /// emulator clients and streams gyro/accelerometer for the enabled slots.
    ///
    /// <para>
    /// Off by default, deliberately. This opens a UDP listener, and a network
    /// service the user never asked for should not appear the first time the
    /// app runs — the same reasoning that keeps the web controller and the
    /// stream overlay opt-in.
    /// </para>
    /// </summary>
    public bool MotionServerEnabled { get; init; }

    /// <summary>
    /// UDP port the DSU / Cemuhook server binds. 26760 is the port every
    /// consumer (Cemu, Dolphin, Yuzu, Ryujinx) probes by default, so changing
    /// it means reconfiguring the emulator to match.
    /// </summary>
    public int MotionServerPort { get; init; } = 26760;

    // ---------------------------------------------------------------------
    // Per-app profile switching.
    // ---------------------------------------------------------------------

    /// <summary>
    /// When <see langword="true"/>,
    /// <see cref="Runtime.Profiles.ProfileAutoSwitchService"/> watches the
    /// foreground application and activates the profile a matching rule
    /// names.
    ///
    /// <para>
    /// Off by default. Changing the user's active profile is a visible,
    /// surprising thing for software to do on its own, and someone who
    /// has not written any rules would only ever notice this as their
    /// mappings changing for no reason they can see. The switch and the
    /// rules are separate so a user can park the whole feature without
    /// losing what they configured.
    /// </para>
    /// </summary>
    public bool AutoSwitchProfilesByApp { get; init; }

    /// <summary>
    /// Process-name → profile rules, in priority order (first match
    /// wins). See <see cref="Runtime.Profiles.AppProfileMatcher"/>.
    /// </summary>
    public IReadOnlyList<Runtime.Profiles.AppProfileRule> AppProfileRules { get; init; } = [];
}
