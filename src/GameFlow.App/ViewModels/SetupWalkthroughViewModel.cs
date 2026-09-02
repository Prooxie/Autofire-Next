using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using GameFlow.Infrastructure.Runtime.Templates;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Slots;

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

    private int stepIndex;
    private bool disposed;

    /// <summary>Total steps, used for the "Step n of m" counter.</summary>
    public const int StepCount = 4;

    public SetupWalkthroughViewModel(InputDeviceCatalog catalog, SlotRegistry registry)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

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
                OnPropertyChanged(nameof(IsWelcomeStep));
                OnPropertyChanged(nameof(IsConnectStep));
                OnPropertyChanged(nameof(IsOutputStep));
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
    public bool IsOutputStep => stepIndex == 2;
    public bool IsDoneStep => stepIndex == 3;
    public bool IsFinalStep => stepIndex == StepCount - 1;

    public string NextButtonLabel => stepIndex switch
    {
        0 => "Get started",
        2 => "Create it",
        3 => "Finish",
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
        _ => true,
    };

    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        if (stepIndex == 2)
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
                d.Category.ToString()))
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

        var assigned = DetectedDevices.FirstOrDefault(d => d.IsGamepadLike);
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
    }
}

/// <summary>One connected device shown on the walkthrough's detection step.</summary>
/// <param name="Id">Catalog id, used to assign it to the created slot.</param>
/// <param name="DisplayName">Name as the catalog reports it.</param>
/// <param name="IsGamepadLike">True for gamepads and joysticks — what the step waits for.</param>
/// <param name="CategoryLabel">Category name, shown as a subtitle.</param>
public sealed record WalkthroughDevice(string Id, string DisplayName, bool IsGamepadLike, string CategoryLabel);

/// <summary>One choice of virtual controller, with the reason to pick it.</summary>
/// <param name="Kind">Output kind the slot is created with.</param>
/// <param name="Label">Short name.</param>
/// <param name="Description">Why someone would choose this one.</param>
public sealed record WalkthroughOutputKind(VirtualControllerKind Kind, string Label, string Description);
