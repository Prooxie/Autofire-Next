namespace GameFlow.Infrastructure.Configuration;

/// <summary>
/// Strongly-typed view over the <c>Runtime</c> section of
/// <c>appsettings.json</c> plus environment-variable overrides.
///
/// <para>
/// These values are read once at startup and treated as immutable for
/// the life of the process. Per-user runtime-mutable preferences live
/// on <see cref="GameFlow.Infrastructure.Profiles.AppSettings"/> instead.
/// </para>
/// </summary>
public sealed class AppRuntimeOptions
{
    /// <summary>
    /// Hertz at which the dashboard view-models recompute. May be
    /// overridden per user via <c>AppSettings.DashboardRefreshHz</c>.
    ///
    /// <para>
    /// 60, not 30. The old value dates from a controller surface costing
    /// ~19 ms to repaint; it now costs a few ms, and 30 Hz was spending up
    /// to 33 ms of latency to save work that no longer exists. The
    /// dashboard's adaptive backoff still degrades a machine that cannot
    /// hold it.
    /// </para>
    /// </summary>
    public int DashboardRefreshHz { get; set; } = 60;

    /// <summary>
    /// When <see langword="true"/>, the runtime coordinator hosted
    /// service starts the polling loop on app launch. Set to
    /// <see langword="false"/> for diagnostic/headless invocations
    /// where you want no provider state.
    /// </summary>
    public bool StartRuntimeOnLaunch { get; set; } = true;

    /// <summary>
    /// Two-letter ISO culture code applied when no per-user
    /// <c>AppSettings.SelectedCulture</c> is on disk. Used only on
    /// first launch.
    /// </summary>
    public string DefaultCulture { get; set; } = "en";

    // EnableViGEm was removed along with the ViGEm dependency itself —
    // HIDMaestro is now the sole output backend.

    /// <summary>
    /// Updater subsection — owner / repo / asset filter pattern. Bound
    /// from <c>Runtime:Updates</c> in <c>appsettings.json</c>. See
    /// <see cref="UpdateOptions"/>.
    /// </summary>
    public UpdateOptions Updates { get; set; } = new();
}

/// <summary>
/// Updater configuration. Bound from <c>Runtime:Updates</c> in
/// <c>appsettings.json</c>.
///
/// <para>
/// Defaults are placeholder strings; the maintainer fills them in for
/// the live distribution. When <see cref="RepoOwner"/> or <see cref="RepoName"/>
/// is empty / placeholder, the update check short-circuits and reports
/// <see cref="GameFlow.Infrastructure.Updates.UpdateCheckResult.Failed"/>
/// — never blocking startup.
/// </para>
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>
    /// GitHub user or organisation that owns the release repo.
    /// </summary>
    public string RepoOwner { get; set; } = string.Empty;

    /// <summary>
    /// GitHub repository name.
    /// </summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    /// Asset-name pattern used to pick the right asset for the running
    /// platform. <c>{rid}</c> is replaced with
    /// <see cref="System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier"/>
    /// at probe time. Default matches the release artifact naming used in
    /// the README (e.g. <c>"GameFlow-v1.2.3-win-x64.zip"</c>).
    /// </summary>
    public string AssetNamePattern { get; set; } = "GameFlow-{tag}-{rid}.zip";

    /// <summary>
    /// User-Agent string sent on GitHub API calls. GitHub rejects API
    /// calls without a User-Agent header. Default is the application
    /// name; override only if you must mimic something else.
    /// </summary>
    public string UserAgent { get; set; } = "GameFlow-UpdateChecker";
}
