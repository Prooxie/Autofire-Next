using GameFlow.Core.Models;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// Builds the DualSense effect report that SDL's PS5 driver accepts
/// through <c>SDL_SendGamepadEffect</c>.
///
/// <para>
/// Adaptive triggers have no portable API — SDL exposes rumble and the
/// LED, but resistance curves are a device-specific output report. This
/// encoder produces that report's payload; SDL owns the framing (report
/// id, and the CRC that Bluetooth requires but USB does not), which is
/// why this goes through SDL rather than a raw HID write.
/// </para>
///
/// <para>
/// The layout mirrors SDL's <c>DS5EffectsState_t</c>. It is a fixed wire
/// contract with the controller firmware: the offsets below are the
/// format, not a choice, and the enable bits are what make the firmware
/// look at a section at all — a correct trigger payload with the wrong
/// enable bit is silently ignored, which is the failure mode this is
/// easiest to get wrong in.
/// </para>
/// </summary>
public static class DualSenseEffectEncoder
{
    /// <summary>Size of DS5EffectsState_t. SDL copies this whole block into the output report.</summary>
    public const int EffectStateLength = 47;

    // Offsets within DS5EffectsState_t.
    private const int OffsetEnableBits1 = 0;
    private const int OffsetEnableBits2 = 1;
    private const int OffsetRumbleRight = 2;
    private const int OffsetRumbleLeft = 3;
    private const int OffsetRightTriggerEffect = 10;
    private const int OffsetLeftTriggerEffect = 21;
    private const int OffsetLedRed = 44;
    private const int OffsetLedGreen = 45;
    private const int OffsetLedBlue = 46;

    // Enable bits. Without the matching bit the firmware ignores that
    // section entirely, whatever it contains.
    private const byte Enable1Rumble = 0x01;
    private const byte Enable1RightTrigger = 0x04;
    private const byte Enable1LeftTrigger = 0x08;
    private const byte Enable2Lightbar = 0x04;

    /// <summary>Trigger effect kinds as the DualSense firmware numbers them.</summary>
    private const byte TriggerOff = 0x05;
    private const byte TriggerFeedback = 0x21;
    private const byte TriggerWeapon = 0x25;
    private const byte TriggerVibration = 0x26;

    /// <summary>
    /// Writes a complete effect report into <paramref name="destination"/>,
    /// which must be at least <see cref="EffectStateLength"/> bytes.
    /// Returns false rather than throwing on a short buffer.
    /// </summary>
    public static bool TryWrite(
        Span<byte> destination,
        AdaptiveTriggerSettings? leftTrigger,
        AdaptiveTriggerSettings? rightTrigger,
        LightColor? led,
        double lowFrequencyRumble,
        double highFrequencyRumble)
    {
        if (destination.Length < EffectStateLength)
        {
            return false;
        }

        var report = destination[..EffectStateLength];
        report.Clear();

        byte enable1 = 0;
        byte enable2 = 0;

        // Rumble is included so a single report carries everything. Sending
        // triggers and rumble as separate reports lets the second clear
        // what the first set, because the enable bits are per-report.
        if (lowFrequencyRumble > 0 || highFrequencyRumble > 0)
        {
            enable1 |= Enable1Rumble;
            report[OffsetRumbleLeft] = ToByte(lowFrequencyRumble);
            report[OffsetRumbleRight] = ToByte(highFrequencyRumble);
        }

        if (rightTrigger is not null && WriteTrigger(report[OffsetRightTriggerEffect..], rightTrigger))
        {
            enable1 |= Enable1RightTrigger;
        }

        if (leftTrigger is not null && WriteTrigger(report[OffsetLeftTriggerEffect..], leftTrigger))
        {
            enable1 |= Enable1LeftTrigger;
        }

        if (led is { } color)
        {
            enable2 |= Enable2Lightbar;
            report[OffsetLedRed] = color.R;
            report[OffsetLedGreen] = color.G;
            report[OffsetLedBlue] = color.B;
        }

        report[OffsetEnableBits1] = enable1;
        report[OffsetEnableBits2] = enable2;
        return true;
    }

    /// <summary>
    /// Writes one 11-byte trigger block. Returns false when the mode
    /// produces no effect, so the caller can leave the enable bit clear
    /// rather than asking the firmware to apply nothing.
    /// </summary>
    private static bool WriteTrigger(Span<byte> block, AdaptiveTriggerSettings settings)
    {
        if (block.Length < 11)
        {
            return false;
        }

        block[..11].Clear();

        // The firmware's position scale is 0-255 across the pull. Start is
        // capped one below maximum so a fully-forward start still leaves
        // somewhere for the effect to act.
        var start = (byte)Math.Clamp(Math.Round(settings.StartPosition * 255), 0, 254);
        var end = (byte)Math.Clamp(Math.Round(settings.EndPosition * 255), 0, 255);
        var strength = (byte)Math.Clamp(Math.Round(settings.Strength * 255), 0, 255);

        // An inverted or empty band would make the effect vanish with no
        // indication why, so it is corrected to a minimal valid band.
        if (end <= start)
        {
            end = (byte)Math.Min(255, start + 1);
        }

        switch (settings.Mode)
        {
            case AdaptiveTriggerMode.Off:
                block[0] = TriggerOff;
                return true;

            // Uniform resistance from `start` onward.
            case AdaptiveTriggerMode.Feedback:
            case AdaptiveTriggerMode.SlopeFeedback:
            case AdaptiveTriggerMode.MultiplePositionFeedback:
                block[0] = TriggerFeedback;
                block[1] = start;
                block[2] = strength;
                return true;

            // Resistance across a band that then gives way — a trigger pull.
            case AdaptiveTriggerMode.Weapon:
                block[0] = TriggerWeapon;
                block[1] = start;
                block[2] = end;
                block[3] = strength;
                return true;

            case AdaptiveTriggerMode.Vibration:
            case AdaptiveTriggerMode.MultiplePositionVibration:
                block[0] = TriggerVibration;
                block[1] = start;
                block[2] = strength;
                block[3] = (byte)Math.Clamp(settings.FrequencyHz, 1, 255);
                return true;

            default:
                return false;
        }
    }

    private static byte ToByte(double unit) =>
        (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);
}
