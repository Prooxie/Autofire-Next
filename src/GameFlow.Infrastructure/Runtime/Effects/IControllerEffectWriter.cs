namespace GameFlow.Infrastructure.Runtime.Effects;

/// <summary>
/// Writes effect state to one physical pad.
///
/// <para>
/// Implementations are expected to BLOCK — a Bluetooth HID write is
/// milliseconds, not microseconds, and on SDL it holds the device lock
/// throughout. That is precisely why this is called only from
/// <see cref="ControllerEffectsService"/>'s own thread and never from the
/// mapping tick, which is what froze the runtime the last time effects
/// were wired up.
/// </para>
/// </summary>
public interface IControllerEffectWriter
{
    /// <summary>
    /// True when this platform/backend can drive effects at all. False
    /// makes the effects service idle out instead of burning a thread.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Applies <paramref name="state"/> to <paramref name="deviceId"/>.
    /// Returns false when the write did not land, so the queue can retry.
    /// Must not throw for an ordinary device-gone-away.
    /// </summary>
    bool TryWrite(string deviceId, in ControllerEffectState state);
}

/// <summary>
/// Effect writer for platforms with no effect backend. Reports
/// unsupported so <see cref="ControllerEffectsService"/> parks itself.
/// </summary>
public sealed class NullControllerEffectWriter : IControllerEffectWriter
{
    public bool IsSupported => false;

    public bool TryWrite(string deviceId, in ControllerEffectState state) => true;
}
