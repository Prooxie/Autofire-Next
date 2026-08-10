namespace GameFlow.Infrastructure.Runtime.Motion;

/// <summary>
/// The three CemuhookUDP message types.
///
/// <para>
/// The numeric values are the wire contract shared with Cemu, Dolphin,
/// Yuzu and Ryujinx — they are not ours to renumber, and unlike an
/// internal enum this one can never be "tidied up" without breaking
/// every emulator on the network.
/// </para>
/// </summary>
public enum DsuMessageType : uint
{
    /// <summary>Client asks which protocol revision the server speaks.</summary>
    ProtocolVersion = 0x100000,

    /// <summary>Client asks which of the pad slots are populated.</summary>
    ControllerInfo = 0x100001,

    /// <summary>Client subscribes to — and the server then streams — live pad state.</summary>
    ControllerData = 0x100002
}

/// <summary>Whether a slot is empty, held, or actually reporting.</summary>
public enum DsuSlotState : byte
{
    /// <summary>Nothing in this slot.</summary>
    Disconnected = 0,

    /// <summary>Slot is claimed but not yet producing input.</summary>
    Reserved = 1,

    /// <summary>Slot has a live pad behind it.</summary>
    Connected = 2
}

/// <summary>
/// How much motion hardware the pad has. Clients gate their gyro UI on
/// this: a pad reported as <see cref="NotApplicable"/> will have its
/// motion values ignored no matter what the packet carries.
/// </summary>
public enum DsuDeviceModel : byte
{
    /// <summary>No motion hardware at all.</summary>
    NotApplicable = 0,

    /// <summary>Accelerometer only, or a partial gyro (DS3-class).</summary>
    PartialGyro = 1,

    /// <summary>Full six-axis motion (DS4-class).</summary>
    FullGyro = 2
}

/// <summary>How the pad is attached to the host.</summary>
public enum DsuConnectionType : byte
{
    /// <summary>Unknown or not meaningful for this source.</summary>
    NotApplicable = 0,

    /// <summary>Wired.</summary>
    Usb = 1,

    /// <summary>Wireless.</summary>
    Bluetooth = 2
}

/// <summary>
/// Battery level as the protocol spells it. The values are sparse and
/// deliberately non-sequential at the top end (charging states live at
/// 0xEE/0xEF), so this is a lookup table rather than a scale.
/// </summary>
public enum DsuBatteryStatus : byte
{
    /// <summary>Unknown, or a pad with no battery (wired).</summary>
    NotApplicable = 0x00,

    /// <summary>About to die.</summary>
    Dying = 0x01,

    /// <summary>Low.</summary>
    Low = 0x02,

    /// <summary>Medium.</summary>
    Medium = 0x03,

    /// <summary>High.</summary>
    High = 0x04,

    /// <summary>Full.</summary>
    Full = 0x05,

    /// <summary>Currently charging.</summary>
    Charging = 0xEE,

    /// <summary>Charged while still on the cable.</summary>
    Charged = 0xEF
}

/// <summary>
/// How a client picked the pads it wants streamed. A request with no
/// flags set (<see cref="AllPads"/>) is a subscription to everything,
/// which is what most emulators send.
/// </summary>
[Flags]
public enum DsuRegistrationFlags : byte
{
    /// <summary>Subscribe to every slot the server has.</summary>
    AllPads = 0x00,

    /// <summary>Subscribe to the single slot id carried in the request.</summary>
    SlotBased = 0x01,

    /// <summary>Subscribe to whichever slot holds the MAC carried in the request.</summary>
    MacBased = 0x02
}

/// <summary>
/// The digital button block, laid out so that writing this as a
/// little-endian <see cref="ushort"/> produces the protocol's two
/// button bytes in order: the low byte is wire byte 1, the high byte is
/// wire byte 2. Keeping that relationship means the encoder writes one
/// value instead of splitting bits by hand, but it also means <b>the
/// numeric values below are the wire format</b> and cannot be
/// reassigned for convenience.
///
/// <para>
/// The names are the DualShock ones because that is the vocabulary the
/// protocol itself uses; the face-button comments give the
/// <c>ButtonId</c> equivalent so the mapping layer has something to
/// aim at.
/// </para>
/// </summary>
[Flags]
public enum DsuButtons : ushort
{
    /// <summary>Nothing held.</summary>
    None = 0,

    // ---- wire byte 1 (low byte) ----

    /// <summary>Share / Select / Back.</summary>
    Share = 0x0001,

    /// <summary>Left stick click (L3).</summary>
    LeftStick = 0x0002,

    /// <summary>Right stick click (R3).</summary>
    RightStick = 0x0004,

    /// <summary>Options / Start.</summary>
    Options = 0x0008,

    /// <summary>D-Pad up.</summary>
    DpadUp = 0x0010,

    /// <summary>D-Pad right.</summary>
    DpadRight = 0x0020,

    /// <summary>D-Pad down.</summary>
    DpadDown = 0x0040,

    /// <summary>D-Pad left.</summary>
    DpadLeft = 0x0080,

    // ---- wire byte 2 (high byte) ----

    /// <summary>Left trigger, as a digital press.</summary>
    L2 = 0x0100,

    /// <summary>Right trigger, as a digital press.</summary>
    R2 = 0x0200,

    /// <summary>Left shoulder.</summary>
    L1 = 0x0400,

    /// <summary>Right shoulder.</summary>
    R1 = 0x0800,

    /// <summary>Square — the west face button.</summary>
    Square = 0x1000,

    /// <summary>Cross — the south face button.</summary>
    Cross = 0x2000,

    /// <summary>Circle — the east face button.</summary>
    Circle = 0x4000,

    /// <summary>Triangle — the north face button.</summary>
    Triangle = 0x8000
}

/// <summary>
/// One touchpad contact. The protocol carries exactly two of these per
/// packet — a hard limit of the format, not a product decision, so a
/// richer multi-touch snapshot has to be reduced to two fingers before
/// it can go on the wire.
/// </summary>
/// <param name="IsActive">Whether this finger is currently down.</param>
/// <param name="Id">Contact id, so a client can track one finger across packets.</param>
/// <param name="X">Raw horizontal position in the source pad's own units (DS4 touchpad is 0..1919).</param>
/// <param name="Y">Raw vertical position in the source pad's own units (DS4 touchpad is 0..941).</param>
public readonly record struct DsuTouch(bool IsActive, byte Id, ushort X, ushort Y);

/// <summary>
/// The eleven-byte device descriptor that opens both the controller-info
/// response and every controller-data packet. It is one type rather than
/// two because the protocol genuinely reuses the same block — keeping
/// them separate invites the two copies to drift.
/// </summary>
public readonly record struct DsuControllerInfo
{
    /// <summary>Slot id, 0-3.</summary>
    public byte Slot { get; init; }

    /// <summary>Whether the slot is empty, reserved, or live.</summary>
    public DsuSlotState State { get; init; }

    /// <summary>How much motion hardware the pad has.</summary>
    public DsuDeviceModel Model { get; init; }

    /// <summary>USB / Bluetooth / unknown.</summary>
    public DsuConnectionType ConnectionType { get; init; }

    /// <summary>
    /// Pad MAC address in the low 48 bits. Held as a <see cref="ulong"/>
    /// rather than a six-byte buffer because nothing in the codec ever
    /// needs the octets individually, and an integer is trivially
    /// comparable — MAC-based subscriptions are a lookup by this value.
    /// </summary>
    public ulong MacAddress { get; init; }

    /// <summary>Battery level.</summary>
    public DsuBatteryStatus Battery { get; init; }
}

/// <summary>
/// One controller-data packet's worth of pad state.
///
/// <para>
/// <b>Units here are the protocol's, not the pipeline's.</b> DSU wants
/// acceleration in <c>g</c> and angular velocity in <c>degrees/second</c>,
/// whereas <c>ControllerSnapshot</c> carries SDL's m/s² and radians/second.
/// The conversion belongs to whatever fills this struct; doing it inside
/// the codec would make the codec untestable against the wire format it
/// is supposed to implement.
/// </para>
/// </summary>
public readonly record struct DsuControllerData
{
    /// <summary>The shared device descriptor for this slot.</summary>
    public DsuControllerInfo Device { get; init; }

    /// <summary>Whether the pad is presently connected.</summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// Per-slot sequence number. Clients use gaps in this to detect
    /// dropped datagrams, so it has to advance once per packet sent for
    /// this slot — see <see cref="DsuPacketCounter"/>.
    /// </summary>
    public uint PacketNumber { get; init; }

    /// <summary>Digital buttons.</summary>
    public DsuButtons Buttons { get; init; }

    /// <summary>PS / Guide button. Carried in its own byte, outside the button bitmask.</summary>
    public bool PsButton { get; init; }

    /// <summary>Touchpad click. Also its own byte, separate from the touch contacts.</summary>
    public bool TouchButton { get; init; }

    /// <summary>Left stick X, 0..255 with 128 centred.</summary>
    public byte LeftStickX { get; init; }

    /// <summary>Left stick Y, 0..255 with 128 centred. DSU points this axis UP, opposite to SDL.</summary>
    public byte LeftStickY { get; init; }

    /// <summary>Right stick X, 0..255 with 128 centred.</summary>
    public byte RightStickX { get; init; }

    /// <summary>Right stick Y, 0..255 with 128 centred. Same inversion as <see cref="LeftStickY"/>.</summary>
    public byte RightStickY { get; init; }

    /// <summary>Analog pressure for D-Pad left.</summary>
    public byte AnalogDpadLeft { get; init; }

    /// <summary>Analog pressure for D-Pad down.</summary>
    public byte AnalogDpadDown { get; init; }

    /// <summary>Analog pressure for D-Pad right.</summary>
    public byte AnalogDpadRight { get; init; }

    /// <summary>Analog pressure for D-Pad up.</summary>
    public byte AnalogDpadUp { get; init; }

    /// <summary>Analog pressure for Triangle (north).</summary>
    public byte AnalogTriangle { get; init; }

    /// <summary>Analog pressure for Circle (east).</summary>
    public byte AnalogCircle { get; init; }

    /// <summary>Analog pressure for Cross (south).</summary>
    public byte AnalogCross { get; init; }

    /// <summary>Analog pressure for Square (west).</summary>
    public byte AnalogSquare { get; init; }

    /// <summary>Analog pressure for R1.</summary>
    public byte AnalogR1 { get; init; }

    /// <summary>Analog pressure for L1.</summary>
    public byte AnalogL1 { get; init; }

    /// <summary>Right trigger travel, 0..255.</summary>
    public byte AnalogR2 { get; init; }

    /// <summary>Left trigger travel, 0..255.</summary>
    public byte AnalogL2 { get; init; }

    /// <summary>First touchpad contact.</summary>
    public DsuTouch FirstTouch { get; init; }

    /// <summary>Second touchpad contact.</summary>
    public DsuTouch SecondTouch { get; init; }

    /// <summary>
    /// Motion sample time in MICROSECONDS. Clients differentiate against
    /// this to recover a sample interval, so it must be a real clock
    /// reading rather than a packet counter scaled by the send rate:
    /// at 1000 Hz the jitter that assumption hides is the whole signal.
    /// </summary>
    public ulong MotionTimestampMicroseconds { get; init; }

    /// <summary>Acceleration along X in g.</summary>
    public float AccelX { get; init; }

    /// <summary>Acceleration along Y in g.</summary>
    public float AccelY { get; init; }

    /// <summary>Acceleration along Z in g.</summary>
    public float AccelZ { get; init; }

    /// <summary>Angular velocity around the pitch axis in degrees/second.</summary>
    public float GyroPitch { get; init; }

    /// <summary>Angular velocity around the yaw axis in degrees/second.</summary>
    public float GyroYaw { get; init; }

    /// <summary>Angular velocity around the roll axis in degrees/second.</summary>
    public float GyroRoll { get; init; }
}

/// <summary>Header fields common to every DSU packet, once validated.</summary>
/// <param name="ProtocolVersion">Revision the sender claims to speak.</param>
/// <param name="PayloadLength">Declared payload length, message type included.</param>
/// <param name="Checksum">The CRC-32 as it appeared on the wire (already verified).</param>
/// <param name="SenderId">Server id on responses, client id on requests.</param>
/// <param name="MessageType">
/// Raw message type. Kept as a <see cref="uint"/> rather than a
/// <see cref="DsuMessageType"/> because an unrecognised value is a
/// perfectly ordinary thing to receive from the network and casting it
/// into the enum would quietly manufacture an undefined member.
/// </param>
public readonly record struct DsuHeader(
    ushort ProtocolVersion,
    ushort PayloadLength,
    uint Checksum,
    uint SenderId,
    uint MessageType)
{
    /// <summary>Total packet length the header describes, header included.</summary>
    public int PacketLength => DsuProtocol.HeaderLength + PayloadLength;
}

/// <summary>
/// A decoded client request.
///
/// <para>
/// A class, not a struct, deliberately: requests arrive at roughly 1 Hz
/// per client (handshake and re-subscription), so the allocation is
/// irrelevant, whereas the streaming path — which does run at 1000 Hz —
/// only ever encodes and never touches this type.
/// </para>
/// </summary>
public sealed record DsuRequest
{
    /// <summary>Which request this is.</summary>
    public required DsuMessageType MessageType { get; init; }

    /// <summary>The requesting client's own id, which its responses must be addressed with.</summary>
    public uint ClientId { get; init; }

    /// <summary>
    /// Slots a <see cref="DsuMessageType.ControllerInfo"/> request asked
    /// about. Empty for the other two message types.
    /// </summary>
    public IReadOnlyList<byte> RequestedSlots { get; init; } = [];

    /// <summary>How a <see cref="DsuMessageType.ControllerData"/> request selected its pads.</summary>
    public DsuRegistrationFlags RegistrationFlags { get; init; }

    /// <summary>Slot to stream, meaningful only with <see cref="DsuRegistrationFlags.SlotBased"/>.</summary>
    public byte RegisteredSlot { get; init; }

    /// <summary>MAC to stream, meaningful only with <see cref="DsuRegistrationFlags.MacBased"/>.</summary>
    public ulong RegisteredMac { get; init; }
}

/// <summary>
/// Per-slot outgoing packet sequence numbers.
///
/// <para>
/// The counters are per slot and independent because that is what
/// clients assume: a client subscribed to slot 2 alone sees only slot 2's
/// numbers, and a shared counter would look to it like constant packet
/// loss. Increments are interlocked so a future sender that fans slots
/// out across threads cannot lose one.
/// </para>
/// </summary>
public sealed class DsuPacketCounter
{
    // Signed storage purely so Interlocked.Increment applies; the wire
    // field is a uint and wraps, and an unchecked cast of the wrapped
    // int reproduces exactly that wraparound sequence.
    private readonly int[] _counters = new int[DsuProtocol.MaxSlots];

    /// <summary>
    /// Advances and returns the sequence number for one slot. An
    /// out-of-range slot yields 0 instead of throwing, so a bad slot id
    /// from elsewhere in the runtime cannot take down the send loop.
    /// </summary>
    public uint Next(int slot)
    {
        if ((uint)slot >= (uint)_counters.Length)
        {
            return 0;
        }

        return unchecked((uint)Interlocked.Increment(ref _counters[slot]));
    }

    /// <summary>
    /// Restarts one slot's numbering, for when a pad is unplugged and a
    /// different one takes the slot — the new pad's stream should not
    /// appear to continue the old one's.
    /// </summary>
    public void Reset(int slot)
    {
        if ((uint)slot < (uint)_counters.Length)
        {
            Interlocked.Exchange(ref _counters[slot], 0);
        }
    }

    /// <summary>Restarts every slot, for a server restart.</summary>
    public void ResetAll()
    {
        for (var slot = 0; slot < _counters.Length; slot++)
        {
            Interlocked.Exchange(ref _counters[slot], 0);
        }
    }
}
