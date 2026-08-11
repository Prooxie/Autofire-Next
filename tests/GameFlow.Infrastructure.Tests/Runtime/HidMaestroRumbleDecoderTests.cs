using GameFlow.Infrastructure.Runtime.HidMaestro;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class HidMaestroRumbleDecoderTests
{
    [Fact]
    public void XusbPacketUsesMotorBytes_NotTheTrailer()
    {
        // HIDMaestro's verified XUSB shape. Byte four is framing and must
        // never be mistaken for the high-frequency motor.
        byte[] packet = [0x00, 0x00, 0xFF, 0x40, 0x02];

        Assert.True(HidMaestroRumbleDecoder.TryDecodeXInput(packet, out var low, out var high));
        Assert.Equal(1, low);
        Assert.InRange(high, 0.249, 0.252);
    }

    [Fact]
    public void ZeroPacketIsStillADecodedStop()
    {
        byte[] packet = [0, 0, 0, 0, 2];

        Assert.True(HidMaestroRumbleDecoder.TryDecodeXInput(packet, out var low, out var high));
        Assert.Equal(0, low);
        Assert.Equal(0, high);
    }

    [Fact]
    public void NativeFourByteVibrationUsesSixteenBitMagnitudes()
    {
        byte[] packet = [0xFF, 0xFF, 0x00, 0x80];

        Assert.True(HidMaestroRumbleDecoder.TryDecodeXInput(packet, out var low, out var high));
        Assert.Equal(1, low);
        Assert.InRange(high, 0.499, 0.501);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void UnknownXinputFrameLengthsAreRejected(int length)
    {
        var packet = new byte[length];

        Assert.False(HidMaestroRumbleDecoder.TryDecodeXInput(packet, out _, out _));
    }

    [Fact]
    public void DecodedProfileFieldsUseNamedMotorChannels()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["leftMotor"] = (byte)255,
            ["rightMotor"] = (byte)64,
        };

        Assert.True(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high));
        Assert.Equal(1, low);
        Assert.InRange(high, 0.249, 0.252);
    }

    [Fact]
    public void DualSenseTriggerOnlyReportDoesNotBecomeAFalseStop()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["validFlag0"] = (byte)0x0C,
            ["validFlag1"] = (byte)0,
            ["validFlag2"] = (byte)0,
            ["leftMotor"] = (byte)0,
            ["rightMotor"] = (byte)0,
            ["leftTriggerEffect"] = new byte[11],
            ["rightTriggerEffect"] = new byte[11],
        };

        Assert.False(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out _, out _));
    }

    [Fact]
    public void DualSenseLedOnlyReportDoesNotBecomeAFalseStop()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["validFlag0"] = (byte)0,
            ["validFlag1"] = (byte)0x04,
            ["validFlag2"] = (byte)0,
            ["leftMotor"] = (byte)0,
            ["rightMotor"] = (byte)0,
        };

        Assert.False(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out _, out _));
    }

    [Fact]
    public void DualSenseAllClearReportPreservesGenuineZeroStop()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["validFlag0"] = (byte)0,
            ["validFlag1"] = (byte)0,
            ["validFlag2"] = (byte)0,
            ["leftMotor"] = (byte)0,
            ["rightMotor"] = (byte)0,
        };

        Assert.True(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high));
        Assert.Equal(0, low);
        Assert.Equal(0, high);
    }

    [Theory]
    [InlineData(0x03, 0x00)] // legacy rumble lane
    [InlineData(0x02, 0x04)] // enhanced rumble lane
    public void DualSenseRumbleValidityCarriesMotorState(int valid0, int valid2)
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["validFlag0"] = (byte)valid0,
            ["validFlag1"] = (byte)0,
            ["validFlag2"] = (byte)valid2,
            ["leftMotor"] = (byte)192,
            ["rightMotor"] = (byte)64,
        };

        Assert.True(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high));
        Assert.InRange(low, 0.752, 0.754);
        Assert.InRange(high, 0.250, 0.252);
    }

    [Fact]
    public void DualShockLedOnlyReportDoesNotBecomeAFalseStop()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["validFlag0"] = (byte)0x02,
            ["validFlag1"] = (byte)0,
            ["leftMotor"] = (byte)0,
            ["rightMotor"] = (byte)0,
        };

        Assert.False(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out _, out _));
    }

    [Fact]
    public void DualShockRumbleValidBitPreservesZeroStop()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["validFlag0"] = (byte)0x01,
            ["validFlag1"] = (byte)0,
            ["leftMotor"] = (byte)0,
            ["rightMotor"] = (byte)0,
        };

        Assert.True(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high));
        Assert.Equal(0, low);
        Assert.Equal(0, high);
    }

    [Fact]
    public void SwitchSemanticFieldsRemainValidWithoutSonyFlags()
    {
        IReadOnlyDictionary<string, object> fields = new Dictionary<string, object>
        {
            ["leftMotor"] = (byte)128,
            ["rightMotor"] = (byte)32,
        };

        Assert.True(HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high));
        Assert.InRange(low, 0.501, 0.503);
        Assert.InRange(high, 0.125, 0.126);
    }
}
