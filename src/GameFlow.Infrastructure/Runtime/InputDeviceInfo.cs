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
    DeviceBatteryState BatteryState = DeviceBatteryState.Unknown,
    bool IsVirtual = false)
{
    /// <summary>
    /// True when this is one of GameFlow's own virtual pads rather than
    /// real hardware.
    ///
    /// <para>
    /// A virtual controller impersonates real hardware down to its
    /// VID/PID — that is what makes games accept it — so it re-enters
    /// through SDL's enumeration looking physical. Without this flag a
    /// slot can take another slot's output as its input, and that chain
    /// can be repeated until the runtime is mapping itself in a circle.
    /// See <see cref="VirtualDeviceIdentity"/> for how it is decided.
    /// </para>
    /// </summary>
    public bool IsVirtual { get; init; } = IsVirtual;

    /// <summary>
    /// Whether this device may be assigned to a slot as an input source.
    /// Virtual pads are excluded — feeding one back in is the recursion
    /// above, not a use case.
    /// </summary>
    public bool IsAssignableAsInput => !IsVirtual
        && Category is DeviceCategory.Gamepad or DeviceCategory.Joystick
                    or DeviceCategory.Keyboard or DeviceCategory.Mouse;

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
