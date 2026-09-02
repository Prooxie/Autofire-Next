using GameFlow.Core.Enums;
using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime.Web;

/// <summary>One rumble command queued back to a phone, played via the browser Vibration API.</summary>
public readonly record struct WebRumbleCommand(float LowFrequency, float HighFrequency, int DurationMs);

/// <summary>
/// Ownership token for one browser connection. The monotonically changing
/// token prevents an old socket from updating or releasing a pad after a
/// reconnect has already taken that pad over.
/// </summary>
public readonly record struct WebPadLease(int PadIndex, long Token)
{
    /// <summary>True when the hub assigned a real pad.</summary>
    public bool IsValid => PadIndex >= 0 && Token > 0;

    /// <summary>Sentinel returned when all sixteen pads are actively in use.</summary>
    public static WebPadLease Unavailable => new(-1, 0);
}

/// <summary>
/// Shared state between <see cref="WebControllerServer"/> (which receives
/// phone input over WebSocket) and <see cref="WebControllerInputSource"/>
/// (which hands it to the mapping pipeline as an ordinary snapshot).
///
/// <para>
/// Each connected phone owns one pad index and becomes one independent
/// physical input from GameFlow's perspective, up to <see cref="MaxPads"/>.
/// Reconnects carry a browser-generated client id so the same phone can
/// reclaim its previous pad number. Every connection also receives a unique
/// <see cref="WebPadLease"/>; stale sockets cannot write into or release a
/// replacement connection's state.
/// </para>
///
/// <para>
/// All access is under a single lock: the write side is one socket task per
/// phone, the read side is the runtime tick, and both operations are tiny.
/// Keeping the ownership check and state mutation atomic matters more than a
/// more elaborate lock-free structure here.
/// </para>
/// </summary>
public sealed class WebControllerHub
{
    /// <summary>Matches the roadmap's sixteen-phone limit and the runtime's own slot ceiling.</summary>
    public const int MaxPads = 16;

    private readonly Lock gate = new();
    private readonly TimeProvider timeProvider;
    private readonly ControllerSnapshot?[] pads = new ControllerSnapshot?[MaxPads];
    private readonly Queue<WebRumbleCommand>[] rumbleQueues = new Queue<WebRumbleCommand>[MaxPads];
    private readonly DateTimeOffset[] lastSeen = new DateTimeOffset[MaxPads];
    private readonly long[] leaseTokens = new long[MaxPads];
    private readonly string?[] clientIds = new string?[MaxPads];
    private long nextLeaseToken;

    /// <summary>
    /// A pad with no traffic for this long is treated as gone. The browser
    /// sends a one-second heartbeat, so five seconds tolerates ordinary Wi-Fi
    /// jitter while still releasing a phone that disappeared without a close
    /// frame.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    public WebControllerHub() : this(TimeProvider.System)
    {
    }

    internal WebControllerHub(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        for (var i = 0; i < MaxPads; i++)
        {
            rumbleQueues[i] = new Queue<WebRumbleCommand>();
        }
    }

    /// <summary>
    /// Claims a pad for one browser connection. A known client reclaims its
    /// previous index; otherwise an unused index wins before a disconnected
    /// client's old index is recycled.
    /// </summary>
    /// <param name="clientId">
    /// Browser-generated opaque id, or null for legacy clients. Only short
    /// ASCII identifiers are retained; invalid input is treated as anonymous.
    /// </param>
    public WebPadLease ClaimPad(string? clientId = null)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var normalizedClientId = NormalizeClientId(clientId);

            if (normalizedClientId is not null)
            {
                for (var i = 0; i < MaxPads; i++)
                {
                    if (string.Equals(clientIds[i], normalizedClientId, StringComparison.Ordinal))
                    {
                        return Activate(i, normalizedClientId, now);
                    }
                }
            }

            // Prefer an index that has never belonged to an identified
            // client. This keeps disconnected clients' pad numbers stable
            // until capacity pressure actually requires reusing one.
            for (var i = 0; i < MaxPads; i++)
            {
                if (clientIds[i] is null && !IsActive(i, now))
                {
                    return Activate(i, normalizedClientId, now);
                }
            }

            // Every index has history. Recycle a disconnected/stale one;
            // active connections are never displaced by a different client.
            for (var i = 0; i < MaxPads; i++)
            {
                if (!IsActive(i, now))
                {
                    return Activate(i, normalizedClientId, now);
                }
            }

            return WebPadLease.Unavailable;
        }
    }

    /// <summary>
    /// Releases a connection only if it still owns the pad. A delayed finally
    /// block from an old socket becomes a no-op after a reconnect replaces it.
    /// </summary>
    public void ReleasePad(WebPadLease lease)
    {
        if (!lease.IsValid)
        {
            return;
        }

        lock (gate)
        {
            if (!Owns(lease))
            {
                return;
            }

            pads[lease.PadIndex] = null;
            leaseTokens[lease.PadIndex] = 0;
            lastSeen[lease.PadIndex] = timeProvider.GetUtcNow();
            rumbleQueues[lease.PadIndex].Clear();
        }
    }

    /// <summary>
    /// Publishes the latest input if this socket still owns the pad. Returns
    /// false when a newer lease replaced it so the server can close the stale
    /// connection.
    /// </summary>
    public bool UpdatePad(WebPadLease lease, ControllerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!lease.IsValid)
        {
            return false;
        }

        lock (gate)
        {
            if (!Owns(lease))
            {
                return false;
            }

            pads[lease.PadIndex] = snapshot;
            lastSeen[lease.PadIndex] = timeProvider.GetUtcNow();
            return true;
        }
    }

    /// <summary>Whether a connection token is still the current owner.</summary>
    public bool IsLeaseCurrent(WebPadLease lease)
    {
        lock (gate)
        {
            return Owns(lease);
        }
    }

    /// <summary>
    /// Latest input for a pad. Returns a neutral snapshot (not the last one
    /// received) when the pad is disconnected or stale, so a phone that dies
    /// mid-press cannot leave a button held forever.
    /// </summary>
    public ControllerSnapshot GetSnapshot(int padIndex)
    {
        if (!IsValidIndex(padIndex))
        {
            return BuildNeutralSnapshot(padIndex);
        }

        lock (gate)
        {
            return IsActive(padIndex, timeProvider.GetUtcNow())
                ? pads[padIndex]!
                : BuildNeutralSnapshot(padIndex);
        }
    }

    public bool IsPadConnected(int padIndex)
    {
        if (!IsValidIndex(padIndex))
        {
            return false;
        }

        lock (gate)
        {
            return IsActive(padIndex, timeProvider.GetUtcNow());
        }
    }

    public IReadOnlyList<int> GetConnectedPads()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var result = new List<int>();
            for (var i = 0; i < MaxPads; i++)
            {
                if (IsActive(i, now))
                {
                    result.Add(i);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Queues rumble for an active phone. The queue is bounded so a browser
    /// that stops draining cannot grow it without limit.
    /// </summary>
    public void QueueRumble(int padIndex, WebRumbleCommand command)
    {
        if (!IsValidIndex(padIndex))
        {
            return;
        }

        lock (gate)
        {
            if (!IsActive(padIndex, timeProvider.GetUtcNow()))
            {
                return;
            }

            var queue = rumbleQueues[padIndex];
            if (queue.Count >= 8)
            {
                _ = queue.Dequeue();
            }

            queue.Enqueue(command);
        }
    }

    /// <summary>
    /// Dequeues feedback only for the socket that currently owns the pad, so
    /// an old connection cannot steal a newer connection's rumble.
    /// </summary>
    public bool TryDequeueRumble(WebPadLease lease, out WebRumbleCommand command)
    {
        command = default;
        if (!lease.IsValid)
        {
            return false;
        }

        lock (gate)
        {
            return Owns(lease) && rumbleQueues[lease.PadIndex].TryDequeue(out command);
        }
    }

    private WebPadLease Activate(int padIndex, string? clientId, DateTimeOffset now)
    {
        nextLeaseToken++;
        if (nextLeaseToken <= 0)
        {
            nextLeaseToken = 1;
        }

        leaseTokens[padIndex] = nextLeaseToken;
        clientIds[padIndex] = clientId;
        pads[padIndex] = BuildNeutralSnapshot(padIndex);
        lastSeen[padIndex] = now;
        rumbleQueues[padIndex].Clear();
        return new WebPadLease(padIndex, nextLeaseToken);
    }

    private bool Owns(WebPadLease lease) => lease.IsValid
        && IsValidIndex(lease.PadIndex)
        && leaseTokens[lease.PadIndex] == lease.Token;

    private bool IsActive(int padIndex, DateTimeOffset now) => pads[padIndex] is not null
        && now - lastSeen[padIndex] <= StaleAfter;

    private static bool IsValidIndex(int padIndex) => padIndex >= 0 && padIndex < MaxPads;

    private static string? NormalizeClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var value = clientId.Trim();
        if (value.Length is < 8 or > 64)
        {
            return null;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '-' or '_'))
            {
                return null;
            }
        }

        return value;
    }

    private static ControllerSnapshot BuildNeutralSnapshot(int padIndex) => new()
    {
        DeviceName = $"Web Controller #{padIndex + 1}",
        Buttons = ButtonState.CreateEmptyMap(),
        Timestamp = DateTimeOffset.UtcNow
    };
}
