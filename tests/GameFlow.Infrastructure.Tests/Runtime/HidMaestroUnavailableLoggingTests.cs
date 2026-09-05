using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.HidMaestro;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

/// <summary>
/// When HIDMaestro cannot be activated the sink latches "unavailable" and
/// arms a retry cooldown. The latch alone is not what keeps it quiet — the
/// cooldown is, and two of the three paths that set the latch never armed
/// it.
///
/// <para>
/// The consequence was not subtle: <c>WriteAsync</c> runs on the runtime
/// tick, so on the most common failure of all ("HIDMaestro.Core.dll not
/// found") the sink re-probed and re-logged the same warning on every tick
/// — roughly a thousand identical lines a second, per slot, to the console
/// and the rolling file sink, with the file I/O landing on the tick thread.
/// </para>
/// </summary>
public sealed class HidMaestroUnavailableLoggingTests
{
    /// <summary>
    /// Counts warning/error records so a test can assert "logged once", not
    /// "logged once per frame".
    /// </summary>
    private sealed class CountingLogger : ILogger<HidMaestroOutputSink>
    {
        public int Problems { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Problems++;
            }
        }
    }

    [Fact]
    public async Task An_unavailable_backend_is_reported_once_not_once_per_frame()
    {
        // The test host has no HIDMaestro.Core.dll beside it, so this
        // exercises the real unavailable path on Windows and the
        // non-Windows path everywhere else. Both must latch quietly.
        var logger = new CountingLogger();
        await using var sink = new HidMaestroOutputSink(logger);

        var snapshot = ControllerSnapshot.Empty("test");
        for (int frame = 0; frame < 500; frame++)
        {
            await sink.WriteAsync(snapshot, CancellationToken.None);
        }

        Assert.True(
            logger.Problems <= 1,
            $"Expected at most one warning for an unavailable backend, got {logger.Problems} " +
            "— the retry cooldown is not being armed, so every frame re-probes and re-logs.");
    }

    [Fact]
    public async Task An_unavailable_backend_still_says_why_in_its_display_name()
    {
        // Quiet must not mean silent: the reason has to stay visible in the
        // slots list, which is the non-log half of this contract.
        var logger = new CountingLogger();
        await using var sink = new HidMaestroOutputSink(logger);

        await sink.WriteAsync(ControllerSnapshot.Empty("test"), CancellationToken.None);

        Assert.Contains("unavailable", sink.DisplayName, StringComparison.OrdinalIgnoreCase);
    }
}
