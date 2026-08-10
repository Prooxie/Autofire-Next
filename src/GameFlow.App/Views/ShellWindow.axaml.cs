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
                "UI thread saturated: {GapMs:F0} ms between 33 ms dashboard ticks — a repaint or event handler is hogging the dispatcher.",
                gap.TotalMilliseconds);
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
