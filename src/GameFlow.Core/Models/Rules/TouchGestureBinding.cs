using System.Text.Json.Serialization;
using GameFlow.Core.Enums;

namespace GameFlow.Core.Models.Rules;

/// <summary>
/// One "when this gesture happens, press that button" entry on a
/// <see cref="TouchpadMapRule"/>. Only the discriminator fields relevant
/// to <see cref="Kind"/> are read — a <see cref="TouchGestureKind.Tap"/>
/// binding ignores <see cref="SwipeDirection"/> entirely — which keeps
/// one flat record serializable without a second layer of polymorphism
/// in the profile JSON.
///
/// <para>
/// A gesture is an EVENT, not a state: there is no "gesture is still
/// happening" for a flick that has already finished. So the output is a
/// timed pulse of <see cref="TargetButton"/> rather than a hold, and
/// <see cref="HoldMilliseconds"/> is how long the game sees the press.
/// Games sample input per frame, so a pulse shorter than a frame at
/// their refresh rate can be missed entirely — hence a default
/// comfortably longer than one 60 Hz frame.
/// </para>
/// </summary>
public sealed record TouchGestureBinding
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("kind")]
    public TouchGestureKind Kind { get; init; } = TouchGestureKind.Swipe;

    /// <summary>
    /// Number of fingers the gesture must be made with, 1–5. Matched
    /// against the PEAK contact count seen during the stroke, not the
    /// count at the moment it ended: fingers rarely leave a surface
    /// simultaneously, so reading the count at lift-off would score
    /// almost every three-finger swipe as a one-finger swipe.
    /// </summary>
    [JsonPropertyName("fingerCount")]
    public int FingerCount { get; init; } = 1;

    /// <summary>Swipe only.</summary>
    [JsonPropertyName("swipeDirection")]
    public TouchSwipeDirection SwipeDirection { get; init; } = TouchSwipeDirection.Up;

    /// <summary>
    /// Swipe only. False quantizes the stroke to the nearest of four
    /// cardinals (so a lazy up-and-right flick still counts as Up);
    /// true resolves all eight, requiring the diagonals be aimed.
    /// </summary>
    [JsonPropertyName("eightWay")]
    public bool EightWay { get; init; }

    /// <summary>Tap only: 1 = single, 2 = double, 3 = triple.</summary>
    [JsonPropertyName("tapCount")]
    public int TapCount { get; init; } = 1;

    /// <summary>Pinch only.</summary>
    [JsonPropertyName("pinchDirection")]
    public TouchPinchDirection PinchDirection { get; init; } = TouchPinchDirection.In;

    /// <summary>Rotate only.</summary>
    [JsonPropertyName("rotateDirection")]
    public TouchRotateDirection RotateDirection { get; init; } = TouchRotateDirection.Clockwise;

    /// <summary>Shape only.</summary>
    [JsonPropertyName("shape")]
    public TouchShape Shape { get; init; } = TouchShape.CircleClockwise;

    /// <summary>The virtual button this gesture presses. <see cref="ButtonId.None"/> makes the binding inert.</summary>
    [JsonPropertyName("targetButton")]
    public ButtonId TargetButton { get; init; } = ButtonId.None;

    /// <summary>
    /// How long <see cref="TargetButton"/> stays held once the gesture
    /// fires. The default clears one 60 Hz frame (16.7 ms) several times
    /// over, so no realistic game misses the press, while staying short
    /// enough not to read as a deliberate hold.
    /// </summary>
    [JsonPropertyName("holdMs")]
    public int HoldMilliseconds { get; init; } = 90;
}
