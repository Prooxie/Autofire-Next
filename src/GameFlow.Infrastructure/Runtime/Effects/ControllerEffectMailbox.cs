using System.Collections.Concurrent;

namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// Hand-off point between the effects thread and whichever backend owns
/// the device handles.
///
/// <para>
/// The effects thread decides WHAT each pad should be doing and when — it
/// coalesces, rate-limits and drops superseded values. It deliberately
/// does not perform the write. On SDL the device handles belong to the
/// SDL worker thread, and a write from any other thread takes SDL's
/// device lock across a blocking Bluetooth HID transfer, which is exactly
/// what froze the app the last time effects were wired up.
/// </para>
///
/// <para>
/// So the effects thread posts here, and the owning thread collects on its
/// own schedule. Latest-wins per device, because a value that was
/// superseded before the owner looked is not worth sending.
/// </para>
/// </summary>
public sealed class ControllerEffectMailbox
{
    private readonly ConcurrentDictionary<string, ControllerEffectState> pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a backend is present to collect. Used to report support without coupling to one.</summary>
    public bool HasCollector { get; set; }

    /// <summary>Posts the state a device should be in. Replaces anything not yet collected.</summary>
    public void Post(string deviceId, in ControllerEffectState state)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            pending[deviceId] = state;
        }
    }

    /// <summary>
    /// Takes everything posted since the last call. Called by the thread
    /// that owns the device handles, from its own loop.
    /// </summary>
    public bool TryCollect(out IReadOnlyList<EffectWrite> writes)
    {
        if (pending.IsEmpty)
        {
            writes = [];
            return false;
        }

        var collected = new List<EffectWrite>(pending.Count);
        foreach (var deviceId in pending.Keys)
        {
            if (pending.TryRemove(deviceId, out var state))
            {
                collected.Add(new EffectWrite(deviceId, state));
            }
        }

        writes = collected;
        return collected.Count > 0;
    }

    /// <summary>Drops everything pending, for teardown.</summary>
    public void Clear() => pending.Clear();
}

/// <summary>
/// Effect writer that posts to <see cref="ControllerEffectMailbox"/>
/// rather than touching hardware.
///
/// <para>
/// Reports success on post: the write is now the collector's
/// responsibility, and re-queueing here would fight the mailbox's own
/// latest-wins rule.
/// </para>
/// </summary>
public sealed class MailboxControllerEffectWriter(ControllerEffectMailbox mailbox) : IControllerEffectWriter
{
    public bool IsSupported => mailbox.HasCollector;

    public bool TryWrite(string deviceId, in ControllerEffectState state)
    {
        mailbox.Post(deviceId, state);
        return true;
    }
}
