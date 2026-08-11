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

    private ShellViewModel? shellViewModel;
    private bool isRefreshing;
    private bool isClosing;

    public ShellWindow()
    {
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
            Log.Information("Dashboard UI tick set to {Hz} Hz ({Interval:F1} ms).", hz, interval.TotalMilliseconds);
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
        }

        base.OnDataContextChanged(e);
        shellViewModel = DataContext as ShellViewModel;

        if (shellViewModel is not null)
        {
            shellViewModel.ControlMappingRequested += OnControlMappingRequested;
            shellViewModel.DeviceSettingsRequested += OnDeviceSettingsRequested;
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
