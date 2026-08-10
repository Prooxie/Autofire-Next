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
/// Stick/trigger conditioning is applied by the pipeline today. Rumble,
/// lighting, and adaptive triggers persist and round-trip correctly, but
/// reaching the hardware needs the dedicated effects thread that isn't
/// built yet — <see cref="EffectsPendingNote"/> says so in the UI rather
/// than letting those controls look live when they aren't.
/// </para>
/// </summary>
public sealed class DeviceSettingsEditorViewModel : ViewModelBase
{
    private readonly DeviceSettingsStore store;
    private bool suspendWrites;

    private string slotId = string.Empty;
    private string deviceId = string.Empty;
    private string deviceName = string.Empty;

    public DeviceSettingsEditorViewModel(DeviceSettingsStore store)
    {
        this.store = store;
        CurveOptions = new ObservableCollection<StickCurve>(Enum.GetValues<StickCurve>());
        LightbarModeOptions = new ObservableCollection<LightbarMode>(Enum.GetValues<LightbarMode>());
        AdaptiveModeOptions = new ObservableCollection<AdaptiveTriggerMode>(Enum.GetValues<AdaptiveTriggerMode>());
    }

    public ObservableCollection<StickCurve> CurveOptions { get; }
    public ObservableCollection<LightbarMode> LightbarModeOptions { get; }
    public ObservableCollection<AdaptiveTriggerMode> AdaptiveModeOptions { get; }

    public string DeviceName
    {
        get => deviceName;
        private set => SetProperty(ref deviceName, value);
    }

    public bool HasDevice => !string.IsNullOrEmpty(deviceId);

    public string EffectsPendingNote =>
        "Rumble, lighting and adaptive triggers are saved per slot, but writing them to the "
        + "physical pad needs the effects thread (not built yet). Stick and trigger tuning is live now.";

    /// <summary>Points the editor at one slot/device pair and loads its saved values.</summary>
    public void Load(string slotIdentifier, string deviceIdentifier, string displayName)
    {
        slotId = slotIdentifier;
        deviceId = deviceIdentifier;
        DeviceName = displayName;

        var settings = store.Get(slotIdentifier, deviceIdentifier);

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
