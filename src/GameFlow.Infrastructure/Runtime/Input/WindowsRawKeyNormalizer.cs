namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Converts the generic and lock-state-dependent virtual keys in a Win32
/// <c>RAWKEYBOARD</c> packet into stable physical-key identifiers.
/// </summary>
internal static class WindowsRawKeyNormalizer
{
    internal const ushort KeyE0 = 0x02;
    internal const ushort KeyE1 = 0x04;

    public static int Normalize(ushort virtualKey, ushort makeCode, ushort flags)
    {
        var isExtended = (flags & KeyE0) != 0;
        var isE1 = (flags & KeyE1) != 0;

        // Print Screen emits an artificial E0 Shift packet and Pause may
        // emit an E1 Control packet. Neither represents a pressed modifier.
        if (virtualKey == 0x10 && isExtended)
        {
            return 0;
        }
        if (virtualKey == 0x11 && isE1)
        {
            return 0;
        }

        // RAWINPUT reports generic VK_SHIFT / VK_CONTROL / VK_MENU even
        // though MakeCode and E0 retain the physical side.
        if (virtualKey == 0x10)
        {
            return makeCode == 0x36 ? 0xA1 : 0xA0;
        }
        if (virtualKey == 0x11)
        {
            return isExtended ? 0xA3 : 0xA2;
        }
        if (virtualKey == 0x12)
        {
            return isExtended ? 0xA5 : 0xA4;
        }

        // Both Enter keys share VK_RETURN. The keypad key is E0-prefixed.
        if (virtualKey == 0x0D && isExtended)
        {
            return KeyboardVirtualKeys.NumpadEnter;
        }

        // With Num Lock off, Windows changes keypad digits into navigation
        // VKs. Their unprefixed Set-1 make codes still identify the physical
        // keypad, while the dedicated navigation cluster is E0-prefixed.
        if (!isExtended)
        {
            var numpadVirtualKey = makeCode switch
            {
                0x52 => 0x60, // keypad 0 / Insert
                0x4F => 0x61, // keypad 1 / End
                0x50 => 0x62, // keypad 2 / Down
                0x51 => 0x63, // keypad 3 / Page Down
                0x4B => 0x64, // keypad 4 / Left
                0x4C => 0x65, // keypad 5 / Clear
                0x4D => 0x66, // keypad 6 / Right
                0x47 => 0x67, // keypad 7 / Home
                0x48 => 0x68, // keypad 8 / Up
                0x49 => 0x69, // keypad 9 / Page Up
                0x53 => 0x6E, // keypad decimal / Delete
                _ => 0,
            };
            if (numpadVirtualKey != 0)
            {
                return numpadVirtualKey;
            }
        }

        return virtualKey;
    }
}
