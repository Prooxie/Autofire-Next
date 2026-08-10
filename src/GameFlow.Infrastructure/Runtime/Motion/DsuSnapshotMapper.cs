using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime.Motion;

/// <summary>
/// Translates a GameFlow <see cref="ControllerSnapshot"/> into the DSU
/// wire representation.
///
/// <para>
/// Kept separate from <see cref="DsuProtocol"/> (which only knows bytes)
/// and from <see cref="DsuServer"/> (which only knows sockets) so the
/// interesting part — unit conversion and button correspondence — is a
/// pure function that tests can pin without a UDP socket in sight.
/// </para>
/// </summary>
public static class DsuSnapshotMapper
{
    /// <summary>
    /// Radians per second to degrees per second.
    ///
    /// <para>
    /// This conversion is load-bearing and easy to skip.
    /// <see cref="ControllerSnapshot.GyroPitch"/> and friends are in
    /// RADIANS/s (SDL's contract), while DSU's motion fields are
    /// DEGREES/s — the unit every consuming emulator assumes. Sending
    /// radians is not a subtly wrong scale, it is wrong by a factor of
    /// ~57.3, which reads in-game as gyro aiming that barely responds.
    /// </para>
    /// </summary>
    private const float RadiansToDegrees = 57.295779513f;

    /// <summary>
    /// Metres per second squared to g. Same hazard as
    /// <see cref="RadiansToDegrees"/>: the snapshot is in m/s² and DSU
    /// expects g, so a pad at rest must report ≈1.0 on the down axis,
    /// not ≈9.81. Standard gravity, matching the constant SDL uses.
    /// </summary>
    private const float StandardGravity = 9.80665f;

    /// <summary>Neutral byte for a centred stick axis. 128, not 127 — a centred stick must land on the same value both axes agree on.</summary>
    private const byte StickCentre = 128;

    /// <summary>
    /// Builds the payload for one slot.
    /// </summary>
    /// <param name="snapshot">The slot's current physical snapshot.</param>
    /// <param name="slot">DSU pad index, 0-3.</param>
    /// <param name="packetNumber">Sequence number from <see cref="DsuPacketCounter"/>.</param>
    /// <param name="timestampMicroseconds">Motion timestamp in microseconds.</param>
    public static DsuControllerData ToControllerData(
        ControllerSnapshot snapshot,
        int slot,
        uint packetNumber,
        ulong timestampMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var info = new DsuControllerInfo
        {
            Slot = (byte)Math.Clamp(slot, 0, DsuProtocol.MaxSlots - 1),
            State = DsuSlotState.Connected,

            // A client that sees PartialGyro may decline to offer motion
            // options at all, so this has to follow the pad's actual
            // sensor rather than being optimistic.
            Model = snapshot.HasGyro ? DsuDeviceModel.FullGyro : DsuDeviceModel.PartialGyro,

            // GameFlow does not track how a pad is attached, and guessing
            // would be worse than declining to answer: clients treat this
            // as informational, and NotApplicable is the defined "unknown".
            ConnectionType = DsuConnectionType.NotApplicable,
            MacAddress = BuildStableMac(slot),

            // ControllerSnapshot carries no battery level. NotApplicable is
            // the honest answer; reporting Full would put a wrong charge
            // reading in front of the user in emulators that surface it.
            Battery = DsuBatteryStatus.NotApplicable
        };

        return new DsuControllerData
        {
            Device = info,
            IsConnected = true,
            PacketNumber = packetNumber,
            Buttons = MapButtons(snapshot.Buttons),

            PsButton = snapshot.IsPressed(ButtonId.Guide),
            TouchButton = snapshot.IsPressed(ButtonId.Touchpad),

            LeftStickX = ToStickByte(snapshot.LeftStick.X),
            LeftStickY = ToStickByte(snapshot.LeftStick.Y),
            RightStickX = ToStickByte(snapshot.RightStick.X),
            RightStickY = ToStickByte(snapshot.RightStick.Y),

            // The analog block. GameFlow tracks the D-pad and face buttons
            // as digital only, so these report the two extremes rather
            // than inventing intermediate pressure the hardware never gave
            // us. The triggers are genuinely analog and pass through.
            AnalogDpadUp = ToDigitalByte(snapshot.IsPressed(ButtonId.DpadUp)),
            AnalogDpadDown = ToDigitalByte(snapshot.IsPressed(ButtonId.DpadDown)),
            AnalogDpadLeft = ToDigitalByte(snapshot.IsPressed(ButtonId.DpadLeft)),
            AnalogDpadRight = ToDigitalByte(snapshot.IsPressed(ButtonId.DpadRight)),

            AnalogCross = ToDigitalByte(snapshot.IsPressed(ButtonId.South)),
            AnalogCircle = ToDigitalByte(snapshot.IsPressed(ButtonId.East)),
            AnalogSquare = ToDigitalByte(snapshot.IsPressed(ButtonId.West)),
            AnalogTriangle = ToDigitalByte(snapshot.IsPressed(ButtonId.North)),

            AnalogL1 = ToDigitalByte(snapshot.IsPressed(ButtonId.LeftShoulder)),
            AnalogR1 = ToDigitalByte(snapshot.IsPressed(ButtonId.RightShoulder)),
            AnalogL2 = ToTriggerByte(snapshot.LeftTrigger),
            AnalogR2 = ToTriggerByte(snapshot.RightTrigger),

            FirstTouch = ToTouch(snapshot, 0),
            SecondTouch = ToTouch(snapshot, 1),

            MotionTimestampMicroseconds = timestampMicroseconds,

            // Sensor values are only meaningful when the pad actually has
            // the sensor. Forwarding zeros from a pad with no gyro would be
            // indistinguishable from a pad held perfectly still, and the
            // snapshot goes to the trouble of tracking that difference.
            AccelX = snapshot.HasGyro ? snapshot.AccelX / StandardGravity : 0f,
            AccelY = snapshot.HasGyro ? snapshot.AccelY / StandardGravity : 0f,
            AccelZ = snapshot.HasGyro ? snapshot.AccelZ / StandardGravity : 0f,
            GyroPitch = snapshot.HasGyro ? snapshot.GyroPitch * RadiansToDegrees : 0f,
            GyroYaw = snapshot.HasGyro ? snapshot.GyroYaw * RadiansToDegrees : 0f,
            GyroRoll = snapshot.HasGyro ? snapshot.GyroRoll * RadiansToDegrees : 0f
        };
    }

    /// <summary>
    /// The payload for a slot that has no device, so a client that asked
    /// about it gets a definite "nothing here" rather than silence.
    /// </summary>
    public static DsuControllerData Disconnected(int slot)
    {
        return new DsuControllerData
        {
            Device = new DsuControllerInfo
            {
                Slot = (byte)Math.Clamp(slot, 0, DsuProtocol.MaxSlots - 1),
                State = DsuSlotState.Disconnected,
                Model = DsuDeviceModel.NotApplicable,
                ConnectionType = DsuConnectionType.NotApplicable,
                MacAddress = 0,
                Battery = DsuBatteryStatus.NotApplicable
            },
            IsConnected = false,

            // Sticks still have to read centred. A disconnected pad
            // reporting 0 here would be a stick slammed to the corner.
            LeftStickX = StickCentre,
            LeftStickY = StickCentre,
            RightStickX = StickCentre,
            RightStickY = StickCentre
        };
    }

    /// <summary>
    /// Maps GameFlow's <see cref="ButtonId"/> set onto the DSU button
    /// word. The names differ because DSU speaks DualShock; the
    /// correspondence is by POSITION (South = the bottom face button =
    /// Cross), which is what a player pressing it expects regardless of
    /// what is printed on their pad.
    /// </summary>
    public static DsuButtons MapButtons(IReadOnlyDictionary<ButtonId, bool>? buttons)
    {
        if (buttons is null)
        {
            return DsuButtons.None;
        }

        var result = DsuButtons.None;

        if (IsDown(buttons, ButtonId.South)) result |= DsuButtons.Cross;
        if (IsDown(buttons, ButtonId.East)) result |= DsuButtons.Circle;
        if (IsDown(buttons, ButtonId.West)) result |= DsuButtons.Square;
        if (IsDown(buttons, ButtonId.North)) result |= DsuButtons.Triangle;

        if (IsDown(buttons, ButtonId.LeftShoulder)) result |= DsuButtons.L1;
        if (IsDown(buttons, ButtonId.RightShoulder)) result |= DsuButtons.R1;
        if (IsDown(buttons, ButtonId.LeftTriggerButton)) result |= DsuButtons.L2;
        if (IsDown(buttons, ButtonId.RightTriggerButton)) result |= DsuButtons.R2;

        if (IsDown(buttons, ButtonId.Back)) result |= DsuButtons.Share;
        if (IsDown(buttons, ButtonId.Start)) result |= DsuButtons.Options;
        if (IsDown(buttons, ButtonId.LeftStick)) result |= DsuButtons.LeftStick;
        if (IsDown(buttons, ButtonId.RightStick)) result |= DsuButtons.RightStick;

        if (IsDown(buttons, ButtonId.DpadUp)) result |= DsuButtons.DpadUp;
        if (IsDown(buttons, ButtonId.DpadDown)) result |= DsuButtons.DpadDown;
        if (IsDown(buttons, ButtonId.DpadLeft)) result |= DsuButtons.DpadLeft;
        if (IsDown(buttons, ButtonId.DpadRight)) result |= DsuButtons.DpadRight;

        // Guide and Touchpad are deliberately absent: the protocol carries
        // them as their own bytes, not as bits in this word.
        return result;
    }

    /// <summary>
    /// Converts a signed axis to the protocol's unsigned byte.
    /// GameFlow's stick Y is already +up (SdlUnifiedInputSource negates
    /// SDL's +down at the source), which is the same direction DSU uses,
    /// so neither axis is flipped here.
    /// </summary>
    public static byte ToStickByte(float value)
    {
        if (float.IsNaN(value))
        {
            return StickCentre;
        }

        var clamped = Math.Clamp(value, -1f, 1f);

        // Scale by 127 and offset, so -1 -> 1, 0 -> 128, +1 -> 255. The
        // one-off at the bottom is inherent to mapping a symmetric range
        // onto 256 values with a whole-number centre, and losing the
        // centre would be the worse trade — an off-centre neutral is
        // stick drift the user cannot tune out.
        return (byte)Math.Clamp((int)MathF.Round((clamped * 127f) + StickCentre), 0, 255);
    }

    /// <summary>Converts a 0..1 trigger to the protocol's 0..255 byte.</summary>
    public static byte ToTriggerByte(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
    }

    private static byte ToDigitalByte(bool pressed) => pressed ? (byte)255 : (byte)0;

    private static bool IsDown(IReadOnlyDictionary<ButtonId, bool> buttons, ButtonId id) =>
        buttons.TryGetValue(id, out var down) && down;

    /// <summary>
    /// Projects one finger onto the protocol's touch slot. DSU carries
    /// touch positions in DS4 surface coordinates (1920x942), so the
    /// snapshot's normalized 0..1 has to be scaled up rather than passed
    /// through — a client reading 0..1 here would see every touch pinned
    /// to the top-left corner.
    /// </summary>
    private static DsuTouch ToTouch(ControllerSnapshot snapshot, int index)
    {
        const float SurfaceWidth = 1920f;
        const float SurfaceHeight = 942f;

        if (index < snapshot.TouchContacts.Count)
        {
            var contact = snapshot.TouchContacts[index];
            return new DsuTouch(
                IsActive: true,
                Id: (byte)(contact.FingerIndex & 0x7F),
                X: (ushort)Math.Clamp((int)MathF.Round(contact.X * SurfaceWidth), 0, (int)SurfaceWidth),
                Y: (ushort)Math.Clamp((int)MathF.Round(contact.Y * SurfaceHeight), 0, (int)SurfaceHeight));
        }

        // Sources that report a primary contact without per-finger detail
        // still populate TouchX/TouchY, so the first slot can be filled
        // from those rather than reporting no touch at all.
        if (index == 0 && snapshot.TouchDown)
        {
            return new DsuTouch(
                IsActive: true,
                Id: 0,
                X: (ushort)Math.Clamp((int)MathF.Round(snapshot.TouchX * SurfaceWidth), 0, (int)SurfaceWidth),
                Y: (ushort)Math.Clamp((int)MathF.Round(snapshot.TouchY * SurfaceHeight), 0, (int)SurfaceHeight));
        }

        return default;
    }

    /// <summary>
    /// Synthesises a stable MAC for a slot. Clients key pads by MAC and
    /// expect it to survive a restart, but GameFlow slots are not network
    /// devices and have none. The locally-administered bit (0x02) is set
    /// so the synthetic address can never collide with real hardware.
    /// </summary>
    private static ulong BuildStableMac(int slot)
    {
        // "GF" in the middle octets makes these recognisable in an
        // emulator's device list rather than looking like random noise.
        return 0x02_47_46_00_00_00UL | (ulong)(uint)Math.Clamp(slot, 0, DsuProtocol.MaxSlots - 1);
    }
}
