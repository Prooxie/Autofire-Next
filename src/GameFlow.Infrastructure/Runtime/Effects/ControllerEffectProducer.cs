using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;
using GameFlow.Infrastructure.Runtime.Slots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// Decides what every assigned pad should currently be showing, and
/// publishes it to the effects queue.
///
/// <para>
/// This is the link that turns saved settings into output. Without it the
/// rest of the chain — resolver, queue, mailbox, SDL writer — is complete
/// and does nothing, because nothing produces. That is exactly the state
/// it shipped in once.
/// </para>
///
/// <para>
/// Runs on a timer rather than inside the mapping tick. The mapping tick
/// can be 1000 Hz and must not perform blocking output work. A 16 ms pass
/// matches the effect queue's per-device write interval, keeps returned
/// game rumble responsive, and remains smooth for lighting animations.
/// </para>
/// </summary>
public sealed class ControllerEffectProducer(
    SlotRegistry slotRegistry,
    SlotSnapshotStore snapshots,
    DeviceSettingsStore deviceSettings,
    InputDeviceCatalog deviceCatalog,
    RumbleFeedbackStore rumbleFeedback,
    ControllerEffectsService effects,
    ILogger<ControllerEffectProducer> logger) : BackgroundService
{
    private static readonly TimeSpan ProduceInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Charge at or below which the pad is treated as low. 20% is roughly
    /// where a DualSense has under an hour left.
    /// </summary>
    private const int LowBatteryPercent = 20;

    /// <summary>Charge that clears the low state. Above the trigger, so a reading hovering on the boundary does not flap.</summary>
    private const int LowBatteryClearPercent = 25;

    private readonly Dictionary<string, bool> lowBatteryByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> publishedDevices = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Devices currently reporting a low battery. The UI reads this to
    /// show a warning without polling SDL itself.
    /// </summary>
    public IReadOnlyCollection<string> LowBatteryDevices
    {
        get
        {
            lock (lowBatteryByDevice)
            {
                return lowBatteryByDevice.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        startedAtUtc = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(ProduceInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                ProduceOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // One bad frame must not kill lighting for the session.
                logger.LogDebug(exception, "Effect production pass failed.");
            }
        }
    }

    private void ProduceOnce()
    {
        var elapsed = (DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slotRegistry.GetSlots())
        {
            if (!slot.Enabled)
            {
                continue;
            }

            var pair = snapshots.Get(slot.Id);

            foreach (var deviceId in slot.InputDeviceIds)
            {
                if (string.IsNullOrEmpty(deviceId) || !seen.Add(deviceId))
                {
                    continue;
                }

                // A pad-specific entry wins; otherwise effects inherit the
                // slot defaults that can be edited before the pad is online.
                var settings = deviceSettings.GetEffective(slot.Id, deviceId);
                _ = deviceCatalog.TryGetById(deviceId, out var info);

                UpdateLowBatteryState(deviceId, info);

                var physical = pair.Physical;
                var requestedRumble = rumbleFeedback.Get(slot.Id);
                var (low, high) = EffectSceneResolver.ResolveRumble(
                    settings.Rumble,
                    requestedRumble.LowFrequency,
                    requestedRumble.HighFrequency);

                var context = new EffectSceneContext(
                    ElapsedSeconds: elapsed,
                    PlayerIndex: slot.Index,
                    BatteryPercent: info?.BatteryPercentage,
                    IsCharging: info?.BatteryState == DeviceBatteryState.Charging,
                    RumbleLevel: Math.Max(low, high),
                    TriggerLevel: Math.Max(physical.LeftTrigger, physical.RightTrigger));

                var lighting = settings.Lighting;

                // A low battery overrides whatever mode is configured. It
                // is the one thing worth interrupting the user's choice
                // for: a pad that dies mid-session is a worse outcome than
                // a lightbar that briefly stops matching its setting.
                var color = IsLow(deviceId)
                    ? LowBatteryPulse(elapsed)
                    : EffectSceneResolver.ResolveLight(lighting, context);

                effects.Queue.Publish(deviceId, new ControllerEffectState
                {
                    LowFrequencyRumble = low,
                    HighFrequencyRumble = high,
                    LedColor = color is { } c ? new EffectColor(c.R, c.G, c.B) : null,
                    LeftTrigger = ToCommand(settings.LeftAdaptiveTrigger),
                    RightTrigger = ToCommand(settings.RightAdaptiveTrigger),
                });

                if (publishedDevices.Add(deviceId))
                {
                    // Says plainly that effects ARE being produced for a
                    // specific pad. Effects only apply to a device
                    // assigned to an enabled slot, so the common "nothing
                    // happens" case is an unassigned pad — and without
                    // this line that is indistinguishable from a broken
                    // backend.
                    logger.LogInformation(
                        "Producing effects for {Device} on slot {Slot}: lighting {Mode}, rumble {Rumble}.",
                        info?.DisplayName ?? deviceId,
                        slot.Name,
                        lighting.Mode,
                        settings.Rumble.Enabled ? "enabled" : "disabled");
                }
            }
        }

        // A pad that left a slot must be told to stop. Without this it
        // keeps whatever rumble and colour it had when it was unassigned.
        foreach (var stale in publishedDevices.Where(id => !seen.Contains(id)).ToList())
        {
            effects.Queue.Retire(stale);
            _ = publishedDevices.Remove(stale);

            lock (lowBatteryByDevice)
            {
                _ = lowBatteryByDevice.Remove(stale);
            }
        }
    }

    /// <summary>
    /// Tracks the low-battery flag with hysteresis and logs each
    /// transition. Separate thresholds for entering and leaving so a
    /// reading sitting on the boundary does not flap the warning on and
    /// off — SDL reports these in coarse steps.
    /// </summary>
    private void UpdateLowBatteryState(string deviceId, InputDeviceInfo? info)
    {
        if (info is null || !info.HasBattery || info.BatteryState == DeviceBatteryState.Charging)
        {
            lock (lowBatteryByDevice)
            {
                if (lowBatteryByDevice.TryGetValue(deviceId, out var wasLow) && wasLow)
                {
                    lowBatteryByDevice[deviceId] = false;
                    logger.LogInformation("{Device} is charging; low-battery warning cleared.", info?.DisplayName ?? deviceId);
                }
            }

            return;
        }

        var percent = info.BatteryPercentage ?? 100;

        lock (lowBatteryByDevice)
        {
            var wasLow = lowBatteryByDevice.TryGetValue(deviceId, out var existing) && existing;

            if (!wasLow && percent <= LowBatteryPercent)
            {
                lowBatteryByDevice[deviceId] = true;
                logger.LogWarning(
                    "{Device} battery is low: {Percent}%. The lightbar will pulse red until it is charged.",
                    info.DisplayName, percent);
            }
            else if (wasLow && percent >= LowBatteryClearPercent)
            {
                lowBatteryByDevice[deviceId] = false;
                logger.LogInformation("{Device} battery recovered to {Percent}%.", info.DisplayName, percent);
            }
        }
    }

    /// <summary>
    /// Turns saved adaptive-trigger settings into the device-neutral
    /// command the queue carries.
    ///
    /// <para>
    /// <see cref="AdaptiveTriggerMode.Off"/> is an explicit command, not
    /// a null/absent section: the firmware retains its previous effect
    /// until a release report reaches it.
    /// </para>
    /// </summary>
    internal static AdaptiveTriggerCommand ToCommand(AdaptiveTriggerSettings settings)
    {
        var effect = settings.Mode switch
        {
            AdaptiveTriggerMode.Off => AdaptiveTriggerEffect.Off,
            AdaptiveTriggerMode.Weapon => AdaptiveTriggerEffect.Section,
            AdaptiveTriggerMode.Vibration or AdaptiveTriggerMode.MultiplePositionVibration
                => AdaptiveTriggerEffect.Vibration,
            _ => AdaptiveTriggerEffect.Constant,
        };

        static byte Scale(float unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

        return new AdaptiveTriggerCommand(
            effect,
            Scale(settings.StartPosition),
            Scale(settings.EndPosition),
            Scale(settings.Strength),
            (byte)Math.Clamp(settings.FrequencyHz, 1, 255));
    }

    private bool IsLow(string deviceId)
    {
        lock (lowBatteryByDevice)
        {
            return lowBatteryByDevice.TryGetValue(deviceId, out var low) && low;
        }
    }

    /// <summary>
    /// Urgent red pulse. Faster than the breathing mode and never fully
    /// dark, so it reads as an alert rather than as an animation someone
    /// chose.
    /// </summary>
    private static LightColor LowBatteryPulse(double elapsedSeconds)
    {
        var phase = (elapsedSeconds % 1.0) / 1.0;
        var level = 0.35 + (0.65 * ((1 - Math.Cos(phase * 2 * Math.PI)) / 2));
        return new LightColor(0xFF, 0x18, 0x18).Scaled(level);
    }
}
