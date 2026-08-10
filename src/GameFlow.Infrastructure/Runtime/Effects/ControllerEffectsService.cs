using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// The dedicated effects thread.
///
/// <para>
/// Rumble, lighting and adaptive triggers were previously written inline
/// from the mapping tick, and SDL holds its device lock across the
/// blocking Bluetooth HID transfer those writes perform — so a single pad
/// on Bluetooth stalled the whole runtime, and the feature was pulled
/// rather than shipped stuttering. Everything here exists to keep that
/// blocking call on a thread of its own, where a slow pad costs only its
/// own latency.
/// </para>
///
/// <para>
/// A dedicated long-running thread rather than the thread pool: the loop
/// blocks by design, and pool threads are the wrong place to do that —
/// starving the pool would reintroduce the very stall this avoids, just
/// somewhere less obvious.
/// </para>
/// </summary>
public sealed class ControllerEffectsService : BackgroundService
{
    /// <summary>
    /// How often the thread looks for work. 8 ms is comfortably finer than
    /// the ~16 ms rate limit below, so the limiter decides pacing rather
    /// than the poll interval.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Minimum spacing between writes to one device — about 60 Hz. Past
    /// this, a pad cannot render the difference and Bluetooth starts
    /// dropping reports, so faster writes buy nothing and cost latency.
    /// </summary>
    private static readonly TimeSpan WriteInterval = TimeSpan.FromMilliseconds(16);

    private readonly IControllerEffectWriter writer;
    private readonly ILogger<ControllerEffectsService> logger;

    public ControllerEffectsService(
        IControllerEffectWriter writer,
        ILogger<ControllerEffectsService> logger)
    {
        this.writer = writer;
        this.logger = logger;
        Queue = new EffectDispatchQueue(WriteInterval);
    }

    /// <summary>
    /// Where producers publish. The mapping tick, the tuning editor and
    /// the output sink's rumble feedback all write here and never touch
    /// hardware themselves.
    /// </summary>
    public EffectDispatchQueue Queue { get; }

    /// <summary>Effect writes delivered since start. Diagnostics.</summary>
    public long WritesDelivered { get; private set; }

    /// <summary>Writes that failed and were re-queued. Diagnostics.</summary>
    public long WritesFailed { get; private set; }

    /// <summary>Last observed backend availability, so the transition logs once each way.</summary>
    private bool backendAvailable = true;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Support is NOT decided here. The backend is the SDL input
        // source, which the runtime creates lazily when it activates a
        // provider — after this hosted service starts. Checking once at
        // startup therefore always saw "unsupported" and parked the thread
        // permanently, so effects never reached hardware however well the
        // rest of the chain worked. The loop checks per pass instead.

        // Run the loop on its own thread rather than returning an async
        // state machine to the host: this body blocks, and it must not
        // borrow a pool thread to do it.
        var thread = new Thread(() => RunLoop(stoppingToken))
        {
            IsBackground = true,
            Name = "GameFlow effects",

            // Above normal so a busy UI cannot delay a motor stop, but not
            // Highest — this must never compete with the mapping tick,
            // which is the one thing users feel immediately.
            Priority = ThreadPriority.AboveNormal
        };

        thread.Start();
        return Task.CompletedTask;
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        logger.LogInformation("Controller effects thread started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DrainOnce();

                // A plain sleep, not a timer: this thread exists to block,
                // and a sleeping dedicated thread costs nothing.
                try
                {
                    Thread.Sleep(PollInterval);
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Controller effects thread stopped unexpectedly.");
        }
        finally
        {
            SilenceEverything();
            logger.LogInformation(
                "Controller effects thread stopped after {Delivered} write(s), {Failed} failure(s).",
                WritesDelivered, WritesFailed);
        }
    }

    private void DrainOnce()
    {
        if (!writer.IsSupported)
        {
            // No backend yet. Log the transition once each way so the
            // state is visible without spamming a line per poll.
            if (backendAvailable)
            {
                backendAvailable = false;
                logger.LogInformation(
                    "Controller effects: no backend attached; rumble and lighting will save but not reach hardware.");
            }

            return;
        }

        if (!backendAvailable)
        {
            backendAvailable = true;
            logger.LogInformation("Controller effects: backend attached, effects are now reaching hardware.");
        }

        var due = Queue.TakeDueWrites(DateTimeOffset.UtcNow);
        if (due.Count == 0)
        {
            return;
        }

        foreach (var write in due)
        {
            bool ok;
            try
            {
                ok = writer.TryWrite(write.DeviceId, write.State);
            }
            catch (Exception exception)
            {
                // A pad unplugged mid-write is ordinary, not exceptional.
                // One bad device must not stop the others.
                logger.LogDebug(exception, "Effect write to {Device} threw; treating as a failed write.", write.DeviceId);
                ok = false;
            }

            if (ok)
            {
                WritesDelivered++;
            }
            else
            {
                WritesFailed++;
                Queue.Requeue(write);
            }
        }

        Queue.PurgeRetired();
    }

    /// <summary>
    /// Best-effort all-off on shutdown. Without it, quitting the app
    /// mid-rumble leaves the pad buzzing until it is power-cycled.
    /// </summary>
    private void SilenceEverything()
    {
        try
        {
            foreach (var write in Queue.TakeDueWrites(DateTimeOffset.UtcNow))
            {
                if (!write.State.IsSilent)
                {
                    _ = writer.TryWrite(write.DeviceId, ControllerEffectState.Silent);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Ignoring failure while silencing effects on shutdown.");
        }
    }
}
