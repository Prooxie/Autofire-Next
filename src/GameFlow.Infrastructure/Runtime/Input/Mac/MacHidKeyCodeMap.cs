namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// Translates HID Keyboard/Keypad usages (usage page 0x07) to Windows
/// virtual-key codes — the macOS counterpart to
/// <see cref="Linux.EvdevKeyCodeMap"/>, and the replacement for the
/// Carbon <c>kVK_*</c> table the CGEventTap reader used.
///
/// <para>
/// <b>Why the source numbering changed.</b> CGEventTap reported Carbon
/// virtual keycodes; IOHIDManager reports raw HID usages, which are a
/// different numbering entirely (Carbon <c>kVK_ANSI_A</c> is 0x00, HID
/// "Keyboard a" is 0x04). Nothing downstream notices, because both
/// tables end at the same place: <see cref="IKeyboardStateSource"/>'s
/// contract is Windows VK codes on every platform, so the translation
/// happens here at the source exactly as it does on Linux.
/// </para>
///
/// <para>
/// HID usages are physical-key identifiers, not characters — usage 0x14
/// is "the key in the Q position" whatever the active layout prints on
/// it. That is the same property evdev codes have, so the US-QWERTY
/// assumption baked into the <c>VK_OEM_*</c> punctuation entries is the
/// standard one for OEM codes generally, not something this table
/// invents.
/// </para>
///
/// <para>
/// <b>Better than the Carbon table it replaces</b> in one specific way:
/// Carbon has no Print Screen / Scroll Lock / Pause keycodes, so a PC
/// keyboard's three keys arrived as F13/F14/F15 and the old map guessed
/// at the legends by physical position. HID has real usages for all six,
/// so both a PC keyboard's Print Screen (0x46) and an Apple keyboard's
/// F13 (0x68) now report what they actually are.
/// </para>
///
/// <para>
/// <b>Known gap:</b> the volume/mute keys the old table carried are not
/// here. They are not keyboard-page usages at all — they live on the
/// Consumer page (0x0C), which is a separate HID device that this
/// reader's matching dictionaries deliberately do not claim. Mapping a
/// volume key to a gamepad button is therefore no longer possible on
/// macOS; Linux has never offered it either.
/// </para>
///
/// <para>
/// Not exhaustive — letters, digits, function keys, navigation,
/// modifiers (left/right kept distinct, which HID does natively),
/// editing keys, the keypad and US punctuation. A usage with no entry
/// is simply never reported as pressed, matching how
/// <see cref="Linux.EvdevKeyCodeMap"/> treats its own gaps.
/// </para>
/// </summary>
internal static class MacHidKeyCodeMap
{
    public static readonly IReadOnlyDictionary<int, int> HidUsageToVirtualKey = Build();

    /// <summary>
    /// Usages below this are not keys: 0x00 is reserved and 0x01-0x03 are
    /// the roll-over / POST-fail / undefined error codes a keyboard emits
    /// when it cannot report the real state. Nothing here maps them, so
    /// they fall out on lookup, but the boundary is named because an
    /// error code arriving as a phantom keypress is the failure mode.
    /// </summary>
    public const int FirstRealKeyUsage = 0x04;

    private static Dictionary<int, int> Build()
    {
        var map = new Dictionary<int, int>();

        // Letters: HID 0x04..0x1D is a..z alphabetically, and VK_A..VK_Z
        // is 0x41..0x5A. Both runs are contiguous and aligned, so the
        // loop states that fact rather than risking a typo in 26 lines.
        for (var i = 0; i < 26; i++)
        {
            Add(map, 0x04 + i, 0x41 + i);
        }

        // Top-row digits: HID runs 1..9 then 0 (0x1E..0x27); VK runs
        // VK_1..VK_9 (0x31..0x39) then VK_0 (0x30). Same shape, so the
        // wrap-around is the only entry written out.
        for (var i = 0; i < 9; i++)
        {
            Add(map, 0x1E + i, 0x31 + i);
        }
        Add(map, 0x27, 0x30);

        // Function keys: F1..F12 = 0x3A..0x45 -> VK_F1..VK_F12.
        for (var i = 0; i < 12; i++)
        {
            Add(map, 0x3A + i, 0x70 + i);
        }

        // F13..F15 = 0x68..0x6A -> VK_F13..VK_F15. These are the keys
        // above the Apple extended keyboard's numpad. The Carbon table
        // reported them as Print Screen / Scroll Lock / Pause because it
        // had no other way to see those PC keys; HID does, below.
        for (var i = 0; i < 3; i++)
        {
            Add(map, 0x68 + i, 0x7C + i);
        }

        // Editing / whitespace.
        Add(map, 0x28, 0x0D); // Return -> VK_RETURN
        Add(map, 0x29, 0x1B); // Escape -> VK_ESCAPE
        Add(map, 0x2A, 0x08); // Backspace (Mac's "delete", left of Return) -> VK_BACK
        Add(map, 0x2B, 0x09); // Tab -> VK_TAB
        Add(map, 0x2C, 0x20); // Space -> VK_SPACE
        Add(map, 0x39, 0x14); // Caps Lock -> VK_CAPITAL
        Add(map, 0x49, 0x2D); // Insert -> VK_INSERT
        Add(map, 0x4A, 0x24); // Home -> VK_HOME
        Add(map, 0x4B, 0x21); // Page Up -> VK_PRIOR
        Add(map, 0x4C, 0x2E); // Delete Forward (PC-style Delete) -> VK_DELETE
        Add(map, 0x4D, 0x23); // End -> VK_END
        Add(map, 0x4E, 0x22); // Page Down -> VK_NEXT
        Add(map, 0x65, 0x5D); // Application (menu key position) -> VK_APPS

        // The three PC keys Carbon could not name.
        Add(map, 0x46, 0x2C); // Print Screen -> VK_SNAPSHOT
        Add(map, 0x47, 0x91); // Scroll Lock -> VK_SCROLL
        Add(map, 0x48, 0x13); // Pause -> VK_PAUSE

        // Arrows.
        Add(map, 0x4F, 0x27); // Right -> VK_RIGHT
        Add(map, 0x50, 0x25); // Left -> VK_LEFT
        Add(map, 0x51, 0x28); // Down -> VK_DOWN
        Add(map, 0x52, 0x26); // Up -> VK_UP

        // Modifiers. HID splits left/right at the usage level, so no
        // guessing is needed — 0xE0..0xE7 in order. Mac's Command is the
        // GUI usage, mapped to VK_LWIN/VK_RWIN as the closest equivalent
        // (same choice the Carbon table made).
        Add(map, 0xE0, 0xA2); // Left Control -> VK_LCONTROL
        Add(map, 0xE1, 0xA0); // Left Shift -> VK_LSHIFT
        Add(map, 0xE2, 0xA4); // Left Alt / Option -> VK_LMENU
        Add(map, 0xE3, 0x5B); // Left GUI / Command -> VK_LWIN
        Add(map, 0xE4, 0xA3); // Right Control -> VK_RCONTROL
        Add(map, 0xE5, 0xA1); // Right Shift -> VK_RSHIFT
        Add(map, 0xE6, 0xA5); // Right Alt / Option -> VK_RMENU
        Add(map, 0xE7, 0x5C); // Right GUI / Command -> VK_RWIN

        // Standard US-layout punctuation (physical positions on both sides).
        Add(map, 0x2D, 0xBD); // - and _ -> VK_OEM_MINUS
        Add(map, 0x2E, 0xBB); // = and + -> VK_OEM_PLUS
        Add(map, 0x2F, 0xDB); // [ and { -> VK_OEM_4
        Add(map, 0x30, 0xDD); // ] and } -> VK_OEM_6
        Add(map, 0x31, 0xDC); // \ and | -> VK_OEM_5
        Add(map, 0x33, 0xBA); // ; and : -> VK_OEM_1
        Add(map, 0x34, 0xDE); // ' and " -> VK_OEM_7
        Add(map, 0x35, 0xC0); // ` and ~ -> VK_OEM_3
        Add(map, 0x36, 0xBC); // , and < -> VK_OEM_COMMA
        Add(map, 0x37, 0xBE); // . and > -> VK_OEM_PERIOD
        Add(map, 0x38, 0xBF); // / and ? -> VK_OEM_2
        Add(map, 0x64, 0xE2); // Non-US \ and | (the ISO 102nd key) -> VK_OEM_102

        // Usage 0x32 (Non-US # and ~) is deliberately absent: on the
        // layouts where it exists it sits where VK_OEM_5 already is, and
        // two usages sharing one VK would light two keys in the preview
        // from one press.

        // Keypad: HID 0x59..0x61 is KP1..KP9 against VK_NUMPAD1..9
        // (0x61..0x69), with KP0 wrapping afterwards — the same shape as
        // the top-row digits.
        for (var i = 0; i < 9; i++)
        {
            Add(map, 0x59 + i, 0x61 + i);
        }
        Add(map, 0x62, 0x60); // Keypad 0 -> VK_NUMPAD0

        Add(map, 0x53, 0x90); // Keypad Num Lock / Clear -> VK_NUMLOCK
        Add(map, 0x54, 0x6F); // Keypad / -> VK_DIVIDE
        Add(map, 0x55, 0x6A); // Keypad * -> VK_MULTIPLY
        Add(map, 0x56, 0x6D); // Keypad - -> VK_SUBTRACT
        Add(map, 0x57, 0x6B); // Keypad + -> VK_ADD
        Add(map, 0x63, 0x6E); // Keypad . -> VK_DECIMAL
        Add(map, 0x58, KeyboardVirtualKeys.NumpadEnter); // Keypad Enter -> extended keypad Enter identifier

        return map;
    }

    private static void Add(Dictionary<int, int> map, int hidUsage, int virtualKey) => map[hidUsage] = virtualKey;
}
