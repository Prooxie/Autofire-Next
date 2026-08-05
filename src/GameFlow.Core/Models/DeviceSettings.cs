using System.Text.Json.Serialization;

namespace GameFlow.Core.Models;

/// <summary>Response curve applied to a stick's magnitude after deadzone shaping.</summary>
public enum StickCurve
{
    /// <summary>1:1 — what the hardware reports is what the game gets.</summary>
    Linear,

    /// <summary>Squared — finer control near centre, full speed still reachable at the edge.</summary>
    Precision,

    /// <summary>Square-rooted — reaches high values sooner; twitchier.</summary>
    Aggressive
}

/// <summary>
/// Per-stick input conditioning. Applied to the PHYSICAL reading before
/// any mapping rule sees it, so every rule downstream operates on an
/// already-cleaned signal.
/// </summary>
public sealed record StickSettings
{
    /// <summary>
    /// Magnitude below this reads as fully centred — kills drift on a worn
    /// stick. Off by default: an untuned device has to be a true
    /// pass-through, so <see cref="DeviceSettings.Default"/> stays identity
    /// and the tick path can skip conditioning outright. Mapping rules and
    /// the games themselves already apply their own deadzones; a silent one
    /// here would stack underneath every one of them.
    /// </summary>
    [JsonPropertyName("deadzone")]
    public float Deadzone { get; init; }

    /// <summary>
    /// Minimum output magnitude once outside the deadzone. Games that
    /// apply their OWN deadzone can swallow small movements entirely;
    /// raising this pushes past that so the stick responds immediately.
    /// </summary>
    [JsonPropertyName("antiDeadzone")]
    public float AntiDeadzone { get; init; }

    /// <summary>Magnitude at which output saturates to full. Below 1.0 means a worn stick that no longer reaches its corners can still hit 100%.</summary>
    [JsonPropertyName("fullAt")]
    public float FullAt { get; init; } = 1.0f;

    [JsonPropertyName("sensitivity")]
    public float Sensitivity { get; init; } = 1.0f;

    [JsonPropertyName("curve")]
    public StickCurve Curve { get; init; } = StickCurve.Linear;

    [JsonPropertyName("invertX")]
    public bool InvertX { get; init; }

    [JsonPropertyName("invertY")]
    public bool InvertY { get; init; }
}

/// <summary>Per-trigger input conditioning, same "applied before rules" contract as <see cref="StickSettings"/>.</summary>
public sealed record TriggerSettings
{
    [JsonPropertyName("deadzone")]
    public float Deadzone { get; init; }

    [JsonPropertyName("fullAt")]
    public float FullAt { get; init; } = 1.0f;

    [JsonPropertyName("sensitivity")]
    public float Sensitivity { get; init; } = 1.0f;

    [JsonPropertyName("invert")]
    public bool Invert { get; init; }
}

/// <summary>Rumble scaling for the assigned physical pad.</summary>
public sealed record RumbleSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>Overall multiplier, 0..2. Above 1 amplifies a weak pad; 0 silences it without touching the game.</summary>
    [JsonPropertyName("gain")]
    public float Gain { get; init; } = 1.0f;

    [JsonPropertyName("lowFrequencyGain")]
    public float LowFrequencyGain { get; init; } = 1.0f;

    [JsonPropertyName("highFrequencyGain")]
    public float HighFrequencyGain { get; init; } = 1.0f;

    /// <summary>Swaps the two motors — some pads wire them opposite to what games assume.</summary>
    [JsonPropertyName("swapMotors")]
    public bool SwapMotors { get; init; }
}

/// <summary>How a DualSense-class lightbar behaves when no game is driving it.</summary>
public enum LightbarMode
{
    Off,
    Solid,
    PlayerNumber,
    Breathing,
    BatteryLevel
}

public sealed record LightingSettings
{
    [JsonPropertyName("mode")]
    public LightbarMode Mode { get; init; } = LightbarMode.PlayerNumber;

    /// <summary>Hex RGB used by <see cref="LightbarMode.Solid"/> and as the base for <see cref="LightbarMode.Breathing"/>.</summary>
    [JsonPropertyName("color")]
    public string Color { get; init; } = "#0066FF";

    /// <summary>0..1.</summary>
    [JsonPropertyName("brightness")]
    public float Brightness { get; init; } = 1.0f;

    /// <summary>Player-indicator LED row brightness (DualSense) / Guide button brightness (Xbox), 0..1.</summary>
    [JsonPropertyName("indicatorBrightness")]
    public float IndicatorBrightness { get; init; } = 1.0f;
}

/// <summary>DualSense adaptive trigger effect. Values mirror the hardware's own effect set.</summary>
public enum AdaptiveTriggerMode
{
    Off,
    Feedback,
    Weapon,
    Vibration,
    SlopeFeedback,
    MultiplePositionFeedback,
    MultiplePositionVibration
}

public sealed record AdaptiveTriggerSettings
{
    [JsonPropertyName("mode")]
    public AdaptiveTriggerMode Mode { get; init; } = AdaptiveTriggerMode.Off;

    /// <summary>Where along the pull the effect starts, 0..1.</summary>
    [JsonPropertyName("startPosition")]
    public float StartPosition { get; init; } = 0.2f;

    /// <summary>Where it ends, 0..1. Must exceed <see cref="StartPosition"/> to have any effect.</summary>
    [JsonPropertyName("endPosition")]
    public float EndPosition { get; init; } = 0.8f;

    /// <summary>Resistance strength, 0..1.</summary>
    [JsonPropertyName("strength")]
    public float Strength { get; init; } = 0.8f;

    /// <summary>Vibration frequency in Hz for the vibration-based modes.</summary>
    [JsonPropertyName("frequencyHz")]
    public int FrequencyHz { get; init; } = 10;
}

/// <summary>
/// Everything tunable about ONE physical device as used by ONE slot.
///
/// <para>
/// Deliberately scoped per slot AND per device, not per device alone:
/// the same pad assigned to two slots can be tuned two different ways
/// (a twitchy config on one, a precise one on the other) without the
/// two fighting each other. That's why <see cref="DeviceSettingsKey"/>
/// carries both ids.
/// </para>
///
/// <para>
/// Split by what actually consumes it. Sticks and triggers are INPUT
/// conditioning — <see cref="Pipeline.DeviceSettingsProcessor"/> applies
/// them to the physical snapshot before any mapping rule runs, so they
/// work today on every platform. Rumble, lighting, and adaptive triggers
/// are OUTPUT effects that have to be written back to the hardware; the
/// settings persist and round-trip correctly, but the write path needs
/// a dedicated effects thread (SDL writes block the runtime tick over
/// Bluetooth), which isn't built yet.
/// </para>
/// </summary>
public sealed record DeviceSettings
{
    [JsonPropertyName("leftStick")]
    public StickSettings LeftStick { get; init; } = new();

    [JsonPropertyName("rightStick")]
    public StickSettings RightStick { get; init; } = new();

    [JsonPropertyName("leftTrigger")]
    public TriggerSettings LeftTrigger { get; init; } = new();

    [JsonPropertyName("rightTrigger")]
    public TriggerSettings RightTrigger { get; init; } = new();

    [JsonPropertyName("rumble")]
    public RumbleSettings Rumble { get; init; } = new();

    [JsonPropertyName("lighting")]
    public LightingSettings Lighting { get; init; } = new();

    [JsonPropertyName("leftAdaptiveTrigger")]
    public AdaptiveTriggerSettings LeftAdaptiveTrigger { get; init; } = new();

    [JsonPropertyName("rightAdaptiveTrigger")]
    public AdaptiveTriggerSettings RightAdaptiveTrigger { get; init; } = new();

    /// <summary>Default-everything instance — used whenever a slot/device pair has never been tuned.</summary>
    public static DeviceSettings Default { get; } = new();
}

/// <summary>Identifies one tuning entry: this device, as used by this slot.</summary>
public readonly record struct DeviceSettingsKey(string SlotId, string DeviceId)
{
    public override string ToString() => $"{SlotId}::{DeviceId}";
}
