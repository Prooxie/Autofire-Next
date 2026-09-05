using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using Xunit;

namespace GameFlow.Core.Tests;

/// <summary>
/// The byte layout is a fixed contract with the controller firmware and
/// cannot be checked by looking at the pad — a wrong offset or a missing
/// enable bit produces no resistance and no error. These pin it.
/// </summary>
public sealed class DualSenseEffectEncoderTests
{
    private static byte[] Encode(
        AdaptiveTriggerSettings? left = null,
        AdaptiveTriggerSettings? right = null,
        LightColor? led = null,
        ushort lowFrequencyRumble = 0,
        ushort highFrequencyRumble = 0,
        DualSenseRumbleMode rumbleMode = DualSenseRumbleMode.Enhanced)
    {
        var buffer = new byte[DualSenseEffectEncoder.EffectStateLength];
        Assert.True(DualSenseEffectEncoder.TryWrite(
            buffer,
            left,
            right,
            led,
            lowFrequencyRumble,
            highFrequencyRumble,
            rumbleMode));
        return buffer;
    }

    private static AdaptiveTriggerSettings Trigger(
        AdaptiveTriggerMode mode, float start = 0.2f, float end = 0.8f, float strength = 0.8f, int hz = 10) =>
        new() { Mode = mode, StartPosition = start, EndPosition = end, Strength = strength, FrequencyHz = hz };

    [Fact]
    public void ATooSmallBufferIsRefusedRatherThanOverrunning()
    {
        var tiny = new byte[10];
        Assert.False(DualSenseEffectEncoder.TryWrite(
            tiny, null, null, null, 0, 0, DualSenseRumbleMode.Enhanced));
    }

    [Fact]
    public void NothingRequestedEnablesNothing()
    {
        var report = Encode();

        // Enable bits clear means the firmware leaves every section alone.
        // Sending zeroed sections WITH the bits set would instead actively
        // switch things off, which is a different instruction.
        Assert.Equal(0, report[0]);
        Assert.Equal(0, report[1]);
        Assert.Equal(0, report[38]);
    }

    [Fact]
    public void TheRightTriggerBlockSitsAtItsDocumentedOffset()
    {
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Feedback, start: 0.5f, strength: 1f));

        Assert.Equal(0x04, report[0] & 0x04);
        Assert.Equal(0x21, report[10]);            // effect kind

        // Zones from the halfway point to the end are engaged. The mask is
        // ten bits over two bytes, so the upper zones land in the second.
        var activeZones = report[11] | (report[12] << 8);
        Assert.Equal(0, activeZones & 0b0000_0111);   // below the start: idle
        Assert.Equal(0b11_1111_0000, activeZones & 0b11_1111_0000);
    }

    [Fact]
    public void TheLeftTriggerBlockSitsElevenBytesAfterTheRight()
    {
        var report = Encode(left: Trigger(AdaptiveTriggerMode.Feedback, start: 0.5f, strength: 1f));

        Assert.Equal(0x08, report[0] & 0x08);
        Assert.Equal(0x21, report[21]);

        var activeZones = report[22] | (report[23] << 8);
        Assert.Equal(0b11_1111_0000, activeZones & 0b11_1111_0000);
    }

    [Fact]
    public void TheTwoTriggersDoNotOverwriteEachOther()
    {
        var report = Encode(
            left: Trigger(AdaptiveTriggerMode.Weapon),
            right: Trigger(AdaptiveTriggerMode.Vibration));

        // Offset 10 is the RIGHT trigger, 21 is the LEFT — so the mode
        // passed as `right` must land at 10, not the other way round.
        Assert.Equal(0x26, report[10]);   // right = vibration
        Assert.Equal(0x25, report[21]);   // left  = weapon
        Assert.Equal(0x0C, report[0] & 0x0C);
    }

    /// <summary>
    /// Weapon addresses its band as a mask with exactly two bits set — the
    /// zone the resistance starts in and the one it gives way at — not as
    /// two position bytes.
    /// </summary>
    [Fact]
    public void WeaponModeCarriesBothEdgesOfItsBand()
    {
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Weapon, start: 0.25f, end: 0.75f, strength: 1f));

        Assert.Equal(0x25, report[10]);

        var band = report[11] | (report[12] << 8);
        Assert.Equal(2, System.Numerics.BitOperations.PopCount((uint)band));
        Assert.Equal(1 << 2, band & (1 << 2));   // start zone
        Assert.Equal(1 << 7, band & (1 << 7));   // stop zone
        Assert.Equal(7, report[13]);             // strength, zero-based
    }

    /// <summary>
    /// The regression this file exists for.
    /// </summary>
    /// <remarks>
    /// Frequency is the NINTH parameter of effect 0x26, not the third.
    /// Written at the third it landed inside the per-zone amplitude mask
    /// and left the frequency byte at zero — a buzz with no rate to buzz
    /// at, which the pad performs as complete silence. The effect id, the
    /// enable bit and the write all looked correct, so nothing anywhere
    /// reported a problem.
    /// </remarks>
    [Fact]
    public void VibrationModeCarriesItsFrequency()
    {
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Vibration, hz: 40));

        Assert.Equal(0x26, report[10]);
        Assert.Equal(40, report[19]);            // block[9]
        Assert.NotEqual(0, report[11] | report[12]);   // some zone is engaged
    }

    /// <summary>
    /// A buzz at no frequency is silence, so it is reported as "off"
    /// rather than arming a trigger that cannot do anything.
    /// </summary>
    [Fact]
    public void VibrationWithNoFrequencyIsNotArmed()
    {
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Vibration, hz: 0));

        Assert.Equal(0x05, report[10]);
    }

    /// <summary>
    /// Zero strength likewise. Every extended effect encodes amplitude as
    /// level-minus-one, so a zero would wrap to seven — full strength —
    /// which is the opposite of what was asked for.
    /// </summary>
    [Fact]
    public void ZeroStrengthIsNotArmed()
    {
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Feedback, strength: 0f));

        Assert.Equal(0x05, report[10]);
    }

    [Fact]
    public void AnInvertedBandIsCorrectedRatherThanSilentlyDoingNothing()
    {
        // end <= start would make the effect vanish with no indication of
        // why, and these come from user-editable sliders.
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Weapon, start: 0.9f, end: 0.1f));

        Assert.True(report[12] > report[11], "end must end up above start");
    }

    [Fact]
    public void OffIsAnInstructionToRelease_NotAnAbsentSection()
    {
        // Off has to be SENT — with its enable bit — or the firmware keeps
        // applying the previous effect forever.
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Off));

        Assert.Equal(0x04, report[0] & 0x04);
        Assert.Equal(0x05, report[10]);
    }

    [Fact]
    public void FinalTriggerReportAtomicallyCarriesEnhancedRumbleAndLighting()
    {
        var report = Encode(
            left: Trigger(AdaptiveTriggerMode.Feedback),
            right: Trigger(AdaptiveTriggerMode.Weapon),
            led: new LightColor(0x12, 0x34, 0x56),
            lowFrequencyRumble: 0xC000,
            highFrequencyRumble: 0x4000,
            rumbleMode: DualSenseRumbleMode.Enhanced);

        Assert.Equal(0x0E, report[0] & 0x0F); // audio-haptics off + both triggers; no legacy lane
        Assert.Equal(0x04, report[38] & 0x04); // enhanced-rumble lane
        Assert.Equal(0xC0, report[3]); // low-frequency / left motor
        Assert.Equal(0x40, report[2]); // high-frequency / right motor
        Assert.Equal(0x04, report[1] & 0x04);
        Assert.Equal(0x12, report[44]);
        Assert.Equal(0x34, report[45]);
        Assert.Equal(0x56, report[46]);
    }

    [Fact]
    public void LegacyRumbleUsesEnableBitsOneAndSdlStrengthScaling()
    {
        var report = Encode(
            lowFrequencyRumble: ushort.MaxValue,
            highFrequencyRumble: 0x8000,
            rumbleMode: DualSenseRumbleMode.Legacy);

        Assert.Equal(0x03, report[0] & 0x03); // legacy emulation + disable audio haptics
        Assert.Equal(0, report[38] & 0x04);
        Assert.Equal(0x7F, report[3]); // (0xFFFF >> 8) >> 1
        Assert.Equal(0x40, report[2]); // (0x8000 >> 8) >> 1
    }

    [Fact]
    public void SubBytePortableRumbleMatchesSdlAndDoesNotEnableAZeroMotor()
    {
        var report = Encode(
            lowFrequencyRumble: 0x00FF,
            rumbleMode: DualSenseRumbleMode.Legacy);

        Assert.Equal(0, report[0] & 0x03);
        Assert.Equal(0, report[2]);
        Assert.Equal(0, report[3]);
    }

    [Theory]
    [InlineData(0x054C, 0x0CE6, 0x0000, DualSenseRumbleMode.Enhanced)]
    [InlineData(0x054C, 0x0CE6, 0x0223, DualSenseRumbleMode.Legacy)]
    [InlineData(0x054C, 0x0CE6, 0x0224, DualSenseRumbleMode.Enhanced)]
    [InlineData(0x054C, 0x0DF2, 0x0001, DualSenseRumbleMode.Enhanced)]
    [InlineData(0x1532, 0x100B, 0x0000, DualSenseRumbleMode.Legacy)]
    public void RumbleModeMatchesBundledSdlDecision(
        int vendorId,
        int productId,
        int firmwareVersion,
        DualSenseRumbleMode expected)
    {
        Assert.Equal(
            expected,
            DualSenseEffectEncoder.ResolveRumbleMode(
                (ushort)vendorId,
                (ushort)productId,
                (ushort)firmwareVersion));
    }

    [Fact]
    public void PositionsAndStrengthAreClampedIntoRange()
    {
        var report = Encode(right: Trigger(AdaptiveTriggerMode.Feedback, start: 5f, strength: 9f));

        // These come from user-editable sliders, so out-of-range has to
        // land somewhere valid rather than wrapping. A start past the end
        // of travel keeps only the last zone, and an over-strength value
        // saturates at the firmware's top level rather than overflowing
        // the three bits each zone gets.
        Assert.Equal(0x21, report[10]);

        var activeZones = report[11] | (report[12] << 8);
        Assert.Equal(1 << 9, activeZones);

        var amplitudeZones = report[13]
            | (report[14] << 8)
            | (report[15] << 16)
            | (report[16] << 24);
        Assert.Equal(7, (amplitudeZones >> 27) & 0x07);
    }

    [Fact]
    public void TheReportIsExactlyTheStructSize()
    {
        Assert.Equal(47, DualSenseEffectEncoder.EffectStateLength);
        Assert.Equal(47, Encode().Length);
    }
}
