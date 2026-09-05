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
    private readonly Dictionary<string, ControllerEffectState> deliveredStates =
        new(StringComparer.Ordinal);

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

    /// <summary>Set once the first non-zero rumble write has been reported.</summary>
    private bool rumbleDeliveryLogged;

    /// <summary>Whether the first adaptive-trigger write has been reported.</summary>
    private bool adaptiveDeliveryLogged;

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
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunLoop(stoppingToken);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "GameFlow effects",

            // Above normal so a busy UI cannot delay a motor stop, but not
            // Highest — this must never compete with the mapping tick,
            // which is the one thing users feel immediately.
            Priority = ThreadPriority.AboveNormal
        };

        thread.Start();
        // BackgroundService.StopAsync waits for this task. Returning a
        // completed task here used to let host teardown race past the still-
        // running effects thread and its final motor/trigger release.
        return completion.Task;
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

            // The last hop: a non-zero rumble actually handed to the
            // physical pad. Together with the slot-level line upstream this
            // brackets the whole path, so a silent motor points at exactly
            // one of the two halves rather than at the whole feature.
            if (!rumbleDeliveryLogged &&
                (write.State.LowFrequencyRumble > 0d || write.State.HighFrequencyRumble > 0d))
            {
                rumbleDeliveryLogged = true;
                logger.LogInformation(
                    "Controller effects: first rumble write to {Device} — low={Low:F2} high={High:F2}, accepted={Accepted}.",
                    write.DeviceId,
                    write.State.LowFrequencyRumble,
                    write.State.HighFrequencyRumble,
                    ok);
            }

            // Same bracketing for adaptive triggers as for rumble above.
            // A trigger mode that "does not work" can fail at the encoder,
            // in the plan, or at the pad, and the three are indistinguishable
            // from the outside — this names which effect was asked for and
            // whether the pad took it.
            var leftEffect = write.State.LeftTrigger?.Effect ?? AdaptiveTriggerEffect.Off;
            var rightEffect = write.State.RightTrigger?.Effect ?? AdaptiveTriggerEffect.Off;

            if (!adaptiveDeliveryLogged &&
                (leftEffect != AdaptiveTriggerEffect.Off || rightEffect != AdaptiveTriggerEffect.Off))
            {
                adaptiveDeliveryLogged = true;
                logger.LogInformation(
                    "Controller effects: first adaptive-trigger write to {Device} — left={Left} "
                    + "(start={LStart} end={LEnd} strength={LStrength} freq={LFreq}Hz), right={Right}, accepted={Accepted}.",
                    write.DeviceId,
                    leftEffect,
                    write.State.LeftTrigger?.StartPosition ?? 0,
                    write.State.LeftTrigger?.EndPosition ?? 0,
                    write.State.LeftTrigger?.Strength ?? 0,
                    write.State.LeftTrigger?.FrequencyHz ?? 0,
                    rightEffect,
                    ok);
            }

            if (ok)
            {
                WritesDelivered++;
                // This is deliberately updated only after the writer accepts
                // the state. It survives queue equality suppression, so the
                // shutdown path still knows which steady devices need an
                // explicit stop even when TakeDueWrites() has nothing due.
                deliveredStates[write.DeviceId] = write.State;
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
        foreach (var (deviceId, state) in deliveredStates.ToList())
        {
            if (state.IsSilent)
            {
                continue;
            }

            try
            {
                if (writer.TryWrite(deviceId, ControllerEffectState.Silent))
                {
                    deliveredStates[deviceId] = ControllerEffectState.Silent;
                }
            }
            catch (Exception exception)
            {
                // Try every known device even when one unplugged during
                // shutdown; one failure must not leave the remaining pads on.
                logger.LogDebug(
                    exception,
                    "Ignoring failure while silencing effects for {Device} on shutdown.",
                    deviceId);
            }
        }
    }
}
