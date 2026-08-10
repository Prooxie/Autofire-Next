using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GameFlow.App.Views;

public partial class DeviceSettingsEditorView : UserControl
{
    public DeviceSettingsEditorView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
