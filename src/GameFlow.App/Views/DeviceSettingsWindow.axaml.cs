using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GameFlow.App.ViewModels;

namespace GameFlow.App.Views;

/// <summary>
/// Hosts <see cref="DeviceSettingsEditorView"/> as a dialog. Opened by
/// clicking a slot's VIRTUAL controller panel — settings save as they're
/// changed, so this window has no OK/Cancel, just Close.
/// </summary>
public partial class DeviceSettingsWindow : Window
{
    /// <summary>
    /// Drives the live stick marker on the response curves. Owned by the
    /// window rather than the view-model so it starts and stops with the
    /// dialog — a closed editor should not be polling snapshots. 30 Hz is
    /// plenty for a marker the eye is tracking loosely.
    /// </summary>
    private readonly Avalonia.Threading.DispatcherTimer liveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(33)
    };

    public DeviceSettingsWindow()
    {
        InitializeComponent();
        liveTimer.Tick += OnLiveTick;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        liveTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        liveTimer.Stop();
        liveTimer.Tick -= OnLiveTick;
        base.OnClosed(e);
    }

    private void OnLiveTick(object? sender, EventArgs e) =>
        (DataContext as DeviceSettingsEditorViewModel)?.RefreshLive();

    private void OnResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (DataContext as DeviceSettingsEditorViewModel)?.ResetAll();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
