using System.Globalization;
using GameFlow.Infrastructure.Runtime;

namespace GameFlow.App.ViewModels;

/// <summary>
/// One row in the Devices list — a thin, display-oriented wrapper over
/// an <see cref="InputDeviceInfo"/> snapshot from the
/// <see cref="InputDeviceCatalog"/>. Rebuilt by
/// <see cref="DevicesViewModel"/> whenever the catalog raises its
/// Updated event, so these are effectively immutable per snapshot.
/// </summary>
public sealed class DeviceRowViewModel(InputDeviceInfo info) : ViewModelBase
{
    private InputDeviceInfo info = info;

    public string Id => info.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(info.DisplayName) ? info.Id : info.DisplayName;
    public bool IsConnected => info.IsConnected;
    public bool IsSelected => info.IsSelected;
    public bool IsGamepad => info.IsGamepad;
    public DeviceCategory Category => info.Category;
    public bool HasBattery => info.HasBattery;
    public string BatteryText => info.HasBattery ? $"{info.BatteryPercentage}%" : string.Empty;
    public string BatteryStatusText => info.BatteryState switch
    {
        DeviceBatteryState.Charging => $"Charging · {info.BatteryPercentage}%",
        DeviceBatteryState.Charged => "Fully charged",
        DeviceBatteryState.OnBattery => $"Battery · {info.BatteryPercentage}%",
        _ => string.Empty,
    };
    public string BatteryIcon => info.BatteryState == DeviceBatteryState.Charging ? "⚡" : "▰";

    /// <summary>
    /// True when this is one of GameFlow's own virtual pads rather than
    /// real hardware. See <see cref="GameFlow.Infrastructure.Runtime.VirtualDeviceIdentity"/>.
    /// </summary>
    public bool IsVirtual => info.IsVirtual;

    /// <summary>
    /// Human-readable device type for the row tag.
    ///
    /// <para>
    /// A virtual pad says so here. It impersonates real hardware down to
    /// its VID/PID — that is what makes games accept it — so nothing else
    /// in this row distinguishes it, and a user looking at the device list
    /// otherwise cannot tell which entry is the controller they plugged in
    /// and which is the one GameFlow created.
    /// </para>
    /// </summary>
    public string KindLabel
    {
        get
        {
            var kind = info.Category switch
            {
                DeviceCategory.Gamepad => "Gamepad",
                DeviceCategory.Joystick => "Generic / HID",
                DeviceCategory.Keyboard => "Keyboard",
                DeviceCategory.Mouse => "Mouse",
                _ => info.IsGamepad ? "Gamepad" : "Generic / HID",
            };

            return info.IsVirtual ? $"{kind} · VIRTUAL" : kind;
        }
    }

    /// <summary>Tag colour per device category.</summary>
    public string KindBrush => info.Category switch
    {
        DeviceCategory.Gamepad => "#3B82F6",
        DeviceCategory.Joystick => "#D97706",
        DeviceCategory.Keyboard => "#8B5CF6",
        DeviceCategory.Mouse => "#10B981",
        _ => info.IsGamepad ? "#3B82F6" : "#D97706",
    };

    /// <summary>
    /// Refreshes this row from a newer catalog snapshot for the same
    /// device id, raising change notifications in place. Used by the
    /// reconciling rebuild so the selected row object survives a refresh
    /// (replacing it would yank the ListBox selection and re-enter the
    /// selection path).
    /// </summary>
    public void Apply(InputDeviceInfo next)
    {
        if (ReferenceEquals(info, next))
        {
            return;
        }

        info = next;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsGamepad));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(BatteryStatusText));
        OnPropertyChanged(nameof(BatteryIcon));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(KindBrush));
        OnPropertyChanged(nameof(VendorIdHex));
        OnPropertyChanged(nameof(ProductIdHex));
        OnPropertyChanged(nameof(HasHardwareId));
        OnPropertyChanged(nameof(HardwareId));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(ConnectionBrush));
    }

    /// <summary>Vendor id as <c>0xXXXX</c>, or empty when unknown.</summary>
    public string VendorIdHex => info.VendorId == 0
        ? string.Empty
        : "0x" + info.VendorId.ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>Product id as <c>0xXXXX</c>, or empty when unknown.</summary>
    public string ProductIdHex => info.ProductId == 0
        ? string.Empty
        : "0x" + info.ProductId.ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>True when both VID and PID are known, so the VID:PID row is worth showing.</summary>
    public bool HasHardwareId => info.VendorId != 0 || info.ProductId != 0;

    public string HardwareId => info.HardwareId;

    /// <summary>"Connected" / "Offline" — surfaced as a small status text.</summary>
    public string ConnectionText => info.IsConnected ? "Connected" : "Offline";

    /// <summary>
    /// Hex colour for the status dot — green when connected, slate when
    /// offline. Bound directly to <c>Ellipse.Fill</c>; Avalonia converts
    /// the string to a brush.
    /// </summary>
    public string ConnectionBrush => info.IsConnected ? "#22C55E" : "#64748B";
}
