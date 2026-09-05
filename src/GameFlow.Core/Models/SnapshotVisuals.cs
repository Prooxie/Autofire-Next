using GameFlow.Core.Enums;

namespace GameFlow.Core.Models;

/// <summary>
/// Decides whether two snapshots would draw the same controller art.
///
/// <para>
/// The runtime hands the UI a fresh snapshot object every tick — new
/// timestamp, same state — and a connected pad streams continuously, so
/// something has to decide when a repaint is actually owed. Getting that
/// wrong is expensive in one direction and invisible in the other: too
/// eager and the whole theme tree repaints for nothing many times a
/// second; too lax and live feedback silently stops moving, which is
/// indistinguishable from the input pipeline being broken.
/// </para>
///
/// <para>
/// It lives in Core, away from the control that consumes it, because that
/// second failure mode cannot be caught by looking at the app: a frozen
/// dot and a dot that is never sent look the same. Every field the art can
/// show needs a case here.
/// </para>
/// </summary>
public static class SnapshotVisuals
{
    /// <summary>Finer than any deflection a rendered surface can show.</summary>
    public const float Epsilon = 1f / 256f;

    /// <summary>
    /// True when both snapshots would paint identically. Timestamp and
    /// device identity are ignored — they change every tick and never
    /// change a pixel.
    /// </summary>
    public static bool AreEquivalent(ControllerSnapshot a, ControllerSnapshot b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (ReferenceEquals(a, b)) { return true; }

        if (a.TouchContactCount != b.TouchContactCount) { return false; }
        if (MathF.Abs(a.LeftTrigger - b.LeftTrigger) > Epsilon) { return false; }
        if (MathF.Abs(a.RightTrigger - b.RightTrigger) > Epsilon) { return false; }
        if (MathF.Abs(a.LeftStick.X - b.LeftStick.X) > Epsilon) { return false; }
        if (MathF.Abs(a.LeftStick.Y - b.LeftStick.Y) > Epsilon) { return false; }
        if (MathF.Abs(a.RightStick.X - b.RightStick.X) > Epsilon) { return false; }
        if (MathF.Abs(a.RightStick.Y - b.RightStick.Y) > Epsilon) { return false; }
        if (!TouchEquivalent(a, b)) { return false; }

        // A mask IS the pressed set, so equality is the comparison this
        // used to spell out over two dictionaries — including the case it
        // was written for, where one snapshot carries a key explicitly set
        // to false and the other omits it. A bit that is clear says the
        // same thing either way.
        return a.Buttons == b.Buttons;
    }

    /// <summary>
    /// Compares where the fingers ARE, not just how many there are.
    ///
    /// <para>
    /// Counting alone made a dragged finger invisible. Putting one down
    /// changed the count, so that frame painted; every frame after it had
    /// the same count, the same buttons and the same sticks, so this
    /// returned "identical" and the dot stayed frozen where it landed
    /// until the finger lifted and the count changed back. The surface was
    /// drawing touch correctly the whole time and simply was never asked
    /// to repaint.
    /// </para>
    ///
    /// <para>
    /// Pressure counts too: contact dots scale their radius with it, so a
    /// finger pressing harder without moving is a visible change.
    /// </para>
    /// </summary>
    private static bool TouchEquivalent(ControllerSnapshot a, ControllerSnapshot b)
    {
        if (a.TouchDown != b.TouchDown) { return false; }

        // The primary point drives themes that map touch without ever
        // reading the contact list.
        if (a.TouchDown
            && (MathF.Abs(a.TouchX - b.TouchX) > Epsilon
                || MathF.Abs(a.TouchY - b.TouchY) > Epsilon))
        {
            return false;
        }

        var left = a.TouchContacts;
        var right = b.TouchContacts;
        if (left.Count != right.Count) { return false; }

        for (var i = 0; i < left.Count; i++)
        {
            // Positional comparison is safe because both lists come from
            // the same producer in finger-slot order, and the slot id is
            // compared anyway — a slot change moves a dot and should
            // repaint regardless.
            if (left[i].FingerIndex != right[i].FingerIndex
                || MathF.Abs(left[i].X - right[i].X) > Epsilon
                || MathF.Abs(left[i].Y - right[i].Y) > Epsilon
                || MathF.Abs(left[i].Pressure - right[i].Pressure) > Epsilon)
            {
                return false;
            }
        }

        return true;
    }

}
