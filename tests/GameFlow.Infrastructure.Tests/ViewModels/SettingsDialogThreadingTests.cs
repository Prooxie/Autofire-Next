using System.Collections.Concurrent;
using GameFlow.App.Bootstrap;
using GameFlow.App.ViewModels;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameFlow.Infrastructure.Tests.ViewModels;

public sealed class SettingsDialogThreadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPublishesSuccessAndFailureOnCallingContext(bool fail)
    {
        var settings = new DeferredSettings();
        using var host = HostBuilderFactory.Create([])
            .ConfigureServices(services => services.AddSingleton<IUserSettingsService>(settings)).Build();
        var vm = host.Services.GetRequiredService<SettingsDialogViewModel>();
        using var context = new PumpContext();
        var previous = SynchronizationContext.Current;
        var notificationContexts = new List<SynchronizationContext?>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            vm.PropertyChanged += (_, _) => notificationContexts.Add(SynchronizationContext.Current);
            var apply = vm.ApplyAsync();
            if (fail) settings.Completion.SetException(new IOException("test save failed"));
            else settings.Completion.SetResult();
            context.PumpUntil(apply);
            Assert.Equal(!fail, await apply);
            Assert.NotEmpty(notificationContexts);
            Assert.All(notificationContexts, actual => Assert.Same(context, actual));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            vm.Dispose();
        }
    }

    private sealed class DeferredSettings : IUserSettingsService
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AppSettings Current { get; } = new();
        public event EventHandler<UserSettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyAsync(AppSettings updated, CancellationToken cancellationToken = default) => Completion.Task;
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        public void PumpUntil(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                if (queue.TryTake(out var work, 20)) work.Callback(work.State);
            }
            Assert.True(task.IsCompleted, "The UI command did not complete within five seconds.");
        }
        public void Dispose() => queue.Dispose();
    }
}
