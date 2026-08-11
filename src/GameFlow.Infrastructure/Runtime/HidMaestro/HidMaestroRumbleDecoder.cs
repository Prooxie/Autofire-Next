namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// Decodes the two output shapes HIDMaestro documents: XInput's raw
/// vibration packet and profile-decoded semantic motor fields.
/// </summary>
internal static class HidMaestroRumbleDecoder
{
    /// <summary>
    /// XUSB normally publishes five bytes
    /// <c>[command, size, low, high, trailer]</c>. HIDMaestro also allows
    /// the four-byte native <c>XINPUT_VIBRATION</c> struct, whose two
    /// magnitudes are little-endian 16-bit values.
    /// </summary>
    public static bool TryDecodeXInput(
        ReadOnlySpan<byte> data,
        out double lowFrequency,
        out double highFrequency)
    {
        lowFrequency = 0;
        highFrequency = 0;

        if (data.Length == 5)
        {
            lowFrequency = data[2] / 255d;
            highFrequency = data[3] / 255d;
            return true;
        }

        if (data.Length == 4)
        {
            lowFrequency = (data[0] | (data[1] << 8)) / (double)ushort.MaxValue;
            highFrequency = (data[2] | (data[3] << 8)) / (double)ushort.MaxValue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sony and Switch profiles expose decoded <c>leftMotor</c> and
    /// <c>rightMotor</c> fields through HIDMaestro's OutputDecoded event.
    /// Sony profiles also expose their output-validity flags. Those flags
    /// must authorize the motor fields: every profile decoder emits motor
    /// entries even for an LED- or adaptive-trigger-only report, where the
    /// zero bytes are padding rather than a request to stop rumble.
    ///
    /// <para>
    /// DualShock 4 uses bit 0 of <c>validFlag0</c> for rumble. DualSense
    /// uses bits 0-1 of <c>validFlag0</c> for legacy/audio-haptic rumble and
    /// bit 2 of <c>validFlag2</c> for the enhanced rumble lane. SDL's
    /// genuine DualSense zero-stop report has all three validity bytes
    /// clear, so that otherwise-empty shape is accepted as a stop. Reports
    /// carrying only LED or trigger validity are ignored. Switch semantic
    /// events have no validity fields and retain the original behavior.
    /// </para>
    ///
    /// Missing motor sides are treated as zero so a one-motor report still
    /// carries an unambiguous complete state.
    /// </summary>
    public static bool TryDecodeSemanticFields(
        IReadOnlyDictionary<string, object> fields,
        out double lowFrequency,
        out double highFrequency)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var hasLeft = TryGet(fields, "leftMotor", out var left);
        var hasRight = TryGet(fields, "rightMotor", out var right);

        if (!hasLeft && !hasRight)
        {
            lowFrequency = 0;
            highFrequency = 0;
            return false;
        }

        var hasValid0 = TryGet(fields, "validFlag0", out var valid0Value);
        var hasValid1 = TryGet(fields, "validFlag1", out var valid1Value);
        var hasValid2 = TryGet(fields, "validFlag2", out var valid2Value);

        // HIDMaestro's Switch decoder publishes only the two motor fields.
        // No validity fields means this is not one of the profile-decoded
        // Sony reports and the named motor values are authoritative.
        if (hasValid0 || hasValid1 || hasValid2)
        {
            byte valid0 = 0;
            byte valid1 = 0;
            byte valid2 = 0;
            if ((hasValid0 && !TryToByte(valid0Value, out valid0))
                || (hasValid1 && !TryToByte(valid1Value, out valid1))
                || (hasValid2 && !TryToByte(valid2Value, out valid2)))
            {
                lowFrequency = 0;
                highFrequency = 0;
                return false;
            }

            var flag0 = valid0;
            var flag1 = valid1;
            var flag2 = valid2;
            var isDualSense = hasValid2
                || Contains(fields, "leftTriggerEffect")
                || Contains(fields, "rightTriggerEffect");

            if (isDualSense)
            {
                var explicitlyCarriesRumble = (flag0 & 0x03) != 0 || (flag2 & 0x04) != 0;
                var isStandaloneZeroStop = flag0 == 0 && flag1 == 0 && flag2 == 0
                    && ToUnit(left) == 0d && ToUnit(right) == 0d;

                if (!explicitlyCarriesRumble && !isStandaloneZeroStop)
                {
                    lowFrequency = 0;
                    highFrequency = 0;
                    return false;
                }
            }
            else if ((flag0 & 0x01) == 0)
            {
                // DS4 bit 0 is the rumble-valid bit. LED-only packets set
                // other bits while leaving the decoded motor bytes at zero.
                lowFrequency = 0;
                highFrequency = 0;
                return false;
            }
        }

        lowFrequency = hasLeft ? ToUnit(left) : 0d;
        highFrequency = hasRight ? ToUnit(right) : 0d;
        return true;
    }

    private static bool Contains(IReadOnlyDictionary<string, object> fields, string name) =>
        fields.ContainsKey(name)
        || fields.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGet(
        IReadOnlyDictionary<string, object> fields,
        string name,
        out object value)
    {
        if (fields.TryGetValue(name, out value!))
        {
            return true;
        }

        foreach (var pair in fields)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryToByte(object value, out byte result)
    {
        try
        {
            result = Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            result = 0;
            return false;
        }
    }

    private static double ToUnit(object value)
    {
        try
        {
            var numeric = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return double.IsFinite(numeric)
                ? Math.Clamp(numeric, 0d, 255d) / 255d
                : 0d;
        }
        catch (Exception)
        {
            return 0d;
        }
    }
}
