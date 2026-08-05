using GameFlow.Infrastructure.Runtime.Templates;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// UI-facing view of HIDMaestro's profile catalog. Wraps the dynamic
/// bridge so view models can list every profile the loaded SDK actually
/// ships (225 across 32 vendors as of v1.1.x) and classify any profile
/// into a kind family for theming — without touching reflection or
/// blocking the UI thread on the probe (first availability check
/// includes a driver-install attempt, which can take seconds).
///
/// <para>When HIDMaestro isn't available (non-Windows, DLL not present
/// yet), the curated per-kind defaults are returned instead so the
/// editor still shows the four built-in choices rather than an empty
/// list.</para>
/// </summary>
public sealed class HidMaestroProfileCatalogService(ILogger<HidMaestroProfileCatalogService> logger)
{
    private static readonly string[] SpecializedControlTerms =
    [
        "wheel", "steering", "racing", "pedal", "shifter", "handbrake",
        "hotas", "flight", "joystick", "throttle", "rudder", "yoke",
        "t16000", "t.16000", "warthog", "airbus", "boeing", "virpil", "winwing"
    ];

    private readonly ILogger<HidMaestroProfileCatalogService> logger = logger;
    private readonly object gate = new();
    private Task<IReadOnlyList<HidMaestroCatalogProfile>>? enumeration;

    /// <summary>
    /// The full catalog, enumerated once off the calling thread and
    /// cached. Returns the curated fallback list when the bridge can't
    /// activate.
    /// </summary>
    public Task<IReadOnlyList<HidMaestroCatalogProfile>> GetProfilesAsync()
    {
        lock (gate)
        {
            enumeration ??= Task.Run(EnumerateOrFallback);
            return enumeration;
        }
    }

    private IReadOnlyList<HidMaestroCatalogProfile> EnumerateOrFallback()
    {
        try
        {
            if (OperatingSystem.IsWindows() && HidMaestroDynamic.IsAvailable(logger))
            {
                var live = HidMaestroDynamic.GetCatalogProfiles(logger);
                if (live.Count > 0)
                {
                    return live;
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "HIDMaestro catalog enumeration failed; using curated defaults.");
        }

        return FallbackProfiles;
    }

    /// <summary>
    /// Classifies a catalog profile id (plus optional display name) into
    /// the nearest kind family — the hook the dashboard theme uses to
    /// follow the selected output controller.
    /// </summary>
    public VirtualControllerKind ClassifyFamily(string? profileId, string? profileName = null) =>
        HidMaestroProfiles.ClassifyFamily(profileId, profileName);

    /// <summary>
    /// True for regular handheld and arcade-style game controllers. The
    /// underlying SDK catalog remains intact; specialized simulation rigs
    /// are only hidden from GameFlow's picker until their extra axes and
    /// feedback semantics have dedicated UI support.
    /// </summary>
    public static bool IsGameControllerProfile(HidMaestroCatalogProfile profile)
    {
        var searchableText = $"{profile.Id} {profile.Name} {profile.Vendor}";
        return !SpecializedControlTerms.Any(term =>
            searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The curated defaults shown when the live catalog isn't available:
    /// one verified profile per non-generic kind.
    /// </summary>
    public static IReadOnlyList<HidMaestroCatalogProfile> FallbackProfiles { get; } =
    [
        new("xbox-360-wired",      "Xbox 360 Controller (Wired)", "Microsoft", 0x045E, 0x028E, 10, 5, true, "usb", true),
        new("dualshock-4-v1-full", "DualShock 4",                 "Sony",      0x054C, 0x05C4, 14, 6, true, "usb", true),
        new("dualsense",           "DualSense (PS5)",             "Sony",      0x054C, 0x0CE6, 15, 6, true, "usb", true),
    ];
}
