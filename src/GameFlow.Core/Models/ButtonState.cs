using GameFlow.Core.Enums;

namespace GameFlow.Core.Models;

/// <summary>
/// Helpers over the <see cref="ButtonId"/> set itself — the list of ids and
/// how many there are. The per-frame state they used to describe now lives
/// in <see cref="ButtonMask"/>.
/// </summary>
public static class ButtonState
{
    /// <summary>
    /// Cached once. <see cref="Enum.GetValues{T}()"/> is reflection-backed
    /// and returns a FRESH array on every call — this used to run twice
    /// per pipeline tick per slot, so at 1000 Hz across 16 slots it was
    /// ~32k reflection calls and 32k throwaway arrays per second, purely
    /// to enumerate a list that cannot change at runtime.
    /// </summary>
    private static readonly ButtonId[] AllButtons = Enum.GetValues<ButtonId>();

    /// <summary>
    /// Number of distinct <see cref="ButtonId"/> values, including
    /// <see cref="ButtonId.None"/>. This is the "how many buttons exist"
    /// denominator the diagnostics print; for "how many are down", read
    /// <see cref="ButtonMask.PressedCount"/>.
    /// </summary>
    public static int Count => AllButtons.Length;

    /// <summary>The full set of button ids, in declaration order. Treat as read-only.</summary>
    public static IReadOnlyList<ButtonId> All => AllButtons;

    /// <summary>
    /// An all-released mask.
    /// </summary>
    /// <remarks>
    /// Kept as a named factory rather than pushing <c>default</c> onto every
    /// caller because "no buttons pressed" is worth saying out loud, and
    /// because this is what the old dictionary-based API was called — the
    /// call sites read the same, they just no longer allocate.
    /// </remarks>
    public static ButtonMask CreateEmptyMap() => ButtonMask.Empty;

    /// <summary>
    /// A copy of <paramref name="source"/> that can be written to without
    /// affecting it.
    /// </summary>
    /// <remarks>
    /// A <see cref="ButtonMask"/> is a value type, so this is the identity
    /// function: assignment already copies. It stays because the pipeline
    /// reads as intended with it ("clone the physical buttons, then write
    /// the mapped result into the clone") and because deleting it would
    /// churn every call site to prove a point.
    /// </remarks>
    public static ButtonMask Clone(ButtonMask source) => source;
}
