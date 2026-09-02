using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GameFlow.App.ViewModels;
using Serilog;

namespace GameFlow.App.Views;

/// <summary>
/// Focused sheet for turning a phone into a controller.
/// </summary>
/// <remarks>
/// Shares <see cref="OverlayPanelViewModel"/> with the Options dialog
/// rather than duplicating the server state — the address, the copy
/// gating and the firewall diagnosis are all the same facts, and two
/// view-models over one server is how they drift apart.
/// </remarks>
public partial class PhoneControllerWindow : Window
{
    public PhoneControllerWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is OverlayPanelViewModel viewModel)
        {
            viewModel.Refresh();
            _ = viewModel.RefreshReachabilityAsync();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlayPanelViewModel viewModel ||
            GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(viewModel.PhoneControllerUrl);
        }
        catch (Exception exception)
        {
            // The address sits in a read-only box right beside the button,
            // so a clipboard briefly owned by another process still leaves
            // the user able to select it by hand.
            Log.Warning(exception, "Phone controller: copying the address failed.");
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlayPanelViewModel viewModel || !viewModel.CanCopyPhoneControllerUrl)
        {
            return;
        }

        // Only http(s) reaches the shell. The URL is built from the
        // server's own listen address, but routing arbitrary strings to
        // ShellExecute is how that quietly becomes a launch primitive.
        if (!Uri.TryCreate(viewModel.PhoneControllerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Phone controller: opening the address in a browser failed.");
        }
    }
}
