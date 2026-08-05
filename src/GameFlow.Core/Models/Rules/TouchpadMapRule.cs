using System.Text.Json.Serialization;
using GameFlow.Core.Enums;

namespace GameFlow.Core.Models.Rules;

/// <summary>
/// Touchpad → stick / D-pad / mouse mapping. The moment a finger
/// touches down, that position becomes the anchor for the stick and
/// D-pad modes — like a phone game's virtual joystick appearing
/// wherever you first tap, not pinned to a fixed spot. Mouse mode is
/// different: it reads frame-to-frame movement rather than
/// anchor-relative distance, the way a laptop touchpad actually works
/// (see <see cref="Pipeline.ControllerMappingPipeline"/>'s touchpad
/// pass for exactly why).
///
/// <para>
/// All three are independently toggleable and can run at once — "every
/// toggle saves per slot" in the source description. Mouse output
/// reaches the OS cursor via <c>IMouseOutputWriter</c> in
/// GameFlow.Infrastructure (SendInput on Windows), consuming
/// <see cref="Pipeline.ControllerFrameResult.MouseDeltaX"/>/
/// <see cref="Pipeline.ControllerFrameResult.MouseDeltaY"/> — this rule
/// only computes the delta, it doesn't move anything itself.
/// </para>
/// </summary>
public sealed record TouchpadMapRule : MappingRule
{
    [JsonPropertyName("stickEnabled")]
    public bool StickEnabled { get; init; } = true;

    [JsonPropertyName("targetStick")]
    public StickId TargetStick { get; init; } = StickId.Right;

    /// <summary>How much anchor-relative travel (in normalized touchpad-surface units) maps to full stick deflection. Higher = less physical travel needed for full deflection.</summary>
    [JsonPropertyName("stickSensitivity")]
    public float StickSensitivity { get; init; } = 2.5f;

    [JsonPropertyName("dpadEnabled")]
    public bool DpadEnabled { get; init; }

    /// <summary>Anchor-relative distance (normalized touchpad-surface units) a finger must travel before any D-pad direction registers — avoids jitter near the anchor firing spurious presses.</summary>
    [JsonPropertyName("dpadDeadzoneRadius")]
    public float DpadDeadzoneRadius { get; init; } = 0.05f;

    /// <summary>True: 8-way (diagonals hold two adjacent buttons at once). False: 4-way cardinal only.</summary>
    [JsonPropertyName("dpadEightWay")]
    public bool DpadEightWay { get; init; } = true;

    [JsonPropertyName("mouseEnabled")]
    public bool MouseEnabled { get; init; }

    /// <summary>Per-axis sensitivity multiplier. 1.0 is the pipeline's documented reference scale (see MouseDeltaReferencePixels); higher = more cursor travel per unit of finger movement.</summary>
    [JsonPropertyName("mouseSensitivityX")]
    public float MouseSensitivityX { get; init; } = 1.0f;

    [JsonPropertyName("mouseSensitivityY")]
    public float MouseSensitivityY { get; init; } = 1.0f;

    [JsonPropertyName("invertMouseX")]
    public bool InvertMouseX { get; init; }

    [JsonPropertyName("invertMouseY")]
    public bool InvertMouseY { get; init; }

    // ── Gestures ───────────────────────────────────────────────────
    // Recognition runs off the same contact stream as the three modes
    // above and composes with them: a finger can drive the anchored
    // stick on its way to completing a swipe. The thresholds below are
    // shared by every binding rather than set per binding — they
    // describe the SURFACE and the hand using it ("what counts as a
    // flick on this pad"), not what any individual gesture means, and
    // per-binding copies would drift out of agreement with each other
    // for no benefit.

    [JsonPropertyName("gesturesEnabled")]
    public bool GesturesEnabled { get; init; }

    [JsonPropertyName("gestures")]
    public IReadOnlyList<TouchGestureBinding> Gestures { get; init; } = [];

    /// <summary>
    /// Minimum straight-line travel, in normalized surface units, before
    /// a stroke counts as a swipe rather than a slip. 0.15 is roughly a
    /// sixth of the pad — far enough that resting-thumb drift never
    /// reaches it.
    /// </summary>
    [JsonPropertyName("swipeMinDistance")]
    public float SwipeMinDistance { get; init; } = 0.15f;

    /// <summary>Longest a stroke can last and still be a tap.</summary>
    [JsonPropertyName("tapMaxMs")]
    public int TapMaxMilliseconds { get; init; } = 250;

    /// <summary>
    /// How far a finger may wander and still count as stationary — the
    /// ceiling for both taps and long presses. Below any plausible
    /// swipe, above the jitter a fingertip produces just resting.
    /// </summary>
    [JsonPropertyName("tapMaxTravel")]
    public float TapMaxTravel { get; init; } = 0.05f;

    /// <summary>
    /// Gap allowed between taps before the run resets to one. Also the
    /// reason a double tap emits its single-tap step first: waiting this
    /// long to find out whether a second tap is coming would put a
    /// visible delay on every single tap, so the recognizer reports each
    /// tap as it lands and lets bindings match the count they want.
    /// </summary>
    [JsonPropertyName("multiTapWindowMs")]
    public int MultiTapWindowMilliseconds { get; init; } = 300;

    /// <summary>Dwell time before a stationary finger fires a long press.</summary>
    [JsonPropertyName("longPressMs")]
    public int LongPressMilliseconds { get; init; } = 500;

    /// <summary>
    /// Fractional change in the distance between two fingers that counts
    /// as a pinch — 0.3 means they must converge to 70% of, or separate
    /// to 130% of, their starting gap.
    /// </summary>
    [JsonPropertyName("pinchThreshold")]
    public float PinchThreshold { get; init; } = 0.3f;

    /// <summary>Accumulated rotation, in DEGREES, before a rotate fires.</summary>
    [JsonPropertyName("rotateThresholdDegrees")]
    public float RotateThresholdDegrees { get; init; } = 30f;

    /// <summary>
    /// How closely a stroke must match a shape template, 0..1. 0.8 is
    /// forgiving enough for a hurried figure drawn with a thumb while
    /// still separating the six templates from each other; pushing it
    /// toward 1.0 demands near-tracing.
    /// </summary>
    [JsonPropertyName("shapeMatchThreshold")]
    public float ShapeMatchThreshold { get; init; } = 0.8f;
}
