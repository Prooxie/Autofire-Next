namespace GameFlow.Infrastructure.Runtime.Input;

/// <summary>
/// Per-keyboard-device source of currently-pressed canonical key codes.
/// Standard keys use their Windows virtual-key values on every platform;
/// physical distinctions Win32's VK range loses (currently keypad Enter)
/// use <see cref="KeyboardVirtualKeys"/> extensions. A
/// <see cref="NullKeyboardStateSource"/> returns an empty set when no live
/// platform reader is available.
/// </summary>
public interface IKeyboardStateSource
{
    /// <summary>Currently-pressed virtual-key codes for the given device id.</summary>
    IReadOnlySet<int> GetPressedKeys(string deviceId);

    /// <summary>
    /// Keys currently down across EVERY tracked keyboard. Windows often
    /// exposes one physical keyboard as several Raw Input handles
    /// (composite HID), so the id the user assigned isn't necessarily
    /// the one keystrokes arrive under — consumers use the per-device
    /// read first and fall back to this union when it's empty.
    /// </summary>
    IReadOnlySet<int> GetPressedKeysAggregate();
}

public static class KeyboardStateSourceExtensions
{
    /// <summary>
    /// Returns the selected device's keys when that device reports any;
    /// otherwise falls back to the aggregate state. Composite keyboards can
    /// expose their catalog identity and key events through different native
    /// handles, and macOS has only an aggregate event stream.
    /// </summary>
    public static IReadOnlySet<int> GetPressedKeysWithAggregateFallback(
        this IKeyboardStateSource source,
        string deviceId)
    {
        ArgumentNullException.ThrowIfNull(source);

        var pressed = source.GetPressedKeys(deviceId);
        return pressed.Count == 0
            ? source.GetPressedKeysAggregate()
            : pressed;
    }
}

/// <summary>No-op default: every keyboard reports an empty key set.</summary>
public sealed class NullKeyboardStateSource : IKeyboardStateSource
{
    private static readonly IReadOnlySet<int> Empty = new HashSet<int>();
    public IReadOnlySet<int> GetPressedKeys(string deviceId) => Empty;
    public IReadOnlySet<int> GetPressedKeysAggregate() => Empty;
}
