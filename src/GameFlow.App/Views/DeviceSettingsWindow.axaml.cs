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
    public DeviceSettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (DataContext as DeviceSettingsEditorViewModel)?.ResetAll();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
