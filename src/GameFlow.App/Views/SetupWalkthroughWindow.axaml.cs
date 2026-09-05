using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GameFlow.App.ViewModels;

namespace GameFlow.App.Views;

/// <summary>
/// Host window for the first-run walkthrough.
/// </summary>
/// <remarks>
/// Deliberately thin: the window owns no setup logic of its own, so the
/// walkthrough can be driven and tested through
/// <see cref="SetupWalkthroughViewModel"/> without a window existing. All
/// this does is close itself when the view-model says the last step was
/// confirmed, and unsubscribe the view-model from the device catalog on
/// the way out — it listens for hot-plug events, and a wizard that has
/// been closed must not keep a live handler on a long-lived service.
/// </remarks>
public partial class SetupWalkthroughWindow : Window
{
    /// <summary>
    /// Drives the live controller surfaces on the layout-check and review
    /// steps.
    /// </summary>
    /// <remarks>
    /// Owned by the window rather than the view-model so nothing ticks
    /// while the walkthrough is closed, and so the view-model stays
    /// testable without a dispatcher. 30 Hz is enough to see a button
    /// light up under your thumb; the surfaces do their own dirty check,
    /// so a pad at rest repaints nothing.
    /// </remarks>
    private readonly DispatcherTimer previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(33),
    };

    public SetupWalkthroughWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;

        previewTimer.Tick += OnPreviewTick;
        previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (DataContext is SetupWalkthroughViewModel viewModel)
        {
            viewModel.RefreshPreviews();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SetupWalkthroughViewModel viewModel)
        {
            viewModel.Finished += OnFinished;
        }
    }

    private void OnFinished(object? sender, EventArgs e) => Close(true);

    private void OnClosed(object? sender, EventArgs e)
    {
        previewTimer.Stop();
        previewTimer.Tick -= OnPreviewTick;

        if (DataContext is SetupWalkthroughViewModel viewModel)
        {
            viewModel.Finished -= OnFinished;
            viewModel.Dispose();
        }
    }
}
