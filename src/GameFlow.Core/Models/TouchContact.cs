namespace GameFlow.Core.Models;

/// <summary>
/// One finger on a touch surface, in the same normalized 0..1 surface
/// coordinates as <see cref="ControllerSnapshot.TouchX"/> (X: 0 = left,
/// Y: 0 = top — SDL's screen convention, not the stick convention).
///
/// <para>
/// <see cref="FingerIndex"/> is the hardware's finger SLOT, not a
/// per-touch sequence number: a DualSense reports slot 0 and slot 1 and
/// keeps a finger in the same slot for as long as it stays down. That
/// stability is what lets the gesture recognizer follow a specific
/// finger across ticks — tracking by list position instead would silently
/// re-target when an earlier finger lifts and the list shifts up, which
/// reads to a pinch/rotate recognizer as a sudden teleport.
/// </para>
/// </summary>
/// <param name="FingerIndex">Hardware finger slot, stable while the finger stays down.</param>
/// <param name="X">Normalized position, 0 = left edge.</param>
/// <param name="Y">Normalized position, 0 = top edge.</param>
/// <param name="Pressure">Reported pressure 0..1, or 1 on surfaces that only report contact.</param>
public readonly record struct TouchContact(int FingerIndex, float X, float Y, float Pressure = 1f);
