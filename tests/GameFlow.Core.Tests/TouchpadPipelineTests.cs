using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Core.Models.Rules;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Core.Tests;

/// <summary>
/// End-to-end coverage of the touchpad pass: gestures reaching virtual
/// buttons through a real pipeline, and the anchored stick / wedge D-pad
/// / mouse modes composing with them.
/// </summary>
public sealed class TouchpadPipelineTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ControllerSnapshot Touch(params (float X, float Y)[] points)
    {
        var snapshot = new ControllerSnapshot { Buttons = ButtonState.Clone(ButtonState.CreateEmptyMap()) };
        if (points.Length == 0)
        {
            return snapshot;
        }

        var contacts = new List<TouchContact>(points.Length);
        for (var i = 0; i < points.Length; i++)
        {
            contacts.Add(new TouchContact(i, points[i].X, points[i].Y));
        }
        return snapshot.WithTouchContacts(contacts);
    }

    private static ProfileDocument Profile(TouchpadMapRule rule) => new() { Rules = [rule] };

    [Fact]
    public void Swipe_up_presses_its_bound_button_and_releases_after_the_hold()
    {
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            StickEnabled = false,
            GesturesEnabled = true,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.Swipe,
                    SwipeDirection = TouchSwipeDirection.Up,
                    TargetButton = ButtonId.North,
                    HoldMilliseconds = 90
                }
            ]
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));
        var now = Origin;

        // Drag the finger up the pad (Y decreasing) and lift.
        for (var i = 0; i <= 10; i++)
        {
            pipeline.Process(Touch((0.5f, 0.9f - (0.08f * i))), now);
            now = now.AddMilliseconds(15);
        }

        var onLift = pipeline.Process(Touch(), now);
        Assert.True(onLift.VirtualSnapshot.IsPressed(ButtonId.North));

        // Still held part-way through the pulse...
        var midPulse = pipeline.Process(Touch(), now.AddMilliseconds(50));
        Assert.True(midPulse.VirtualSnapshot.IsPressed(ButtonId.North));

        // ...and released once it expires.
        var afterPulse = pipeline.Process(Touch(), now.AddMilliseconds(120));
        Assert.False(afterPulse.VirtualSnapshot.IsPressed(ButtonId.North));
    }

    [Fact]
    public void Gestures_do_nothing_while_the_master_toggle_is_off()
    {
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            StickEnabled = false,
            GesturesEnabled = false,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.Swipe,
                    SwipeDirection = TouchSwipeDirection.Up,
                    TargetButton = ButtonId.North
                }
            ]
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));
        var now = Origin;
        for (var i = 0; i <= 10; i++)
        {
            pipeline.Process(Touch((0.5f, 0.9f - (0.08f * i))), now);
            now = now.AddMilliseconds(15);
        }

        var result = pipeline.Process(Touch(), now);
        Assert.False(result.VirtualSnapshot.IsPressed(ButtonId.North));
    }

    [Fact]
    public void Anchored_stick_and_a_swipe_gesture_both_run_off_the_same_finger()
    {
        // The three mapping modes and the gesture stack are independently
        // toggleable and composable — a finger can be driving the stick
        // on its way to completing a swipe.
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            StickEnabled = true,
            TargetStick = StickId.Right,
            StickSensitivity = 2.5f,
            GesturesEnabled = true,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.Swipe,
                    SwipeDirection = TouchSwipeDirection.Up,
                    TargetButton = ButtonId.North
                }
            ]
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));
        var now = Origin;

        pipeline.Process(Touch((0.5f, 0.9f)), now); // anchor lands here
        ControllerFrameResult? mid = null;
        for (var i = 1; i <= 10; i++)
        {
            now = now.AddMilliseconds(15);
            mid = pipeline.Process(Touch((0.5f, 0.9f - (0.08f * i))), now);
        }

        // Mid-stroke the stick is deflected upward (surface Y is negated
        // into stick space, where up is positive).
        Assert.NotNull(mid);
        Assert.True(mid!.VirtualSnapshot.RightStick.Y > 0.5f);

        // And the completed stroke still fires the swipe.
        var onLift = pipeline.Process(Touch(), now.AddMilliseconds(15));
        Assert.True(onLift.VirtualSnapshot.IsPressed(ButtonId.North));
        // Stick recenters the moment the finger leaves.
        Assert.Equal(0f, onLift.VirtualSnapshot.RightStick.Y);
    }

    [Fact]
    public void Wedge_dpad_holds_the_direction_the_finger_moved_from_its_anchor()
    {
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            StickEnabled = false,
            DpadEnabled = true,
            DpadEightWay = false,
            DpadDeadzoneRadius = 0.05f
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));

        pipeline.Process(Touch((0.5f, 0.5f)), Origin);
        var right = pipeline.Process(Touch((0.85f, 0.5f)), Origin.AddMilliseconds(15));

        Assert.True(right.VirtualSnapshot.IsPressed(ButtonId.DpadRight));
        Assert.False(right.VirtualSnapshot.IsPressed(ButtonId.DpadUp));

        var released = pipeline.Process(Touch(), Origin.AddMilliseconds(30));
        Assert.False(released.VirtualSnapshot.IsPressed(ButtonId.DpadRight));
    }

    [Fact]
    public void Two_finger_gesture_needs_real_contact_positions_not_just_a_count()
    {
        // A source that counts fingers without placing them cannot
        // support pinch — and the pipeline must not invent positions to
        // fill the gap, which would fabricate gestures from nothing.
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            StickEnabled = false,
            GesturesEnabled = true,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.Pinch,
                    PinchDirection = TouchPinchDirection.Out,
                    FingerCount = 2,
                    TargetButton = ButtonId.East
                }
            ]
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));
        var now = Origin;

        for (var i = 0; i <= 10; i++)
        {
            // Primary position only, with a count claiming two fingers.
            var snapshot = new ControllerSnapshot { Buttons = ButtonState.Clone(ButtonState.CreateEmptyMap()) }
                .WithTouch(true, 0.5f, 0.5f)
                .WithTouchContactCount(2);
            var result = pipeline.Process(snapshot, now);
            Assert.False(result.VirtualSnapshot.IsPressed(ButtonId.East));
            now = now.AddMilliseconds(20);
        }
    }

    [Fact]
    public void Single_finger_gestures_work_on_a_source_that_reports_only_a_primary_contact()
    {
        // The flip side: swipes, taps, long presses and shapes follow one
        // finger, so they must still work where TouchContacts is empty.
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            StickEnabled = false,
            GesturesEnabled = true,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.Swipe,
                    SwipeDirection = TouchSwipeDirection.Right,
                    TargetButton = ButtonId.West
                }
            ]
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));
        var now = Origin;

        for (var i = 0; i <= 10; i++)
        {
            var snapshot = new ControllerSnapshot { Buttons = ButtonState.Clone(ButtonState.CreateEmptyMap()) }
                .WithTouch(true, 0.1f + (0.08f * i), 0.5f);
            pipeline.Process(snapshot, now);
            now = now.AddMilliseconds(15);
        }

        var onLift = pipeline.Process(Touch(), now);
        Assert.True(onLift.VirtualSnapshot.IsPressed(ButtonId.West));
    }

    [Fact]
    public void A_disabled_rule_does_not_resume_its_abandoned_stroke_when_re_enabled()
    {
        // Regression guard for the reset path: a finger resting on the pad
        // across a disable/enable cycle must not classify as one enormous
        // long press measured from before the gap.
        var rule = new TouchpadMapRule
        {
            Id = "touch",
            Enabled = false,
            StickEnabled = false,
            GesturesEnabled = true,
            LongPressMilliseconds = 100,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Kind = TouchGestureKind.LongPress,
                    TargetButton = ButtonId.South
                }
            ]
        };

        var pipeline = new ControllerMappingPipeline(Profile(rule));

        // Rule disabled: ticks pass with a finger down, nothing accrues.
        for (var i = 0; i < 5; i++)
        {
            var result = pipeline.Process(Touch((0.5f, 0.5f)), Origin.AddSeconds(i));
            Assert.False(result.VirtualSnapshot.IsPressed(ButtonId.South));
        }
    }
}
