using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>Implemented by the Raw Input reader so the UI can attach it
/// to the main window's HWND once that window is open.</summary>
public interface IRawInputAttacher
{
    void AttachToHwnd(IntPtr hwnd);
}

/// <summary>No-op attacher used on non-Windows platforms.</summary>
public sealed class NullRawInputAttacher : IRawInputAttacher
{
    public void AttachToHwnd(IntPtr hwnd) { }
}

/// <summary>
/// Windows Raw Input keyboard + mouse reader. Subclasses the main
/// Avalonia window's WndProc to intercept <c>WM_INPUT</c> directly on
/// the UI thread, and registers both usages with
/// <c>RIDEV_INPUTSINK</c> so it keeps receiving while another window
/// (the game) is foreground. State is keyed by the same canonical catalog
/// id the enumerator publishes (see RawInputDeviceScanner.BuildDeviceId),
/// which also merges sibling HID collections of one physical interface —
/// so events from any endpoint of a device land on the single entry the
/// UI shows. Queried via <see cref="IKeyboardStateSource"/> and
/// <see cref="IMouseStateSource"/>.
/// The original WndProc is chained via CallWindowProc so Avalonia
/// continues to work normally.
/// </summary>
public sealed class WindowsRawInputReader : IKeyboardStateSource, IMouseStateSource, IRawInputAttacher, IDisposable
{
    private readonly ILogger<WindowsRawInputReader> logger;

    private readonly ConcurrentDictionary<IntPtr, string> keyboardIdByHandle = new();
    private readonly ConcurrentDictionary<IntPtr, string> mouseIdByHandle = new();
    private readonly ConcurrentDictionary<string, DeviceState> keyboardStateById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MouseStateData> mouseStateById =
        new(StringComparer.OrdinalIgnoreCase);

    // Reused native buffer for WM_INPUT payloads (they are ~48 bytes for
    // keyboards/mice; 1 KiB is generous). WM_INPUT is delivered on the UI
    // thread only, so a single buffer needs no synchronization — and it
    // removes a per-event AllocHGlobal/FreeHGlobal pair at input rate.
    private const int RawInputBufferSize = 1024;
    private readonly IntPtr rawInputBuffer = Marshal.AllocHGlobal(RawInputBufferSize);

    private IntPtr attachedHwnd = IntPtr.Zero;
    private IntPtr originalWndProc = IntPtr.Zero;
    private WndProcDelegate? wndProcRef; // keep alive for the lifetime of the subclass
    private bool disposed;

    private static readonly IReadOnlySet<int> EmptyKeys = new HashSet<int>();

    public WindowsRawInputReader(ILogger<WindowsRawInputReader> logger)
    {
        this.logger = logger;
    }

    public IReadOnlySet<int> GetPressedKeys(string deviceId)
    {
        return !string.IsNullOrEmpty(deviceId)
            && keyboardStateById.TryGetValue(deviceId, out var state)
            ? state.Snapshot()
            : EmptyKeys;
    }

    public IReadOnlySet<int> GetPressedKeysAggregate()
    {
        var union = new HashSet<int>();
        foreach (var state in keyboardStateById.Values)
        {
            union.UnionWith(state.Snapshot());
        }
        return union;
    }

    public MouseFrame ReadMouseFrameAggregate()
    {
        int dx = 0, dy = 0, wheel = 0;
        bool l = false, r = false, m = false, b4 = false, b5 = false;
        foreach (var state in mouseStateById.Values)
        {
            var frame = state.ReadAndReset();
            dx += frame.Dx; dy += frame.Dy; wheel += frame.WheelDelta;
            l |= frame.Left; r |= frame.Right; m |= frame.Middle;
            b4 |= frame.Button4; b5 |= frame.Button5;
        }
        return new MouseFrame(dx, dy, l, r, m, b4, b5, wheel);
    }

    public MouseFrame ReadMouseFrame(string deviceId)
    {
        return !string.IsNullOrEmpty(deviceId)
            && mouseStateById.TryGetValue(deviceId, out var state)
            ? state.ReadAndReset()
            : default;
    }

    public void AttachToHwnd(IntPtr hwnd)
    {
        if (disposed || !OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
        {
            return;
        }
        if (attachedHwnd != IntPtr.Zero)
        {
            return; // already attached
        }

        try
        {
            wndProcRef = WndProc;
            var newProcPtr = Marshal.GetFunctionPointerForDelegate(wndProcRef);

            originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProcPtr);
            if (originalWndProc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 0)
                {
                    throw new InvalidOperationException("SetWindowLongPtr failed: " + err);
                }
            }
            attachedHwnd = hwnd;

            // Keyboards (usage 6) + mice (usage 2) on usage page 1.
            // INPUTSINK = keep receiving even when the app isn't foreground.
            var rid = new[]
            {
                new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, hwndTarget = hwnd },
                new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                throw new InvalidOperationException(
                    "RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
            }

            logger.LogInformation("Raw Input attached to main window HWND 0x{Hwnd:X}.", (long)hwnd);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to attach Raw Input reader to main window.");
            // Best-effort restore if we partly attached.
            if (attachedHwnd != IntPtr.Zero && originalWndProc != IntPtr.Zero)
            {
                try { _ = SetWindowLongPtr(attachedHwnd, GWLP_WNDPROC, originalWndProc); } catch { }
            }
            attachedHwnd = IntPtr.Zero;
            originalWndProc = IntPtr.Zero;
            wndProcRef = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (attachedHwnd != IntPtr.Zero && originalWndProc != IntPtr.Zero)
        {
            try { _ = SetWindowLongPtr(attachedHwnd, GWLP_WNDPROC, originalWndProc); } catch { }
        }
        attachedHwnd = IntPtr.Zero;
        originalWndProc = IntPtr.Zero;
        wndProcRef = null;
        Marshal.FreeHGlobal(rawInputBuffer);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleWmInput(lParam); }
            catch (Exception exception) { logger.LogDebug(exception, "WM_INPUT handler error."); }
        }
        return CallWindowProc(originalWndProc, hwnd, msg, wParam, lParam);
    }

    private void HandleWmInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0) return;
        if (size == 0 || size > RawInputBufferSize) return;

        var buffer = rawInputBuffer;
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType == RIM_TYPEKEYBOARD)
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + (int)headerSize);
                var virtualKey = WindowsRawKeyNormalizer.Normalize(kb.VKey, kb.MakeCode, kb.Flags);
                if (virtualKey == 0 || virtualKey == 0xFF) return;
                bool keyUp = (kb.Flags & RI_KEY_BREAK) != 0;
                UpdateKey(header.hDevice, virtualKey, keyUp);
            }
            else if (header.dwType == RIM_TYPEMOUSE)
            {
                var mouse = Marshal.PtrToStructure<RAWMOUSE>(buffer + (int)headerSize);
                UpdateMouse(header.hDevice, mouse);
            }
        }
    }

    private void UpdateKey(IntPtr hDevice, int virtualKey, bool keyUp)
    {
        var id = keyboardIdByHandle.GetOrAdd(hDevice, static h => ResolveCatalogId(h, "keyboard"));
        var state = keyboardStateById.GetOrAdd(id, static _ => new DeviceState());
        lock (state.Lock)
        {
            if (keyUp) state.Pressed.Remove(virtualKey);
            else       state.Pressed.Add(virtualKey);
        }
    }

    private void UpdateMouse(IntPtr hDevice, RAWMOUSE m)
    {
        var id = mouseIdByHandle.GetOrAdd(hDevice, static h => ResolveCatalogId(h, "mouse"));
        var state = mouseStateById.GetOrAdd(id, static _ => new MouseStateData());
        lock (state.Lock)
        {
            state.AccumX += m.lLastX;
            state.AccumY += m.lLastY;
            var bf = m.usButtonFlags;
            if ((bf & 0x0001) != 0) state.Left = true;    if ((bf & 0x0002) != 0) state.Left = false;
            if ((bf & 0x0004) != 0) state.Right = true;   if ((bf & 0x0008) != 0) state.Right = false;
            if ((bf & 0x0010) != 0) state.Middle = true;  if ((bf & 0x0020) != 0) state.Middle = false;
            if ((bf & 0x0040) != 0) state.Button4 = true; if ((bf & 0x0080) != 0) state.Button4 = false;
            if ((bf & 0x0100) != 0) state.Button5 = true; if ((bf & 0x0200) != 0) state.Button5 = false;
            if ((bf & RI_MOUSE_WHEEL) != 0)
            {
                // usButtonData is a signed 16-bit wheel delta (multiples of
                // 120, positive = away from the user / scroll up).
                state.AccumWheel += unchecked((short)m.usButtonData);
            }
        }
    }

    /// <summary>
    /// Resolves the canonical catalog id (rawinput-{kind}-{hash}) for a raw
    /// device handle — the SAME id RawInputDeviceScanner publishes, so state
    /// lookups by catalog id always resolve. Falls back to a handle-derived
    /// id when the path can't be read (such a device is also absent from the
    /// catalog, so the fallback is only ever a container for orphan events).
    /// </summary>
    private static string ResolveCatalogId(IntPtr hDevice, string kind)
    {
        try
        {
            var path = ResolveDevicePath(hDevice);
            if (!string.IsNullOrEmpty(path))
            {
                return RawInput.RawInputDeviceScanner.BuildDeviceId(kind, path!);
            }
        }
        catch
        {
            // fall through to the handle-derived id
        }
        return $"rawinput-{kind}-h{hDevice:x}";
    }

    private static string? ResolveDevicePath(IntPtr hDevice)
    {
        uint charCount = 0;
        if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount) != 0
            || charCount == 0)
        {
            return null;
        }
        var bytes = ((int)charCount + 1) * 2;
        var ptr = Marshal.AllocHGlobal(bytes);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, ptr, ref charCount) > 0)
            {
                return Marshal.PtrToStringUni(ptr);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private sealed class DeviceState
    {
        public readonly object Lock = new();
        public readonly HashSet<int> Pressed = new();
        public IReadOnlySet<int> Snapshot()
        {
            lock (Lock) { return new HashSet<int>(Pressed); }
        }
    }

    private sealed class MouseStateData
    {
        public readonly object Lock = new();
        public int AccumX;
        public int AccumY;
        public int AccumWheel;
        public bool Left, Right, Middle, Button4, Button5;

        public MouseFrame ReadAndReset()
        {
            lock (Lock)
            {
                var frame = new MouseFrame(AccumX, AccumY, Left, Right, Middle, Button4, Button5, AccumWheel);
                AccumX = 0;
                AccumY = 0;
                AccumWheel = 0;
                return frame;
            }
        }
    }

    // ─── Win32 constants ───
    private const uint WM_INPUT  = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const ushort RI_KEY_BREAK = 0x01;
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private const int GWLP_WNDPROC = -4;

    // ─── Structs ───
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    // Explicit offsets to match the native union + 4-byte alignment.
    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)]  public ushort usFlags;
        [FieldOffset(4)]  public ushort usButtonFlags;
        [FieldOffset(6)]  public ushort usButtonData;
        [FieldOffset(8)]  public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ─── P/Invokes ───
    // SetWindowLongPtrW exists on both x86 (aliased to SetWindowLongW) and x64.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData,
        ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
}
