namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// Keeps feedback teardown ordered: consumers first observe an explicit
/// motor stop, then the virtual controller that could have produced that
/// stop is detached. This matters when a cached output sink reconfigures
/// itself without SlotRuntime replacing the sink object.
/// </summary>
internal static class HidMaestroRumbleLifecycle
{
    public static void StopAndTeardown(
        Action<double, double>? rumbleReceived,
        Action teardown)
    {
        try
        {
            rumbleReceived?.Invoke(0d, 0d);
        }
        finally
        {
            teardown();
        }
    }
}
