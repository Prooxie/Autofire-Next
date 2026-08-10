using System.Collections.Concurrent;

namespace GameFlow.Infrastructure.Runtime;

/// <summary>
/// Decides whether an enumerated input device is one of GameFlow's own
/// virtual pads.
///
/// <para>
/// This has to exist because a virtual controller is, by design,
/// indistinguishable from real hardware to anything that looks at
/// VID/PID — that impersonation is the entire point, and it is what lets
/// a game accept the output. The consequence is that the same device
/// comes straight back in through SDL's enumeration looking like a
/// physical pad, so without an explicit marker a slot can be fed from
/// another slot's output, and that chain can be extended indefinitely.
/// </para>
///
/// <para>
/// Two independent signals, because neither alone is sufficient:
/// </para>
/// <list type="bullet">
/// <item><b>Claimed serials</b> — the authoritative one. An output sink
///   registers the serial it stamped on the device it created, so the
///   match is exact and covers any backend.</item>
/// <item><b>Device path</b> — the fallback, for pads that were already
///   present when the app started (a previous run that did not shut down
///   cleanly leaves them behind). HIDMaestro root-enumerates its devices,
///   so the OS path carries its name.</item>
/// </list>
/// </summary>
public static class VirtualDeviceIdentity
{
    /// <summary>
    /// Enumerator fragment a HIDMaestro device's OS path carries. The
    /// driver installs against <c>root\HIDMaestro</c> (see
    /// <c>driver/hidmaestro.inf</c>), so every device it creates is
    /// root-enumerated under that name.
    /// </summary>
    private const string HidMaestroPathMarker = "hidmaestro";

    private static readonly ConcurrentDictionary<string, byte> ClaimedSerials =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> ClaimedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a device this process created, so enumeration can
    /// recognise it. Called by an output sink once its virtual pad exists.
    /// </summary>
    public static void ClaimSerial(string? serial)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            _ = ClaimedSerials.TryAdd(serial.Trim(), 0);
        }
    }

    /// <summary>Registers a created device by its OS path.</summary>
    public static void ClaimPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _ = ClaimedPaths.TryAdd(path.Trim(), 0);
        }
    }

    /// <summary>Forgets a device this process destroyed.</summary>
    public static void Release(string? serial, string? path)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            _ = ClaimedSerials.TryRemove(serial.Trim(), out _);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            _ = ClaimedPaths.TryRemove(path.Trim(), out _);
        }
    }

    /// <summary>Forgets everything. For teardown and tests.</summary>
    public static void ReleaseAll()
    {
        ClaimedSerials.Clear();
        ClaimedPaths.Clear();
    }

    /// <summary>
    /// True when the device described looks like one of ours.
    ///
    /// <para>
    /// Errs toward <see langword="false"/>: wrongly flagging a real pad
    /// would make it unselectable with no way for the user to override,
    /// which is a worse failure than leaving one virtual pad listed.
    /// </para>
    /// </summary>
    public static bool IsVirtual(string? devicePath, string? serial)
    {
        if (!string.IsNullOrWhiteSpace(serial) && ClaimedSerials.ContainsKey(serial.Trim()))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        var path = devicePath.Trim();
        return ClaimedPaths.ContainsKey(path)
            || path.Contains(HidMaestroPathMarker, StringComparison.OrdinalIgnoreCase);
    }
}
