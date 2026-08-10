using System.Buffers.Binary;
using GameFlow.Infrastructure.Runtime.Motion;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Motion;

public sealed class DsuProtocolTests
{
    [Fact]
    public void Crc32MatchesTheIeeeReferenceVector()
    {
        // "123456789" => 0xCBF43926 is the check value every CRC-32/ISO-HDLC
        // implementation publishes. If the polynomial, reflection, seed or
        // final XOR were wrong, this is the one assertion that says so
        // without a live emulator on the other end.
        Assert.Equal(0xCBF43926u, DsuProtocol.ComputeCrc32("123456789"u8));
    }

    [Fact]
    public void Crc32OfEmptyInputIsZero()
    {
        Assert.Equal(0u, DsuProtocol.ComputeCrc32([]));
    }

    [Fact]
    public void EncodedControllerDataMatchesTheDocumentedFraming()
    {
        var packet = EncodeControllerData(SampleData());

        Assert.Equal(DsuProtocol.ControllerDataPacketLength, packet.Length);
        Assert.Equal(100, packet.Length);                                            // the datagram size emulators expect
        Assert.Equal("DSUS"u8.ToArray(), packet[..4]);
        Assert.Equal(1001, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)));
        Assert.Equal(84, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)));  // payload INCLUDING the message type
        Assert.Equal(0x100002u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
    }

    [Fact]
    public void WellFormedPacketPassesChecksumVerification()
    {
        var packet = EncodeControllerData(SampleData());

        Assert.True(DsuProtocol.TryReadHeader(packet, DsuProtocol.ServerMagic, out var header));
        Assert.Equal(1001, header.ProtocolVersion);
        Assert.Equal(84, header.PayloadLength);
        Assert.Equal(100, header.PacketLength);
        Assert.Equal(0x100002u, header.MessageType);
    }

    [Theory]
    [InlineData(8)]   // inside the checksum field itself
    [InlineData(12)]  // sender id
    [InlineData(16)]  // message type
    [InlineData(20)]  // first payload byte
    [InlineData(70)]  // middle of the gyro block
    [InlineData(99)]  // last byte
    public void CorruptedPacketFailsChecksumVerification(int offset)
    {
        var packet = EncodeControllerData(SampleData());
        packet[offset] ^= 0xFF;

        Assert.False(DsuProtocol.TryReadHeader(packet, DsuProtocol.ServerMagic, out _));
        Assert.False(DsuProtocol.TryReadControllerData(packet, out _));
    }

    [Fact]
    public void ControllerDataRoundTripsEveryField()
    {
        var original = SampleData();
        var packet = EncodeControllerData(original);

        Assert.True(DsuProtocol.TryReadControllerData(packet, out var decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ControllerDataRoundTripsTheDeviceBlockAndTouchDetail()
    {
        // Spelled out field by field as well as by record equality: a
        // decoder that transposed two adjacent bytes of the same type
        // would still satisfy Assert.Equal on a struct built from the
        // same transposition.
        var packet = EncodeControllerData(SampleData());
        Assert.True(DsuProtocol.TryReadControllerData(packet, out var decoded));

        Assert.Equal(2, decoded.Device.Slot);
        Assert.Equal(DsuSlotState.Connected, decoded.Device.State);
        Assert.Equal(DsuDeviceModel.FullGyro, decoded.Device.Model);
        Assert.Equal(DsuConnectionType.Bluetooth, decoded.Device.ConnectionType);
        Assert.Equal(0x1A2B3C4D5E6Ful, decoded.Device.MacAddress);
        Assert.Equal(DsuBatteryStatus.Charging, decoded.Device.Battery);
        Assert.True(decoded.IsConnected);

        Assert.Equal(0x0BADF00Du, decoded.PacketNumber);
        Assert.Equal(DsuButtons.DpadUp | DsuButtons.Cross | DsuButtons.R2, decoded.Buttons);
        Assert.True(decoded.PsButton);
        Assert.True(decoded.TouchButton);

        Assert.Equal(0x11, decoded.LeftStickX);
        Assert.Equal(0x22, decoded.LeftStickY);
        Assert.Equal(0x33, decoded.RightStickX);
        Assert.Equal(0x44, decoded.RightStickY);

        Assert.Equal(new DsuTouch(true, 7, 1400, 900), decoded.FirstTouch);
        Assert.Equal(new DsuTouch(false, 9, 12, 34), decoded.SecondTouch);

        Assert.Equal(1_234_567_890_123ul, decoded.MotionTimestampMicroseconds);
    }

    [Fact]
    public void EveryAnalogByteLandsInItsOwnWireSlot()
    {
        // Twelve consecutive same-typed bytes is exactly where an
        // off-by-one in the layout hides, so each gets a unique value.
        var data = SampleData() with
        {
            AnalogDpadLeft = 1,
            AnalogDpadDown = 2,
            AnalogDpadRight = 3,
            AnalogDpadUp = 4,
            AnalogTriangle = 5,
            AnalogCircle = 6,
            AnalogCross = 7,
            AnalogSquare = 8,
            AnalogR1 = 9,
            AnalogL1 = 10,
            AnalogR2 = 11,
            AnalogL2 = 12
        };

        var packet = EncodeControllerData(data);
        Assert.True(DsuProtocol.TryReadControllerData(packet, out var decoded));

        Assert.Equal(data, decoded);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, packet[44..56]); // payload offsets 24..35
    }

    [Theory]
    [InlineData(DsuButtons.Share, 0x01, 0x00)]
    [InlineData(DsuButtons.LeftStick, 0x02, 0x00)]
    [InlineData(DsuButtons.RightStick, 0x04, 0x00)]
    [InlineData(DsuButtons.Options, 0x08, 0x00)]
    [InlineData(DsuButtons.DpadUp, 0x10, 0x00)]
    [InlineData(DsuButtons.DpadRight, 0x20, 0x00)]
    [InlineData(DsuButtons.DpadDown, 0x40, 0x00)]
    [InlineData(DsuButtons.DpadLeft, 0x80, 0x00)]
    [InlineData(DsuButtons.L2, 0x00, 0x01)]
    [InlineData(DsuButtons.R2, 0x00, 0x02)]
    [InlineData(DsuButtons.L1, 0x00, 0x04)]
    [InlineData(DsuButtons.R1, 0x00, 0x08)]
    [InlineData(DsuButtons.Square, 0x00, 0x10)]
    [InlineData(DsuButtons.Cross, 0x00, 0x20)]
    [InlineData(DsuButtons.Circle, 0x00, 0x40)]
    [InlineData(DsuButtons.Triangle, 0x00, 0x80)]
    public void EveryButtonFlagLandsOnItsDocumentedWireBit(DsuButtons button, byte firstByte, byte secondByte)
    {
        // This is the contract every emulator decodes against. The enum
        // is written as one little-endian ushort, so the low half must
        // come out as wire button byte 1 and the high half as byte 2.
        var packet = EncodeControllerData(SampleData() with { Buttons = button });

        Assert.Equal(firstByte, packet[36]);   // payload offset 16
        Assert.Equal(secondByte, packet[37]);  // payload offset 17
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]   // header only, no message type
    [InlineData(19)]   // one byte short of the smallest legal packet
    [InlineData(20)]   // header + message type, no body
    [InlineData(21)]
    [InlineData(50)]
    [InlineData(83)]
    [InlineData(99)]   // one byte short of a whole data packet
    public void TruncatedPacketsAreRejectedInsteadOfThrowing(int length)
    {
        var packet = EncodeControllerData(SampleData());

        // A short datagram is the cheapest thing an attacker can send, so
        // none of these may reach an indexer.
        Assert.False(DsuProtocol.TryReadControllerData(packet.AsSpan(0, length), out _));
        Assert.False(DsuProtocol.TryReadHeader(packet.AsSpan(0, length), DsuProtocol.ServerMagic, out _));
        Assert.Null(DsuProtocol.TryParseRequest(packet.AsSpan(0, length)));
    }

    [Fact]
    public void ClientMagicIsRejectedWhereServerMagicIsExpected()
    {
        var buffer = new byte[DsuProtocol.ControllerDataRequestPacketLength];
        Assert.True(DsuProtocol.TryWriteControllerDataRequest(
            buffer, clientId: 7, DsuRegistrationFlags.AllPads, slot: 0, macAddress: 0, out _));

        // A perfectly valid DSUC packet — right version, right checksum,
        // right message type — must not decode as a server response.
        Assert.False(DsuProtocol.TryReadControllerData(buffer, out _));
        Assert.False(DsuProtocol.TryReadHeader(buffer, DsuProtocol.ServerMagic, out _));
        Assert.NotNull(DsuProtocol.TryParseRequest(buffer));
    }

    [Fact]
    public void ServerMagicIsRejectedWhereClientMagicIsExpected()
    {
        var packet = EncodeControllerData(SampleData());
        Assert.Null(DsuProtocol.TryParseRequest(packet));
    }

    [Theory]
    [InlineData("XXXX")]
    [InlineData("dsus")]  // case matters
    [InlineData("\0\0\0\0")]
    public void GarbageMagicIsRejectedEvenWithAValidChecksum(string magic)
    {
        var packet = EncodeControllerData(SampleData());
        for (var index = 0; index < 4; index++)
        {
            packet[index] = (byte)magic[index];
        }

        // Resealed, so this fails on the magic and not incidentally on the
        // checksum — otherwise the test would pass with no magic check at all.
        Reseal(packet);

        Assert.False(DsuProtocol.TryReadControllerData(packet, out _));
    }

    [Fact]
    public void LengthFieldClaimingMoreThanArrivedIsRejected()
    {
        var packet = EncodeControllerData(SampleData());
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), ushort.MaxValue);
        Reseal(packet);

        // Rejected on the length check before any payload read — the
        // alternative is slicing 65535 bytes out of a 100-byte datagram.
        Assert.False(DsuProtocol.TryReadHeader(packet, DsuProtocol.ServerMagic, out _));
        Assert.False(DsuProtocol.TryReadControllerData(packet, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]  // shorter than the message type the length must cover
    public void LengthFieldShorterThanTheMessageTypeIsRejected(ushort payloadLength)
    {
        var packet = EncodeControllerData(SampleData());
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), payloadLength);
        Reseal(packet);

        Assert.False(DsuProtocol.TryReadHeader(packet, DsuProtocol.ServerMagic, out _));
    }

    [Fact]
    public void HonestLengthShorterThanTheDataBlockIsRejected()
    {
        // Header and checksum are impeccable; the packet simply does not
        // carry a full 80-byte controller-data block.
        var packet = BuildPacket("DSUS"u8, 1001, (uint)DsuMessageType.ControllerData, senderId: 1, body: new byte[40]);

        Assert.True(DsuProtocol.TryReadHeader(packet, DsuProtocol.ServerMagic, out _));
        Assert.False(DsuProtocol.TryReadControllerData(packet, out _));
    }

    [Fact]
    public void DatagramPaddedBeyondItsDeclaredLengthStillDecodes()
    {
        // Senders pad to a fixed buffer, so a 100-byte packet can arrive
        // inside a 1024-byte read. The declared length, not the buffer
        // size, defines what the checksum covered.
        var buffer = new byte[1024];
        Assert.True(DsuProtocol.TryWriteControllerData(buffer, DsuProtocol.ServerId, SampleData(), out var written));
        Assert.Equal(100, written);

        Assert.True(DsuProtocol.TryReadControllerData(buffer, out var decoded));
        Assert.Equal(SampleData(), decoded);
    }

    [Fact]
    public void ProtocolVersionNewerThanOursIsRejected()
    {
        // An older revision still lays its fields out the way we read
        // them; a newer one may not, so it is refused rather than guessed at.
        var packet = EncodeControllerData(SampleData());
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), 1002);
        Reseal(packet);

        Assert.False(DsuProtocol.TryReadControllerData(packet, out _));
    }

    [Theory]
    [InlineData(2500f, -1750.5f, 0.0009765625f)]
    [InlineData(1e20f, -3.4e30f, 1.5e-20f)]
    public void MotionValuesRoundTripAsFloat32WithoutClamping(float first, float second, float third)
    {
        // A fast flick legitimately exceeds any bound one could pick — the
        // same reasoning WebControllerProtocol uses for its unbounded
        // motion fields. Clamping here would silently flatten exactly the
        // gesture that mattered, so these must come back bit-identical.
        var data = SampleData() with
        {
            AccelX = first,
            AccelY = second,
            AccelZ = third,
            GyroPitch = -first,
            GyroYaw = second * 2f,
            GyroRoll = third
        };

        Assert.True(DsuProtocol.TryReadControllerData(EncodeControllerData(data), out var decoded));

        Assert.Equal(first, decoded.AccelX);
        Assert.Equal(second, decoded.AccelY);
        Assert.Equal(third, decoded.AccelZ);
        Assert.Equal(-first, decoded.GyroPitch);
        Assert.Equal(second * 2f, decoded.GyroYaw);
        Assert.Equal(third, decoded.GyroRoll);
    }

    [Fact]
    public void MotionTimestampCarriesFullMicrosecondRange()
    {
        // Microseconds, not milliseconds: at 1000 Hz a millisecond field
        // would quantise away the entire inter-sample interval.
        var data = SampleData() with { MotionTimestampMicroseconds = ulong.MaxValue };

        Assert.True(DsuProtocol.TryReadControllerData(EncodeControllerData(data), out var decoded));
        Assert.Equal(ulong.MaxValue, decoded.MotionTimestampMicroseconds);
    }

    [Fact]
    public void UnknownEnumValuesSurviveDecodingInsteadOfFailingThePacket()
    {
        // The protocol has grown battery values before; refusing an
        // unrecognised one would throw away an otherwise perfect packet.
        var packet = EncodeControllerData(SampleData());
        packet[30] = 0x7F; // battery byte: device block offset 10

        Reseal(packet);

        Assert.True(DsuProtocol.TryReadControllerData(packet, out var decoded));
        Assert.Equal((DsuBatteryStatus)0x7F, decoded.Device.Battery);
    }

    [Fact]
    public void EncodersRefuseAnUndersizedDestinationInsteadOfThrowing()
    {
        Assert.False(DsuProtocol.TryWriteControllerData(
            new byte[DsuProtocol.ControllerDataPacketLength - 1], 1, SampleData(), out var dataWritten));
        Assert.Equal(0, dataWritten);

        Assert.False(DsuProtocol.TryWriteVersionResponse(new byte[4], 1, out var versionWritten));
        Assert.Equal(0, versionWritten);

        Assert.False(DsuProtocol.TryWriteControllerInfoResponse(
            new byte[10], 1, new DsuControllerInfo(), out var infoWritten));
        Assert.Equal(0, infoWritten);
    }

    [Fact]
    public void VersionResponseRoundTrips()
    {
        var buffer = new byte[DsuProtocol.VersionResponsePacketLength];

        Assert.True(DsuProtocol.TryWriteVersionResponse(buffer, serverId: 42, out var written));
        Assert.Equal(22, written);
        Assert.True(DsuProtocol.TryReadVersionResponse(buffer, out var version));
        Assert.Equal(1001, version);
    }

    [Fact]
    public void ControllerInfoResponseRoundTrips()
    {
        var info = new DsuControllerInfo
        {
            Slot = 3,
            State = DsuSlotState.Reserved,
            Model = DsuDeviceModel.PartialGyro,
            ConnectionType = DsuConnectionType.Usb,
            MacAddress = 0xFFEEDDCCBBAAul,
            Battery = DsuBatteryStatus.Full
        };
        var buffer = new byte[DsuProtocol.ControllerInfoPacketLength];

        Assert.True(DsuProtocol.TryWriteControllerInfoResponse(buffer, serverId: 42, info, out var written));
        Assert.Equal(32, written);
        Assert.True(DsuProtocol.TryReadControllerInfoResponse(buffer, out var decoded));
        Assert.Equal(info, decoded);

        // The format's trailing pad byte after the 11-byte device block.
        Assert.Equal(0, buffer[^1]);
    }

    [Fact]
    public void VersionRequestRoundTrips()
    {
        var buffer = new byte[DsuProtocol.VersionRequestPacketLength];
        Assert.True(DsuProtocol.TryWriteVersionRequest(buffer, clientId: 0xDEADBEEF, out var written));
        Assert.Equal(20, written);

        var request = DsuProtocol.TryParseRequest(buffer);

        Assert.NotNull(request);
        Assert.Equal(DsuMessageType.ProtocolVersion, request!.MessageType);
        Assert.Equal(0xDEADBEEFu, request.ClientId);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 2 })]
    [InlineData(new byte[0])]
    public void ControllerInfoRequestRoundTrips(byte[] slots)
    {
        var buffer = new byte[64];
        Assert.True(DsuProtocol.TryWriteControllerInfoRequest(buffer, clientId: 9, slots, out var written));

        var request = DsuProtocol.TryParseRequest(buffer.AsSpan(0, written));

        Assert.NotNull(request);
        Assert.Equal(DsuMessageType.ControllerInfo, request!.MessageType);
        Assert.Equal(9u, request.ClientId);
        Assert.Equal(slots, request.RequestedSlots.ToArray());
    }

    [Fact]
    public void ControllerInfoRequestForMoreSlotsThanExistIsRefusedAtTheEncoder()
    {
        Assert.False(DsuProtocol.TryWriteControllerInfoRequest(
            new byte[64], clientId: 1, [0, 1, 2, 3, 4], out var written));
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData(5)]           // more slots than the protocol has
    [InlineData(-1)]          // signed field, hostile value
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ControllerInfoRequestWithAnImpossibleSlotCountIsRejected(int slotCount)
    {
        var body = new byte[4 + DsuProtocol.MaxSlots];
        BinaryPrimitives.WriteInt32LittleEndian(body, slotCount);
        var packet = BuildPacket("DSUC"u8, 1001, (uint)DsuMessageType.ControllerInfo, senderId: 1, body: body);

        // The count is read as a length; unbounded, it becomes either a
        // negative allocation or a read far past the datagram.
        Assert.Null(DsuProtocol.TryParseRequest(packet));
    }

    [Fact]
    public void ControllerInfoRequestPromisingMoreSlotsThanItCarriesIsRejected()
    {
        var body = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(body, 4); // says four slots, carries two
        var packet = BuildPacket("DSUC"u8, 1001, (uint)DsuMessageType.ControllerInfo, senderId: 1, body: body);

        Assert.Null(DsuProtocol.TryParseRequest(packet));
    }

    [Fact]
    public void ControllerDataRequestRoundTrips()
    {
        var buffer = new byte[DsuProtocol.ControllerDataRequestPacketLength];
        Assert.True(DsuProtocol.TryWriteControllerDataRequest(
            buffer,
            clientId: 5,
            DsuRegistrationFlags.SlotBased | DsuRegistrationFlags.MacBased,
            slot: 3,
            macAddress: 0x010203040506ul,
            out var written));
        Assert.Equal(28, written);

        var request = DsuProtocol.TryParseRequest(buffer);

        Assert.NotNull(request);
        Assert.Equal(DsuMessageType.ControllerData, request!.MessageType);
        Assert.Equal(5u, request.ClientId);
        Assert.Equal(DsuRegistrationFlags.SlotBased | DsuRegistrationFlags.MacBased, request.RegistrationFlags);
        Assert.Equal(3, request.RegisteredSlot);
        Assert.Equal(0x010203040506ul, request.RegisteredMac);
        Assert.Empty(request.RequestedSlots);
    }

    [Fact]
    public void ControllerDataRequestShorterThanItsFixedBodyIsRejected()
    {
        var packet = BuildPacket("DSUC"u8, 1001, (uint)DsuMessageType.ControllerData, senderId: 1, body: new byte[7]);

        Assert.Null(DsuProtocol.TryParseRequest(packet));
    }

    [Fact]
    public void UnknownMessageTypeIsRejected()
    {
        var packet = BuildPacket("DSUC"u8, 1001, messageType: 0x100003, senderId: 1, body: new byte[8]);

        Assert.Null(DsuProtocol.TryParseRequest(packet));
    }

    [Fact]
    public void ServerIdIsStableAndNonZero()
    {
        // Clients treat a changed server id as a different server and
        // re-register every pad, so it must not drift within a process.
        Assert.NotEqual(0u, DsuProtocol.ServerId);
        Assert.Equal(DsuProtocol.ServerId, DsuProtocol.ServerId);
    }

    [Fact]
    public void PacketCounterIncrementsPerSlotIndependently()
    {
        var counter = new DsuPacketCounter();

        Assert.Equal(1u, counter.Next(0));
        Assert.Equal(2u, counter.Next(0));
        Assert.Equal(3u, counter.Next(0));

        // Slot 1 has sent nothing yet; a shared counter would hand it 4
        // here and look like three dropped packets to its subscriber.
        Assert.Equal(1u, counter.Next(1));
        Assert.Equal(2u, counter.Next(1));
        Assert.Equal(4u, counter.Next(0));
        Assert.Equal(1u, counter.Next(3));
    }

    [Fact]
    public void PacketCounterResetsOneSlotWithoutDisturbingTheOthers()
    {
        var counter = new DsuPacketCounter();
        counter.Next(0);
        counter.Next(0);
        counter.Next(1);

        counter.Reset(0);

        Assert.Equal(1u, counter.Next(0));
        Assert.Equal(2u, counter.Next(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(DsuProtocol.MaxSlots)]
    [InlineData(int.MaxValue)]
    public void PacketCounterIgnoresOutOfRangeSlots(int slot)
    {
        var counter = new DsuPacketCounter();

        Assert.Equal(0u, counter.Next(slot));
        counter.Reset(slot);
        counter.ResetAll();
    }

    private static DsuControllerData SampleData() => new()
    {
        Device = new DsuControllerInfo
        {
            Slot = 2,
            State = DsuSlotState.Connected,
            Model = DsuDeviceModel.FullGyro,
            ConnectionType = DsuConnectionType.Bluetooth,
            MacAddress = 0x1A2B3C4D5E6Ful,
            Battery = DsuBatteryStatus.Charging
        },
        IsConnected = true,
        PacketNumber = 0x0BADF00D,
        Buttons = DsuButtons.DpadUp | DsuButtons.Cross | DsuButtons.R2,
        PsButton = true,
        TouchButton = true,
        LeftStickX = 0x11,
        LeftStickY = 0x22,
        RightStickX = 0x33,
        RightStickY = 0x44,
        AnalogDpadLeft = 0x55,
        AnalogDpadDown = 0x66,
        AnalogDpadRight = 0x77,
        AnalogDpadUp = 0x88,
        AnalogTriangle = 0x99,
        AnalogCircle = 0xAA,
        AnalogCross = 0xBB,
        AnalogSquare = 0xCC,
        AnalogR1 = 0xDD,
        AnalogL1 = 0xEE,
        AnalogR2 = 0xF0,
        AnalogL2 = 0x0F,
        FirstTouch = new DsuTouch(true, 7, 1400, 900),
        SecondTouch = new DsuTouch(false, 9, 12, 34),
        MotionTimestampMicroseconds = 1_234_567_890_123ul,
        AccelX = 0.25f,
        AccelY = -1.5f,
        AccelZ = 9.80665f,
        GyroPitch = 12.5f,
        GyroYaw = -240.75f,
        GyroRoll = 0.125f
    };

    private static byte[] EncodeControllerData(in DsuControllerData data)
    {
        var packet = new byte[DsuProtocol.ControllerDataPacketLength];
        Assert.True(DsuProtocol.TryWriteControllerData(packet, DsuProtocol.ServerId, data, out var written));
        Assert.Equal(packet.Length, written);
        return packet;
    }

    /// <summary>
    /// Builds a packet by hand so tests can express framing the encoders
    /// deliberately cannot produce — a lying length, an unknown message
    /// type, a body that stops early.
    /// </summary>
    private static byte[] BuildPacket(
        ReadOnlySpan<byte> magic,
        ushort version,
        uint messageType,
        uint senderId,
        byte[] body)
    {
        var packet = new byte[DsuProtocol.MinimumPacketLength + body.Length];
        magic.CopyTo(packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)(DsuProtocol.MessageTypeLength + body.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), senderId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), messageType);
        body.CopyTo(packet.AsSpan(DsuProtocol.MinimumPacketLength));
        Reseal(packet);
        return packet;
    }

    /// <summary>Recomputes the checksum after a test has edited a packet, so the assertion is about the edit and not the CRC.</summary>
    private static void Reseal(Span<byte> packet)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], DsuProtocol.ComputeCrc32(packet));
    }
}
