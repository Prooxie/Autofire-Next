namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Canonical key identifiers that extend the Win32 virtual-key range.
/// </summary>
public static class KeyboardVirtualKeys
{
    /// <summary>
    /// The numeric keypad Enter key. Win32 reports both Enter keys as
    /// <c>VK_RETURN</c>; raw input's extended-key flag lets GameFlow retain
    /// the physical distinction without colliding with a real Win32 VK.
    /// </summary>
    public const int NumpadEnter = 0x10D;
}

/// <summary>One key in the fixed ANSI 104-key preview.</summary>
public sealed record PhysicalKeyboardKey(
    int VirtualKey,
    string Label,
    string? Hint,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// Canonical geometry and legends for a full-size ANSI PC keyboard.
/// Coordinates are deliberately independent of Avalonia so the physical
/// layout and its key identifiers can be regression-tested without a UI
/// runtime. The view scales this fixed surface uniformly when space is tight.
/// </summary>
public static class KeyboardPhysicalLayout
{
    public const int Ansi104KeyCount = 104;
    public const double SurfaceWidth = 770;
    public const double SurfaceHeight = 208;
    public const double KeyWidth = 30;
    public const double KeyHeight = 30;

    public static IReadOnlyList<PhysicalKeyboardKey> Ansi104 { get; } = BuildAnsi104();

    private static PhysicalKeyboardKey[] BuildAnsi104()
    {
        var keys = new List<PhysicalKeyboardKey>(Ansi104KeyCount);

        void Add(
            int virtualKey,
            string label,
            double x,
            double y,
            double width = KeyWidth,
            double height = KeyHeight,
            string? hint = null) =>
            keys.Add(new PhysicalKeyboardKey(virtualKey, label, hint, x, y, width, height));

        // Function row. The spaces after Escape, F4 and F8 mirror the
        // groupings on a conventional full-size keyboard.
        Add(0x1B, "Esc", 0, 0);
        double[] functionX = [68, 102, 136, 170, 221, 255, 289, 323, 374, 408, 442, 476];
        for (var index = 0; index < functionX.Length; index++)
        {
            Add(0x70 + index, $"F{index + 1}", functionX[index], 0);
        }

        // Main typing block: 14 / 14 / 13 / 12 / 8 keys by row.
        (int Vk, string Label, string Hint)[] numberRow =
        [
            (0xC0, "`", "~"),
            (0x31, "1", "!"), (0x32, "2", "@"), (0x33, "3", "#"),
            (0x34, "4", "$"), (0x35, "5", "%"), (0x36, "6", "^"),
            (0x37, "7", "&"), (0x38, "8", "*"), (0x39, "9", "("),
            (0x30, "0", ")"), (0xBD, "-", "_"), (0xBB, "=", "+")
        ];
        for (var index = 0; index < numberRow.Length; index++)
        {
            var key = numberRow[index];
            Add(key.Vk, key.Label, index * 34, 42, hint: key.Hint);
        }
        Add(0x08, "Backspace", 442, 42, width: 64);

        Add(0x09, "Tab", 0, 76, width: 47);
        (int Vk, string Label, string? Hint)[] qwertyRow =
        [
            (0x51, "Q", null), (0x57, "W", null), (0x45, "E", null),
            (0x52, "R", null), (0x54, "T", null), (0x59, "Y", null),
            (0x55, "U", null), (0x49, "I", null), (0x4F, "O", null),
            (0x50, "P", null), (0xDB, "[", "{"), (0xDD, "]", "}"),
            (0xDC, "\\", "|")
        ];
        for (var index = 0; index < qwertyRow.Length - 1; index++)
        {
            var key = qwertyRow[index];
            Add(key.Vk, key.Label, 51 + (index * 34), 76, hint: key.Hint);
        }
        var backslash = qwertyRow[^1];
        Add(backslash.Vk, backslash.Label, 459, 76, width: 47, hint: backslash.Hint);

        Add(0x14, "Caps", 0, 110, width: 56, hint: "Lock");
        (int Vk, string Label, string? Hint)[] homeRow =
        [
            (0x41, "A", null), (0x53, "S", null), (0x44, "D", null),
            (0x46, "F", null), (0x47, "G", null), (0x48, "H", null),
            (0x4A, "J", null), (0x4B, "K", null), (0x4C, "L", null),
            (0xBA, ";", ":"), (0xDE, "'", "\"")
        ];
        for (var index = 0; index < homeRow.Length; index++)
        {
            var key = homeRow[index];
            Add(key.Vk, key.Label, 60 + (index * 34), 110, hint: key.Hint);
        }
        Add(0x0D, "Enter", 434, 110, width: 72);

        Add(0xA0, "L Shift", 0, 144, width: 72);
        (int Vk, string Label, string? Hint)[] lowerRow =
        [
            (0x5A, "Z", null), (0x58, "X", null), (0x43, "C", null),
            (0x56, "V", null), (0x42, "B", null), (0x4E, "N", null),
            (0x4D, "M", null), (0xBC, ",", "<"), (0xBE, ".", ">"),
            (0xBF, "/", "?")
        ];
        for (var index = 0; index < lowerRow.Length; index++)
        {
            var key = lowerRow[index];
            Add(key.Vk, key.Label, 76 + (index * 34), 144, hint: key.Hint);
        }
        Add(0xA1, "R Shift", 416, 144, width: 90);

        Add(0xA2, "Ctrl", 0, 178, width: 38, hint: "L");
        Add(0x5B, "Win", 42, 178, width: 38, hint: "L");
        Add(0xA4, "Alt", 84, 178, width: 38, hint: "L");
        Add(0x20, "Space", 126, 178, width: 208);
        Add(0xA5, "Alt", 338, 178, width: 38, hint: "R");
        Add(0x5C, "Win", 380, 178, width: 38, hint: "R");
        Add(0x5D, "Menu", 422, 178, width: 38);
        Add(0xA3, "Ctrl", 464, 178, width: 42, hint: "R");

        // Navigation cluster. Editing keys occupy the conventional 2x3
        // block; the arrow T sits below it instead of being interleaved.
        Add(0x2C, "Prt", 522, 0, hint: "Scn");
        Add(0x91, "Scr", 556, 0, hint: "Lock");
        Add(0x13, "Pause", 590, 0);

        Add(0x2D, "Ins", 522, 42);
        Add(0x24, "Home", 556, 42);
        Add(0x21, "Pg", 590, 42, hint: "Up");
        Add(0x2E, "Del", 522, 76);
        Add(0x23, "End", 556, 76);
        Add(0x22, "Pg", 590, 76, hint: "Down");

        Add(0x26, "↑", 556, 144);
        Add(0x25, "←", 522, 178);
        Add(0x28, "↓", 556, 178);
        Add(0x27, "→", 590, 178);

        // Numeric keypad. Plus and keypad Enter span two rows; zero spans
        // two columns, matching a standard 104-key ANSI board.
        Add(0x90, "Num", 638, 42, hint: "Lock");
        Add(0x6F, "/", 672, 42);
        Add(0x6A, "*", 706, 42);
        Add(0x6D, "-", 740, 42);

        Add(0x67, "7", 638, 76, hint: "Home");
        Add(0x68, "8", 672, 76, hint: "↑");
        Add(0x69, "9", 706, 76, hint: "PgUp");
        Add(0x6B, "+", 740, 76, height: 64);
        Add(0x64, "4", 638, 110, hint: "←");
        Add(0x65, "5", 672, 110);
        Add(0x66, "6", 706, 110, hint: "→");

        Add(0x61, "1", 638, 144, hint: "End");
        Add(0x62, "2", 672, 144, hint: "↓");
        Add(0x63, "3", 706, 144, hint: "PgDn");
        Add(KeyboardVirtualKeys.NumpadEnter, "Enter", 740, 144, height: 64, hint: "Num");
        Add(0x60, "0", 638, 178, width: 64, hint: "Ins");
        Add(0x6E, ".", 706, 178, hint: "Del");

        return keys.ToArray();
    }
}
