using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Core.Models.Rules;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// One recognized gesture. Discriminator fields not relevant to
/// <see cref="Kind"/> hold their default and must not be read — see
/// <see cref="TouchGestureBinding"/>, whose shape this mirrors so a
/// match is a field-by-field comparison.
/// </summary>
/// <param name="Kind">Which family fired.</param>
/// <param name="FingerCount">Peak contacts seen during the stroke, 1–5.</param>
/// <param name="SwipeDirection">Quantized direction; swipes only.</param>
/// <param name="SwipeEightWay">
/// Which resolution produced <paramref name="SwipeDirection"/>. One
/// stroke is reported twice — once quantized to eight directions and
/// once to four — so that four-way and eight-way bindings can live on
/// the same pad without either style swallowing the other's gestures.
/// For a stroke straight along a cardinal the two readings name the same
/// direction and this flag is all that tells them apart, which is why it
/// exists rather than being inferred from whether the direction is
/// diagonal.
/// </param>
/// <param name="TapCount">Position in the multi-tap run; taps only.</param>
/// <param name="PinchDirection">Pinches only.</param>
/// <param name="RotateDirection">Rotations only.</param>
/// <param name="Shape">Matched template; shapes only.</param>
public readonly record struct TouchGestureEvent(
    TouchGestureKind Kind,
    int FingerCount,
    TouchSwipeDirection SwipeDirection = default,
    bool SwipeEightWay = false,
    int TapCount = 0,
    TouchPinchDirection PinchDirection = default,
    TouchRotateDirection RotateDirection = default,
    TouchShape Shape = default);

/// <summary>
/// Turns a stream of touch contacts into discrete gestures. One instance
/// per <see cref="TouchpadMapRule"/>, ticked once per polling frame by
/// <see cref="ControllerMappingPipeline"/>; it is stateful and not
/// thread-safe, which is fine because a slot's pipeline is single
/// threaded.
///
/// <para>
/// The unit of recognition is a STROKE: everything between the first
/// finger landing on an empty surface and the last finger leaving it.
/// Treating a whole multi-finger episode as one stroke, rather than
/// tracking each finger separately, is what makes "three-finger swipe
/// down" expressible — fingers never land or lift in unison, and
/// per-finger strokes would report three separate one-finger swipes at
/// three slightly different times.
/// </para>
///
/// <para>
/// Continuous gestures (long press, pinch, rotate) fire DURING a stroke,
/// once each, as soon as their threshold is crossed. Discrete ones
/// (swipe, tap, shape) are classified when the stroke ends, because
/// which one a stroke was is not knowable until it is over: a slow drag
/// that stops short is not a swipe, and a circle only becomes a circle
/// on closing.
/// </para>
/// </summary>
public sealed class TouchGestureEngine
{
    /// <summary>
    /// Ceiling on retained path samples. At 1000 Hz — the highest
    /// polling rate this app offers — 512 samples is about half a second
    /// of continuous drawing, comfortably longer than any real figure,
    /// and bounds the per-stroke allocation regardless of how long a
    /// finger stays down. Sampling is distance-gated as well (see
    /// <see cref="MinSampleDistance"/>), so in practice a stroke uses a
    /// small fraction of this.
    /// </summary>
    private const int MaxPathSamples = 512;

    /// <summary>
    /// Minimum movement before a new path sample is recorded. Without
    /// this a finger held still would fill the buffer with hundreds of
    /// identical points, and the shape resampler — which walks by arc
    /// length — would find almost no length to walk.
    /// </summary>
    private const float MinSampleDistance = 0.004f;

    private readonly List<TouchShapeTemplates.Point> path = new(64);
    private readonly List<TouchGestureEvent> emitted = new(4);

    private bool strokeActive;
    private DateTimeOffset strokeStartedAt;
    private int peakContacts;
    private float pathTravel;
    private TouchShapeTemplates.Point strokeStart;
    private TouchShapeTemplates.Point strokeLatest;

    private bool longPressFired;
    private bool pinchFired;
    private bool rotateFired;

    /// <summary>Finger pair currently driving pinch/rotate, or null while fewer than two fingers are down.</summary>
    private (int A, int B)? trackedPair;
    private float pinchBaselineDistance;
    private float lastPairAngle;
    private float rotationAccumulated;

    private int tapRunCount;
    private int tapRunFingers;
    private DateTimeOffset lastTapAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Feeds one frame and returns the gestures that fired on it —
    /// usually none. The returned list is REUSED between calls, so
    /// callers must consume it before ticking again rather than storing
    /// it; that keeps a per-frame path free of allocation at 1000 Hz.
    /// </summary>
    public IReadOnlyList<TouchGestureEvent> Tick(
        IReadOnlyList<TouchContact> contacts, TouchpadMapRule rule, DateTimeOffset now)
    {
        emitted.Clear();

        if (contacts.Count == 0)
        {
            if (strokeActive)
            {
                ClassifyCompletedStroke(rule, now);
                strokeActive = false;
            }
            return emitted;
        }

        if (!strokeActive)
        {
            BeginStroke(contacts, now);
        }

        peakContacts = Math.Max(peakContacts, contacts.Count);

        var primary = Primary(contacts);
        var point = new TouchShapeTemplates.Point(primary.X, primary.Y);
        var step = Distance(strokeLatest, point);
        if (step >= MinSampleDistance)
        {
            pathTravel += step;
            strokeLatest = point;
            if (path.Count < MaxPathSamples)
            {
                path.Add(point);
            }
        }

        TrackLongPress(rule, now);
        TrackPinchAndRotate(contacts, rule);

        return emitted;
    }

    /// <summary>
    /// Drops all stroke state. Called when the owning rule is disabled
    /// or its layer deactivates mid-stroke, so the finger already down
    /// doesn't complete a gesture against a rule that is no longer
    /// listening — and, more importantly, so re-enabling doesn't resume
    /// a stroke whose start is now minutes in the past and would classify
    /// as an absurdly long press.
    /// </summary>
    public void Reset()
    {
        strokeActive = false;
        path.Clear();
        emitted.Clear();
        trackedPair = null;
        tapRunCount = 0;
        lastTapAt = DateTimeOffset.MinValue;
    }

    private void BeginStroke(IReadOnlyList<TouchContact> contacts, DateTimeOffset now)
    {
        strokeActive = true;
        strokeStartedAt = now;
        peakContacts = 0;
        pathTravel = 0f;
        longPressFired = false;
        pinchFired = false;
        rotateFired = false;
        trackedPair = null;
        rotationAccumulated = 0f;
        path.Clear();

        var primary = Primary(contacts);
        strokeStart = new TouchShapeTemplates.Point(primary.X, primary.Y);
        strokeLatest = strokeStart;
        path.Add(strokeStart);
    }

    /// <summary>
    /// The lowest finger slot present. Matches
    /// <see cref="ControllerSnapshot.WithTouchContacts"/>'s choice of
    /// primary, so the path this engine traces is the same finger the
    /// anchored stick and mouse modes are following.
    /// </summary>
    private static TouchContact Primary(IReadOnlyList<TouchContact> contacts)
    {
        var primary = contacts[0];
        for (var i = 1; i < contacts.Count; i++)
        {
            if (contacts[i].FingerIndex < primary.FingerIndex)
            {
                primary = contacts[i];
            }
        }
        return primary;
    }

    private void TrackLongPress(TouchpadMapRule rule, DateTimeOffset now)
    {
        if (longPressFired || pathTravel > rule.TapMaxTravel)
        {
            return;
        }

        if ((now - strokeStartedAt).TotalMilliseconds >= rule.LongPressMilliseconds)
        {
            longPressFired = true;
            emitted.Add(new TouchGestureEvent(TouchGestureKind.LongPress, Math.Max(1, peakContacts)));
        }
    }

    private void TrackPinchAndRotate(IReadOnlyList<TouchContact> contacts, TouchpadMapRule rule)
    {
        if (contacts.Count < 2)
        {
            // Baseline is dropped rather than kept, so bringing a second
            // finger back down starts a fresh measurement instead of
            // comparing against a gap from before it was lifted.
            trackedPair = null;
            return;
        }

        var (first, second) = LowestPair(contacts);
        var pair = (first.FingerIndex, second.FingerIndex);
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        var distance = MathF.Sqrt((dx * dx) + (dy * dy));
        var angle = MathF.Atan2(dy, dx);

        if (trackedPair != pair)
        {
            // A different pair of fingers is a different measurement:
            // rebaseline instead of reading the swap as a huge jump in
            // both separation and angle.
            trackedPair = pair;
            pinchBaselineDistance = distance;
            lastPairAngle = angle;
            rotationAccumulated = 0f;
            return;
        }

        if (!pinchFired && pinchBaselineDistance > 1e-4f)
        {
            var ratio = distance / pinchBaselineDistance;
            if (ratio >= 1f + rule.PinchThreshold)
            {
                pinchFired = true;
                emitted.Add(new TouchGestureEvent(
                    TouchGestureKind.Pinch, peakContacts, PinchDirection: TouchPinchDirection.Out));
            }
            else if (ratio <= 1f - rule.PinchThreshold)
            {
                pinchFired = true;
                emitted.Add(new TouchGestureEvent(
                    TouchGestureKind.Pinch, peakContacts, PinchDirection: TouchPinchDirection.In));
            }
        }

        // Per-frame delta wrapped into (-pi, pi] before accumulating.
        // Summing raw angle differences would jump by a full turn every
        // time the pair crosses the atan2 branch cut at pi, which a
        // slow, steady rotation does routinely.
        var delta = WrapAngle(angle - lastPairAngle);
        lastPairAngle = angle;
        rotationAccumulated += delta;

        if (!rotateFired)
        {
            var threshold = rule.RotateThresholdDegrees * MathF.PI / 180f;
            if (MathF.Abs(rotationAccumulated) >= threshold)
            {
                rotateFired = true;
                // Y grows downward on a touch surface, so a positive
                // angle sweep is clockwise from the user's side.
                emitted.Add(new TouchGestureEvent(
                    TouchGestureKind.Rotate,
                    peakContacts,
                    RotateDirection: rotationAccumulated > 0f
                        ? TouchRotateDirection.Clockwise
                        : TouchRotateDirection.CounterClockwise));
            }
        }
    }

    private static (TouchContact First, TouchContact Second) LowestPair(IReadOnlyList<TouchContact> contacts)
    {
        var first = contacts[0];
        var second = contacts[1];
        if (second.FingerIndex < first.FingerIndex)
        {
            (first, second) = (second, first);
        }

        for (var i = 2; i < contacts.Count; i++)
        {
            var candidate = contacts[i];
            if (candidate.FingerIndex < first.FingerIndex)
            {
                second = first;
                first = candidate;
            }
            else if (candidate.FingerIndex < second.FingerIndex)
            {
                second = candidate;
            }
        }

        return (first, second);
    }

    private void ClassifyCompletedStroke(TouchpadMapRule rule, DateTimeOffset now)
    {
        var fingers = Math.Max(1, peakContacts);
        var duration = (now - strokeStartedAt).TotalMilliseconds;

        // A long press that already fired has consumed its stroke — the
        // finger then lifting is the release, not a separate gesture.
        // This precedes tap classification because imported profiles can
        // set the dwell below the tap window; release must not fire both.
        if (longPressFired)
        {
            return;
        }

        if (duration <= rule.TapMaxMilliseconds && pathTravel <= rule.TapMaxTravel)
        {
            EmitTap(rule, fingers, now);
            return;
        }

        if (WantsShapes(rule) && TryMatchShape(rule, fingers))
        {
            return;
        }

        var netX = strokeLatest.X - strokeStart.X;
        var netY = strokeLatest.Y - strokeStart.Y;
        var netDistance = MathF.Sqrt((netX * netX) + (netY * netY));
        if (netDistance >= rule.SwipeMinDistance)
        {
            // Reported at BOTH resolutions, unconditionally. A pad can
            // carry four-way and eight-way bindings at once and the
            // recognizer cannot know which the user meant, so it states
            // the stroke both ways and lets each binding take the
            // reading it asked for. Emitting only the eight-way reading
            // and collapsing on demand doesn't work: an up-and-right
            // flick reads UpRight at eight-way and Up at four-way, and a
            // four-way binding has to see that Up.
            emitted.Add(new TouchGestureEvent(
                TouchGestureKind.Swipe, fingers,
                SwipeDirection: DirectionOf(netX, netY, eightWay: true),
                SwipeEightWay: true));

            emitted.Add(new TouchGestureEvent(
                TouchGestureKind.Swipe, fingers,
                SwipeDirection: DirectionOf(netX, netY, eightWay: false),
                SwipeEightWay: false));
        }
    }

    /// <summary>
    /// Whether any enabled binding actually asks for a shape. Shape
    /// matching SUPPRESSES the swipe that a stroke would otherwise
    /// produce, so running it on a pad with no shape bindings is pure
    /// downside: a hurried flick that happens to score well against some
    /// template would silently eat the swipe the user wanted.
    /// </summary>
    private static bool WantsShapes(TouchpadMapRule rule)
    {
        for (var i = 0; i < rule.Gestures.Count; i++)
        {
            var binding = rule.Gestures[i];
            if (binding.Enabled && binding.Kind == TouchGestureKind.Shape)
            {
                return true;
            }
        }
        return false;
    }

    private void EmitTap(TouchpadMapRule rule, int fingers, DateTimeOffset now)
    {
        var withinWindow = lastTapAt != DateTimeOffset.MinValue
            && (now - lastTapAt).TotalMilliseconds <= rule.MultiTapWindowMilliseconds;

        // The run only continues for the same number of fingers: a
        // one-finger tap followed by a two-finger tap is two separate
        // gestures, not a "double tap" of indeterminate width.
        tapRunCount = withinWindow && tapRunFingers == fingers ? tapRunCount + 1 : 1;
        tapRunFingers = fingers;
        lastTapAt = now;

        emitted.Add(new TouchGestureEvent(TouchGestureKind.Tap, fingers, TapCount: tapRunCount));
    }

    private bool TryMatchShape(TouchpadMapRule rule, int fingers)
    {
        var match = TouchShapeTemplates.Match(path);
        if (match is null || match.Value.Score < rule.ShapeMatchThreshold)
        {
            return false;
        }

        emitted.Add(new TouchGestureEvent(TouchGestureKind.Shape, fingers, Shape: match.Value.Shape));
        return true;
    }

    /// <summary>
    /// Quantizes a displacement to a compass direction. Y is negated
    /// first because the surface's Y grows downward while the returned
    /// directions are named from the user's point of view — without it,
    /// a finger pushed away from the body would report as
    /// <see cref="TouchSwipeDirection.Down"/>.
    /// </summary>
    public static TouchSwipeDirection DirectionOf(float dx, float dy, bool eightWay)
    {
        var degrees = MathF.Atan2(-dy, dx) * 180f / MathF.PI;
        if (degrees < 0f)
        {
            degrees += 360f;
        }

        if (!eightWay)
        {
            // 90-degree wedges centered on each cardinal: Right owns
            // -45..45, Up owns 45..135, and so on.
            return degrees switch
            {
                < 45f => TouchSwipeDirection.Right,
                < 135f => TouchSwipeDirection.Up,
                < 225f => TouchSwipeDirection.Left,
                < 315f => TouchSwipeDirection.Down,
                _ => TouchSwipeDirection.Right
            };
        }

        // 45-degree wedges, each centered on its direction (Right owns
        // -22.5..22.5, and so on around).
        return degrees switch
        {
            < 22.5f => TouchSwipeDirection.Right,
            < 67.5f => TouchSwipeDirection.UpRight,
            < 112.5f => TouchSwipeDirection.Up,
            < 157.5f => TouchSwipeDirection.UpLeft,
            < 202.5f => TouchSwipeDirection.Left,
            < 247.5f => TouchSwipeDirection.DownLeft,
            < 292.5f => TouchSwipeDirection.Down,
            < 337.5f => TouchSwipeDirection.DownRight,
            _ => TouchSwipeDirection.Right
        };
    }

    /// <summary>Wraps to (-pi, pi].</summary>
    private static float WrapAngle(float radians)
    {
        while (radians <= -MathF.PI)
        {
            radians += 2f * MathF.PI;
        }
        while (radians > MathF.PI)
        {
            radians -= 2f * MathF.PI;
        }
        return radians;
    }

    private static float Distance(TouchShapeTemplates.Point a, TouchShapeTemplates.Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// True when <paramref name="gesture"/> satisfies
    /// <paramref name="binding"/>. Only the fields that discriminate the
    /// binding's own kind are compared — a swipe binding says nothing
    /// about tap counts, and demanding the defaults match would make
    /// every binding fail.
    /// </summary>
    public static bool Matches(TouchGestureBinding binding, TouchGestureEvent gesture)
    {
        if (!binding.Enabled || binding.Kind != gesture.Kind)
        {
            return false;
        }

        if (Math.Clamp(binding.FingerCount, 1, 5) != gesture.FingerCount)
        {
            return false;
        }

        return binding.Kind switch
        {
            TouchGestureKind.Swipe => binding.SwipeDirection == gesture.SwipeDirection
                && binding.EightWay == gesture.SwipeEightWay,
            TouchGestureKind.Tap => Math.Max(1, binding.TapCount) == gesture.TapCount,
            TouchGestureKind.LongPress => true,
            TouchGestureKind.Pinch => binding.PinchDirection == gesture.PinchDirection,
            TouchGestureKind.Rotate => binding.RotateDirection == gesture.RotateDirection,
            TouchGestureKind.Shape => binding.Shape == gesture.Shape,
            _ => false
        };
    }
}
