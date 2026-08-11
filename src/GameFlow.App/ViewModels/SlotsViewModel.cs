using System.Collections.ObjectModel;
using System.Windows.Input;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Slots;
using GameFlow.Infrastructure.Runtime.Templates;
using GameFlow.Infrastructure.Profiles;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace GameFlow.App.ViewModels;

/// <summary>Row in the slots list (display only).</summary>
public sealed class SlotRowViewModel : ViewModelBase
{
    public SlotRowViewModel(ControllerSlot slot, IReadOnlyList<InputDeviceInfo>? devices = null)
    {
        Id = slot.Id;
        Apply(slot, devices);
    }

    public string Id { get; }

    private string name = string.Empty;
    public string Name { get => name; private set => SetProperty(ref name, value); }

    private int index;
    public int Index { get => index; private set => SetProperty(ref index, value); }

    private string kindLabel = string.Empty;
    public string KindLabel { get => kindLabel; private set => SetProperty(ref kindLabel, value); }

    private bool enabled;
    public bool Enabled { get => enabled; private set => SetProperty(ref enabled, value); }

    private string deviceSummary = string.Empty;
    public string DeviceSummary { get => deviceSummary; private set => SetProperty(ref deviceSummary, value); }

    private string statusLabel = string.Empty;
    public string StatusLabel { get => statusLabel; private set => SetProperty(ref statusLabel, value); }

    private string statusBrush = "#64748B";
    public string StatusBrush { get => statusBrush; private set => SetProperty(ref statusBrush, value); }

    private string outputIcon = "HID";
    public string OutputIcon { get => outputIcon; private set => SetProperty(ref outputIcon, value); }

    private string profileSummary = string.Empty;
    public string ProfileSummary { get => profileSummary; private set => SetProperty(ref profileSummary, value); }

    public string SlotLabel => $"SLOT {Index}";

    public void Apply(ControllerSlot slot, IReadOnlyList<InputDeviceInfo>? devices = null)
    {
        Name = slot.Name;
        Index = slot.Index;
        KindLabel = SlotsViewModel.KindLabelFor(slot.OutputTemplate);
        Enabled = slot.Enabled;
        OnPropertyChanged(nameof(SlotLabel));

        OutputIcon = slot.OutputTemplate.OutputKind switch
        {
            VirtualControllerKind.Xbox360 or VirtualControllerKind.XboxOne or VirtualControllerKind.XboxSeries => "X",
            VirtualControllerKind.DualShock4 or VirtualControllerKind.DualSense => "PS",
            VirtualControllerKind.SwitchPro => "N",
            VirtualControllerKind.SteamController => "S",
            _ => "HID",
        };

        var assigned = slot.InputDeviceIds
            .Select(id => devices?.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var connected = assigned.Where(device => device?.IsConnected == true).Cast<InputDeviceInfo>().ToArray();

        DeviceSummary = slot.OutputTemplate.DemoPreview
            ? "Animated demo input"
            : slot.InputDeviceIds.Count == 0
                ? "No physical device assigned"
                : connected.Length == 0
                    ? "Assigned device is offline"
                    : connected.Length == 1
                        ? connected[0].DisplayName
                        : $"{connected[0].DisplayName} +{connected.Length - 1}";

        ProfileSummary = slot.ProfileIds.Count switch
        {
            0 => "Pass-through mapping",
            1 => "1 profile layer",
            _ => $"{slot.ProfileIds.Count} profile layers",
        };

        (StatusLabel, StatusBrush) = !slot.Enabled
            ? ("Disabled", "#64748B")
            : slot.OutputTemplate.DemoPreview
                ? ("Demo", "#38BDF8")
                : slot.InputDeviceIds.Count == 0
                    ? ("Needs input", "#F59E0B")
                    : connected.Length == 0
                        ? ("Offline", "#F87171")
                        : ("Ready", "#4ADE80");
    }
}

/// <summary>A device that can be assigned/unassigned to the selected slot.</summary>
public sealed class AssignableDeviceRow(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
}

/// <summary>
/// Reconciles an <see cref="ObservableCollection{T}"/> against a desired
/// sequence instead of clearing and refilling it.
///
/// <para>
/// Clear-then-refill is why the Add and Remove buttons blinked. These
/// lists are rebuilt whenever the device catalog or the slot registry
/// raises a change, which is often, and every rebuild replaced every row —
/// so Avalonia tore down each row's controls and built new ones, and the
/// buttons visibly flashed. The navigation column had the identical bug
/// and the identical fix; this is that fix, generalised, so the next list
/// does not have to rediscover it.
/// </para>
///
/// <para>
/// A row whose key AND content are unchanged is left alone entirely,
/// which is what keeps its controls — and their hover and focus state —
/// alive across a refresh.
/// </para>
/// </summary>
public static class RowSync
{
    /// <param name="key">
    /// Row identity. Matched case-insensitively, because catalog ids come
    /// from several backends that do not agree on case.
    /// </param>
    /// <param name="sameContent">
    /// Compares only what is DISPLAYED. It must not look at the key: two
    /// rows only reach this check because their keys already matched, and
    /// including the key means a device whose id differs only in case
    /// compares unequal and gets replaced — throwing away the controls
    /// this whole reconciler exists to keep. Record equality is therefore
    /// the wrong thing to pass here.
    /// </param>
    public static void Apply<T>(
        ObservableCollection<T> rows,
        IReadOnlyList<T> target,
        Func<T, string> key,
        Func<T, T, bool> sameContent)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var existing = rows[i];
            if (!target.Any(t => string.Equals(key(t), key(existing), StringComparison.OrdinalIgnoreCase)))
            {
                rows.RemoveAt(i);
            }
        }

        for (var idx = 0; idx < target.Count; idx++)
        {
            var wanted = target[idx];
            var at = IndexOfKey(rows, key, key(wanted));

            if (at < 0)
            {
                rows.Insert(Math.Min(idx, rows.Count), wanted);
                continue;
            }

            // Same identity, different content — replace in place rather
            // than remove-then-insert, which would also drop the controls.
            if (!sameContent(rows[at], wanted))
            {
                rows[at] = wanted;
            }

            if (at != idx && idx < rows.Count)
            {
                rows.Move(at, idx);
            }
        }
    }

    private static int IndexOfKey<T>(ObservableCollection<T> rows, Func<T, string> key, string wanted)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(key(rows[i]), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed class SlotsViewModel : ViewModelBase, IDisposable
{
    private readonly SlotRegistry registry;
    private readonly InputDeviceCatalog catalog;
    private readonly ProfileSession profileSession;
    private readonly DeviceSettingsStore deviceSettingsStore;

    private bool loadingDetail;
    private bool rebuildQueued;
    private bool disposed;

    public SlotsViewModel(SlotRegistry registry, InputDeviceCatalog catalog, DeviceTemplateStore templateStore, ProfileSession profileSession, GameFlow.Infrastructure.Localization.ILocalizationService localization, GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfileCatalogService hidMaestroCatalog, DeviceSettingsStore deviceSettingsStore)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.profileSession = profileSession ?? throw new ArgumentNullException(nameof(profileSession));
        this.deviceSettingsStore = deviceSettingsStore ?? throw new ArgumentNullException(nameof(deviceSettingsStore));
        TemplateEditor = new DeviceTemplateEditorViewModel(
            templateStore ?? throw new ArgumentNullException(nameof(templateStore)),
            localization,
            hidMaestroCatalog ?? throw new ArgumentNullException(nameof(hidMaestroCatalog)));
        TouchpadEditor = new TouchpadEditorViewModel(localization);

        // Demo preview and assigned devices are mutually exclusive — the
        // slot reads EITHER the waveform OR its devices, never both.
        // Turning the checkbox on unassigns whatever was already there
        // (the Available list is separately disabled in XAML so nothing
        // NEW can be added while it's on).
        TemplateEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceTemplateEditorViewModel.DemoPreview) && TemplateEditor.DemoPreview)
            {
                ClearAssignedDevicesForSelectedSlot();
            }
        };

        OutputKindOptions =
        [
            .. GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.SelectableKinds
                .Select(k => new OutputKindOption(k, GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.LabelFor(k))),
        ];
        newSlotKind = OutputKindOptions[0];

        CreateSlotCommand = new RelayCommand(CreateSlot, () => registry.CanCreate);
        DuplicateSlotCommand = new RelayCommand(DuplicateSlot, () => SelectedSlot is not null && registry.CanCreate);
        SaveSlotCommand = new RelayCommand(SaveSlot, () => SelectedSlot is not null);
        DeleteSlotCommand = new RelayCommand(DeleteSlot, () => SelectedSlot is not null);
        AssignDeviceCommand = new RelayCommand<string>(AssignDevice);
        UnassignDeviceCommand = new RelayCommand<string>(UnassignDevice);
        AddProfileCommand = new RelayCommand(AddProfile, () => SelectedSlot is not null && SelectedAvailableProfile is not null);
        RemoveProfileCommand = new RelayCommand<string>(RemoveProfile);
        LoadAvailableProfilesAsync();

        registry.SlotsChanged += OnSlotsChanged;
        catalog.Updated += OnCatalogUpdated;
        this.localization = localization;
        localization.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VirtualControllersHeader));
            OnPropertyChanged(nameof(AddControllerLabel));
            OnPropertyChanged(nameof(SlotEnabledLabel));
            OnPropertyChanged(nameof(SlotDuplicateLabel));
            OnPropertyChanged(nameof(SlotSaveLabel));
            OnPropertyChanged(nameof(SlotDeleteLabel));
            OnPropertyChanged(nameof(TouchpadTabHeader));
            OnPropertyChanged(nameof(SlotSettingsTabHeader));
            OnPropertyChanged(nameof(NoSlotSelectedLabel));
            OnPropertyChanged(nameof(PreviewTabHeader));
            OnPropertyChanged(nameof(OutputTabHeader));
            OnPropertyChanged(nameof(MappingsTabHeader));
        };

        Rebuild();
    }

    private readonly GameFlow.Infrastructure.Localization.ILocalizationService localization;

    public string VirtualControllersHeader =>
        Loc("SidebarVirtualHeader", "VIRTUAL CONTROLLERS");
    public string AddControllerLabel =>
        Loc("SlotsAddControllerLabel", "Add controller");
    public string SlotEnabledLabel =>
        Loc("SlotsEnabledLabel", "Enabled");
    public string SlotDuplicateLabel =>
        Loc("SlotsDuplicateLabel", "Duplicate");
    public string SlotSaveLabel =>
        Loc("SlotsSaveLabel", "Save");
    public string SlotDeleteLabel =>
        Loc("SlotsDeleteLabel", "Delete");

    private string Loc(string key, string fallback)
    {
        var value = localization[key];
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    public ObservableCollection<SlotRowViewModel> Slots { get; } = [];
    public ObservableCollection<AssignableDeviceRow> AssignedDevices { get; } = [];
    public ObservableCollection<AssignableDeviceRow> AvailableDevices { get; } = [];

    /// <summary>Profiles layered onto the selected slot, in order.</summary>
    public ObservableCollection<ProfileSummary> AssignedProfiles { get; } = [];

    /// <summary>All profiles available to add as a layer.</summary>
    public ObservableCollection<ProfileSummary> AvailableProfiles { get; } = [];

    public IReadOnlyList<OutputKindOption> OutputKindOptions { get; }
    public DeviceTemplateEditorViewModel TemplateEditor { get; }

    /// <summary>Backs the Touchpad tab; only meaningful while <see cref="SelectedSlotHasTouchpad"/> is true.</summary>
    public TouchpadEditorViewModel TouchpadEditor { get; }

    private bool selectedSlotHasTouchpad;

    /// <summary>
    /// True when at least one device assigned to the selected slot
    /// carries a touch surface. Drives the Touchpad tab's visibility, so
    /// the tab appears on a DualSense or DualShock 4 slot and stays
    /// hidden on an Xbox pad — where every control on it would be inert.
    /// </summary>
    public bool SelectedSlotHasTouchpad
    {
        get => selectedSlotHasTouchpad;
        private set => SetProperty(ref selectedSlotHasTouchpad, value);
    }

    public string TouchpadTabHeader => Loc("DevicesTouchpadTab", "Touchpad");
    public string SlotSettingsTabHeader => Loc("DevicesSlotSetupTab", "Devices & profiles");

    /// <summary>
    /// Shown in place of the slot detail pane while no slot is selected.
    /// The pane is otherwise simply blank, which on this page occupies
    /// most of the width and reads as a failed load rather than as
    /// "nothing picked yet" — the Devices and mapping-rule pages both
    /// already answer that question in the same spot.
    /// </summary>
    public string NoSlotSelectedLabel =>
        Loc("SlotsNoSelection", "Select a virtual controller to edit its settings.");

    public ICommand CreateSlotCommand { get; }
    public ICommand DuplicateSlotCommand { get; }
    public ICommand SaveSlotCommand { get; }
    public ICommand DeleteSlotCommand { get; }
    public ICommand AssignDeviceCommand { get; }
    public ICommand UnassignDeviceCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }

    private ProfileSummary? selectedAvailableProfile;
    public ProfileSummary? SelectedAvailableProfile
    {
        get => selectedAvailableProfile;
        set
        {
            if (SetProperty(ref selectedAvailableProfile, value))
            {
                (AddProfileCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The kind a newly created controller starts as.
    ///
    /// <para>
    /// No longer bound to a picker beside the Add button. There were two
    /// controller-type combo boxes on that page — one choosing the type of
    /// a slot that did not exist yet, one changing the type of the
    /// selected slot — which read as the same setting in two places
    /// disagreeing. Adding a controller now creates one at this default
    /// and the single picker above the tabs sets its type, where the type
    /// belongs to something real.
    /// </para>
    /// </summary>
    private OutputKindOption newSlotKind;
    public OutputKindOption NewSlotKind
    {
        get => newSlotKind;
        set => SetProperty(ref newSlotKind, value);
    }

    public bool CanCreate => registry.CanCreate;

    private SlotRowViewModel? selectedSlot;
    public SlotRowViewModel? SelectedSlot
    {
        get => selectedSlot;
        set
        {
            if (SetProperty(ref selectedSlot, value))
            {
                LoadDetail();
                OnPropertyChanged(nameof(HasSelectedSlot));
                (DeleteSlotCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (DuplicateSlotCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SaveSlotCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (AddProfileCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedSlot => SelectedSlot is not null;
    public bool HasAssignedDevices => AssignedDevices.Count > 0;
    public bool HasAvailableDevices => AvailableDevices.Count > 0;
    public string SelectedSlotLabel => SelectedSlot?.SlotLabel ?? string.Empty;
    public string SelectedSlotStatus => SelectedSlot?.StatusLabel ?? string.Empty;
    public string SelectedSlotStatusBrush => SelectedSlot?.StatusBrush ?? "#64748B";
    public string SelectedSlotOutput => SelectedSlot?.KindLabel ?? string.Empty;
    public string SelectedSlotInput => SelectedSlot?.DeviceSummary ?? string.Empty;
    public string SelectedSlotProfiles => SelectedSlot?.ProfileSummary ?? string.Empty;
    public string PreviewTabHeader => Loc("DevicesSlotPreviewTab", "Preview");
    public string OutputTabHeader => Loc("DevicesSlotOutputTab", "Output");
    public string MappingsTabHeader => Loc("DevicesSlotMappingsTab", "Mappings");

    private string slotName = string.Empty;
    public string SlotName
    {
        get => slotName;
        set
        {
            if (SetProperty(ref slotName, value) && !loadingDetail && SelectedSlot is not null)
            {
                registry.Rename(SelectedSlot.Id, value);
            }
        }
    }

    private bool slotEnabled;
    public bool SlotEnabled
    {
        get => slotEnabled;
        set
        {
            if (SetProperty(ref slotEnabled, value) && !loadingDetail && SelectedSlot is not null)
            {
                registry.SetEnabled(SelectedSlot.Id, value);
            }
        }
    }

    public static string KindLabelFor(VirtualControllerKind kind) =>
        GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.LabelFor(kind);

    /// <summary>
    /// Row label for a slot's output: the explicit HIDMaestro catalog id
    /// when one is chosen (any of the 225 profiles), the kind label
    /// otherwise.
    /// </summary>
    public static string KindLabelFor(DeviceOutputTemplate template) =>
        string.IsNullOrWhiteSpace(template.OutputProfileId)
            ? KindLabelFor(template.OutputKind)
            : template.OutputProfileId;

    /// <summary>
    /// Sidebar entry point for "+ Add controller": same path as the Add
    /// button on this page (selection follows via pendingSelectId once
    /// SlotsChanged rebuilds the rows).
    /// </summary>
    public void CreateSlotFromSidebar() => CreateSlot();

    private void CreateSlot()
    {
        var created = registry.CreateSlot(NewSlotKind?.Kind ?? VirtualControllerKind.Xbox360);
        if (created is not null)
        {
            // Selection follows after the SlotsChanged-driven rebuild.
            pendingSelectId = created.Id;
        }
    }

    private void DuplicateSlot()
    {
        if (SelectedSlot is null)
        {
            return;
        }
        var created = registry.DuplicateSlot(SelectedSlot.Id);
        if (created is not null)
        {
            pendingSelectId = created.Id;
        }
    }

    /// <summary>
    /// Every field in this editor already persists the instant it changes
    /// (SlotName's setter calls registry.Rename directly, template edits
    /// commit through the same registry, etc.) — there's no pending,
    /// unsaved state to flush. This exists anyway as an explicit,
    /// deliberate confirmation: it re-persists the slot's current values,
    /// which is always safe (idempotent) and gives a concrete moment the
    /// user can point to as "I saved this," rather than trusting silent
    /// auto-save alone.
    /// </summary>
    private void SaveSlot()
    {
        if (SelectedSlot is not null)
        {
            registry.Rename(SelectedSlot.Id, SlotName);
        }
    }

    private void DeleteSlot()
    {
        if (SelectedSlot is not null)
        {
            var slotId = SelectedSlot.Id;
            if (registry.DeleteSlot(slotId))
            {
                deviceSettingsStore.RemoveSlot(slotId);
            }
        }
    }

    private void AssignDevice(string? deviceId)
    {
        if (SelectedSlot is not null && !string.IsNullOrWhiteSpace(deviceId))
        {
            registry.AssignDevice(SelectedSlot.Id, deviceId);
        }
    }

    private void UnassignDevice(string? deviceId)
    {
        if (SelectedSlot is not null && !string.IsNullOrWhiteSpace(deviceId))
        {
            registry.UnassignDevice(SelectedSlot.Id, deviceId);
        }
    }

    private void ClearAssignedDevicesForSelectedSlot()
    {
        if (SelectedSlot is null)
        {
            return;
        }
        // Snapshot first: UnassignDevice → registry.SlotsChanged → a
        // synchronous rebuild of AssignedDevices would otherwise mutate
        // the collection out from under this foreach.
        foreach (var row in AssignedDevices.ToList())
        {
            registry.UnassignDevice(SelectedSlot.Id, row.Id);
        }
    }

    private async void LoadAvailableProfilesAsync()
    {
        try
        {
            var list = await profileSession.ListProfilesAsync();
            Dispatcher.UIThread.Post(() =>
            {
                AvailableProfiles.Clear();
                foreach (var summary in list)
                {
                    AvailableProfiles.Add(summary);
                }
                // Re-resolve names now that the catalog is loaded.
                if (SelectedSlot is not null)
                {
                    LoadDetail();
                }
            });
        }
        catch
        {
            // Non-fatal: the picker just stays empty.
        }
    }

    private void AddProfile()
    {
        if (SelectedSlot is null || SelectedAvailableProfile is null)
        {
            return;
        }
        var slot = registry.GetSlot(SelectedSlot.Id);
        if (slot is null || slot.ProfileIds.Contains(SelectedAvailableProfile.Id))
        {
            return;
        }
        var ids = new List<string>(slot.ProfileIds) { SelectedAvailableProfile.Id };
        registry.SetProfiles(slot.Id, ids);
        LoadDetail();
    }

    private void RemoveProfile(string? profileId)
    {
        if (SelectedSlot is null || string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }
        var slot = registry.GetSlot(SelectedSlot.Id);
        if (slot is null)
        {
            return;
        }
        var ids = slot.ProfileIds.Where(p => p != profileId).ToList();
        registry.SetProfiles(slot.Id, ids);
        LoadDetail();
    }

    private string? pendingSelectId;

    private void OnSlotsChanged(object? sender, EventArgs e) => QueueRebuild();
    private void OnCatalogUpdated(object? sender, EventArgs e) => QueueRebuild();

    private void QueueRebuild()
    {
        if (rebuildQueued || disposed)
        {
            return;
        }
        rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            rebuildQueued = false;
            if (!disposed)
            {
                Rebuild();
            }
        });
    }

    private void Rebuild()
    {
        var target = registry.GetSlots();

        // Remove rows whose slot vanished.
        for (int i = Slots.Count - 1; i >= 0; i--)
        {
            if (target.All(s => s.Id != Slots[i].Id))
            {
                Slots.RemoveAt(i);
            }
        }

        // Add new / update existing, in target order.
        for (int idx = 0; idx < target.Count; idx++)
        {
            var slot = target[idx];
            var existing = Slots.FirstOrDefault(r => r.Id == slot.Id);
            if (existing is null)
            {
                Slots.Insert(Math.Min(idx, Slots.Count), new SlotRowViewModel(slot, catalog.Devices));
            }
            else
            {
                existing.Apply(slot, catalog.Devices);
                int currentIdx = Slots.IndexOf(existing);
                if (currentIdx != idx && idx < Slots.Count)
                {
                    Slots.Move(currentIdx, idx);
                }
            }
        }

        // Honor a pending selection from CreateSlot.
        if (pendingSelectId is not null)
        {
            var match = Slots.FirstOrDefault(r => r.Id == pendingSelectId);
            pendingSelectId = null;
            if (match is not null)
            {
                SelectedSlot = match;
            }
        }
        else if (SelectedSlot is not null)
        {
            // Selected slot may have changed (device assignment etc.) — refresh detail.
            LoadDetail();
        }

        OnPropertyChanged(nameof(CanCreate));
        (CreateSlotCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void LoadDetail()
    {
        loadingDetail = true;
        try
        {
            var slot = SelectedSlot is null ? null : registry.GetSlot(SelectedSlot.Id);
            if (slot is null)
            {
                AssignedDevices.Clear();
                AvailableDevices.Clear();
                AssignedProfiles.Clear();
                SlotName = string.Empty;
                SlotEnabled = false;
                TemplateEditor.Clear();
                TouchpadEditor.Clear();
                SelectedSlotHasTouchpad = false;
                RaiseSelectedSlotSummary();
                return;
            }

            SlotName = slot.Name;
            SlotEnabled = slot.Enabled;

            var slotId = slot.Id;
            TemplateEditor.LoadTemplate(slot.OutputTemplate, t => registry.UpdateTemplate(slotId, t));

            // These three are reconciled rather than rebuilt. LoadDetail
            // runs on every registry and catalog change, and clearing an
            // ObservableCollection destroys every row's controls — which
            // is what made the Add and Remove buttons blink.
            var devices = catalog.Devices;

            // Assigned devices (in slot order), resolving names from the catalog.
            RowSync.Apply(
                AssignedDevices,
                slot.InputDeviceIds
                    .Select(id => new AssignableDeviceRow(
                        id,
                        devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
                            ?.DisplayName ?? id))
                    .ToList(),
                row => row.Id,
                (a, b) => string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal));

            // Available = assignable devices not already on this slot.
            //
            // IsAssignableAsInput excludes GameFlow's own virtual pads.
            // They enumerate through SDL looking exactly like physical
            // hardware — impersonating it is what makes games accept the
            // output — so without that check a slot could be fed from
            // another slot's output, and the chain could be extended
            // until the runtime was mapping itself in a circle.
            RowSync.Apply(
                AvailableDevices,
                devices
                    .Where(d => d.IsAssignableAsInput && !slot.InputDeviceIds.Contains(d.Id))
                    .Select(d => new AssignableDeviceRow(d.Id, d.DisplayName))
                    .ToList(),
                row => row.Id,
                (a, b) => string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal));

            // Layered profiles (in order), resolving names from the catalog.
            RowSync.Apply(
                AssignedProfiles,
                slot.ProfileIds
                    .Select(pid => new ProfileSummary(
                        pid, AvailableProfiles.FirstOrDefault(p => p.Id == pid)?.Name ?? pid))
                    .ToList(),
                row => row.Id,
                (a, b) => string.Equals(a.Name, b.Name, StringComparison.Ordinal));

            // The Touchpad tab follows the hardware: it shows as soon as
            // any assigned device reports a touch surface. Settings are
            // kept even while the tab is hidden — unplugging a DualSense
            // shouldn't discard its touchpad configuration, and
            // reassigning it brings the tab straight back.
            SelectedSlotHasTouchpad = slot.InputDeviceIds
                .Select(id => devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Any(info => info?.HasTouchpad == true);

            if (SelectedSlotHasTouchpad)
            {
                TouchpadEditor.Load(slot.Touchpad, rule => registry.UpdateTouchpad(slotId, rule));
            }
            else
            {
                TouchpadEditor.Clear();
            }

            RaiseSelectedSlotSummary();
        }
        finally
        {
            loadingDetail = false;
        }
    }

    private void RaiseSelectedSlotSummary()
    {
        OnPropertyChanged(nameof(HasAssignedDevices));
        OnPropertyChanged(nameof(HasAvailableDevices));
        OnPropertyChanged(nameof(SelectedSlotLabel));
        OnPropertyChanged(nameof(SelectedSlotStatus));
        OnPropertyChanged(nameof(SelectedSlotStatusBrush));
        OnPropertyChanged(nameof(SelectedSlotOutput));
        OnPropertyChanged(nameof(SelectedSlotInput));
        OnPropertyChanged(nameof(SelectedSlotProfiles));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        registry.SlotsChanged -= OnSlotsChanged;
        catalog.Updated -= OnCatalogUpdated;
    }
}
