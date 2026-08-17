using GameFlow.Infrastructure.Runtime;

namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// macOS counterpart to RawInputDeviceScanner / LinuxInputDeviceScanner,
/// enumerating REAL per-device keyboards and mice through IOHIDManager.
///
/// <para>
/// This previously published two fixed rows — "Keyboard (all)" and
/// "Mouse (all)" — because CGEventTap genuinely cannot tell devices
/// apart. IOHIDManager can: every device carries a vendor id, product id
/// and location id, and the location id is stable per physical port,
/// which is what makes an id worth saving in a slot.
/// </para>
///
/// <para>
/// <b>Untested on hardware.</b> Deliberately the first piece of the macOS
/// rewrite to land, because it is the only part that produces visible
/// output without the input-callback path working: if the Devices page
/// lists real keyboard and mouse names on a Mac, then the framework
/// binding, the matching dictionaries and the property reads are all
/// correct, and only the callback path remains in question.
/// </para>
/// </summary>
internal static class MacInputDeviceScanner
{
    // Kept for profiles saved before per-device enumeration existed. A
    // slot referencing one of these still resolves rather than silently
    // losing its assignment on upgrade.
    internal const string AggregateKeyboardId = "mac-keyboard-aggregate";
    internal const string AggregateMouseId = "mac-mouse-aggregate";

    public static IReadOnlyList<InputDeviceInfo> Scan()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        try
        {
            return ScanCore();
        }
        catch (Exception)
        {
            // A missing framework or a rejected permission must not take
            // the device catalog down with it. Falling back to the
            // aggregate rows keeps macOS working exactly as it did before
            // this rewrite rather than leaving the page empty.
            return Fallback();
        }
    }

    private static IReadOnlyList<InputDeviceInfo> ScanCore()
    {
        var manager = MacHidInterop.ManagerCreate(IntPtr.Zero, 0);
        if (manager == IntPtr.Zero)
        {
            return Fallback();
        }

        var matching = IntPtr.Zero;
        var devices = IntPtr.Zero;

        try
        {
            (int Page, int Usage)[] pairs =
            [
                (MacHidInterop.UsagePageGenericDesktop, MacHidInterop.UsageKeyboard),
                (MacHidInterop.UsagePageGenericDesktop, MacHidInterop.UsageMouse),

                // Trackpads usually present as Pointer, not Mouse.
                // Matching only Mouse misses them on most laptops.
                (MacHidInterop.UsagePageGenericDesktop, MacHidInterop.UsagePointer),
            ];

            matching = MacHidInterop.CreateMatchingDictionaries(pairs);
            MacHidInterop.ManagerSetDeviceMatchingMultiple(manager, matching);

            // Enumeration does not require the device to be opened, so it
            // works even before Input Monitoring is granted. That is the
            // property that makes this a useful first milestone.
            devices = MacHidInterop.ManagerCopyDevices(manager);
            if (devices == IntPtr.Zero)
            {
                return Fallback();
            }

            var count = (int)MacHidInterop.CFSetGetCount(devices);
            if (count <= 0)
            {
                return Fallback();
            }

            var handles = new IntPtr[count];
            MacHidInterop.CFSetGetValues(devices, handles);

            var results = new List<InputDeviceInfo>(count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var handle in handles)
            {
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                var info = Describe(handle);
                if (info is not null && seen.Add(info.Id))
                {
                    results.Add(info);
                }
            }

            return results.Count > 0 ? results : Fallback();
        }
        finally
        {
            if (devices != IntPtr.Zero) { MacHidInterop.CFRelease(devices); }
            if (matching != IntPtr.Zero) { MacHidInterop.CFRelease(matching); }
            if (manager != IntPtr.Zero) { MacHidInterop.CFRelease(manager); }
        }
    }

    private static InputDeviceInfo? Describe(IntPtr device)
    {
        var category = ClassifyDevice(device);
        if (category == DeviceCategory.Unknown)
        {
            return null;
        }

        var vendor = MacHidInterop.GetIntProperty(device, MacHidInterop.KeyVendorId) ?? 0;
        var product = MacHidInterop.GetIntProperty(device, MacHidInterop.KeyProductId) ?? 0;

        var name = MacHidInterop.GetStringProperty(device, MacHidInterop.KeyProduct);
        var maker = MacHidInterop.GetStringProperty(device, MacHidInterop.KeyManufacturer);

        var display = string.IsNullOrWhiteSpace(name)
            ? $"{(category == DeviceCategory.Keyboard ? "Keyboard" : "Mouse")} {vendor:X4}:{product:X4}"
            : string.IsNullOrWhiteSpace(maker) ? name : $"{maker} {name}";

        return new InputDeviceInfo(
            Id: BuildDeviceId(device, category),
            DisplayName: display,
            VendorId: (ushort)vendor,
            ProductId: (ushort)product,
            Category: category);
    }

    /// <summary>
    /// The catalog id for one IOHIDDevice.
    ///
    /// <para>
    /// Shared with <see cref="MacRawInputReader"/>, which has to arrive at
    /// the exact same string from the device handle the input callback
    /// hands it — a device whose keystrokes file under a different id than
    /// the one the Devices page published would look assignable and then
    /// never register a press.
    /// </para>
    ///
    /// <para>
    /// Location id is preferred because it is stable per physical port
    /// across reconnects; VID/PID alone collides the moment someone plugs
    /// in two identical keyboards. Serial is the better key when present,
    /// which it usually is not for HID peripherals.
    /// </para>
    /// </summary>
    internal static string BuildDeviceId(IntPtr device, DeviceCategory category)
    {
        var vendor = MacHidInterop.GetIntProperty(device, MacHidInterop.KeyVendorId) ?? 0;
        var product = MacHidInterop.GetIntProperty(device, MacHidInterop.KeyProductId) ?? 0;
        var location = MacHidInterop.GetIntProperty(device, MacHidInterop.KeyLocationId);

        var serial = MacHidInterop.GetStringProperty(device, MacHidInterop.KeySerialNumber);
        var discriminator = !string.IsNullOrWhiteSpace(serial)
            ? serial
            : location?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";

        var kind = category == DeviceCategory.Keyboard ? "keyboard" : "mouse";
        return $"mac-{kind}-{vendor:x4}{product:x4}-{discriminator}";
    }

    /// <summary>
    /// Classifies from the device's primary usage. Read from the device
    /// rather than inferred from its name — names are marketing strings
    /// and routinely say nothing useful ("USB Receiver").
    /// </summary>
    internal static DeviceCategory ClassifyDevice(IntPtr device)
    {
        var usage = MacHidInterop.GetIntProperty(device, "PrimaryUsage");
        var page = MacHidInterop.GetIntProperty(device, "PrimaryUsagePage");

        if (page != MacHidInterop.UsagePageGenericDesktop)
        {
            return DeviceCategory.Unknown;
        }

        return usage switch
        {
            MacHidInterop.UsageKeyboard => DeviceCategory.Keyboard,
            MacHidInterop.UsageMouse or MacHidInterop.UsagePointer => DeviceCategory.Mouse,
            _ => DeviceCategory.Unknown,
        };
    }

    /// <summary>
    /// The pre-rewrite behaviour: two aggregate rows. Used whenever real
    /// enumeration cannot answer, so macOS is never left worse off than
    /// before — a Mac that fails here still behaves exactly as it used to.
    /// </summary>
    private static IReadOnlyList<InputDeviceInfo> Fallback() =>
    [
        new InputDeviceInfo(AggregateKeyboardId, "Keyboard (all)", Category: DeviceCategory.Keyboard),
        new InputDeviceInfo(AggregateMouseId, "Mouse (all)", Category: DeviceCategory.Mouse),
    ];
}
