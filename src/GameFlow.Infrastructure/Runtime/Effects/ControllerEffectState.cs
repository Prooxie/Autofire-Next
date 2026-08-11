namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// The complete set of effects one physical pad should currently be
/// producing. A whole-state value rather than a stream of deltas, because
/// the transport is the bottleneck: only the newest state is worth
/// sending, and older ones are dropped rather than queued.
/// </summary>
public readonly record struct ControllerEffectState
{
    /// <summary>Low-frequency (heavy) motor, 0–1.</summary>
    public double LowFrequencyRumble { get; init; }

    /// <summary>High-frequency (light) motor, 0–1.</summary>
    public double HighFrequencyRumble { get; init; }

    /// <summary>Lightbar / player LED colour. Null leaves the LED alone.</summary>
    public EffectColor? LedColor { get; init; }

    /// <summary>
    /// Left adaptive-trigger effect. Null leaves the trigger alone;
    /// <see cref="AdaptiveTriggerEffect.Off"/> explicitly releases it.
    /// </summary>
    public AdaptiveTriggerCommand? LeftTrigger { get; init; }

    /// <summary>
    /// Right adaptive-trigger effect. Null leaves the trigger alone;
    /// <see cref="AdaptiveTriggerEffect.Off"/> explicitly releases it.
    /// </summary>
    public AdaptiveTriggerCommand? RightTrigger { get; init; }

    /// <summary>
    /// Everything off — what a slot is set to when it stops owning a pad.
    /// Trigger releases are explicit because DualSense firmware retains
    /// the last adaptive effect when that report section is merely absent.
    /// </summary>
    public static ControllerEffectState Silent => new()
    {
        LeftTrigger = AdaptiveTriggerCommand.Release,
        RightTrigger = AdaptiveTriggerCommand.Release,
    };

    /// <summary>
    /// True when no motor is running and nothing else is being driven.
    /// Used to decide whether a final "all off" write is still owed to a
    /// device that is going away.
    /// </summary>
    public bool IsSilent =>
        LowFrequencyRumble <= 0d &&
        HighFrequencyRumble <= 0d &&
        LedColor is null &&
        IsReleased(LeftTrigger) &&
        IsReleased(RightTrigger);

    private static bool IsReleased(AdaptiveTriggerCommand? command) =>
        command is null || command.Value.Effect == AdaptiveTriggerEffect.Off;
}

/// <summary>8-bit RGB. Deliberately not <c>System.Drawing.Color</c> — Infrastructure has no drawing dependency.</summary>
public readonly record struct EffectColor(byte R, byte G, byte B)
{
    /// <summary>
    /// Parses <c>#RRGGBB</c> / <c>RRGGBB</c> (and tolerates <c>#AARRGGBB</c>
    /// by ignoring the alpha). Returns <see langword="false"/> rather than
    /// throwing: the value comes from a user-editable settings file.
    /// </summary>
    public static bool TryParse(string? hex, out EffectColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var text = hex.Trim().TrimStart('#');
        if (text.Length == 8)
        {
            text = text[2..];
        }

        if (text.Length != 6)
        {
            return false;
        }

        static bool Hex(ReadOnlySpan<char> span, out byte value) =>
            byte.TryParse(span, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);

        if (Hex(text.AsSpan(0, 2), out var r) &&
            Hex(text.AsSpan(2, 2), out var g) &&
            Hex(text.AsSpan(4, 2), out var b))
        {
            color = new EffectColor(r, g, b);
            return true;
        }

        return false;
    }
}

/// <summary>
/// One adaptive-trigger effect, in the shape the DualSense HID report
/// wants: a mode plus up to three parameters. Kept generic rather than a
/// closed enum-per-mode so a new mode is a value change, not a type change.
/// </summary>
public readonly record struct AdaptiveTriggerCommand(
    AdaptiveTriggerEffect Effect,
    byte StartPosition,
    byte EndPosition,
    byte Strength,
    byte FrequencyHz = 10)
{
    /// <summary>An explicit free-travel instruction for retained firmware state.</summary>
    public static AdaptiveTriggerCommand Release => new(AdaptiveTriggerEffect.Off, 0, 0, 0);
}

/// <summary>Adaptive-trigger effect kinds the DualSense firmware understands.</summary>
public enum AdaptiveTriggerEffect : byte
{
    /// <summary>Free travel, no resistance.</summary>
    Off = 0,

    /// <summary>Constant resistance from a start position.</summary>
    Constant = 1,

    /// <summary>Resistance across a band, then release.</summary>
    Section = 2,

    /// <summary>Repeated resistance steps — the "machine gun" feel.</summary>
    Vibration = 3
}
