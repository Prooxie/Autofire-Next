using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Editor for one device's tuning as used by one slot — the UI surface
/// over <see cref="DeviceSettingsStore"/>.
///
/// <para>
/// Every setter writes straight through to the store, which persists
/// immediately and is read by the runtime tick, so changes take effect
/// live with no apply step (matching how mapping rules already behave).
/// A <see cref="suspendWrites"/> guard stops the bulk property refresh in
/// <see cref="Load"/> from writing each value back as it populates.
/// </para>
///
/// <para>
/// Stick/trigger conditioning is applied by the mapping pipeline. Rumble,
/// lighting, and adaptive triggers flow through the dedicated effects
/// thread to a connected, supported physical controller.
/// </para>
/// </summary>
public sealed class DeviceSettingsEditorViewModel : ViewModelBase
{
    private readonly DeviceSettingsStore store;
    private bool suspendWrites;

    private string slotId = string.Empty;
    private string deviceId = string.Empty;
    private string deviceName = string.Empty;

    private readonly GameFlow.Infrastructure.Runtime.Slots.SlotSnapshotStore snapshots;

    public DeviceSettingsEditorViewModel(
        DeviceSettingsStore store,
        GameFlow.Infrastructure.Runtime.Slots.SlotSnapshotStore snapshots)
    {
        this.store = store;
        this.snapshots = snapshots;
        CurveOptions = new ObservableCollection<StickCurve>(Enum.GetValues<StickCurve>());
        LightbarModeOptions = new ObservableCollection<LightbarMode>(Enum.GetValues<LightbarMode>());
        AdaptiveModeOptions = new ObservableCollection<AdaptiveTriggerMode>(Enum.GetValues<AdaptiveTriggerMode>());
    }

    /// <summary>
    /// Live magnitude of the left stick, 0–1, drawn as a marker on the
    /// response curve. Reading the PHYSICAL snapshot deliberately: the
    /// marker has to show what the hardware is sending, since the point of
    /// watching it is to pick a deadzone that matches this particular
    /// worn pad. Showing the post-shaping value would just trace the curve.
    /// </summary>
    public double LeftStickLive
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Live right-stick magnitude. See <see cref="LeftStickLive"/>.</summary>
    public double RightStickLive
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Re-reads the live stick magnitudes. Driven by the editor window's
    /// timer rather than a timer of its own, so a closed editor costs
    /// nothing.
    /// </summary>
    public void RefreshLive()
    {
        if (!HasDevice)
        {
            return;
        }

        var physical = snapshots.Get(slotId).Physical;
        LeftStickLive = physical.LeftStick.Magnitude;
        RightStickLive = physical.RightStick.Magnitude;
    }

    /// <summary>
    /// Named starting points, so the common cases do not require
    /// understanding five interacting numbers first. Each is a complete
    /// stick configuration, not a partial nudge, so applying one always
    /// lands somewhere predictable regardless of what was set before.
    /// </summary>
    public void ApplyStickPreset(string preset, bool leftStick)
    {
        var settings = preset switch
        {
            // A true pass-through. Matches DeviceSettings.Default, so the
            // tick can skip conditioning entirely.
            "default" => new StickSettings(),

            // Finer control near centre for aiming; still reaches full.
            "precision" => new StickSettings { Deadzone = 0.05f, FullAt = 1.0f, Curve = StickCurve.Precision },

            // Reaches high output sooner — twitchier, for fast turns.
            "aggressive" => new StickSettings { Deadzone = 0.05f, FullAt = 0.95f, Curve = StickCurve.Aggressive },

            // For a pad that drifts and no longer reaches its corners.
            "worn" => new StickSettings { Deadzone = 0.18f, AntiDeadzone = 0.05f, FullAt = 0.85f },

            _ => new StickSettings()
        };

        if (leftStick)
        {
            LeftDeadzone = settings.Deadzone;
            LeftAntiDeadzone = settings.AntiDeadzone;
            LeftFullAt = settings.FullAt;
            LeftSensitivity = settings.Sensitivity;
            LeftCurve = settings.Curve;
        }
        else
        {
            RightDeadzone = settings.Deadzone;
            RightAntiDeadzone = settings.AntiDeadzone;
            RightFullAt = settings.FullAt;
            RightSensitivity = settings.Sensitivity;
            RightCurve = settings.Curve;
        }
    }

    // Presets are exposed one command per (stick, preset) rather than one
    // parameterised command, because Avalonia's CommandParameter cannot
    // carry two values without a converter — and a converter for four
    // fixed buttons is more machinery than the eight lambdas it replaces.
    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyLeftDefaultCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("default", leftStick: true));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyLeftPrecisionCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("precision", leftStick: true));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyLeftAggressiveCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("aggressive", leftStick: true));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyLeftWornCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("worn", leftStick: true));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyRightDefaultCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("default", leftStick: false));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyRightPrecisionCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("precision", leftStick: false));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyRightAggressiveCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("aggressive", leftStick: false));

    public CommunityToolkit.Mvvm.Input.IRelayCommand ApplyRightWornCommand =>
        new CommunityToolkit.Mvvm.Input.RelayCommand(() => ApplyStickPreset("worn", leftStick: false));

    public ObservableCollection<StickCurve> CurveOptions { get; }
    public ObservableCollection<LightbarMode> LightbarModeOptions { get; }
    public ObservableCollection<AdaptiveTriggerMode> AdaptiveModeOptions { get; }

    public string DeviceName
    {
        get => deviceName;
        private set => SetProperty(ref deviceName, value);
    }

    public bool HasDevice => !string.IsNullOrEmpty(deviceId);

    /// <summary>True when this editor is changing the slot-wide fallback rather than one physical-device override.</summary>
    public bool IsSlotDefaults => DeviceSettingsStore.IsSlotDefaultsDevice(deviceId);

    public string TuningScopeDescription => IsSlotDefaults
        ? "These defaults apply to this virtual controller when an input has no device-specific override."
        : "Tuning applies to this device on this slot only.";

    public string EffectsStatusNote =>
        "Changes save immediately. Stick and trigger tuning shapes this slot's input; rumble, "
        + "lighting and adaptive triggers are sent on the effects thread to an assigned, supported "
        + "physical controller when it is connected.";

    /// <summary>Points the editor at one slot/device pair and loads its saved values.</summary>
    public void Load(string slotIdentifier, string deviceIdentifier, string displayName)
    {
        slotId = slotIdentifier;
        deviceId = deviceIdentifier;
        DeviceName = displayName;

        var settings = store.GetEffective(slotIdentifier, deviceIdentifier);

        suspendWrites = true;
        try
        {
            leftDeadzone = settings.LeftStick.Deadzone;
            leftAntiDeadzone = settings.LeftStick.AntiDeadzone;
            leftFullAt = settings.LeftStick.FullAt;
            leftSensitivity = settings.LeftStick.Sensitivity;
            leftCurve = settings.LeftStick.Curve;
            leftInvertX = settings.LeftStick.InvertX;
            leftInvertY = settings.LeftStick.InvertY;

            rightDeadzone = settings.RightStick.Deadzone;
            rightAntiDeadzone = settings.RightStick.AntiDeadzone;
            rightFullAt = settings.RightStick.FullAt;
            rightSensitivity = settings.RightStick.Sensitivity;
            rightCurve = settings.RightStick.Curve;
            rightInvertX = settings.RightStick.InvertX;
            rightInvertY = settings.RightStick.InvertY;

            leftTriggerDeadzone = settings.LeftTrigger.Deadzone;
            leftTriggerFullAt = settings.LeftTrigger.FullAt;
            rightTriggerDeadzone = settings.RightTrigger.Deadzone;
            rightTriggerFullAt = settings.RightTrigger.FullAt;

            rumbleEnabled = settings.Rumble.Enabled;
            rumbleGain = settings.Rumble.Gain;
            rumbleLowGain = settings.Rumble.LowFrequencyGain;
            rumbleHighGain = settings.Rumble.HighFrequencyGain;
            rumbleSwapMotors = settings.Rumble.SwapMotors;

            lightbarMode = settings.Lighting.Mode;
            lightbarColor = settings.Lighting.Color;
            lightbarBrightness = settings.Lighting.Brightness;
            indicatorBrightness = settings.Lighting.IndicatorBrightness;

            leftAdaptiveMode = settings.LeftAdaptiveTrigger.Mode;
            leftAdaptiveStart = settings.LeftAdaptiveTrigger.StartPosition;
            leftAdaptiveEnd = settings.LeftAdaptiveTrigger.EndPosition;
            leftAdaptiveStrength = settings.LeftAdaptiveTrigger.Strength;
            leftAdaptiveFrequency = settings.LeftAdaptiveTrigger.FrequencyHz;

            rightAdaptiveMode = settings.RightAdaptiveTrigger.Mode;
            rightAdaptiveStart = settings.RightAdaptiveTrigger.StartPosition;
            rightAdaptiveEnd = settings.RightAdaptiveTrigger.EndPosition;
            rightAdaptiveStrength = settings.RightAdaptiveTrigger.Strength;
            rightAdaptiveFrequency = settings.RightAdaptiveTrigger.FrequencyHz;
        }
        finally
        {
            suspendWrites = false;
        }

        RaiseAll();
    }

    /// <summary>Drops this device back to defaults.</summary>
    public void ResetAll()
    {
        if (!HasDevice)
        {
            return;
        }
        store.Reset(slotId, deviceId);
        Load(slotId, deviceId, DeviceName);
    }

    /// <summary>Rebuilds the whole record from current values and saves it.</summary>
    private void Persist()
    {
        if (suspendWrites || !HasDevice)
        {
            return;
        }

        store.Set(slotId, deviceId, new DeviceSettings
        {
            LeftStick = new StickSettings
            {
                Deadzone = leftDeadzone, AntiDeadzone = leftAntiDeadzone, FullAt = leftFullAt,
                Sensitivity = leftSensitivity, Curve = leftCurve,
                InvertX = leftInvertX, InvertY = leftInvertY,
            },
            RightStick = new StickSettings
            {
                Deadzone = rightDeadzone, AntiDeadzone = rightAntiDeadzone, FullAt = rightFullAt,
                Sensitivity = rightSensitivity, Curve = rightCurve,
                InvertX = rightInvertX, InvertY = rightInvertY,
            },
            LeftTrigger = new TriggerSettings { Deadzone = leftTriggerDeadzone, FullAt = leftTriggerFullAt },
            RightTrigger = new TriggerSettings { Deadzone = rightTriggerDeadzone, FullAt = rightTriggerFullAt },
            Rumble = new RumbleSettings
            {
                Enabled = rumbleEnabled, Gain = rumbleGain,
                LowFrequencyGain = rumbleLowGain, HighFrequencyGain = rumbleHighGain,
                SwapMotors = rumbleSwapMotors,
            },
            Lighting = new LightingSettings
            {
                Mode = lightbarMode, Color = lightbarColor,
                Brightness = lightbarBrightness, IndicatorBrightness = indicatorBrightness,
            },
            LeftAdaptiveTrigger = new AdaptiveTriggerSettings
            {
                Mode = leftAdaptiveMode, StartPosition = leftAdaptiveStart, EndPosition = leftAdaptiveEnd,
                Strength = leftAdaptiveStrength, FrequencyHz = leftAdaptiveFrequency,
            },
            RightAdaptiveTrigger = new AdaptiveTriggerSettings
            {
                Mode = rightAdaptiveMode, StartPosition = rightAdaptiveStart, EndPosition = rightAdaptiveEnd,
                Strength = rightAdaptiveStrength, FrequencyHz = rightAdaptiveFrequency,
            },
        });
    }

    /// <summary>Sets a backing field, notifies, and saves — the shared shape of every setter below.</summary>
    private void Apply<T>(ref T field, T value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            Persist();
        }
    }

    // ── Left stick ──
    private float leftDeadzone, leftAntiDeadzone, leftFullAt = 1f, leftSensitivity = 1f;
    private StickCurve leftCurve;
    private bool leftInvertX, leftInvertY;

    public float LeftDeadzone { get => leftDeadzone; set => Apply(ref leftDeadzone, value, nameof(LeftDeadzone)); }
    public float LeftAntiDeadzone { get => leftAntiDeadzone; set => Apply(ref leftAntiDeadzone, value, nameof(LeftAntiDeadzone)); }
    public float LeftFullAt { get => leftFullAt; set => Apply(ref leftFullAt, value, nameof(LeftFullAt)); }
    public float LeftSensitivity { get => leftSensitivity; set => Apply(ref leftSensitivity, value, nameof(LeftSensitivity)); }
    public StickCurve LeftCurve { get => leftCurve; set => Apply(ref leftCurve, value, nameof(LeftCurve)); }
    public bool LeftInvertX { get => leftInvertX; set => Apply(ref leftInvertX, value, nameof(LeftInvertX)); }
    public bool LeftInvertY { get => leftInvertY; set => Apply(ref leftInvertY, value, nameof(LeftInvertY)); }

    // ── Right stick ──
    private float rightDeadzone, rightAntiDeadzone, rightFullAt = 1f, rightSensitivity = 1f;
    private StickCurve rightCurve;
    private bool rightInvertX, rightInvertY;

    public float RightDeadzone { get => rightDeadzone; set => Apply(ref rightDeadzone, value, nameof(RightDeadzone)); }
    public float RightAntiDeadzone { get => rightAntiDeadzone; set => Apply(ref rightAntiDeadzone, value, nameof(RightAntiDeadzone)); }
    public float RightFullAt { get => rightFullAt; set => Apply(ref rightFullAt, value, nameof(RightFullAt)); }
    public float RightSensitivity { get => rightSensitivity; set => Apply(ref rightSensitivity, value, nameof(RightSensitivity)); }
    public StickCurve RightCurve { get => rightCurve; set => Apply(ref rightCurve, value, nameof(RightCurve)); }
    public bool RightInvertX { get => rightInvertX; set => Apply(ref rightInvertX, value, nameof(RightInvertX)); }
    public bool RightInvertY { get => rightInvertY; set => Apply(ref rightInvertY, value, nameof(RightInvertY)); }

    // ── Triggers ──
    private float leftTriggerDeadzone, leftTriggerFullAt = 1f, rightTriggerDeadzone, rightTriggerFullAt = 1f;

    public float LeftTriggerDeadzone { get => leftTriggerDeadzone; set => Apply(ref leftTriggerDeadzone, value, nameof(LeftTriggerDeadzone)); }
    public float LeftTriggerFullAt { get => leftTriggerFullAt; set => Apply(ref leftTriggerFullAt, value, nameof(LeftTriggerFullAt)); }
    public float RightTriggerDeadzone { get => rightTriggerDeadzone; set => Apply(ref rightTriggerDeadzone, value, nameof(RightTriggerDeadzone)); }
    public float RightTriggerFullAt { get => rightTriggerFullAt; set => Apply(ref rightTriggerFullAt, value, nameof(RightTriggerFullAt)); }

    // ── Rumble ──
    private bool rumbleEnabled = true;
    private float rumbleGain = 1f, rumbleLowGain = 1f, rumbleHighGain = 1f;
    private bool rumbleSwapMotors;

    public bool RumbleEnabled { get => rumbleEnabled; set => Apply(ref rumbleEnabled, value, nameof(RumbleEnabled)); }
    public float RumbleGain { get => rumbleGain; set => Apply(ref rumbleGain, value, nameof(RumbleGain)); }
    public float RumbleLowGain { get => rumbleLowGain; set => Apply(ref rumbleLowGain, value, nameof(RumbleLowGain)); }
    public float RumbleHighGain { get => rumbleHighGain; set => Apply(ref rumbleHighGain, value, nameof(RumbleHighGain)); }
    public bool RumbleSwapMotors { get => rumbleSwapMotors; set => Apply(ref rumbleSwapMotors, value, nameof(RumbleSwapMotors)); }

    // ── Lighting ──
    private LightbarMode lightbarMode = LightbarMode.PlayerNumber;
    private string lightbarColor = "#0066FF";
    private float lightbarBrightness = 1f, indicatorBrightness = 1f;

    public LightbarMode LightbarMode { get => lightbarMode; set => Apply(ref lightbarMode, value, nameof(LightbarMode)); }
    public string LightbarColor { get => lightbarColor; set => Apply(ref lightbarColor, value, nameof(LightbarColor)); }
    public float LightbarBrightness { get => lightbarBrightness; set => Apply(ref lightbarBrightness, value, nameof(LightbarBrightness)); }
    public float IndicatorBrightness { get => indicatorBrightness; set => Apply(ref indicatorBrightness, value, nameof(IndicatorBrightness)); }

    // ── Adaptive triggers ──
    private AdaptiveTriggerMode leftAdaptiveMode, rightAdaptiveMode;
    private float leftAdaptiveStart = 0.2f, leftAdaptiveEnd = 0.8f, leftAdaptiveStrength = 0.8f;
    private float rightAdaptiveStart = 0.2f, rightAdaptiveEnd = 0.8f, rightAdaptiveStrength = 0.8f;
    private int leftAdaptiveFrequency = 10, rightAdaptiveFrequency = 10;

    public AdaptiveTriggerMode LeftAdaptiveMode { get => leftAdaptiveMode; set => Apply(ref leftAdaptiveMode, value, nameof(LeftAdaptiveMode)); }
    public float LeftAdaptiveStart { get => leftAdaptiveStart; set => Apply(ref leftAdaptiveStart, value, nameof(LeftAdaptiveStart)); }
    public float LeftAdaptiveEnd { get => leftAdaptiveEnd; set => Apply(ref leftAdaptiveEnd, value, nameof(LeftAdaptiveEnd)); }
    public float LeftAdaptiveStrength { get => leftAdaptiveStrength; set => Apply(ref leftAdaptiveStrength, value, nameof(LeftAdaptiveStrength)); }
    public int LeftAdaptiveFrequency { get => leftAdaptiveFrequency; set => Apply(ref leftAdaptiveFrequency, value, nameof(LeftAdaptiveFrequency)); }

    public AdaptiveTriggerMode RightAdaptiveMode { get => rightAdaptiveMode; set => Apply(ref rightAdaptiveMode, value, nameof(RightAdaptiveMode)); }
    public float RightAdaptiveStart { get => rightAdaptiveStart; set => Apply(ref rightAdaptiveStart, value, nameof(RightAdaptiveStart)); }
    public float RightAdaptiveEnd { get => rightAdaptiveEnd; set => Apply(ref rightAdaptiveEnd, value, nameof(RightAdaptiveEnd)); }
    public float RightAdaptiveStrength { get => rightAdaptiveStrength; set => Apply(ref rightAdaptiveStrength, value, nameof(RightAdaptiveStrength)); }
    public int RightAdaptiveFrequency { get => rightAdaptiveFrequency; set => Apply(ref rightAdaptiveFrequency, value, nameof(RightAdaptiveFrequency)); }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(HasDevice),
            nameof(IsSlotDefaults), nameof(TuningScopeDescription),
            nameof(LeftDeadzone), nameof(LeftAntiDeadzone), nameof(LeftFullAt), nameof(LeftSensitivity),
            nameof(LeftCurve), nameof(LeftInvertX), nameof(LeftInvertY),
            nameof(RightDeadzone), nameof(RightAntiDeadzone), nameof(RightFullAt), nameof(RightSensitivity),
            nameof(RightCurve), nameof(RightInvertX), nameof(RightInvertY),
            nameof(LeftTriggerDeadzone), nameof(LeftTriggerFullAt),
            nameof(RightTriggerDeadzone), nameof(RightTriggerFullAt),
            nameof(RumbleEnabled), nameof(RumbleGain), nameof(RumbleLowGain), nameof(RumbleHighGain), nameof(RumbleSwapMotors),
            nameof(LightbarMode), nameof(LightbarColor), nameof(LightbarBrightness), nameof(IndicatorBrightness),
            nameof(LeftAdaptiveMode), nameof(LeftAdaptiveStart), nameof(LeftAdaptiveEnd), nameof(LeftAdaptiveStrength), nameof(LeftAdaptiveFrequency),
            nameof(RightAdaptiveMode), nameof(RightAdaptiveStart), nameof(RightAdaptiveEnd), nameof(RightAdaptiveStrength), nameof(RightAdaptiveFrequency),
        })
        {
            OnPropertyChanged(name);
        }
    }
}
