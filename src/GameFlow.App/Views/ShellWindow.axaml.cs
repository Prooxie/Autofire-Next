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

    /// <summary>Consecutive in-budget ticks, used to decide when it is safe to speed back up.</summary>
    private int sustainedFastFrames;
    private ShellViewModel? shellViewModel;
    private bool isRefreshing;
    private bool isClosing;

    public ShellWindow()
    {
        InitializeComponent();

        refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)   // ~30 Hz UI tick
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
    /// </summary>
    private void ApplyConfiguredRefreshRate()
    {
        var hz = shellViewModel?.DashboardRefreshHz ?? 30;

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
    /// Backs the UI tick off when the dispatcher cannot keep up, and
    /// restores it once it can.
    ///
    /// <para>
    /// Without this the app could be configured into a state it could not
    /// recover from. If a repaint costs more than the frame budget, each
    /// tick queues more work than it retires, the dispatcher backs up, and
    /// the window stops responding — which is the freeze that was reported
    /// after opening a tab with several controller surfaces on it. Asking
    /// for MORE frames than the machine can paint does not produce more
    /// frames; it only starves input handling.
    /// </para>
    ///
    /// <para>
    /// The configured rate is treated as a ceiling rather than a promise.
    /// Recovery is deliberately slower than backoff — a single fast frame
    /// should not undo the throttle and start the cycle again.
    /// </para>
    /// </summary>
    private void AdaptTickRate(TimeSpan gap)
    {
        var wanted = TimeSpan.FromMilliseconds(1000d / Math.Clamp(configuredRefreshHz, 30, 1000));
        var current = refreshTimer.Interval;

        // Overran the budget by more than half: halve the rate, to a floor
        // of 10 Hz. The dashboard is a visualisation — a slow one still
        // works, an unresponsive window does not.
        if (gap > current + current)
        {
            var slower = TimeSpan.FromMilliseconds(Math.Min(current.TotalMilliseconds * 2, 100));
            if (slower > current)
            {
                refreshTimer.Interval = slower;
                sustainedFastFrames = 0;
                Log.Warning(
                    "Dashboard tick throttled to {Hz:F0} Hz — the UI thread could not keep up at {Was:F0} Hz. "
                    + "Large theme bitmaps under software rendering are the usual cause.",
                    1000d / slower.TotalMilliseconds, 1000d / current.TotalMilliseconds);
            }

            return;
        }

        if (current <= wanted)
        {
            return;
        }

        // Comfortably inside budget. Require a sustained run before
        // speeding up, so recovery cannot oscillate against backoff.
        if (gap < current)
        {
            sustainedFastFrames++;
        }
        else
        {
            sustainedFastFrames = 0;
        }

        if (sustainedFastFrames >= 120)
        {
            sustainedFastFrames = 0;
            var faster = TimeSpan.FromMilliseconds(Math.Max(current.TotalMilliseconds / 2, wanted.TotalMilliseconds));
            refreshTimer.Interval = faster;
            Log.Information("Dashboard tick restored to {Hz:F0} Hz.", 1000d / faster.TotalMilliseconds);
        }
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
