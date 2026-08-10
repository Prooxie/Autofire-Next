using System;
using GameFlow.App.ViewModels;
using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Theming;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace GameFlow.App.Views;

/// <summary>
/// Host UserControl that puts a <see cref="ThemeSurface"/> inside the
/// panel chrome (header, variant picker, footer) and keeps it fed.
///
/// <para>
/// Lifecycle:
/// <list type="number">
/// <item>On <c>DataContextChanged</c>, the bound <see cref="ControllerVisualStateViewModel"/>
///   gets the static <see cref="SharedRegistry"/> handed to it via
///   <c>ThemeRegistry</c> — which forces an initial
///   <c>RefreshActiveTheme</c> so the variant ComboBox has something
///   to show.</item>
/// <item>A 33 ms <see cref="DispatcherTimer"/> (≈ 30 Hz) re-pushes the
///   VM's <c>RawSnapshot</c> and re-evaluates <c>ActiveTheme</c>; this is
///   how live button-press art updates reach the surface.</item>
/// <item><see cref="ThemeSurface.Clicked"/> routes through
///   <c>vm.ClickAtCommand</c> so click-to-map shares the same path the
///   programmatic art's <c>SelectElementCommand</c> already uses.</item>
/// </list>
/// </para>
///
/// <para>
/// The registry is a process-wide static for now (it's expensive to
/// scan the themes folder and we don't want each surface duplicating
/// the work). It will move to DI when the rest of the app's services
/// are wired up.
/// </para>
/// </summary>
public partial class ControllerSurface : UserControl
{
    private static readonly ThemeRegistry SharedRegistry = CreateAndRefresh();

    private DispatcherTimer? pollTimer;
    private ControllerVisualStateViewModel? boundViewModel;
    private ThemeSurface? surface;
    private TextBlock? noThemeMessage;

    public ControllerSurface()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Click-to-edit on the VIRTUAL panel. Delegates the decision to the
    /// view model (<see cref="ControllerVisualStateViewModel.RequestEdit"/>),
    /// which no-ops unless this really is an editable virtual panel — so a
    /// stray click on a physical panel can never open an editor for the
    /// wrong thing.
    /// </summary>
    private void OnPanelPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is ControllerVisualStateViewModel viewModel && viewModel.IsEditable)
        {
            viewModel.RequestEdit();
            e.Handled = true;
        }
    }

    /// <summary>Hand cursor on editable panels only, so the affordance matches the behaviour.</summary>
    private void ApplyEditableCursor(ControllerVisualStateViewModel? viewModel)
    {
        if (this.FindControl<Border>("PanelRoot") is { } root)
        {
            root.Cursor = viewModel?.IsEditable == true
                ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                : Avalonia.Input.Cursor.Default;
        }
    }

    private static ThemeRegistry CreateAndRefresh()
    {
        var registry = new ThemeRegistry();
        try
        {
            registry.Refresh();
            Serilog.Log.Information(
                "ControllerSurface theme registry refreshed: {Count} theme(s) found.",
                registry.Themes.Count);
            foreach (var t in registry.Themes)
            {
                Serilog.Log.Information(
                    "  - id='{Id}' style={Style} name='{Name}' dir='{Dir}'",
                    t.Id, t.PreferredStyle, t.DisplayName, t.DirectoryPath);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Initial theme registry refresh failed.");
        }
        return registry;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        surface         = this.FindControl<ThemeSurface>("ThemedSurface");
        noThemeMessage  = this.FindControl<TextBlock>("NoThemeMessage");

        if (surface is not null)
        {
            surface.Clicked += OnSurfaceClicked;
        }

        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        pollTimer.Tick += OnPollTick;
        pollTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (pollTimer is not null)
        {
            pollTimer.Stop();
            pollTimer.Tick -= OnPollTick;
            pollTimer = null;
        }

        if (surface is not null)
        {
            surface.Clicked -= OnSurfaceClicked;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Detach any handlers from a previous binding so the registry
        // doesn't end up wired into two VMs.
        boundViewModel = DataContext as ControllerVisualStateViewModel;

        // Reflect this panel's editability in the cursor as soon as it's
        // bound — otherwise the hand cursor would lag a panel swap.
        ApplyEditableCursor(boundViewModel);

        // Hand the registry over to the VM so its
        // AvailableThemeVariants list populates and SelectedThemeVariant
        // resolves to a real theme. The VM's setter triggers
        // RefreshActiveTheme internally.
        if (boundViewModel is not null && boundViewModel.ThemeRegistry is null)
        {
            boundViewModel.ThemeRegistry = SharedRegistry;
        }
    }

    private void OnSurfaceClicked(object? sender, ThemeClickEventArgs e)
    {
        // Forward the (X, Y) in theme-local coordinates to the VM's
        // hit-tester / SelectElement pipeline.
        var vm = boundViewModel;
        if (vm is null) { return; }

        if (vm.ClickAtCommand.CanExecute(null))
        {
            vm.ClickAtCommand.Execute((e.X, e.Y));
        }
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        var vm = boundViewModel;
        if (vm is null || surface is null) { return; }

        // Skip everything when this surface isn't actually on screen.
        // Hidden panels (e.g. the virtual panel while emulation is off)
        // and collapsed surfaces still receive timer ticks because they
        // remain attached to the visual tree; pushing state to a surface
        // that won't render is pure waste and 30x/sec adds up when
        // several surfaces are alive at once.
        if (!IsEffectivelyVisible) { return; }

        // The VM has already resolved its visual style and theme via
        // RefreshActiveTheme(); we just need to push the result.
        surface.ActiveTheme    = vm.ActiveTheme;
        surface.IsPhysicalView = vm.IsPhysicalView;
        surface.UpdateLightColor(vm.LightColor);
        surface.UpdateState(vm.RawSnapshot);

        if (noThemeMessage is not null)
        {
            noThemeMessage.IsVisible = vm.ActiveTheme is null;
        }
    }
}
