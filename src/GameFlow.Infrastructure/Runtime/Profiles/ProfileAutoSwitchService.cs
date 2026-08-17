using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Profiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Profiles;

/// <summary>
/// Watches the foreground application and switches the active profile
/// when a rule claims it.
///
/// <para>
/// Polls rather than subscribing to <c>SetWinEventHook</c>. The hook is
/// the tidier mechanism on paper, but it delivers on a thread with a
/// message pump this process does not own in a place that would suit it,
/// and the thing being detected is a human alt-tabbing. A one-second
/// poll is imperceptible for that and costs one window-handle comparison
/// per tick in the common case where nothing moved — see
/// <see cref="WindowsForegroundAppReader"/>'s cache.
/// </para>
/// </summary>
public sealed class ProfileAutoSwitchService(
    IForegroundAppReader reader,
    IUserSettingsService userSettings,
    ProfileSession session,
    ILogger<ProfileAutoSwitchService> logger) : BackgroundService
{
    private readonly IForegroundAppReader reader = reader;
    private readonly IUserSettingsService userSettings = userSettings;
    private readonly ProfileSession session = session;
    private readonly ILogger<ProfileAutoSwitchService> logger = logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The application seen on the previous tick. Rules are evaluated
    /// only when this changes, so a user who switches profile by hand
    /// while a matched game is still in front keeps their choice instead
    /// of having it overwritten a second later.
    /// </summary>
    private string? lastSeenApp;

    /// <summary>Last app whose rule actually fired, for the log line and to avoid re-switching.</summary>
    private string? lastSwitchedApp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticker = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ticker.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // A failure here must never take the host down or stop
                // the loop: this is a convenience running alongside the
                // input pipeline, and a profile that did not switch is a
                // far smaller problem than a runtime that stopped.
                logger.LogWarning(exception, "Per-app profile switch: a poll failed; continuing.");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = userSettings.Current;
        if (!settings.AutoSwitchProfilesByApp)
        {
            // Reset so re-enabling the feature evaluates the app in front
            // right now, rather than waiting for the user to alt-tab.
            lastSeenApp = null;
            return;
        }

        var app = reader.GetForegroundProcessName();
        if (app is null || string.Equals(app, lastSeenApp, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lastSeenApp = app;

        var profileId = AppProfileMatcher.Match(settings.AppProfileRules, app);
        if (profileId is null)
        {
            // No rule for this app. Deliberately leaves the current
            // profile alone — see AppProfileMatcher.Match.
            return;
        }

        if (string.Equals(profileId, session.CurrentProfile.Id, StringComparison.OrdinalIgnoreCase))
        {
            lastSwitchedApp = app;
            return;
        }

        try
        {
            await session.SwitchToProfileAsync(profileId, cancellationToken);
            lastSwitchedApp = app;
            logger.LogInformation(
                "Per-app profile switch: {App} came to the foreground, switched to profile {Profile}.",
                app, profileId);
        }
        catch (Exception exception)
        {
            // Most likely a rule naming a profile the user has since
            // deleted. Logged once per switch attempt rather than
            // per second, because lastSeenApp has already advanced.
            logger.LogWarning(exception,
                "Per-app profile switch: {App} matched profile {Profile}, but switching failed.",
                app, profileId);
        }
    }

    /// <summary>
    /// The app that last caused a switch. Exposed for the settings UI's
    /// status line, which is the only way a user can tell whether their
    /// rule is matching what they think it is.
    /// </summary>
    public string? LastSwitchedApp => lastSwitchedApp;

    /// <summary>The application currently in front, or null. Also for the settings UI — it is how a user learns what to type into a rule.</summary>
    public string? CurrentApp => reader.GetForegroundProcessName();
}
