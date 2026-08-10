namespace GameFlow.App.ViewModels;

/// <summary>
/// Raised when a virtual controller panel is clicked, asking the shell
/// window to open the per-device tuning editor for that slot. Mirrors
/// <see cref="ControlMappingRequestedEventArgs"/> — the ViewModel layer
/// stays free of window handling, and the View owns actually showing it.
/// </summary>
public sealed class DeviceSettingsRequestedEventArgs(DeviceSettingsEditorViewModel editorViewModel) : EventArgs
{
    public DeviceSettingsEditorViewModel EditorViewModel { get; } = editorViewModel;
}
