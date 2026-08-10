using GameFlow.Core.Models;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// Everything the animated lighting modes need to know about the pad at
/// this instant. Passed in rather than read, so the resolver stays pure
/// and testable — no clock, no device, no globals.
/// </summary>
/// <param name="ElapsedSeconds">Seconds since the effect engine started. Drives every time-based mode.</param>
/// <param name="PlayerIndex">Zero-based slot index, for <see cref="LightbarMode.PlayerNumber"/>.</param>
/// <param name="BatteryPercent">0–100, or null when the pad does not report one.</param>
/// <param name="IsCharging">Whether the pad is charging, which the battery mode shows differently.</param>
/// <param name="RumbleLevel">Current rumble magnitude 0–1, for <see cref="LightbarMode.RumbleReactive"/>.</param>
/// <param name="TriggerLevel">Highest trigger pull 0–1, for <see cref="LightbarMode.TriggerReactive"/>.</param>
public readonly record struct EffectSceneContext(
    double ElapsedSeconds,
    int PlayerIndex = 0,
    int? BatteryPercent = null,
    bool IsCharging = false,
    double RumbleLevel = 0,
    double TriggerLevel = 0);

/// <summary>An 8-bit RGB triple. Core has no drawing dependency, so this is defined here.</summary>
public readonly record struct LightColor(byte R, byte G, byte B)
{
    public static LightColor Black => new(0, 0, 0);

    /// <summary>Scales all three channels. Used for brightness and for the animated modes.</summary>
    public LightColor Scaled(double factor)
    {
        var f = Math.Clamp(factor, 0d, 1d);
        return new LightColor(
            (byte)Math.Round(R * f),
            (byte)Math.Round(G * f),
            (byte)Math.Round(B * f));
    }

    /// <summary>
    /// Parses <c>#RRGGBB</c> / <c>RRGGBB</c>, tolerating an <c>#AARRGGBB</c>
    /// alpha prefix. Returns false rather than throwing: the value comes
    /// from a hand-editable profile.
    /// </summary>
    public static bool TryParse(string? hex, out LightColor color)
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
            color = new LightColor(r, g, b);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Turns saved lighting and rumble settings into the concrete values a
/// pad should be showing right now.
///
/// <para>
/// Kept in Core, pure, and driven by an explicit
/// <see cref="EffectSceneContext"/> rather than reading a clock: the
/// animated modes are the part most likely to be wrong in a way nobody
/// notices — a breathing curve that never reaches full, a strobe with an
/// uneven duty cycle, a battery colour that reads green at 5% — and none
/// of that is testable if the time source is ambient.
/// </para>
/// </summary>
public static class EffectSceneResolver
{
    /// <summary>Console-style player colours: blue, red, green, magenta.</summary>
    private static readonly LightColor[] PlayerColors =
    [
        new(0x00, 0x66, 0xFF),
        new(0xFF, 0x22, 0x22),
        new(0x22, 0xCC, 0x44),
        new(0xCC, 0x22, 0xCC),
    ];

    private const double BreathingPeriodSeconds = 4.0;
    private const double StrobePeriodSeconds = 0.5;
    private const double RainbowPeriodSeconds = 6.0;

    /// <summary>
    /// The colour the lightbar should show, or <see langword="null"/> when
    /// the light should be left off.
    /// </summary>
    public static LightColor? ResolveLight(LightingSettings settings, in EffectSceneContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Mode == LightbarMode.Off)
        {
            return null;
        }

        var baseColor = LightColor.TryParse(settings.Color, out var parsed)
            ? parsed
            : PlayerColors[0];

        var color = settings.Mode switch
        {
            LightbarMode.Solid => baseColor,

            // Wraps rather than going dark past the fourth pad: sixteen
            // slots are supported and an unlit pad reads as broken.
            LightbarMode.PlayerNumber =>
                PlayerColors[((context.PlayerIndex % PlayerColors.Length) + PlayerColors.Length) % PlayerColors.Length],

            // Never fades fully to black — a lightbar that goes out looks
            // like a disconnection. Floor at 15%.
            LightbarMode.Breathing =>
                baseColor.Scaled(0.15 + (0.85 * Wave(context.ElapsedSeconds, BreathingPeriodSeconds))),

            LightbarMode.BatteryLevel => BatteryColor(context),

            // Square wave, even duty cycle.
            LightbarMode.Strobe =>
                Phase(context.ElapsedSeconds, StrobePeriodSeconds) < 0.5 ? baseColor : LightColor.Black,

            LightbarMode.Rainbow => Rainbow(Phase(context.ElapsedSeconds, RainbowPeriodSeconds)),

            // Idle floor so the pad is still lit when nothing is shaking.
            LightbarMode.RumbleReactive =>
                baseColor.Scaled(0.2 + (0.8 * Math.Clamp(context.RumbleLevel, 0d, 1d))),

            LightbarMode.TriggerReactive =>
                context.TriggerLevel > 0.05
                    ? new LightColor(0xFF, 0x22, 0x22).Scaled(Math.Clamp(context.TriggerLevel, 0.25d, 1d))
                    : baseColor,

            _ => baseColor,
        };

        // Brightness applies last so it scales whatever the mode produced,
        // including the animated modes.
        var brightness = Math.Clamp(settings.Brightness, 0f, 1f);
        return color.Scaled(brightness);
    }

    /// <summary>
    /// Applies rumble settings to the raw magnitudes a game asked for,
    /// returning (low, high) in 0–1.
    /// </summary>
    public static (double Low, double High) ResolveRumble(
        RumbleSettings settings, double lowFrequency, double highFrequency)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return (0d, 0d);
        }

        var low = Math.Clamp(lowFrequency, 0d, 1d);
        var high = Math.Clamp(highFrequency, 0d, 1d);

        if (settings.SwapMotors)
        {
            (low, high) = (high, low);
        }

        // Gain is allowed above 1 to rescue a weak pad, so the result is
        // clamped after multiplying rather than trusting the inputs.
        var gain = Math.Max(0d, settings.Gain);
        low = Math.Clamp(low * gain * Math.Max(0d, settings.LowFrequencyGain), 0d, 1d);
        high = Math.Clamp(high * gain * Math.Max(0d, settings.HighFrequencyGain), 0d, 1d);

        return (low, high);
    }

    /// <summary>Green above 60%, amber to 25%, red below. Charging shows a breathing green.</summary>
    private static LightColor BatteryColor(in EffectSceneContext context)
    {
        if (context.IsCharging)
        {
            return new LightColor(0x22, 0xCC, 0x44)
                .Scaled(0.25 + (0.75 * Wave(context.ElapsedSeconds, BreathingPeriodSeconds)));
        }

        // No reading is not the same as an empty battery: show the neutral
        // player colour rather than a red "about to die" that is not true.
        if (context.BatteryPercent is not { } percent)
        {
            return PlayerColors[0];
        }

        return percent switch
        {
            > 60 => new LightColor(0x22, 0xCC, 0x44),
            > 25 => new LightColor(0xFF, 0xAA, 0x22),
            _ => new LightColor(0xFF, 0x22, 0x22),
        };
    }

    /// <summary>Position within one cycle, 0 to 1.</summary>
    private static double Phase(double elapsedSeconds, double periodSeconds)
    {
        if (periodSeconds <= 0)
        {
            return 0;
        }

        var phase = (elapsedSeconds % periodSeconds) / periodSeconds;
        return phase < 0 ? phase + 1 : phase;
    }

    /// <summary>Smooth 0→1→0 over one period. Cosine, so the turns are gentle.</summary>
    private static double Wave(double elapsedSeconds, double periodSeconds) =>
        (1 - Math.Cos(Phase(elapsedSeconds, periodSeconds) * 2 * Math.PI)) / 2;

    /// <summary>Full-saturation hue sweep. Hand-rolled so Core needs no colour library.</summary>
    private static LightColor Rainbow(double phase)
    {
        var h = Math.Clamp(phase, 0d, 1d) * 6d;
        var sector = (int)Math.Floor(h) % 6;
        var f = h - Math.Floor(h);

        byte Up() => (byte)Math.Round(f * 255);
        byte Down() => (byte)Math.Round((1 - f) * 255);

        return sector switch
        {
            0 => new LightColor(255, Up(), 0),
            1 => new LightColor(Down(), 255, 0),
            2 => new LightColor(0, 255, Up()),
            3 => new LightColor(0, Down(), 255),
            4 => new LightColor(Up(), 0, 255),
            _ => new LightColor(255, 0, Down()),
        };
    }
}
