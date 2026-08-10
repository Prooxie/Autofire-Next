using System.Runtime.InteropServices;

namespace GameFlow.Infrastructure.Runtime.Sdl;

internal static partial class SdlInterop
{
    internal const uint InitJoystick = 0x00000200u;
    internal const uint InitHaptic = 0x00001000u;
    internal const uint InitGamepad = 0x00002000u;

    internal const uint HatCentered = 0x00u;
    internal const uint HatUp = 0x01u;
    internal const uint HatRight = 0x02u;
    internal const uint HatDown = 0x04u;
    internal const uint HatLeft = 0x08u;

    internal const string HintJoystickThread = "SDL_JOYSTICK_THREAD";
    internal const string HintJoystickAllowBackgroundEvents = "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS";
    internal const string HintJoystickHidApi = "SDL_JOYSTICK_HIDAPI";
    internal const string HintJoystickDirectInput = "SDL_JOYSTICK_DIRECTINPUT";
    internal const string HintXInputEnabled = "SDL_XINPUT_ENABLED";
    internal const string HintAutoUpdateJoysticks = "SDL_AUTO_UPDATE_JOYSTICKS";

    internal enum GamepadAxis
    {
        Invalid = -1,
        LeftX = 0,
        LeftY = 1,
        RightX = 2,
        RightY = 3,
        LeftTrigger = 4,
        RightTrigger = 5
    }

    internal enum GamepadButton
    {
        Invalid = -1,
        South = 0,
        East = 1,
        West = 2,
        North = 3,
        Back = 4,
        Guide = 5,
        Start = 6,
        LeftStick = 7,
        RightStick = 8,
        LeftShoulder = 9,
        RightShoulder = 10,
        DpadUp = 11,
        DpadDown = 12,
        DpadLeft = 13,
        DpadRight = 14,
        Misc1 = 15,
        RightPaddle1 = 16,
        LeftPaddle1 = 17,
        RightPaddle2 = 18,
        LeftPaddle2 = 19,
        Touchpad = 20
    }

    /// <summary>SDL_PowerState from SDL3/SDL_power.h.</summary>
    internal enum PowerState
    {
        Error = -1,
        Unknown = 0,
        OnBattery = 1,
        NoBattery = 2,
        Charging = 3,
        Charged = 4,
    }

    [LibraryImport("SDL3", EntryPoint = "SDL_SetMainReady")]
    internal static partial void SetMainReady();

    [LibraryImport("SDL3", EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool Init(uint flags);

    [LibraryImport("SDL3", EntryPoint = "SDL_QuitSubSystem")]
    internal static partial void QuitSubSystem(uint flags);

    [LibraryImport("SDL3", EntryPoint = "SDL_Quit")]
    internal static partial void Quit();

    [LibraryImport("SDL3", EntryPoint = "SDL_WasInit")]
    internal static partial uint WasInit(uint flags);

    [LibraryImport("SDL3", EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SetHint(string name, string value);

    [LibraryImport("SDL3", EntryPoint = "SDL_SetGamepadEventsEnabled")]
    internal static partial void SetGamepadEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);

    [LibraryImport("SDL3", EntryPoint = "SDL_SetJoystickEventsEnabled")]
    internal static partial void SetJoystickEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetError")]
    internal static partial IntPtr GetErrorPointer();

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepads")]
    internal static partial IntPtr GetGamepads(out int count);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoysticks")]
    internal static partial IntPtr GetJoysticks(out int count);

    [LibraryImport("SDL3", EntryPoint = "SDL_IsGamepad")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool IsGamepad(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_OpenGamepad")]
    internal static partial IntPtr OpenGamepad(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_CloseGamepad")]
    internal static partial void CloseGamepad(IntPtr gamepad);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadName")]
    internal static partial IntPtr GetGamepadNamePointer(IntPtr gamepad);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadNameForID")]
    internal static partial IntPtr GetGamepadNameForIdPointer(uint instanceId);

    // The OS device path. On Windows this carries the enumerator and
    // hardware id, which is how a HIDMaestro-created pad is told apart
    // from a real one: the virtual device is root-enumerated as
    // ROOT\HIDMAESTRO, while VID/PID deliberately impersonate real
    // hardware and so cannot be used to distinguish them.
    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadPathForID")]
    internal static partial IntPtr GetGamepadPathForIdPointer(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickPathForID")]
    internal static partial IntPtr GetJoystickPathForIdPointer(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadVendor")]
    internal static partial ushort GetGamepadVendor(IntPtr gamepad);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadVendorForID")]
    internal static partial ushort GetGamepadVendorForId(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadProduct")]
    internal static partial ushort GetGamepadProduct(IntPtr gamepad);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadProductForID")]
    internal static partial ushort GetGamepadProductForId(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadAxis")]
    internal static partial short GetGamepadAxis(IntPtr gamepad, GamepadAxis axis);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadButton")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool GetGamepadButton(IntPtr gamepad, GamepadButton button);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadPowerInfo")]
    internal static partial PowerState GetGamepadPowerInfo(IntPtr gamepad, out int percent);

    // Effect output. These were removed once because they were being
    // called from the runtime tick, where the blocking Bluetooth HID
    // write froze everything. They are back because the write now happens
    // on the SDL worker thread, which already owns device I/O and is the
    // only thread allowed to block on it.
    [LibraryImport("SDL3", EntryPoint = "SDL_RumbleGamepad")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool RumbleGamepad(
        IntPtr gamepad, ushort lowFrequencyRumble, ushort highFrequencyRumble, uint durationMs);

    [LibraryImport("SDL3", EntryPoint = "SDL_SetGamepadLED")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SetGamepadLED(IntPtr gamepad, byte red, byte green, byte blue);

    /// <summary>
    /// Device-specific effect report. This is the only route to adaptive
    /// triggers — SDL has no portable API for them — and SDL's PS5 driver
    /// owns the framing, so the payload here is the effect state alone
    /// without report id or the CRC that Bluetooth requires.
    /// </summary>
    [LibraryImport("SDL3", EntryPoint = "SDL_SendGamepadEffect")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SendGamepadEffect(IntPtr gamepad, ReadOnlySpan<byte> data, int size);

    /// <summary>
    /// SDL_SensorType — values verified against upstream
    /// include/SDL3/SDL_sensor.h, not written from memory.
    /// </summary>
    internal enum SensorType
    {
        Invalid = -1,
        Unknown = 0,
        Accel = 1,
        Gyro = 2,
        AccelLeft = 3,
        GyroLeft = 4,
        AccelRight = 5,
        GyroRight = 6
    }

    [LibraryImport("SDL3", EntryPoint = "SDL_GamepadHasSensor")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool GamepadHasSensor(IntPtr gamepad, SensorType type);

    [LibraryImport("SDL3", EntryPoint = "SDL_SetGamepadSensorEnabled")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SetGamepadSensorEnabled(IntPtr gamepad, SensorType type, [MarshalAs(UnmanagedType.I1)] bool enabled);

    /// <summary>
    /// Fills <paramref name="data"/> with <paramref name="numValues"/> floats.
    /// Per the upstream header: gyro is RADIANS PER SECOND, positive =
    /// counter-clockwise, and the three values are [0]=pitch (x axis),
    /// [1]=yaw (y axis), [2]=roll (z axis). Accelerometer is m/s².
    /// Both use axes: -X..+X left..right, -Y..+Y bottom..top,
    /// -Z..+Z farther..closer (controller held in front of you).
    /// </summary>
    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadSensorData")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool GetGamepadSensorData(IntPtr gamepad, SensorType type, [Out] float[] data, int numValues);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetNumGamepadTouchpads")]
    internal static partial int GetNumGamepadTouchpads(IntPtr gamepad);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetNumGamepadTouchpadFingers")]
    internal static partial int GetNumGamepadTouchpadFingers(IntPtr gamepad, int touchpad);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadTouchpadFinger")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool GetGamepadTouchpadFinger(
        IntPtr gamepad,
        int touchpad,
        int finger,
        out byte down,
        out float x,
        out float y,
        out float pressure);

    [LibraryImport("SDL3", EntryPoint = "SDL_UpdateGamepads")]
    internal static partial void UpdateGamepads();

    // NOTE: the SDL LED/rumble effect imports (SDL_SetGamepadLED,
    // SDL_RumbleGamepad, SDL_SetJoystickLED, SDL_RumbleJoystick) were
    // removed on purpose: those calls hold SDL's joystick lock through a
    // blocking Bluetooth HID write on DualSense pads, which froze the
    // runtime. Rumble passthrough will return on a dedicated effects
    // thread (see IRumbleFeedbackSource).
    [LibraryImport("SDL3", EntryPoint = "SDL_GetGamepadJoystick")]
    internal static partial IntPtr GetGamepadJoystick(IntPtr gamepad);

    [LibraryImport("SDL3", EntryPoint = "SDL_OpenJoystick")]
    internal static partial IntPtr OpenJoystick(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_CloseJoystick")]
    internal static partial void CloseJoystick(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickName")]
    internal static partial IntPtr GetJoystickNamePointer(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickNameForID")]
    internal static partial IntPtr GetJoystickNameForIdPointer(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickVendor")]
    internal static partial ushort GetJoystickVendor(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickVendorForID")]
    internal static partial ushort GetJoystickVendorForId(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickProduct")]
    internal static partial ushort GetJoystickProduct(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickProductForID")]
    internal static partial ushort GetJoystickProductForId(uint instanceId);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickPowerInfo")]
    internal static partial PowerState GetJoystickPowerInfo(IntPtr joystick, out int percent);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetNumJoystickAxes")]
    internal static partial int GetNumJoystickAxes(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickAxis")]
    internal static partial short GetJoystickAxis(IntPtr joystick, int axis);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetNumJoystickButtons")]
    internal static partial int GetNumJoystickButtons(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickButton")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool GetJoystickButton(IntPtr joystick, int button);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetNumJoystickHats")]
    internal static partial int GetNumJoystickHats(IntPtr joystick);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetJoystickHat")]
    internal static partial byte GetJoystickHat(IntPtr joystick, int hat);

    [LibraryImport("SDL3", EntryPoint = "SDL_UpdateJoysticks")]
    internal static partial void UpdateJoysticks();

    [LibraryImport("SDL3", EntryPoint = "SDL_free")]
    internal static partial void Free(IntPtr pointer);

    [LibraryImport("SDL3", EntryPoint = "SDL_AddGamepadMappingsFromFile", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AddGamepadMappingsFromFile(string file);

    internal static string GetError()
    {
        return ReadString(GetErrorPointer());
    }

    internal static string ReadString(IntPtr pointer)
    {
        return pointer == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }
}
