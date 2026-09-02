using GameFlow.App.Services;
using GameFlow.App.Startup;
using GameFlow.App.ViewModels;
using GameFlow.App.Views;
using GameFlow.Infrastructure;
using GameFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GameFlow.App.Bootstrap;

/// <summary>
/// Factory that produces the application's <see cref="IHostBuilder"/>.
///
/// <para>
/// Two extra responsibilities beyond a standard host build:
/// </para>
///
/// <list type="bullet">
///   <item>
///     <description>
///       Eagerly creates a Serilog <see cref="LoggingLevelSwitch"/> seeded
///       from <c>appsettings.json</c>'s <c>Serilog:MinimumLevel:Default</c>
///       value. The switch is wired into the Serilog pipeline through
///       <c>MinimumLevel.ControlledBy(...)</c>, then exposed to the rest
///       of the app as an <see cref="ILogLevelSwitch"/> via
///       <see cref="SerilogLogLevelSwitch"/> so the user can change the
///       level from the settings menu without restarting the process.
///     </description>
///   </item>
///   <item>
///     <description>
///       Registers the App-only services (<see cref="IProfileFileDialogService"/>,
///       <see cref="ShellViewModel"/>, <see cref="ShellWindow"/>) on top of
///       the Infrastructure registrations.
///     </description>
///   </item>
/// </list>
/// </summary>
public static class HostBuilderFactory
{
    /// <summary>
    /// Builds and returns a configured but un-started host.
    /// </summary>
    /// <param name="args">Process command-line arguments, forwarded to the
    /// host builder so it can pick up CLI configuration overrides.</param>
    public static IHostBuilder Create(string[] args)
    {
        // Created up front so it can be referenced from both the Serilog
        // configuration and the DI service registrations below. The seeded
        // value is overwritten as soon as UserSettingsService.InitializeAsync
        // runs at app startup; until then the level reflects what the static
        // appsettings.json said.
        var loggingLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, configurationBuilder) =>
            {
                _ = configurationBuilder.SetBasePath(AppContext.BaseDirectory);
                _ = configurationBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                _ = configurationBuilder.AddEnvironmentVariables(prefix: "AUTOFIRE_");
            })
            .UseSerilog((hostingContext, services, loggerConfiguration) =>
            {
                _ = Directory.CreateDirectory(AppPaths.LogsDirectory);

                // Seed the switch from configuration so we honour whatever
                // the user pinned in appsettings.json on first launch (before
                // the persisted settings.json has been read).
                var seedLevel = ParseSeedLevel(hostingContext.Configuration);
                loggingLevelSwitch.MinimumLevel = seedLevel;

                _ = loggerConfiguration
                    .ReadFrom.Configuration(hostingContext.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .MinimumLevel.ControlledBy(loggingLevelSwitch)
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .WriteTo.Console()
                    .WriteTo.File(
                        Path.Combine(AppPaths.LogsDirectory, "gameflow-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14);
            })
            .ConfigureServices((hostingContext, services) =>
            {
                _ = services.Configure<HostOptions>(options =>
                {
                    options.ShutdownTimeout = TimeSpan.FromSeconds(5);
                });

                // Expose the underlying Serilog switch and the framework-neutral
                // wrapper. The wrapper is what UserSettingsService actually
                // consumes; the raw switch is registered too in case any
                // future code wants to read its current value directly.
                _ = services.AddSingleton(loggingLevelSwitch);
                _ = services.AddSingleton<ILogLevelSwitch>(_ => new SerilogLogLevelSwitch(loggingLevelSwitch));

                _ = services.AddAutofireInfrastructure(hostingContext.Configuration);
                _ = services.AddSingleton<IProfileFileDialogService, ProfileFileDialogService>();
                _ = services.AddSingleton<StartupChecksCoordinator>();
                _ = services.AddTransient<SettingsDialogViewModel>();

                // Per-device tuning editor. Transient: each slot/device
                // pairing gets its own editor instance pointed at the
                // shared DeviceSettingsStore singleton.
                _ = services.AddTransient<DeviceSettingsEditorViewModel>();

                // Motion-server panel. Singleton to match the shell that
                // holds it: its enable/port state is the persisted user
                // setting, so a second instance would be a second, stale
                // view of the same thing.
                _ = services.AddSingleton<MotionServerPanelViewModel>();

                // Stream-overlay panel. Transient: it holds no state worth
                // keeping between dialogs — the pickers are rebuilt from
                // the live theme and slot lists each time it is shown —
                // and a singleton would just be a stale copy of both.
                _ = services.AddTransient<OverlayPanelViewModel>();

                // Per-app profile rules. Transient for the same reason:
                // it reloads from settings on construction, so a fresh
                // one per dialog is always current.
                _ = services.AddTransient<AppProfilePanelViewModel>();

                // First-run walkthrough. Transient and IDisposable: it
                // subscribes to the device catalog for hot-plug detection,
                // and each showing must start from step one with a fresh
                // subscription that the window disposes on close.
                _ = services.AddTransient<SetupWalkthroughViewModel>();

                _ = services.AddSingleton<ShellViewModel>();
                _ = services.AddSingleton<ShellWindow>();
            });
    }

    /// <summary>
    /// Reads the initial Serilog level from
    /// <c>Serilog:MinimumLevel:Default</c>, falling back to
    /// <see cref="LogEventLevel.Information"/> when the value is missing
    /// or unparseable.
    /// </summary>
    private static LogEventLevel ParseSeedLevel(IConfiguration configuration)
    {
        var raw = configuration["Serilog:MinimumLevel:Default"];
        return Enum.TryParse<LogEventLevel>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;
    }
}
