using System.Collections.Concurrent;
using GameFlow.Infrastructure.Runtime.Effects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Effects;

public sealed class ControllerEffectsServiceTests
{
    [Fact]
    public async Task StopAsyncWaitsForFinalSilenceOfAnAlreadyDeliveredSteadyState()
    {
        var writer = new BlockingSilenceWriter();
        using var service = new ControllerEffectsService(
            writer,
            NullLogger<ControllerEffectsService>.Instance);

        await service.StartAsync(CancellationToken.None);
        service.Queue.Publish("pad-1", new ControllerEffectState
        {
            LowFrequencyRumble = 0.75,
            LeftTrigger = new AdaptiveTriggerCommand(
                AdaptiveTriggerEffect.Constant, 20, 180, 200),
        });

        await writer.NonSilentDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The state is steady now: the dispatch queue has no changed write
        // due. Shutdown must use the service's successfully-delivered state,
        // not TakeDueWrites(), or this pad would never receive its stop.
        var stopTask = service.StopAsync(CancellationToken.None);
        await writer.SilenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopTask.IsCompleted);

        writer.AllowSilence.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        var writes = writer.Writes.Where(write => write.DeviceId == "pad-1").ToList();
        Assert.True(writes.Count >= 2);
        Assert.False(writes[0].State.IsSilent);
        Assert.Equal(ControllerEffectState.Silent, writes[^1].State);
    }

    private sealed class BlockingSilenceWriter : IControllerEffectWriter
    {
        public ConcurrentQueue<EffectWrite> Writes { get; } = new();
        public TaskCompletionSource<bool> NonSilentDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SilenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowSilence { get; } = new(initialState: false);

        public bool IsSupported => true;

        public bool TryWrite(string deviceId, in ControllerEffectState state)
        {
            Writes.Enqueue(new EffectWrite(deviceId, state));
            if (state.IsSilent)
            {
                SilenceStarted.TrySetResult(true);
                _ = AllowSilence.Wait(TimeSpan.FromSeconds(2));
            }
            else
            {
                NonSilentDelivered.TrySetResult(true);
            }

            return true;
        }
    }
}
