using Avalonia.Input.Platform;
using GameFlow.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;

namespace GameFlow.App.Views;

public partial class ShellWindow : Window
{
    private readonly DispatcherTimer refreshTimer;
    private DateTime lastTickUtc = DateTime.UtcNow;
    private DateTime lastTickGapWarnUtc;

    /// <summary>The rate the user asked for; a ceiling, not a promise. See <see cref="AdaptTickRate"/>.</summary>
    private int configuredRefreshHz = 30;

    /// <summary>When the dispatcher last overran its budget. Drives recovery.</summary>
    private DateTime lastOverrunUtc = DateTime.MinValue;

    /// <summary>
    /// How long the UI must go without a single overrun before the tick
    /// steps back up. Long enough that recovery cannot chase a transient.
    /// </summary>
    private static readonly TimeSpan RecoveryQuietPeriod = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long after the window opens the dashboard ticks gently and
    /// ignores overruns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Startup is the one moment the UI thread is guaranteed to be busy
    /// with something other than the dashboard: theme artwork decoding,
    /// the first device enumeration, and HIDMaestro walking its 231-entry
    /// profile catalog. Ticking at the display's full rate straight into
    /// that contention did not just drop frames, it made the panels look
    /// like they had vanished — and then the adaptive backoff read the
    /// contention as a slow machine and stepped 165 → 83 → 41 → 21 → 10
    /// Hz, after which the 8-second quiet period had to elapse before any
    /// of it came back. A one-off startup cost was being converted into
    /// fifteen seconds of degraded dashboard.
    /// </para>
    /// <para>
    /// So: a fixed, modest rate while the queue drains, and no backoff
    /// decisions taken from measurements made during it.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StartupWarmUp = TimeSpan.FromSeconds(4);

    /// <summary>Tick rate held during <see cref="StartupWarmUp"/>.</summary>
    private const int WarmUpRefreshHz = 30;

    private DateTime warmUpUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Set once the configured rate has been applied at the end of
    /// warm-up, so it is applied exactly once.
    /// </summary>
    /// <remarks>
    /// Without this the tick handler re-asserts the configured rate every
    /// frame, which immediately undoes whatever the adaptive backoff just
    /// decided — the two then trade the interval back and forth forever
    /// (165 Hz, throttle to 83, re-assert 165, …), restarting the
    /// DispatcherTimer each time and filling the log at frame rate. The
    /// handoff from warm-up to configured is a one-time event, so it is
    /// modelled as one.
    /// </remarks>
    private bool warmUpHandedOff;

    private ShellViewModel? shellViewModel;
    private bool isRefreshing;
    private bool isClosing;

    /// <summary>
    /// Container used to build the short-lived view-models behind the
    /// sheets this window opens (setup walkthrough, phone controller).
    /// </summary>
    /// <remarks>
    /// Null only under the XAML previewer, which uses the parameterless
    /// constructor; at runtime the window is always resolved from DI, and
    /// the greedier constructor wins.
    /// </remarks>
    private readonly IServiceProvider? services;

    public ShellWindow() : this(null)
    {
    }

    public ShellWindow(IServiceProvider? services)
    {
        this.services = services;
        InitializeComponent();

        refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)   // ~60 Hz UI tick; see ApplyConfiguredRefreshRate
        };

        refreshTimer.Tick += RefreshTimerOnTick;
        Opened  += OnOpened;
        Closing += OnClosing;
        Closed  += OnClosed;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Applies the user's dashboard refresh rate to the UI tick.
    ///
    /// <para>
    /// The setting was previously inert: it is persisted, exposed in the
    /// settings dialog, documented in the README and bound from
    /// appsettings.json, but the timer was constructed with a hard-coded
    /// 33 ms and nothing ever wrote to it. Turning the rate down on a
    /// weak machine therefore did nothing at all.
    /// </para>
    ///
    /// <para>
    /// This tick drives the whole dashboard redraw — every controller
    /// surface, physical and virtual, for every slot — so it is the
    /// dominant UI cost, and the one dial worth having.
    /// </para>
    ///
    /// <para>
    /// The default is 60 Hz. It was 30, chosen when a controller surface
    /// cost ~19 ms to repaint and a faster tick could not have been paid
    /// for. That cost is gone — measured against a live DualSense on
    /// 2026-08-11, a surface now repaints in <b>0.55–0.79 ms</b> — so 30 Hz
    /// was buying nothing and costing up to 33 ms of latency between a
    /// finger moving and the screen showing it. That delay is precisely
    /// what "the theme feels laggy" is, and it is most obvious on the
    /// touchpad, where a dot visibly trails the finger that is drawing it.
    /// Two surfaces at 60 Hz is roughly 9% of one core.
    /// </para>
    ///
    /// <para>
    /// The adaptive backoff below still protects a machine or theme that
    /// cannot hold this, so raising the default cannot make anything
    /// unresponsive — it degrades instead.
    /// </para>
    /// </summary>
    private void ApplyConfiguredRefreshRate()
    {
        // Still warming up: hold the gentle rate and come back later. The
        // caller is re-invoked from the tick, so no timer is needed.
        if (DateTime.UtcNow < warmUpUntilUtc)
        {
            var warmUpInterval = TimeSpan.FromMilliseconds(1000d / WarmUpRefreshHz);
            if (refreshTimer.Interval != warmUpInterval)
            {
                refreshTimer.Interval = warmUpInterval;
            }

            return;
        }

        var hz = shellViewModel?.DashboardRefreshHz ?? 60;

        // Clamped to the same range the settings dialog validates, so a
        // hand-edited settings.json cannot stall the UI with 1 Hz or spin
        // it at 10 000.
        hz = Math.Clamp(hz, 30, 1000);
        configuredRefreshHz = hz;

        var interval = TimeSpan.FromMilliseconds(1000d / hz);
        if (refreshTimer.Interval != interval)
        {
            refreshTimer.Interval = interval;

            // Says where the number came from. "60 Hz" alone cannot be
            // told apart from the fallback that is also 60, and those two
            // mean very different things when someone is asking why a
            // 144 Hz monitor is not being used.
            var detected = Platform.DisplayRefreshRate.TryGetPrimaryHz();
            Log.Information(
                "Dashboard UI tick set to {Hz} Hz ({Interval:F1} ms); display reports {Display}.",
                hz,
                interval.TotalMilliseconds,
                detected is { } displayHz ? $"{displayHz} Hz" : "no rate");
        }
    }

    /// <summary>
    /// Keeps the first frame inside the primary monitor's usable area,
    /// including DPI scaling and the taskbar. This runs before
    /// <see cref="Window.Show()"/> so an oversized window never flashes.
    /// </summary>
    public void FitToWorkingArea()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var maximumWidth = screen.WorkingArea.Width / scaling * 0.92d;
        var maximumHeight = screen.WorkingArea.Height / scaling * 0.92d;

        MinWidth = Math.Min(MinWidth, maximumWidth);
        MinHeight = Math.Min(MinHeight, maximumHeight);
        Width = Math.Min(Width, maximumWidth);
        Height = Math.Min(Height, maximumHeight);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        // Explicit null checks rather than `shellViewModel?.Event -= handler`:
        // the null-conditional operator cannot be used for event
        // subscription in C# (the left side of += / -= must be an event
        // access, not a null-conditional expression). Behaviour is
        // identical — subscribe/unsubscribe only when the view model is
        // present.
        if (shellViewModel is not null)
        {
            shellViewModel.ControlMappingRequested -= OnControlMappingRequested;
            shellViewModel.DeviceSettingsRequested -= OnDeviceSettingsRequested;
            shellViewModel.WalkthroughRequested -= OnWalkthroughRequested;
            shellViewModel.PhoneControllerRequested -= OnPhoneControllerRequested;
            shellViewModel.OverlayUrlCopyRequested -= OnOverlayUrlCopyRequested;
            shellViewModel.WalkthroughRequested -= OnWalkthroughRequested;
            shellViewModel.PhoneControllerRequested -= OnPhoneControllerRequested;
            shellViewModel.OverlayUrlCopyRequested -= OnOverlayUrlCopyRequested;
        }

        base.OnDataContextChanged(e);
        shellViewModel = DataContext as ShellViewModel;

        if (shellViewModel is not null)
        {
            shellViewModel.ControlMappingRequested += OnControlMappingRequested;
            shellViewModel.DeviceSettingsRequested += OnDeviceSettingsRequested;
            shellViewModel.WalkthroughRequested += OnWalkthroughRequested;
            shellViewModel.PhoneControllerRequested += OnPhoneControllerRequested;
            shellViewModel.OverlayUrlCopyRequested += OnOverlayUrlCopyRequested;
        }
    }

    /// <summary>Sidebar "Setup guide" — opens the walkthrough on demand.</summary>
    private async void OnWalkthroughRequested(object? sender, EventArgs e)
    {
        if (isClosing || services is null)
        {
            return;
        }

        try
        {
            var window = new SetupWalkthroughWindow
            {
                DataContext = services.GetService(typeof(SetupWalkthroughViewModel)),
            };
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Setup walkthrough failed to open from the sidebar.");
        }
    }

    /// <summary>Dashboard "Use a phone" — opens the phone-controller sheet.</summary>
    private async void OnPhoneControllerRequested(object? sender, EventArgs e)
    {
        if (isClosing || services is null)
        {
            return;
        }

        try
        {
            var window = new PhoneControllerWindow
            {
                DataContext = services.GetService(typeof(OverlayPanelViewModel)),
            };
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Phone controller sheet failed to open.");
        }
    }

    /// <summary>
    /// Dashboard "Copy OBS layout" — puts the stream-overlay URL on the
    /// clipboard without a trip through Options.
    /// </summary>
    /// <remarks>
    /// The overlay view-model is built fresh per press rather than held:
    /// it reads the live slot and theme lists on construction, and the
    /// URL names a slot that may have been created since the last press.
    /// </remarks>
    private async void OnOverlayUrlCopyRequested(object? sender, EventArgs e)
    {
        if (services?.GetService(typeof(OverlayPanelViewModel)) is not OverlayPanelViewModel overlay ||
            GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        if (!overlay.CanCopy)
        {
            shellViewModel?.ReportStatus("The web server is not running, so there is no overlay URL yet.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(overlay.Url);
            shellViewModel?.ReportStatus("Overlay URL copied — paste it into an OBS Browser source.");
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Copying the overlay URL from the dashboard failed.");
        }
    }

    /// <summary>
    /// Click on a slot's VIRTUAL panel opens that slot's tuning editor.
    /// The slot id rides on the Border's Tag, so the handler doesn't need
    /// to walk the visual tree to work out which panel was hit.
    /// </summary>
    private void OnVirtualPanelPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string slotId } || string.IsNullOrWhiteSpace(slotId))
        {
            return;
        }

        // Left button only — a right-click here shouldn't hijack any
        // future context menu on the panel.
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        (DataContext as ShellViewModel)?.OpenDeviceSettingsCommand.Execute(slotId);
    }

    private async void OnDeviceSettingsRequested(object? sender, DeviceSettingsRequestedEventArgs e)
    {
        if (isClosing)
        {
            return;
        }

        var window = new DeviceSettingsWindow { DataContext = e.EditorViewModel };
        try
        {
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            // Settings persist on every change, so a failure to SHOW the
            // dialog costs nothing already saved — log and carry on.
            Log.Error(exception, "Device settings window failed to open.");
        }
    }

    private async void OnControlMappingRequested(object? sender, ControlMappingRequestedEventArgs e)
    {
        if (isClosing)
        {
            return;
        }

        var window = new ControlMappingWindow
        {
            DataContext = e.DialogViewModel
        };

        try
        {
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Control mapping window failed to open.");
            e.DialogViewModel.Dispose();
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (!isClosing)
        {
            // Warm-up starts when the window does, not when the app does:
            // this is the point from which the dashboard is competing for
            // the UI thread. See StartupWarmUp.
            warmUpUntilUtc = DateTime.UtcNow + StartupWarmUp;
            warmUpHandedOff = false;
            ApplyConfiguredRefreshRate();
            refreshTimer.Start();
        }
        // Attach the Raw Input reader to this window's HWND so the keyboard
        // + mouse subsystem starts receiving WM_INPUT. No-op off Windows.
        try
        {
            var handle = TryGetPlatformHandle();
            if (handle is not null && shellViewModel is not null)
            {
                shellViewModel.AttachRawInput(handle.Handle);
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ShellWindow>().Warning(ex, "Raw Input attach failed.");
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        isClosing = true;
        refreshTimer.Stop();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        refreshTimer.Stop();
        refreshTimer.Tick -= RefreshTimerOnTick;
        Opened  -= OnOpened;
        Closing -= OnClosing;
        Closed  -= OnClosed;
        if (shellViewModel is not null)
        {
            // Same reason as OnDataContextChanged above: `?.` is not
            // valid for event unsubscription.
            shellViewModel.ControlMappingRequested -= OnControlMappingRequested;
            // Subscribed alongside the above in OnDataContextChanged, so
            // it has to be released here too — otherwise the shell view
            // model keeps this window alive after it closes.
            shellViewModel.DeviceSettingsRequested -= OnDeviceSettingsRequested;
            shellViewModel.WalkthroughRequested -= OnWalkthroughRequested;
            shellViewModel.PhoneControllerRequested -= OnPhoneControllerRequested;
            shellViewModel.OverlayUrlCopyRequested -= OnOverlayUrlCopyRequested;
        }
        shellViewModel = null;

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Backs the UI tick off when the dispatcher cannot keep up.
    ///
    /// <para>
    /// Without this the app can be configured into a state it cannot
    /// recover from. If a repaint costs more than the frame budget, each
    /// tick queues more work than it retires, the dispatcher backs up, and
    /// the window stops responding — the freeze reported after opening a
    /// tab with several controller surfaces on it. Asking for MORE frames
    /// than the machine can paint does not produce more frames; it only
    /// starves input handling.
    /// </para>
    ///
    /// <para>
    /// Backoff is MONOTONE within a session: the rate only ever drops, and
    /// is restored solely when the window reopens or the setting changes.
    /// An earlier version tried to climb back up after a run of fast
    /// frames, which oscillated — it would speed up, immediately
    /// re-saturate, and throttle again. Every one of those transitions
    /// restarts the DispatcherTimer, so the oscillation was itself a
    /// source of the stutter it was supposed to cure. A dashboard that
    /// settles at a lower rate is fine; one that constantly renegotiates
    /// is not.
    /// </para>
    /// </summary>
    private void AdaptTickRate(TimeSpan gap)
    {
        var current = refreshTimer.Interval;
        var nowUtc = DateTime.UtcNow;

        // Overruns during warm-up describe startup, not this machine.
        // Acting on them is what used to leave the dashboard at 10 Hz
        // long after the work that caused it had finished.
        if (nowUtc < warmUpUntilUtc)
        {
            return;
        }

        // Only react to a real overrun — more than double the budget —
        // so ordinary jitter does not trigger a downgrade.
        if (gap > current + current)
        {
            lastOverrunUtc = nowUtc;

            // Floor of 10 Hz. The dashboard is a visualisation: a slow one
            // still works, an unresponsive window does not.
            var slower = TimeSpan.FromMilliseconds(Math.Min(current.TotalMilliseconds * 2, 100));
            if (slower > current)
            {
                refreshTimer.Interval = slower;
                Log.Warning(
                    "Dashboard tick throttled to {Hz:F0} Hz — the UI thread could not keep up at {Was:F0} Hz.",
                    1000d / slower.TotalMilliseconds, 1000d / current.TotalMilliseconds);
            }

            return;
        }

        // Recovery, on a QUIET-PERIOD basis rather than a run of fast
        // frames. An earlier version counted consecutive in-budget ticks
        // and oscillated: it would step up, immediately re-saturate, and
        // step down again, and since every change restarts the
        // DispatcherTimer the renegotiation was itself a source of
        // stutter. Requiring a long stretch with no overrun at all makes
        // stepping up rare and, once taken, usually durable.
        //
        // Recovery has to exist. Without it a single stall during startup
        // — when themes are still loading — pinned the dashboard at 10 Hz
        // for the rest of the session, which reads as permanently choppy
        // even after the cause has passed.
        var wanted = TimeSpan.FromMilliseconds(1000d / Math.Clamp(configuredRefreshHz, 30, 1000));
        if (current <= wanted || nowUtc - lastOverrunUtc < RecoveryQuietPeriod)
        {
            return;
        }

        var faster = TimeSpan.FromMilliseconds(Math.Max(current.TotalMilliseconds / 2, wanted.TotalMilliseconds));
        refreshTimer.Interval = faster;

        // Counts as activity, so the next step up needs another full quiet
        // period rather than following immediately.
        lastOverrunUtc = nowUtc;
        Log.Information("Dashboard tick restored to {Hz:F0} Hz after a quiet period.", 1000d / faster.TotalMilliseconds);
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        // UI-saturation telemetry: this timer wants 33 ms ticks; if the gap
        // between ticks balloons, something (a repaint, a handler) is eating
        // the dispatcher and every interaction lags behind it.
        var nowUtc = DateTime.UtcNow;
        var gap = nowUtc - lastTickUtc;
        lastTickUtc = nowUtc;
        if (gap.TotalMilliseconds > 120 && (nowUtc - lastTickGapWarnUtc).TotalSeconds >= 5)
        {
            lastTickGapWarnUtc = nowUtc;
            Log.Warning(
                "UI thread saturated: {GapMs:F0} ms between {WantedMs:F0} ms dashboard ticks — a repaint or event handler is hogging the dispatcher.",
                gap.TotalMilliseconds, refreshTimer.Interval.TotalMilliseconds);
        }

        AdaptTickRate(gap);

        // Hand off from the warm-up rate to the user's configured one,
        // exactly once, on the first tick after warm-up ends.
        if (!warmUpHandedOff && nowUtc >= warmUpUntilUtc)
        {
            warmUpHandedOff = true;
            ApplyConfiguredRefreshRate();
        }

        if (isClosing || isRefreshing || DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        try
        {
            isRefreshing = true;
            await viewModel.RefreshRuntimeAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)
        {
            refreshTimer.Stop();
        }
        catch (InvalidOperationException exception) when (isClosing)
        {
            Log.Debug(exception, "Dashboard refresh stopped because the shell window is closing.");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Dashboard refresh failed.");
        }
        finally
        {
            isRefreshing = false;
        }
    }
}
