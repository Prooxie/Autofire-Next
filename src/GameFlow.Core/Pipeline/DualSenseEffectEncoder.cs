using GameFlow.Core.Models;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// Builds a complete DualSense effect report that SDL's PS5 driver accepts
/// through <c>SDL_SendGamepadEffect</c>.
///
/// <para>
/// Adaptive triggers have no portable API — SDL exposes rumble and the
/// LED, but resistance curves are a device-specific output report.
/// SDL's portable rumble and LED APIs are still called first so SDL's
/// internal state remains synchronized. This encoder then produces the
/// final, atomic device state containing those portable channels together
/// with the trigger commands. DualSense output reports are stateful: a
/// trigger-only report can restore audio haptics and stop steady rumble,
/// while a later portable report can clear the trigger effect. The combined
/// final write avoids both order-dependent failures. SDL owns the framing
/// (report id, and the CRC that Bluetooth requires but USB does not), which
/// is why this goes through SDL rather than a raw HID write.
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
    private const int OffsetEnableBits3 = 38;
    private const int OffsetLedRed = 44;
    private const int OffsetLedGreen = 45;
    private const int OffsetLedBlue = 46;

    // Enable bits. Without the matching bit the firmware ignores that
    // section entirely, whatever it contains.
    private const byte Enable1LegacyRumble = 0x01;
    private const byte Enable1DisableAudioHaptics = 0x02;
    private const byte Enable1RightTrigger = 0x04;
    private const byte Enable1LeftTrigger = 0x08;
    private const byte Enable2Lightbar = 0x04;
    private const byte Enable3EnhancedRumble = 0x04;

    // SDL 3.4.2 uses the enhanced rumble lane for Sony pads on firmware
    // 0x0224+, for unknown Sony firmware (the Bluetooth fallback), and for
    // every DualSense Edge. All other PS5-driver devices use legacy emulation.
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseEdgeProductId = 0x0DF2;
    public const ushort EnhancedRumbleFirmwareMinimum = 0x0224;

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
        ushort lowFrequencyRumble,
        ushort highFrequencyRumble,
        DualSenseRumbleMode rumbleMode)
    {
        if (destination.Length < EffectStateLength)
        {
            return false;
        }

        var report = destination[..EffectStateLength];
        report.Clear();

        byte enable1 = 0;
        byte enable2 = 0;

        // Match HIDAPI_DriverPS5_UpdateEffects in bundled SDL 3.4.2.
        // SDL first reduces its public 16-bit magnitudes to the high byte.
        // Legacy firmware then halves that byte to match Xbox controller
        // strength; enhanced firmware uses the full byte and enable-bits-3.
        var left = (byte)(lowFrequencyRumble >> 8);
        var right = (byte)(highFrequencyRumble >> 8);
        if (left != 0 || right != 0)
        {
            enable1 |= Enable1DisableAudioHaptics;
            if (rumbleMode == DualSenseRumbleMode.Enhanced)
            {
                report[OffsetEnableBits3] |= Enable3EnhancedRumble;
                report[OffsetRumbleLeft] = left;
                report[OffsetRumbleRight] = right;
            }
            else
            {
                enable1 |= Enable1LegacyRumble;
                report[OffsetRumbleLeft] = (byte)(left >> 1);
                report[OffsetRumbleRight] = (byte)(right >> 1);
            }
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
    /// Mirrors SDL 3.4.2's <c>ctx-&gt;enhanced_rumble</c> decision so the
    /// final raw report uses the same lane and scaling as the portable call
    /// that preceded it.
    /// </summary>
    public static DualSenseRumbleMode ResolveRumbleMode(
        ushort vendorId,
        ushort productId,
        ushort firmwareVersion) =>
        vendorId == SonyVendorId &&
        (productId == DualSenseEdgeProductId ||
         firmwareVersion == 0 ||
         firmwareVersion >= EnhancedRumbleFirmwareMinimum)
            ? DualSenseRumbleMode.Enhanced
            : DualSenseRumbleMode.Legacy;

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
}

/// <summary>The two rumble encodings selected by SDL's PS5 HIDAPI driver.</summary>
public enum DualSenseRumbleMode
{
    Legacy,
    Enhanced,
}
