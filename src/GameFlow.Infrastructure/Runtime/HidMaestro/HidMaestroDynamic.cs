using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// A live dynamic controller plus everything the sink needs to describe
/// it: the resolved catalog id, display name, and the REAL hardware
/// identity it advertises (read from the deployed profile, so
/// input-hiding works for every catalog profile, not just the four
/// curated kinds).
/// </summary>
internal sealed record DynamicControllerHandle(
    DynamicHidMaestroController Controller,
    string ProfileId,
    string ProfileName,
    (ushort Vid, ushort Pid)? HardwareSignature);

/// <summary>
/// Runtime (reflection-based) bridge to HIDMaestro.Core, and the only way
/// the SDK is bound. A user drops <c>HIDMaestro.Core.dll</c> next to the
/// executable (or into a <c>HIDMaestro</c> subfolder) and the real sink
/// activates — no rebuild, no compile symbol, no project reference.
///
/// <para>
/// Everything here is defensive and LOUD. Binding is all-or-nothing —
/// <see cref="TryCreateController"/> either returns a controller with
/// every required member correctly wired, or fails immediately with a
/// complete list of what could and couldn't be found. On top of the
/// earlier revision this adds the three things that were actually
/// keeping virtual controllers from being created in the field:
/// </para>
/// <list type="number">
/// <item><b>Elevation detection.</b> HIDMaestro's driver install and
/// device creation need administrator rights
/// (<c>SeLoadDriverPrivilege</c>). A non-elevated process used to
/// resolve as "available" and then fail every CreateController with an
/// opaque access error; now the probe checks
/// <see cref="Environment.IsPrivilegedProcess"/> up front and the status
/// says, in words, "run GameFlow as Administrator."</item>
/// <item><b>Catalog-verified profile ids.</b> Profile lookups go through
/// <see cref="TryResolveExistingProfileId"/>, which checks an ordered
/// candidate list against the SDK's actually-loaded catalog instead of
/// trusting one hardcoded slug.</item>
/// <item><b>Runtime-built custom profiles.</b>
/// <see cref="TryCreateCustomController"/> drives
/// <c>HMProfileBuilder</c> + <c>HidDescriptorBuilder</c> through
/// reflection so the Generic (DirectInput) kind creates a real device
/// from the template's axis/button/POV counts — previously it asked the
/// catalog for a slug named "custom", which doesn't exist, and always
/// failed.</item>
/// </list>
/// </summary>
internal static class HidMaestroDynamic
{
    private enum ProbeOutcome { NotAttempted, DllNotFound, Failed, Available }

    private static readonly object Gate = new();
    private static ProbeOutcome outcome = ProbeOutcome.NotAttempted;
    private static DateTimeOffset lastProbeAt = DateTimeOffset.MinValue;
    private static string status = "Not probed yet.";

    /// <summary>Re-probe interval when the DLL simply wasn't there yet, so dropping it in doesn't require an app restart.</summary>
    private static readonly TimeSpan NotFoundReprobeInterval = TimeSpan.FromSeconds(10);

    private static object? context;
    private static HidMaestroDriverInitialization? driverInitialization;
    private static Type? profileType;         // HMProfile
    private static Type? controllerType;      // HMController
    private static Type? stateType;           // HMGamepadState
    private static Type? buttonEnumType;      // HMButton
    private static Type? hatEnumType;         // HMHat
    private static Type? profileBuilderType;  // HMProfileBuilder (optional — custom path only)
    private static Type? descriptorBuilderType; // HidDescriptorBuilder (optional — custom path only)
    private static MethodInfo? getProfile;             // HMContext.GetProfile(string)
    private static MethodInfo? createFromProfile;      // HMContext.CreateController(HMProfile)
    private static MethodInfo? createFromString;       // HMContext.CreateController(string), if the SDK has one
    private static MethodInfo? submitState;            // HMController.SubmitState(in HMGamepadState)
    private static IReadOnlyList<HidMaestroCatalogProfile>? catalogCache;

    private static readonly string[] CandidateFileNames =
    [
        "HIDMaestro.Core.dll",
        "HidMaestro.Core.dll",
        "hidmaestro.core.dll",
    ];

    /// <summary>
    /// Environment variable that can point at a directory containing
    /// HIDMaestro.Core.dll, for installs that keep the SDK outside the
    /// app folder.
    /// </summary>
    private const string DirectoryOverrideVariable = "GAMEFLOW_HIDMAESTRO_DIR";

    // Preferred name fragments for the per-frame state-submit method, in
    // priority order. The real SDK method is SubmitState(in HMGamepadState)
    // (verified against example/SdkDemo/Program.cs); the hint list keeps
    // resolution working if a future SDK renames it, and disambiguates if
    // HMController ever exposes a second single-parameter method that
    // accepts an HMGamepadState.
    private static readonly string[] SubmitNameHints = ["submitstate", "submit", "send", "write", "update", "push", "set"];

    public static string StatusDescription
    {
        get { lock (Gate) { return status; } }
    }

    /// <summary>
    /// True when the current process can actually install the driver and
    /// create devices. HIDMaestro's own SdkDemo states the requirement:
    /// "Requires admin (virtual device creation needs
    /// SeLoadDriverPrivilege)."
    /// </summary>
    public static bool IsProcessElevated => Environment.IsPrivilegedProcess;

    public static bool IsAvailable(ILogger logger)
    {
        lock (Gate)
        {
            var shouldProbe = outcome switch
            {
                ProbeOutcome.NotAttempted => true,
                // The one recoverable case: the DLL wasn't there. Re-check
                // periodically so "drop the DLL next to the exe" starts
                // working without restarting the app.
                ProbeOutcome.DllNotFound => DateTimeOffset.UtcNow - lastProbeAt >= NotFoundReprobeInterval,
                _ => false,
            };

            if (shouldProbe)
            {
                lastProbeAt = DateTimeOffset.UtcNow;
                try
                {
                    Probe(logger);
                }
                catch (Exception exception)
                {
                    outcome = ProbeOutcome.Failed;
                    status = $"Probe failed: {exception.Message}";
                    logger.LogWarning(exception, "HIDMaestro dynamic probe failed.");
                }
            }

            return outcome == ProbeOutcome.Available;
        }
    }

    private static void Probe(ILogger logger)
    {
        var searchRoots = GetSearchRoots();
        string? path = searchRoots
            .SelectMany(root => CandidateFileNames.Select(name => Path.Combine(root, name)))
            .FirstOrDefault(File.Exists);

        if (path is null)
        {
            outcome = ProbeOutcome.DllNotFound;
            status = "HIDMaestro.Core.dll not found. Place the SDK assembly next to the executable " +
                     $"(or in a 'HIDMaestro' subfolder, or point {DirectoryOverrideVariable} at its folder) " +
                     "to activate HIDMaestro output. Searched: " + string.Join("; ", searchRoots);
            logger.LogWarning("HIDMaestro dynamic: {Status}", status);
            return;
        }

        var elevated = !OperatingSystem.IsWindows() || IsProcessElevated;
        var assembly = Assembly.LoadFrom(path);
        Type? Find(string simpleName) =>
            assembly.GetTypes().FirstOrDefault(t => string.Equals(t.Name, simpleName, StringComparison.Ordinal));

        var contextType       = Find("HMContext");
        profileType           = Find("HMProfile");
        controllerType        = Find("HMController");
        stateType             = Find("HMGamepadState");
        buttonEnumType        = Find("HMButton");
        hatEnumType           = Find("HMHat");
        profileBuilderType    = Find("HMProfileBuilder");
        descriptorBuilderType = Find("HidDescriptorBuilder");

        if (contextType is null || controllerType is null || stateType is null
            || buttonEnumType is null || hatEnumType is null)
        {
            outcome = ProbeOutcome.Failed;
            status = "HIDMaestro.Core.dll loaded but expected types are missing " +
                     $"(HMContext:{contextType is not null} HMController:{controllerType is not null} " +
                     $"HMGamepadState:{stateType is not null} HMButton:{buttonEnumType is not null} " +
                     $"HMHat:{hatEnumType is not null}). Exported types: " +
                     string.Join(", ", assembly.GetExportedTypes().Take(24).Select(t => t.Name));
            logger.LogWarning("HIDMaestro dynamic: {Status}", status);
            return;
        }

        context = Activator.CreateInstance(contextType)
            ?? throw new InvalidOperationException("HMContext could not be instantiated.");

        // Bootstrap calls, any-arity (a strictly parameterless-only match
        // once silently skipped the ONE call that installs the driver).
        var loadedProfilesResult = InvokeBestEffort(contextType, "LoadDefaultProfiles", logger, out var loadFailure);
        int? profileCount = loadedProfilesResult switch
        {
            int i => i,
            short s => s,
            long l and >= 0 and <= int.MaxValue => (int)l,
            _ => null,
        };
        // Catalog discovery is read-only. InstallDriver performs a system-wide
        // sweep, so invoke it only when the first output is actually created.
        driverInitialization = new HidMaestroDriverInitialization(context);

        getProfile = contextType.GetMethod("GetProfile", [typeof(string)]);

        var createOverloads = contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "CreateController" && m.GetParameters().Length == 1)
            .ToList();
        createFromProfile = createOverloads.FirstOrDefault(m =>
            profileType is not null && m.GetParameters()[0].ParameterType.IsAssignableFrom(profileType))
            ?? createOverloads.FirstOrDefault(m => m.GetParameters()[0].ParameterType != typeof(string));
        createFromString = createOverloads.FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(string));

        submitState = ResolveSubmitMethod(controllerType!, stateType!, logger);

        var canCreateByProfile = createFromProfile is not null && getProfile is not null;
        var canCreateByString = createFromString is not null;

        if (submitState is null || (!canCreateByProfile && !canCreateByString))
        {
            outcome = ProbeOutcome.Failed;
            status = "HIDMaestro.Core API mismatch — could not bind " +
                     $"(GetProfile:{getProfile is not null} CreateController(profile):{createFromProfile is not null} " +
                     $"CreateController(string):{createFromString is not null} SubmitState:{submitState is not null}). " +
                     "HMController members: " +
                     string.Join(", ", controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Select(m => m.Name).Distinct().Take(24)) +
                     " | HMContext members: " +
                     string.Join(", ", contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Select(m => m.Name).Distinct().Take(24));
            logger.LogWarning("HIDMaestro dynamic: {Status}", status);
            return;
        }

        outcome = ProbeOutcome.Available;
        var profileSummary = profileCount is int count ? $"{count} profiles" : "profile catalog";
        status = $"Active (dynamic) — loaded {Path.GetFileName(path)} ({profileSummary}, submit '{submitState.Name}').";

        if (!elevated)
        {
            status += " WARNING: GameFlow is NOT running elevated. HIDMaestro needs administrator rights " +
                      "to install its driver and create virtual devices — if no controller appears, " +
                      "restart GameFlow as Administrator.";
            logger.LogWarning(
                "HIDMaestro dynamic bridge resolved, but the process is not elevated. Driver install and " +
                "device creation need administrator rights (SeLoadDriverPrivilege) — restart as Administrator " +
                "if no virtual controller appears.");
        }
        if (loadFailure is not null)
        {
            logger.LogWarning("HIDMaestro dynamic: LoadDefaultProfiles() failed ({Failure}); catalog lookups may miss.", loadFailure);
        }

        catalogCache = null; // (re)enumerate lazily against the new context
        logger.LogInformation("HIDMaestro dynamic bridge ready: {Path} (submit='{Submit}', elevated={Elevated}).",
            path, submitState.Name, elevated);
    }

    /// <summary>Directories probed for HIDMaestro.Core.dll, in priority order.</summary>
    private static IReadOnlyList<string> GetSearchRoots()
    {
        var roots = new List<string>(3);

        var overrideDirectory = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && Directory.Exists(overrideDirectory))
        {
            roots.Add(overrideDirectory);
        }

        roots.Add(AppContext.BaseDirectory);
        roots.Add(Path.Combine(AppContext.BaseDirectory, "HIDMaestro"));
        return roots;
    }

    /// <summary>
    /// Finds HMController's per-frame state-submit method. Name hints
    /// disambiguate when multiple single-parameter methods accept an
    /// HMGamepadState, falling back to the first match with a logged
    /// warning so a wrong pick is at least visible, not silent.
    /// </summary>
    private static MethodInfo? ResolveSubmitMethod(Type controllerType, Type stateType, ILogger logger)
    {
        var candidates = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.GetElementTypeOrSelf() == stateType)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        foreach (var hint in SubmitNameHints)
        {
            var match = candidates.FirstOrDefault(m => m.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        logger.LogWarning(
            "HIDMaestro dynamic: {Count} candidate submit methods found ({Names}) and none matched a known verb — " +
            "picking '{Picked}'. If input doesn't arrive, this is the first thing to check.",
            candidates.Count, string.Join(", ", candidates.Select(c => c.Name)), candidates[0].Name);
        return candidates[0];
    }

    /// <summary>
    /// Re-applies friendly names to every live virtual controller. Call once
    /// after ALL of a rebuild's controllers exist, never per controller.
    ///
    /// <para>
    /// The SDK documents a Windows PnP race this exists to close: creating a
    /// second controller re-triggers driver-bind activity that overwrites the
    /// FIRST one's friendly name. Any session with more than one slot
    /// therefore ends up with a mis-named device unless the names are
    /// re-applied after PnP settles, which is what this does — it polls for
    /// every controller's HID child to reach DN_STARTED rather than sleeping
    /// a fixed interval, so it costs well under a tenth of a second on a
    /// machine that is keeping up.
    /// </para>
    ///
    /// <para>
    /// Best-effort by design: an SDK build without the method, or a failure
    /// inside it, leaves the names as Windows left them and is not worth
    /// failing a rebuild over.
    /// </para>
    /// </summary>
    public static void FinalizeControllerNames(ILogger logger)
    {
        if (context is null)
        {
            return;
        }

        _ = InvokeBestEffort(context.GetType(), "FinalizeNames", logger, out var failure);
        if (failure is not null)
        {
            logger.LogDebug(
                "HIDMaestro dynamic: FinalizeNames() failed ({Failure}); virtual controller names may " +
                "show the driver default.", failure);
        }
    }

    /// <summary>
    /// Invokes a method by name if it exists, tolerating any parameter
    /// count (required parameters get a reasonable default rather than
    /// causing the lookup to skip the method entirely). Returns the
    /// method's return value on success and sets
    /// <paramref name="failure"/> to the exception message if the call
    /// threw, or null if it succeeded or the method wasn't found.
    /// </summary>
    private static object? InvokeBestEffort(Type type, string methodName, ILogger logger, out string? failure)
    {
        failure = null;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault();

        if (method is null)
        {
            logger.LogDebug("HIDMaestro dynamic: {Method}() not found on {Type} — skipped.", methodName, type.Name);
            return null;
        }

        try
        {
            var args = method.GetParameters()
                .Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultFor(p.ParameterType))
                .ToArray();
            var result = method.Invoke(context, args);
            logger.LogDebug("HIDMaestro dynamic: {Method}({Arity} args) invoked.", methodName, args.Length);
            return result;
        }
        catch (Exception exception)
        {
            var inner = (exception as TargetInvocationException)?.InnerException ?? exception;
            logger.LogWarning(inner, "HIDMaestro dynamic: {Method}() threw — continuing.", methodName);
            failure = inner.Message;
            return null;
        }
    }

    private static object? DefaultFor(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;

    private static Type GetElementTypeOrSelf(this Type type) =>
        type.IsByRef ? type.GetElementType()! : type;

    // ─── Catalog enumeration ────────────────────────────────────────────

    /// <summary>
    /// The SDK's loaded profile catalog as typed records, or an empty
    /// list when the bridge isn't available or the catalog can't be
    /// enumerated on this SDK build. Cached after the first successful
    /// enumeration. Safe to call from any thread.
    /// </summary>
    public static IReadOnlyList<HidMaestroCatalogProfile> GetCatalogProfiles(ILogger logger)
    {
        lock (Gate)
        {
            if (outcome != ProbeOutcome.Available || context is null)
            {
                return [];
            }
            if (catalogCache is not null)
            {
                return catalogCache;
            }

            catalogCache = EnumerateCatalogLocked(logger);
            return catalogCache;
        }
    }

    /// <summary>Callers must hold <see cref="Gate"/>.</summary>
    private static IReadOnlyList<HidMaestroCatalogProfile> EnumerateCatalogLocked(ILogger logger)
    {
        try
        {
            var contextType = context!.GetType();
            var candidateNames = new[] { "GetProfiles", "Profiles", "AllProfiles", "ListProfiles", "EnumerateProfiles" };

            foreach (var name in candidateNames)
            {
                var member = (MemberInfo?)contextType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
                    ?? contextType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (member is null)
                {
                    continue;
                }

                var result = member is MethodInfo methodInfo
                    ? methodInfo.Invoke(context, null)
                    : ((PropertyInfo)member).GetValue(context);

                if (result is null || result is string || result is not System.Collections.IEnumerable enumerable)
                {
                    continue;
                }

                var profiles = new List<HidMaestroCatalogProfile>(256);
                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        continue;
                    }
                    var profile = ReadCatalogProfile(item);
                    if (profile is not null)
                    {
                        profiles.Add(profile);
                    }
                }

                if (profiles.Count > 0)
                {
                    logger.LogInformation(
                        "HIDMaestro dynamic: enumerated {Count} catalog profile(s) via {Type}.{Member}.",
                        profiles.Count, contextType.Name, name);
                    return profiles
                        .OrderBy(p => p.Vendor, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            logger.LogWarning(
                "HIDMaestro dynamic: could not enumerate the profile catalog (none of {Names} matched). " +
                "Profile picking falls back to the curated defaults; explicit ids still work.",
                string.Join("/", candidateNames));
            return [];
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "HIDMaestro dynamic: profile catalog enumeration failed.");
            return [];
        }
    }

    private static HidMaestroCatalogProfile? ReadCatalogProfile(object item)
    {
        var type = item.GetType();
        string ReadString(params string[] names)
        {
            foreach (var name in names)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(item) is { } value)
                {
                    return value.ToString() ?? string.Empty;
                }
            }
            return string.Empty;
        }
        T ReadValue<T>(T fallback, params string[] names) where T : struct, IConvertible
        {
            foreach (var name in names)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                var value = property?.GetValue(item);
                if (value is IConvertible convertible)
                {
                    try { return (T)Convert.ChangeType(convertible, typeof(T)); }
                    catch { /* wrong shape — try the next candidate name */ }
                }
            }
            return fallback;
        }

        var id = ReadString("Id", "Slug", "ProfileId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new HidMaestroCatalogProfile(
            Id: id,
            Name: ReadString("Name", "DisplayName") is { Length: > 0 } displayName ? displayName : id,
            Vendor: ReadString("Vendor", "Manufacturer", "ManufacturerString"),
            VendorId: ReadValue<ushort>(0, "VendorId", "Vid"),
            ProductId: ReadValue<ushort>(0, "ProductId", "Pid"),
            ButtonCount: ReadValue(0, "ButtonCount", "Buttons"),
            AxisCount: ReadValue(0, "AxisCount", "Axes"),
            HasHat: ReadValue(false, "HasHat"),
            Connection: ReadString("Connection", "ConnectionType"),
            // Real pre-flight check (HMProfile.IsDeployable, backed by
            // Inner.HasDescriptor): a handful of catalog entries are
            // metadata-only and throw "has no HID descriptor and cannot
            // be deployed" from CreateController every time, with no
            // retry that would ever fix it. Default true so a profile
            // whose SDK version doesn't expose this flag isn't wrongly
            // excluded — CreateController remains the final authority
            // either way; this only spares the picker from offering a
            // choice guaranteed to fail.
            IsDeployable: ReadValue(true, "IsDeployable"));
    }

    /// <summary>
    /// Resolves the first candidate id that actually exists in the SDK's
    /// loaded catalog. When the catalog can't be enumerated, falls back
    /// to probing GetProfile per candidate; when even that isn't
    /// possible, returns the first candidate unverified (CreateController
    /// will then produce the authoritative error).
    /// </summary>
    public static string? TryResolveExistingProfileId(IReadOnlyList<string> candidates, ILogger logger)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var catalog = GetCatalogProfiles(logger);
        lock (Gate)
        {
            if (outcome != ProbeOutcome.Available)
            {
                return candidates[0];
            }

            if (catalog.Count > 0)
            {
                foreach (var candidate in candidates)
                {
                    // IsDeployable guard: a match here that the SDK can't
                    // actually create is worse than no match — it would
                    // return with false confidence and fail at
                    // CreateController every single time (see the
                    // keyword fallback below, which also excludes these).
                    if (catalog.Any(p => string.Equals(p.Id, candidate, StringComparison.OrdinalIgnoreCase) && p.IsDeployable))
                    {
                        return candidate;
                    }
                }

                // Keyword fallback: derive search terms from the FIRST
                // candidate ("switch-pro" → ["switch","pro"]) and pick
                // the catalog profile whose id+name contains them all.
                // Turns slug drift across HIDMaestro releases into a
                // slower lookup instead of a hard failure — with a 225-
                // profile catalog, exact spellings are the fragile part.
                var keywords = candidates[0]
                    .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Where(k => k.Length >= 2)
                    .ToArray();
                if (keywords.Length > 0)
                {
                    var match = catalog.FirstOrDefault(p =>
                    {
                        if (!p.IsDeployable)
                        {
                            return false;
                        }
                        var haystack = $"{p.Id} {p.Name}".ToLowerInvariant();
                        return keywords.All(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
                    });
                    if (match is not null)
                    {
                        logger.LogInformation(
                            "HIDMaestro dynamic: no exact candidate matched; keyword search ({Keywords}) resolved " +
                            "catalog profile '{Id}' ('{Name}').",
                            string.Join("+", keywords), match.Id, match.Name);
                        return match.Id;
                    }
                }

                logger.LogWarning(
                    "HIDMaestro dynamic: none of the candidate profile ids ({Candidates}) exist in the {Count}-profile " +
                    "catalog. Using '{First}' anyway; expect creation to fail with the catalog's own error.",
                    string.Join(", ", candidates), catalog.Count, candidates[0]);
                return candidates[0];
            }

            if (getProfile is not null && context is not null)
            {
                foreach (var candidate in candidates)
                {
                    try
                    {
                        if (getProfile.Invoke(context, [candidate]) is not null)
                        {
                            return candidate;
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(exception, "HIDMaestro dynamic: GetProfile probe for '{Candidate}' threw.", candidate);
                    }
                }
            }

            return candidates[0];
        }
    }

    // ─── Controller creation ────────────────────────────────────────────

    /// <summary>Creates a virtual controller for the given HIDMaestro catalog profile id.</summary>
    public static DynamicControllerHandle? TryCreateController(
        string profileId, ILogger logger, out string? failure)
    {
        lock (Gate)
        {
            if (!EnsureCreatableLocked(out failure))
            {
                return null;
            }

            try
            {
                object? controller;
                object? profile = null;
                if (createFromProfile is not null && getProfile is not null)
                {
                    profile = getProfile.Invoke(context, [profileId]);
                    if (profile is null)
                    {
                        throw new InvalidOperationException(
                            $"Profile '{profileId}' not found in the loaded catalog. {DescribeCatalogSampleLocked(logger)}");
                    }
                    controller = createFromProfile.Invoke(context, [profile]);
                }
                else
                {
                    controller = createFromString!.Invoke(context, [profileId]);
                }

                return WrapControllerLocked(controller, profileId, profile, logger, out failure);
            }
            catch (TargetInvocationException exception)
            {
                failure = DescribeCreationFailure(exception.InnerException ?? exception);
                logger.LogError(exception.InnerException ?? exception,
                    "HIDMaestro controller creation failed for profile {Profile}.", profileId);
                return null;
            }
            catch (Exception exception)
            {
                failure = DescribeCreationFailure(exception);
                logger.LogError(exception,
                    "HIDMaestro controller creation failed for profile {Profile}.", profileId);
                return null;
            }
        }
    }

    /// <summary>
    /// Builds a profile at runtime from the template's shape (via
    /// HMProfileBuilder + HidDescriptorBuilder) and deploys it — the
    /// Generic (DirectInput) path. HMGamepadState models two sticks, two
    /// triggers and one hat, so counts beyond those are clamped with a
    /// log line rather than emitting axes that could never move.
    /// </summary>
    public static DynamicControllerHandle? TryCreateCustomController(
        string profileId, string displayName, string productString,
        ushort vendorId, ushort productId,
        int thumbstickCount, int triggerCount, int buttonCount, int povCount,
        ILogger logger, out string? failure)
    {
        lock (Gate)
        {
            if (!EnsureCreatableLocked(out failure))
            {
                return null;
            }
            if (profileBuilderType is null || descriptorBuilderType is null)
            {
                failure = "This HIDMaestro.Core build does not expose HMProfileBuilder/HidDescriptorBuilder, " +
                          "so a custom (generic) profile can't be authored at runtime. Pick a catalog profile instead.";
                logger.LogWarning("HIDMaestro dynamic: {Failure}", failure);
                return null;
            }

            try
            {
                var missing = new List<string>();

                // ── HID descriptor: mirror SdkDemo's authoring order ──
                object descriptorBuilder = Activator.CreateInstance(descriptorBuilderType)
                    ?? throw new InvalidOperationException("HidDescriptorBuilder could not be instantiated.");
                descriptorBuilder = FluentInvoke(descriptorBuilder, "Gamepad", [], missing);

                var sticks = Math.Clamp(thumbstickCount, 0, 2);
                if (thumbstickCount > sticks)
                {
                    logger.LogInformation(
                        "HIDMaestro dynamic: generic template asked for {Requested} thumbsticks; HMGamepadState drives at most 2 — clamped.",
                        thumbstickCount);
                }
                if (sticks >= 1) { descriptorBuilder = FluentInvoke(descriptorBuilder, "AddStick", ["Left", 16], missing); }
                if (sticks >= 2) { descriptorBuilder = FluentInvoke(descriptorBuilder, "AddStick", ["Right", 16], missing); }

                var triggers = Math.Clamp(triggerCount, 0, 2);
                if (triggerCount > triggers)
                {
                    logger.LogInformation(
                        "HIDMaestro dynamic: generic template asked for {Requested} triggers; HMGamepadState drives at most 2 — clamped.",
                        triggerCount);
                }
                if (triggers >= 1) { descriptorBuilder = FluentInvoke(descriptorBuilder, "AddTrigger", ["Left", 8], missing); }
                if (triggers >= 2) { descriptorBuilder = FluentInvoke(descriptorBuilder, "AddTrigger", ["Right", 8], missing); }

                var buttons = Math.Clamp(buttonCount, 1, 128);
                descriptorBuilder = FluentInvoke(descriptorBuilder, "AddButtons", [buttons], missing);

                if (povCount >= 1)
                {
                    if (povCount > 1)
                    {
                        logger.LogInformation(
                            "HIDMaestro dynamic: generic template asked for {Requested} POV hats; HMGamepadState drives 1 — clamped.",
                            povCount);
                    }
                    descriptorBuilder = FluentInvoke(descriptorBuilder, "AddHat", [], missing);
                }

                // ── Profile: identity + descriptor, SdkDemo order ──
                object profileBuilder = Activator.CreateInstance(profileBuilderType)
                    ?? throw new InvalidOperationException("HMProfileBuilder could not be instantiated.");
                profileBuilder = FluentInvoke(profileBuilder, "Id", [profileId], missing);
                profileBuilder = FluentInvoke(profileBuilder, "Name", [displayName], missing);
                profileBuilder = FluentInvoke(profileBuilder, "Vendor", ["GameFlow"], missing);
                profileBuilder = FluentInvoke(profileBuilder, "Vid", [(int)vendorId], missing);
                profileBuilder = FluentInvoke(profileBuilder, "Pid", [(int)productId], missing);
                profileBuilder = FluentInvoke(profileBuilder, "ProductString",
                    [string.IsNullOrWhiteSpace(productString) ? displayName : productString], missing);
                profileBuilder = FluentInvoke(profileBuilder, "ManufacturerString", ["GameFlow"], missing);
                profileBuilder = FluentInvoke(profileBuilder, "Type", ["gamepad"], missing);
                profileBuilder = FluentInvoke(profileBuilder, "Connection", ["usb"], missing);

                if (HasMethod(profileBuilderType, "FromDescriptorBuilder", 1))
                {
                    profileBuilder = FluentInvoke(profileBuilder, "FromDescriptorBuilder", [descriptorBuilder], missing);
                }
                else
                {
                    // Descriptor bytes alone are not enough — the profile
                    // also needs the matching InputReportSize, which only
                    // FromDescriptorBuilder derives for us. Guessing a
                    // size deploys a device whose reports never parse, so
                    // fail loudly instead.
                    failure = "HMProfileBuilder.FromDescriptorBuilder is missing on this SDK build; " +
                              "a runtime-built generic profile can't be authored safely. Pick a catalog profile instead.";
                    logger.LogWarning("HIDMaestro dynamic: {Failure}", failure);
                    return null;
                }

                var buildMethod = profileBuilder.GetType().GetMethod("Build", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                if (buildMethod is null)
                {
                    missing.Add("Build");
                }

                if (missing.Count > 0)
                {
                    failure = "HIDMaestro builder API mismatch — missing member(s): " + string.Join(", ", missing);
                    logger.LogWarning("HIDMaestro dynamic: {Failure}", failure);
                    return null;
                }

                var profile = buildMethod!.Invoke(profileBuilder, null)
                    ?? throw new InvalidOperationException("HMProfileBuilder.Build() returned null.");

                var creator = createFromProfile
                    ?? throw new InvalidOperationException(
                        "HMContext has no CreateController(HMProfile) overload, so a runtime-built profile can't be deployed.");
                var controller = creator.Invoke(context, [profile]);

                var handle = WrapControllerLocked(controller, profileId, profile, logger, out failure);
                return handle is null
                    ? null
                    : handle with { HardwareSignature = (vendorId, productId), ProfileName = displayName };
            }
            catch (TargetInvocationException exception)
            {
                failure = DescribeCreationFailure(exception.InnerException ?? exception);
                logger.LogError(exception.InnerException ?? exception,
                    "HIDMaestro custom controller creation failed for {ProfileId}.", profileId);
                return null;
            }
            catch (Exception exception)
            {
                failure = DescribeCreationFailure(exception);
                logger.LogError(exception,
                    "HIDMaestro custom controller creation failed for {ProfileId}.", profileId);
                return null;
            }
        }
    }

    /// <summary>Shared precondition checks. Callers must hold <see cref="Gate"/>.</summary>
    private static bool EnsureCreatableLocked(out string? failure)
    {
        if (outcome != ProbeOutcome.Available || context is null
            || stateType is null || buttonEnumType is null || hatEnumType is null || submitState is null
            || (createFromProfile is null && createFromString is null))
        {
            failure = status;
            return false;
        }
        if (!IsProcessElevated)
        {
            failure = "Restart GameFlow as Administrator to create HIDMaestro controllers.";
            return false;
        }
        return driverInitialization!.EnsureInstalled(out failure);
    }

    /// <summary>
    /// Wraps a freshly created controller object into the all-or-nothing
    /// dynamic driver and reads the profile's real identity for input
    /// hiding. Callers must hold <see cref="Gate"/>.
    /// </summary>
    private static DynamicControllerHandle? WrapControllerLocked(
        object? controller, string profileId, object? profile, ILogger logger, out string? failure)
    {
        if (controller is null)
        {
            failure = $"CreateController('{profileId}') returned null.";
            return null;
        }

        DynamicHidMaestroController dynamicController;
        try
        {
            dynamicController = new DynamicHidMaestroController(
                controller, context, stateType!, buttonEnumType!, hatEnumType!, submitState!, logger);
        }
        catch (Exception exception)
        {
            // Binding failed AFTER the OS device was created — remove it
            // before reporting failure, or every failed attempt leaves a
            // ghost controller behind until process exit.
            DynamicHidMaestroController.TryRemoveController(controller, context, logger);
            failure = DescribeCreationFailure(exception);
            logger.LogError(exception, "HIDMaestro state binding failed for profile {Profile}; device removed.", profileId);
            return null;
        }

        var name = profileId;
        (ushort Vid, ushort Pid)? signature = null;
        if (profile is not null && ReadCatalogProfile(profile) is { } described)
        {
            name = described.Name;
            if (described.VendorId != 0 || described.ProductId != 0)
            {
                signature = (described.VendorId, described.ProductId);
            }
        }
        else if (catalogCache?.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) is { } fromCatalog)
        {
            name = fromCatalog.Name;
            if (fromCatalog.VendorId != 0 || fromCatalog.ProductId != 0)
            {
                signature = (fromCatalog.VendorId, fromCatalog.ProductId);
            }
        }

        failure = null;
        return new DynamicControllerHandle(dynamicController, profileId, name, signature);
    }

    /// <summary>Callers must hold <see cref="Gate"/>.</summary>
    private static string DescribeCatalogSampleLocked(ILogger logger)
    {
        var catalog = catalogCache ?? EnumerateCatalogLocked(logger);
        catalogCache ??= catalog;
        return catalog.Count == 0
            ? "The catalog could not be enumerated for diagnostics."
            : $"Catalog has {catalog.Count} profile(s); a sample: " +
              string.Join(", ", catalog.Take(30).Select(p => p.Id));
    }

    /// <summary>
    /// Appends the elevation hint when it is very likely the actual
    /// cause — access-denied-shaped failures in a non-elevated process.
    /// </summary>
    private static string DescribeCreationFailure(Exception exception)
    {
        var message = exception.Message;
        if (OperatingSystem.IsWindows() && !IsProcessElevated)
        {
            message += " — GameFlow is not running elevated; HIDMaestro needs administrator rights " +
                       "to create virtual devices. Restart GameFlow as Administrator.";
        }
        return message;
    }

    // ─── Fluent reflection helpers ──────────────────────────────────────

    private static bool HasMethod(Type type, string name, int parameterCount) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == name && m.GetParameters().Length == parameterCount);

    /// <summary>
    /// Invokes a fluent builder method, coercing arguments to the real
    /// parameter types (int vs ushort etc.) and following the returned
    /// instance when the builder returns one. Missing methods are
    /// recorded in <paramref name="missing"/> so the caller can fail
    /// with the complete list instead of the first hole.
    /// </summary>
    private static object FluentInvoke(object target, string methodName, object?[] args, List<string> missing)
    {
        var type = target.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            ?? type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName
                    && m.GetParameters().Length > args.Length
                    && m.GetParameters().Skip(args.Length).All(p => p.HasDefaultValue))
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();

        if (method is null)
        {
            missing.Add($"{methodName}({args.Length} arg{(args.Length == 1 ? string.Empty : "s")})");
            return target;
        }

        var parameters = method.GetParameters();
        var coerced = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            coerced[i] = i < args.Length
                ? CoerceArgument(args[i], parameters[i].ParameterType)
                : parameters[i].DefaultValue;
        }

        var result = method.Invoke(target, coerced);
        return result ?? target;
    }

    private static object? CoerceArgument(object? value, Type targetType)
    {
        if (value is null || targetType.IsInstanceOfType(value))
        {
            return value;
        }
        if (value is IConvertible && (targetType.IsPrimitive || targetType == typeof(decimal)))
        {
            return Convert.ChangeType(value, targetType);
        }
        return value;
    }
}

/// <summary>
/// A live HIDMaestro virtual controller driven through reflection. Every
/// numeric axis, the button flags, and the hat are bound at construction
/// time and are ALL mandatory — if the real SDK uses different field
/// names than expected, construction throws immediately with the full
/// list of what bound and what didn't, instead of quietly running with
/// dead stick axes. Field values are coerced to the target field's actual
/// numeric type (float vs double) so a type mismatch there can't throw
/// on every single frame.
/// </summary>
internal sealed class DynamicHidMaestroController : IDisposable
{
    private readonly object controller;
    private readonly object? context;
    private readonly MethodInfo submitState;
    private readonly ILogger logger;
    private readonly object boxedState;
    private readonly Action submitFrame;

    // v1.3.9+: HMGamepadState has no LeftStickX/RightStickX/LeftTrigger/etc
    // properties -- analog input goes through a single Axes dictionary,
    // keyed by HID usage (HMAxis) and resolved PER PROFILE (a wheel's
    // "stick" is a different HID usage than a gamepad's). See Bind()
    // in the constructor for the discovery step and the SDK's own
    // HMGamepadStateHelpers.StandardAxes, which this mirrors.
    private readonly Setter setAxes, setButtons, setHat;

    /// <summary>
    /// Battery members, when the deployed SDK has them.
    /// </summary>
    /// <remarks>
    /// Optional, unlike axes/buttons/hat: those three are bound
    /// all-or-nothing so a controller never silently runs with dead
    /// sticks, but an SDK build without battery fields should still
    /// produce a working pad. Null here simply means this build cannot
    /// report charge, which is the state every build was in before.
    /// </remarks>
    private readonly Action<byte>? setBatteryLevel;
    private readonly Action<bool>? setBatteryCharging, setBatteryFull;

    /// <summary>
    /// Calibrated motion members (g / deg/s), when the deployed SDK has
    /// them. Optional for the same reason battery is: an older build
    /// without them should still produce a working pad, just one that
    /// reports no motion.
    /// </summary>
    private readonly Action<float>? setAccelGX, setAccelGY, setAccelGZ, setGyroDpsX, setGyroDpsY, setGyroDpsZ;

    /// <summary>Two-finger touch surface members, when present.</summary>
    private readonly Action<bool>? setTouch0Active;
    private readonly Action<ushort>? setTouch0X, setTouch0Y;
    private readonly Action<byte>? setTouch0Id;
    private readonly Action<bool>? setTouch1Active;
    private readonly Action<ushort>? setTouch1X, setTouch1Y;
    private readonly Action<byte>? setTouch1Id;

    /// <summary>
    /// The OS device-instance id of the pad this bridge created, when the
    /// SDK reports one.
    /// </summary>
    /// <remarks>
    /// Read after construction as PnP settles and handed to
    /// <see cref="VirtualDeviceIdentity"/>, which is what stops the pad
    /// this process just created from coming back through SDL's
    /// enumeration and being offered as an input source. Without it the
    /// only remaining signal is a path substring, and the interface path
    /// SDL reports for a HIDMaestro pad does not contain one — the
    /// "hidmaestro" name lives on a sibling software node, not on the HID
    /// interface.
    /// </remarks>
    public string? InstanceId { get; private set; }

    /// <summary>
    /// How long after creation to keep asking the SDK for the new pad's
    /// instance id.
    /// </summary>
    /// <remarks>
    /// The property is empty at the moment <c>CreateController</c>
    /// returns: the device still has to be enumerated by PnP, which the
    /// SDK documents as taking roughly 200 ms for a warm create. Reading
    /// it once in the constructor therefore always came back null, and
    /// the claim never happened.
    /// </remarks>
    private static readonly TimeSpan IdentitySettleWindow = TimeSpan.FromSeconds(10);

    private DateTime identityDeadlineUtc;
    private bool identityGaveUp;

    public bool HasMotionFields { get; }

    /// <summary>True when this SDK exposes the touch-surface members.</summary>
    public bool HasTouchpadFields { get; }
    private readonly Type axesDictType;
    private readonly object? axisLeftX, axisLeftY, axisRightX, axisRightY, axisLeftTrigger, axisRightTrigger;
    private readonly bool hasAnyAxis;
    private readonly Action<float>? writeLeftX, writeLeftY, writeRightX, writeRightY, writeLeftTrigger, writeRightTrigger;
    // Allocated once, reused every frame (SDK's own guidance: "Allocate
    // once and reuse" -- the boxed struct's Axes field holds a
    // REFERENCE to this, so mutating its values in Submit() never needs
    // to touch the struct again). Non-generic IDictionary avoids needing
    // MakeGenericType/generic MethodInfo gymnastics for a type (HMAxis)
    // this assembly has no compile-time knowledge of.
    private readonly System.Collections.IDictionary axesInstance;
    private readonly Type buttonEnumType;
    private readonly Type hatEnumType;
    private readonly Dictionary<string, ulong> buttonValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> missingButtons = new(StringComparer.OrdinalIgnoreCase);
    private EventInfo? outputReceivedEvent;
    private EventInfo? outputDecodedEvent;
    private Delegate? outputReceivedHandler;
    private Delegate? outputDecodedHandler;
    private int outputWarningLogged;
    private bool disposed;
    private int consecutiveSubmitFailures;
    private const int FailureGiveUpThreshold = 300; // ~1-3s at typical tick rates

    /// <summary>Last button mask and its box; see <see cref="BoxButtonMask"/>.</summary>
    private ulong lastButtonMask;
    private object? lastButtonBox;

    /// <summary>Boxed hat values by direction name; see <see cref="BoxHat"/>.</summary>
    private readonly Dictionary<string, object> hatBoxes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// False once submits have failed enough consecutive times in a row
    /// that continuing to retry is pointless (a persistent reflection
    /// mismatch won't fix itself frame-to-frame). The owning sink should
    /// stop calling <see cref="Submit"/> once this goes false and treat
    /// HIDMaestro as unavailable for the rest of this configuration.
    /// </summary>
    public bool IsHealthy => consecutiveSubmitFailures < FailureGiveUpThreshold;

    /// <summary>Game-requested motor state, including explicit zero stops.</summary>
    public event Action<double, double>? RumbleReceived;

    /// <summary>A bound field/property setter that also knows the target's real numeric type, for safe reflection coercion.</summary>
    private readonly record struct Setter(MemberInfo Member, Type TargetType, Action<object, object?> Apply);

    public DynamicHidMaestroController(
        object controller, object? context, Type stateType, Type buttonEnumType, Type hatEnumType,
        MethodInfo submitState, ILogger logger)
    {
        this.controller = controller;
        this.context = context;
        this.submitState = submitState;
        this.logger = logger;
        this.buttonEnumType = buttonEnumType;
        this.hatEnumType = hatEnumType;

        boxedState = Activator.CreateInstance(stateType)
            ?? throw new InvalidOperationException("HMGamepadState could not be instantiated.");
        // Reflection Invoke copies an in/ref struct back into its argument array,
        // replacing the box. That leaves later field writes targeting an old box.
        // A compiled call reads the same state box on every frame and avoids the
        // per-frame reflection/copy-back allocation.
        var state = stateType.IsValueType
            ? Expression.Unbox(Expression.Constant(boxedState, typeof(object)), stateType)
            : Expression.Convert(Expression.Constant(boxedState, typeof(object)), stateType);
        var call = Expression.Call(Expression.Constant(controller), submitState, state);
        submitFrame = Expression.Lambda<Action>(Expression.Block(call, Expression.Empty())).Compile();

        Setter? Bind(string name)
        {
            var field = stateType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field is not null)
            {
                return new Setter(field, field.FieldType, field.SetValue);
            }
            var property = stateType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is not null && property.CanWrite)
            {
                return new Setter(property, property.PropertyType, property.SetValue);
            }
            return null;
        }

        Action<T>? BindValue<T>(string name)
        {
            var member = Bind(name);
            if (member is null) return null;
            var value = Expression.Parameter(typeof(T), "value");
            var target = stateType.IsValueType
                ? Expression.Unbox(Expression.Constant(boxedState, typeof(object)), stateType)
                : Expression.Convert(Expression.Constant(boxedState, typeof(object)), stateType);
            var assignment = Expression.Assign(Expression.MakeMemberAccess(target, member.Value.Member),
                Expression.Convert(value, member.Value.TargetType));
            return Expression.Lambda<Action<T>>(Expression.Block(assignment, Expression.Empty()), value).Compile();
        }

        var axes    = Bind("Axes");
        var buttons = Bind("Buttons");
        var hat     = Bind("Hat");

        var missing = new List<string>();
        if (axes    is null) missing.Add("Axes");
        if (buttons is null) missing.Add("Buttons");
        if (hat     is null) missing.Add("Hat");

        if (missing.Count > 0)
        {
            var available = stateType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .Concat(stateType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite).Select(p => p.Name))
                .Distinct();
            throw new InvalidOperationException(
                $"HMGamepadState is missing expected member(s): {string.Join(", ", missing)}. " +
                $"Binding is all-or-nothing so a real controller never silently runs with dead axes. " +
                $"Writable members actually found on {stateType.Name}: {string.Join(", ", available)}");
        }

        setAxes = axes!.Value; setButtons = buttons!.Value; setHat = hat!.Value;

        // Best-effort: present on HMGamepadState in current SDKs, absent
        // in older ones. See the field declarations for why these are not
        // part of the all-or-nothing bind above.
        setBatteryLevel = BindValue<byte>("BatteryLevel");
        setBatteryCharging = BindValue<bool>("BatteryCharging");
        setBatteryFull = BindValue<bool>("BatteryFull");
        if (setBatteryLevel is null)
        {
            logger.LogDebug(
                "HIDMaestro dynamic: HMGamepadState has no BatteryLevel member; " +
                "the emitted pad will report whatever this SDK defaults to.");
        }

        // Motion, in the SDK's CALIBRATED units (g and deg/s) rather than
        // the raw Sony-firmware shorts beside them. GameFlow's snapshot
        // carries SDL's values, and the SDK documents these fields as
        // being in SDL's own sensor frame — "consumers that read motion
        // FROM SDL submit those values verbatim" — so this path needs a
        // unit conversion and no axis gymnastics.
        setAccelGX = BindValue<float>("AccelGX");
        setAccelGY = BindValue<float>("AccelGY");
        setAccelGZ = BindValue<float>("AccelGZ");
        setGyroDpsX = BindValue<float>("GyroDpsX");
        setGyroDpsY = BindValue<float>("GyroDpsY");
        setGyroDpsZ = BindValue<float>("GyroDpsZ");
        HasMotionFields = setAccelGX is not null && setGyroDpsX is not null;

        // Touch surface, two fingers, in the Sony native ranges the SDK
        // documents (X 0..1919, Y 0..1079).
        setTouch0Active = BindValue<bool>("TouchpadFinger0Active");
        setTouch0X = BindValue<ushort>("TouchpadFinger0X");
        setTouch0Y = BindValue<ushort>("TouchpadFinger0Y");
        setTouch0Id = BindValue<byte>("TouchpadFinger0Id");
        setTouch1Active = BindValue<bool>("TouchpadFinger1Active");
        setTouch1X = BindValue<ushort>("TouchpadFinger1X");
        setTouch1Y = BindValue<ushort>("TouchpadFinger1Y");
        setTouch1Id = BindValue<byte>("TouchpadFinger1Id");
        HasTouchpadFields = setTouch0Active is not null && setTouch0X is not null && setTouch0Y is not null;

        // Identity is claimed lazily, not here — see EnsureIdentityClaimed.
        identityDeadlineUtc = DateTime.UtcNow + IdentitySettleWindow;
        EnsureIdentityClaimed();

        // Information, not Debug: whether these optional fields bound at all
        // is the first question asked when a virtual pad reports the wrong
        // battery or no motion, and needing to raise the log level to find
        // out means the answer is missing from every report that arrives.
        {
            logger.LogInformation(
                "HIDMaestro dynamic: optional state fields — battery={Battery}, motion={Motion}, touchpad={Touchpad}.",
                setBatteryLevel is not null, HasMotionFields, HasTouchpadFields);
        }

        axesDictType = setAxes.TargetType;

        // Discover WHICH HID usage each logical slot (left stick X/Y,
        // right stick X/Y, the two triggers) maps to for THIS deployed
        // profile, via HMController.Profile.Sticks / .Triggers -- the
        // SDK's own documented discovery surface, and the same data
        // HMGamepadStateHelpers.StandardAxes uses internally. Resolved
        // ONCE here (a profile's axis layout never changes for the life
        // of a controller instance), so Submit() below does zero
        // reflection to figure out WHERE an axis goes -- only a
        // dictionary write to a key resolved at bind time.
        var profileProperty = controller.GetType().GetProperty("Profile", BindingFlags.Public | BindingFlags.Instance);
        var profile = profileProperty?.GetValue(controller);
        if (profile is null)
        {
            throw new InvalidOperationException(
                "HMController.Profile could not be read via reflection -- cannot discover which HID axis " +
                "carries the left/right stick or triggers for this profile. Analog input would be silently dead.");
        }

        var sticksProperty = profile.GetType().GetProperty("Sticks", BindingFlags.Public | BindingFlags.Instance);
        var triggersProperty = profile.GetType().GetProperty("Triggers", BindingFlags.Public | BindingFlags.Instance);
        var sticks = (sticksProperty?.GetValue(profile) as System.Collections.IEnumerable)?.Cast<object>().ToList()
            ?? [];
        var triggers = (triggersProperty?.GetValue(profile) as System.Collections.IEnumerable)?.Cast<object>().ToList()
            ?? [];

        object? AxisOrNull(object? record, string memberName)
        {
            if (record is null) { return null; }
            var value = record.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(record);
            // HMAxis.None (numeric 0) means "this profile doesn't expose
            // this axis" (e.g. a 1D stick has no YAxis) -- treated the
            // same as not being able to resolve it at all: skip writing.
            return value is not null && Convert.ToInt64(value) != 0 ? value : null;
        }

        object AxisByName(string name)
        {
            var axisType = axesDictType.GetGenericArguments()[0];
            return Enum.Parse(axisType, name);
        }

        var stick0 = sticks.Count > 0 ? sticks[0] : null;
        var stick1 = sticks.Count > 1 ? sticks[1] : null;
        var trigger0 = triggers.Count > 0 ? triggers[0] : null;
        var trigger1 = triggers.Count > 1 ? triggers[1] : null;

        axisLeftX  = AxisOrNull(stick0, "XAxis");
        axisLeftY  = AxisOrNull(stick0, "YAxis");
        axisRightX = AxisOrNull(stick1, "XAxis");
        axisRightY = AxisOrNull(stick1, "YAxis");
        axisLeftTrigger  = AxisOrNull(trigger0, "Axis");
        axisRightTrigger = AxisOrNull(trigger1, "Axis");

        // Profiles backed by opaque vendor reports (including Valve pads)
        // intentionally expose no simple layout. HIDMaestro's own
        // StandardAxes helper falls back to these canonical usages, and
        // SubmitState resolves them through the profile's report codec.
        if (sticks.Count == 0)
        {
            axisLeftX = AxisByName("X");
            axisLeftY = AxisByName("Y");
            axisRightX = AxisByName("Rx");
            axisRightY = AxisByName("Ry");
        }
        if (triggers.Count == 0)
        {
            axisLeftTrigger = AxisByName("Z");
            axisRightTrigger = AxisByName("Rz");
        }
        hasAnyAxis = axisLeftX is not null || axisLeftY is not null || axisRightX is not null
            || axisRightY is not null || axisLeftTrigger is not null || axisRightTrigger is not null;

        if (!hasAnyAxis)
        {
            // Not fatal -- some profiles genuinely have zero of the
            // "standard six" (a hat-only macropad, e.g.) -- but for
            // anything claiming to be a gamepad this means dead sticks,
            // so it's worth a loud warning rather than silent failure.
            logger.LogWarning(
                "HIDMaestro dynamic: profile '{Profile}' exposes none of the standard 6 axes (left/right " +
                "stick X/Y, two triggers) via Profile.Sticks/Triggers ({StickCount} stick(s), {TriggerCount} " +
                "trigger(s) declared) -- analog input will do nothing for this controller.",
                (profile.GetType().GetProperty("Id")?.GetValue(profile)) ?? profile, sticks.Count, triggers.Count);
        }

        foreach (var name in Enum.GetNames(buttonEnumType))
        {
            buttonValues[name] = Convert.ToUInt64(Enum.Parse(buttonEnumType, name));
        }

        // Alias table: the sink speaks XInput-ish logical names, but the
        // SDK enum may spell some differently (only A/B/X/Y, the
        // bumpers, Guide and Share are verified from the SDK's own
        // example). If the canonical spelling is absent, adopt the first
        // synonym the enum actually has — so e.g. Back still maps when
        // the enum calls it View or Select.
        void Alias(string canonical, params string[] synonyms)
        {
            if (buttonValues.ContainsKey(canonical))
            {
                return;
            }
            foreach (var synonym in synonyms)
            {
                if (buttonValues.TryGetValue(synonym, out var value))
                {
                    buttonValues[canonical] = value;
                    logger.LogInformation(
                        "HIDMaestro dynamic: HMButton spells '{Canonical}' as '{Synonym}' — aliased.",
                        canonical, synonym);
                    return;
                }
            }
        }

        Alias("Back", "View", "Select", "Minus");
        Alias("Start", "Menu", "Options", "Plus");
        Alias("LeftThumb", "LeftStick", "LeftStickClick", "L3", "LS", "ThumbLeft", "LeftThumbstick");
        Alias("RightThumb", "RightStick", "RightStickClick", "R3", "RS", "ThumbRight", "RightThumbstick");
        Alias("Guide", "Home", "Xbox", "PS", "System");
        Alias("Touchpad", "TouchpadClick", "TouchPad", "Pad");
        Alias("LeftPaddle", "LeftPaddle1", "Paddle3", "P3", "LeftBackButton");
        Alias("RightPaddle", "RightPaddle1", "Paddle1", "P1", "RightBackButton");
        Alias("LeftPaddle2", "LeftFn", "Paddle4", "P4");
        Alias("RightPaddle2", "RightFn", "Paddle2", "P2");
        Alias("Misc1", "Mute", "MicMute", "Capture", "Share");

        axesInstance = (System.Collections.IDictionary)(Activator.CreateInstance(axesDictType)
            ?? throw new InvalidOperationException($"Could not instantiate {axesDictType.Name} for HMGamepadState.Axes."));
        setAxes.Apply(boxedState, axesInstance);

        // Typed dictionary writes avoid boxing each float through IDictionary.
        Action<float>? BindAxis(object? axis)
        {
            if (axis is null) return null;
            var value = Expression.Parameter(typeof(float), "value");
            var item = Expression.Property(Expression.Constant(axesInstance, axesDictType), "Item",
                Expression.Constant(axis, axis.GetType()));
            return Expression.Lambda<Action<float>>(
                Expression.Block(Expression.Assign(item, value), Expression.Empty()), value).Compile();
        }
        writeLeftX = BindAxis(axisLeftX);
        writeLeftY = BindAxis(axisLeftY);
        writeRightX = BindAxis(axisRightX);
        writeRightY = BindAxis(axisRightY);
        writeLeftTrigger = BindAxis(axisLeftTrigger);
        writeRightTrigger = BindAxis(axisRightTrigger);

        SubscribeOutputEvents();
    }

    /// <summary>
    /// Binds HIDMaestro's output callbacks without a compile-time SDK
    /// reference. OutputReceived covers XInput; OutputDecoded covers the
    /// profile-authored Sony and Switch motor fields.
    /// </summary>
    private void SubscribeOutputEvents()
    {
        try
        {
            var type = controller.GetType();
            outputReceivedEvent = type.GetEvent("OutputReceived", BindingFlags.Public | BindingFlags.Instance);
            if (outputReceivedEvent?.EventHandlerType is { } rawHandlerType)
            {
                outputReceivedHandler = CreateEventBridge(rawHandlerType, OnOutputReceived);
                outputReceivedEvent.AddEventHandler(controller, outputReceivedHandler);
            }

            outputDecodedEvent = type.GetEvent("OutputDecoded", BindingFlags.Public | BindingFlags.Instance);
            if (outputDecodedEvent?.EventHandlerType is { } decodedHandlerType)
            {
                outputDecodedHandler = CreateEventBridge(decodedHandlerType, OnOutputDecoded);
                outputDecodedEvent.AddEventHandler(controller, outputDecodedHandler);
            }

            if (outputReceivedHandler is null && outputDecodedHandler is null)
            {
                logger.LogWarning(
                    "HIDMaestro dynamic: controller exposes neither OutputReceived nor OutputDecoded; game rumble cannot return to the physical pad.");
            }
        }
        catch (Exception exception)
        {
            // Output feedback is optional; losing it must not destroy an
            // otherwise healthy virtual input device.
            logger.LogWarning(exception,
                "HIDMaestro dynamic: could not bind output feedback; virtual input remains active but game rumble cannot return.");
            UnsubscribeOutputEvents();
        }
    }

    private static Delegate CreateEventBridge(
        Type handlerType,
        Action<object?, object?> callback)
    {
        var invoke = handlerType.GetMethod("Invoke")
            ?? throw new InvalidOperationException($"Event delegate {handlerType.Name} has no Invoke method.");
        var delegateParameters = invoke.GetParameters();
        if (delegateParameters.Length == 0)
        {
            throw new InvalidOperationException($"Event delegate {handlerType.Name} carries no callback payload.");
        }

        var parameters = delegateParameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        Expression sender = parameters.Length > 1
            ? Expression.Convert(parameters[0], typeof(object))
            : Expression.Constant(null, typeof(object));
        var payload = Expression.Convert(parameters[^1], typeof(object));
        var body = Expression.Call(
            Expression.Constant(callback),
            typeof(Action<object, object>).GetMethod(nameof(Action<object, object>.Invoke))!,
            sender,
            payload);

        return Expression.Lambda(handlerType, body, parameters).Compile();
    }

    private void OnOutputReceived(object? _, object? packet)
    {
        if (disposed || packet is null)
        {
            return;
        }

        try
        {
            var source = ReadMember(packet, "Source");
            var sourceValue = source is null ? -1 : Convert.ToInt32(source);

            // One line the first time a host writes anything to this pad.
            // "Rumble does not work" has half a dozen possible stopping
            // points between the game and the motor, and this is the
            // first: it says whether output is reaching GameFlow at all,
            // and in which form.
            var readBytes = TryReadBytes(ReadMember(packet, "Data"), out var bytes);

            // One line per source, with the payload. A packet whose length
            // the decoder does not recognise is otherwise indistinguishable
            // from no packet arriving at all, and the two have completely
            // different causes.
            if (firstOutputLogged.Add(sourceValue))
            {
                logger.LogInformation(
                    "HIDMaestro dynamic: first output packet from the host — source={Source} ({SourceName}), " +
                    "reportId={ReportId}, {Length} byte(s): {Payload}",
                    sourceValue,
                    sourceValue switch
                    {
                        0 => "HidOutput", 1 => "HidFeature", 2 => "XInput", 3 => "HidFeatureRead",
                        _ => "unknown",
                    },
                    ReadMember(packet, "ReportId"),
                    readBytes ? bytes.Length : -1,
                    readBytes ? Convert.ToHexString(bytes.Span) : "<unreadable>");
            }

            if (sourceValue != 2) // HMOutputSource.XInput
            {
                return;
            }

            if (!readBytes)
            {
                return;
            }

            if (HidMaestroRumbleDecoder.TryDecodeXInput(bytes.Span, out var low, out var high))
            {
                if (!xinputRumbleLogged)
                {
                    xinputRumbleLogged = true;
                    logger.LogInformation(
                        "HIDMaestro dynamic: first XInput rumble decoded — low={Low:F2} high={High:F2}.",
                        low, high);
                }

                RumbleReceived?.Invoke(low, high);
            }
            else if (unrecognisedXInputShapes.Add(bytes.Length))
            {
                // Not necessarily a dropped rumble, and saying so was
                // actively misleading. XUSB carries more than vibration on
                // this channel — an LED/index assignment arrives the moment
                // Windows binds the pad, before any game is running — and
                // the shapes the decoder knows are the two HIDMaestro
                // documents. An unknown shape here is an unknown shape,
                // nothing more; recognised packets on the same pad continue
                // to be decoded and delivered.
                //
                // Logged once per distinct length rather than once per pad,
                // so a shape that only ever appears while a game is actually
                // vibrating is still visible against the startup chatter.
                logger.LogInformation(
                    "HIDMaestro dynamic: ignored an XInput output packet in a shape the vibration " +
                    "decoder does not know — {Length} byte(s): {Payload}. This is expected for the " +
                    "LED/index packets Windows sends when a pad is bound; it only indicates a problem " +
                    "if it coincides with rumble being requested and not felt.",
                    bytes.Length, Convert.ToHexString(bytes.Span));
            }
        }
        catch (Exception exception)
        {
            LogOutputWarningOnce(exception);
        }
    }

    /// <summary>Output sources already reported once; see OnOutputReceived.</summary>
    private readonly HashSet<int> firstOutputLogged = [];

    private bool decodedRumbleLogged;

    private bool xinputRumbleLogged;

    /// <summary>XInput payload lengths already reported as unrecognised.</summary>
    private readonly HashSet<int> unrecognisedXInputShapes = [];

    /// <summary>Declined reports described so far; see OnOutputDecoded.</summary>
    private int declinedReportsLogged;

    private const int MaxDeclinedReportsLogged = 6;

    /// <summary>Renders a decoded field value compactly for the log.</summary>
    private static string Describe(object? value) => value switch
    {
        null => "null",
        byte b => "0x" + b.ToString("X2"),
        ReadOnlyMemory<byte> m => "0x" + Convert.ToHexString(m.Span),
        byte[] a => "0x" + Convert.ToHexString(a),
        _ => value.ToString() ?? "?",
    };

    private void OnOutputDecoded(object? _, object? eventArgs)
    {
        if (disposed || eventArgs is null)
        {
            return;
        }

        try
        {
            // Older HIDMaestro builds may not expose CrcValid; absence is
            // treated as the historical behavior. When the member exists,
            // never actuate a physical motor from a corrupt Bluetooth frame.
            if (ReadMember(eventArgs, "CrcValid") is bool crcValid && !crcValid)
            {
                return;
            }

            if (ReadMember(eventArgs, "Fields") is not IReadOnlyDictionary<string, object> fields)
            {
                return;
            }

            if (HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high))
            {
                if (!decodedRumbleLogged)
                {
                    decodedRumbleLogged = true;
                    logger.LogInformation(
                        "HIDMaestro dynamic: first decoded rumble from the host — low={Low:F2} high={High:F2}.",
                        low, high);
                }

                RumbleReceived?.Invoke(low, high);
            }
            else if (declinedReportsLogged < MaxDeclinedReportsLogged)
            {
                // Reached the decoder and it declined. Logs the VALUES, not
                // just the field names: the decision turns on the Sony
                // validity flags, so the flags and the motor bytes together
                // are the only way to tell a correct rejection (an LED-only
                // packet, whose zero motors are padding) from a wrong one
                // (a genuine rumble request whose flags we misread).
                //
                // Several reports rather than one, because the first report
                // a host sends is almost always LED or configuration — it
                // would otherwise consume the single log slot and the actual
                // rumble packet would never be described.
                declinedReportsLogged++;
                logger.LogInformation(
                    "HIDMaestro dynamic: decoded output report {Index}/{Max} carried no rumble the validity " +
                    "flags authorise. Fields: {Fields}",
                    declinedReportsLogged,
                    MaxDeclinedReportsLogged,
                    string.Join(", ", fields.Select(f => $"{f.Key}={Describe(f.Value)}")));
            }
        }
        catch (Exception exception)
        {
            LogOutputWarningOnce(exception);
        }
    }

    private static object? ReadMember(object instance, string name)
    {
        var type = instance.GetType();
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
            ?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    }

    private static bool TryReadBytes(object? value, out ReadOnlyMemory<byte> bytes)
    {
        switch (value)
        {
            case ReadOnlyMemory<byte> readOnlyMemory:
                bytes = readOnlyMemory;
                return true;
            case Memory<byte> memory:
                bytes = memory;
                return true;
            case byte[] array:
                bytes = array;
                return true;
            default:
                bytes = default;
                return false;
        }
    }

    private void LogOutputWarningOnce(Exception exception)
    {
        if (Interlocked.Exchange(ref outputWarningLogged, 1) == 0)
        {
            logger.LogWarning(exception,
                "HIDMaestro dynamic: an output-feedback packet could not be decoded; later packets will still be attempted.");
        }
    }

    private void UnsubscribeOutputEvents()
    {
        try
        {
            if (outputReceivedEvent is not null && outputReceivedHandler is not null)
            {
                outputReceivedEvent.RemoveEventHandler(controller, outputReceivedHandler);
            }
            if (outputDecodedEvent is not null && outputDecodedHandler is not null)
            {
                outputDecodedEvent.RemoveEventHandler(controller, outputDecodedHandler);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "HIDMaestro dynamic: output callback detach failed during teardown.");
        }
        finally
        {
            outputReceivedHandler = null;
            outputDecodedHandler = null;
            outputReceivedEvent = null;
            outputDecodedEvent = null;
        }
    }

    /// <summary>
    /// One frame of motion, already converted to the SDK's units.
    /// <see cref="Present"/> is false on a pad with no sensor, which is
    /// what keeps "held perfectly still" distinguishable from "no gyro
    /// here" — writing zeroes for the latter would claim a reading the
    /// hardware never produced.
    /// </summary>
    /// <param name="Present">False on a pad with no motion sensor; nothing is written.</param>
    /// <param name="AccelG">Acceleration in g, SDL sensor frame.</param>
    /// <param name="GyroDps">Angular velocity in degrees per second, same frame.</param>
    public readonly record struct MotionSubmission(
        bool Present,
        (float X, float Y, float Z) AccelG,
        (float X, float Y, float Z) GyroDps)
    {
        public static readonly MotionSubmission None = new(false, default, default);
    }

    /// <summary>
    /// One frame of the touch surface, in the Sony native ranges
    /// (X 0..1919, Y 0..1079) the SDK documents.
    /// </summary>
    public readonly record struct TouchSubmission(
        bool Finger0Down, ushort Finger0X, ushort Finger0Y, byte Finger0Id,
        bool Finger1Down, ushort Finger1X, ushort Finger1Y, byte Finger1Id)
    {
        public static readonly TouchSubmission None = default;
    }

    private void WriteMotion(in MotionSubmission motion)
    {
        if (!HasMotionFields)
        {
            return;
        }

        // The SDK state is reused. Clear the previous sensor values when the
        // source disconnects or is replaced by a source without motion.
        var current = motion.Present ? motion : MotionSubmission.None;
        setAccelGX?.Invoke(current.AccelG.X);
        setAccelGY?.Invoke(current.AccelG.Y);
        setAccelGZ?.Invoke(current.AccelG.Z);
        setGyroDpsX?.Invoke(current.GyroDps.X);
        setGyroDpsY?.Invoke(current.GyroDps.Y);
        setGyroDpsZ?.Invoke(current.GyroDps.Z);
    }

    private void WriteTouch(in TouchSubmission touch)
    {
        if (!HasTouchpadFields)
        {
            return;
        }

        // Written every frame including the all-released one: a finger
        // that lifts has to be reported as lifted, or the emitted pad
        // holds the last contact forever.
        setTouch0Active?.Invoke(touch.Finger0Down);
        setTouch0X?.Invoke(touch.Finger0X);
        setTouch0Y?.Invoke(touch.Finger0Y);
        setTouch0Id?.Invoke(touch.Finger0Id);

        setTouch1Active?.Invoke(touch.Finger1Down);
        setTouch1X?.Invoke(touch.Finger1X);
        setTouch1Y?.Invoke(touch.Finger1Y);
        setTouch1Id?.Invoke(touch.Finger1Id);
    }

    /// <summary>
    /// Boxed <c>HMButton</c> value for a button mask, memoised on the last
    /// one used.
    ///
    /// <para>
    /// <see cref="Enum.ToObject(Type, ulong)"/> allocates a box every call,
    /// and this runs once per frame per slot — up to 1000 times a second —
    /// even though the mask is unchanged on the overwhelming majority of
    /// frames (a human cannot change a button state every millisecond).
    /// Reusing the box is safe because the setter copies the value into the
    /// state struct's field rather than retaining the reference.
    /// </para>
    /// </summary>
    private object BoxButtonMask(ulong mask)
    {
        if (lastButtonBox is not null && lastButtonMask == mask)
        {
            return lastButtonBox;
        }

        lastButtonMask = mask;
        lastButtonBox = Enum.ToObject(buttonEnumType, mask);
        return lastButtonBox;
    }

    /// <summary>
    /// Boxed <c>HMHat</c> value for a direction name, cached per name.
    ///
    /// <para>
    /// The name comes from a fixed set of nine directions, but this used to
    /// run a case-insensitive <see cref="Enum.Parse(Type, string, bool)"/>
    /// inside a try/catch on every frame — a string search plus a box, to
    /// re-derive one of nine constants. Unknown names still resolve to the
    /// zero value, and are cached too so a bad name cannot reintroduce a
    /// per-frame exception.
    /// </para>
    /// </summary>
    private object BoxHat(string hatName)
    {
        if (hatBoxes.TryGetValue(hatName, out var cached))
        {
            return cached;
        }

        object hat;
        try { hat = Enum.Parse(hatEnumType, hatName, ignoreCase: true); }
        catch (ArgumentException) { hat = Enum.ToObject(hatEnumType, 0); }
        hatBoxes[hatName] = hat;
        return hat;
    }

    /// <summary>
    /// Submits one input frame. Sticks in [-1,1], triggers in [0,1].
    /// Returns false if the underlying reflected call failed — callers
    /// should count consecutive failures and stop calling after a few,
    /// rather than eating the same exception every frame forever.
    /// </summary>
    public bool Submit(
        float lx, float ly, float rx, float ry, float lt, float rt,
        IReadOnlyList<(string ButtonName, bool Down)> buttons, string hatName,
        byte batteryLevel, bool batteryCharging, bool batteryFull,
        in MotionSubmission motion, in TouchSubmission touch)
    {
        if (disposed)
        {
            return false;
        }

        EnsureIdentityClaimed();

        try
        {
            // Sticks arrive as [-1,1]; HMAxis values are uniform [0,1]
            // with 0.5 = center on signed axes (StandardAxes' contract).
            // Triggers arrive already [0,1] (0 = released), which IS the
            // unsigned-axis convention, so they pass through unscaled.
            writeLeftX?.Invoke((NormalizeAxis(lx, -1f) + 1f) * 0.5f);
            writeLeftY?.Invoke((NormalizeAxis(ly, -1f) + 1f) * 0.5f);
            writeRightX?.Invoke((NormalizeAxis(rx, -1f) + 1f) * 0.5f);
            writeRightY?.Invoke((NormalizeAxis(ry, -1f) + 1f) * 0.5f);
            writeLeftTrigger?.Invoke(NormalizeAxis(lt, 0f));
            writeRightTrigger?.Invoke(NormalizeAxis(rt, 0f));

            ulong mask = 0;
            foreach (var (name, down) in buttons)
            {
                if (!down)
                {
                    continue;
                }
                if (buttonValues.TryGetValue(name, out var value))
                {
                    mask |= value;
                }
                else if (missingButtons.Add(name))
                {
                    logger.LogWarning(
                        "HIDMaestro dynamic: HMButton has no member named '{Name}' — mapping skipped. Available: {Members}",
                        name, string.Join(", ", buttonValues.Keys));
                }
            }
            setButtons.Apply(boxedState, BoxButtonMask(mask));
            setHat.Apply(boxedState, BoxHat(hatName));

            // Charge, when this SDK exposes it. Leaving these at their
            // default is not neutral: the DualSense and DS4 reports always
            // carry a battery field, so an unwritten level goes out as
            // zero and the pad announces itself as nearly flat.
            setBatteryLevel?.Invoke(batteryLevel);
            setBatteryCharging?.Invoke(batteryCharging);
            setBatteryFull?.Invoke(batteryFull);

            WriteMotion(in motion);
            WriteTouch(in touch);

            submitFrame();

            if (consecutiveSubmitFailures > 0)
            {
                logger.LogInformation("HIDMaestro dynamic: submit recovered after {Count} failed frame(s).", consecutiveSubmitFailures);
                consecutiveSubmitFailures = 0;
            }
            return true;
        }
        catch (Exception exception)
        {
            consecutiveSubmitFailures++;
            var inner = (exception as TargetInvocationException)?.InnerException ?? exception;
            if (consecutiveSubmitFailures is 1 or 30 or 100)
            {
                // Rate-limited: log on the 1st/30th/100th consecutive
                // failure rather than every single frame (this loop can
                // run at 100–250 Hz).
                logger.LogError(inner,
                    "HIDMaestro dynamic: submit failed ({Count} consecutive). Method='{Method}'.",
                    consecutiveSubmitFailures, submitState.Name);
            }
            if (consecutiveSubmitFailures == FailureGiveUpThreshold)
            {
                logger.LogError(
                    "HIDMaestro dynamic: submit has failed {Count} consecutive times — giving up on this " +
                    "controller instance for good rather than retrying forever. Last error: {Error}",
                    consecutiveSubmitFailures, inner.Message);
            }
            return false;
        }
    }

    /// <summary>
    /// Claims the created pad's OS identity as soon as the SDK can name
    /// it, so device enumeration stops offering it as an input source.
    /// </summary>
    /// <remarks>
    /// Called from the submit path, where it costs one bool test once the
    /// id is known. It has to be retried rather than read once: the pad
    /// does not exist as an OS device the instant it is created, so the
    /// id is only available a little later. Retrying is bounded — an SDK
    /// that never reports one is a supported configuration, it just falls
    /// back to the other signals, and this must not turn into a
    /// reflection call on every frame forever.
    /// </remarks>
    private void EnsureIdentityClaimed()
    {
        if (identityGaveUp || InstanceId is not null)
        {
            return;
        }

        var id = ReadInstanceId(controller);
        if (!string.IsNullOrWhiteSpace(id))
        {
            InstanceId = id;
            VirtualDeviceIdentity.ClaimPath(id);
            logger.LogInformation(
                "HIDMaestro dynamic: claimed virtual pad instance {InstanceId}; it will not be offered as an input.",
                id);
            return;
        }

        if (DateTime.UtcNow >= identityDeadlineUtc)
        {
            identityGaveUp = true;
            logger.LogWarning(
                "HIDMaestro dynamic: the SDK never reported an InstanceId for this pad. It may appear in the " +
                "input device list as if it were physical hardware; the hardware-signature filter is the " +
                "remaining defence.");
        }
    }

    /// <summary>
    /// Reads <c>HMController.InstanceId</c> if this SDK exposes it.
    /// </summary>
    private static float NormalizeAxis(float value, float minimum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, 1f) : 0f;

    private static string? ReadInstanceId(object controller)
    {
        try
        {
            var property = controller.GetType().GetProperty(
                "InstanceId", BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(controller) as string;
        }
        catch (Exception)
        {
            // Identity is a nicety; a bridge that works without it is far
            // better than one that refuses to start because of it.
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        // Stop claiming the instance before the device goes away, so a
        // later pad that reuses the same id is not mistaken for this one.
        VirtualDeviceIdentity.Release(serial: null, path: InstanceId);

        UnsubscribeOutputEvents();
        TryRemoveController(controller, context, logger);
    }

    /// <summary>
    /// Removes the virtual device from the OS, trying every plausible
    /// teardown surface: IDisposable, duck-typed instance methods on the
    /// controller (Dispose/Close/Remove/…), then context-level removal
    /// (RemoveController/…). This being a silent no-op was THE
    /// controller-storm bug: every pipeline rebuild "disposed" its sink,
    /// nothing actually removed the device, and ghost pads accumulated
    /// (dozens over a session) until process exit tore the context down.
    /// Logs a WARNING with the actually-available members if no removal
    /// path exists, so a future SDK rename is loud instead of leaky.
    /// </summary>
    internal static void TryRemoveController(object controller, object? context, ILogger logger)
    {
        if (controller is IDisposable disposable)
        {
            try { disposable.Dispose(); return; }
            catch (Exception exception) { logger.LogDebug(exception, "HIDMaestro controller IDisposable.Dispose failed; trying other removal paths."); }
        }

        var controllerType = controller.GetType();
        foreach (var name in new[] { "Dispose", "Close", "Remove", "Destroy", "Disconnect", "Detach", "Delete" })
        {
            var method = controllerType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (method is null)
            {
                continue;
            }
            try
            {
                _ = method.Invoke(controller, null);
                return;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "HIDMaestro controller {Method}() failed; trying next removal path.", name);
            }
        }

        if (context is not null)
        {
            var contextType = context.GetType();
            foreach (var name in new[] { "RemoveController", "DestroyController", "DisconnectController", "Remove", "Destroy" })
            {
                var method = contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == name
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.IsInstanceOfType(controller));
                if (method is null)
                {
                    continue;
                }
                try
                {
                    _ = method.Invoke(context, [controller]);
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "HIDMaestro context {Method}(controller) failed; trying next removal path.", name);
                }
            }
        }

        logger.LogWarning(
            "HIDMaestro dynamic: no removal method found on {ControllerType} or its context — the virtual " +
            "device will remain until the process exits. Controller methods: {ControllerMethods}. Context methods: {ContextMethods}.",
            controllerType.Name,
            string.Join(", ", controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetParameters().Length == 0).Select(m => m.Name).Distinct().Take(24)),
            context is null ? "(no context)" : string.Join(", ", context.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name).Distinct().Take(24)));
    }
}
