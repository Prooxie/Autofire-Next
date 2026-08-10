using GameFlow.Core.Enums;

namespace GameFlow.Core.Models;

public sealed record ControllerSnapshot
{
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>
    /// USB vendor id of the connected hardware (or <c>0</c> when not
    /// applicable / not yet known). Populated by hardware-aware input
    /// sources so the visual layer can pick a controller skin
    /// deterministically by VID/PID rather than by parsing the
    /// device-name string.
    /// </summary>
    public ushort VendorId { get; init; }

    /// <summary>
    /// USB product id. See <see cref="VendorId"/>.
    /// </summary>
    public ushort ProductId { get; init; }

    public StickVector LeftStick { get; init; } = StickVector.Zero;
    public StickVector RightStick { get; init; } = StickVector.Zero;
    public float LeftTrigger { get; init; }
    public float RightTrigger { get; init; }
    /// <summary>
    /// Angular velocity in RADIANS PER SECOND, positive =
    /// counter-clockwise (right-hand rule). Axis convention, with the
    /// controller held in front of you: X right, Y up, Z toward you —
    /// so Pitch is rotation around X (nose tilting up/down), Yaw around
    /// Y (turning left/right), Roll around Z (tilting side to side).
    /// These units and signs are SDL's documented contract, verified
    /// against upstream SDL_sensor.h rather than assumed; keeping the
    /// raw values here (instead of pre-normalizing) means the DSU /
    /// Cemuhook motion server can emit them unmodified later.
    /// Zero on pads with no gyro.
    /// </summary>
    public float GyroPitch { get; init; }
    public float GyroYaw { get; init; }
    public float GyroRoll { get; init; }

    /// <summary>
    /// Acceleration in m/s², same axis convention as the gyro above. At
    /// rest this reads the reaction to gravity (magnitude ≈ 9.81), which
    /// is what makes it usable as an "which way is down" reference for
    /// the Player/World gyro reference frames. Zero on pads with no
    /// accelerometer.
    /// </summary>
    public float AccelX { get; init; }
    public float AccelY { get; init; }
    public float AccelZ { get; init; }

    /// <summary>True when the source pad actually reports gyro data — distinguishes "not moving" from "no sensor", which read identically as all-zero otherwise.</summary>
    public bool HasGyro { get; init; }

    /// <summary>
    /// Raw Windows virtual-key codes currently held, for sources that
    /// are actual keyboards. Empty for gamepads.
    ///
    /// <para>
    /// Kept ALONGSIDE <see cref="Buttons"/> rather than replacing it: the
    /// mapping pipeline works in <see cref="ButtonId"/> terms (a keyboard
    /// mapped to a virtual pad), but a keyboard has ~104 keys and
    /// ButtonId has ~24 values, so collapsing to ButtonId throws away
    /// most of the keyboard. A full-layout keyboard visual needs the
    /// unreduced set, which is what this carries.
    /// </para>
    /// </summary>
    public IReadOnlySet<int> PressedKeys { get; init; } = EmptyKeys;

    private static readonly IReadOnlySet<int> EmptyKeys = new HashSet<int>();

    public int TouchContactCount { get; init; }

    /// <summary>
    /// Primary (first active) finger position on the touchpad surface,
    /// normalized 0..1 per SDL's own convention (matches how
    /// <c>SdlInterop</c> in GameFlow.Infrastructure already reports it;
    /// X: 0=left, Y: 0=top) — plain text, not a <c>cref</c>: that type
    /// lives in GameFlow.Infrastructure, which Core has no reference to
    /// (the dependency runs the other way), so a cref here could never
    /// resolve. Meaningless when <see cref="TouchDown"/> is false.
    /// </summary>
    public float TouchX { get; init; }
    public float TouchY { get; init; }
    public bool TouchDown { get; init; }

    /// <summary>
    /// Every finger currently on the surface, ascending by
    /// <see cref="TouchContact.FingerIndex"/>. Empty on sources that
    /// report only a primary contact (or none at all), which is why
    /// <see cref="TouchContactCount"/> stays the authority on "how many
    /// fingers" — a source can know the count without being able to
    /// place each finger, and single-finger mapping (anchor stick, wedge
    /// D-pad, mouse) only ever needed <see cref="TouchX"/>/<see cref="TouchY"/>.
    ///
    /// <para>
    /// Multi-finger gestures — pinch, rotate, and anything counting two
    /// to five fingers — need real per-finger positions and are simply
    /// inert when this is empty, rather than guessing from the count.
    /// </para>
    /// </summary>
    public IReadOnlyList<TouchContact> TouchContacts { get; init; } = [];
    public IReadOnlyDictionary<ButtonId, bool> Buttons { get; init; } = ButtonState.CreateEmptyMap();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public static ControllerSnapshot Empty(string? deviceName = null)
    {
        return new()
        {
            DeviceName = deviceName ?? string.Empty,
            Buttons = ButtonState.CreateEmptyMap(),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public StickVector GetStick(StickId stickId)
    {
        return stickId == StickId.Left ? LeftStick : RightStick;
    }

    public bool IsPressed(ButtonId buttonId)
    {
        return Buttons.TryGetValue(buttonId, out var isPressed) && isPressed;
    }

    public ControllerSnapshot WithStick(StickId stickId, StickVector value)
    {
        return stickId == StickId.Left
                ? this with { LeftStick = value.Clamp() }
                : this with { RightStick = value.Clamp() };
    }

    public ControllerSnapshot WithButtons(IReadOnlyDictionary<ButtonId, bool> buttons)
    {
        return this with { Buttons = buttons };
    }

    public ControllerSnapshot WithTriggers(float leftTrigger, float rightTrigger)
    {
        return this with
        {
            LeftTrigger = Math.Clamp(leftTrigger, 0f, 1f),
            RightTrigger = Math.Clamp(rightTrigger, 0f, 1f)
        };
    }

    public ControllerSnapshot WithTouchContactCount(int touchContactCount)
    {
        return this with { TouchContactCount = Math.Max(0, touchContactCount) };
    }

    public ControllerSnapshot WithTouch(bool down, float x, float y)
    {
        return this with { TouchDown = down, TouchX = Math.Clamp(x, 0f, 1f), TouchY = Math.Clamp(y, 0f, 1f) };
    }

    /// <summary>
    /// Sets the full contact list and derives every legacy touch field
    /// from it — count, and the primary finger's down/X/Y. Deriving
    /// rather than asking callers to set both keeps the two views from
    /// ever disagreeing (a snapshot claiming three contacts while
    /// <see cref="TouchDown"/> is false would be nonsense that the
    /// mapping pipeline has no way to resolve).
    ///
    /// <para>The "primary" finger is the lowest
    /// <see cref="TouchContact.FingerIndex"/>, matching the recognizer's
    /// own choice — see <c>TouchGestureEngine</c>.</para>
    /// </summary>
    public ControllerSnapshot WithTouchContacts(IReadOnlyList<TouchContact>? contacts)
    {
        if (contacts is null || contacts.Count == 0)
        {
            return this with
            {
                TouchContacts = [],
                TouchContactCount = 0,
                TouchDown = false
            };
        }

        var ordered = contacts.Count == 1
            ? contacts
            : [.. contacts.OrderBy(c => c.FingerIndex)];
        var primary = ordered[0];

        return this with
        {
            TouchContacts = ordered,
            TouchContactCount = ordered.Count,
            TouchDown = true,
            TouchX = Math.Clamp(primary.X, 0f, 1f),
            TouchY = Math.Clamp(primary.Y, 0f, 1f)
        };
    }

    /// <summary>Sets the raw motion values. Deliberately unclamped — angular velocity has no natural bound, and clamping here would quietly cap fast flicks.</summary>
    public ControllerSnapshot WithMotion(float gyroPitch, float gyroYaw, float gyroRoll, float accelX, float accelY, float accelZ)
    {
        return this with
        {
            GyroPitch = gyroPitch,
            GyroYaw = gyroYaw,
            GyroRoll = gyroRoll,
            AccelX = accelX,
            AccelY = accelY,
            AccelZ = accelZ,
            HasGyro = true
        };
    }
}
