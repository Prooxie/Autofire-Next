namespace GameFlow.Core.Enums;

/// <summary>
/// The family a touch gesture belongs to. Deliberately split from
/// direction/finger-count/shape rather than flattened into one enum of
/// every combination: "two-finger swipe down-left" and "three-finger
/// double tap" are points in a product space, and enumerating that space
/// would run to well over a hundred members that JSON profiles would
/// then be pinned to forever.
/// </summary>
public enum TouchGestureKind
{
    /// <summary>A directional flick. Reads <c>SwipeDirection</c>.</summary>
    Swipe,

    /// <summary>A quick touch that goes nowhere. Reads <c>TapCount</c>.</summary>
    Tap,

    /// <summary>A stationary hold past the dwell threshold.</summary>
    LongPress,

    /// <summary>Two fingers converging or separating. Reads <c>PinchDirection</c>.</summary>
    Pinch,

    /// <summary>Two fingers turning about their midpoint. Reads <c>RotateDirection</c>.</summary>
    Rotate,

    /// <summary>A traced figure matched against a template. Reads <c>Shape</c>.</summary>
    Shape
}

/// <summary>
/// Compass direction of a swipe. The four diagonals are only ever
/// produced when the binding asks for eight-way resolution; a four-way
/// binding quantizes the same stroke into the nearest cardinal, so the
/// two styles can coexist on one pad without a diagonal flick falling
/// through the cracks.
/// </summary>
public enum TouchSwipeDirection
{
    Up,
    UpRight,
    Right,
    DownRight,
    Down,
    DownLeft,
    Left,
    UpLeft
}

public enum TouchPinchDirection
{
    /// <summary>Fingers converging.</summary>
    In,

    /// <summary>Fingers separating.</summary>
    Out
}

/// <summary>
/// Rotation sense as SEEN ON THE SURFACE, with Y growing downward (the
/// touch convention). A gesture that looks clockwise to the user is
/// <see cref="Clockwise"/> here, even though that's a mathematically
/// positive angle sweep in a y-down frame.
/// </summary>
public enum TouchRotateDirection
{
    Clockwise,
    CounterClockwise
}

/// <summary>
/// Traced figures the recognizer ships templates for. Circles carry
/// their direction in the member itself because the two are recognized
/// as separate templates — a stroke's point ORDER is what distinguishes
/// them, and that survives the recognizer's start-point normalization
/// (which rotates a closed template's starting corner, never reverses
/// it).
/// </summary>
public enum TouchShape
{
    CircleClockwise,
    CircleCounterClockwise,
    Square,
    Triangle,
    Z,
    Checkmark
}
