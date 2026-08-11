namespace GameFlow.Infrastructure.Runtime.Input.Mac;

/// <summary>
/// Translates macOS "Carbon" virtual keycodes (the values CGEventTap
/// reports via kCGKeyboardEventKeycode) to Windows virtual-key codes —
/// same reasoning as EvdevKeyCodeMap on the Linux side:
/// <see cref="IKeyboardStateSource"/>'s contract is Windows VK codes, so
/// translating at the source keeps everything downstream OS-agnostic.
///
/// <para>
/// The source values are Apple's physical key-position constants from
/// <c>HIToolbox/Events.h</c> (<c>kVK_ANSI_*</c> and <c>kVK_*</c>).
/// That makes punctuation and keypad keys layout-independent here, just
/// like evdev scan codes on Linux. External-PC-keyboard F13/F14/F15 are
/// presented as Print Screen / Scroll Lock / Pause, the legends normally
/// occupying those physical positions on an ANSI 104-key board.
/// </para>
/// </summary>
internal static class MacKeyCodeMap
{
    public static readonly IReadOnlyDictionary<int, int> MacToVirtualKey = Build();

    private static Dictionary<int, int> Build()
    {
        var map = new Dictionary<int, int>();

        // Letters (kVK_ANSI_*) — Mac's own physical-key scancode order, not alphabetical.
        Add(map, 0x00, 0x41);  // A
        Add(map, 0x0B, 0x42);  // B
        Add(map, 0x08, 0x43);  // C
        Add(map, 0x02, 0x44);  // D
        Add(map, 0x0E, 0x45);  // E
        Add(map, 0x03, 0x46);  // F
        Add(map, 0x05, 0x47);  // G
        Add(map, 0x04, 0x48);  // H
        Add(map, 0x22, 0x49);  // I
        Add(map, 0x26, 0x4A);  // J
        Add(map, 0x28, 0x4B);  // K
        Add(map, 0x25, 0x4C);  // L
        Add(map, 0x2E, 0x4D);  // M
        Add(map, 0x2D, 0x4E);  // N
        Add(map, 0x1F, 0x4F);  // O
        Add(map, 0x23, 0x50);  // P
        Add(map, 0x0C, 0x51);  // Q
        Add(map, 0x0F, 0x52);  // R
        Add(map, 0x01, 0x53);  // S
        Add(map, 0x11, 0x54);  // T
        Add(map, 0x20, 0x55);  // U
        Add(map, 0x09, 0x56);  // V
        Add(map, 0x0D, 0x57);  // W
        Add(map, 0x07, 0x58);  // X
        Add(map, 0x10, 0x59);  // Y
        Add(map, 0x06, 0x5A);  // Z

        // Digits.
        Add(map, 0x12, 0x31); Add(map, 0x13, 0x32); Add(map, 0x14, 0x33); Add(map, 0x15, 0x34);
        Add(map, 0x17, 0x35); Add(map, 0x16, 0x36); Add(map, 0x1A, 0x37); Add(map, 0x1C, 0x38);
        Add(map, 0x19, 0x39); Add(map, 0x1D, 0x30);

        // ANSI punctuation (physical positions, not produced characters).
        Add(map, 0x18, 0xBB); // Equal
        Add(map, 0x1B, 0xBD); // Minus
        Add(map, 0x1E, 0xDD); // Right bracket
        Add(map, 0x21, 0xDB); // Left bracket
        Add(map, 0x27, 0xDE); // Quote
        Add(map, 0x29, 0xBA); // Semicolon
        Add(map, 0x2A, 0xDC); // Backslash
        Add(map, 0x2B, 0xBC); // Comma
        Add(map, 0x2C, 0xBF); // Slash
        Add(map, 0x2F, 0xBE); // Period
        Add(map, 0x32, 0xC0); // Grave

        // Editing / whitespace.
        Add(map, 0x24, 0x0D); // Return -> VK_RETURN
        Add(map, 0x30, 0x09); // Tab -> VK_TAB
        Add(map, 0x31, 0x20); // Space -> VK_SPACE
        Add(map, 0x33, 0x08); // Delete (Mac's "Backspace", left of Return) -> VK_BACK
        Add(map, 0x75, 0x2E); // Forward Delete (PC-style Delete) -> VK_DELETE
        Add(map, 0x35, 0x1B); // Escape -> VK_ESCAPE
        Add(map, 0x73, 0x24); // Home -> VK_HOME
        Add(map, 0x77, 0x23); // End -> VK_END
        Add(map, 0x74, 0x21); // Page Up -> VK_PRIOR
        Add(map, 0x79, 0x22); // Page Down -> VK_NEXT
        Add(map, 0x72, 0x2D); // Help / Insert position -> VK_INSERT

        // Function row.
        Add(map, 0x7A, 0x70); // F1
        Add(map, 0x78, 0x71); // F2
        Add(map, 0x63, 0x72); // F3
        Add(map, 0x76, 0x73); // F4
        Add(map, 0x60, 0x74); // F5
        Add(map, 0x61, 0x75); // F6
        Add(map, 0x62, 0x76); // F7
        Add(map, 0x64, 0x77); // F8
        Add(map, 0x65, 0x78); // F9
        Add(map, 0x6D, 0x79); // F10
        Add(map, 0x67, 0x7A); // F11
        Add(map, 0x6F, 0x7B); // F12
        Add(map, 0x69, 0x2C); // F13 / Print Screen position
        Add(map, 0x6B, 0x91); // F14 / Scroll Lock position
        Add(map, 0x71, 0x13); // F15 / Pause position

        // Arrows.
        Add(map, 0x7B, 0x25); // Left
        Add(map, 0x7C, 0x27); // Right
        Add(map, 0x7D, 0x28); // Down
        Add(map, 0x7E, 0x26); // Up

        // Modifiers — left/right distinct where the Mac keycode space distinguishes them.
        Add(map, 0x38, 0xA0); // Shift -> VK_LSHIFT
        Add(map, 0x3C, 0xA1); // Right Shift -> VK_RSHIFT
        Add(map, 0x3B, 0xA2); // Control -> VK_LCONTROL
        Add(map, 0x3E, 0xA3); // Right Control -> VK_RCONTROL
        Add(map, 0x3A, 0xA4); // Option -> VK_LMENU
        Add(map, 0x3D, 0xA5); // Right Option -> VK_RMENU
        Add(map, 0x37, 0x5B); // Command -> VK_LWIN (closest semantic equivalent)
        Add(map, 0x36, 0x5C); // Right Command -> VK_RWIN
        Add(map, 0x39, 0x14); // Caps Lock -> VK_CAPITAL

        // Numeric keypad. Keypad Clear occupies Num Lock's position on an
        // Apple extended keyboard, so it drives the same physical preview.
        Add(map, 0x47, 0x90); // Keypad Clear -> VK_NUMLOCK
        Add(map, 0x52, 0x60); Add(map, 0x53, 0x61); Add(map, 0x54, 0x62); Add(map, 0x55, 0x63);
        Add(map, 0x56, 0x64); Add(map, 0x57, 0x65); Add(map, 0x58, 0x66);
        Add(map, 0x59, 0x67); Add(map, 0x5B, 0x68); Add(map, 0x5C, 0x69);
        Add(map, 0x43, 0x6A); // Keypad multiply
        Add(map, 0x45, 0x6B); // Keypad plus
        Add(map, 0x4E, 0x6D); // Keypad minus
        Add(map, 0x41, 0x6E); // Keypad decimal
        Add(map, 0x4B, 0x6F); // Keypad divide
        Add(map, 0x4C, KeyboardVirtualKeys.NumpadEnter);

        // Dedicated media keys exposed by CGEventTap.
        Add(map, 0x4A, 0xAD); // Mute
        Add(map, 0x49, 0xAE); // Volume down
        Add(map, 0x48, 0xAF); // Volume up

        return map;
    }

    private static void Add(Dictionary<int, int> map, int macKeycode, int virtualKey) => map[macKeycode] = virtualKey;
}
