using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GameFlow.App.Services;
using GameFlow.App.Views;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Runtime;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Shell-level view-model.
///
/// Performance contract:
///   Fast path  (every 33 ms tick): update controller visuals, status text,
///              diagnostics text (no longer throttled — cheap string build).
///   Medium path (~0.5 s / 15 ticks): rebuild controller inventory, runtime notes.
///   Slow path  (~3 s / 90 ticks): rebuild JSON snapshots.
///   Event path (profile or culture changed): rebuild rule summary, re-sync selections.
/// </summary>
public sealed class ShellViewModel : ViewModelBase, IDisposable
{
    // Refresh cadence, expressed in UI ticks (the dispatcher fires at ~30 Hz, see ShellWindow.OnOpened).
    //   Fast path  — every tick (~33 ms).  Visible button states + diagnostics text.
    //   Medium     — every 15 ticks (~500 ms).  Controller-inventory rebuild + runtime notes.
    //   Slow       — every 15 ticks (~500 ms).  JSON re-serialisation of the raw snapshots.
    //
    // The JSON path used to fire every 90 ticks (~3 s), which made the "Raw physical/virtual
    // snapshot" debug panes feel frozen. 500 ms keeps them legibly responsive without the
    // serialisation cost showing up in profilers.
    private const int SlowPathEvery = 1;
    private const int MedPathEvery  = 1;
    private int refreshTick;

    private bool ruleSummaryDirty = true;
    private bool aboutTextDirty   = true;

    private readonly ProfileSession profileSession;
    private readonly RuntimeSnapshotStore runtimeSnapshotStore;
    private readonly ILocalizationService localizationService;
    private readonly InputDeviceCatalog inputDeviceCatalog;
    private int rebuildQueued;
    private readonly GameFlow.Infrastructure.Runtime.Input.IRawInputAttacher rawInputAttacher;

    /// <summary>
    /// Called by <see cref="Views.ShellWindow"/> once on Opened to hand
    /// the main-window HWND to the Raw Input reader (so it can subclass
    /// the WndProc and start receiving <c>WM_INPUT</c>). No-op off Windows.
    /// </summary>
    public void AttachRawInput(IntPtr mainWindowHwnd) =>
        rawInputAttacher.AttachToHwnd(mainWindowHwnd);
    private readonly GameFlow.Infrastructure.Runtime.Slots.SlotRegistry slotRegistry;
    private readonly GameFlow.Infrastructure.Runtime.Slots.SlotSnapshotStore slotSnapshotStore;
    private readonly GameFlow.Infrastructure.Runtime.Slots.PhysicalPanelPinService physicalPanelPins;
    private readonly IProfileFileDialogService profileFileDialogService;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<ShellViewModel> logger;
    private readonly IServiceProvider serviceProvider;
    private readonly AppRuntimeOptions runtimeOptions;
    private readonly GameFlow.Infrastructure.Configuration.IUserSettingsService userSettings;

    /// <summary>
    /// Dashboard UI tick rate, in Hz. The per-user setting supersedes the
    /// appsettings.json value, matching the contract documented on
    /// <see cref="GameFlow.Infrastructure.Profiles.AppSettings.DashboardRefreshHz"/>.
    ///
    /// <para>
    /// Resolution order: the user's explicit setting, then an explicit
    /// appsettings.json value, then <b>the display's own refresh rate</b>,
    /// then 60. Following the monitor is the point — drawing 60 frames
    /// into a 144 Hz panel wastes its headroom, and drawing 60 into a
    /// 50 Hz one produces frames nobody sees. A value of 0 in
    /// appsettings.json means "follow the display" rather than being
    /// invalid.
    /// </para>
    ///
    /// <para>
    /// This is a ceiling, not a promise: see
    /// <c>ShellWindow.ApplyConfiguredRefreshRate</c> and its adaptive
    /// backoff. It stopped being 30 because a surface repaint went from
    /// ~19 ms to under 1 ms, and 30 Hz was spending 33 ms of latency to
    /// save work that no longer exists.
    /// </para>
    /// </summary>
    public int DashboardRefreshHz =>
        userSettings.Current.DashboardRefreshHz
        ?? (runtimeOptions.DashboardRefreshHz > 0
            ? runtimeOptions.DashboardRefreshHz
            : GameFlow.App.Platform.DisplayRefreshRate.TryGetPrimaryHz() ?? 60);
    private readonly SemaphoreSlim rulesSaveGate = new(1, 1);
    private readonly SemaphoreSlim panelBackgroundSaveGate = new(1, 1);
    private int panelBackgroundPersistVersion;
    private string? pendingPanelBackgroundBrush;

    private LanguageOption? selectedLanguage;
    private AppThemeOption? selectedTheme;
    private DetectedControllerOption? selectedController;
    private ProfileOption? selectedProfileOption;
    private string providerSummary = string.Empty;
    private string? selectedControlKey;
    private bool isSwitchingProfile;

    /// <summary>
    /// Set <c>true</c> while <see cref="SelectedLanguage"/>'s setter is
    /// running so the <see cref="ProfileSession.Changed"/> event raised
    /// by <see cref="ProfileSession.SetCultureAsync"/> doesn't trigger
    /// the heavy profile-sync chain. The profile didn't actually change,
    /// only the persisted UI culture did, and the sync chain has been
    /// observed to flicker the language combo back to its previous
    /// value.
    /// </summary>
    private bool isChangingCulture;
    private bool disposed;

    /// <summary>
    /// Latest theme-variant id the user picked from the physical
    /// panel's variant ComboBox. Null until they pick something; cleared
    /// after a successful "Apply Dashboard Preferences" save folds it
    /// into the profile.
    /// </summary>
    private string? pendingPhysicalVariantPick;

    /// <summary>
    /// Sibling to <see cref="pendingPhysicalVariantPick"/> for the
    /// virtual panel.
    /// </summary>
    private string? pendingVirtualVariantPick;

    public static string AppVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build} Beta"
            : "v1.0.3 Beta";

    public static string AppFooterText { get; } =
        $"Made by Proxy Darkness  ·  {AppVersion}  ·  © 2026";

    public ShellViewModel(
        ProfileSession profileSession,
        RuntimeSnapshotStore runtimeSnapshotStore,
        ILocalizationService localizationService,
        InputDeviceCatalog inputDeviceCatalog,
        GameFlow.Infrastructure.Runtime.Templates.DeviceTemplateStore deviceTemplateStore,
        GameFlow.Infrastructure.Runtime.Input.ButtonMapStore buttonMapStore,
        GameFlow.Infrastructure.Runtime.Input.IKeyboardStateSource keyboardStateSource,
        GameFlow.Infrastructure.Runtime.Input.IMouseStateSource mouseStateSource,
        GameFlow.Infrastructure.Runtime.Input.IRawInputAttacher rawInputAttacher,
        GameFlow.Infrastructure.Runtime.Slots.SlotRegistry slotRegistry,
        GameFlow.Infrastructure.Runtime.Slots.SlotSnapshotStore slotSnapshotStore,
        GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfileCatalogService hidMaestroCatalog,
        GameFlow.Infrastructure.Runtime.Slots.PhysicalPanelPinService physicalPanelPins,
        DevicesViewModel devicesPanel,
        GameFlow.Infrastructure.Runtime.DeviceCategoryOverrideStore deviceCategoryOverrides,
        GameFlow.Infrastructure.Runtime.DeviceSettingsStore deviceSettingsStore,
        IProfileFileDialogService profileFileDialogService,
        MotionServerPanelViewModel motionServerPanel,
        IOptions<AppRuntimeOptions> runtimeOptions,
        GameFlow.Infrastructure.Configuration.IUserSettingsService userSettings,
        ILoggerFactory loggerFactory,
        ILogger<ShellViewModel> logger,
        IServiceProvider serviceProvider)
    {
        this.userSettings = userSettings;
        MotionServerPanel = motionServerPanel;
        this.profileSession = profileSession;
        this.runtimeSnapshotStore = runtimeSnapshotStore;
        this.localizationService = localizationService;
        this.inputDeviceCatalog = inputDeviceCatalog;
        this.rawInputAttacher = rawInputAttacher;
        this.profileFileDialogService = profileFileDialogService;
        this.runtimeOptions = runtimeOptions.Value;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
        this.serviceProvider = serviceProvider;

        SaveProfileCommand               = new AsyncRelayCommand(SaveProfileAsync);
        ResetProfileCommand              = new AsyncRelayCommand(ResetProfileAsync);
        ApplyDashboardPreferencesCommand = new AsyncRelayCommand(ApplyDashboardPreferencesAsync);
        CreateProfileCommand             = new AsyncRelayCommand(CreateProfileAsync);
        DuplicateProfileCommand          = new AsyncRelayCommand(DuplicateProfileAsync);
        ImportProfileCommand             = new AsyncRelayCommand(ImportProfileAsync);
        ExportProfileCommand             = new AsyncRelayCommand(ExportProfileAsync);
        RenameProfileCommand             = new AsyncRelayCommand(RenameProfileAsync);
        DeleteProfileCommand             = new AsyncRelayCommand(DeleteProfileAsync, CanDeleteProfile);
        OpenControlEditorCommand         = new RelayCommand<string>(OpenControlEditor);
        OpenDeviceSettingsCommand        = new RelayCommand<string>(OpenDeviceSettings);
        OpenSettingsCommand              = new AsyncRelayCommand(OpenSettingsAsync);
        AddVirtualControllerCommand      = new RelayCommand(AddVirtualControllerFromSidebar);
        OpenWalkthroughCommand           = new RelayCommand(() => WalkthroughRequested?.Invoke(this, EventArgs.Empty));
        OpenPhoneControllerCommand       = new RelayCommand(() => PhoneControllerRequested?.Invoke(this, EventArgs.Empty));
        CopyOverlayUrlCommand            = new RelayCommand(() => OverlayUrlCopyRequested?.Invoke(this, EventArgs.Empty));
        OpenVirtualControllerCommand     = new RelayCommand<string>(SelectVirtualMenuItem);
        OpenMappingsCommand              = new RelayCommand(() => OuterNavSelectedIndex = OuterNavProfiles);

        SupportedLanguages     = localizationService.SupportedLanguages;
        ThemeOptions           = CreateThemeOptions();
        InputProviderOptions   = CreateInputProviderOptions();
        OutputProviderOptions  = CreateOutputProviderOptions();
        ControllerStyleOptions = CreateControllerStyleOptions();
        MappingEditor          = new MappingEditorViewModel(loggerFactory.CreateLogger<MappingEditorViewModel>(), localizationService);
        MappingEditor.RulesChanged += OnMappingRulesChanged;
        DevicesPanel           = devicesPanel ?? throw new ArgumentNullException(nameof(devicesPanel));
        SlotsPanel             = new SlotsViewModel(slotRegistry, inputDeviceCatalog, deviceTemplateStore, profileSession, localizationService, hidMaestroCatalog, deviceSettingsStore);

        // Tuning follows the virtual-controller selection rather than
        // keeping a second picker of its own.
        //
        // Tuning only ever edits a virtual controller's slot, so offering
        // its own slot list restated a choice the user had already made one
        // tab over — and the two pickers could disagree, leaving the tuning
        // panel editing a different controller from the one on screen.
        SlotsPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SlotsViewModel.SelectedSlot))
            {
                return;
            }

            var id = SlotsPanel.SelectedSlot?.Id;
            DevicesPanel.SelectedTuningSlot = string.IsNullOrEmpty(id)
                ? null
                : DevicesPanel.TuningSlotOptions.FirstOrDefault(slot => slot.Id == id);
        };

        this.slotRegistry = slotRegistry;
        this.slotSnapshotStore = slotSnapshotStore;
        this.physicalPanelPins = physicalPanelPins ?? throw new ArgumentNullException(nameof(physicalPanelPins));
        physicalPanelPins.PinsChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { RebuildControllerPanels(); RebuildMenuColumn(); });
        slotRegistry.SlotsChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { RebuildControllerPanels(); RebuildMenuColumn(); });
        ControlRuleMatcher.UseLocalizer(localizationService);

        inputDeviceCatalog.Updated += (_, _) =>
        {
            // Coalesce rebuilds: if one is already queued on the UI thread,
            // don't pile on another. A device (re)connect can fire several
            // catalog updates in quick succession; without this gate each one
            // posts a full panel+menu rebuild and the burst can saturate the
            // dispatcher and leave the window "not responding".
            if (System.Threading.Interlocked.Exchange(ref rebuildQueued, 1) == 1)
            {
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                System.Threading.Volatile.Write(ref rebuildQueued, 0);
                RebuildControllerPanels();
                RebuildMenuColumn();
            });
        };

        selectedLanguage = SupportedLanguages
            .FirstOrDefault(l => l.Code == localizationService.CurrentCulture)
            ?? SupportedLanguages.FirstOrDefault();

        selectedTheme = ThemeOptions.FirstOrDefault(t => t.Kind == AppThemeKind.CyberBlue)
            ?? ThemeOptions.FirstOrDefault();

        PhysicalController = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
        PhysicalController.SetPanelKind(isPhysical: true);
        VirtualController  = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
        VirtualController.SetPanelKind(isPhysical: false);
        SlotConfigurationController = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
        SlotConfigurationController.SetPanelKind(isPhysical: false);

        // Initial build — deliberately AFTER PhysicalController /
        // VirtualController above; see the note on the removed early
        // call sites. Event-driven rebuilds are posted to the dispatcher
        // and can't run before the constructor completes.
        RebuildControllerPanels();
        RebuildMenuColumn();

        // Mark each panel's render mode. Physical = base image only
        // (the actual controller model, no live overlays); Virtual =
        // full live render with active button highlights and stick
        // deflection. The flag is read by the ThemeSurface; programmatic
        // XAML art is unaffected.
        PhysicalController.SetPanelKind(isPhysical: true);
        VirtualController.SetPanelKind(isPhysical: false);

        // Remember user theme picks so they survive a profile save.
        // We don't immediately call SaveProfile here — that would be
        // surprising side-effect behaviour; instead we stash the choice
        // in `pendingThemeVariantPick*` fields and ApplyDashboardPreferences
        // folds them into the next profile write. Hitting Apply (or any
        // other profile save) commits the change to disk.
        PhysicalController.ThemeVariantUserSelected += (_, pick) =>
            pendingPhysicalVariantPick = pick?.Id;
        VirtualController.ThemeVariantUserSelected += (_, pick) =>
            pendingVirtualVariantPick = pick?.Id;

        providerSummary = string.Join(
            Environment.NewLine,
            ProviderCatalog.KnownProviders.Select(p =>
                $"- {p.DisplayName}: {(p.IsImplemented ? "available" : "planned")} — {p.Notes}"));

        localizationService.CultureChanged += OnCultureChanged;
        profileSession.Changed             += OnProfileChanged;
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    public IAsyncRelayCommand SaveProfileCommand               { get; }
    public IAsyncRelayCommand ResetProfileCommand              { get; }
    public IAsyncRelayCommand ApplyDashboardPreferencesCommand { get; }
    public IAsyncRelayCommand CreateProfileCommand             { get; }
    public IAsyncRelayCommand DuplicateProfileCommand          { get; }
    public IAsyncRelayCommand ImportProfileCommand             { get; }
    public IAsyncRelayCommand ExportProfileCommand             { get; }
    public IAsyncRelayCommand RenameProfileCommand             { get; }
    public IAsyncRelayCommand DeleteProfileCommand             { get; }
    public IAsyncRelayCommand OpenSettingsCommand              { get; }

    /// <summary>Sidebar "+ Add controller": creates a slot and jumps to its editor.</summary>
    public IRelayCommand AddVirtualControllerCommand { get; }

    /// <summary>Sidebar "Setup guide": opens the walkthrough on demand.</summary>
    public IRelayCommand OpenWalkthroughCommand { get; }

    /// <summary>Dashboard "Use a phone": opens the phone-controller sheet.</summary>
    public IRelayCommand OpenPhoneControllerCommand { get; }

    /// <summary>Raised when the sidebar asks for the setup walkthrough.</summary>
    public event EventHandler? WalkthroughRequested;

    /// <summary>Raised when the user asks to add a phone as a controller.</summary>
    public event EventHandler? PhoneControllerRequested;

    /// <summary>
    /// Raised when the dashboard's OBS button is pressed.
    /// </summary>
    /// <remarks>
    /// An event rather than the view-model doing the copy itself: the
    /// clipboard hangs off a TopLevel, which only the window has.
    /// </remarks>
    public event EventHandler? OverlayUrlCopyRequested;

    /// <summary>Copies this session's stream-overlay URL to the clipboard.</summary>
    public IRelayCommand CopyOverlayUrlCommand { get; }

    /// <summary>
    /// Puts a message on the shell's status line.
    /// </summary>
    /// <remarks>
    /// <see cref="StatusText"/> itself is set only from inside this
    /// view-model; this is the seam for the window, which owns the
    /// clipboard and so is the one place that knows whether a copy
    /// actually succeeded.
    /// </remarks>
    public void ReportStatus(string message) => StatusText = message;

    public string SidebarWalkthroughLabel => Localized("SidebarWalkthrough", "Setup guide");
    public string SidebarPhoneControllerLabel => Localized("SidebarPhoneController", "Use a phone");
    public IRelayCommand<string> OpenVirtualControllerCommand { get; }
    public IRelayCommand OpenMappingsCommand { get; }

    public string SidebarAddControllerLabel => Localized("SidebarAddController", "Add controller");

    private void AddVirtualControllerFromSidebar()
    {
        SlotsPanel.CreateSlotFromSidebar();
        OuterNavSelectedIndex = OuterNavDevices;
        DevicesSubTabIndex = DevicesSubTabVirtual;
    }
    public IRelayCommand<string> OpenControlEditorCommand      { get; }

    /// <summary>Opens the per-device tuning editor for a slot. Parameter is the slot id.</summary>
    public IRelayCommand<string> OpenDeviceSettingsCommand     { get; }

    public event EventHandler<ControlMappingRequestedEventArgs>? ControlMappingRequested;

    /// <summary>Raised when a virtual panel is clicked; the shell window shows the dialog.</summary>
    public event EventHandler<DeviceSettingsRequestedEventArgs>? DeviceSettingsRequested;

    // ─── Collections ──────────────────────────────────────────────────────────

    public IReadOnlyList<LanguageOption> SupportedLanguages    { get; }
    public IReadOnlyList<AppThemeOption> ThemeOptions
    {
        get => themeOptions;
        private set => SetProperty(ref themeOptions, value);
    }
    public IReadOnlyList<InputProviderOption> InputProviderOptions
    {
        get => inputProviderOptions;
        private set => SetProperty(ref inputProviderOptions, value);
    }
    public IReadOnlyList<OutputProviderOption> OutputProviderOptions
    {
        get => outputProviderOptions;
        private set => SetProperty(ref outputProviderOptions, value);
    }
    public IReadOnlyList<ControllerStyleOption> ControllerStyleOptions
    {
        get => controllerStyleOptions;
        private set => SetProperty(ref controllerStyleOptions, value);
    }

    // Backing fields for the rebuilt-on-culture-change option lists above.
    // Kept private so the only writes go through the property setters.
    private IReadOnlyList<AppThemeOption>          themeOptions          = [];
    private IReadOnlyList<InputProviderOption>     inputProviderOptions  = [];
    private IReadOnlyList<OutputProviderOption>    outputProviderOptions = [];
    private IReadOnlyList<ControllerStyleOption>   controllerStyleOptions = [];

    public IReadOnlyList<DetectedControllerOption> AvailableControllers
    {
        get; private set => SetProperty(ref field, value);
    } = [];

    public IReadOnlyList<ProfileOption> AvailableProfiles
    {
        get; private set => SetProperty(ref field, value);
    } = [];

    public IReadOnlyList<ControlConfigurationCardViewModel> ControlConfigurationCards
    {
        get; private set => SetProperty(ref field, value);
    } = [];

    public MappingEditorViewModel MappingEditor { get; }

    /// <summary>View-model backing the Devices tab (device discovery surface).</summary>
    public DevicesViewModel DevicesPanel { get; }

    public SlotsViewModel SlotsPanel { get; }

    /// <summary>
    /// Backs the Dashboard's DSU / Cemuhook motion-server section. Refreshed
    /// from this view-model's medium tick; see <see cref="MotionServerPanelViewModel"/>.
    /// </summary>
    public MotionServerPanelViewModel MotionServerPanel { get; }
    public ControllerVisualStateViewModel PhysicalController { get; }
    public ControllerVisualStateViewModel VirtualController  { get; }
    public ControllerVisualStateViewModel SlotConfigurationController { get; }

    /// <summary>Per-slot live controller panels shown on the dashboard.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DashboardControllerPanelViewModel> ControllerPanels { get; } = [];

    public bool HasControllerPanels => ControllerPanels.Count > 0;
    public bool HasNoControllerPanels => ControllerPanels.Count == 0;

    // ─── Compact menu column ─────────────────────────────────────────

    /// <summary>Index of the active outer tab (0=Dashboard, 1=Profiles, 2=Devices).</summary>
    public const int OuterNavDashboard = 0;
    /// <inheritdoc cref="OuterNavDashboard"/>
    public const int OuterNavProfiles = 1;
    /// <inheritdoc cref="OuterNavDashboard"/>
    public const int OuterNavDevices = 2;

    /// <summary>
    /// Positions of the Devices page's inner tabs, in declaration order.
    /// </summary>
    /// <remarks>
    /// Named rather than written inline at each call site. When the
    /// Tuning tab was inserted between Physical and Virtual, every
    /// "jump to the slot editor" path kept its literal 1 and silently
    /// started landing on Tuning instead. These indices count declared
    /// tabs, not visible ones, so <see cref="DevicesSubTabVirtual"/>
    /// stays correct even while Tuning is hidden.
    /// </remarks>
    public const int DevicesSubTabPhysical = 0;
    /// <inheritdoc cref="DevicesSubTabPhysical"/>
    public const int DevicesSubTabTuning = 1;
    /// <inheritdoc cref="DevicesSubTabPhysical"/>
    public const int DevicesSubTabVirtual = 2;

    private int outerNavSelectedIndex;
    public int OuterNavSelectedIndex
    {
        get => outerNavSelectedIndex;
        set => SetProperty(ref outerNavSelectedIndex, value);
    }

    /// <summary>Index of the Devices inner sub-tab; see the DevicesSubTab* constants.</summary>
    private int devicesSubTabIndex;
    public int DevicesSubTabIndex
    {
        get => devicesSubTabIndex;
        set => SetProperty(ref devicesSubTabIndex, value);
    }

    /// <summary>Physical devices currently in the catalog (sidebar rows).</summary>
    public ObservableCollection<MenuColumnItemViewModel> PhysicalMenuItems { get; } = [];

    /// <summary>Configured virtual-controller slots (sidebar rows).</summary>
    public ObservableCollection<MenuColumnItemViewModel> VirtualMenuItems { get; } = [];

    /// <summary>
    /// Replaces <paramref name="target"/>'s contents with
    /// <paramref name="fresh"/>, touching only the rows that actually
    /// differ.
    ///
    /// <para>
    /// This is the fix for the navigation column blinking under the
    /// cursor. The menu is rebuilt whenever the device catalog or a slot
    /// changes — battery readings and slot status alone are enough to
    /// trigger it repeatedly — and the old code cleared the collection and
    /// re-added everything. <c>Clear()</c> tears down every container in
    /// the ItemsControl, so the row under the pointer was destroyed and
    /// recreated: it vanished for a frame and came back without its hover
    /// state, which is exactly the reported flicker.
    /// </para>
    ///
    /// <para>
    /// Comparing <see cref="MenuColumnItemViewModel.Signature"/> means a
    /// rebuild triggered by an unrelated row — or by nothing visible at
    /// all — leaves every other container alive and untouched.
    /// </para>
    /// </summary>
    private static void ReconcileMenu(
        ObservableCollection<MenuColumnItemViewModel> target,
        List<MenuColumnItemViewModel> fresh)
    {
        // Overwrite in place where the row genuinely changed.
        for (var i = 0; i < fresh.Count && i < target.Count; i++)
        {
            if (!string.Equals(target[i].Signature, fresh[i].Signature, StringComparison.Ordinal))
            {
                target[i] = fresh[i];
            }
        }

        // Trim surplus from the end, so removing one row does not
        // reshuffle the ones above it.
        while (target.Count > fresh.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var i = target.Count; i < fresh.Count; i++)
        {
            target.Add(fresh[i]);
        }
    }

    private void RebuildMenuColumn()
    {
        var physical = new List<MenuColumnItemViewModel>();
        foreach (var device in inputDeviceCatalog.Devices)
        {
            var icon = device.Category switch
            {
                DeviceCategory.Gamepad  => "🎮",
                DeviceCategory.Joystick => "🕹",
                DeviceCategory.Keyboard => "⌨",
                DeviceCategory.Mouse    => "🖱",
                _                       => "■",
            };
            var capturedId = device.Id;
            var deviceType = device.Category switch
            {
                DeviceCategory.Gamepad => "Gamepad",
                DeviceCategory.Joystick => "Controller / HID",
                DeviceCategory.Keyboard => "Keyboard",
                DeviceCategory.Mouse => "Mouse",
                _ => "Input device",
            };
            physical.Add(new MenuColumnItemViewModel(
                device.Id, device.DisplayName, icon, isConnected: true,
                onSelect: () => SelectPhysicalMenuItem(capturedId),
                isPinned: physicalPanelPins.IsPinned(capturedId),
                onTogglePin: () => physicalPanelPins.TogglePin(capturedId),
                secondaryText: deviceType,
                batteryPercentage: device.BatteryPercentage,
                batteryState: device.BatteryState));
        }

        ReconcileMenu(PhysicalMenuItems, physical);

        var virtualItems = new List<MenuColumnItemViewModel>();
        foreach (var slot in slotRegistry.GetSlots())
        {
            var name = string.IsNullOrWhiteSpace(slot.Name) ? "(unnamed)" : slot.Name;
            var capturedId = slot.Id;
            var row = SlotsPanel.Slots.FirstOrDefault(item => item.Id == slot.Id);
            virtualItems.Add(new MenuColumnItemViewModel(
                slot.Id, name, "▣", isConnected: slot.Enabled,
                onSelect: () => SelectVirtualMenuItem(capturedId),
                secondaryText: row is null
                    ? SlotsViewModel.KindLabelFor(slot.OutputTemplate)
                    : $"{row.KindLabel} · {row.StatusLabel}"));
        }

        ReconcileMenu(VirtualMenuItems, virtualItems);
    }

    private void SelectPhysicalMenuItem(string deviceId)
    {
        OuterNavSelectedIndex = OuterNavDevices;
        DevicesSubTabIndex = DevicesSubTabPhysical;
        var row = DevicesPanel.Devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
        if (row is not null)
        {
            DevicesPanel.SelectedDevice = row;
        }
    }

    private void SelectVirtualMenuItem(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return;
        }

        OuterNavSelectedIndex = OuterNavDevices;
        DevicesSubTabIndex = DevicesSubTabVirtual;
        var row = SlotsPanel.Slots.FirstOrDefault(s => string.Equals(s.Id, slotId, StringComparison.Ordinal));
        if (row is not null)
        {
            SlotsPanel.SelectedSlot = row;
        }
    }

    /// <summary>"VIRTUAL" badge shown on every virtual/output panel, top-level and per-slot alike.</summary>
    public string VirtualBadgeLabel => Localized("DashboardVirtualBadgeLabel", "VIRTUAL");

    public string NoControllerConnectedLabel => Localized("DashboardNoControllerConnected", "No controller assigned");

    /// <summary>
    /// The dashboard style derived from the first enabled slot's output
    /// controller, or null when no slot is enabled. Recomputed in
    /// <see cref="RebuildControllerPanels"/> (which already runs on every
    /// SlotsChanged) so the per-tick refresh never touches the registry.
    /// This is what makes the theme follow the selected output virtual
    /// controller: pick a DualSense output and the Auto virtual panel
    /// renders the PS5 silhouette with its skin variants, pick Xbox 360
    /// and it renders the Xbox layout.
    /// </summary>
    private ControllerVisualStyle? slotDerivedVirtualStyle;

    /// <summary>
    /// Resolves a slot's output template to the dashboard style that
    /// represents it: the explicit HIDMaestro catalog profile's family
    /// when one is chosen (any of the 225 profiles classify), the
    /// template's kind otherwise.
    /// </summary>
    /// <summary>
    /// Controller art for a slot's physical side, from the catalog rather
    /// than from frames. See the note on
    /// <see cref="DashboardControllerPanelViewModel.PhysicalStyle"/>.
    /// </summary>
    private ControllerVisualStyle ResolveSlotPhysicalStyle(
        GameFlow.Infrastructure.Runtime.Slots.ControllerSlot slot)
    {
        foreach (var deviceId in slot.InputDeviceIds)
        {
            var device = inputDeviceCatalog.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
            {
                continue;
            }

            var style = GameFlow.Core.Models.ControllerHardwareCatalog.Resolve(
                device.VendorId, device.ProductId);
            if (style != ControllerVisualStyle.Auto)
            {
                return style;
            }
        }

        // Nothing assigned yet, or hardware the catalog does not know.
        // Auto still resolves once a frame arrives, so this is a head
        // start rather than a replacement for it.
        return ControllerVisualStyle.Auto;
    }

    private static ControllerVisualStyle ResolveSlotVirtualStyle(GameFlow.Infrastructure.Runtime.Slots.ControllerSlot slot)
    {
        var template = slot.OutputTemplate;
        var family = string.IsNullOrWhiteSpace(template.OutputProfileId)
            ? template.OutputKind
            : GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.ClassifyFamily(template.OutputProfileId);
        return GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.ResolveVisualStyle(family);
    }

    private void RebuildControllerPanels()
    {
        var connected = inputDeviceCatalog.Devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var allEnabled = slotRegistry.GetSlots().Where(s => s.Enabled).ToList();
        slotDerivedVirtualStyle = allEnabled.Count > 0
            ? ResolveSlotVirtualStyle(allEnabled[0])
            : null;

        var slots = allEnabled
            .Where(s => s.InputDeviceIds.Any(connected.Contains) || s.OutputTemplate.DemoPreview)
            .ToList();

        // Pinned layout-only panels live behind a "phys:" id prefix so
        // slot ids and device ids can never collide in the panel list.
        var pinnedIds = physicalPanelPins.GetPinnedDeviceIds()
            .Where(connected.Contains)
            .ToList();

        // Remove panels whose slot vanished/was disabled, or whose pin
        // was removed / device disconnected.
        for (int i = ControllerPanels.Count - 1; i >= 0; i--)
        {
            var panel = ControllerPanels[i];
            var stale = panel.IsPhysicalOnly
                ? !pinnedIds.Contains(panel.SlotId[5..])
                : slots.All(s => s.Id != panel.SlotId);
            if (stale)
            {
                ControllerPanels.RemoveAt(i);
            }
        }

        // Add/update in slot order.
        for (int idx = 0; idx < slots.Count; idx++)
        {
            var slot = slots[idx];
            var existing = ControllerPanels.FirstOrDefault(p => p.SlotId == slot.Id);
            if (existing is null)
            {
                var physicalVisual = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
                physicalVisual.SetPanelKind(isPhysical: true);
                var virtualVisual = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
                virtualVisual.SetPanelKind(isPhysical: false);

                // New panels start on whatever background the dashboard
                // already has selected, so they match the top-level pair
                // and each other from the first frame instead of popping
                // to a default and then jumping when the picker is next
                // touched.
                var currentBackground = PhysicalController?.PanelBackgroundBrush;
                if (currentBackground is not null)
                {
                    physicalVisual.PanelBackgroundBrush = currentBackground;
                    virtualVisual.PanelBackgroundBrush = currentBackground;
                }

                var panel = new DashboardControllerPanelViewModel(
                    slot.Id, slot.Name, physicalVisual, virtualVisual, VirtualBadgeLabel)
                {
                    LightColor = LightColorForSlot(slot),
                    VirtualStyle = ResolveSlotVirtualStyle(slot),
                    PhysicalStyle = ResolveSlotPhysicalStyle(slot),
                    IsDemoPreview = slot.OutputTemplate.DemoPreview,
                };
                ControllerPanels.Insert(Math.Min(idx, ControllerPanels.Count), panel);
            }
            else
            {
                existing.Title = slot.Name;
                existing.LightColor = LightColorForSlot(slot);
                existing.VirtualStyle = ResolveSlotVirtualStyle(slot);
                existing.PhysicalStyle = ResolveSlotPhysicalStyle(slot);
                existing.IsDemoPreview = slot.OutputTemplate.DemoPreview;
                int cur = ControllerPanels.IndexOf(existing);
                if (cur != idx && idx < ControllerPanels.Count)
                {
                    ControllerPanels.Move(cur, idx);
                }
            }
        }

        // Pinned layout-only panels follow after the slot panels.
        foreach (var deviceId in pinnedIds)
        {
            var panelId = "phys:" + deviceId;
            var existing = ControllerPanels.FirstOrDefault(p => p.SlotId == panelId);
            var deviceName = inputDeviceCatalog.Devices
                .FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))?.DisplayName ?? deviceId;
            if (existing is null)
            {
                var physicalVisual = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
                physicalVisual.SetPanelKind(isPhysical: true);
                var pinnedBackground = PhysicalController?.PanelBackgroundBrush;
                if (pinnedBackground is not null)
                {
                    physicalVisual.PanelBackgroundBrush = pinnedBackground;
                }
                var virtualVisual = new ControllerVisualStateViewModel(OnControllerElementSelected, localizationService);
                virtualVisual.SetPanelKind(isPhysical: false);
                ControllerPanels.Add(new DashboardControllerPanelViewModel(
                    panelId, deviceName, physicalVisual, virtualVisual, VirtualBadgeLabel)
                {
                    IsPhysicalOnly = true,
                });
            }
            else
            {
                existing.Title = deviceName;
            }
        }

        OnPropertyChanged(nameof(HasControllerPanels));
        OnPropertyChanged(nameof(HasNoControllerPanels));
    }

    private static string LightColorForSlot(GameFlow.Infrastructure.Runtime.Slots.ControllerSlot slot)
    {
        var t = slot.OutputTemplate;
        return t.LightingEnabled
            ? $"#FF{t.LightR:X2}{t.LightG:X2}{t.LightB:X2}"
            : "#00000000";
    }

    // ─── Observable properties ────────────────────────────────────────────────

    public string WindowTitle
    {
        get; private set => SetProperty(ref field, value);
    } = "GameFlow";

    public string StatusText
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string PhysicalStateJson
    {
        get; private set => SetProperty(ref field, value);
    } = "{}";

    public string VirtualStateJson
    {
        get; private set => SetProperty(ref field, value);
    } = "{}";

    public string ProfileJson
    {
        get; set => SetProperty(ref field, value);
    } = "{}";

    public string AboutText
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string RuleSummary
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string ProviderSummary
    {
        get => providerSummary;
        private set => SetProperty(ref providerSummary, value);
    }

    public string SelectedControlTitle
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string SelectedControlValue
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string SelectedControlRules
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string SelectedControlHint
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string RuntimeNotesText
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string ControllerInventoryText
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string ProviderStatusText
    {
        get; private set => SetProperty(ref field, value);
    } = string.Empty;

    public string ProfileName
    {
        get; set => SetProperty(ref field, value);
    } = string.Empty;

    // ─── Localised label properties ───────────────────────────────────────────

    public string DashboardTabLabel              => localizationService["DashboardTab"];
    public string ProfilesTabLabel               => localizationService["ProfilesTab"];
    public string DevicesTabLabel                => localizationService["DevicesTab"];
    public string DiagnosticsTabLabel            => localizationService["DiagnosticsTab"];
    public string LanguageLabel                  => localizationService["LanguageLabel"];
    public string ThemeLabel                     => localizationService["ThemeLabel"];
    public string OpenSettingsButtonLabel        => localizationService["OpenSettingsButtonLabel"];
    public string SaveProfileButtonLabel         => localizationService["SaveProfileButton"];
    public string ResetProfileButtonLabel        => localizationService["ResetProfileButton"];
    public string PhysicalInputLabel             => localizationService["PhysicalInputLabel"];
    public string VirtualOutputLabel             => localizationService["VirtualOutputLabel"];
    public string RuleSummaryLabel               => localizationService["RuleSummaryLabel"];
    public string ProfileEditorLabel             => localizationService["ProfileEditorLabel"];
    public string DiagnosticsLabel               => localizationService["DiagnosticsLabel"];
    public string DashboardSettingsLabel         => localizationService["DashboardSettingsLabel"];
    public string DashboardSubtitle              => localizationService["DashboardSubtitle"];
    public string InputProviderLabel             => localizationService["InputProviderLabel"];
    public string OutputProviderLabel            => localizationService["OutputProviderLabel"];
    public string ControllerLabel                => localizationService["ControllerLabel"];
    public string ControllerInventoryLabel       => localizationService["ControllerInventoryLabel"];
    public string ProviderStatusLabel            => localizationService["ProviderStatusLabel"];
    public string PollingRateLabel               => localizationService["PollingRateLabel"];
    public string PhysicalStyleLabel             => localizationService["PhysicalStyleLabel"];
    public string VirtualStyleLabel              => localizationService["VirtualStyleLabel"];
    public string ApplyDashboardSettingsButtonLabel => localizationService["ApplyDashboardSettingsButtonLabel"];
    public string SelectedControlLabel           => localizationService["SelectedControlLabel"];
    public string RuntimeNotesLabel              => localizationService["RuntimeNotesLabel"];
    public string RawPhysicalLabel               => localizationService["RawPhysicalLabel"];
    public string RawVirtualLabel                => localizationService["RawVirtualLabel"];
    public string ProfileComboLabel              => localizationService["ProfileComboLabel"];
    public string ProfileToolbarSubtitle         => localizationService["ProfileToolbarSubtitle"];
    public string ProfileNameLabel               => localizationService["ProfileNameLabel"];
    public string ProfileNameWatermark           => localizationService["ProfileNameWatermark"];
    public string RenameProfileButtonLabel       => localizationService["RenameProfileButton"];
    public string CreateProfileButtonLabel       => localizationService["CreateProfileButton"];
    public string DuplicateProfileButtonLabel    => localizationService["DuplicateProfileButton"];
    public string ImportProfileButtonLabel       => localizationService["ImportProfileButton"];
    public string ExportProfileButtonLabel       => localizationService["ExportProfileButton"];
    public string DeleteProfileButtonLabel       => localizationService["DeleteProfileButton"];
    public string MappingOverviewLabel           => localizationService["MappingOverviewLabel"];
    public string MappingOverviewSubtitle        => localizationService["MappingOverviewSubtitle"];
    public string OpenControlEditorButtonLabel   => localizationService["OpenControlEditorButton"];
    public string NoMappingCardsText             => localizationService["NoMappingCardsText"];

    public string SelectedInputProviderSummary =>
        SelectedInputProvider?.Label ?? "—";

    public string SelectedOutputProviderSummary =>
        SelectedOutputProvider?.Label ?? "—";

    public string SelectedControllerSummary =>
        SelectedController is null || string.IsNullOrWhiteSpace(SelectedController.Id)
            ? localizationService["AutomaticControllerSelection"]
            : SelectedController.Label;

    public string SelectedPollingRateText => $"{SelectedPollingRateHz:0} Hz";
    public string SelectedProfileSummary  => SelectedProfileOption?.Description ?? string.Empty;

    public bool HasControlConfigurationCards   => ControlConfigurationCards.Count > 0;
    public bool HasNoControlConfigurationCards => !HasControlConfigurationCards;

    // ─── Mutable selection properties ─────────────────────────────────────────

    public LanguageOption? SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            if (!SetProperty(ref selectedLanguage, value) || value is null)
            {
                return;
            }

            // Guard the OnProfileChanged handler so the Changed event
            // fired by SetCultureAsync (after its async settings save
            // completes) doesn't run the profile-sync chain — which has
            // been observed to flicker the language combo back to its
            // previous value during a culture change. The flag is held
            // until the async write completes; the OnProfileChanged
            // handler checks it before doing any profile sync work.
            isChangingCulture = true;
            localizationService.SetCulture(value.Code);
            var pending = profileSession.SetCultureAsync(value.Code);
            RefreshLocalizedText();

            _ = pending.ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    isChangingCulture = false;

                    // Belt-and-braces: re-pin SelectedLanguage on the
                    // binding after the refresh chain has completed, so
                    // even if some other path attempted to push back
                    // the previous value the combo box re-syncs to what
                    // we actually picked.
                    OnPropertyChanged(nameof(SelectedLanguage));
                });
            }, TaskScheduler.Default);
        }
    }

    public AppThemeOption? SelectedTheme
    {
        get => selectedTheme;
        set
        {
            if (!SetProperty(ref selectedTheme, value) || value is null)
            {
                return;
            }

            AppThemeService.Apply(value.Kind);

            // Persist the theme choice to the active profile immediately so
            // a subsequent profile-changed event (e.g. raised by a culture
            // change via ProfileSession.SetCultureAsync) doesn't revert
            // the user's pick. Mirrors SelectedLanguage, which has the same
            // self-persist contract via the localization service.
            var profile = profileSession.CurrentProfile;
            var newKind = value.Kind.ToString();

            // CRITICAL: fold in any pending variant picks too. This save
            // fires ProfileChanged → OnProfileChanged → SyncDashboard →
            // ApplyVariantPreferenceForStyle, which re-reads the per-style
            // variant ids straight out of profile.Ui. The user's most
            // recent skin pick lives only in the pending* fields until the
            // next full Apply, so if we write Theme alone the round-trip
            // restores the OLD variant id and the skin appears to "revert"
            // the instant you change the app colour theme. Merging the
            // pending picks into the same write keeps the skin stable.
            var mergedUi = profile.Ui with { Theme = newKind };
            if (SelectedPhysicalStyle is not null)
            {
                mergedUi = MergeVariantPick(mergedUi, SelectedPhysicalStyle.Style, pendingPhysicalVariantPick);
            }
            if (SelectedVirtualStyle is not null)
            {
                mergedUi = MergeVariantPick(mergedUi, SelectedVirtualStyle.Style, pendingVirtualVariantPick);
            }

            var themeChanged   = !string.Equals(profile.Ui.Theme, newKind, StringComparison.OrdinalIgnoreCase);
            var variantChanged = !ReferenceEquals(mergedUi, profile.Ui) && !UiVariantsEqual(mergedUi, profile.Ui);

            if (themeChanged || variantChanged)
            {
                var updated = profile with { Ui = mergedUi };
                _ = profileSession.SaveCurrentProfileAsync(updated);

                // Picks are now committed; clear the pending slots so a
                // later partial save doesn't re-stamp a stale value.
                pendingPhysicalVariantPick = null;
                pendingVirtualVariantPick  = null;
            }
        }
    }

    /// <summary>
    /// Compares only the per-style variant id fields of two
    /// <see cref="UiPreferences"/> records, so the <see cref="SelectedTheme"/>
    /// setter can tell whether a merge actually changed a variant id
    /// (and therefore whether a save is warranted).
    /// </summary>
    private static bool UiVariantsEqual(UiPreferences a, UiPreferences b) =>
        string.Equals(a.DualSenseVariantId,  b.DualSenseVariantId,  StringComparison.Ordinal) &&
        string.Equals(a.DualShock4VariantId, b.DualShock4VariantId, StringComparison.Ordinal) &&
        string.Equals(a.DualShock3VariantId, b.DualShock3VariantId, StringComparison.Ordinal) &&
        string.Equals(a.XboxVariantId,       b.XboxVariantId,       StringComparison.Ordinal);

    public InputProviderOption? SelectedInputProvider
    {
        get; set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedInputProviderSummary));
        }
    }

    public OutputProviderOption? SelectedOutputProvider
    {
        get; set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedOutputProviderSummary));
            OnPropertyChanged(nameof(IsEmulationActive));
            OnPropertyChanged(nameof(IsEmulationInactive));
        }
    }

    /// <summary>
    /// Background-colour preset for the controller panels. Each
    /// option carries both the human-readable label and the
    /// Avalonia-parseable brush string the panel actually applies.
    /// </summary>
    /// <summary>Header for the dashboard background preset picker.</summary>
    public string DashboardBackgroundLabel => Localized("DashboardBackgroundLabel", "Background");

    public string SidebarPhysicalHeader     => Localized("SidebarPhysicalHeader",     "PHYSICAL DEVICES");
    public string SidebarVirtualHeader      => Localized("SidebarVirtualHeader",      "VIRTUAL CONTROLLERS");
    public string DevicesPhysicalTabHeader  => Localized("DevicesPhysicalTabHeader",  "Physical devices");
    public string DevicesVirtualTabHeader   => Localized("DevicesVirtualTabHeader",   "Virtual controllers");

    public sealed record PanelBackgroundOption(string Label, string BrushValue);

    /// <summary>
    /// Canonical presets exposed by the dashboard ComboBox. Chroma green
    /// and chroma blue are the standard "key colour" hex values used by
    /// OBS Studio and most chroma-key workflows; setting one of those
    /// lets streamers mask the controller panel onto a webcam feed
    /// without picking colours by hand.
    /// </summary>
    public IReadOnlyList<PanelBackgroundOption> PanelBackgroundOptions =>
    [
        new(Localized("PanelBackgroundThemeDefault", "Theme default"), ""),
        new(Localized("PanelBackgroundChromaGreen",  "Chroma green"),  "#00B140"),
        new(Localized("PanelBackgroundChromaBlue",   "Chroma blue"),   "#0047BB"),
        new(Localized("PanelBackgroundPureBlack",    "Pure black"),    "#000000"),
    ];

    /// <summary>
    /// True for stored brush values that mean "follow the app theme":
    /// empty (the new default), the legacy fixed default "#09111B", and
    /// the retired "Transparent" preset.
    /// </summary>
    private static bool IsThemeDefaultBrushValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value, "#09111B", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// The active preset. Setter writes the chosen brush into both
    /// controller VMs in unison and notifies the binding so the panels
    /// repaint. Persisted to
    /// <see cref="UiPreferences.ControllerPanelBackground"/> on the next
    /// Apply Dashboard Preferences.
    /// </summary>
    public PanelBackgroundOption? SelectedPanelBackgroundOption
    {
        get
        {
            // Match the current brush against the preset list. Custom
            // hex values that came in via the JSON file (no UI for them
            // yet) fall through to the default preset for display
            // purposes; the underlying brush is untouched.
            var current = PhysicalController.PanelBackgroundBrush;
            if (IsThemeDefaultBrushValue(current)) { current = ""; }
            foreach (var opt in PanelBackgroundOptions)
            {
                if (string.Equals(opt.BrushValue, current, StringComparison.OrdinalIgnoreCase))
                {
                    return opt;
                }
            }
            return PanelBackgroundOptions.Count > 0 ? PanelBackgroundOptions[0] : null;
        }
        set
        {
            if (value is null) { return; }
            var target = value.BrushValue;
            if (IsPanelBackgroundAppliedEverywhere(target))
            {
                return;
            }
            ApplyPanelBackgroundToAll(target);
            OnPropertyChanged(nameof(SelectedPanelBackgroundOption));

            // Persist immediately. Previously the pick only reached the
            // profile on Apply, so any profile sync in between reverted the
            // panels to the stored value ("blank" right after selecting) —
            // and a later sync resurrected a previously-applied chroma
            // ("green screen" after switching the app theme).
            pendingPanelBackgroundBrush = target;
            var version = Interlocked.Increment(ref panelBackgroundPersistVersion);
            _ = PersistPanelBackgroundAsync(target, version);
        }
    }

    private bool IsPanelBackgroundAppliedEverywhere(string brushValue) =>
        BackgroundEquals(PhysicalController.PanelBackgroundBrush, brushValue)
        && BackgroundEquals(VirtualController.PanelBackgroundBrush, brushValue)
        && BackgroundEquals(SlotConfigurationController.PanelBackgroundBrush, brushValue)
        && ControllerPanels.All(panel =>
            BackgroundEquals(panel.PhysicalVisual.PanelBackgroundBrush, brushValue)
            && BackgroundEquals(panel.VirtualVisual.PanelBackgroundBrush, brushValue));

    private static bool BackgroundEquals(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private void ApplyPanelBackgroundToAll(string? brushValue)
    {
        var normalized = brushValue ?? string.Empty;
        PhysicalController.PanelBackgroundBrush = normalized;
        VirtualController.PanelBackgroundBrush = normalized;
        SlotConfigurationController.PanelBackgroundBrush = normalized;
        foreach (var panel in ControllerPanels)
        {
            panel.PhysicalVisual.PanelBackgroundBrush = normalized;
            panel.VirtualVisual.PanelBackgroundBrush = normalized;
        }
    }

    private async Task PersistPanelBackgroundAsync(string brushValue, int version)
    {
        await panelBackgroundSaveGate.WaitAsync();
        try
        {
            // A later picker change superseded this queued write. Skipping
            // it prevents rapid green/blue/default changes from completing
            // out of order and restoring the wrong colour on next startup.
            if (version != Volatile.Read(ref panelBackgroundPersistVersion))
            {
                return;
            }

            var current = profileSession.CurrentProfile;
            var updated = current with
            {
                Ui = current.Ui with { ControllerPanelBackground = brushValue },
            };
            await profileSession.SaveCurrentProfileAsync(updated);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Persisting controller panel background failed.");
        }
        finally
        {
            if (version == Volatile.Read(ref panelBackgroundPersistVersion))
            {
                pendingPanelBackgroundBrush = null;
            }
            panelBackgroundSaveGate.Release();
        }
    }

    /// <summary>
    /// True when the selected output provider actually emits a virtual
    /// device — i.e. anything other than the "preview" pseudo-sink and
    /// the "none" no-op. The dashboard's virtual-controller panel hides
    /// when this is false (design point 5: with no emulation there's
    /// no virtual device to visualise).
    /// </summary>
    public bool IsEmulationActive
    {
        get
        {
            var key = SelectedOutputProvider?.Key;
            return !string.IsNullOrWhiteSpace(key)
                && !string.Equals(key, "preview", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "none",    StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Inverse of <see cref="IsEmulationActive"/>. Exists as its own
    /// property (rather than relying on a converter in AXAML) so the
    /// no-emulation single-panel layout can bind to a positive boolean.
    /// </summary>
    public bool IsEmulationInactive => !IsEmulationActive;

    public ControllerStyleOption? SelectedPhysicalStyle
    {
        get; set => SetProperty(ref field, value);
    }

    public ControllerStyleOption? SelectedVirtualStyle
    {
        get; set => SetProperty(ref field, value);
    }

    public DetectedControllerOption? SelectedController
    {
        get => selectedController;
        set
        {
            if (!SetProperty(ref selectedController, value))
            {
                return;
            }

            inputDeviceCatalog.SetSelectedDevice(
                string.IsNullOrWhiteSpace(value?.Id) ? null : value!.Id);
            OnPropertyChanged(nameof(SelectedControllerSummary));
        }
    }

    public ProfileOption? SelectedProfileOption
    {
        get => selectedProfileOption;
        set
        {
            if (!SetProperty(ref selectedProfileOption, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedProfileSummary));
            DeleteProfileCommand.NotifyCanExecuteChanged();

            if (value is null || isSwitchingProfile ||
                string.Equals(value.Id, profileSession.CurrentProfile.Id, StringComparison.Ordinal))
            {
                return;
            }

            _ = SwitchToProfileAsync(value.Id);
        }
    }

    public double SelectedPollingRateHz
    {
        get; set
        {
            var normalized = Math.Clamp(Math.Round(value), 30d, 1000d);
            if (!SetProperty(ref field, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedPollingRateText));
        }
    } = 250;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        await profileSession.EnsureInitializedAsync();

        SelectedLanguage = SupportedLanguages
            .FirstOrDefault(l => l.Code == profileSession.Settings.SelectedCulture)
            ?? SupportedLanguages.FirstOrDefault();

        // Apply the saved theme (defaults to CyberBlue if not stored)
        var savedThemeKey = profileSession.CurrentProfile.Ui.Theme;
        if (Enum.TryParse<AppThemeKind>(savedThemeKey, true, out var savedKind))
        {
            SelectedTheme = ThemeOptions.FirstOrDefault(t => t.Kind == savedKind)
                            ?? ThemeOptions.FirstOrDefault();
        }

        SyncDashboardSelectionsFromProfile();
        MappingEditor.LoadFromProfile(profileSession.CurrentProfile);
        RebuildControlConfigurationCards();
        await RefreshAvailableProfilesAsync();
        RefreshControllerInventory();

        AboutText = BuildAboutText();
        aboutTextDirty = false;

        ProfileJson = profileSession.SerializeCurrentProfile();

        ProfileName = profileSession.CurrentProfile.Name;

        RefreshLocalizedText();
        ResetSelectionInspector();
        await RefreshRuntimeAsync();
    }

    // ─── Main refresh — called every 33 ms ────────────────────────────────────

    public Task RefreshRuntimeAsync()
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        var snapshot = runtimeSnapshotStore.Current;
        var profile  = profileSession.CurrentProfile;
        var physicalStyle = SelectedPhysicalStyle?.Style ?? profile.Ui.PhysicalControllerStyle;
        var virtualStyle  = SelectedVirtualStyle?.Style  ?? profile.Ui.VirtualControllerStyle;

        // When the user leaves the virtual panel on Auto, resolve the
        // style from the SELECTED OUTPUT CONTROLLER first — the first
        // enabled slot's kind or explicit HIDMaestro catalog profile
        // (computed in RebuildControllerPanels). This is authoritative in
        // a way device names never were: HIDMaestro's provider id is
        // always just "hidmaestro" regardless of what it emits, and the
        // emitted snapshot carries the PHYSICAL device's name, so both
        // older inference paths misclassified (a DualSense output driven
        // by an Xbox pad rendered as Xbox). The provider-id map stays as
        // a fallback for any future backend that encodes kind in its id.
        if (virtualStyle == ControllerVisualStyle.Auto)
        {
            virtualStyle = slotDerivedVirtualStyle
                ?? InferVirtualStyleFromProvider(snapshot.OutputProvider)
                ?? virtualStyle;
        }

        // Physical "Auto" with no concrete detection yields an EMPTY theme
        // variant list — which left the physical preview blank and unable
        // to keep a skin the user picked (every refresh re-cleared it). Fall
        // back to the resolved virtual style so the physical surface renders
        // a controller and its Skin picker actually has options. Proper
        // per-device physical detection (VID/PID → style) is a later pass.
        if (physicalStyle == ControllerVisualStyle.Auto)
        {
            physicalStyle = virtualStyle == ControllerVisualStyle.Auto
                ? ControllerVisualStyle.PlayStation5
                : virtualStyle;
        }

        // ── Fast path: always ─────────────────────────────────────────────────
        PhysicalController.Update("physical", PhysicalInputLabel, snapshot.PhysicalSnapshot, physicalStyle);
        VirtualController.Update("virtual",   VirtualOutputLabel,  snapshot.VirtualSnapshot,  virtualStyle);

        var selectedSlotId = SlotsPanel.SelectedSlot?.Id;
        var selectedSlot = string.IsNullOrWhiteSpace(selectedSlotId)
            ? null
            : slotRegistry.GetSlot(selectedSlotId);
        if (selectedSlot is not null)
        {
            var selectedPair = slotSnapshotStore.Get(selectedSlot.Id);
            SlotConfigurationController.Update(
                selectedSlot.Id + ":virtual",
                "Virtual output · click a control to map it",
                selectedPair.Virtual,
                ResolveSlotVirtualStyle(selectedSlot));
        }

        // Per-slot dashboard panels (live state for each running controller).
        // Each side gets its own clear title — "Physical Input" / "Virtual
        // Output" — since the slot's own name is already shown once above
        // the pair; repeating it on both sides was what read as an
        // unlabeled "LIVE" panel rather than a clearly-marked physical one.
        foreach (var panel in ControllerPanels)
        {
            if (panel.IsPhysicalOnly)
            {
                // Layout-only panel: physical side straight from the pin
                // service; no virtual half exists.
                var deviceSnapshot = physicalPanelPins.GetSnapshot(panel.SlotId[5..]);
                panel.PhysicalVisual.Update(panel.SlotId + ":physical", PhysicalInputLabel,
                    deviceSnapshot, ControllerVisualStyle.Auto);
                continue;
            }

            var pair = slotSnapshotStore.Get(panel.SlotId);
            panel.OutputStatus = slotSnapshotStore.GetOutputStatus(panel.SlotId);
            panel.PhysicalVisual.Update(panel.SlotId + ":physical", PhysicalInputLabel,
                pair.Physical, panel.PhysicalStyle);
            panel.VirtualVisual.Update(panel.SlotId + ":virtual", VirtualOutputLabel,
                pair.Virtual, panel.VirtualStyle);
        }

        // Header subtitle intentionally left to transient command/error
        // messages only (Apply/Save/errors set StatusText directly). The
        // old per-tick "Runtime SDL3 → Preview Output · Active profile"
        // summary was removed: provider/output is implied per-controller
        // now, so an always-on global line just added noise.

        // Diagnostics tab removed: no per-tick string build on the UI thread.

        if (!string.IsNullOrWhiteSpace(selectedControlKey))
        {
            ApplySelection(selectedControlKey, snapshot);
        }

        refreshTick++;

        // ── Medium path: ~0.5 s ───────────────────────────────────────────────
        if (refreshTick % MedPathEvery == 0 || refreshTick == 1)
        {
            RefreshControllerInventory();
            RuntimeNotesText = BuildRuntimeNotes(snapshot);

            // The motion panel rides this tick rather than owning a timer,
            // so it costs nothing while the Dashboard is off screen.
            MotionServerPanel.Refresh();
        }

        // Diagnostics tab removed: no raw physical/virtual JSON serialization.

        // ── Dirty-flag paths ──────────────────────────────────────────────────
        if (ruleSummaryDirty)
        {
            RuleSummary = string.Join(
                Environment.NewLine,
                profile.Rules.Select(r => $"- {r.Name} [{r.GetType().Name}]"));
            ruleSummaryDirty = false;
        }

        if (aboutTextDirty)
        {
            AboutText = BuildAboutText();
            aboutTextDirty = false;
        }

        return Task.CompletedTask;
    }

    // ─── Dashboard preference command ─────────────────────────────────────────

    private async Task ApplyDashboardPreferencesAsync()
    {
        if (SelectedInputProvider is null || SelectedPhysicalStyle is null || SelectedVirtualStyle is null)
        {
            return;
        }

        try
        {
            var outputKey = SelectedOutputProvider?.Key
                ?? profileSession.CurrentProfile.OutputProvider;

            var themeKey = SelectedTheme?.Kind.ToString() ?? "CyberBlue";

            // Build the new Ui prefs by folding the pending theme-variant
            // picks (if any) and the current panel background into the
            // existing per-style fields. Picks that don't map to a known
            // style fall through unchanged.
            var ui = profileSession.CurrentProfile.Ui with
            {
                PhysicalControllerStyle = SelectedPhysicalStyle.Style,
                VirtualControllerStyle  = SelectedVirtualStyle.Style,
                Theme = themeKey,
                ControllerPanelBackground = PhysicalController.PanelBackgroundBrush,
            };

            ui = MergeVariantPick(ui, SelectedPhysicalStyle.Style, pendingPhysicalVariantPick);
            ui = MergeVariantPick(ui, SelectedVirtualStyle.Style,  pendingVirtualVariantPick);

            var profile = profileSession.CurrentProfile with
            {
                InputProvider = SelectedInputProvider.Key,
                OutputProvider = outputKey,
                PollingRateHz = (int)SelectedPollingRateHz,
                PreferredInputDeviceId = SelectedController?.Id ?? string.Empty,
                Ui = ui,
            };

            await profileSession.SaveCurrentProfileAsync(profile);

            // Picks are now persisted — clear the pending slots so a
            // later Apply that doesn't include another pick doesn't
            // re-write the same field.
            pendingPhysicalVariantPick = null;
            pendingVirtualVariantPick  = null;

            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            await RefreshRuntimeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to apply dashboard preferences.");
            StatusText = $"Apply failed: {exception.Message}";
        }
    }

    // ─── Save / reset / duplicate / rename helpers ────────────────────────────

    /// <summary>
    /// Returns <paramref name="desired"/> if no existing profile uses that
    /// name; otherwise appends " (2)", " (3)", … until unique. Case-
    /// insensitive. If <paramref name="excludeProfileId"/> is supplied,
    /// that profile is ignored (so renaming to the same name is allowed).
    /// </summary>
    private async Task<string> MakeUniqueProfileNameAsync(string desired, string? excludeProfileId = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(desired) ? "Profile" : desired.Trim();
        var summaries = await profileSession.ListProfilesAsync();
        var taken = summaries
            .Where(p => excludeProfileId is null || !string.Equals(p.Id, excludeProfileId, StringComparison.Ordinal))
            .Select(p => p.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(trimmed))
        {
            return trimmed;
        }
        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{trimmed} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
        return $"{trimmed} ({Guid.NewGuid().ToString("N")[..6]})";
    }

    private async Task SaveProfileAsync()
    {
        try
        {
            await profileSession.SaveCurrentProfileAsync(profileSession.CurrentProfile);
            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Profile saved.");
            }
            await RefreshRuntimeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save profile.");
            StatusText = $"Save failed: {exception.Message}";
        }
    }

    private async Task ResetProfileAsync()
    {
        isSwitchingProfile = true;
        try
        {
            await profileSession.ResetToDefaultAsync();
            MappingEditor.LoadFromProfile(profileSession.CurrentProfile);
            RebuildControlConfigurationCards();
            await RefreshAvailableProfilesAsync();
            SyncDashboardSelectionsFromProfile();
            RefreshControllerInventory();
            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            ProfileName      = profileSession.CurrentProfile.Name;
            await RefreshRuntimeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to reset profile.");
            StatusText = $"Reset failed: {exception.Message}";
        }
        finally
        {
            isSwitchingProfile = false;
        }
    }

    private async Task CreateProfileAsync()
    {
        isSwitchingProfile = true;
        try
        {
            var desired = $"Profile {DateTime.Now:HHmmss}";
            var unique = await MakeUniqueProfileNameAsync(desired);
            var profile = await profileSession.CreateNewProfileAsync(unique);
            await RefreshAvailableProfilesAsync();
            ProfileName = profile.Name;
            var target = AvailableProfiles.FirstOrDefault(o => o.Id == profile.Id);
            if (target is not null)
            {
                selectedProfileOption = target;
                OnPropertyChanged(nameof(SelectedProfileOption));
                OnPropertyChanged(nameof(SelectedProfileSummary));
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create profile.");
            StatusText = $"Create profile failed: {exception.Message}";
        }
        finally
        {
            isSwitchingProfile = false;
        }
    }

    private async Task DuplicateProfileAsync()
    {
        isSwitchingProfile = true;
        try
        {
            var desired = $"{profileSession.CurrentProfile.Name} Copy";
            var unique = await MakeUniqueProfileNameAsync(desired);
            var copy = await profileSession.DuplicateCurrentProfileAsync(unique);
            await RefreshAvailableProfilesAsync();
            ProfileName = copy.Name;
            var target = AvailableProfiles.FirstOrDefault(o => o.Id == copy.Id);
            if (target is not null)
            {
                selectedProfileOption = target;
                OnPropertyChanged(nameof(SelectedProfileOption));
                OnPropertyChanged(nameof(SelectedProfileSummary));
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to duplicate profile.");
            StatusText = $"Duplicate profile failed: {exception.Message}";
        }
        finally
        {
            isSwitchingProfile = false;
        }
    }

    private async Task ImportProfileAsync()
    {
        try
        {
            var json = await profileFileDialogService.ImportProfileJsonAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            isSwitchingProfile = true;
            try
            {
                await profileSession.ImportProfileAsync(json);
                await RefreshAvailableProfilesAsync();
                ProfileJson      = profileSession.SerializeCurrentProfile();
                ruleSummaryDirty = true;
                RebuildControlConfigurationCards();
                ProfileName = profileSession.CurrentProfile.Name;
                await RefreshRuntimeAsync();
            }
            finally
            {
                isSwitchingProfile = false;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import profile.");
            StatusText = $"Import failed: {exception.Message}";
        }
    }

    private async Task ExportProfileAsync()
    {
        try
        {
            var name = SanitizeFileName(profileSession.CurrentProfile.Name);
            _ = await profileFileDialogService.ExportProfileJsonAsync($"{name}.json",
                profileSession.SerializeCurrentProfile());
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export profile.");
            StatusText = $"Export failed: {exception.Message}";
        }
    }

    private async Task RenameProfileAsync()
    {
        var newName = ProfileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(newName, profileSession.CurrentProfile.Name, StringComparison.Ordinal))
        {
            return;
        }

        isSwitchingProfile = true;
        try
        {
            var unique = await MakeUniqueProfileNameAsync(newName, profileSession.CurrentProfile.Id);
            var profile = profileSession.CurrentProfile with { Name = unique };
            await profileSession.SaveCurrentProfileAsync(profile);
            ProfileName      = unique;
            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            await RefreshAvailableProfilesAsync();
            var target = AvailableProfiles.FirstOrDefault(o => o.Id == profile.Id);
            if (target is not null)
            {
                selectedProfileOption = target;
                OnPropertyChanged(nameof(SelectedProfileOption));
                OnPropertyChanged(nameof(SelectedProfileSummary));
            }
            await RefreshRuntimeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to rename profile.");
            StatusText = $"Rename failed: {exception.Message}";
        }
        finally
        {
            isSwitchingProfile = false;
        }
    }

    /// <summary>
    /// Deletes the currently selected profile. If the selected profile is the
    /// active one (the common case — the dropdown switches the active profile
    /// on selection), the runtime first switches to another profile, then
    /// deletes the original. Refuses to delete when there is no other profile
    /// to switch to (the user must always have at least one profile).
    /// </summary>
    /// <returns>A task that completes when deletion (and the subsequent UI
    /// refresh) finishes.</returns>
    private async Task DeleteProfileAsync()
    {
        var target = selectedProfileOption;
        if (target is null || string.IsNullOrWhiteSpace(target.Id))
        {
            return;
        }

        // Can't leave the user with zero profiles.
        if (AvailableProfiles.Count <= 1)
        {
            StatusText = localizationService["DeleteProfileBlockedLastOne"];
            return;
        }

        try
        {
            // If the user is deleting the active profile, switch to another
            // one first so ProfileSession.DeleteProfileAsync (which refuses
            // to delete the active profile) actually proceeds.
            if (string.Equals(target.Id, profileSession.CurrentProfile.Id, StringComparison.Ordinal))
            {
                var fallback = AvailableProfiles
                    .FirstOrDefault(p => !string.Equals(p.Id, target.Id, StringComparison.Ordinal));
                if (fallback is null)
                {
                    StatusText = localizationService["DeleteProfileBlockedLastOne"];
                    return;
                }

                await SwitchToProfileAsync(fallback.Id);
            }

            await profileSession.DeleteProfileAsync(target.Id);
            await RefreshAvailableProfilesAsync();

            // Snap the dropdown selection to whatever's now active.
            var current = AvailableProfiles
                .FirstOrDefault(p => p.Id == profileSession.CurrentProfile.Id)
                ?? AvailableProfiles.FirstOrDefault();
            if (current is not null && current != selectedProfileOption)
            {
                selectedProfileOption = current;
                OnPropertyChanged(nameof(SelectedProfileOption));
                OnPropertyChanged(nameof(SelectedProfileSummary));
            }

            DeleteProfileCommand.NotifyCanExecuteChanged();

            logger.LogInformation("Deleted profile {ProfileId} ({ProfileName}).", target.Id, target.Label);
            StatusText = string.Format(localizationService["ProfileDeletedStatus"], target.Label);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete profile {ProfileId}.", target.Id);
            StatusText = $"Delete failed: {exception.Message}";
        }
    }

    /// <summary>
    /// CanExecute predicate for <see cref="DeleteProfileCommand"/>. Disables
    /// the button when there's no selection or only one profile exists (the
    /// runtime always needs at least one profile).
    /// </summary>
    /// <returns>True when the currently selected profile may be deleted.</returns>
    private bool CanDeleteProfile()
    {
        return selectedProfileOption is not null
            && !string.IsNullOrWhiteSpace(selectedProfileOption.Id)
            && AvailableProfiles.Count > 1;
    }

    /// <summary>
    /// Opens the Options / Settings dialog. Resolves a fresh
    /// <see cref="SettingsDialogViewModel"/> (registered as transient
    /// in DI) and shows the dialog modally over the current main window.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IServiceProvider"/> rather than direct DI of the
    /// view-model because the dialog is short-lived and we want a clean
    /// view-model state every open — preserves the cancel-discards-edits
    /// behaviour without having to manually reset everything.
    /// </remarks>
    private async Task OpenSettingsAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
        {
            logger.LogWarning("OpenSettings invoked but no main window is available — ignoring.");
            return;
        }

        try
        {
            var dialogViewModel = serviceProvider.GetRequiredService<SettingsDialogViewModel>();
            dialogViewModel.Shell = this;

            // Both of these read lists that change while the app runs —
            // installed themes and slots for the overlay URL, saved
            // profiles and the foreground app for the per-app rules. They
            // are refreshed here rather than in their constructors
            // because "what is in front right now" has to be read when
            // the user is looking at it, not when the VM was built.
            dialogViewModel.Overlay.Refresh();
            await dialogViewModel.AppProfiles.RefreshAsync();

            var dialog = new SettingsDialog
            {
                DataContext = dialogViewModel,
            };

            logger.LogDebug("Opening settings dialog.");
            await dialog.ShowDialog(desktop.MainWindow);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Settings dialog failed to open.");
        }
    }

    // ─── Dashboard sync ───────────────────────────────────────────────────────

    private void SyncDashboardSelectionsFromProfile()
    {
        var profile = profileSession.CurrentProfile;

        // Phase 1 input simplification: input is no longer user-selectable.
        // We always resolve to the platform's unified input provider (SDL3
        // on every OS for now) regardless of what the profile saved, and the
        // picker ComboBox is hidden in ShellWindow.axaml. The saved
        // profile.InputProvider is preserved on disk but ignored here, so
        // re-enabling the picker later (or adding a Windows-specific RawInput
        // path for keyboard/mouse) is a localized change.
        var defaultInputKey = ResolveDefaultInputProviderKey();
        SelectedInputProvider = InputProviderOptions
            .FirstOrDefault(o => o.Key == defaultInputKey)
            ?? InputProviderOptions.FirstOrDefault(o => o.Key == "sdl")
            ?? InputProviderOptions.FirstOrDefault();

        SelectedOutputProvider = OutputProviderOptions
            .FirstOrDefault(o => o.Key == profile.OutputProvider)
            ?? OutputProviderOptions.FirstOrDefault();

        SelectedPhysicalStyle = ControllerStyleOptions
            .FirstOrDefault(o => o.Style == profile.Ui.PhysicalControllerStyle)
            ?? ControllerStyleOptions.FirstOrDefault();

        SelectedVirtualStyle = ControllerStyleOptions
            .FirstOrDefault(o => o.Style == profile.Ui.VirtualControllerStyle)
            ?? ControllerStyleOptions.FirstOrDefault();

        SelectedPollingRateHz = profile.PollingRateHz;

        if (Enum.TryParse<AppThemeKind>(profile.Ui.Theme, true, out var savedKind))
        {
            var themeOpt = ThemeOptions.FirstOrDefault(t => t.Kind == savedKind);
            if (themeOpt is not null && themeOpt != selectedTheme)
            {
                selectedTheme = themeOpt;
                OnPropertyChanged(nameof(SelectedTheme));
                AppThemeService.Apply(savedKind);
            }
        }

        // Push the persisted theme-variant ids into the controller VMs.
        // The VMs only consult these on the next RefreshActiveTheme(),
        // which happens automatically once a snapshot arrives. The
        // ResolveVisualStyle for each panel determines which field
        // applies: physical typically resolves via the detected device,
        // virtual uses the user-picked style directly.
        ApplyVariantPreferenceForStyle(PhysicalController, profile.Ui.PhysicalControllerStyle, profile.Ui);
        ApplyVariantPreferenceForStyle(VirtualController,  profile.Ui.VirtualControllerStyle,  profile.Ui);

        // Background brush — applies to both panels uniformly.
        ApplyPanelBackgroundToAll(pendingPanelBackgroundBrush ?? profile.Ui.ControllerPanelBackground);
        OnPropertyChanged(nameof(SelectedPanelBackgroundOption));
    }

    /// <summary>
    /// Looks up the variant-id field on <paramref name="ui"/> that
    /// applies to <paramref name="style"/> and pushes it into
    /// <paramref name="vm"/>'s preference slot. Auto / None fall
    /// through with no preference — the registry's first variant wins.
    /// </summary>
    private static void ApplyVariantPreferenceForStyle(
        ControllerVisualStateViewModel vm,
        ControllerVisualStyle style,
        UiPreferences ui)
    {
        var preferredId = style switch
        {
            ControllerVisualStyle.PlayStation5 => ui.DualSenseVariantId,
            ControllerVisualStyle.PlayStation4 => ui.DualShock4VariantId,
            ControllerVisualStyle.PlayStation3 => ui.DualShock3VariantId,
            ControllerVisualStyle.Xbox         => ui.XboxVariantId,
            _ => string.Empty,
        };

        vm.SetPreferredVariantId(preferredId);
        // Trigger a refresh in case the style was already resolved.
        vm.RefreshActiveTheme();
    }

    /// <summary>
    /// Returns a copy of <paramref name="ui"/> with the variant id
    /// for <paramref name="style"/> replaced by <paramref name="pickedId"/>.
    /// No-op when <paramref name="pickedId"/> is null (nothing pending)
    /// or when the style is Auto/None (nothing to slot the pick into).
    /// </summary>
    private static UiPreferences MergeVariantPick(
        UiPreferences ui,
        ControllerVisualStyle style,
        string? pickedId)
    {
        if (string.IsNullOrEmpty(pickedId)) { return ui; }

        return style switch
        {
            ControllerVisualStyle.PlayStation5 => ui with { DualSenseVariantId  = pickedId },
            ControllerVisualStyle.PlayStation4 => ui with { DualShock4VariantId = pickedId },
            ControllerVisualStyle.PlayStation3 => ui with { DualShock3VariantId = pickedId },
            ControllerVisualStyle.Xbox         => ui with { XboxVariantId       = pickedId },
            _ => ui,
        };
    }

    private async Task RefreshAvailableProfilesAsync()
    {
        var profiles = await profileSession.ListProfilesAsync();
        AvailableProfiles = [.. profiles
            .Select(p => new ProfileOption(p.Id, p.Name, p.Id))
            .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase)];

        var current = AvailableProfiles.FirstOrDefault(o => o.Id == profileSession.CurrentProfile.Id)
            ?? AvailableProfiles.FirstOrDefault();

        if (!ReferenceEquals(selectedProfileOption, current))
        {
            selectedProfileOption = current;
            OnPropertyChanged(nameof(SelectedProfileOption));
            OnPropertyChanged(nameof(SelectedProfileSummary));
        }

        // CanExecute depends on AvailableProfiles.Count and on the selection,
        // both of which may have changed above.
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private async Task SwitchToProfileAsync(string profileId)
    {
        try
        {
            isSwitchingProfile = true;
            await profileSession.SwitchToProfileAsync(profileId);
            MappingEditor.LoadFromProfile(profileSession.CurrentProfile);
            RebuildControlConfigurationCards();
            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            ProfileName      = profileSession.CurrentProfile.Name;
            await RefreshRuntimeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to switch profile to {ProfileId}.", profileId);
            StatusText = $"Profile switch failed: {exception.Message}";
        }
        finally
        {
            isSwitchingProfile = false;
        }
    }

    private void RefreshControllerInventory()
    {
        if (disposed)
        {
            return;
        }

        var profile = profileSession.CurrentProfile;
        var devices = inputDeviceCatalog.Devices;

        var options = new List<DetectedControllerOption>
        {
            new(string.Empty,
                localizationService["AutomaticControllerSelection"],
                localizationService["AutomaticControllerSelectionDescription"])
        };

        options.AddRange(devices.Select(d =>
            new DetectedControllerOption(d.Id, d.DisplayName,
                string.IsNullOrWhiteSpace(d.HardwareId) ? d.DisplayName : d.HardwareId)));

        AvailableControllers = options;

        var preferredId = selectedController?.Id;
        if (string.IsNullOrWhiteSpace(preferredId))
        {
            preferredId = inputDeviceCatalog.SelectedDeviceId;
        }

        if (string.IsNullOrWhiteSpace(preferredId))
        {
            preferredId = profile.PreferredInputDeviceId;
        }

        var nextSelected = AvailableControllers
            .FirstOrDefault(o => string.Equals(o.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableControllers.First();

        if (selectedController != nextSelected)
        {
            selectedController = nextSelected;
            OnPropertyChanged(nameof(SelectedController));
            OnPropertyChanged(nameof(SelectedControllerSummary));
        }

        inputDeviceCatalog.SetSelectedDevice(
            string.IsNullOrWhiteSpace(nextSelected.Id) ? null : nextSelected.Id);

        ControllerInventoryText = devices.Count switch
        {
            0 => localizationService["NoControllersDetected"],
            1 => string.Format(localizationService["ControllersDetectedSingle"], devices[0].DisplayName),
            _ => string.Format(localizationService["ControllersDetectedMultiple"], devices.Count)
        };

        ProviderStatusText = inputDeviceCatalog.ProviderStatus;
    }

    private string RuleCountText(int count) => count switch
    {
        0 => Localized("MappedControlsRuleCountZero", "No rules"),
        1 => Localized("MappedControlsRuleCountOne",  "1 rule"),
        _ => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localized("MappedControlsRuleCountMany", "{0} rules"),
            count),
    };

    private void RebuildControlConfigurationCards()
    {
        var grouped = profileSession.CurrentProfile.Rules
            .GroupBy(ControlRuleMatcher.GetPrimarySelectionKey)
            .OrderBy(g => ControlRuleMatcher.GetTitle(g.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ControlConfigurationCardViewModel(
                g.Key,
                ControlRuleMatcher.GetTitle(g.Key),
                ControlRuleMatcher.GetHint(g.Key),
                RuleCountText(g.Count()),
                [.. g.Select(ControlRuleMatcher.CreateEntry)]))
            .ToArray();

        ControlConfigurationCards = grouped;
        OnPropertyChanged(nameof(HasControlConfigurationCards));
        OnPropertyChanged(nameof(HasNoControlConfigurationCards));
    }

    /// <summary>
    /// Opens the tuning editor for a slot. An assigned device remains the
    /// target even while offline; a slot without one edits its inheritable
    /// defaults, so a virtual panel never becomes a dead button merely
    /// because physical hardware is absent.
    /// </summary>
    private void OpenDeviceSettings(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return;
        }

        var slot = slotRegistry.GetSlots().FirstOrDefault(s => s.Id == slotId);
        if (slot is null)
        {
            return;
        }

        var target = GameFlow.Infrastructure.Runtime.Slots.DeviceSettingsTargetResolver.Resolve(
            slot,
            inputDeviceCatalog.Devices);

        var editor = serviceProvider.GetService(typeof(DeviceSettingsEditorViewModel)) as DeviceSettingsEditorViewModel;
        if (editor is null)
        {
            return;
        }

        editor.Load(slot.Id, target.DeviceId, target.DisplayName);
        DeviceSettingsRequested?.Invoke(this, new DeviceSettingsRequestedEventArgs(editor));
    }

    private void OpenControlEditor(string? selectionKey)
    {
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return;
        }

        var dialogViewModel = new ControlMappingDialogViewModel(
            selectionKey, profileSession.CurrentProfile, loggerFactory);

        dialogViewModel.MergedRulesChanged += OnDialogMergedRulesChanged;
        ControlMappingRequested?.Invoke(this, new ControlMappingRequestedEventArgs(dialogViewModel));
    }

    private void OnDialogMergedRulesChanged(object? sender, IReadOnlyList<MappingRule> mergedRules)
    {
        _ = SaveRulesAsync(mergedRules);
    }

    private void OnMappingRulesChanged(object? sender, IReadOnlyList<MappingRule> rules)
    {
        _ = SaveRulesAsync(rules);
    }

    private async Task SaveRulesAsync(IReadOnlyList<MappingRule> rules)
    {
        if (disposed)
        {
            return;
        }

        await rulesSaveGate.WaitAsync();
        try
        {
            var profile = profileSession.CurrentProfile with { Rules = [.. rules] };
            await profileSession.SaveCurrentProfileAsync(profile);
            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            RebuildControlConfigurationCards();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save mapping rules.");
            StatusText = $"Rule save failed: {exception.Message}";
        }
        finally
        {
            _ = rulesSaveGate.Release();
        }
    }

    // ─── Text builders ────────────────────────────────────────────────────────

    private string BuildAboutText()
    {
        return string.Join(Environment.NewLine,
        [
            localizationService["AboutBodyLine1"],
            localizationService["AboutBodyLine2"],
            localizationService["AboutBodyLine3"],
            string.Empty,
            localizationService["AboutBodyLine4"]
        ]);
    }

    private string BuildRuntimeNotes(RuntimeSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"- {ProviderStatusText}",
            $"- {ControllerInventoryText}"
        };

        var inputProvider = profileSession.CurrentProfile.InputProvider;

        if (inputProvider.Equals("demo", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- {localizationService["RuntimeNoteDemoActive"]}");
        }

        if (IsSdlProvider(inputProvider))
        {
            lines.Add($"- {localizationService["RuntimeNoteSdlActive"]}");
        }

        if (inputProvider.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- {localizationService["RuntimeNoteLiveInputDisabled"]}");
        }

        lines.AddRange(snapshot.Notes.Select(n => $"- {n}"));
        return string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }

    // ─── Selection inspector ──────────────────────────────────────────────────

    private void OnControllerElementSelected(string selectionKey)
    {
        selectedControlKey = selectionKey;
        ApplySelection(selectionKey, runtimeSnapshotStore.Current);

        OpenControlEditor(selectionKey);
    }

    private void ApplySelection(string selectionKey, RuntimeSnapshot snapshot)
    {
        var sep = selectionKey.IndexOf(':');
        if (sep < 0)
        {
            return;
        }

        var scope      = selectionKey[..sep];
        var elementKey = selectionKey[(sep + 1)..];
        var source     = scope.Equals("virtual", StringComparison.OrdinalIgnoreCase)
            ? snapshot.VirtualSnapshot
            : snapshot.PhysicalSnapshot;

        SelectedControlTitle = $"{FormatScope(scope)} · {FormatElementName(elementKey)}";
        SelectedControlValue = BuildCurrentValue(elementKey, source);
        SelectedControlRules = BuildRuleMatches(elementKey);
        SelectedControlHint  = BuildSelectionHint(elementKey);
    }

    private static string BuildCurrentValue(string elementKey, ControllerSnapshot snapshot)
    {
        if (TryParseButton(elementKey, out var button))
        {
            return snapshot.IsPressed(button) ? "Current state: pressed" : "Current state: released";
        }

        return elementKey.StartsWith("LeftTrigger", StringComparison.OrdinalIgnoreCase)
            ? $"Current value: {snapshot.LeftTrigger:P0}"
            : elementKey.StartsWith("RightTrigger", StringComparison.OrdinalIgnoreCase)
            ? $"Current value: {snapshot.RightTrigger:P0}"
            : elementKey.StartsWith("LeftStick", StringComparison.OrdinalIgnoreCase)
            ? $"Current vector: {snapshot.LeftStick.X:0.00}, {snapshot.LeftStick.Y:0.00}"
            : elementKey.StartsWith("RightStick", StringComparison.OrdinalIgnoreCase)
            ? $"Current vector: {snapshot.RightStick.X:0.00}, {snapshot.RightStick.Y:0.00}"
            : elementKey.Equals("Touchpad", StringComparison.OrdinalIgnoreCase)
            ? snapshot.TouchContactCount switch
            {
                <= 0 => "Current state: no active touches",
                1    => "Current state: 1 touch contact",
                _    => $"Current state: {snapshot.TouchContactCount} touch contacts"
            }
            : "Current state unavailable.";
    }

    private string BuildRuleMatches(string elementKey)
    {
        var selectionKey = ControlRuleMatcher.EnsurePhysicalSelectionKey(elementKey);
        var lines = profileSession.CurrentProfile.Rules
            .Where(r => ControlRuleMatcher.Matches(selectionKey, r))
            .Select(r => $"- {r.Name}: {ControlRuleMatcher.CreateEntry(r).Value}")
            .ToArray();

        return lines.Length == 0
            ? "No rule is using this control yet."
            : string.Join(Environment.NewLine, lines);
    }

    private static string BuildSelectionHint(string elementKey)
    {
        return ControlRuleMatcher.GetHint(elementKey);
    }

    private void ResetSelectionInspector()
    {
        SelectedControlTitle = "Click any control on either controller surface.";
        SelectedControlValue = "The inspector shows the current live state of the selected element.";
        SelectedControlRules = "Matching rules for the selected control will appear here.";
        SelectedControlHint  = "Physical controls configure input behavior; virtual controls create output mappings.";
    }

    // ─── Localised text refresh ───────────────────────────────────────────────

    private void RefreshLocalizedText()
    {
        if (disposed)
        {
            return;
        }

        WindowTitle    = localizationService["WindowTitle"];
        aboutTextDirty = true;

        // Re-build the option lists in the new culture and re-select
        // the currently-active option by its language-independent key.
        // Done before the OnPropertyChanged batch below so the combo
        // boxes pick up both the new ItemsSource and the new selection
        // in one pass.
        RebuildLocalizedOptionLists();

        // Push the culture change down into the per-controller panels
        // so their StyleLabel / MinimalSummaryText pick up the new
        // localized strings without waiting for a style change or the
        // next snapshot tick.
        PhysicalController.RefreshLocalizedLabels();
        VirtualController.RefreshLocalizedLabels();
        MappingEditor.RefreshLocalizedLabels();

        OnPropertyChanged(nameof(DashboardTabLabel));
        OnPropertyChanged(nameof(ProfilesTabLabel));
        OnPropertyChanged(nameof(DiagnosticsTabLabel));
        OnPropertyChanged(nameof(DevicesTabLabel));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(OpenSettingsButtonLabel));
        OnPropertyChanged(nameof(SaveProfileButtonLabel));
        OnPropertyChanged(nameof(ResetProfileButtonLabel));
        OnPropertyChanged(nameof(PhysicalInputLabel));
        OnPropertyChanged(nameof(VirtualOutputLabel));
        OnPropertyChanged(nameof(RuleSummaryLabel));
        OnPropertyChanged(nameof(ProfileEditorLabel));
        OnPropertyChanged(nameof(DiagnosticsLabel));
        OnPropertyChanged(nameof(DashboardSettingsLabel));
        OnPropertyChanged(nameof(DashboardSubtitle));
        OnPropertyChanged(nameof(InputProviderLabel));
        OnPropertyChanged(nameof(OutputProviderLabel));
        OnPropertyChanged(nameof(ControllerLabel));
        OnPropertyChanged(nameof(ControllerInventoryLabel));
        OnPropertyChanged(nameof(ProviderStatusLabel));
        OnPropertyChanged(nameof(PollingRateLabel));
        OnPropertyChanged(nameof(PhysicalStyleLabel));
        OnPropertyChanged(nameof(VirtualStyleLabel));
        OnPropertyChanged(nameof(ApplyDashboardSettingsButtonLabel));
        OnPropertyChanged(nameof(SelectedControlLabel));
        OnPropertyChanged(nameof(RuntimeNotesLabel));
        OnPropertyChanged(nameof(RawPhysicalLabel));
        OnPropertyChanged(nameof(RawVirtualLabel));
        OnPropertyChanged(nameof(ProfileComboLabel));
        OnPropertyChanged(nameof(ProfileToolbarSubtitle));
        OnPropertyChanged(nameof(ProfileNameLabel));
        OnPropertyChanged(nameof(ProfileNameWatermark));
        OnPropertyChanged(nameof(RenameProfileButtonLabel));
        OnPropertyChanged(nameof(CreateProfileButtonLabel));
        OnPropertyChanged(nameof(DuplicateProfileButtonLabel));
        OnPropertyChanged(nameof(ImportProfileButtonLabel));
        OnPropertyChanged(nameof(ExportProfileButtonLabel));
        OnPropertyChanged(nameof(DeleteProfileButtonLabel));
        OnPropertyChanged(nameof(MappingOverviewLabel));
        OnPropertyChanged(nameof(MappingOverviewSubtitle));
        OnPropertyChanged(nameof(OpenControlEditorButtonLabel));
        OnPropertyChanged(nameof(NoMappingCardsText));
        OnPropertyChanged(nameof(SelectedControllerSummary));
        OnPropertyChanged(nameof(SelectedProfileSummary));
        OnPropertyChanged(nameof(HasControlConfigurationCards));
        OnPropertyChanged(nameof(HasNoControlConfigurationCards));
        OnPropertyChanged(nameof(DashboardBackgroundLabel));
        OnPropertyChanged(nameof(VirtualBadgeLabel));
        OnPropertyChanged(nameof(NoControllerConnectedLabel));
        OnPropertyChanged(nameof(SidebarPhysicalHeader));
        OnPropertyChanged(nameof(SidebarVirtualHeader));
        OnPropertyChanged(nameof(DevicesPhysicalTabHeader));
        OnPropertyChanged(nameof(DevicesVirtualTabHeader));
        OnPropertyChanged(nameof(PanelBackgroundOptions));
        OnPropertyChanged(nameof(SelectedPanelBackgroundOption));

        // Rebuild the mapping cards with the new culture: their rule-count
        // text is baked at construction, and regenerating the items also
        // forces detached tab content (Profiles not currently visible) to
        // re-evaluate its bindings when it next attaches — fixing labels
        // that otherwise kept the startup language.
        RebuildControlConfigurationCards();

        RefreshControllerInventory();
    }

    // ─── Event handlers ───────────────────────────────────────────────────────

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        PostUiRefresh(RefreshLocalizedText);
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        if (isSwitchingProfile || isChangingCulture)
        {
            return;
        }

        PostUiRefresh(() =>
        {
            SyncDashboardSelectionsFromProfile();
            MappingEditor.LoadFromProfile(profileSession.CurrentProfile);
            RebuildControlConfigurationCards();
            RefreshControllerInventory();
            _ = RefreshAvailableProfilesAsync();
            ProfileJson      = profileSession.SerializeCurrentProfile();
            ruleSummaryDirty = true;
            ProfileName      = profileSession.CurrentProfile.Name;
            OnPropertyChanged(nameof(SelectedProfileSummary));
            OnPropertyChanged(nameof(HasControlConfigurationCards));
            OnPropertyChanged(nameof(HasNoControlConfigurationCards));
        });
    }

    private void PostUiRefresh(Action? beforeRefresh = null)
    {
        if (disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (disposed)
            {
                return;
            }

            try
            {
                beforeRefresh?.Invoke();
                await RefreshRuntimeAsync();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Deferred dashboard refresh failed.");
            }
        });
    }

    // ─── Static factory helpers ───────────────────────────────────────────────

    private IReadOnlyList<AppThemeOption> CreateThemeOptions()
    {
        return
        [
            new AppThemeOption(AppThemeKind.CyberBlue,      Localized("ThemeNameCyberBlue",      "Cyber Blue")),
            new AppThemeOption(AppThemeKind.MidnightPurple, Localized("ThemeNameMidnightPurple", "Midnight Purple")),
            new AppThemeOption(AppThemeKind.NeonGreen,      Localized("ThemeNameNeonGreen",      "Neon Green")),
            new AppThemeOption(AppThemeKind.SolarRed,       Localized("ThemeNameSolarRed",       "Solar Red")),
            new AppThemeOption(AppThemeKind.Light,          Localized("ThemeNameLight",          "Light")),
        ];
    }

    /// <summary>
    /// Resolves the single input provider the app uses on this platform.
    /// Phase 1: SDL3 everywhere — it's the unified cross-platform path
    /// (gamepads, joysticks, wheels) on Windows, Linux, and macOS. The
    /// input picker is hidden, so this is the only input source. When a
    /// Windows RawInput keyboard/mouse provider is added later, branch
    /// here on <see cref="OperatingSystem"/>.
    /// </summary>
    private static string ResolveDefaultInputProviderKey() => "sdl";

    private IReadOnlyList<InputProviderOption> CreateInputProviderOptions()
    {
        return
        [
            new InputProviderOption("sdl",
                Localized("InputProviderSdlLabel",          "SDL3 unified input"),
                Localized("InputProviderSdlDescription",    "Cross-platform SDL3 gamepad mapping plus Raw Input keyboard/mouse synthesis.")),
            new InputProviderOption("demo",
                Localized("InputProviderDemoLabel",         "Demo preview"),
                Localized("InputProviderDemoDescription",   "Animated preview source for UI testing — no hardware required.")),
            new InputProviderOption("none",
                Localized("InputProviderNoneLabel",         "No live input"),
                Localized("InputProviderNoneDescription",   "Turns off live input and leaves the dashboard idle.")),
        ];
    }

    private IReadOnlyList<OutputProviderOption> CreateOutputProviderOptions()
    {
        // One real choice per platform — HIDMaestro is the sole Windows
        // output backend (see OutputProviderPolicy), and it can't run
        // anywhere else, where the in-app preview is all there is.
        // Offering retired/unreachable options here is precisely how "I
        // picked an output and nothing happened" used to occur.
        if (OperatingSystem.IsWindows())
        {
            return
            [
                new OutputProviderOption("hidmaestro",
                    Localized("OutputProviderHidMaestroLabel",         "HIDMaestro virtual controller"),
                    Localized("OutputProviderHidMaestroDescription",   "User-mode virtual controller platform — no kernel driver or reboot. Presents as real hardware to XInput, DirectInput, SDL3 and WGI, with 225 controller profiles plus custom devices. Activates when HIDMaestro.Core.dll is next to the executable and GameFlow runs as Administrator. The sole output backend — if it can't activate, the slot has no output.")),
            ];
        }

        return
        [
            new OutputProviderOption("preview",
                Localized("OutputProviderPreviewLabel",            "Preview only"),
                Localized("OutputProviderPreviewDescription",      "Shows the transformed output in the dashboard without creating a virtual device. HIDMaestro output is Windows-only.")),
        ];
    }

    private IReadOnlyList<ControllerStyleOption> CreateControllerStyleOptions()
    {
        return
        [
            new ControllerStyleOption(ControllerVisualStyle.Auto,         Localized("ControllerStyleAuto",         "Auto")),
            new ControllerStyleOption(ControllerVisualStyle.Xbox,         Localized("ControllerStyleXbox",         "Xbox")),
            new ControllerStyleOption(ControllerVisualStyle.PlayStation4, Localized("ControllerStylePlayStation4", "PlayStation 4")),
            new ControllerStyleOption(ControllerVisualStyle.PlayStation5, Localized("ControllerStylePlayStation5", "PlayStation 5")),
            new ControllerStyleOption(ControllerVisualStyle.None,         Localized("ControllerStyleMinimal",      "Minimal")),
        ];
    }

    /// <summary>
    /// Looks up a localised string. If the key is missing from the
    /// active language's catalog (the localizer returns the key string
    /// itself for unknown keys), falls back to the supplied English
    /// default so labels still render correctly. Used by the option
    /// factories above.
    /// </summary>
    private string Localized(string key, string fallback)
    {
        var hit = localizationService[key];
        return string.IsNullOrEmpty(hit) || string.Equals(hit, key, StringComparison.Ordinal)
            ? fallback
            : hit;
    }

    /// <summary>
    /// Recreates every localised option list (themes, providers, styles)
    /// in the active culture and re-finds the currently-selected option
    /// by its stable key. Called from <see cref="RefreshLocalizedText"/>
    /// whenever the culture changes.
    /// </summary>
    /// <remarks>
    /// The option records are immutable, so flipping cultures means
    /// throwing the old collection away and binding to a new one. We
    /// match the old selection by the language-independent key
    /// (<see cref="AppThemeKind"/>, <see cref="ControllerVisualStyle"/>,
    /// or the provider id string) so the user keeps the same effective
    /// pick across the switch.
    /// </remarks>
    private void RebuildLocalizedOptionLists()
    {
        var previousThemeKind         = SelectedTheme?.Kind;
        var previousInputKey          = SelectedInputProvider?.Key;
        var previousOutputKey         = SelectedOutputProvider?.Key;
        var previousPhysicalStyle     = SelectedPhysicalStyle?.Style;
        var previousVirtualStyle      = SelectedVirtualStyle?.Style;

        ThemeOptions           = CreateThemeOptions();
        InputProviderOptions   = CreateInputProviderOptions();
        OutputProviderOptions  = CreateOutputProviderOptions();
        ControllerStyleOptions = CreateControllerStyleOptions();

        // Avoid running the full theme-persist side effects that fire
        // from the SelectedTheme setter — we're not changing the user's
        // theme, just relabelling the option. Set the backing field
        // directly and notify the binding pipeline manually.
        if (previousThemeKind is { } themeKind)
        {
            var match = ThemeOptions.FirstOrDefault(t => t.Kind == themeKind);
            if (match is not null && !ReferenceEquals(match, selectedTheme))
            {
                selectedTheme = match;
                OnPropertyChanged(nameof(SelectedTheme));
            }
        }

        if (previousInputKey is not null)
        {
            var match = InputProviderOptions.FirstOrDefault(o => o.Key == previousInputKey);
            if (match is not null)
            {
                SelectedInputProvider = match;
            }
        }

        if (previousOutputKey is not null)
        {
            var match = OutputProviderOptions.FirstOrDefault(o => o.Key == previousOutputKey);
            if (match is not null)
            {
                SelectedOutputProvider = match;
            }
        }

        if (previousPhysicalStyle is { } pStyle)
        {
            var match = ControllerStyleOptions.FirstOrDefault(o => o.Style == pStyle);
            if (match is not null)
            {
                SelectedPhysicalStyle = match;
            }
        }

        if (previousVirtualStyle is { } vStyle)
        {
            var match = ControllerStyleOptions.FirstOrDefault(o => o.Style == vStyle);
            if (match is not null)
            {
                SelectedVirtualStyle = match;
            }
        }
    }

    // ─── Misc helpers ─────────────────────────────────────────────────────────

    private string FormatScope(string scope)
    {
        return scope.Equals("virtual", StringComparison.OrdinalIgnoreCase)
            ? localizationService["VirtualOutputLabel"]
            : localizationService["PhysicalInputLabel"];
    }

    private static string FormatElementName(string k)
    {
        return k.Replace("LeftStick",    "Left stick",    StringComparison.OrdinalIgnoreCase)
                .Replace("RightStick",   "Right stick",   StringComparison.OrdinalIgnoreCase)
                .Replace("LeftTrigger",  "Left trigger",  StringComparison.OrdinalIgnoreCase)
                .Replace("RightTrigger", "Right trigger", StringComparison.OrdinalIgnoreCase)
                .Replace(".Analog", " · Analog", StringComparison.OrdinalIgnoreCase)
                .Replace(".Button", " · Click",  StringComparison.OrdinalIgnoreCase)
                .Replace(".Up",     " · Up",     StringComparison.OrdinalIgnoreCase)
                .Replace(".Down",   " · Down",   StringComparison.OrdinalIgnoreCase)
                .Replace(".Left",   " · Left",   StringComparison.OrdinalIgnoreCase)
                .Replace(".Right",  " · Right",  StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseButton(string elementKey, out ButtonId button)
    {
        return ControlRuleMatcher.TryResolveButtonId(elementKey, out button);
    }

    private static bool IsSdlProvider(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        var n = id.Trim().ToLowerInvariant();
        return n is "sdl" or "sdlgamepad" or "sdl-unified" or "sdl3" or "sdl-unified-gamepad";
    }

    /// <summary>
    /// Maps an output-provider identifier (e.g. <c>"vigem-ds5"</c>) to
    /// the visual style that best represents the controller layout the
    /// user expects to see. Returns <see langword="null"/> for providers
    /// whose layout depends on the connected device (e.g. raw passthrough
    /// or preview-only sinks) — the caller should fall through to the
    /// device-name-based resolver in that case.
    /// </summary>
    /// <remarks>
    /// Originally existed because ViGEm Bus had no native DualSense
    /// target (a <c>vigem-ds5</c> sink emitted a DS4-shaped device, so
    /// inferring "DS5" from the device's actual name was impossible).
    /// ViGEm is gone now, but this stays as a general provider-id-to-style
    /// mapping — still useful for any future backend whose device name
    /// doesn't cleanly reveal its kind. Note HIDMaestro's provider string
    /// is always just "hidmaestro" regardless of kind (unlike the old
    /// vigem-ds4/vigem-ds5 split), so this returns null for HIDMaestro
    /// slots today and the caller falls through to the device-name
    /// resolver — a pre-existing gap, not one introduced by removing
    /// ViGEm.
    /// </remarks>
    private static ControllerVisualStyle? InferVirtualStyleFromProvider(string? outputProvider)
    {
        if (string.IsNullOrWhiteSpace(outputProvider))
        {
            return null;
        }

        var n = outputProvider.Trim().ToLowerInvariant();
        return n switch
        {
            "xbox360" or "x360" => ControllerVisualStyle.Xbox,
            "ds4" or "dualshock4" or "playstation4" or "ps4" => ControllerVisualStyle.PlayStation4,
            "ds5" or "dualsense" or "playstation5" or "ps5" => ControllerVisualStyle.PlayStation5,
            _ => null,
        };
    }

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "profile";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder      = new StringBuilder(value.Length);

        foreach (var ch in value.Trim())
        {
            _ = builder.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim().Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "profile" : sanitized;
    }

    // ─── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        localizationService.CultureChanged -= OnCultureChanged;
        profileSession.Changed             -= OnProfileChanged;
        MappingEditor.RulesChanged         -= OnMappingRulesChanged;
        MappingEditor.Dispose();
        DevicesPanel.Dispose();
        rulesSaveGate.Dispose();
        panelBackgroundSaveGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, nameof(ShellViewModel));
    }
}
