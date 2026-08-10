using System.Collections.ObjectModel;
using System.Windows.Input;
using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Runtime;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace GameFlow.App.ViewModels;

/// <summary>One "treat as" choice in the Devices page's category-override picker. Null Category means Auto (detected, no override).</summary>
public sealed record DeviceCategoryOption(DeviceCategory? Category, string Label);

/// <summary>
/// View-model for the Devices tab — a device-discovery surface listing
/// every input device the active provider currently sees, with a detail
/// pane for the selected one. Backed entirely by
/// <see cref="InputDeviceCatalog"/>: it mirrors the catalog's device
/// list and selection, and writes selection changes back through
/// <see cref="InputDeviceCatalog.SetSelectedDevice"/>.
///
/// <para>
/// Note on scope: per-device live raw axis/button visualization (like
/// the the original reference) isn't possible from this layer — only the
/// active/selected device produces a live snapshot, and that's already
/// shown on the Dashboard. This view is the discovery + identity
/// surface: what's connected, its VID/PID, and which one is active.
/// </para>
/// </summary>
public sealed class DevicesViewModel : ViewModelBase, IDisposable
{
    private readonly InputDeviceCatalog catalog;
    private readonly ILocalizationService localization;
    private readonly GameFlow.Infrastructure.Runtime.Input.ButtonMapStore buttonMapStore;
    private readonly GameFlow.Infrastructure.Runtime.Input.IKeyboardStateSource keyboardStateSource;
    private readonly GameFlow.Infrastructure.Runtime.Input.IMouseStateSource mouseStateSource;
    private readonly DeviceCategoryOverrideStore categoryOverrides;
    private readonly DispatcherTimer keyboardPreviewTimer;

    private DeviceRowViewModel? selectedDevice;
    private bool suppressSelectionWriteback;
    private bool isViewActive;
    private bool isRebuilding;
    private bool rebuildQueued;
    private bool rawQueued;

    // Calibration wizard state.
    private int calibrationIndex = -1;
    private readonly Dictionary<ButtonId, int> capturedMap = new();
    private HashSet<int> lastPressedRaw = [];

    public DevicesViewModel(InputDeviceCatalog catalog, ILocalizationService localization, GameFlow.Infrastructure.Runtime.Templates.DeviceTemplateStore templateStore, GameFlow.Infrastructure.Runtime.Input.ButtonMapStore buttonMapStore, GameFlow.Infrastructure.Runtime.Input.IKeyboardStateSource keyboardStateSource, GameFlow.Infrastructure.Runtime.Input.IMouseStateSource mouseStateSource, GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfileCatalogService hidMaestroCatalog, DeviceCategoryOverrideStore categoryOverrides,
        GameFlow.Infrastructure.Runtime.DeviceSettingsStore deviceSettingsStore,
        GameFlow.Infrastructure.Runtime.Slots.SlotRegistry slotRegistry)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));
        this.buttonMapStore = buttonMapStore ?? throw new ArgumentNullException(nameof(buttonMapStore));
        this.keyboardStateSource = keyboardStateSource ?? throw new ArgumentNullException(nameof(keyboardStateSource));
        this.mouseStateSource = mouseStateSource ?? throw new ArgumentNullException(nameof(mouseStateSource));
        this.categoryOverrides = categoryOverrides ?? throw new ArgumentNullException(nameof(categoryOverrides));
        this.slotRegistry = slotRegistry ?? throw new ArgumentNullException(nameof(slotRegistry));
        DeviceSettingsEditor = new DeviceSettingsEditorViewModel(
            deviceSettingsStore ?? throw new ArgumentNullException(nameof(deviceSettingsStore)));
        ResetTuningCommand = new RelayCommand(() => DeviceSettingsEditor.ResetAll());
        RefreshTuningSlots();

        TemplateEditor = new DeviceTemplateEditorViewModel(
            templateStore ?? throw new ArgumentNullException(nameof(templateStore)),
            localization,
            hidMaestroCatalog ?? throw new ArgumentNullException(nameof(hidMaestroCatalog)));

        keyboardPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        keyboardPreviewTimer.Tick += OnInputPreviewTick;

        RefreshCommand = new RelayCommand(Rebuild);
        StartCalibrationCommand = new RelayCommand(StartCalibration, () => SelectedDevice is not null && !IsCalibrating);
        SkipButtonCommand = new RelayCommand(SkipButton, () => IsCalibrating);
        CancelCalibrationCommand = new RelayCommand(CancelCalibration, () => IsCalibrating);
        ClearButtonMapCommand = new RelayCommand(ClearButtonMap, () => HasButtonMap);

        this.catalog.Updated += OnCatalogUpdated;
        this.catalog.RawInspectionUpdated += OnRawInspectionUpdated;
        this.localization.CultureChanged += OnCultureChanged;

        Rebuild();
    }

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand StartCalibrationCommand { get; }
    public ICommand SkipButtonCommand { get; }
    public ICommand CancelCalibrationCommand { get; }
    public ICommand ClearButtonMapCommand { get; }

    /// <summary>True while the press-to-detect button calibration is running.</summary>
    public bool IsCalibrating => calibrationIndex >= 0;

    /// <summary>True when the selected device has a saved button remap.</summary>
    public bool HasButtonMap => SelectedDevice is not null && buttonMapStore.Has(SelectedDevice.Id);

    /// <summary>Selected device is a gamepad/joystick — i.e. button calibration applies.</summary>
    public bool IsCalibratableSelected =>
        SelectedDevice?.Category is DeviceCategory.Gamepad or DeviceCategory.Joystick;

    /// <summary>Selected device is a keyboard — show the keyboard-as-gamepad reference.</summary>
    public bool IsKeyboardSelected => SelectedDevice?.Category == DeviceCategory.Keyboard;

    /// <summary>
    /// "Treat as" options for the selected device's category override —
    /// the escape hatch for devices Windows/SDL misidentifies (a
    /// HID-only wheel reported as Unknown, a gamepad-shaped device that
    /// falls through to keyboard/mouse heuristics, or the reverse).
    /// Auto (first entry, null Category) clears any override.
    /// </summary>
    public IReadOnlyList<DeviceCategoryOption> CategoryOverrideOptions { get; } =
    [
        new(null, "Auto (detected)"),
        new(DeviceCategory.Gamepad, "Gamepad"),
        new(DeviceCategory.Joystick, "Joystick"),
        new(DeviceCategory.Keyboard, "Keyboard"),
        new(DeviceCategory.Mouse, "Mouse"),
    ];

    /// <summary>True once a device is selected — gates the override picker's visibility.</summary>
    public bool HasCategoryOverrideTarget => SelectedDevice is not null;

    /// <summary>
    /// The active override for the selected device (Auto = no override).
    /// Setting this writes through to <see cref="DeviceCategoryOverrideStore"/>
    /// immediately; the catalog re-merges and every consumer (dashboard,
    /// slot device lists, theme resolution) picks up the correction.
    /// </summary>
    public DeviceCategoryOption SelectedCategoryOverride
    {
        get
        {
            var overridden = SelectedDevice is null ? null : categoryOverrides.GetOrNull(SelectedDevice.Id);
            return CategoryOverrideOptions.FirstOrDefault(o => o.Category == overridden) ?? CategoryOverrideOptions[0];
        }
        set
        {
            if (SelectedDevice is null)
            {
                return;
            }
            categoryOverrides.Set(SelectedDevice.Id, value.Category ?? DeviceCategory.Unknown);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// A selected keyboard is always presented as a gamepad — the opt-out
    /// checkbox was removed (the preview is the whole point of selecting a
    /// keyboard, and it costs nothing while hidden).
    /// </summary>
    public bool ShowKeyboardGamepadPreview => IsKeyboardSelected;

    /// <summary>Selected device is a mouse — show the mouse-as-gamepad preview.</summary>
    public bool IsMouseSelected => SelectedDevice?.Category == DeviceCategory.Mouse;

    // ─── Live state bound to KeyboardSurface / MouseSurface ──────────

    private IReadOnlySet<int> pressedKeysSet = new HashSet<int>();
    /// <summary>Current pressed-VK set for the selected keyboard (drives KeyboardSurface highlights).</summary>
    public IReadOnlySet<int> PressedKeysSet
    {
        get => pressedKeysSet;
        private set => SetProperty(ref pressedKeysSet, value);
    }

    private bool isMouseLeftDown;
    public bool IsMouseLeftDown { get => isMouseLeftDown; private set => SetProperty(ref isMouseLeftDown, value); }
    private bool isMouseRightDown;
    public bool IsMouseRightDown { get => isMouseRightDown; private set => SetProperty(ref isMouseRightDown, value); }
    private bool isMouseMiddleDown;
    public bool IsMouseMiddleDown { get => isMouseMiddleDown; private set => SetProperty(ref isMouseMiddleDown, value); }
    private bool isMouseButton4Down;
    public bool IsMouseButton4Down { get => isMouseButton4Down; private set => SetProperty(ref isMouseButton4Down, value); }
    private bool isMouseButton5Down;
    public bool IsMouseButton5Down { get => isMouseButton5Down; private set => SetProperty(ref isMouseButton5Down, value); }
    private bool isMouseScrollUp;
    /// <summary>Wheel scrolled up during the last preview frame (transient, ~1 tick).</summary>
    public bool IsMouseScrollUp { get => isMouseScrollUp; private set => SetProperty(ref isMouseScrollUp, value); }
    private bool isMouseScrollDown;
    /// <summary>Wheel scrolled down during the last preview frame (transient, ~1 tick).</summary>
    public bool IsMouseScrollDown { get => isMouseScrollDown; private set => SetProperty(ref isMouseScrollDown, value); }

    private Point mouseAimEndPoint = new(30, 30);
    /// <summary>Endpoint (Canvas coords) for the MouseSurface aim-direction line.</summary>
    public Point MouseAimEndPoint { get => mouseAimEndPoint; private set => SetProperty(ref mouseAimEndPoint, value); }

    private string mouseInfo = "(no movement)";
    /// <summary>Diagnostic: the selected mouse's movement + button state.</summary>
    public string MouseInfo
    {
        get => mouseInfo;
        private set => SetProperty(ref mouseInfo, value);
    }

    private string keyboardKeysDown = "(none)";
    /// <summary>Diagnostic: the raw virtual-key codes currently down on the selected keyboard.</summary>
    public string KeyboardKeysDown
    {
        get => keyboardKeysDown;
        private set => SetProperty(ref keyboardKeysDown, value);
    }

    private static Point ComputeMouseAimEndpoint(int dx, int dy)
    {
        // Canvas is 60x60; centered at (30,30). Scale movement toward edge,
        // clamped to ~25 px so the arrow stays inside the surrounding ellipse.
        if (dx == 0 && dy == 0)
        {
            return new Point(30, 30);
        }
        double mag = Math.Sqrt((double)dx * dx + (double)dy * dy);
        double scale = Math.Min(25.0, mag * 0.8);
        double nx = dx / mag * scale;
        double ny = dy / mag * scale;
        return new Point(30 + nx, 30 + ny);
    }

    private void OnInputPreviewTick(object? sender, EventArgs e)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        if (device.Category == DeviceCategory.Keyboard)
        {
            var pressed = keyboardStateSource.GetPressedKeys(device.Id);

            // Change-gate: a keyboard at rest produces the same (usually
            // empty) set every tick — rebuilding the display string and
            // firing property-changed 30x/sec for identical state is pure
            // UI-thread waste. Only publish when the set actually changed.
            if (PressedKeysSet is not null && SetEquals(PressedKeysSet, pressed))
            {
                return;
            }

            PressedKeysSet = pressed;
            KeyboardKeysDown = pressed.Count == 0
                ? "(none)"
                : string.Join("  ", pressed
                    .OrderBy(v => v)
                    .Select(GameFlow.Infrastructure.Runtime.Input.VirtualKeyNames.GetName));
        }
        else if (device.Category == DeviceCategory.Mouse)
        {
            var frame = mouseStateSource.ReadMouseFrame(device.Id);
            var btns = string.Concat(
                frame.Left ? "L" : "·", frame.Right ? "R" : "·", frame.Middle ? "M" : "·",
                frame.Button4 ? "4" : "·", frame.Button5 ? "5" : "·");
            IsMouseScrollUp   = frame.WheelDelta > 0;
            IsMouseScrollDown = frame.WheelDelta < 0;
            var wheel = frame.WheelDelta == 0 ? "  --" : $"{frame.WheelDelta,4:+#;-#}";
            MouseInfo = $"move {frame.Dx,4},{frame.Dy,4}   wheel {wheel}   buttons {btns}";

            IsMouseLeftDown    = frame.Left;
            IsMouseRightDown   = frame.Right;
            IsMouseMiddleDown  = frame.Middle;
            IsMouseButton4Down = frame.Button4;
            IsMouseButton5Down = frame.Button5;
            MouseAimEndPoint   = ComputeMouseAimEndpoint(frame.Dx, frame.Dy);
        }
    }

    private static bool SetEquals(IReadOnlySet<int> a, IReadOnlySet<int> b)
    {
        if (ReferenceEquals(a, b)) { return true; }
        if (a.Count != b.Count) { return false; }
        foreach (var item in a)
        {
            if (!b.Contains(item)) { return false; }
        }
        return true;
    }

    /// <summary>Prompt shown during calibration (which button to press + progress).</summary>
    public string CalibrationPrompt =>
        IsCalibrating && calibrationIndex < CalibrationTargets.Length
            ? $"Press: {CalibrationTargets[calibrationIndex].Label}   ({calibrationIndex + 1} / {CalibrationTargets.Length})"
            : string.Empty;

    /// <summary>Editor for the selected device's HidMaestro output template.</summary>
    public DeviceTemplateEditorViewModel TemplateEditor { get; }

    /// <summary>Per-device tuning editor (sticks, triggers, rumble, lighting, adaptive triggers).</summary>
    public DeviceSettingsEditorViewModel DeviceSettingsEditor { get; }

    /// <summary>Slots the selected device can be tuned for — tuning is per slot AND per device.</summary>
    public ObservableCollection<GameFlow.Infrastructure.Runtime.Slots.ControllerSlot> TuningSlotOptions { get; } = [];

    public IRelayCommand ResetTuningCommand { get; }

    private readonly GameFlow.Infrastructure.Runtime.Slots.SlotRegistry slotRegistry;
    private GameFlow.Infrastructure.Runtime.Slots.ControllerSlot? selectedTuningSlot;

    public GameFlow.Infrastructure.Runtime.Slots.ControllerSlot? SelectedTuningSlot
    {
        get => selectedTuningSlot;
        set
        {
            if (SetProperty(ref selectedTuningSlot, value))
            {
                LoadTuningForSelection();
            }
        }
    }

    /// <summary>Repopulates the slot picker, keeping the current pick when it still exists.</summary>
    private void RefreshTuningSlots()
    {
        var previousId = selectedTuningSlot?.Id;
        TuningSlotOptions.Clear();
        foreach (var slot in slotRegistry.GetSlots())
        {
            TuningSlotOptions.Add(slot);
        }
        selectedTuningSlot = TuningSlotOptions.FirstOrDefault(s => s.Id == previousId)
            ?? TuningSlotOptions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedTuningSlot));
    }

    /// <summary>Points the tuning editor at the current device + slot pair.</summary>
    private void LoadTuningForSelection()
    {
        // Both halves are required: settings are keyed by slot AND
        // device, so neither alone identifies an entry.
        if (SelectedDevice is null || selectedTuningSlot is null)
        {
            DeviceSettingsEditor.Load(string.Empty, string.Empty, string.Empty);
            return;
        }
        DeviceSettingsEditor.Load(selectedTuningSlot.Id, SelectedDevice.Id, SelectedDevice.DisplayName);
    }

    /// <summary>Axes of the inspected device (raw, live).</summary>
    public ObservableCollection<RawAxisRowViewModel> RawAxes { get; } = [];

    /// <summary>Buttons of the inspected device (raw, live).</summary>
    public ObservableCollection<RawButtonRowViewModel> RawButtons { get; } = [];

    /// <summary>Hats/POVs of the inspected device (raw, live).</summary>
    public ObservableCollection<RawHatRowViewModel> RawHats { get; } = [];

    /// <summary>
    /// True while the Devices tab is the active tab — bound from
    /// <c>TabItem.IsSelected</c>. Raw polling only runs while this is set,
    /// so an unwatched tab costs nothing.
    /// </summary>
    public bool IsViewActive
    {
        get => isViewActive;
        set
        {
            if (SetProperty(ref isViewActive, value))
            {
                UpdateInspectionTarget();
            }
        }
    }

    public DeviceRowViewModel? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (!SetProperty(ref selectedDevice, value))
            {
                return;
            }
            OnPropertyChanged(nameof(HasSelectedDevice));

            // Push the selection back into the catalog so the runtime
            // switches its active input device — unless we're the ones
            // who just set it during a Rebuild (avoids a feedback loop).
            if (!suppressSelectionWriteback)
            {
                catalog.SetSelectedDevice(value?.Id);
            }

            UpdateInspectionTarget();
            TemplateEditor.LoadFor(value?.Id, value?.Category ?? DeviceCategory.Unknown);
            RefreshTuningSlots();
            LoadTuningForSelection();
            if (IsCalibrating)
            {
                CancelCalibration();
            }
            OnPropertyChanged(nameof(HasButtonMap));
            OnPropertyChanged(nameof(IsKeyboardSelected));
            OnPropertyChanged(nameof(IsMouseSelected));
            OnPropertyChanged(nameof(HasCategoryOverrideTarget));
            OnPropertyChanged(nameof(SelectedCategoryOverride));
            OnPropertyChanged(nameof(IsCalibratableSelected));
            OnPropertyChanged(nameof(ShowKeyboardGamepadPreview));
            if (IsKeyboardSelected || IsMouseSelected)
            {
                KeyboardKeysDown = "(none)";
                MouseInfo = "(no movement)";
                keyboardPreviewTimer.Start();
            }
            else
            {
                keyboardPreviewTimer.Stop();
            }
            (StartCalibrationCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (ClearButtonMapCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelectedDevice => SelectedDevice is not null;

    /// <summary>True when the inspected device is reporting any axes/buttons/hats.</summary>
    public bool HasRawState => RawAxes.Count > 0 || RawButtons.Count > 0 || RawHats.Count > 0;

    public string ProviderStatus => catalog.ProviderStatus;

    public int OnlineCount => Devices.Count(d => d.IsConnected);
    public int TotalCount => Devices.Count;

    /// <summary>True when no devices are visible — drives the empty-state placeholder.</summary>
    public bool IsEmpty => Devices.Count == 0;

    // ─── Localized labels (live via CultureChanged) ───────────────────
    public string TitleLabel        => localization["DevicesTitle"];
    public string RefreshLabel      => localization["CommonRefresh"];
    public string OnlineTotalLabel  => localization["DevicesOnlineTotal"];
    public string TotalLabel        => localization["DevicesTotal"];
    public string ProductLabel      => localization["DevicesProduct"];
    public string VidPidLabel       => localization["DevicesVIDPID"];
    public string HardwareIdLabel   => localization["DevicesHardwareId"];
    public string ActiveDeviceLabel => localization["DevicesActiveDevice"];
    public string NoSelectionLabel  => localization["DevicesNoSelection"];
    public string EmptyListLabel    => localization["DevicesEmptyList"];
    public string KeyboardHintLabel => localization["DevicesKeyboardHint"];
    public string MouseHintLabel    => localization["DevicesMouseHint"];
    public string KeysDownLabel     => localization["DevicesKeysDownLabel"];
    public string InputLiveLabel    => localization["DevicesInputLiveLabel"];

    private void OnCatalogUpdated(object? sender, EventArgs e)
    {
        // Always defer to the UI thread AND coalesce: a selection change
        // raises Updated synchronously, and running Rebuild inline there
        // would re-enter the ListBox selection path. Posting (even when
        // already on the UI thread) breaks that reentrancy; the queued
        // flag caps the work to one pending rebuild regardless of how
        // often the catalog fires.
        if (rebuildQueued)
        {
            return;
        }
        rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        if (isRebuilding)
        {
            return;
        }
        isRebuilding = true;
        try
        {
            // Order by category (gamepad, joystick, keyboard, mouse; unknown
            // last), then by name — so device types cluster instead of being
            // jumbled together.
            var ordered = catalog.Devices
                .OrderBy(d => d.Category == DeviceCategory.Unknown ? int.MaxValue : (int)d.Category)
                .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Remove rows whose device disappeared.
            for (var i = Devices.Count - 1; i >= 0; i--)
            {
                if (!ordered.Any(s => string.Equals(s.Id, Devices[i].Id, StringComparison.Ordinal)))
                {
                    Devices.RemoveAt(i);
                }
            }

            // Insert / update / reorder to match the target order. Existing
            // row objects are updated in place (never replaced) and moved,
            // so the selected row survives and the ListBox selection holds.
            for (var target = 0; target < ordered.Count; target++)
            {
                var info = ordered[target];
                var current = -1;
                for (var i = 0; i < Devices.Count; i++)
                {
                    if (string.Equals(Devices[i].Id, info.Id, StringComparison.Ordinal))
                    {
                        current = i;
                        break;
                    }
                }

                if (current < 0)
                {
                    Devices.Insert(Math.Min(target, Devices.Count), new DeviceRowViewModel(info));
                }
                else
                {
                    Devices[current].Apply(info);
                    if (current != target)
                    {
                        Devices.Move(current, target);
                    }
                }
            }

            // Reflect the catalog's selection without writing back.
            var desiredId = SelectedDevice?.Id ?? catalog.SelectedDeviceId;
            var desired = Devices.FirstOrDefault(d => string.Equals(d.Id, desiredId, StringComparison.Ordinal))
                          ?? Devices.FirstOrDefault(d => d.IsSelected);
            if (!ReferenceEquals(desired, SelectedDevice))
            {
                suppressSelectionWriteback = true;
                SelectedDevice = desired;
                suppressSelectionWriteback = false;
            }

            OnPropertyChanged(nameof(ProviderStatus));
            OnPropertyChanged(nameof(OnlineCount));
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            isRebuilding = false;
        }
    }

    private void UpdateInspectionTarget()
    {
        // Inspect only when the tab is active and a device is selected.
        var target = IsViewActive ? SelectedDevice?.Id : null;
        catalog.SetRawInspectionTarget(target);

        if (target is null)
        {
            ClearRawState();
        }
    }

    private void OnRawInspectionUpdated(object? sender, EventArgs e)
    {
        // Coalesce: the source publishes every input tick. Keep at most
        // one ApplyRawSnapshot queued so a fast loop can't outrun the UI.
        if (rawQueued)
        {
            return;
        }
        rawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            rawQueued = false;
            ApplyRawSnapshot();
        });
    }

    private void ApplyRawSnapshot()
    {
        var snapshot = catalog.RawInspection;

        // Ignore snapshots for a device we're no longer showing.
        if (snapshot is null || !string.Equals(snapshot.DeviceId, SelectedDevice?.Id, StringComparison.Ordinal))
        {
            ClearRawState();
            return;
        }

        SyncAxes(snapshot.Axes);
        SyncButtons(snapshot.Buttons);
        SyncHats(snapshot.Hats);
        OnPropertyChanged(nameof(HasRawState));

        if (IsCalibrating)
        {
            CaptureCalibrationPress();
        }
    }

    // ─── Button calibration wizard ────────────────────────────────────

    private static readonly (ButtonId Id, string Label)[] CalibrationTargets =
    [
        (ButtonId.South, "bottom face button (A / Cross)"),
        (ButtonId.East, "right face button (B / Circle)"),
        (ButtonId.West, "left face button (X / Square)"),
        (ButtonId.North, "top face button (Y / Triangle)"),
        (ButtonId.LeftShoulder, "left shoulder (LB / L1)"),
        (ButtonId.RightShoulder, "right shoulder (RB / R1)"),
        (ButtonId.Back, "Back / Select / Share"),
        (ButtonId.Start, "Start / Options"),
        (ButtonId.Guide, "Guide / PS / Home"),
        (ButtonId.LeftStick, "left stick click (L3)"),
        (ButtonId.RightStick, "right stick click (R3)"),
        (ButtonId.DpadUp, "D-pad Up"),
        (ButtonId.DpadDown, "D-pad Down"),
        (ButtonId.DpadLeft, "D-pad Left"),
        (ButtonId.DpadRight, "D-pad Right"),
    ];

    private void StartCalibration()
    {
        if (SelectedDevice is null)
        {
            return;
        }
        capturedMap.Clear();
        // Ignore buttons already held when the wizard starts.
        lastPressedRaw = RawButtons.Where(b => b.IsPressed).Select(b => b.Index).ToHashSet();
        calibrationIndex = 0;
        NotifyCalibrationState();
    }

    private void CaptureCalibrationPress()
    {
        var pressedNow = RawButtons.Where(b => b.IsPressed).Select(b => b.Index).ToHashSet();
        // Rising edge: a raw button pressed now that wasn't pressed last tick.
        int? captured = null;
        foreach (var index in pressedNow)
        {
            if (!lastPressedRaw.Contains(index))
            {
                captured = index;
                break;
            }
        }
        lastPressedRaw = pressedNow;

        if (captured is null)
        {
            return;
        }

        capturedMap[CalibrationTargets[calibrationIndex].Id] = captured.Value;
        Advance();
    }

    private void SkipButton()
    {
        if (IsCalibrating)
        {
            Advance();
        }
    }

    private void Advance()
    {
        calibrationIndex++;
        if (calibrationIndex >= CalibrationTargets.Length)
        {
            FinishCalibration();
        }
        else
        {
            OnPropertyChanged(nameof(CalibrationPrompt));
        }
    }

    private void FinishCalibration()
    {
        if (SelectedDevice is not null && capturedMap.Count > 0)
        {
            buttonMapStore.Save(new GameFlow.Infrastructure.Runtime.Input.DeviceButtonMap
            {
                DeviceId = SelectedDevice.Id,
                Buttons = new Dictionary<ButtonId, int>(capturedMap),
            });
        }
        calibrationIndex = -1;
        NotifyCalibrationState();
    }

    private void CancelCalibration()
    {
        calibrationIndex = -1;
        capturedMap.Clear();
        NotifyCalibrationState();
    }

    private void ClearButtonMap()
    {
        if (SelectedDevice is not null)
        {
            buttonMapStore.Remove(SelectedDevice.Id);
        }
        NotifyCalibrationState();
    }

    private void NotifyCalibrationState()
    {
        OnPropertyChanged(nameof(IsCalibrating));
        OnPropertyChanged(nameof(CalibrationPrompt));
        OnPropertyChanged(nameof(HasButtonMap));
        (StartCalibrationCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SkipButtonCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CancelCalibrationCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ClearButtonMapCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void SyncAxes(IReadOnlyList<short> values)
    {
        // Rebuild only when the count changes; otherwise update in place
        // so the 60 Hz cadence doesn't thrash the collection.
        if (RawAxes.Count != values.Count)
        {
            RawAxes.Clear();
            for (var i = 0; i < values.Count; i++)
            {
                RawAxes.Add(new RawAxisRowViewModel(i));
            }
        }
        for (var i = 0; i < values.Count; i++)
        {
            RawAxes[i].Update(values[i]);
        }
    }

    private void SyncButtons(IReadOnlyList<bool> values)
    {
        if (RawButtons.Count != values.Count)
        {
            RawButtons.Clear();
            for (var i = 0; i < values.Count; i++)
            {
                RawButtons.Add(new RawButtonRowViewModel(i));
            }
        }
        for (var i = 0; i < values.Count; i++)
        {
            RawButtons[i].Update(values[i]);
        }
    }

    private void SyncHats(IReadOnlyList<byte> values)
    {
        if (RawHats.Count != values.Count)
        {
            RawHats.Clear();
            for (var i = 0; i < values.Count; i++)
            {
                RawHats.Add(new RawHatRowViewModel(i));
            }
        }
        for (var i = 0; i < values.Count; i++)
        {
            RawHats[i].Update(values[i]);
        }
    }

    private void ClearRawState()
    {
        if (RawAxes.Count == 0 && RawButtons.Count == 0 && RawHats.Count == 0)
        {
            return;
        }
        RawAxes.Clear();
        RawButtons.Clear();
        RawHats.Clear();
        OnPropertyChanged(nameof(HasRawState));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleLabel));
        OnPropertyChanged(nameof(RefreshLabel));
        OnPropertyChanged(nameof(OnlineTotalLabel));
        OnPropertyChanged(nameof(TotalLabel));
        OnPropertyChanged(nameof(ProductLabel));
        OnPropertyChanged(nameof(VidPidLabel));
        OnPropertyChanged(nameof(HardwareIdLabel));
        OnPropertyChanged(nameof(ActiveDeviceLabel));
        OnPropertyChanged(nameof(NoSelectionLabel));
        OnPropertyChanged(nameof(EmptyListLabel));
        OnPropertyChanged(nameof(KeyboardHintLabel));
        OnPropertyChanged(nameof(MouseHintLabel));
        OnPropertyChanged(nameof(KeysDownLabel));
        OnPropertyChanged(nameof(InputLiveLabel));
        OnPropertyChanged(nameof(ProviderStatus));
    }

    public void Dispose()
    {
        catalog.SetRawInspectionTarget(null);
        catalog.Updated -= OnCatalogUpdated;
        catalog.RawInspectionUpdated -= OnRawInspectionUpdated;
        localization.CultureChanged -= OnCultureChanged;
    }
}
