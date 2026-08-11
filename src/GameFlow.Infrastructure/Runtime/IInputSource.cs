using GameFlow.Core.Models;

namespace GameFlow.Infrastructure.Runtime;

public interface IInputSource : IAsyncDisposable
{
    string DisplayName { get; }

    ValueTask<ControllerSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by sources that poll hardware on their own thread and can
/// follow the profile's requested rate.
///
/// <para>
/// Without this the runtime's tick rate is only half the story: the
/// coordinator can tick at 1000 Hz and still see the same snapshot four
/// times over if the source refreshes it at 250. Separate from
/// <see cref="IInputSource"/> because most sources — demo input, the web
/// controller, remote link — have no polling loop of their own to steer.
/// </para>
/// </summary>
public interface IPollRateAware
{
    /// <summary>
    /// Requested samples per second. A target, not a guarantee: a device
    /// that reports at 250 Hz does not report faster because it is asked
    /// more often, and the OS timer bounds how tight the loop can be.
    /// </summary>
    int TargetPollingHz { get; set; }
}
