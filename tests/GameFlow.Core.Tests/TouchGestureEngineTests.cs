using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Core.Models.Rules;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Core.Tests;

public sealed class TouchGestureEngineTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Shape bindings are present by default so shape matching is live in
    /// tests that need it; the engine skips matching entirely when no
    /// binding asks for a shape.
    /// </summary>
    private static TouchpadMapRule Rule(bool withShapeBinding = true) => new()
    {
        Id = "touch",
        GesturesEnabled = true,
        Gestures = withShapeBinding
            ?
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.Shape,
                    Shape = TouchShape.Square,
                    TargetButton = ButtonId.South
                }
            ]
            : []
    };

    private static IReadOnlyList<TouchContact> Contacts(params (float X, float Y)[] points)
    {
        var list = new List<TouchContact>(points.Length);
        for (var i = 0; i < points.Length; i++)
        {
            list.Add(new TouchContact(i, points[i].X, points[i].Y));
        }
        return list;
    }

    private static readonly IReadOnlyList<TouchContact> NoContacts = [];

    /// <summary>
    /// Drags the primary finger along <paramref name="points"/>, one tick
    /// each, then lifts. Returns everything emitted across the whole
    /// stroke, including the classification on lift-off.
    /// </summary>
    private static List<TouchGestureEvent> Stroke(
        TouchGestureEngine engine, TouchpadMapRule rule,
        IEnumerable<(float X, float Y)> points, int msPerStep = 20, int fingers = 1,
        DateTimeOffset? startAt = null)
    {
        var all = new List<TouchGestureEvent>();
        var now = startAt ?? Origin;

        foreach (var (x, y) in points)
        {
            // Extra fingers ride alongside the primary at a fixed offset,
            // which is enough to be counted without triggering pinch or
            // rotate (their separation and angle never change).
            var frame = new List<TouchContact> { new(0, x, y) };
            for (var f = 1; f < fingers; f++)
            {
                frame.Add(new TouchContact(f, x + (0.05f * f), y));
            }

            all.AddRange(engine.Tick(frame, rule, now));
            now = now.AddMilliseconds(msPerStep);
        }

        all.AddRange(engine.Tick(NoContacts, rule, now));
        return all;
    }

    /// <summary>Interpolates a straight drag so strokes have realistic sample density.</summary>
    private static List<(float X, float Y)> Line((float X, float Y) from, (float X, float Y) to, int steps = 12)
    {
        var points = new List<(float, float)>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            points.Add((from.X + (t * (to.X - from.X)), from.Y + (t * (to.Y - from.Y))));
        }
        return points;
    }

    // ── Swipes ─────────────────────────────────────────────────────

    [Theory]
    // Y grows downward on the surface, so a decreasing Y is "up" to the user.
    [InlineData(0.5f, 0.9f, 0.5f, 0.1f, TouchSwipeDirection.Up)]
    [InlineData(0.5f, 0.1f, 0.5f, 0.9f, TouchSwipeDirection.Down)]
    [InlineData(0.1f, 0.5f, 0.9f, 0.5f, TouchSwipeDirection.Right)]
    [InlineData(0.9f, 0.5f, 0.1f, 0.5f, TouchSwipeDirection.Left)]
    public void Cardinal_swipe_is_reported_at_both_resolutions(
        float x0, float y0, float x1, float y1, TouchSwipeDirection expected)
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, Rule(withShapeBinding: false), Line((x0, y0), (x1, y1)));

        var swipes = events.Where(e => e.Kind == TouchGestureKind.Swipe).ToList();
        Assert.Equal(2, swipes.Count);
        Assert.All(swipes, s => Assert.Equal(expected, s.SwipeDirection));
        // A four-way and an eight-way binding must both be able to catch
        // a straight flick; the resolution flag is what distinguishes the
        // two otherwise-identical readings.
        Assert.Contains(swipes, s => s.SwipeEightWay);
        Assert.Contains(swipes, s => !s.SwipeEightWay);
    }

    [Fact]
    public void Diagonal_swipe_resolves_to_diagonal_at_eight_way_and_cardinal_at_four_way()
    {
        var engine = new TouchGestureEngine();
        // Up and to the right: X increases, Y decreases.
        var events = Stroke(engine, Rule(withShapeBinding: false), Line((0.2f, 0.8f), (0.8f, 0.2f)));

        var swipes = events.Where(e => e.Kind == TouchGestureKind.Swipe).ToList();
        Assert.Equal(2, swipes.Count);

        var eight = Assert.Single(swipes, s => s.SwipeEightWay);
        Assert.Equal(TouchSwipeDirection.UpRight, eight.SwipeDirection);

        // Exactly 45 degrees sits on the Up/Right wedge boundary; the
        // point is that four-way collapses to a cardinal, not which.
        var four = Assert.Single(swipes, s => !s.SwipeEightWay);
        Assert.Contains(four.SwipeDirection, new[] { TouchSwipeDirection.Up, TouchSwipeDirection.Right });
    }

    [Fact]
    public void Eight_way_binding_ignores_the_four_way_reading_of_a_diagonal()
    {
        // A user who bound eight-way "Up" wants a deliberate straight-up
        // flick, not the up-and-right one that merely collapses to Up.
        var binding = new TouchGestureBinding
        {
            Kind = TouchGestureKind.Swipe,
            SwipeDirection = TouchSwipeDirection.Up,
            EightWay = true,
            TargetButton = ButtonId.South
        };

        var engine = new TouchGestureEngine();
        var events = Stroke(engine, Rule(withShapeBinding: false), Line((0.2f, 0.8f), (0.8f, 0.2f)));

        Assert.DoesNotContain(events, e => TouchGestureEngine.Matches(binding, e));
    }

    [Fact]
    public void Drag_shorter_than_the_swipe_threshold_is_not_a_swipe()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { SwipeMinDistance = 0.15f };
        // 0.08 of travel: past the tap ceiling, short of the swipe floor.
        var events = Stroke(engine, rule, Line((0.5f, 0.5f), (0.58f, 0.5f)), msPerStep: 40);

        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Swipe);
    }

    // ── Taps, multi-taps, long press ───────────────────────────────

    [Fact]
    public void Quick_stationary_touch_is_a_single_tap()
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, Rule(withShapeBinding: false), [(0.5f, 0.5f), (0.5f, 0.5f)], msPerStep: 30);

        var tap = Assert.Single(events, e => e.Kind == TouchGestureKind.Tap);
        Assert.Equal(1, tap.TapCount);
        Assert.Equal(1, tap.FingerCount);
    }

    [Fact]
    public void Taps_inside_the_window_accumulate_and_a_late_tap_restarts_the_run()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { MultiTapWindowMilliseconds = 300 };
        var now = Origin;

        var counts = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var events = Stroke(engine, rule, [(0.5f, 0.5f), (0.5f, 0.5f)], msPerStep: 30, startAt: now);
            counts.Add(events.Single(e => e.Kind == TouchGestureKind.Tap).TapCount);
            now = now.AddMilliseconds(150);
        }

        Assert.Equal([1, 2, 3], counts);

        // Well past the window — the run resets rather than reaching four.
        now = now.AddMilliseconds(2000);
        var late = Stroke(engine, rule, [(0.5f, 0.5f), (0.5f, 0.5f)], msPerStep: 30, startAt: now);
        Assert.Equal(1, late.Single(e => e.Kind == TouchGestureKind.Tap).TapCount);
    }

    [Fact]
    public void Multi_tap_run_does_not_carry_across_a_change_in_finger_count()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false);

        var first = Stroke(engine, rule, [(0.5f, 0.5f), (0.5f, 0.5f)], msPerStep: 30, fingers: 1);
        Assert.Equal(1, first.Single(e => e.Kind == TouchGestureKind.Tap).TapCount);

        var second = Stroke(engine, rule, [(0.5f, 0.5f), (0.5f, 0.5f)],
            msPerStep: 30, fingers: 2, startAt: Origin.AddMilliseconds(100));
        var tap = second.Single(e => e.Kind == TouchGestureKind.Tap);
        Assert.Equal(1, tap.TapCount);
        Assert.Equal(2, tap.FingerCount);
    }

    [Fact]
    public void Stationary_hold_past_the_dwell_fires_one_long_press_and_no_swipe()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { LongPressMilliseconds = 500 };

        // Held still for 800 ms, then released with a touch of drift.
        var held = Enumerable.Repeat((0.5f, 0.5f), 16).ToList();
        var events = Stroke(engine, rule, held, msPerStep: 50);

        Assert.Single(events, e => e.Kind == TouchGestureKind.LongPress);
        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Swipe);
        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Tap);
    }

    [Fact]
    public void Long_press_shorter_than_the_tap_window_does_not_also_tap_on_release()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with
        {
            LongPressMilliseconds = 100,
            TapMaxMilliseconds = 250
        };

        var events = Stroke(
            engine,
            rule,
            Enumerable.Repeat((0.5f, 0.5f), 4),
            msPerStep: 50);

        Assert.Single(events, e => e.Kind == TouchGestureKind.LongPress);
        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Tap);
    }

    [Fact]
    public void Moving_finger_never_fires_a_long_press()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { LongPressMilliseconds = 200 };
        var events = Stroke(engine, rule, Line((0.1f, 0.5f), (0.9f, 0.5f), steps: 20), msPerStep: 50);

        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.LongPress);
        Assert.Contains(events, e => e.Kind == TouchGestureKind.Swipe);
    }

    // ── Finger counting ────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Swipe_reports_the_peak_contact_count(int fingers)
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, Rule(withShapeBinding: false),
            Line((0.5f, 0.9f), (0.5f, 0.1f)), fingers: fingers);

        var swipe = events.First(e => e.Kind == TouchGestureKind.Swipe);
        Assert.Equal(fingers, swipe.FingerCount);
    }

    [Fact]
    public void Finger_count_is_the_peak_not_the_count_at_lift_off()
    {
        // Fingers leave a surface one at a time; reading the count at
        // lift-off would score nearly every multi-finger gesture as one.
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false);
        var now = Origin;

        foreach (var y in new[] { 0.9f, 0.7f, 0.5f, 0.3f })
        {
            engine.Tick(Contacts((0.5f, y), (0.6f, y), (0.7f, y)), rule, now);
            now = now.AddMilliseconds(20);
        }
        // Two fingers lift early, leaving one to finish the stroke.
        engine.Tick(Contacts((0.5f, 0.1f)), rule, now);
        var events = engine.Tick(NoContacts, rule, now.AddMilliseconds(20));

        Assert.Equal(3, events.First(e => e.Kind == TouchGestureKind.Swipe).FingerCount);
    }

    // ── Pinch and rotate ───────────────────────────────────────────

    [Fact]
    public void Separating_two_fingers_fires_pinch_out_once()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { PinchThreshold = 0.3f };
        var now = Origin;
        var all = new List<TouchGestureEvent>();

        // Start 0.10 apart and open to 0.40 — well past the 30% ratio.
        for (var i = 0; i <= 10; i++)
        {
            var half = 0.05f + (0.015f * i);
            all.AddRange(engine.Tick(Contacts((0.5f - half, 0.5f), (0.5f + half, 0.5f)), rule, now));
            now = now.AddMilliseconds(20);
        }
        all.AddRange(engine.Tick(NoContacts, rule, now));

        var pinch = Assert.Single(all, e => e.Kind == TouchGestureKind.Pinch);
        Assert.Equal(TouchPinchDirection.Out, pinch.PinchDirection);
    }

    [Fact]
    public void Converging_two_fingers_fires_pinch_in()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { PinchThreshold = 0.3f };
        var now = Origin;
        var all = new List<TouchGestureEvent>();

        for (var i = 0; i <= 10; i++)
        {
            var half = 0.25f - (0.02f * i);
            all.AddRange(engine.Tick(Contacts((0.5f - half, 0.5f), (0.5f + half, 0.5f)), rule, now));
            now = now.AddMilliseconds(20);
        }
        all.AddRange(engine.Tick(NoContacts, rule, now));

        var pinch = Assert.Single(all, e => e.Kind == TouchGestureKind.Pinch);
        Assert.Equal(TouchPinchDirection.In, pinch.PinchDirection);
    }

    [Theory]
    [InlineData(true, TouchRotateDirection.Clockwise)]
    [InlineData(false, TouchRotateDirection.CounterClockwise)]
    public void Turning_two_fingers_fires_rotate_in_the_direction_seen_on_the_surface(
        bool clockwise, TouchRotateDirection expected)
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false) with { RotateThresholdDegrees = 30f };
        var now = Origin;
        var all = new List<TouchGestureEvent>();

        const float Radius = 0.2f;
        for (var i = 0; i <= 12; i++)
        {
            // Y grows downward, so a growing angle sweeps clockwise as
            // the user sees it.
            var sweep = (float)(i * 5 * Math.PI / 180);
            var angle = clockwise ? sweep : -sweep;
            var dx = Radius * MathF.Cos(angle);
            var dy = Radius * MathF.Sin(angle);
            all.AddRange(engine.Tick(
                Contacts((0.5f - dx, 0.5f - dy), (0.5f + dx, 0.5f + dy)), rule, now));
            now = now.AddMilliseconds(20);
        }
        all.AddRange(engine.Tick(NoContacts, rule, now));

        var rotate = Assert.Single(all, e => e.Kind == TouchGestureKind.Rotate);
        Assert.Equal(expected, rotate.RotateDirection);
    }

    [Fact]
    public void Two_fingers_held_at_a_fixed_offset_produce_neither_pinch_nor_rotate()
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, Rule(withShapeBinding: false),
            Line((0.5f, 0.9f), (0.5f, 0.1f)), fingers: 2);

        Assert.DoesNotContain(events, e => e.Kind is TouchGestureKind.Pinch or TouchGestureKind.Rotate);
    }

    // ── Shape templates ────────────────────────────────────────────

    private static TouchpadMapRule ShapeRule(TouchShape shape) => new()
    {
        Id = "touch",
        GesturesEnabled = true,
        ShapeMatchThreshold = 0.8f,
        Gestures =
        [
            new TouchGestureBinding { Kind = TouchGestureKind.Shape, Shape = shape, TargetButton = ButtonId.South }
        ]
    };

    private static List<(float X, float Y)> Trace(params (float X, float Y)[] corners)
    {
        var path = new List<(float, float)>();
        for (var i = 1; i < corners.Length; i++)
        {
            // Skip the duplicated joint so corners aren't over-sampled.
            path.AddRange(Line(corners[i - 1], corners[i], steps: 10).Skip(i == 1 ? 0 : 1));
        }
        return path;
    }

    private static List<(float X, float Y)> CircleTrace(bool clockwise)
    {
        var path = new List<(float, float)>();
        for (var i = 0; i <= 32; i++)
        {
            var sweep = 2f * MathF.PI * i / 32;
            var angle = (-MathF.PI / 2f) + (clockwise ? sweep : -sweep);
            path.Add((0.5f + (0.35f * MathF.Cos(angle)), 0.5f + (0.35f * MathF.Sin(angle))));
        }
        return path;
    }

    public static TheoryData<TouchShape> AllShapes() =>
    [
        TouchShape.CircleClockwise,
        TouchShape.CircleCounterClockwise,
        TouchShape.Square,
        TouchShape.Triangle,
        TouchShape.Z,
        TouchShape.Checkmark
    ];

    private static List<(float X, float Y)> TraceFor(TouchShape shape) => shape switch
    {
        TouchShape.CircleClockwise => CircleTrace(clockwise: true),
        TouchShape.CircleCounterClockwise => CircleTrace(clockwise: false),
        TouchShape.Square => Trace((0.2f, 0.2f), (0.8f, 0.2f), (0.8f, 0.8f), (0.2f, 0.8f), (0.2f, 0.2f)),
        TouchShape.Triangle => Trace((0.5f, 0.15f), (0.85f, 0.85f), (0.15f, 0.85f), (0.5f, 0.15f)),
        TouchShape.Z => Trace((0.2f, 0.2f), (0.8f, 0.2f), (0.2f, 0.8f), (0.8f, 0.8f)),
        TouchShape.Checkmark => Trace((0.15f, 0.45f), (0.4f, 0.85f), (0.85f, 0.15f)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Each_template_is_recognized_as_itself(TouchShape shape)
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, ShapeRule(shape), TraceFor(shape), msPerStep: 10);

        var match = Assert.Single(events, e => e.Kind == TouchGestureKind.Shape);
        Assert.Equal(shape, match.Shape);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void A_matched_shape_suppresses_the_swipe_the_stroke_would_otherwise_produce(TouchShape shape)
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, ShapeRule(shape), TraceFor(shape), msPerStep: 10);

        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Swipe);
    }

    [Fact]
    public void Circle_direction_is_not_confused()
    {
        // The two circles are identical figures distinguished only by the
        // order their points are traced in — the property the recognizer
        // must preserve, and the one a rotation-invariant matcher loses.
        var engine = new TouchGestureEngine();
        var clockwise = Stroke(engine, ShapeRule(TouchShape.CircleClockwise), CircleTrace(true), msPerStep: 10);
        Assert.Equal(TouchShape.CircleClockwise,
            clockwise.Single(e => e.Kind == TouchGestureKind.Shape).Shape);

        var counter = Stroke(new TouchGestureEngine(), ShapeRule(TouchShape.CircleCounterClockwise),
            CircleTrace(false), msPerStep: 10);
        Assert.Equal(TouchShape.CircleCounterClockwise,
            counter.Single(e => e.Kind == TouchGestureKind.Shape).Shape);
    }

    [Fact]
    public void A_square_started_from_a_different_corner_still_matches()
    {
        // Closed figures are compared against every cyclic rotation of
        // their template, so where the user began tracing is irrelevant.
        var engine = new TouchGestureEngine();
        var fromBottomRight = Trace(
            (0.8f, 0.8f), (0.2f, 0.8f), (0.2f, 0.2f), (0.8f, 0.2f), (0.8f, 0.8f));

        var events = Stroke(engine, ShapeRule(TouchShape.Square), fromBottomRight, msPerStep: 10);
        Assert.Equal(TouchShape.Square, events.Single(e => e.Kind == TouchGestureKind.Shape).Shape);
    }

    [Fact]
    public void A_straight_swipe_is_not_mistaken_for_a_shape()
    {
        // Regression: per-axis normalization used to stretch a swipe's
        // hand-wobble to full height, making a flick a passable Z — and
        // since a shape match suppresses the swipe, the user lost the
        // gesture they actually made.
        var engine = new TouchGestureEngine();
        var wobbly = new List<(float, float)>();
        for (var i = 0; i <= 20; i++)
        {
            var t = i / 20f;
            wobbly.Add((0.1f + (0.8f * t), 0.5f + (0.01f * MathF.Sin(t * 6f))));
        }

        var events = Stroke(engine, ShapeRule(TouchShape.Z), wobbly, msPerStep: 10);

        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Shape);
        Assert.Contains(events, e => e.Kind == TouchGestureKind.Swipe);
    }

    [Fact]
    public void Shape_matching_is_skipped_when_no_binding_asks_for_a_shape()
    {
        var engine = new TouchGestureEngine();
        var events = Stroke(engine, Rule(withShapeBinding: false), CircleTrace(true), msPerStep: 10);

        Assert.DoesNotContain(events, e => e.Kind == TouchGestureKind.Shape);
    }

    // ── Binding match rules ────────────────────────────────────────

    [Fact]
    public void Binding_requires_the_finger_count_to_agree()
    {
        var binding = new TouchGestureBinding
        {
            Kind = TouchGestureKind.Swipe,
            SwipeDirection = TouchSwipeDirection.Up,
            FingerCount = 3,
            TargetButton = ButtonId.South
        };

        var oneFinger = new TouchGestureEvent(TouchGestureKind.Swipe, 1, TouchSwipeDirection.Up);
        var threeFinger = new TouchGestureEvent(TouchGestureKind.Swipe, 3, TouchSwipeDirection.Up);

        Assert.False(TouchGestureEngine.Matches(binding, oneFinger));
        Assert.True(TouchGestureEngine.Matches(binding, threeFinger));
    }

    [Fact]
    public void A_disabled_binding_never_matches()
    {
        var binding = new TouchGestureBinding
        {
            Enabled = false,
            Kind = TouchGestureKind.Tap,
            TargetButton = ButtonId.South
        };

        Assert.False(TouchGestureEngine.Matches(
            binding, new TouchGestureEvent(TouchGestureKind.Tap, 1, TapCount: 1)));
    }

    [Fact]
    public void Reset_discards_a_stroke_in_progress()
    {
        var engine = new TouchGestureEngine();
        var rule = Rule(withShapeBinding: false);

        engine.Tick(Contacts((0.5f, 0.9f)), rule, Origin);
        engine.Reset();

        // Lifting after a reset must not classify the abandoned stroke.
        var events = engine.Tick(NoContacts, rule, Origin.AddMilliseconds(40));
        Assert.Empty(events);
    }
}
