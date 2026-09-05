using System.Numerics;
using GameFlow.Core.Enums;

namespace GameFlow.Core.Models;

/// <summary>
/// The pressed/released state of every <see cref="ButtonId"/>, as one bit
/// each.
///
/// <para>
/// This replaces the <c>IReadOnlyDictionary&lt;ButtonId, bool&gt;</c> that
/// <see cref="ControllerSnapshot"/> used to carry. That dictionary was
/// rebuilt on every frame of every slot — the mapping pipeline clones the
/// physical button map before it starts writing to it — so at 1000 Hz
/// across sixteen slots it was sixteen thousand ~600-byte allocations a
/// second, plus a hash lookup for every single button read, to hold
/// twenty-four booleans. A struct over a <see cref="uint"/> holds the same
/// state in four bytes with no allocation at all, and turns the common
/// whole-map operations (merge, compare, "did anything change?") into one
/// machine instruction each.
/// </para>
///
/// <para>
/// <b>Value semantics.</b> Copying a <see cref="ButtonMask"/> copies the
/// state, where copying the old dictionary shared it. That is the safer
/// direction — a snapshot handed to the UI thread can no longer be mutated
/// behind its back by whoever built it — but it does mean writing to a
/// local after building a snapshot from it no longer changes that
/// snapshot. Every caller in the tree builds the mask first and the
/// snapshot second, so none relied on the old aliasing.
/// </para>
///
/// <para>
/// Bit <c>n</c> is the <see cref="ButtonId"/> whose underlying value is
/// <c>n</c>, including <see cref="ButtonId.None"/> at bit 0. <c>None</c>
/// is carried rather than rejected so the mask behaves exactly as the
/// dictionary did, which also kept a <c>None</c> entry.
/// </para>
/// </summary>
public struct ButtonMask : IEquatable<ButtonMask>
{
    /// <summary>
    /// Guards the one assumption this type makes: that every
    /// <see cref="ButtonId"/> fits in a <see cref="uint"/>.
    ///
    /// <para>
    /// Adding a 33rd button would otherwise silently drop it — the shift
    /// would wrap and alias onto an existing button, which reads as
    /// "pressing Misc1 also presses South" and is exactly the kind of bug
    /// that survives a release. Failing at type-initialisation instead
    /// makes it impossible to miss, and
    /// <c>ButtonMaskTests.Every_button_id_fits_in_the_mask</c> catches it
    /// in CI before anyone runs the app.
    /// </para>
    /// </summary>
    static ButtonMask()
    {
        var count = Enum.GetValues<ButtonId>().Length;
        if (count > 32)
        {
            throw new InvalidOperationException(
                $"ButtonMask stores one bit per ButtonId in a uint, but ButtonId now has {count} " +
                "members. Widen the backing field to ulong (and ButtonState.Count with it) before " +
                "adding more buttons.");
        }
    }

    private uint bits;

    private ButtonMask(uint bits) => this.bits = bits;

    /// <summary>Nothing pressed. Same as <c>default</c>.</summary>
    public static ButtonMask Empty => default;

    /// <summary>
    /// Reads or writes one button. <see cref="ButtonId"/> values outside
    /// the backing field are ignored on write and read as released, which
    /// cannot happen while the static guard above holds.
    /// </summary>
    public bool this[ButtonId button]
    {
        readonly get => (bits & Bit(button)) != 0;
        set
        {
            if (value) { bits |= Bit(button); }
            else { bits &= ~Bit(button); }
        }
    }

    /// <summary>How many buttons are currently down.</summary>
    public readonly int PressedCount => BitOperations.PopCount(bits);

    /// <summary>True when no button is down.</summary>
    public readonly bool IsEmpty => bits == 0;

    /// <summary>
    /// The union of two masks — a button is down if it is down in either.
    /// This is what merging several physical devices into one slot does,
    /// and it replaces a nested loop over two dictionaries.
    /// </summary>
    public static ButtonMask operator |(ButtonMask left, ButtonMask right) =>
        new(left.bits | right.bits);

    /// <summary>Buttons whose state differs between the two masks — the "what changed this tick" set.</summary>
    public static ButtonMask operator ^(ButtonMask left, ButtonMask right) =>
        new(left.bits ^ right.bits);

    /// <summary>Buttons down in <paramref name="left"/> and also in <paramref name="right"/>.</summary>
    public static ButtonMask operator &(ButtonMask left, ButtonMask right) =>
        new(left.bits & right.bits);

    public static bool operator ==(ButtonMask left, ButtonMask right) => left.bits == right.bits;

    public static bool operator !=(ButtonMask left, ButtonMask right) => left.bits != right.bits;

    public readonly bool Equals(ButtonMask other) => bits == other.bits;

    public override readonly bool Equals(object? obj) => obj is ButtonMask other && Equals(other);

    public override readonly int GetHashCode() => (int)bits;

    /// <summary>Comma-separated list of the buttons that are down, or <c>"(none)"</c>. For logs and test failures.</summary>
    public override readonly string ToString()
    {
        if (bits == 0)
        {
            return "(none)";
        }

        var names = new List<string>(PressedCount);
        foreach (var button in this)
        {
            names.Add(button.ToString());
        }
        return string.Join(", ", names);
    }

    /// <summary>
    /// Enumerates the buttons that are DOWN, ascending. Deliberately not an
    /// <see cref="IEnumerable{T}"/>: <c>foreach</c> binds to this method
    /// directly and the struct enumerator never boxes, which matters
    /// because snapshot merging walks this on the tick path.
    /// </summary>
    public readonly Enumerator GetEnumerator() => new(bits);

    /// <summary>Struct enumerator over the pressed buttons; see <see cref="GetEnumerator"/>.</summary>
    public struct Enumerator(uint bits)
    {
        private uint remaining = bits;

        public ButtonId Current { get; private set; } = ButtonId.None;

        public bool MoveNext()
        {
            if (remaining == 0)
            {
                return false;
            }

            var index = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1; // clear the lowest set bit
            Current = (ButtonId)index;
            return true;
        }
    }

    private static uint Bit(ButtonId button)
    {
        var index = (int)button;
        return (uint)index < 32u ? 1u << index : 0u;
    }
}
