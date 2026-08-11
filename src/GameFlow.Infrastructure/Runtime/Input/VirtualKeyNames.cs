using System.Collections.Frozen;

namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Human-readable names for Windows virtual-key codes, used by the input
/// viewer instead of raw hex. Letters/digits map to themselves; everything
/// commonly found on a keyboard has an explicit name; unknown codes fall
/// back to <c>VK 0x??</c> so nothing ever displays blank.
/// </summary>
public static class VirtualKeyNames
{
    public static string GetName(int vk) => vk switch
    {
        KeyboardVirtualKeys.NumpadEnter => "Num Enter",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),          // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),          // A-Z
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",             // Numpad 0-9
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",                // F1-F24
        _ => Named.TryGetValue(vk, out var name) ? name : $"VK 0x{vk:X2}",
    };

    private static readonly FrozenDictionary<int, string> Named = new Dictionary<int, string>
    {
        [0x01] = "LMB",           [0x02] = "RMB",            [0x04] = "MMB",
        [0x08] = "Backspace",     [0x09] = "Tab",            [0x0C] = "Clear",
        [0x0D] = "Enter",         [0x13] = "Pause",          [0x14] = "Caps Lock",
        [0x1B] = "Esc",           [0x20] = "Space",
        [0x21] = "Page Up",       [0x22] = "Page Down",
        [0x23] = "End",           [0x24] = "Home",
        [0x25] = "Left",          [0x26] = "Up",             [0x27] = "Right",         [0x28] = "Down",
        [0x2C] = "Print Screen",  [0x2D] = "Insert",         [0x2E] = "Delete",
        [0x5B] = "Left Win",      [0x5C] = "Right Win",      [0x5D] = "Menu",
        [0x6A] = "Num *",         [0x6B] = "Num +",          [0x6D] = "Num -",
        [0x6E] = "Num .",         [0x6F] = "Num /",
        [0x90] = "Num Lock",      [0x91] = "Scroll Lock",
        [0x10] = "Shift",         [0x11] = "Ctrl",           [0x12] = "Alt",
        [0xA0] = "Left Shift",    [0xA1] = "Right Shift",
        [0xA2] = "Left Ctrl",     [0xA3] = "Right Ctrl",
        [0xA4] = "Left Alt",      [0xA5] = "Right Alt",
        [0xBA] = ";",             [0xBB] = "=",              [0xBC] = ",",
        [0xBD] = "-",             [0xBE] = ".",              [0xBF] = "/",
        [0xC0] = "`",             [0xDB] = "[",              [0xDC] = "\\",
        [0xDD] = "]",             [0xDE] = "'",              [0xE2] = "\\ (102nd)",
        [0xAD] = "Volume Mute",   [0xAE] = "Volume Down",    [0xAF] = "Volume Up",
        [0xB0] = "Next Track",    [0xB1] = "Prev Track",     [0xB2] = "Media Stop",
        [0xB3] = "Play/Pause",
    }.ToFrozenDictionary();
}
