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

    /// <summary>
    /// Enumerator every software-created device is named under. See the
    /// note in <see cref="IsVirtual"/>.
    /// </summary>
    private const string RootEnumeratorPrefix = @"ROOT\";

    /// <summary>
    /// Instance-path prefix of a HID device created by a HID minidriver
    /// with no bus device underneath it.
    /// </summary>
    /// <remarks>
    /// This is the spelling HIDMaestro's own pads actually enumerate
    /// under, which neither of the other two signals covers. A live
    /// capture of a virtual DualShock 4 gave SDL the interface path
    /// <c>\\?\HID#HIDCLASS#1&amp;4784345&amp;10b&amp;0000#{4d1e55b2-...}</c>:
    /// no <c>hidmaestro</c> anywhere in it, and <c>HID</c> rather than
    /// <c>ROOT</c> as the enumerator, so it was classified as physical
    /// hardware on every poll and only ever hidden later, by the slower
    /// vendor/product filter — which is exactly the "appears as a real
    /// controller first, then vanishes" flicker.
    ///
    /// <para>
    /// Real pads never look like this. They arrive over a bus and the
    /// second path segment names their identity —
    /// <c>HID\VID_054C&amp;PID_0CE6&amp;MI_03\...</c> over USB,
    /// <c>HID\{00001124-...}_VID&amp;0002054C_PID&amp;0CE6\...</c> over
    /// Bluetooth. <c>HIDCLASS</c> in that position means the HID stack
    /// itself parented the device because nothing else did, which on a
    /// gamepad means software created it.
    /// </para>
    /// </remarks>
    private const string SoftwareHidPrefix = @"HID\HIDCLASS\";

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

    /// <summary>Registers a created device by its OS path or instance id.</summary>
    public static void ClaimPath(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length > 0)
        {
            _ = ClaimedPaths.TryAdd(normalized, 0);
        }
    }

    /// <summary>
    /// Reduces the several spellings Windows uses for one device to a
    /// single comparable form.
    /// </summary>
    /// <remarks>
    /// The same device is named differently depending on who is asking.
    /// The SDK reports an instance id — <c>ROOT\VID_045E&amp;PID_028E&amp;IG_00\0</c>
    /// — while SDL reports the device interface path,
    /// <c>\\?\ROOT#VID_045E&amp;PID_028E&amp;IG_00#0000#{4d1e55b2-…}</c>.
    /// They describe one device: separators differ, the interface path
    /// carries a prefix and a trailing class GUID. Folding both to
    /// uppercase, backslash-separated, prefix- and GUID-free lets a claim
    /// made from one source match a lookup from the other.
    /// </remarks>
    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().Replace('#', '\\');

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            text.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        // Drop a trailing interface-class GUID, which the instance id form
        // never carries.
        var guid = text.IndexOf(@"\{", StringComparison.Ordinal);
        if (guid >= 0)
        {
            text = text[..guid];
        }

        return text.Trim('\\').ToUpperInvariant();
    }

    /// <summary>Forgets a device this process destroyed.</summary>
    public static void Release(string? serial, string? path)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            _ = ClaimedSerials.TryRemove(serial.Trim(), out _);
        }

        var normalized = NormalizePath(path);
        if (normalized.Length > 0)
        {
            _ = ClaimedPaths.TryRemove(normalized, out _);
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

        var path = NormalizePath(devicePath);
        if (path.Length == 0)
        {
            return false;
        }

        if (ClaimedPaths.ContainsKey(path) ||
            path.Contains(HidMaestroPathMarker, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Parented by the HID stack rather than by a bus, and therefore
        // software-created. See SoftwareHidPrefix.
        if (path.StartsWith(SoftwareHidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Root-enumerated, and therefore software-created.
        //
        // This is the signal that survives a crash. A claim only covers
        // pads THIS process made, so a previous run that did not shut down
        // cleanly leaves its pads behind, still enumerating, with nothing
        // claiming them — and the "hidmaestro" name is on a sibling
        // software node, not on the HID interface SDL reports. What is
        // left is the enumerator: real pads arrive over a bus (USB, or
        // BTHENUM over Bluetooth) and are named for it, while a device
        // with no bus behind it is created by software and named ROOT.
        return path.StartsWith(RootEnumeratorPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
