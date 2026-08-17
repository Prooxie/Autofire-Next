using System.Runtime.InteropServices;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Input;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// macOS counterpart to WindowsRawInputReader / LinuxRawInputReader — an
/// IOHIDManager whose input-value callback runs on a dedicated thread
/// hosting its own CFRunLoop, updating shared state per device.
///
/// <para>
/// <b>Per-device, at last.</b> This used to be a CGEventTap, which
/// reports ONE aggregate stream for every keyboard and mouse on the
/// system — there is no "which physical keyboard" at that API level, so
/// <see cref="GetPressedKeys"/> and <see cref="ReadMouseFrame"/> always
/// returned empty and everything fell through to the aggregate reads.
/// IOHIDManager carries device identity on every value: the element says
/// which device it came from, so state is now bucketed by the same
/// catalog id <see cref="MacInputDeviceScanner"/> publishes and the
/// per-device reads return real data. The aggregate reads remain, both as
/// the union across devices and as the fallback path
/// <see cref="IKeyboardStateSource"/> already documents.
/// </para>
///
/// <para>
/// <b>Requires Input Monitoring consent</b> (System Settings > Privacy
/// &amp; Security > Input Monitoring), and asks for it explicitly rather
/// than inferring it from silence. This matters more here than it did
/// with the event tap: CGEventTapCreate at least returned null when
/// consent was missing, whereas without it IOHIDManager still creates,
/// still opens, still enumerates devices — and the value callback simply
/// never fires. A refusal and a broken implementation look identical
/// from the outside, so <see cref="HasInputMonitoringConsent"/> checks
/// first and says which one happened in the log.
/// </para>
///
/// <para>
/// <b>Hot-plug works for reading.</b> The manager keeps matching devices
/// after it is open, so a keyboard plugged in later starts reporting
/// without a restart — unlike LinuxRawInputReader, which opens its fds
/// once at construction. The Devices page still lists what existed at its
/// last scan.
/// </para>
///
/// <para>
/// <b>Untested on hardware.</b> Written on Windows, where none of it can
/// execute. Structured so a macOS tester can localise a fault from the
/// log alone: the consent check, the manager open and the device match
/// each report separately, so "no input" always names its own cause.
/// </para>
/// </summary>
public sealed class MacRawInputReader : IKeyboardStateSource, IMouseStateSource, IDisposable
{
    private readonly ILogger<MacRawInputReader> logger;
    private readonly Lock gate = new();

    // Keyed by catalog device id, same shape as LinuxRawInputReader.
    // Buckets appear the first time a device actually reports something,
    // rather than being pre-created from a scan — the manager decides
    // which devices it matches, and it keeps matching new ones after
    // startup.
    private readonly Dictionary<string, HashSet<int>> pressedKeysByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MouseAccumulator> mouseByDevice = new(StringComparer.Ordinal);

    // Touched ONLY from the HID callback thread, so it needs no lock:
    // resolving an id means several CoreFoundation property reads, and
    // doing that once per device beats doing it once per keystroke.
    private readonly Dictionary<IntPtr, ResolvedDevice> devicesByHandle = [];

    private static readonly IReadOnlySet<int> EmptyKeys = new HashSet<int>();

    private GCHandle selfHandle;
    private IntPtr manager;
    private IntPtr hostRunLoop;
    private IntPtr runLoopMode;
    private Thread? hidThread;
    private volatile bool disposed;
    private readonly ManualResetEventSlim started = new(false);

    private sealed class MouseAccumulator
    {
        // Drained to zero on every read — see MouseFrame's own doc comment.
        public int Dx;
        public int Dy;
        public int WheelDelta;

        // Level state — NOT drained; reflects current physical state.
        public bool Left;
        public bool Right;
        public bool Middle;
        public bool Button4;
        public bool Button5;
    }

    /// <summary>
    /// A device handle resolved to its catalog identity. <see cref="Id"/>
    /// is null for a device that classified as neither keyboard nor mouse
    /// — its values are dropped rather than filed under a guessed id.
    /// </summary>
    private readonly record struct ResolvedDevice(string? Id, DeviceCategory Category);

    public MacRawInputReader(ILogger<MacRawInputReader> logger)
    {
        this.logger = logger;
        selfHandle = GCHandle.Alloc(this);

        hidThread = new Thread(RunHidThread) { IsBackground = true, Name = "iohid-reader" };
        hidThread.Start();

        // Bounded wait so callers get a settled state (capturing, or
        // failed-and-logged) rather than a brief ambiguous window right
        // after construction. Deliberately short: the consent prompt
        // blocks on a human, and construction must not.
        _ = started.Wait(TimeSpan.FromSeconds(2));
    }

    private void RunHidThread()
    {
        try
        {
            if (TryStartCapture())
            {
                started.Set();
                MacHidInterop.CFRunLoopRun(); // blocks here until Dispose() calls CFRunLoopStop
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "IOHID: the input thread failed to start. Keyboard/mouse-as-a-source will read as neutral.");
        }
        finally
        {
            // Nothing in here may throw. This is the outermost frame of a
            // background thread, so an exception escaping it takes the
            // whole process down rather than just the reader.
            try
            {
                started.Set();

                // Teardown belongs on this thread: unscheduling a manager
                // from a run loop another thread is spinning is not safe,
                // so Dispose only stops the loop and waits here.
                Teardown();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "IOHID: the input thread failed to shut down cleanly.");
            }
        }
    }

    /// <summary>
    /// Consent, manager, matching, callback, run loop, open — in that
    /// order, each failure reporting itself. Returns false when there is
    /// nothing to run a loop for.
    /// </summary>
    private bool TryStartCapture()
    {
        if (!HasInputMonitoringConsent() || disposed)
        {
            return false;
        }

        manager = MacHidInterop.ManagerCreate(IntPtr.Zero, 0);
        if (manager == IntPtr.Zero)
        {
            logger.LogWarning(
                "IOHID: IOHIDManagerCreate returned null. Input Monitoring was already confirmed granted, " +
                "so this is a framework-level failure rather than a permission problem.");
            return false;
        }

        (int Page, int Usage)[] pairs =
        [
            (MacHidInterop.UsagePageGenericDesktop, MacHidInterop.UsageKeyboard),
            (MacHidInterop.UsagePageGenericDesktop, MacHidInterop.UsageMouse),

            // Trackpads usually present as Pointer, not Mouse — the same
            // pair list MacInputDeviceScanner enumerates with, so what
            // reports input and what the Devices page lists agree.
            (MacHidInterop.UsagePageGenericDesktop, MacHidInterop.UsagePointer),
        ];

        var matching = MacHidInterop.CreateMatchingDictionaries(pairs);
        try
        {
            MacHidInterop.ManagerSetDeviceMatchingMultiple(manager, matching);
        }
        finally
        {
            if (matching != IntPtr.Zero) { MacHidInterop.CFRelease(matching); }
        }

        unsafe
        {
            // Must explicitly say unmanaged[Cdecl] here to match
            // InputValueCallback's [UnmanagedCallersOnly(CallConvs =
            // [typeof(CallConvCdecl)])] — a bare `unmanaged<>` (no
            // explicit convention) doesn't imply Cdecl specifically, and
            // the compiler won't assume the two agree (CS8786).
            delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr, void> callback = &InputValueCallback;
            MacHidInterop.ManagerRegisterInputValueCallback(manager, (IntPtr)callback, GCHandle.ToIntPtr(selfHandle));
        }

        hostRunLoop = MacHidInterop.CFRunLoopGetCurrent();
        runLoopMode = MacHidInterop.CreateDefaultRunLoopMode();
        MacHidInterop.ManagerScheduleWithRunLoop(manager, hostRunLoop, runLoopMode);

        // Options 0 = kIOHIDOptionsTypeNone. Explicitly NOT
        // kIOHIDOptionsTypeSeizeDevice (1), which would take the devices
        // away from the rest of the system — this reader observes input,
        // exactly as the listen-only event tap it replaces did.
        var opened = MacHidInterop.ManagerOpen(manager, 0);
        if (opened != MacHidInterop.ReturnSuccess)
        {
            logger.LogWarning(
                "IOHID: IOHIDManagerOpen failed with IOReturn 0x{Result:X8}. Input Monitoring was already " +
                "confirmed granted, so consent is not the cause here. No input will be read.",
                opened);
            return false;
        }

        logger.LogInformation(
            "IOHID: input capture active — {Count} keyboard/mouse device(s) matched.", CountMatchedDevices());
        return true;
    }

    /// <summary>
    /// The Input Monitoring gate, checked explicitly and logged either
    /// way.
    ///
    /// <para>
    /// Without this, a refusal is invisible: the manager opens, devices
    /// enumerate, and the value callback never fires — which reads
    /// exactly like a reader that does not work. Every branch below logs,
    /// so the answer to "why is nothing happening" is always in the log.
    /// </para>
    /// </summary>
    private bool HasInputMonitoringConsent()
    {
        var access = MacHidInterop.TryCheckAccess();
        if (access is null)
        {
            logger.LogInformation(
                "IOHID: this macOS build has no IOHIDCheckAccess (pre-10.15), so there is no Input Monitoring " +
                "gate to clear. Opening the manager directly.");
            return true;
        }

        if (access == MacHidInterop.AccessType.Granted)
        {
            logger.LogInformation("IOHID: Input Monitoring permission is already granted.");
            return true;
        }

        logger.LogInformation(
            "IOHID: Input Monitoring permission reports {Access}; asking for it now.", access);

        var prompted = MacHidInterop.TryRequestAccess();
        var after = MacHidInterop.TryCheckAccess() ?? MacHidInterop.AccessType.Unknown;
        if (after == MacHidInterop.AccessType.Granted)
        {
            logger.LogInformation("IOHID: Input Monitoring permission granted; input capture starting.");
            return true;
        }

        logger.LogWarning(
            "IOHID: Input Monitoring permission was NOT granted (IOHIDCheckAccess reports {Access}; " +
            "IOHIDRequestAccess returned {Prompted}). This is a consent decision, not a failure of this " +
            "reader — had it proceeded, the manager would have opened and matched devices normally and the " +
            "input callback would simply never have fired, which is indistinguishable from broken code. " +
            "Grant it under System Settings > Privacy & Security > Input Monitoring and restart GameFlow; " +
            "macOS shows the prompt only once, so a refusal can only be undone there. " +
            "Keyboard/mouse-as-a-source reads as neutral until then.",
            after,
            prompted is null ? "unavailable" : prompted.Value.ToString());
        return false;
    }

    private int CountMatchedDevices()
    {
        var devices = MacHidInterop.ManagerCopyDevices(manager);
        if (devices == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return (int)MacHidInterop.CFSetGetCount(devices);
        }
        finally
        {
            MacHidInterop.CFRelease(devices);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void InputValueCallback(IntPtr context, int result, IntPtr sender, IntPtr value)
    {
        MacRawInputReader? reader = null;
        try
        {
            if (context == IntPtr.Zero || value == IntPtr.Zero || result != MacHidInterop.ReturnSuccess)
            {
                return;
            }

            reader = GCHandle.FromIntPtr(context).Target as MacRawInputReader;
            reader?.HandleValue(value);
        }
        catch (Exception exception)
        {
            // A native callback must never let a managed exception escape
            // across the P/Invoke boundary — that's undefined behavior on
            // the C side, not just a missed event.
            reader?.logger.LogDebug(exception, "IOHID: input value callback threw.");
        }
    }

    private void HandleValue(IntPtr value)
    {
        var element = MacHidInterop.ValueGetElement(value);
        if (element == IntPtr.Zero)
        {
            return;
        }

        var device = MacHidInterop.ElementGetDevice(element);
        if (device == IntPtr.Zero)
        {
            return;
        }

        var page = MacHidInterop.ElementGetUsagePage(element);
        var usage = MacHidInterop.ElementGetUsage(element);
        var reading = (long)MacHidInterop.ValueGetIntegerValue(value);

        switch (page)
        {
            case MacHidInterop.UsagePageKeyboard:
                HandleKey(device, (int)usage, isDown: reading != 0);
                break;
            case MacHidInterop.UsagePageButton:
                HandleButton(device, usage, isDown: reading != 0);
                break;
            case (uint)MacHidInterop.UsagePageGenericDesktop:
                HandleAxis(device, element, usage, reading);
                break;
        }
    }

    private void HandleKey(IntPtr device, int usage, bool isDown)
    {
        // Usages below MacHidKeyCodeMap.FirstRealKeyUsage are the
        // roll-over / POST-fail error codes a keyboard sends when it
        // cannot report the real state. Nothing maps them, so the lookup
        // below drops them without a special case.
        if (!MacHidKeyCodeMap.HidUsageToVirtualKey.TryGetValue(usage, out var virtualKey))
        {
            return;
        }

        var resolved = Resolve(device);
        if (resolved.Id is null || resolved.Category != DeviceCategory.Keyboard)
        {
            return;
        }

        lock (gate)
        {
            if (!pressedKeysByDevice.TryGetValue(resolved.Id, out var keys))
            {
                keys = [];
                pressedKeysByDevice[resolved.Id] = keys;
            }

            if (isDown) { keys.Add(virtualKey); } else { keys.Remove(virtualKey); }
        }
    }

    private void HandleButton(IntPtr device, uint usage, bool isDown)
    {
        var resolved = Resolve(device);
        if (resolved.Id is null || resolved.Category != DeviceCategory.Mouse)
        {
            return;
        }

        lock (gate)
        {
            var mouse = MouseFor(resolved.Id);
            switch (usage)
            {
                case 1: mouse.Left = isDown; break;
                case 2: mouse.Right = isDown; break;
                case 3: mouse.Middle = isDown; break;
                case 4: mouse.Button4 = isDown; break;
                case 5: mouse.Button5 = isDown; break;
            }
        }
    }

    private void HandleAxis(IntPtr device, IntPtr element, uint usage, long reading)
    {
        if (reading == 0)
        {
            return;
        }

        // A keyboard also carries Generic Desktop elements, so the usage
        // filter comes before anything else.
        if (usage is not (MacHidInterop.UsageX or MacHidInterop.UsageY or MacHidInterop.UsageWheel))
        {
            return;
        }

        // X/Y are movement only when the descriptor says they are deltas.
        // On an absolute pointer they are a position — treating "1180
        // pixels from the left" as a delta would peg the synthesized stick
        // at full deflection. Wheel is inherently relative and needs no
        // such guard. Absolute pointers are therefore silently unsupported
        // rather than wrong, which is the same trade the old writer made
        // for its own absolute/relative mismatch.
        if (usage != MacHidInterop.UsageWheel && !MacHidInterop.ElementIsRelative(element))
        {
            return;
        }

        var resolved = Resolve(device);
        if (resolved.Id is null || resolved.Category != DeviceCategory.Mouse)
        {
            return;
        }

        lock (gate)
        {
            var mouse = MouseFor(resolved.Id);
            switch (usage)
            {
                case MacHidInterop.UsageX: mouse.Dx += (int)reading; break;
                case MacHidInterop.UsageY: mouse.Dy += (int)reading; break;

                // *120 to match MouseFrame's documented "Windows wheel
                // units (multiples of 120)" convention, as evdev does.
                case MacHidInterop.UsageWheel: mouse.WheelDelta += (int)reading * 120; break;
            }
        }
    }

    /// <summary>Caller already holds <see cref="gate"/>.</summary>
    private MouseAccumulator MouseFor(string deviceId)
    {
        if (!mouseByDevice.TryGetValue(deviceId, out var mouse))
        {
            mouse = new MouseAccumulator();
            mouseByDevice[deviceId] = mouse;
        }
        return mouse;
    }

    /// <summary>
    /// Device handle to catalog identity, cached. Called only from the HID
    /// thread, which is what makes the uncached dictionary safe.
    ///
    /// <para>
    /// The cache is keyed on the IOHIDDeviceRef pointer, which is stable
    /// while the device is attached. CoreFoundation could in principle
    /// hand the same address to a different device after an unplug, which
    /// would attribute the new device's input to the old one's id until
    /// restart — a known, small risk taken in exchange for not re-reading
    /// six device properties on every keystroke.
    /// </para>
    /// </summary>
    private ResolvedDevice Resolve(IntPtr device)
    {
        if (devicesByHandle.TryGetValue(device, out var known))
        {
            return known;
        }

        var category = MacInputDeviceScanner.ClassifyDevice(device);
        var resolved = category == DeviceCategory.Unknown
            ? new ResolvedDevice(null, category)
            : new ResolvedDevice(MacInputDeviceScanner.BuildDeviceId(device, category), category);

        devicesByHandle[device] = resolved;

        if (resolved.Id is null)
        {
            logger.LogDebug("IOHID: a matched device classified as neither keyboard nor mouse; its input is ignored.");
        }
        else
        {
            logger.LogDebug("IOHID: first input from {DeviceId} ({Category}).", resolved.Id, resolved.Category);
        }

        return resolved;
    }

    public IReadOnlySet<int> GetPressedKeys(string deviceId)
    {
        // A profile saved before per-device enumeration existed still
        // references the aggregate id; honour it rather than reporting
        // nothing for a slot the user did assign.
        if (string.Equals(deviceId, MacInputDeviceScanner.AggregateKeyboardId, StringComparison.Ordinal))
        {
            return GetPressedKeysAggregate();
        }

        lock (gate)
        {
            return pressedKeysByDevice.TryGetValue(deviceId, out var keys) && keys.Count > 0
                ? new HashSet<int>(keys)
                : EmptyKeys;
        }
    }

    public IReadOnlySet<int> GetPressedKeysAggregate()
    {
        lock (gate)
        {
            if (pressedKeysByDevice.Count == 0)
            {
                return EmptyKeys;
            }

            var union = new HashSet<int>();
            foreach (var keys in pressedKeysByDevice.Values)
            {
                union.UnionWith(keys);
            }
            return union.Count == 0 ? EmptyKeys : union;
        }
    }

    public MouseFrame ReadMouseFrame(string deviceId)
    {
        if (string.Equals(deviceId, MacInputDeviceScanner.AggregateMouseId, StringComparison.Ordinal))
        {
            return ReadMouseFrameAggregate();
        }

        lock (gate)
        {
            return mouseByDevice.TryGetValue(deviceId, out var mouse) ? Drain(mouse) : default;
        }
    }

    public MouseFrame ReadMouseFrameAggregate()
    {
        lock (gate)
        {
            if (mouseByDevice.Count == 0)
            {
                return default;
            }

            int dx = 0, dy = 0, wheel = 0;
            bool left = false, right = false, middle = false, button4 = false, button5 = false;
            foreach (var mouse in mouseByDevice.Values)
            {
                dx += mouse.Dx; dy += mouse.Dy; wheel += mouse.WheelDelta;
                left |= mouse.Left; right |= mouse.Right; middle |= mouse.Middle;
                button4 |= mouse.Button4; button5 |= mouse.Button5;
                mouse.Dx = 0; mouse.Dy = 0; mouse.WheelDelta = 0;
            }
            return new MouseFrame(dx, dy, left, right, middle, button4, button5, wheel);
        }
    }

    /// <summary>Reads the accumulator into a frame and resets ONLY the drain fields (Dx/Dy/WheelDelta) — button levels persist. Caller already holds <see cref="gate"/>.</summary>
    private static MouseFrame Drain(MouseAccumulator mouse)
    {
        var frame = new MouseFrame(mouse.Dx, mouse.Dy, mouse.Left, mouse.Right, mouse.Middle, mouse.Button4, mouse.Button5, mouse.WheelDelta);
        mouse.Dx = 0;
        mouse.Dy = 0;
        mouse.WheelDelta = 0;
        return frame;
    }

    /// <summary>Runs on the HID thread once its run loop has exited.</summary>
    private void Teardown()
    {
        if (manager != IntPtr.Zero)
        {
            // Detach the callback FIRST: past this point no native code
            // can call back into managed state, which is what makes
            // freeing the GCHandle in Dispose safe.
            MacHidInterop.ManagerRegisterInputValueCallback(manager, IntPtr.Zero, IntPtr.Zero);

            if (hostRunLoop != IntPtr.Zero && runLoopMode != IntPtr.Zero)
            {
                MacHidInterop.ManagerUnscheduleFromRunLoop(manager, hostRunLoop, runLoopMode);
            }

            _ = MacHidInterop.ManagerClose(manager, 0);
            MacHidInterop.CFRelease(manager);
            manager = IntPtr.Zero;
        }

        if (runLoopMode != IntPtr.Zero)
        {
            MacHidInterop.CFRelease(runLoopMode);
            runLoopMode = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        var runLoop = hostRunLoop;
        if (runLoop != IntPtr.Zero)
        {
            MacHidInterop.CFRunLoopStop(runLoop);
        }

        var stopped = hidThread?.Join(TimeSpan.FromSeconds(1)) ?? true;
        if (!stopped && runLoop != IntPtr.Zero)
        {
            // CoreFoundation drops a stop that lands in the window between
            // the loop being scheduled and it actually running. One retry
            // covers that window.
            MacHidInterop.CFRunLoopStop(runLoop);
            stopped = hidThread!.Join(TimeSpan.FromSeconds(1));
        }

        if (stopped)
        {
            if (selfHandle.IsAllocated)
            {
                selfHandle.Free();
            }

            // Safe only here: a thread that has exited will not touch this
            // again, whereas one still in flight ends by setting it, and
            // an ObjectDisposedException thrown from that thread's finally
            // block would take the process with it.
            started.Dispose();
        }
        else
        {
            // Both the handle and the event are leaked on purpose. The
            // thread is a background thread and dies with the process, but
            // until it does the native callback can still dereference this
            // context — freeing it first turns a slow shutdown into a
            // crash.
            logger.LogDebug("IOHID: the input thread did not stop in time; its callback context is left allocated deliberately.");
        }
    }
}
