using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Runtime.Slots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Motion;

/// <summary>
/// The DSU / Cemuhook motion server. Listens on UDP (26760 by default)
/// and streams gyroscope and accelerometer for the enabled slots, so
/// Cemu, Dolphin, Yuzu and Ryujinx can drive real motion controls.
///
/// <para>
/// Structured the way the rest of the runtime is: the socket loop here is
/// deliberately thin, with everything interesting pushed into
/// <see cref="DsuProtocol"/> (bytes) and <see cref="DsuSnapshotMapper"/>
/// (units and button correspondence), both of which are pure and covered
/// by tests. Nothing in this file needs a socket to be verified because
/// nothing in this file decides anything.
/// </para>
///
/// <para>
/// Like every other network server in the app, this never throws out of
/// <see cref="ExecuteAsync"/>. A port that is already taken is a
/// configuration problem for the user to fix, not a reason to bring the
/// application down, so a bind failure logs and leaves the server idle
/// while the rest of GameFlow carries on.
/// </para>
/// </summary>
public sealed class DsuServer(
    IUserSettingsService userSettings,
    SlotRegistry slotRegistry,
    SlotSnapshotStore slotSnapshotStore,
    ILogger<DsuServer> logger) : BackgroundService
{
    /// <summary>
    /// Broadcast rate. 60 Hz is what Cemuhook clients expect and is well
    /// past what motion aiming can use; the encoder is allocation-free
    /// and could run far faster, but sending faster than the consumer
    /// samples only burns bandwidth on a shared Wi-Fi network.
    /// </summary>
    private const int BroadcastHz = 60;

    /// <summary>
    /// How long a client stays subscribed without saying anything.
    /// Clients re-register roughly every second; this is generous enough
    /// to survive a few dropped datagrams but short enough that a closed
    /// emulator stops receiving traffic promptly.
    /// </summary>
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often the supervisory loop re-checks the enable flag and port.</summary>
    private static readonly TimeSpan SettingsPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<IPEndPoint, ClientRegistration> clients = new();
    private readonly DsuPacketCounter packetCounter = new();

    // Monotonic, so the motion timestamp cannot jump backwards when the
    // system clock is adjusted mid-session. Clients use the delta between
    // consecutive timestamps to integrate rotation, and a backwards step
    // there reads as a violent flick.
    private readonly Stopwatch uptime = Stopwatch.StartNew();

    /// <summary>True while the socket is bound and streaming. Read by the Dashboard.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The bound endpoint, or <see langword="null"/> when not running. Read by the Dashboard.</summary>
    public string? ListenEndpoint { get; private set; }

    /// <summary>How many emulator clients are currently subscribed. Read by the Dashboard.</summary>
    public int ConnectedClientCount => clients.Count;

    /// <summary>
    /// Last failure reason, for the Dashboard to show instead of leaving
    /// the user staring at a toggle that is on while nothing happens.
    /// </summary>
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Supervisory loop. A BackgroundService cannot be individually
        // stopped and restarted by the host, so the enable toggle is
        // handled here by opening and closing the socket rather than by
        // starting and stopping the service — the same shape
        // RuntimeCoordinator uses for its slot rebuilds.
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = userSettings.Current;
            if (!settings.MotionServerEnabled)
            {
                await DelayQuietlyAsync(SettingsPollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var port = settings.MotionServerPort;
            await RunSessionAsync(port, stoppingToken).ConfigureAwait(false);

            // A session ends either because the user turned the server off
            // or changed the port (clean, loop straight back round), or
            // because binding failed (in which case pause before retrying
            // so a permanently-taken port does not spin).
            if (LastError is not null)
            {
                await DelayQuietlyAsync(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One bound-socket session. Returns when the user disables the
    /// server, changes the port, the host shuts down, or the bind fails.
    /// </summary>
    private async Task RunSessionAsync(int port, CancellationToken stoppingToken)
    {
        UdpClient? socket;
        try
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            LastError = null;
        }
        catch (SocketException exception)
        {
            LastError = $"Port {port} unavailable: {exception.SocketErrorCode}";
            logger.LogError(
                exception,
                "DSU motion server: could not bind UDP port {Port} ({Error}). " +
                "Another DSU provider (DS4Windows, BetterJoy) is the usual cause — " +
                "stop it or change the port. Motion server idle; the rest of GameFlow is unaffected.",
                port, exception.SocketErrorCode);
            return;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            logger.LogError(exception, "DSU motion server: unexpected failure binding port {Port}; server idle.", port);
            return;
        }

        clients.Clear();
        packetCounter.ResetAll();
        IsRunning = true;
        ListenEndpoint = $"0.0.0.0:{port}";
        logger.LogInformation(
            "DSU motion server: listening on UDP {Port}. Point Cemu / Dolphin / Yuzu / Ryujinx at this PC on that port.",
            port);

        // Cancelled when the user flips the toggle or edits the port, so
        // both loops below unwind together and the socket is rebound.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        try
        {
            await Task.WhenAll(
                ReceiveLoopAsync(socket, session.Token),
                BroadcastLoopAsync(socket, session.Token),
                WatchSettingsAsync(port, session)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown or a settings change.
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            logger.LogError(exception, "DSU motion server: session ended unexpectedly; will retry.");
        }
        finally
        {
            IsRunning = false;
            ListenEndpoint = null;
            clients.Clear();
            try
            {
                socket.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "DSU motion server: ignoring error while closing the socket.");
            }

            logger.LogInformation("DSU motion server: stopped.");
        }
    }

    /// <summary>
    /// Ends the session when the enable flag or port changes. Polling
    /// rather than subscribing to <c>IUserSettingsService.Changed</c>:
    /// the poll is every 500 ms against a volatile field, and it means
    /// there is no subscription to leak if a session unwinds abnormally.
    /// </summary>
    private async Task WatchSettingsAsync(int boundPort, CancellationTokenSource session)
    {
        while (!session.IsCancellationRequested)
        {
            var settings = userSettings.Current;
            if (!settings.MotionServerEnabled || settings.MotionServerPort != boundPort)
            {
                await session.CancelAsync().ConfigureAwait(false);
                return;
            }

            await DelayQuietlyAsync(SettingsPollInterval, session.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles inbound requests: version handshake, controller info, and
    /// data subscriptions.
    /// </summary>
    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException exception)
            {
                // A datagram from a client that has since closed its
                // socket surfaces here (ConnectionReset on Windows) and
                // must not end the loop for everyone else.
                logger.LogDebug(exception, "DSU motion server: ignoring socket error while receiving.");
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                HandleRequest(socket, received);
            }
            catch (Exception exception)
            {
                // Everything reachable from here is hostile input off the
                // network. One malformed datagram must never take the
                // server down.
                logger.LogDebug(exception, "DSU motion server: ignoring malformed request from {Endpoint}.", received.RemoteEndPoint);
            }
        }
    }

    private void HandleRequest(UdpClient socket, UdpReceiveResult received)
    {
        var request = DsuProtocol.TryParseRequest(received.Buffer);
        if (request is null)
        {
            return;
        }

        var endpoint = received.RemoteEndPoint;
        Span<byte> buffer = stackalloc byte[DsuProtocol.ControllerDataPacketLength];

        switch (request.MessageType)
        {
            case DsuMessageType.ProtocolVersion:
                if (DsuProtocol.TryWriteVersionResponse(buffer, DsuProtocol.ServerId, out var versionBytes))
                {
                    Send(socket, buffer[..versionBytes], endpoint);
                }

                break;

            case DsuMessageType.ControllerInfo:
                // The client names the slots it cares about; answer each
                // one, including the ones with nothing in them, so it can
                // tell "empty" from "server did not reply".
                foreach (var slot in request.RequestedSlots)
                {
                    var info = DescribeSlot(slot);
                    if (DsuProtocol.TryWriteControllerInfoResponse(buffer, DsuProtocol.ServerId, info, out var infoBytes))
                    {
                        Send(socket, buffer[..infoBytes], endpoint);
                    }
                }

                break;

            case DsuMessageType.ControllerData:
                // Re-registering refreshes the timestamp, which is what
                // keeps a live client from ageing out.
                clients[endpoint] = new ClientRegistration(
                    request.ClientId,
                    request.RegistrationFlags,
                    request.RegisteredSlot,
                    request.RegisteredMac,
                    DateTimeOffset.UtcNow);
                break;

            default:
                break;
        }
    }

    /// <summary>Streams controller data to every live subscriber.</summary>
    private async Task BroadcastLoopAsync(UdpClient socket, CancellationToken token)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / BroadcastHz));

        while (await SafeWaitAsync(ticker, token).ConfigureAwait(false))
        {
            DropStaleClients();
            if (clients.IsEmpty)
            {
                continue;
            }

            BroadcastOnce(socket);
        }
    }

    /// <summary>
    /// One tick's worth of sending. A separate method purely so its stack
    /// frame — and the packet buffer on it — unwinds every tick; a
    /// <c>stackalloc</c> in the loop body above would accumulate instead
    /// (CA2014), which at 60 Hz is a stack overflow with a long fuse.
    /// </summary>
    private void BroadcastOnce(UdpClient socket)
    {
        var timestamp = (ulong)(uptime.Elapsed.TotalMilliseconds * 1000.0);
        Span<byte> buffer = stackalloc byte[DsuProtocol.ControllerDataPacketLength];

        // DSU addresses at most four pads. Slots past the fourth are
        // simply not visible to emulators — GameFlow supports 16, but
        // the protocol has no way to describe them.
        var padCount = 0;
        foreach (var slot in slotRegistry.GetSlots())
        {
            if (!slot.Enabled)
            {
                continue;
            }

            if (padCount >= DsuProtocol.MaxSlots)
            {
                break;
            }

            var pad = padCount++;
            var snapshot = slotSnapshotStore.Get(slot.Id).Physical;
            var data = DsuSnapshotMapper.ToControllerData(
                snapshot, pad, packetCounter.Next(pad), timestamp);

            if (!DsuProtocol.TryWriteControllerData(buffer, DsuProtocol.ServerId, data, out var written))
            {
                continue;
            }

            foreach (var (endpoint, registration) in clients)
            {
                if (registration.WantsPad(pad, data.Device.MacAddress))
                {
                    Send(socket, buffer[..written], endpoint);
                }
            }
        }
    }

    /// <summary>
    /// Describes a slot for a controller-info reply, or reports it empty
    /// when the client asked about a pad index GameFlow has nothing in.
    /// </summary>
    private DsuControllerInfo DescribeSlot(byte slot)
    {
        var enabled = slotRegistry.GetSlots().Where(s => s.Enabled).ToList();
        if (slot >= enabled.Count || slot >= DsuProtocol.MaxSlots)
        {
            return DsuSnapshotMapper.Disconnected(slot).Device;
        }

        var snapshot = slotSnapshotStore.Get(enabled[slot].Id).Physical;
        return DsuSnapshotMapper.ToControllerData(snapshot, slot, packetNumber: 0, timestampMicroseconds: 0).Device;
    }

    private void DropStaleClients()
    {
        var cutoff = DateTimeOffset.UtcNow - ClientTimeout;
        foreach (var (endpoint, registration) in clients)
        {
            if (registration.LastSeen < cutoff)
            {
                _ = clients.TryRemove(endpoint, out _);
                logger.LogDebug("DSU motion server: client {Endpoint} timed out.", endpoint);
            }
        }
    }

    private void Send(UdpClient socket, ReadOnlySpan<byte> packet, IPEndPoint endpoint)
    {
        try
        {
            _ = socket.Client.SendTo(packet, SocketFlags.None, endpoint);
        }
        catch (SocketException exception)
        {
            // An emulator that exited without unsubscribing produces this
            // on every tick until it ages out. Debug, not warning — it is
            // expected traffic, not a fault.
            logger.LogDebug(exception, "DSU motion server: dropping client {Endpoint} after a send failure.", endpoint);
            _ = clients.TryRemove(endpoint, out _);
        }
        catch (ObjectDisposedException)
        {
            // Session tearing down underneath us.
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer ticker, CancellationToken token)
    {
        try
        {
            return await ticker.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; the caller's loop condition handles it.
        }
    }

    /// <summary>
    /// One subscribed client. <paramref name="Flags"/> decides whether it
    /// wanted everything, one slot, or one MAC.
    /// </summary>
    private readonly record struct ClientRegistration(
        uint ClientId,
        DsuRegistrationFlags Flags,
        byte Slot,
        ulong Mac,
        DateTimeOffset LastSeen)
    {
        public bool WantsPad(int pad, ulong mac)
        {
            if (Flags == DsuRegistrationFlags.AllPads)
            {
                return true;
            }

            if (Flags.HasFlag(DsuRegistrationFlags.SlotBased) && Slot == pad)
            {
                return true;
            }

            return Flags.HasFlag(DsuRegistrationFlags.MacBased) && Mac == mac;
        }
    }
}
