using GameFlow.App.Bootstrap;
using GameFlow.App.Services;
using GameFlow.App.Startup;
using GameFlow.App.ViewModels;
using GameFlow.App.Views;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Profiles;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GameFlow.App;

public partial class App : Application
{
    private IHost? host;
    private int shutdownStarted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Seed the default palette before anything paints. ShellViewModel
        // re-applies the user's saved theme once profiles have loaded, but
        // only when it differs from the default — so on a default-theme
        // launch this is the sole call, and skipping it would leave every
        // stock Fluent control (check boxes, sliders, scroll bars, combo
        // popups) on Fluent's own blue instead of the app palette. It also
        // covers the splash window, which appears long before the shell.
        AppThemeService.Apply(AppThemeKind.CyberBlue);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splashWindow = new SplashWindow();
            desktop.MainWindow = splashWindow;
            splashWindow.Show();

            _ = StartDesktopAsync(desktop, splashWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splashWindow)
    {
        try
        {
            host = HostBuilderFactory.Create(Environment.GetCommandLineArgs()).Build();
            await host.StartAsync();

            // Load persisted user settings BEFORE ProfileSession initialises:
            // the loaded settings push path overrides into AppPathOverrides,
            // and ProfileSession reads AppPaths.ProfilesDirectory on its
            // first call. They must be in the right order or the first-run
            // profile lands in the wrong directory.
            var userSettings = host.Services.GetRequiredService<IUserSettingsService>();
            await userSettings.InitializeAsync();

            // First-run / repair: mirror the bundled themes folder from the
            // build output into the user-writable %LocalAppData%\GAMEFLOW\themes
            // location. Runs every launch; ThemeBootstrap skips style folders
            // that already exist, so manual edits survive. Without this call
            // a user who deletes their themes folder ends up with the empty
            // "no theme installed for this style" message because the
            // registry has nothing to scan.
            try
            {
                var installed = GameFlow.Infrastructure.Theming.ThemeBootstrap
                    .EnsureBundledThemesInstalled();
                if (installed > 0)
                {
                    Serilog.Log.Information(
                        "ThemeBootstrap installed {Count} bundled theme(s) into LocalAppData.",
                        installed);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex,
                    "ThemeBootstrap failed to install bundled themes; the controller surface may render empty.");
            }

            var profileSession   = host.Services.GetRequiredService<ProfileSession>();
            await profileSession.EnsureInitializedAsync();

            var localization = host.Services.GetRequiredService<ILocalizationService>();
            localization.SetCulture(profileSession.Settings.SelectedCulture);

            var shellViewModel = host.Services.GetRequiredService<ShellViewModel>();
            await shellViewModel.InitializeAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(350));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var shellWindow = host!.Services.GetRequiredService<ShellWindow>();
                shellWindow.DataContext = shellViewModel;
                shellWindow.FitToWorkingArea();

                desktop.MainWindow = shellWindow;
                shellWindow.Show();
                splashWindow.Close();

                desktop.Exit += DesktopOnExit;
            });

            // Fire startup checks (requirements + updates) AFTER the shell
            // window is fully realised, on a background task.
            //
            // Why the wait: if the task races and reaches `desktop.MainWindow`
            // before the shell window has finished loading, the dialogs we
            // spawn parent against a not-yet-ready window. That manifests as
            // "[Control] PlatformImpl is null, couldn't handle input." spam
            // and an exit code of 0xFFFFFFFF (-1) when the window then closes
            // out from under the still-pending dialog.
            //
            // We hook the Loaded event on the UI thread, complete a TCS from
            // it, and have the background task await that TCS before
            // dispatching anything dialog-shaped to the UI thread.
            var shellLoaded = new TaskCompletionSource();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (desktop.MainWindow is { } shell)
                {
                    if (shell.IsLoaded)
                    {
                        shellLoaded.TrySetResult();
                    }
                    else
                    {
                        shell.Loaded += (_, _) => shellLoaded.TrySetResult();
                    }
                }
                else
                {
                    shellLoaded.TrySetResult();
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await shellLoaded.Task.ConfigureAwait(false);

                    var coordinator = host!.Services.GetRequiredService<StartupChecksCoordinator>();
                    var ownerWindow = await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // Re-check on UI thread — the user may have closed the
                        // window in the gap between Loaded firing and us
                        // getting back here.
                        var w = desktop.MainWindow;
                        return (w is null || !w.IsVisible) ? null : w;
                    });

                    if (ownerWindow is null)
                    {
                        Log.Debug("Startup checks skipped: main window is no longer available.");
                        return;
                    }

                    await coordinator.RunAsync(ownerWindow);
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Startup checks task failed.");
                }
            });
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Application startup was cancelled.");
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Desktop bootstrap failed.");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                splashWindow.Content = new TextBlock
                {
                    Text = exception.Message,
                    Margin = new Thickness(24)
                };
            });
        }
    }

    private void DesktopOnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) == 1)
        {
            return;
        }

        // Watchdog: spawn a *background* thread that waits a few
        // seconds and then force-kills the process if any of the
        // cleanup steps below have hung. Background = doesn't itself
        // prevent process exit, so when the polite shutdown finishes
        // promptly the watchdog is harmless. When it doesn't — typically
        // a native provider's foreground thread we can't reach from
        // managed code — Process.Kill() reclaims everything.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(4));
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
                // Nothing useful to do if Kill itself faults.
            }
        })
        {
            IsBackground = true,
            Name         = "Autofire-Exit-Watchdog",
        }.Start();

        var currentHost = Interlocked.Exchange(ref host, null);
        if (currentHost is null)
        {
            // Still force-exit so a foreground thread doesn't keep
            // the process alive after the main window has closed.
            Log.CloseAndFlush();
            Environment.Exit(0);
            return;
        }

        try
        {
            // Use a generous timeout so native providers (SDL, HIDMaestro) have
            // time to release OS handles before the process exits.  Without this
            // the CLR garbage collector may run finalizers AFTER the native DLLs
            // have already been unloaded, causing an AccessViolation on shutdown.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            currentHost.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Host shutdown timed out — forcing process exit.");
        }
        catch (AggregateException ex) when
            (ex.InnerExceptions.All(inner =>
                inner is OperationCanceledException or ObjectDisposedException))
        {
            Log.Debug("Host shutdown completed with cancellation during application exit.");
        }
        catch (ObjectDisposedException)
        {
            Log.Debug("Host was already disposed during application exit.");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to stop host cleanly.");
        }
        finally
        {
            // Dispose BEFORE the process exits so native destructors run in the
            // correct order and don't fault when the SDL / HIDMaestro DLLs
            // are still mapped in memory.
            try
            {
                currentHost.Dispose();
            }
            catch (Exception exception)
            {
                Log.Debug(exception, "Host dispose reported an error during application exit.");
            }

            // Flush Serilog after everything else is torn down.
            Log.CloseAndFlush();

            // Some input/output providers (SDL, HIDMaestro wrappers)
            // park foreground threads we can't reach from managed code,
            // and they're notorious for keeping the process alive after
            // the UI has closed. Now that cleanup is done — host stopped,
            // host disposed, logs flushed — force a hard exit so the
            // user doesn't need to kill the process from a terminal.
            // Environment.Exit terminates the process regardless of any
            // remaining non-background threads.
            Environment.Exit(0);
        }
    }
}
