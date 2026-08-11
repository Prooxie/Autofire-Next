namespace GameFlow.Infrastructure.Runtime.Input.Linux;

/// <summary>
/// Translates evdev KEY_* codes to Windows virtual-key codes.
///
/// <para>
/// <see cref="IKeyboardStateSource"/>'s contract is explicitly
/// "Windows virtual-key codes" — everything downstream (ButtonMapStore,
/// the mapping UI) already speaks that numbering. Rather than widen
/// that contract to a second numbering scheme across the whole app,
/// this reader translates at the source, so Linux is a drop-in: nothing
/// downstream of <see cref="IKeyboardStateSource"/>/<see cref="IMouseStateSource"/>
/// needs to know which OS produced the codes.
/// </para>
///
/// <para>
/// evdev codes are physical-key scancodes (KEY_A=30, KEY_B=48 — desk
/// layout order, not alphabetical), verified against this machine's own
/// <c>/usr/include/linux/input-event-codes.h</c> rather than recalled
/// from memory. Windows VK_* values are the long-stable Win32 constants
/// (VK_A=0x41.._VK_Z=0x5A follow ASCII directly; VK_F1=0x70 etc.).
/// Punctuation (VK_OEM_*) is inherently a physical-position mapping on
/// BOTH sides already — this assumes a US QWERTY physical layout, which
/// is the standard assumption for OEM_* codes generally, not something
/// specific to this table.
/// </para>
///
/// <para>Not exhaustive — covers letters, digits, function keys,
/// navigation, modifiers (left/right where Windows distinguishes them),
/// common editing keys, numpad, and standard US punctuation. An
/// evdev code with no entry here is simply not reported as pressed;
/// that's a silent gap for now rather than a crash, matching how
/// <see cref="NullKeyboardStateSource"/> already behaves when nothing
/// is wired at all.</para>
/// </summary>
internal static class EvdevKeyCodeMap
{
    public static readonly IReadOnlyDictionary<ushort, int> EvdevToVirtualKey = Build();

    private static Dictionary<ushort, int> Build()
    {
        var map = new Dictionary<ushort, int>();

        // Letters — evdev physical-scancode order; VK_A..VK_Z = 0x41..0x5A.
        Add(map, 30, 0x41);  // KEY_A -> VK_A
        Add(map, 48, 0x42);  // KEY_B -> VK_B
        Add(map, 46, 0x43);  // KEY_C -> VK_C
        Add(map, 32, 0x44);  // KEY_D -> VK_D
        Add(map, 18, 0x45);  // KEY_E -> VK_E
        Add(map, 33, 0x46);  // KEY_F -> VK_F
        Add(map, 34, 0x47);  // KEY_G -> VK_G
        Add(map, 35, 0x48);  // KEY_H -> VK_H
        Add(map, 23, 0x49);  // KEY_I -> VK_I
        Add(map, 36, 0x4A);  // KEY_J -> VK_J
        Add(map, 37, 0x4B);  // KEY_K -> VK_K
        Add(map, 38, 0x4C);  // KEY_L -> VK_L
        Add(map, 50, 0x4D);  // KEY_M -> VK_M
        Add(map, 49, 0x4E);  // KEY_N -> VK_N
        Add(map, 24, 0x4F);  // KEY_O -> VK_O
        Add(map, 25, 0x50);  // KEY_P -> VK_P
        Add(map, 16, 0x51);  // KEY_Q -> VK_Q
        Add(map, 19, 0x52);  // KEY_R -> VK_R
        Add(map, 31, 0x53);  // KEY_S -> VK_S
        Add(map, 20, 0x54);  // KEY_T -> VK_T
        Add(map, 22, 0x55);  // KEY_U -> VK_U
        Add(map, 47, 0x56);  // KEY_V -> VK_V
        Add(map, 17, 0x57);  // KEY_W -> VK_W
        Add(map, 45, 0x58);  // KEY_X -> VK_X
        Add(map, 21, 0x59);  // KEY_Y -> VK_Y
        Add(map, 44, 0x5A);  // KEY_Z -> VK_Z

        // Top-row digits — VK_0..VK_9 = 0x30..0x39.
        Add(map, 2, 0x31); Add(map, 3, 0x32); Add(map, 4, 0x33); Add(map, 5, 0x34); Add(map, 6, 0x35);
        Add(map, 7, 0x36); Add(map, 8, 0x37); Add(map, 9, 0x38); Add(map, 10, 0x39); Add(map, 11, 0x30);

        // Function keys.
        Add(map, 59, 0x70); Add(map, 60, 0x71); Add(map, 61, 0x72); Add(map, 62, 0x73);
        Add(map, 63, 0x74); Add(map, 64, 0x75); Add(map, 65, 0x76); Add(map, 66, 0x77);
        Add(map, 67, 0x78); Add(map, 68, 0x79); Add(map, 87, 0x7A); Add(map, 88, 0x7B);

        // Editing / whitespace.
        Add(map, 1, 0x1B);   // KEY_ESC -> VK_ESCAPE
        Add(map, 14, 0x08);  // KEY_BACKSPACE -> VK_BACK
        Add(map, 15, 0x09);  // KEY_TAB -> VK_TAB
        Add(map, 28, 0x0D);  // KEY_ENTER -> VK_RETURN
        Add(map, 57, 0x20);  // KEY_SPACE -> VK_SPACE
        Add(map, 58, 0x14);  // KEY_CAPSLOCK -> VK_CAPITAL
        Add(map, 110, 0x2D); // KEY_INSERT -> VK_INSERT
        Add(map, 111, 0x2E); // KEY_DELETE -> VK_DELETE
        Add(map, 102, 0x24); // KEY_HOME -> VK_HOME
        Add(map, 107, 0x23); // KEY_END -> VK_END
        Add(map, 104, 0x21); // KEY_PAGEUP -> VK_PRIOR
        Add(map, 109, 0x22); // KEY_PAGEDOWN -> VK_NEXT
        Add(map, 119, 0x13); // KEY_PAUSE -> VK_PAUSE
        Add(map, 70, 0x91);  // KEY_SCROLLLOCK -> VK_SCROLL
        Add(map, 69, 0x90);  // KEY_NUMLOCK -> VK_NUMLOCK
        Add(map, 99, 0x2C);  // KEY_SYSRQ -> VK_SNAPSHOT

        // Arrows.
        Add(map, 103, 0x26); // KEY_UP -> VK_UP
        Add(map, 108, 0x28); // KEY_DOWN -> VK_DOWN
        Add(map, 105, 0x25); // KEY_LEFT -> VK_LEFT
        Add(map, 106, 0x27); // KEY_RIGHT -> VK_RIGHT

        // Modifiers — left/right distinct, matching evdev's own split.
        Add(map, 42, 0xA0);  // KEY_LEFTSHIFT -> VK_LSHIFT
        Add(map, 54, 0xA1);  // KEY_RIGHTSHIFT -> VK_RSHIFT
        Add(map, 29, 0xA2);  // KEY_LEFTCTRL -> VK_LCONTROL
        Add(map, 97, 0xA3);  // KEY_RIGHTCTRL -> VK_RCONTROL
        Add(map, 56, 0xA4);  // KEY_LEFTALT -> VK_LMENU
        Add(map, 100, 0xA5); // KEY_RIGHTALT -> VK_RMENU
        Add(map, 125, 0x5B); // KEY_LEFTMETA -> VK_LWIN
        Add(map, 126, 0x5C); // KEY_RIGHTMETA -> VK_RWIN
        Add(map, 127, 0x5D); // KEY_COMPOSE -> VK_APPS (menu key position on PC boards)

        // Standard US-layout punctuation (physical-position codes on both sides).
        Add(map, 12, 0xBD);  // KEY_MINUS -> VK_OEM_MINUS
        Add(map, 13, 0xBB);  // KEY_EQUAL -> VK_OEM_PLUS
        Add(map, 26, 0xDB);  // KEY_LEFTBRACE -> VK_OEM_4
        Add(map, 27, 0xDD);  // KEY_RIGHTBRACE -> VK_OEM_6
        Add(map, 39, 0xBA);  // KEY_SEMICOLON -> VK_OEM_1
        Add(map, 40, 0xDE);  // KEY_APOSTROPHE -> VK_OEM_7
        Add(map, 41, 0xC0);  // KEY_GRAVE -> VK_OEM_3
        Add(map, 43, 0xDC);  // KEY_BACKSLASH -> VK_OEM_5
        Add(map, 51, 0xBC);  // KEY_COMMA -> VK_OEM_COMMA
        Add(map, 52, 0xBE);  // KEY_DOT -> VK_OEM_PERIOD
        Add(map, 53, 0xBF);  // KEY_SLASH -> VK_OEM_2

        // Numpad.
        Add(map, 82, 0x60); Add(map, 79, 0x61); Add(map, 80, 0x62); Add(map, 81, 0x63);
        Add(map, 75, 0x64); Add(map, 76, 0x65); Add(map, 77, 0x66);
        Add(map, 71, 0x67); Add(map, 72, 0x68); Add(map, 73, 0x69);
        Add(map, 55, 0x6A); // KEY_KPASTERISK -> VK_MULTIPLY
        Add(map, 78, 0x6B); // KEY_KPPLUS -> VK_ADD
        Add(map, 74, 0x6D); // KEY_KPMINUS -> VK_SUBTRACT
        Add(map, 83, 0x6E); // KEY_KPDOT -> VK_DECIMAL
        Add(map, 98, 0x6F); // KEY_KPSLASH -> VK_DIVIDE
        Add(map, 96, KeyboardVirtualKeys.NumpadEnter); // KEY_KPENTER -> extended keypad Enter identifier

        return map;
    }

    private static void Add(Dictionary<ushort, int> map, int evdevCode, int virtualKey) =>
        map[(ushort)evdevCode] = virtualKey;
}
