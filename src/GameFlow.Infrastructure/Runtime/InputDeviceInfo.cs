namespace GameFlow.Infrastructure.Runtime;

/// <summary>Broad classification of an input device, used to group the Devices view by type.</summary>
public enum DeviceCategory
{
    Unknown = 0,
    Gamepad,
    Joystick,
    Keyboard,
    Mouse,
}

/// <summary>Battery/charging state reported by the input backend.</summary>
public enum DeviceBatteryState
{
    Unknown = 0,
    OnBattery,
    Charging,
    Charged,
    NoBattery,
}

public sealed record InputDeviceInfo(
    string Id,
    string DisplayName,
    bool IsConnected = true,
    bool IsSelected = false,
    ushort VendorId = 0,
    ushort ProductId = 0,
    bool IsGamepad = false,
    DeviceCategory Category = DeviceCategory.Unknown,
    int? BatteryPercentage = null,
    DeviceBatteryState BatteryState = DeviceBatteryState.Unknown)
{
    public string HardwareId => VendorId == 0 && ProductId == 0
        ? string.Empty
        : $"VID {VendorId:X4} · PID {ProductId:X4}";

    /// <summary>
    /// True when this device carries a finger-tracking touch surface, so
    /// a slot it's assigned to should offer the Touchpad tab. Derived
    /// from the hardware signature — see
    /// <see cref="GameFlow.Core.Models.ControllerHardwareCatalog.HasTouchpadSurface"/>
    /// for why it isn't asked of SDL directly.
    /// </summary>
    public bool HasTouchpad =>
        GameFlow.Core.Models.ControllerHardwareCatalog.HasTouchpadSurface(VendorId, ProductId);

    /// <summary>True when the backend supplied a useful battery reading.</summary>
    public bool HasBattery => BatteryPercentage is >= 0 and <= 100
        && BatteryState is not DeviceBatteryState.NoBattery;
}
