using System.Runtime.InteropServices;

namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// IOKit HID bindings — the replacement for the CGEventTap path.
///
/// <para>
/// CGEventTap delivers one aggregate stream for the whole system: every
/// keyboard looks like the same keyboard, so per-device selection was
/// impossible to offer honestly on macOS. IOHIDManager is the API that
/// does carry device identity, which is why the input side moves here.
/// Output stays on <c>CGEventPost</c> — that is a synthesis API and
/// per-device identity is meaningless for it.
/// </para>
///
/// <para>
/// <b>Untested on hardware.</b> Written on Windows, where none of this can
/// execute. Everything below is arranged so a macOS tester can find a
/// fault quickly: the enumeration path is separated from the callback
/// path, permission is checked explicitly rather than inferred from
/// silence, and every failure reports which step failed.
/// </para>
/// </summary>
internal static partial class MacHidInterop
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // HID usage tables. Page 1 is "Generic Desktop"; the usages below are
    // what the OS itself matches on to classify a device.
    internal const int UsagePageGenericDesktop = 0x01;
    internal const int UsageKeyboard = 0x06;
    internal const int UsageMouse = 0x02;
    internal const int UsagePointer = 0x01;

    // Element-level usage pages. Device matching (above) asks "what kind
    // of device is this"; these answer "what did this particular value
    // come from" inside the input callback.
    internal const uint UsagePageKeyboard = 0x07; // Keyboard/Keypad — one element per key, value 1 = down
    internal const uint UsagePageButton = 0x09;   // Button — usage 1 = left, 2 = right, 3 = middle, 4/5 = side

    // Generic Desktop axis usages, as they appear on a pointer's elements.
    internal const uint UsageX = 0x30;
    internal const uint UsageY = 0x31;
    internal const uint UsageWheel = 0x38;

    // IOHIDDevice property keys, as CFStrings.
    internal const string KeyProduct = "Product";
    internal const string KeyManufacturer = "Manufacturer";
    internal const string KeyVendorId = "VendorID";
    internal const string KeyProductId = "ProductID";
    internal const string KeyLocationId = "LocationID";
    internal const string KeyTransport = "Transport";
    internal const string KeySerialNumber = "SerialNumber";

    /// <summary>
    /// Result of <c>IOHIDCheckAccess</c>. Input Monitoring consent is the
    /// single most likely reason a correct implementation appears to do
    /// nothing: without it the manager opens, devices enumerate, and the
    /// value callback simply never fires. It has to be checked, never
    /// assumed.
    /// </summary>
    internal enum AccessType
    {
        Granted = 0,
        Denied = 1,
        Unknown = 2,
    }

    [LibraryImport(IOKit, EntryPoint = "IOHIDCheckAccess")]
    internal static partial AccessType CheckAccess(uint requestType);

    [LibraryImport(IOKit, EntryPoint = "IOHIDRequestAccess")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool RequestAccess(uint requestType);

    /// <summary>kIOHIDRequestTypeListenEvent — the "read input in the background" permission.</summary>
    internal const uint RequestTypeListenEvent = 1;

    /// <summary>
    /// <see cref="CheckAccess"/>, tolerating the symbol being absent.
    /// Both access functions arrived in macOS 10.15 — on anything older
    /// the P/Invoke throws <see cref="EntryPointNotFoundException"/> on
    /// first call rather than failing at load. There is also no Input
    /// Monitoring consent to check on those releases, so "not askable"
    /// and "no gate exists" are the same answer: <see langword="null"/>,
    /// meaning carry on and let opening the manager decide.
    /// </summary>
    internal static AccessType? TryCheckAccess()
    {
        try
        {
            return CheckAccess(RequestTypeListenEvent);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="RequestAccess"/>, tolerating the symbol being absent.
    ///
    /// <para>
    /// Blocks until the user answers the system prompt. It only ever
    /// prompts once per app: after a refusal it returns
    /// <see langword="false"/> immediately and the only way back is
    /// System Settings, which is precisely why the caller has to say so
    /// in the log rather than retry.
    /// </para>
    /// </summary>
    internal static bool? TryRequestAccess()
    {
        try
        {
            return RequestAccess(RequestTypeListenEvent);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerCreate")]
    internal static partial IntPtr ManagerCreate(IntPtr allocator, uint options);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerSetDeviceMatchingMultiple")]
    internal static partial void ManagerSetDeviceMatchingMultiple(IntPtr manager, IntPtr multiple);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerOpen")]
    internal static partial int ManagerOpen(IntPtr manager, uint options);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerClose")]
    internal static partial int ManagerClose(IntPtr manager, uint options);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerCopyDevices")]
    internal static partial IntPtr ManagerCopyDevices(IntPtr manager);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerScheduleWithRunLoop")]
    internal static partial void ManagerScheduleWithRunLoop(IntPtr manager, IntPtr runLoop, IntPtr mode);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerUnscheduleFromRunLoop")]
    internal static partial void ManagerUnscheduleFromRunLoop(IntPtr manager, IntPtr runLoop, IntPtr mode);

    [LibraryImport(IOKit, EntryPoint = "IOHIDManagerRegisterInputValueCallback")]
    internal static partial void ManagerRegisterInputValueCallback(IntPtr manager, IntPtr callback, IntPtr context);

    [LibraryImport(IOKit, EntryPoint = "IOHIDDeviceGetProperty")]
    internal static partial IntPtr DeviceGetProperty(IntPtr device, IntPtr key);

    [LibraryImport(IOKit, EntryPoint = "IOHIDValueGetElement")]
    internal static partial IntPtr ValueGetElement(IntPtr value);

    [LibraryImport(IOKit, EntryPoint = "IOHIDValueGetIntegerValue")]
    internal static partial nint ValueGetIntegerValue(IntPtr value);

    [LibraryImport(IOKit, EntryPoint = "IOHIDElementGetUsage")]
    internal static partial uint ElementGetUsage(IntPtr element);

    [LibraryImport(IOKit, EntryPoint = "IOHIDElementGetUsagePage")]
    internal static partial uint ElementGetUsagePage(IntPtr element);

    [LibraryImport(IOKit, EntryPoint = "IOHIDElementGetDevice")]
    internal static partial IntPtr ElementGetDevice(IntPtr element);

    /// <summary>
    /// Whether an element reports a delta or an absolute reading. This is
    /// the guard that keeps a tablet or an absolute-mode trackpad from
    /// being read as if it were a mouse: its X would be "1180 pixels from
    /// the left", and accumulating that as movement would fling the stick
    /// to full deflection and hold it there.
    /// </summary>
    [LibraryImport(IOKit, EntryPoint = "IOHIDElementIsRelative")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool ElementIsRelative(IntPtr element);

    /// <summary>kIOReturnSuccess. Every other IOReturn is a failure whose exact value is not worth decoding here — it is logged in hex for a tester to look up.</summary>
    internal const int ReturnSuccess = 0;

    // ── CoreFoundation ───────────────────────────────────────────────
    // Needed because every IOKit property is a CF object. Kept minimal:
    // only what the enumeration and callback paths actually touch.

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    internal static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundation, EntryPoint = "CFGetTypeID")]
    internal static partial nuint CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetTypeID")]
    internal static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetTypeID")]
    internal static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation, EntryPoint = "CFSetGetCount")]
    internal static partial nint CFSetGetCount(IntPtr set);

    [LibraryImport(CoreFoundation, EntryPoint = "CFSetGetValues")]
    internal static partial void CFSetGetValues(IntPtr set, IntPtr[] values);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    internal static partial IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetCString", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool CFStringGetCString(IntPtr theString, Span<byte> buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool CFNumberGetValue(IntPtr number, nint theType, out int value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryCreateMutable")]
    internal static partial IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionarySetValue")]
    internal static partial void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberCreate")]
    internal static partial IntPtr CFNumberCreate(IntPtr allocator, nint theType, ref int valuePtr);

    [LibraryImport(CoreFoundation, EntryPoint = "CFArrayCreateMutable")]
    internal static partial IntPtr CFArrayCreateMutable(IntPtr allocator, nint capacity, IntPtr callBacks);

    [LibraryImport(CoreFoundation, EntryPoint = "CFArrayAppendValue")]
    internal static partial void CFArrayAppendValue(IntPtr array, IntPtr value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopGetCurrent")]
    internal static partial IntPtr CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopRun")]
    internal static partial void CFRunLoopRun();

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopStop")]
    internal static partial void CFRunLoopStop(IntPtr runLoop);

    internal const uint EncodingUtf8 = 0x08000100;
    internal const nint CFNumberIntType = 9;   // kCFNumberIntType

    /// <summary>
    /// A CFString equal to <c>kCFRunLoopDefaultMode</c>, for scheduling
    /// the manager on a run loop.
    ///
    /// <para>
    /// That constant is an exported CFStringRef global, not a function,
    /// and P/Invoke cannot read a data export without <c>dlopen</c> /
    /// <c>dlsym</c> gymnastics. Building an equal string instead works
    /// because run loop modes are compared by value (CFEqual), not by
    /// pointer, and the global's contents are literally the characters
    /// "kCFRunLoopDefaultMode".
    /// </para>
    ///
    /// <para>Caller owns the result and must <see cref="CFRelease"/> it.</para>
    /// </summary>
    internal static IntPtr CreateDefaultRunLoopMode() =>
        CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", EncodingUtf8);

    /// <summary>UTF-8 string out of a CFString. Returns null when the value is absent or not a string.</summary>
    internal static string? ReadString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero || CFGetTypeID(cfString) != CFStringGetTypeID())
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[512];
        if (!CFStringGetCString(cfString, buffer, buffer.Length, EncodingUtf8))
        {
            return null;
        }

        var terminator = buffer.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(terminator >= 0 ? buffer[..terminator] : buffer);
    }

    /// <summary>Int out of a CFNumber. Returns null when absent or not a number.</summary>
    internal static int? ReadInt(IntPtr cfNumber)
    {
        if (cfNumber == IntPtr.Zero || CFGetTypeID(cfNumber) != CFNumberGetTypeID())
        {
            return null;
        }

        return CFNumberGetValue(cfNumber, CFNumberIntType, out var value) ? value : null;
    }

    /// <summary>Reads a device property by name, as a string.</summary>
    internal static string? GetStringProperty(IntPtr device, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, EncodingUtf8);
        try
        {
            return ReadString(DeviceGetProperty(device, cfKey));
        }
        finally
        {
            if (cfKey != IntPtr.Zero) { CFRelease(cfKey); }
        }
    }

    /// <summary>Reads a device property by name, as an int.</summary>
    internal static int? GetIntProperty(IntPtr device, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, EncodingUtf8);
        try
        {
            return ReadInt(DeviceGetProperty(device, cfKey));
        }
        finally
        {
            if (cfKey != IntPtr.Zero) { CFRelease(cfKey); }
        }
    }

    /// <summary>
    /// Builds the matching dictionary array IOHIDManager expects: one
    /// dictionary per (usage page, usage) pair.
    ///
    /// <para>
    /// Both mouse AND pointer usages are matched, because trackpads
    /// commonly present as Pointer rather than Mouse and matching only
    /// Mouse silently misses them on most laptops.
    /// </para>
    ///
    /// <para>Caller owns the returned CFArray and must <see cref="CFRelease"/> it.</para>
    /// </summary>
    internal static IntPtr CreateMatchingDictionaries(ReadOnlySpan<(int Page, int Usage)> pairs)
    {
        var array = CFArrayCreateMutable(IntPtr.Zero, pairs.Length, IntPtr.Zero);

        foreach (var (page, usage) in pairs)
        {
            var dict = CFDictionaryCreateMutable(IntPtr.Zero, 2, IntPtr.Zero, IntPtr.Zero);

            var pageKey = CFStringCreateWithCString(IntPtr.Zero, "DeviceUsagePage", EncodingUtf8);
            var usageKey = CFStringCreateWithCString(IntPtr.Zero, "DeviceUsage", EncodingUtf8);

            var pageValue = page;
            var usageValue = usage;
            var pageNumber = CFNumberCreate(IntPtr.Zero, CFNumberIntType, ref pageValue);
            var usageNumber = CFNumberCreate(IntPtr.Zero, CFNumberIntType, ref usageValue);

            CFDictionarySetValue(dict, pageKey, pageNumber);
            CFDictionarySetValue(dict, usageKey, usageNumber);
            CFArrayAppendValue(array, dict);

            // The array retains the dictionary, and the dictionary retains
            // its keys and values, so the local references are handed off.
            CFRelease(pageKey);
            CFRelease(usageKey);
            CFRelease(pageNumber);
            CFRelease(usageNumber);
            CFRelease(dict);
        }

        return array;
    }
}
