using System.Diagnostics;
using GameFlow.Infrastructure.Updates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;

namespace GameFlow.App.Views;

/// <summary>
/// Choice enum returned by <see cref="UpdateAvailableDialog"/>.
/// </summary>
public enum UpdateDialogChoice
{
    /// <summary>User closed the dialog without choosing — treat as skip-once.</summary>
    Closed,

    /// <summary>"Skip this update" — coordinator writes <c>SkippedUpdateVersion</c>.</summary>
    SkipThisUpdate,

    /// <summary>"Don't ask again" — coordinator clears <c>CheckForUpdatesOnStartup</c>.</summary>
    DontAskAgain,

    /// <summary>"Download &amp; install" — coordinator runs the installer.</summary>
    DownloadAndInstall,
}

/// <summary>
/// Dialog that surfaces an available update and lets the user choose
/// among the three actions specified in the roadmap: skip this update,
/// don't ask again, download &amp; install.
///
/// <para>
/// Code-behind only — no view-model. The hosting coordinator passes
/// the <see cref="UpdateInfo"/> via <see cref="SetUpdate"/> after
/// construction and reads back <see cref="Choice"/> after the dialog
/// closes.
/// </para>
/// </summary>
public partial class UpdateAvailableDialog : Window
{
    private UpdateInfo? update;

    /// <summary>
    /// The user's final choice. Default is <see cref="UpdateDialogChoice.Closed"/>
    /// for the case where the user dismisses the window via the title-bar
    /// close button.
    /// </summary>
    public UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.Closed;

    /// <summary>
    /// Constructs the dialog.
    /// </summary>
    public UpdateAvailableDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Populates the dialog labels and stashes the update so the
    /// install button has something to act on. Call once before
    /// <see cref="Window.ShowDialog"/>.
    /// </summary>
    /// <param name="update">The update to surface.</param>
    /// <param name="currentVersion">The version of the running build.</param>
    public void SetUpdate(UpdateInfo update, Version currentVersion)
    {
        this.update = update ?? throw new ArgumentNullException(nameof(update));

        var versionLine = this.FindControl<TextBlock>("VersionLine");
        if (versionLine is not null)
        {
            versionLine.Text = $"You're running v{currentVersion}. The latest release is {update.TagName}.";
        }

        var releaseName = this.FindControl<TextBlock>("ReleaseName");
        if (releaseName is not null)
        {
            releaseName.Text = update.ReleaseName;
        }

        var releaseNotesLinkText = this.FindControl<TextBlock>("ReleaseNotesLinkText");
        if (releaseNotesLinkText is not null)
        {
            releaseNotesLinkText.Text = update.ReleaseNotesUrl.ToString();
        }
    }

    /// <summary>Reports download progress to the visible progress bar.</summary>
    private void ReportProgress(double fraction)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var area = this.FindControl<StackPanel>("ProgressArea");
            var bar = this.FindControl<ProgressBar>("DownloadProgress");
            var line = this.FindControl<TextBlock>("ProgressLine");

            if (area is not null)
            {
                area.IsVisible = true;
            }
            if (bar is not null)
            {
                bar.Value = Math.Clamp(fraction, 0.0, 1.0);
            }
            if (line is not null)
            {
                line.Text = $"Downloading… {(int)Math.Round(fraction * 100)}%";
            }
        });
    }

    /// <summary>
    /// Opens the release notes URL in the default browser. Failures
    /// are logged at Debug — the dialog stays open.
    /// </summary>
    private void OnReleaseNotesClick(object? sender, RoutedEventArgs e)
    {
        if (update is null)
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = update.ReleaseNotesUrl.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Could not open release notes URL {Url}.", update.ReleaseNotesUrl);
        }
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        Choice = UpdateDialogChoice.SkipThisUpdate;
        Close();
    }

    private void OnDontAskClick(object? sender, RoutedEventArgs e)
    {
        Choice = UpdateDialogChoice.DontAskAgain;
        Close();
    }

    /// <summary>
    /// Locks the install button so the user can't double-click, then
    /// returns the choice. The coordinator (not this dialog) is
    /// responsible for actually running the installer; we only signal
    /// intent. The dialog stays open until the coordinator closes it
    /// via <see cref="ReportProgress"/> + <see cref="Avalonia.Controls.Window.Close()"/>.
    /// </summary>
    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        Choice = UpdateDialogChoice.DownloadAndInstall;

        var installButton = this.FindControl<Button>("InstallButton");
        if (installButton is not null)
        {
            installButton.IsEnabled = false;
            installButton.Content = "Starting download…";
        }

        Close();
    }

    /// <summary>
    /// Allows the coordinator to subscribe to a progress reporter and
    /// have it surfaced in the dialog. Useful when the coordinator
    /// chooses to keep the dialog open during the download — currently
    /// unused (we close the dialog immediately and let the install
    /// run while the shell becomes interactive), but kept for the
    /// future enhancement.
    /// </summary>
    public IProgress<double> CreateProgressSink() => new Progress<double>(ReportProgress);
}
