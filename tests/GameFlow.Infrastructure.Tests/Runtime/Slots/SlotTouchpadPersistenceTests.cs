using System.Text.Json;
using GameFlow.Core.Enums;
using GameFlow.Core.Models.Rules;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Runtime.Slots;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Slots;

/// <summary>
/// The per-slot touchpad configuration is written to the slots file, so
/// it has to survive the same JSON round trip every other slot field
/// does — enums by name, the gesture list intact, and "never configured"
/// distinguishable from "configured with everything off".
/// </summary>
public sealed class SlotTouchpadPersistenceTests
{
    private static ControllerSlot SlotWithTouchpad() => new()
    {
        Id = "slot-1",
        Name = "DualSense",
        Touchpad = new TouchpadMapRule
        {
            StickEnabled = true,
            TargetStick = StickId.Right,
            StickSensitivity = 3.25f,
            DpadEnabled = true,
            DpadEightWay = true,
            DpadDeadzoneRadius = 0.08f,
            MouseEnabled = true,
            MouseSensitivityX = 1.5f,
            MouseSensitivityY = 0.75f,
            InvertMouseY = true,
            GesturesEnabled = true,
            LongPressMilliseconds = 420,
            RotateThresholdDegrees = 45f,
            Gestures =
            [
                new TouchGestureBinding
                {
                    Id = "g1",
                    Kind = TouchGestureKind.Swipe,
                    SwipeDirection = TouchSwipeDirection.DownLeft,
                    EightWay = true,
                    FingerCount = 3,
                    TargetButton = ButtonId.LeftShoulder,
                    HoldMilliseconds = 120
                },
                new TouchGestureBinding
                {
                    Id = "g2",
                    Kind = TouchGestureKind.Shape,
                    Shape = TouchShape.CircleCounterClockwise,
                    TargetButton = ButtonId.Guide
                },
                new TouchGestureBinding
                {
                    Id = "g3",
                    Kind = TouchGestureKind.Pinch,
                    PinchDirection = TouchPinchDirection.Out,
                    FingerCount = 2,
                    Enabled = false,
                    TargetButton = ButtonId.North
                }
            ]
        }
    };

    private static ControllerSlot RoundTrip(ControllerSlot slot)
    {
        var json = JsonSerializer.Serialize(new List<ControllerSlot> { slot }, ProfileJsonOptions.Default);
        var loaded = JsonSerializer.Deserialize<List<ControllerSlot>>(json, ProfileJsonOptions.Default);
        return Assert.Single(loaded!);
    }

    [Fact]
    public void Every_touchpad_setting_survives_the_round_trip()
    {
        var restored = RoundTrip(SlotWithTouchpad());

        Assert.NotNull(restored.Touchpad);
        var touchpad = restored.Touchpad!;

        Assert.True(touchpad.StickEnabled);
        Assert.Equal(StickId.Right, touchpad.TargetStick);
        Assert.Equal(3.25f, touchpad.StickSensitivity);

        Assert.True(touchpad.DpadEnabled);
        Assert.True(touchpad.DpadEightWay);
        Assert.Equal(0.08f, touchpad.DpadDeadzoneRadius);

        Assert.True(touchpad.MouseEnabled);
        Assert.Equal(1.5f, touchpad.MouseSensitivityX);
        Assert.Equal(0.75f, touchpad.MouseSensitivityY);
        Assert.False(touchpad.InvertMouseX);
        Assert.True(touchpad.InvertMouseY);

        Assert.True(touchpad.GesturesEnabled);
        Assert.Equal(420, touchpad.LongPressMilliseconds);
        Assert.Equal(45f, touchpad.RotateThresholdDegrees);
    }

    [Fact]
    public void Gesture_bindings_survive_with_their_discriminators()
    {
        var touchpad = RoundTrip(SlotWithTouchpad()).Touchpad!;
        Assert.Equal(3, touchpad.Gestures.Count);

        var swipe = touchpad.Gestures[0];
        Assert.Equal(TouchGestureKind.Swipe, swipe.Kind);
        Assert.Equal(TouchSwipeDirection.DownLeft, swipe.SwipeDirection);
        Assert.True(swipe.EightWay);
        Assert.Equal(3, swipe.FingerCount);
        Assert.Equal(ButtonId.LeftShoulder, swipe.TargetButton);
        Assert.Equal(120, swipe.HoldMilliseconds);

        var shape = touchpad.Gestures[1];
        Assert.Equal(TouchGestureKind.Shape, shape.Kind);
        Assert.Equal(TouchShape.CircleCounterClockwise, shape.Shape);

        // A disabled binding must come back disabled, not silently
        // re-enabled by a default.
        var pinch = touchpad.Gestures[2];
        Assert.Equal(TouchGestureKind.Pinch, pinch.Kind);
        Assert.Equal(TouchPinchDirection.Out, pinch.PinchDirection);
        Assert.False(pinch.Enabled);
    }

    [Fact]
    public void Enums_are_written_by_name_so_reordering_them_cannot_corrupt_saved_slots()
    {
        var json = JsonSerializer.Serialize(
            new List<ControllerSlot> { SlotWithTouchpad() }, ProfileJsonOptions.Default);

        Assert.Contains("DownLeft", json, StringComparison.Ordinal);
        Assert.Contains("CircleCounterClockwise", json, StringComparison.Ordinal);
        Assert.Contains("LeftShoulder", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slot_that_was_never_configured_round_trips_as_null()
    {
        // Null is meaningfully different from an all-defaults rule: it is
        // what tells SlotRuntime to contribute no touchpad rule at all.
        var restored = RoundTrip(new ControllerSlot { Id = "slot-2" });
        Assert.Null(restored.Touchpad);
    }

    [Fact]
    public void Older_slot_files_without_a_touchpad_field_still_load()
    {
        const string LegacyJson = """
        [
          {
            "id": "slot-legacy",
            "index": 0,
            "name": "Existing Controller",
            "enabled": true,
            "inputDeviceIds": ["sdl-gamepad-abc"],
            "profileIds": ["default"]
          }
        ]
        """;

        var loaded = JsonSerializer.Deserialize<List<ControllerSlot>>(LegacyJson, ProfileJsonOptions.Default);
        var slot = Assert.Single(loaded!);

        Assert.Equal("Existing Controller", slot.Name);
        Assert.Null(slot.Touchpad);
    }

    [Fact]
    public void Clone_carries_the_touchpad_configuration()
    {
        // Slot duplication goes through Clone, and a duplicate that
        // silently lost its touchpad settings would be a confusing
        // half-copy.
        var clone = SlotWithTouchpad().Clone();

        Assert.NotNull(clone.Touchpad);
        Assert.Equal(3, clone.Touchpad!.Gestures.Count);
        Assert.Equal(StickId.Right, clone.Touchpad.TargetStick);
    }
}
