namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// Decides what the effects thread should actually write, and when.
///
/// <para>
/// This exists because the transport is slow and the producers are fast.
/// Rumble feedback arrives from the game at the output sink's rate and
/// tuning changes arrive on every slider drag, while a single write to a
/// Bluetooth pad can take milliseconds and holds SDL's device lock while
/// it does. Sending every value produced would queue up work faster than
/// it drains, so the pad would lag further behind reality the longer a
/// rumble lasted.
/// </para>
///
/// <para>
/// Three rules, in order:
/// </para>
/// <list type="number">
/// <item><b>Latest wins.</b> Only the newest state per device is kept;
///   superseded ones are dropped, never queued.</item>
/// <item><b>No redundant writes.</b> A state equal to what was last
///   written is not written again — the common case is a pad sitting
///   idle, which should cost nothing.</item>
/// <item><b>Rate limit.</b> Each device is written at most once per
///   interval, EXCEPT that going silent is never delayed: a motor that
///   should stop must stop, or the pad buzzes on after the game stopped
///   asking.</item>
/// </list>
///
/// <para>
/// Pure and lock-guarded, with no I/O and no clock of its own — the
/// caller supplies the timestamp — so the policy can be tested without a
/// thread or a device.
/// </para>
/// </summary>
public sealed class EffectDispatchQueue(TimeSpan minimumWriteInterval)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, DeviceEntry> devices = new(StringComparer.Ordinal);

    /// <summary>Minimum spacing between two writes to the same device.</summary>
    public TimeSpan MinimumWriteInterval { get; } = minimumWriteInterval;

    /// <summary>Devices currently tracked. For diagnostics.</summary>
    public int TrackedDeviceCount
    {
        get
        {
            lock (gate)
            {
                return devices.Count;
            }
        }
    }

    /// <summary>
    /// Records the state <paramref name="deviceId"/> should be in.
    /// Replaces any pending state for that device.
    /// </summary>
    public void Publish(string deviceId, ControllerEffectState state)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        lock (gate)
        {
            if (devices.TryGetValue(deviceId, out var entry))
            {
                devices[deviceId] = entry with { Pending = state };
            }
            else
            {
                devices[deviceId] = new DeviceEntry { Pending = state };
            }
        }
    }

    /// <summary>
    /// Returns the writes that are due at <paramref name="now"/>, and
    /// records them as written. The caller performs the I/O; anything it
    /// fails to write should be re-published.
    /// </summary>
    public IReadOnlyList<EffectWrite> TakeDueWrites(DateTimeOffset now)
    {
        List<EffectWrite>? due = null;

        lock (gate)
        {
            foreach (var (deviceId, entry) in devices)
            {
                var pending = entry.Pending;

                // Rule 2: nothing changed since the last write.
                if (entry.HasWritten && entry.LastWritten == pending)
                {
                    continue;
                }

                // Rule 3: rate limit, with silence exempt. A stop must not
                // wait behind the interval — a motor left running is the
                // one failure a user cannot ignore.
                if (entry.HasWritten && !pending.IsSilent &&
                    now - entry.LastWriteTime < MinimumWriteInterval)
                {
                    continue;
                }

                (due ??= []).Add(new EffectWrite(deviceId, pending));
                devices[deviceId] = entry with
                {
                    LastWritten = pending,
                    LastWriteTime = now,
                    HasWritten = true
                };
            }
        }

        return due ?? (IReadOnlyList<EffectWrite>)[];
    }

    /// <summary>
    /// Queues a final all-off write for a device that is going away, and
    /// stops tracking it once that write has been taken.
    ///
    /// <para>
    /// Unassigning a slot mid-rumble would otherwise leave the motor
    /// running with nothing left to turn it off.
    /// </para>
    /// </summary>
    public void Retire(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        lock (gate)
        {
            if (!devices.TryGetValue(deviceId, out var entry))
            {
                return;
            }

            // Never wrote anything, so there is nothing to undo.
            if (!entry.HasWritten && entry.Pending.IsSilent)
            {
                _ = devices.Remove(deviceId);
                return;
            }

            devices[deviceId] = entry with { Pending = ControllerEffectState.Silent, Retiring = true };
        }
    }

    /// <summary>
    /// Drops devices whose final silent write has gone out. Called by the
    /// effects thread after a successful drain.
    /// </summary>
    public void PurgeRetired()
    {
        lock (gate)
        {
            foreach (var deviceId in devices
                         .Where(kv => kv.Value.Retiring && kv.Value.HasWritten && kv.Value.LastWritten.IsSilent)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _ = devices.Remove(deviceId);
            }
        }
    }

    /// <summary>Forgets a device outright, with no final write. For teardown.</summary>
    public void Forget(string deviceId)
    {
        lock (gate)
        {
            _ = devices.Remove(deviceId);
        }
    }

    /// <summary>Re-queues a write that the caller could not deliver.</summary>
    public void Requeue(EffectWrite write)
    {
        lock (gate)
        {
            if (devices.TryGetValue(write.DeviceId, out var entry))
            {
                // Clearing HasWritten is what makes the retry happen: the
                // equality check in TakeDueWrites would otherwise treat the
                // failed value as already delivered and never send it.
                devices[write.DeviceId] = entry with { HasWritten = false };
            }
        }
    }

    private readonly record struct DeviceEntry
    {
        public ControllerEffectState Pending { get; init; }

        public ControllerEffectState LastWritten { get; init; }

        public DateTimeOffset LastWriteTime { get; init; }

        public bool HasWritten { get; init; }

        public bool Retiring { get; init; }
    }
}

/// <summary>One device's state, ready to go to hardware.</summary>
public readonly record struct EffectWrite(string DeviceId, ControllerEffectState State);
