using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using GameFlow.App.ViewModels;
using Serilog;

// SetTextAsync is an extension in Avalonia 12 — IClipboard itself only
// speaks IAsyncDataTransfer now.

namespace GameFlow.App.Views;

/// <summary>
/// Code-behind for the Options / Settings dialog.
///
/// <para>
/// Owns three small responsibilities:
/// <list type="bullet">
///   <item><description>Wiring the folder pickers (Profiles dir, Logs dir).</description></item>
///   <item><description>Copying phone-controller and OBS URLs through the
///     window-owned clipboard.</description></item>
///   <item><description>Routing the Apply button through the
///     <see cref="SettingsDialogViewModel.ApplyAsync"/> command and
///     closing on success.</description></item>
///   <item><description>Routing the Cancel button to discard pending
///     edits via <see cref="SettingsDialogViewModel.Reload"/> and
///     close.</description></item>
/// </list>
/// </para>
/// </summary>
public partial class SettingsDialog : Window
{
    /// <summary>
    /// XAML loader entry point. The dialog expects its
    /// <see cref="Avalonia.StyledElement.DataContext"/> to be populated with a
    /// <see cref="SettingsDialogViewModel"/> by the caller before
    /// <see cref="Window.ShowDialog"/>.
    /// </summary>
    public SettingsDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>
    /// Kicks off the firewall check once the dialog is on screen.
    /// </summary>
    /// <remarks>
    /// Deliberately fire-and-forget rather than awaited before showing:
    /// the check shells out to netsh, and the dialog should not wait on a
    /// process launch to appear. The banner it feeds is hidden until the
    /// answer arrives, so a slow or failed check simply shows nothing
    /// rather than a wrong reassurance.
    /// </remarks>
    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
        {
            _ = vm.Overlay.RefreshReachabilityAsync();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Detach the VM's <c>CultureChanged</c> subscription when the
    /// dialog closes so the long-lived localization service doesn't
    /// hold this short-lived view-model alive via the event handler.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
        {
            vm.Dispose();
        }
    }

    /// <summary>
    /// Puts the overlay URL on the clipboard.
    ///
    /// <para>
    /// In the code-behind rather than behind a command because the
    /// clipboard hangs off the <see cref="TopLevel"/>, which a view-model
    /// has no handle on. The alternative — a clipboard service injected
    /// into the VM — would be the right shape if anything else needed
    /// one; nothing does, and one button does not justify the seam.
    /// </para>
    /// </summary>
    private async void OnCopyOverlayUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm)
        {
            return;
        }

        await CopyUrlAsync(
            vm.Overlay.Url,
            "Overlay URL copied — paste it into an OBS Browser source.",
            "overlay");
    }

    /// <summary>Puts the phone-controller address on the clipboard.</summary>
    private async void OnCopyPhoneControllerUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm)
        {
            return;
        }

        await CopyUrlAsync(
            vm.Overlay.PhoneControllerUrl,
            "Phone controller address copied — open it in a phone browser.",
            "phone controller");
    }

    /// <summary>
    /// Re-runs the first-run walkthrough on demand.
    /// </summary>
    /// <remarks>
    /// Parented to this dialog rather than the shell, so it behaves like
    /// the modal it is and returns here when closed. The view-model is
    /// resolved through the shell's own service provider: the walkthrough
    /// needs the live device catalog and slot registry, and re-creating
    /// either would give it a private, empty view of the machine.
    /// </remarks>
    private async void OnRunWalkthrough(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm)
        {
            return;
        }

        try
        {
            var walkthrough = vm.CreateWalkthrough();
            var window = new SetupWalkthroughWindow { DataContext = walkthrough };
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Settings: could not open the setup walkthrough.");
            vm.StatusMessage = "The setup walkthrough could not be opened.";
        }
    }

    /// <summary>
    /// Opens the phone-controller page in this PC's default browser.
    /// </summary>
    /// <remarks>
    /// Copying the address was the only thing this section could do, and
    /// a copied URL cannot tell you whether the server is actually
    /// reachable — the usual first symptom is a phone that just times
    /// out. Opening it here answers the "is it serving at all?" half of
    /// that question without involving a second device.
    /// </remarks>
    private void OnOpenPhoneControllerUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && vm.Overlay.CanCopyPhoneControllerUrl)
        {
            LaunchInBrowser(vm.Overlay.PhoneControllerUrl, "phone controller");
        }
    }

    /// <summary>
    /// Opens the built overlay URL in a browser so the streamer can see
    /// what OBS will render before creating the source.
    /// </summary>
    private void OnOpenOverlayUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm && vm.Overlay.CanCopy)
        {
            LaunchInBrowser(vm.Overlay.Url, "overlay");
        }
    }

    /// <summary>
    /// Hands a URL to the shell's default handler.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessStartInfo.UseShellExecute"/> must be true: without
    /// it .NET tries to execute the URL as a program and throws. Only
    /// http/https are passed through — the URLs here are built from the
    /// server's own listen address, but routing arbitrary strings to the
    /// shell is the kind of thing that quietly turns into a launch
    /// primitive, so the scheme is checked at the one place that launches.
    /// </remarks>
    private void LaunchInBrowser(string url, string logLabel)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warning("Settings: refusing to open the {Label} URL {Url} — not an http(s) address.", logLabel, url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            if (DataContext is SettingsDialogViewModel vm)
            {
                vm.StatusMessage = $"Opened {uri.AbsoluteUri} in your browser.";
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Settings: opening the {Label} URL in a browser failed.", logLabel);
            if (DataContext is SettingsDialogViewModel vm)
            {
                vm.StatusMessage = "Could not open a browser. Use Copy and paste the address instead.";
            }
        }
    }

    private async Task CopyUrlAsync(string url, string successMessage, string logLabel)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            Log.Warning("Settings: no clipboard available; the {Label} URL was not copied.", logLabel);
            return;
        }

        try
        {
            await clipboard.SetTextAsync(url);
            if (DataContext is SettingsDialogViewModel vm)
            {
                vm.StatusMessage = successMessage;
            }
        }
        catch (Exception exception)
        {
            // A clipboard owned by another process, which Windows does
            // transiently. The URL is in a read-only text box right next
            // to the button, so the user can still select it by hand.
            Log.Warning(exception, "Settings: copying the {Label} URL failed.", logLabel);
        }
    }

    /// <summary>
    /// Opens a folder picker and writes the chosen path into the
    /// matching textbox via the view-model. The button's <c>Tag</c>
    /// distinguishes which override to set ("profiles" or "logs").
    /// </summary>
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SettingsDialogViewModel vm)
        {
            return;
        }

        var which = button.Tag as string ?? "";

        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = button.Content?.ToString() ?? "Select folder",
            });
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Folder picker failed.");
            return;
        }

        if (folders.Count == 0)
        {
            return;
        }

        var chosen = folders[0].Path.LocalPath;
        if (string.IsNullOrEmpty(chosen))
        {
            return;
        }

        switch (which)
        {
            case "profiles":
                vm.ProfilesDirectoryOverride = chosen;
                break;
            case "logs":
                vm.LogsDirectoryOverride = chosen;
                break;
        }
    }

    /// <summary>
    /// Apply: persist the dialog state via the view-model. Closes only
    /// if the apply succeeded; on failure the status line shows the
    /// error and the dialog stays open so the user can correct things.
    /// </summary>
    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm)
        {
            Close();
            return;
        }

        var ok = await vm.ApplyAsync();
        if (ok)
        {
            Close();
        }
    }

    /// <summary>
    /// Cancel: drop any pending edits by reloading from the persisted
    /// snapshot, then close. <see cref="Button.IsCancel"/> additionally
    /// makes Esc trigger this.
    /// </summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
        {
            vm.Reload();
        }
        Close();
    }
}
