using System.Buffers.Binary;

namespace GameFlow.Infrastructure.Runtime.Motion;

/// <summary>
/// Wire codec for DSU / CemuhookUDP — the motion protocol Cemu, Dolphin,
/// Yuzu and Ryujinx all speak.
///
/// <para>
/// This type is the whole protocol and nothing else: it reads and writes
/// byte buffers and owns no socket, no thread and no state beyond
/// <see cref="ServerId"/>. That split is what makes the format testable
/// without a network, which matters because <b>every field below is a
/// fixed external contract</b> — a mistake here shows up as an emulator
/// silently reading garbage motion, not as a compile error.
/// </para>
///
/// <para>
/// Everything is little-endian regardless of host architecture, so all
/// reads and writes go through <see cref="BinaryPrimitives"/>'s explicit
/// LittleEndian overloads rather than <c>BitConverter</c>, which follows
/// the host and would produce a subtly different packet on a big-endian
/// machine.
/// </para>
///
/// <para>
/// Decoders here parse HOSTILE input: anything on the local network can
/// send a datagram to a DSU port. They return <c>false</c>/<c>null</c>
/// for malformed, truncated, mis-magicked or lying packets rather than
/// throwing, for the same reason
/// <c>WebControllerProtocol.TryParseInput</c> does — a throw would kill
/// the receive loop for every client at once.
/// </para>
/// </summary>
public static class DsuProtocol
{
    /// <summary>Fixed header size, in bytes, ahead of every payload.</summary>
    public const int HeaderLength = 16;

    /// <summary>Size of the message type, which the protocol counts as the first part of the payload.</summary>
    public const int MessageTypeLength = 4;

    /// <summary>Protocol revision this codec implements and advertises.</summary>
    public const ushort ProtocolVersion = 1001;

    /// <summary>Slots the protocol addresses, 0-3. Not a product limit — the request format only carries four.</summary>
    public const int MaxSlots = 4;

    /// <summary>Length of the device descriptor shared by the info and data responses.</summary>
    public const int DeviceBlockLength = 11;

    /// <summary>Controller-data payload size after the message type.</summary>
    public const int ControllerDataPayloadLength = 80;

    /// <summary>Full controller-data packet size — the 100-byte datagram emulators expect.</summary>
    public const int ControllerDataPacketLength = HeaderLength + MessageTypeLength + ControllerDataPayloadLength;

    /// <summary>Controller-info payload size after the message type: the device block plus one pad byte.</summary>
    public const int ControllerInfoPayloadLength = DeviceBlockLength + 1;

    /// <summary>Full controller-info response size.</summary>
    public const int ControllerInfoPacketLength = HeaderLength + MessageTypeLength + ControllerInfoPayloadLength;

    /// <summary>Full protocol-version response size.</summary>
    public const int VersionResponsePacketLength = HeaderLength + MessageTypeLength + sizeof(ushort);

    /// <summary>Full protocol-version request size — the request has no body beyond its message type.</summary>
    public const int VersionRequestPacketLength = HeaderLength + MessageTypeLength;

    /// <summary>Full controller-data (subscription) request size.</summary>
    public const int ControllerDataRequestPacketLength = HeaderLength + MessageTypeLength + 8;

    /// <summary>Smallest packet that can be valid: a header plus a message type.</summary>
    public const int MinimumPacketLength = HeaderLength + MessageTypeLength;

    // The checksum sits mid-header and is excluded from its own input,
    // so both the writer and the verifier need its position.
    private const int ChecksumOffset = 8;

    private const uint Crc32Polynomial = 0xEDB88320u;
    private const uint Crc32Seed = 0xFFFFFFFFu;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>Magic on server-to-client packets.</summary>
    public static ReadOnlySpan<byte> ServerMagic => "DSUS"u8;

    /// <summary>Magic on client-to-server packets.</summary>
    public static ReadOnlySpan<byte> ClientMagic => "DSUC"u8;

    /// <summary>
    /// This process's server id.
    ///
    /// <para>
    /// Clients remember the id of the server they subscribed to and treat
    /// a change as a different server, dropping and re-registering their
    /// pads. It therefore has to outlive individual sockets and sessions,
    /// which is why it is a process-wide value rather than something the
    /// server object generates when it starts listening.
    /// </para>
    /// </summary>
    public static uint ServerId { get; } = CreateServerId();

    /// <summary>
    /// Standard CRC-32 (IEEE 802.3): reflected polynomial 0xEDB88320,
    /// initial value 0xFFFFFFFF, final XOR 0xFFFFFFFF. Hand-rolled
    /// because <c>System.IO.Hashing</c> is not referenced and one 256-entry
    /// table is not worth a dependency.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data) => Accumulate(Crc32Seed, data) ^ Crc32Seed;

    /// <summary>
    /// Validates a packet's framing and checksum and returns its header.
    /// Every decoder starts here; nothing reads a payload byte until this
    /// has passed.
    /// </summary>
    /// <param name="packet">The datagram exactly as received.</param>
    /// <param name="expectedMagic">
    /// <see cref="ServerMagic"/> when decoding a response,
    /// <see cref="ClientMagic"/> when decoding a request. Passed in rather
    /// than inferred so a server can never be talked into accepting its
    /// own response format as a request.
    /// </param>
    /// <param name="header">The validated header, or <c>default</c> on failure.</param>
    /// <returns><c>true</c> when the packet is well-formed and the checksum matches.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> expectedMagic, out DsuHeader header)
    {
        header = default;

        // Length first: every check below indexes into the buffer, and a
        // short datagram is the cheapest attack there is.
        if (packet.Length < MinimumPacketLength || !packet[..4].SequenceEqual(expectedMagic))
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);

        // Only the upper bound is checked. An older revision still lays
        // its fields out the way we read them, but a NEWER one may not,
        // so guessing at it would be the one way to misread a packet
        // that passes its own checksum.
        if (version > ProtocolVersion)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);

        // The declared length must cover the message type and must not
        // reach past what actually arrived — this is the check that stops
        // a lying length field from turning into an over-read.
        if (payloadLength < MessageTypeLength || HeaderLength + payloadLength > packet.Length)
        {
            return false;
        }

        // A datagram may be LONGER than it declares (senders pad to a
        // fixed buffer), and the checksum was computed over the declared
        // extent only, so trim before verifying rather than after.
        var framed = packet[..(HeaderLength + payloadLength)];
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(framed[ChecksumOffset..]);
        if (checksum != ComputeFramedChecksum(framed))
        {
            return false;
        }

        header = new DsuHeader(
            version,
            payloadLength,
            checksum,
            BinaryPrimitives.ReadUInt32LittleEndian(framed[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(framed[HeaderLength..]));
        return true;
    }

    /// <summary>Writes the protocol-version response. Needs <see cref="VersionResponsePacketLength"/> bytes.</summary>
    public static bool TryWriteVersionResponse(Span<byte> destination, uint serverId, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < VersionResponsePacketLength)
        {
            return false;
        }

        var packet = destination[..VersionResponsePacketLength];
        WriteHeader(packet, ServerMagic, MessageTypeLength + sizeof(ushort), serverId, DsuMessageType.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[MinimumPacketLength..], ProtocolVersion);

        Seal(packet);
        bytesWritten = VersionResponsePacketLength;
        return true;
    }

    /// <summary>Writes one slot's controller-info response. Needs <see cref="ControllerInfoPacketLength"/> bytes.</summary>
    public static bool TryWriteControllerInfoResponse(
        Span<byte> destination,
        uint serverId,
        in DsuControllerInfo info,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < ControllerInfoPacketLength)
        {
            return false;
        }

        var packet = destination[..ControllerInfoPacketLength];
        WriteHeader(
            packet,
            ServerMagic,
            MessageTypeLength + ControllerInfoPayloadLength,
            serverId,
            DsuMessageType.ControllerInfo);

        var payload = packet[MinimumPacketLength..];
        WriteDeviceBlock(payload, info);

        // Trailing zero the format requires after the device block. It
        // carries nothing, but clients size the response by it.
        payload[DeviceBlockLength] = 0;

        Seal(packet);
        bytesWritten = ControllerInfoPacketLength;
        return true;
    }

    /// <summary>
    /// Writes one controller-data packet. This is the 1000 Hz path, so it
    /// fills a caller-owned buffer (typically stack- or pool-backed) and
    /// allocates nothing. Needs <see cref="ControllerDataPacketLength"/> bytes.
    /// </summary>
    public static bool TryWriteControllerData(
        Span<byte> destination,
        uint serverId,
        in DsuControllerData data,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < ControllerDataPacketLength)
        {
            return false;
        }

        var packet = destination[..ControllerDataPacketLength];
        WriteHeader(
            packet,
            ServerMagic,
            MessageTypeLength + ControllerDataPayloadLength,
            serverId,
            DsuMessageType.ControllerData);

        var payload = packet[MinimumPacketLength..];
        WriteDeviceBlock(payload, data.Device);
        payload[11] = data.IsConnected ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[12..], data.PacketNumber);

        // DsuButtons is laid out so this single little-endian write emits
        // wire button byte 1 then byte 2, in that order.
        BinaryPrimitives.WriteUInt16LittleEndian(payload[16..], (ushort)data.Buttons);
        payload[18] = data.PsButton ? (byte)1 : (byte)0;
        payload[19] = data.TouchButton ? (byte)1 : (byte)0;

        payload[20] = data.LeftStickX;
        payload[21] = data.LeftStickY;
        payload[22] = data.RightStickX;
        payload[23] = data.RightStickY;

        payload[24] = data.AnalogDpadLeft;
        payload[25] = data.AnalogDpadDown;
        payload[26] = data.AnalogDpadRight;
        payload[27] = data.AnalogDpadUp;

        payload[28] = data.AnalogTriangle;
        payload[29] = data.AnalogCircle;
        payload[30] = data.AnalogCross;
        payload[31] = data.AnalogSquare;

        payload[32] = data.AnalogR1;
        payload[33] = data.AnalogL1;
        payload[34] = data.AnalogR2;
        payload[35] = data.AnalogL2;

        WriteTouch(payload[36..], data.FirstTouch);
        WriteTouch(payload[42..], data.SecondTouch);

        BinaryPrimitives.WriteUInt64LittleEndian(payload[48..], data.MotionTimestampMicroseconds);
        BinaryPrimitives.WriteSingleLittleEndian(payload[56..], data.AccelX);
        BinaryPrimitives.WriteSingleLittleEndian(payload[60..], data.AccelY);
        BinaryPrimitives.WriteSingleLittleEndian(payload[64..], data.AccelZ);
        BinaryPrimitives.WriteSingleLittleEndian(payload[68..], data.GyroPitch);
        BinaryPrimitives.WriteSingleLittleEndian(payload[72..], data.GyroYaw);
        BinaryPrimitives.WriteSingleLittleEndian(payload[76..], data.GyroRoll);

        Seal(packet);
        bytesWritten = ControllerDataPacketLength;
        return true;
    }

    /// <summary>Writes a protocol-version request. Present so the codec can be exercised — and driven — from both ends.</summary>
    public static bool TryWriteVersionRequest(Span<byte> destination, uint clientId, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < VersionRequestPacketLength)
        {
            return false;
        }

        var packet = destination[..VersionRequestPacketLength];
        WriteHeader(packet, ClientMagic, MessageTypeLength, clientId, DsuMessageType.ProtocolVersion);

        Seal(packet);
        bytesWritten = VersionRequestPacketLength;
        return true;
    }

    /// <summary>
    /// Writes a controller-info request for up to <see cref="MaxSlots"/>
    /// slots. More than that is refused rather than truncated: a silently
    /// shortened request would look to the caller like the extra slots
    /// were asked about and found empty.
    /// </summary>
    public static bool TryWriteControllerInfoRequest(
        Span<byte> destination,
        uint clientId,
        ReadOnlySpan<byte> slots,
        out int bytesWritten)
    {
        bytesWritten = 0;
        var packetLength = MinimumPacketLength + sizeof(int) + slots.Length;
        if (slots.Length > MaxSlots || destination.Length < packetLength)
        {
            return false;
        }

        var packet = destination[..packetLength];
        WriteHeader(
            packet,
            ClientMagic,
            (ushort)(MessageTypeLength + sizeof(int) + slots.Length),
            clientId,
            DsuMessageType.ControllerInfo);

        var payload = packet[MinimumPacketLength..];
        BinaryPrimitives.WriteInt32LittleEndian(payload, slots.Length);
        slots.CopyTo(payload[sizeof(int)..]);

        Seal(packet);
        bytesWritten = packetLength;
        return true;
    }

    /// <summary>Writes a controller-data subscription request. Needs <see cref="ControllerDataRequestPacketLength"/> bytes.</summary>
    public static bool TryWriteControllerDataRequest(
        Span<byte> destination,
        uint clientId,
        DsuRegistrationFlags flags,
        byte slot,
        ulong macAddress,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < ControllerDataRequestPacketLength)
        {
            return false;
        }

        var packet = destination[..ControllerDataRequestPacketLength];
        WriteHeader(packet, ClientMagic, MessageTypeLength + 8, clientId, DsuMessageType.ControllerData);

        var payload = packet[MinimumPacketLength..];
        payload[0] = (byte)flags;
        payload[1] = slot;
        WriteMac(payload[2..8], macAddress);

        Seal(packet);
        bytesWritten = ControllerDataRequestPacketLength;
        return true;
    }

    /// <summary>
    /// Decodes a server controller-data packet. Returns <c>false</c> for
    /// anything that is not one — wrong magic, wrong message type, bad
    /// checksum, or a payload shorter than the fixed 80-byte block.
    /// </summary>
    public static bool TryReadControllerData(ReadOnlySpan<byte> packet, out DsuControllerData data)
    {
        data = default;
        if (!TryReadHeader(packet, ServerMagic, out var header)
            || header.MessageType != (uint)DsuMessageType.ControllerData
            || header.PayloadLength < MessageTypeLength + ControllerDataPayloadLength)
        {
            return false;
        }

        var payload = packet.Slice(MinimumPacketLength, ControllerDataPayloadLength);
        data = new DsuControllerData
        {
            Device = ReadDeviceBlock(payload),
            IsConnected = payload[11] != 0,
            PacketNumber = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]),
            Buttons = (DsuButtons)BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]),
            PsButton = payload[18] != 0,
            TouchButton = payload[19] != 0,

            LeftStickX = payload[20],
            LeftStickY = payload[21],
            RightStickX = payload[22],
            RightStickY = payload[23],

            AnalogDpadLeft = payload[24],
            AnalogDpadDown = payload[25],
            AnalogDpadRight = payload[26],
            AnalogDpadUp = payload[27],

            AnalogTriangle = payload[28],
            AnalogCircle = payload[29],
            AnalogCross = payload[30],
            AnalogSquare = payload[31],

            AnalogR1 = payload[32],
            AnalogL1 = payload[33],
            AnalogR2 = payload[34],
            AnalogL2 = payload[35],

            FirstTouch = ReadTouch(payload[36..]),
            SecondTouch = ReadTouch(payload[42..]),

            MotionTimestampMicroseconds = BinaryPrimitives.ReadUInt64LittleEndian(payload[48..]),

            // Motion arrives as raw float32 and stays that way. There is no
            // clamp here on purpose: a fast flick legitimately exceeds any
            // bound one could pick, and capping it would silently flatten
            // exactly the gesture that mattered. Consumers that need a
            // sanity ceiling apply it where the units are known.
            AccelX = BinaryPrimitives.ReadSingleLittleEndian(payload[56..]),
            AccelY = BinaryPrimitives.ReadSingleLittleEndian(payload[60..]),
            AccelZ = BinaryPrimitives.ReadSingleLittleEndian(payload[64..]),
            GyroPitch = BinaryPrimitives.ReadSingleLittleEndian(payload[68..]),
            GyroYaw = BinaryPrimitives.ReadSingleLittleEndian(payload[72..]),
            GyroRoll = BinaryPrimitives.ReadSingleLittleEndian(payload[76..])
        };
        return true;
    }

    /// <summary>Decodes a server controller-info response.</summary>
    public static bool TryReadControllerInfoResponse(ReadOnlySpan<byte> packet, out DsuControllerInfo info)
    {
        info = default;
        if (!TryReadHeader(packet, ServerMagic, out var header)
            || header.MessageType != (uint)DsuMessageType.ControllerInfo
            || header.PayloadLength < MessageTypeLength + DeviceBlockLength)
        {
            return false;
        }

        info = ReadDeviceBlock(packet[MinimumPacketLength..]);
        return true;
    }

    /// <summary>Decodes a server protocol-version response.</summary>
    public static bool TryReadVersionResponse(ReadOnlySpan<byte> packet, out ushort version)
    {
        version = 0;
        if (!TryReadHeader(packet, ServerMagic, out var header)
            || header.MessageType != (uint)DsuMessageType.ProtocolVersion
            || header.PayloadLength < MessageTypeLength + sizeof(ushort))
        {
            return false;
        }

        version = BinaryPrimitives.ReadUInt16LittleEndian(packet[MinimumPacketLength..]);
        return true;
    }

    /// <summary>
    /// Parses one client request, returning <c>null</c> for anything
    /// malformed. Null rather than an exception because this runs on the
    /// receive loop of a socket any host on the LAN can reach.
    /// </summary>
    public static DsuRequest? TryParseRequest(ReadOnlySpan<byte> packet)
    {
        if (!TryReadHeader(packet, ClientMagic, out var header))
        {
            return null;
        }

        var payload = packet[MinimumPacketLength..header.PacketLength];
        switch (header.MessageType)
        {
            case (uint)DsuMessageType.ProtocolVersion:
                return new DsuRequest
                {
                    MessageType = DsuMessageType.ProtocolVersion,
                    ClientId = header.SenderId
                };

            case (uint)DsuMessageType.ControllerInfo:
                return TryParseControllerInfoRequest(payload, header.SenderId);

            case (uint)DsuMessageType.ControllerData:
                if (payload.Length < 8)
                {
                    return null;
                }

                return new DsuRequest
                {
                    MessageType = DsuMessageType.ControllerData,
                    ClientId = header.SenderId,
                    RegistrationFlags = (DsuRegistrationFlags)payload[0],
                    RegisteredSlot = payload[1],
                    RegisteredMac = ReadMac(payload[2..8])
                };

            default:
                // An unknown message type is not an error worth logging at
                // volume — anything at all can arrive on this port.
                return null;
        }
    }

    private static DsuRequest? TryParseControllerInfoRequest(ReadOnlySpan<byte> payload, uint clientId)
    {
        if (payload.Length < sizeof(int))
        {
            return null;
        }

        var requestedCount = BinaryPrimitives.ReadInt32LittleEndian(payload);

        // The count is a signed 32-bit field on the wire, so a hostile
        // client can send -1 or 2 billion. Bounding it against MaxSlots
        // before it is used as a length is what keeps the slice below
        // in range and the allocation trivial.
        if (requestedCount < 0 || requestedCount > MaxSlots || payload.Length < sizeof(int) + requestedCount)
        {
            return null;
        }

        var slots = new byte[requestedCount];
        payload.Slice(sizeof(int), requestedCount).CopyTo(slots);
        return new DsuRequest
        {
            MessageType = DsuMessageType.ControllerInfo,
            ClientId = clientId,
            RequestedSlots = slots
        };
    }

    private static void WriteHeader(
        Span<byte> packet,
        ReadOnlySpan<byte> magic,
        ushort payloadLength,
        uint senderId,
        DsuMessageType messageType)
    {
        magic.CopyTo(packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[4..], ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], payloadLength);

        // Zeroed for now: the checksum covers this field as zero, so Seal
        // can compute over the finished packet in one pass.
        BinaryPrimitives.WriteUInt32LittleEndian(packet[ChecksumOffset..], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[12..], senderId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[HeaderLength..], (uint)messageType);
    }

    /// <summary>Stamps the checksum. Must be the last write — anything after it invalidates the packet.</summary>
    private static void Seal(Span<byte> packet) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet[ChecksumOffset..], ComputeCrc32(packet));

    /// <summary>
    /// CRC of a received packet with the checksum field treated as zero.
    /// Feeding four zero bytes into the running CRC gives the identical
    /// result to blanking the field, without copying the datagram — and
    /// without needing a writable buffer, which a received
    /// <see cref="ReadOnlySpan{T}"/> is not.
    /// </summary>
    private static uint ComputeFramedChecksum(ReadOnlySpan<byte> framed)
    {
        ReadOnlySpan<byte> zeroedChecksum = [0, 0, 0, 0];

        var crc = Accumulate(Crc32Seed, framed[..ChecksumOffset]);
        crc = Accumulate(crc, zeroedChecksum);
        crc = Accumulate(crc, framed[(ChecksumOffset + sizeof(uint))..]);
        return crc ^ Crc32Seed;
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
    {
        var table = Crc32Table;
        foreach (var value in data)
        {
            crc = (crc >> 8) ^ table[(byte)(crc ^ value)];
        }

        return crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0u; index < table.Length; index++)
        {
            var entry = index;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Crc32Polynomial : entry >> 1;
            }

            table[index] = entry;
        }

        return table;
    }

    private static void WriteDeviceBlock(Span<byte> destination, in DsuControllerInfo info)
    {
        destination[0] = info.Slot;
        destination[1] = (byte)info.State;
        destination[2] = (byte)info.Model;
        destination[3] = (byte)info.ConnectionType;
        WriteMac(destination[4..10], info.MacAddress);
        destination[10] = (byte)info.Battery;
    }

    // Byte values outside the declared enum members are kept rather than
    // rejected: the protocol has grown values before (the 0xEE/0xEF
    // charging states), and a decoder that insisted on known members
    // would throw away an otherwise perfect packet from a newer peer.
    private static DsuControllerInfo ReadDeviceBlock(ReadOnlySpan<byte> source) =>
        new()
        {
            Slot = source[0],
            State = (DsuSlotState)source[1],
            Model = (DsuDeviceModel)source[2],
            ConnectionType = (DsuConnectionType)source[3],
            MacAddress = ReadMac(source[4..10]),
            Battery = (DsuBatteryStatus)source[10]
        };

    private static void WriteTouch(Span<byte> destination, in DsuTouch touch)
    {
        destination[0] = touch.IsActive ? (byte)1 : (byte)0;
        destination[1] = touch.Id;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], touch.X);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], touch.Y);
    }

    private static DsuTouch ReadTouch(ReadOnlySpan<byte> source) =>
        new(
            source[0] != 0,
            source[1],
            BinaryPrimitives.ReadUInt16LittleEndian(source[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[4..]));

    // Six bytes, low octet first. The protocol never interprets the MAC,
    // so the packing order only has to agree with ReadMac — and this one
    // matches the little-endian rule the rest of the format follows.
    private static void WriteMac(Span<byte> destination, ulong macAddress)
    {
        for (var index = 0; index < 6; index++)
        {
            destination[index] = (byte)(macAddress >> (8 * index));
        }
    }

    private static ulong ReadMac(ReadOnlySpan<byte> source)
    {
        var macAddress = 0ul;
        for (var index = 0; index < 6; index++)
        {
            macAddress |= (ulong)source[index] << (8 * index);
        }

        return macAddress;
    }

    // Zero reads as "uninitialised" to some clients, so it is excluded.
    private static uint CreateServerId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue + 1L);
}
