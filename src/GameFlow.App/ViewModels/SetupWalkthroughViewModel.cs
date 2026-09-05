using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using GameFlow.Infrastructure.Runtime.Templates;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Slots;
using GameFlow.Infrastructure.Runtime.HidMaestro;
using GameFlow.Infrastructure.Localization;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Drives the first-run walkthrough: plug a pad in, choose what the game
/// should see, and end up with a working virtual controller.
/// </summary>
/// <remarks>
/// <para>
/// The setup this covers is three steps and none of them are guessable
/// from the main window. A new user lands on a dashboard that stays empty
/// until a slot exists, in an app whose central idea — that the pad you
/// hold and the pad the game sees are two different devices — is exactly
/// the part no other tool has taught them. The Devices page lists their
/// controller as present and connected, which reads like it is already
/// working.
/// </para>
/// <para>
/// It therefore <b>performs</b> the setup rather than describing it.
/// Instructions the user has to carry to another screen are where this
/// kind of thing usually fails; by the last step the slot exists, the pad
/// is assigned to it, and the dashboard has something in it.
/// </para>
/// <para>
/// The detection step watches the live catalog instead of asking "is it
/// plugged in?", so connecting the pad while the page is open advances it
/// on its own. That doubles as the answer to "does GameFlow even see my
/// controller", which is the first thing that goes wrong.
/// </para>
/// </remarks>
public sealed class SetupWalkthroughViewModel : ViewModelBase, IDisposable
{
    private readonly InputDeviceCatalog catalog;
    private readonly SlotRegistry registry;
    private readonly PhysicalPanelPinService pins;
    private readonly SlotSnapshotStore slotSnapshots;

    private int stepIndex;
    private bool disposed;

    /// <summary>Total steps, used for the "Step n of m" counter.</summary>
    public const int StepCount = 6;

    public SetupWalkthroughViewModel(
        InputDeviceCatalog catalog,
        SlotRegistry registry,
        DevicesViewModel devices,
        PhysicalPanelPinService pins,
        SlotSnapshotStore slotSnapshots,
        ILocalizationService? localization = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Devices = devices ?? throw new ArgumentNullException(nameof(devices));
        this.pins = pins ?? throw new ArgumentNullException(nameof(pins));
        this.slotSnapshots = slotSnapshots ?? throw new ArgumentNullException(nameof(slotSnapshots));

        // Two live surfaces: the pad in the user's hands, and the one games
        // will see. Both are the same control the dashboard uses, so what
        // the guide shows is what the app shows.
        PhysicalPreview = new ControllerVisualStateViewModel(_ => { }, localization);
        PhysicalPreview.SetPanelKind(isPhysical: true);
        VirtualPreview = new ControllerVisualStateViewModel(_ => { }, localization);
        VirtualPreview.SetPanelKind(isPhysical: false);

        OutputKinds =
        [
            new WalkthroughOutputKind(
                VirtualControllerKind.Xbox360,
                "Xbox 360 controller",
                "Works with almost every PC game ever made. Choose this if you are not sure."),
            new WalkthroughOutputKind(
                VirtualControllerKind.XboxOne,
                "Xbox Series / One controller",
                "The same broad support, plus impulse triggers in games that use them."),
            new WalkthroughOutputKind(
                VirtualControllerKind.DualShock4,
                "DualShock 4",
                "For games that expect a PlayStation pad, and for the touchpad and lightbar."),
            new WalkthroughOutputKind(
                VirtualControllerKind.DualSense,
                "DualSense",
                "Adds adaptive triggers and haptics where a game supports them."),
        ];
        SelectedOutputKind = OutputKinds[0];

        NextCommand = new RelayCommand(Next, () => CanGoNext);
        BackCommand = new RelayCommand(Back, () => stepIndex > 0);

        catalog.Updated += OnCatalogUpdated;
        RefreshDetectedDevices();
    }

    // ─── Step machine ─────────────────────────────────────────────────

    /// <summary>Zero-based index of the visible step.</summary>
    public int StepIndex
    {
        get => stepIndex;
        private set
        {
            if (SetProperty(ref stepIndex, value))
            {
                OnPropertyChanged(nameof(StepNumberLabel));
                // Raw input is only published for the Devices panel while
                // it considers itself on screen, and calibration reads it.
                // Without this the wizard would sit on "Press: A" forever,
                // because nothing was feeding it presses.
                Devices.IsViewActive = IsCheckLayoutStep;

                OnPropertyChanged(nameof(IsWelcomeStep));
                OnPropertyChanged(nameof(IsConnectStep));
                OnPropertyChanged(nameof(IsCheckLayoutStep));
                OnPropertyChanged(nameof(IsOutputStep));
                OnPropertyChanged(nameof(IsNameStep));
                OnPropertyChanged(nameof(IsReviewStep));
                OnPropertyChanged(nameof(IsDoneStep));
                OnPropertyChanged(nameof(NextButtonLabel));
                OnPropertyChanged(nameof(IsFinalStep));
                RaiseCanExecuteChanged();
            }
        }
    }

    public string StepNumberLabel => $"Step {stepIndex + 1} of {StepCount}";

    public bool IsWelcomeStep => stepIndex == 0;
    public bool IsConnectStep => stepIndex == 1;

    /// <summary>
    /// Shows the chosen pad's live layout so the user can confirm the
    /// buttons line up before anything is built on top of them.
    /// </summary>
    /// <remarks>
    /// A mis-mapped pad is invisible until something built on it behaves
    /// oddly, and by then the guide is long finished. Pressing the buttons
    /// against a picture is the cheapest check there is, and calibration is
    /// offered right here rather than somewhere the user has to be told to
    /// find later.
    /// </remarks>
    public bool IsCheckLayoutStep => stepIndex == 2;

    public bool IsOutputStep => stepIndex == 3;

    /// <summary>Names the controller before it is created.</summary>
    public bool IsNameStep => stepIndex == 4;

    /// <summary>Both layouts side by side, once the slot exists.</summary>
    public bool IsReviewStep => stepIndex == 5;

    public bool IsDoneStep => stepIndex == 5;
    public bool IsFinalStep => stepIndex == StepCount - 1;

    public string NextButtonLabel => stepIndex switch
    {
        0 => "Get started",
        2 => "Buttons look right",
        4 => "Create it",
        5 => "Finish",
        _ => "Next",
    };

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>Raised when the last step is confirmed, so the host can close and navigate.</summary>
    public event EventHandler? Finished;

    private bool CanGoNext => stepIndex switch
    {
        // The detection step gates on a real device so the walkthrough
        // cannot produce a slot with nothing feeding it — the one outcome
        // that looks finished and does nothing.
        1 => HasUsableDevice || SkipDeviceStep,

        // Calibration owns the pad while it runs — every press is being
        // captured as an answer, so a press meant for "Next" would be
        // swallowed and recorded against whichever button was being asked
        // for.
        2 => !Devices.IsCalibrating,
        _ => true,
    };

    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        // Built when leaving the naming step, so the review step that
        // follows has a real controller to show rather than a mock-up.
        if (stepIndex == 4)
        {
            CreateAndAssign();
        }

        if (IsFinalStep)
        {
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        StepIndex = stepIndex + 1;
    }

    private void Back()
    {
        if (stepIndex > 0)
        {
            StepIndex = stepIndex - 1;
        }
    }

    // ─── Detection step ───────────────────────────────────────────────

    /// <summary>Connected devices that can drive a slot, refreshed live.</summary>
    public ObservableCollection<WalkthroughDevice> DetectedDevices { get; } = [];

    /// <summary>True once at least one gamepad or joystick is connected.</summary>
    public bool HasUsableDevice => DetectedDevices.Any(d => d.IsGamepadLike);

    /// <summary>
    /// The pad this walkthrough is setting up.
    /// </summary>
    /// <remarks>
    /// Creation used to take whichever gamepad happened to be first in the
    /// catalog. With two pads connected that is a coin toss, and the guide
    /// never said which one it had chosen — so the layout check, the name
    /// and the review could all describe a controller the user was not
    /// holding.
    /// </remarks>
    public WalkthroughDevice? SelectedDevice
    {
        get;
        set
        {
            var previous = field;
            if (SetProperty(ref field, value))
            {
                // The calibration flow reads its live raw input from the
                // Devices panel's own selection, so they have to agree.
                Devices.SelectedDevice = value is null
                    ? null
                    : Devices.Devices.FirstOrDefault(d => d.Id == value.Id);

                ReleasePin(previous?.Id);
                AcquirePin(value?.Id);

                // Resolved from the catalog's vendor/product rather than
                // left as Auto.
                //
                // Auto reads the identity off the incoming FRAME, and the
                // first frames here carry none: a device that is not pinned
                // gets an empty placeholder snapshot, whose name is the
                // catalog id and whose ids are zero. Auto then resolves to
                // Auto, no theme matches, and the step renders "No
                // controller theme installed" over a blank panel — which is
                // what the guide showed. The catalog already knows what the
                // pad is, so it does not need to be inferred.
                physicalStyle = value is null
                    ? ControllerVisualStyle.Auto
                    : ControllerHardwareCatalog.Resolve(value.VendorId, value.ProductId);

                OnPropertyChanged(nameof(SelectedDeviceName));
                RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedDeviceName => SelectedDevice?.DisplayName ?? "your controller";

    /// <summary>
    /// Stamps the catalog's identity onto a frame that carries none.
    /// </summary>
    /// <remarks>
    /// Pinning a device starts real frames, but not instantly — the
    /// runtime publishes on its next tick, and until then the placeholder
    /// snapshot has zero vendor/product and the catalog id where the name
    /// should be. Anything resolving the controller art from the frame
    /// gets nothing and falls through to "no theme", which is visible for
    /// exactly as long as it takes the first frame to land: long enough to
    /// read, and alarming. Backfilling costs nothing once real frames
    /// arrive, because then there is nothing to fill.
    /// </remarks>
    private static ControllerSnapshot WithIdentity(ControllerSnapshot snapshot, WalkthroughDevice device) =>
        snapshot.VendorId == 0 && snapshot.ProductId == 0
            ? snapshot with
            {
                DeviceName = device.DisplayName,
                VendorId = device.VendorId,
                ProductId = device.ProductId,
            }
            : snapshot;

    /// <summary>Controller art for the selected pad; see the note in the setter above.</summary>
    private ControllerVisualStyle physicalStyle = ControllerVisualStyle.Auto;

    /// <summary>Device this walkthrough pinned, so it can unpin exactly that one.</summary>
    private string? pinnedByWalkthrough;

    /// <summary>
    /// Starts live frames for a device.
    /// </summary>
    /// <remarks>
    /// The runtime publishes physical snapshots only for PINNED devices —
    /// everything else reads back an empty placeholder. Without this the
    /// layout check showed a controller that never moved, which is the one
    /// thing that step exists to demonstrate.
    /// </remarks>
    private void AcquirePin(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || pins.IsPinned(deviceId))
        {
            return; // Already pinned by the user; leave their pin alone.
        }

        _ = pins.TogglePin(deviceId);
        pinnedByWalkthrough = deviceId;
    }

    private void ReleasePin(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)
            || !string.Equals(pinnedByWalkthrough, deviceId, StringComparison.Ordinal))
        {
            return;
        }

        _ = pins.TogglePin(deviceId);
        pinnedByWalkthrough = null;
    }

    /// <summary>The Devices panel, borrowed for its button-calibration flow.</summary>
    public DevicesViewModel Devices { get; }

    /// <summary>Live surface for the physical pad, shown while checking the layout.</summary>
    public ControllerVisualStateViewModel PhysicalPreview { get; }

    /// <summary>Live surface for the emitted controller, shown on review.</summary>
    public ControllerVisualStateViewModel VirtualPreview { get; }

    /// <summary>
    /// Name given to the created controller. Empty means "let the registry
    /// name it", which is what happened before this step existed.
    /// </summary>
    public string SlotName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// Pushes the current frame into whichever preview is on screen.
    /// Driven by the window's timer, so nothing renders while the
    /// walkthrough is closed.
    /// </summary>
    public void RefreshPreviews()
    {
        if (IsCheckLayoutStep && SelectedDevice is not null)
        {
            PhysicalPreview.Update(
                "walkthrough:physical",
                string.Empty,
                WithIdentity(pins.GetSnapshot(SelectedDevice.Id), SelectedDevice),
                physicalStyle);
            return;
        }

        if (!IsReviewStep || CreatedSlotId is null)
        {
            return;
        }

        // The physical half keeps coming from the pin, not from the slot.
        //
        // A slot's own physical snapshot only fills once its pipeline is
        // running, and this slot was created a second ago — the rebuild is
        // debounced, so on this step the pair is still empty and the pad
        // sits motionless exactly where the user is being asked to press
        // something and watch it move. The pinned feed is already live
        // from the previous step and owes nothing to slot startup.
        if (SelectedDevice is not null)
        {
            PhysicalPreview.Update(
                "walkthrough:physical",
                string.Empty,
                WithIdentity(pins.GetSnapshot(SelectedDevice.Id), SelectedDevice),
                physicalStyle);
        }

        // The emitted controller's art comes from the kind that was
        // chosen, not from its frames. Auto reads vendor/product off the
        // snapshot, and until the slot's pipeline produces one there is
        // nothing to read — which rendered "no controller theme installed
        // for this style" beside a controller that had just been created
        // successfully.
        var virtualStyle = SelectedOutputKind is { } chosen
            ? HidMaestroProfiles.ResolveVisualStyle(chosen.Kind)
            : ControllerVisualStyle.Auto;

        VirtualPreview.Update(
            "walkthrough:virtual",
            string.Empty,
            slotSnapshots.Get(CreatedSlotId).Virtual,
            virtualStyle);
    }

    /// <summary>
    /// Lets someone with no pad at all continue, since keyboard and mouse
    /// as a gamepad is a supported setup rather than a degraded one.
    /// </summary>
    public bool SkipDeviceStep
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaiseCanExecuteChanged();
            }
        }
    }

    public string DetectionMessage => HasUsableDevice
        ? "Found it. Continue when you are ready."
        : "Nothing yet — connect a controller by USB or Bluetooth and it will appear here on its own.";

    private void OnCatalogUpdated(object? sender, EventArgs e) => RefreshDetectedDevices();

    private void RefreshDetectedDevices()
    {
        // Gamepads and joysticks only. Keyboards and mice are assignable
        // too, but they are ALWAYS present — this machine reports nine of
        // them — and listing them on a step headed "Connect your
        // controller" buries the one device the user is waiting to see
        // under a wall of hardware they did not connect and cannot use
        // here. The keyboard-and-mouse route is the checkbox below the
        // list, which that wall was pushing off the bottom of the window.
        var fresh = catalog.Devices
            .Where(d => d.IsConnected
                     && d.IsAssignableAsInput
                     && d.Category is DeviceCategory.Gamepad or DeviceCategory.Joystick)
            .Select(d => new WalkthroughDevice(
                d.Id,
                d.DisplayName,
                IsGamepadLike: true,
                d.Category.ToString(),
                d.VendorId,
                d.ProductId))
            .ToList();

        // Rebuild only on a real change: the catalog raises Updated for
        // battery ticks too, and replacing the list on every tick makes
        // the step flicker under the user's cursor.
        if (fresh.Count == DetectedDevices.Count &&
            fresh.Zip(DetectedDevices).All(pair => pair.First.Id == pair.Second.Id))
        {
            return;
        }

        DetectedDevices.Clear();
        foreach (var device in fresh)
        {
            DetectedDevices.Add(device);
        }

        SelectedDevice = DetectedDevices.FirstOrDefault(d => d.Id == SelectedDevice?.Id)
            ?? DetectedDevices.FirstOrDefault(d => d.IsGamepadLike);

        OnPropertyChanged(nameof(HasUsableDevice));
        OnPropertyChanged(nameof(DetectionMessage));
        RaiseCanExecuteChanged();
    }

    // ─── Output step ──────────────────────────────────────────────────

    public IReadOnlyList<WalkthroughOutputKind> OutputKinds { get; }

    public WalkthroughOutputKind SelectedOutputKind
    {
        get;
        set => SetProperty(ref field, value);
    }

    // ─── Result ───────────────────────────────────────────────────────

    /// <summary>What actually happened, shown on the final step.</summary>
    public string SummaryMessage
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>Id of the slot this walkthrough created, for the host to select afterwards.</summary>
    public string? CreatedSlotId
    {
        get;
        private set => SetProperty(ref field, value);
    }

    private void CreateAndAssign()
    {
        var kind = SelectedOutputKind?.Kind ?? VirtualControllerKind.Xbox360;
        var slot = registry.CreateSlot(kind);
        if (slot is null)
        {
            SummaryMessage = "The virtual controller could not be created. " +
                             "The Devices page shows the output backend's status.";
            return;
        }

        CreatedSlotId = slot.Id;

        if (!string.IsNullOrWhiteSpace(SlotName))
        {
            registry.Rename(slot.Id, SlotName.Trim());
        }

        var assigned = SelectedDevice ?? DetectedDevices.FirstOrDefault(d => d.IsGamepadLike);
        if (assigned is not null)
        {
            registry.AssignDevice(slot.Id, assigned.Id);
            SummaryMessage =
                $"{assigned.DisplayName} now drives a virtual {SelectedOutputKind?.Label}. " +
                "Games will see the virtual one.";
        }
        else
        {
            SummaryMessage =
                $"A virtual {SelectedOutputKind?.Label} is ready. Assign a keyboard, mouse or pad to it " +
                "on the Devices page, under Virtual controllers.";
        }
    }

    private void RaiseCanExecuteChanged()
    {
        (NextCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (BackCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        catalog.Updated -= OnCatalogUpdated;

        if (Devices.IsCalibrating)
        {
            Devices.CancelCalibrationCommand.Execute(null);
        }

        Devices.IsViewActive = false;
        ReleasePin(pinnedByWalkthrough);
    }
}

/// <summary>One connected device shown on the walkthrough's detection step.</summary>
/// <param name="Id">Catalog id, used to assign it to the created slot.</param>
/// <param name="DisplayName">Name as the catalog reports it.</param>
/// <param name="IsGamepadLike">True for gamepads and joysticks — what the step waits for.</param>
/// <param name="CategoryLabel">Category name, shown as a subtitle.</param>
/// <param name="VendorId">USB vendor id, used to pick the controller art.</param>
/// <param name="ProductId">USB product id, used to pick the controller art.</param>
public sealed record WalkthroughDevice(
    string Id,
    string DisplayName,
    bool IsGamepadLike,
    string CategoryLabel,
    ushort VendorId = 0,
    ushort ProductId = 0);

/// <summary>One choice of virtual controller, with the reason to pick it.</summary>
/// <param name="Kind">Output kind the slot is created with.</param>
/// <param name="Label">Short name.</param>
/// <param name="Description">Why someone would choose this one.</param>
public sealed record WalkthroughOutputKind(VirtualControllerKind Kind, string Label, string Description);
