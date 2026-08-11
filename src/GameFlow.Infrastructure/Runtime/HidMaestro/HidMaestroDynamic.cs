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
/// Runtime (reflection-based) bridge to HIDMaestro.Core. The compile-time
/// path (<c>HIDMAESTRO_SDK</c>) is still preferred when the project is
/// built against the SDK, but this loader means a user can simply drop
/// <c>HIDMaestro.Core.dll</c> next to the executable (or into a
/// <c>HIDMaestro</c> subfolder) and the real sink activates — no rebuild,
/// no compile symbol.
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
        _ = InvokeBestEffort(contextType, "InstallDriver", logger, out var installDriverFailure);

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
        else if (installDriverFailure is not null)
        {
            status += $" WARNING: InstallDriver() failed ({installDriverFailure}); device creation may fail — see log.";
            logger.LogWarning(
                "HIDMaestro dynamic bridge resolved, but InstallDriver() failed ({Failure}). Device creation " +
                "will likely fail until this is resolved.",
                installDriverFailure);
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
        failure = null;
        return true;
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
    private readonly object?[] submitArgs;

    // v1.3.9+: HMGamepadState has no LeftStickX/RightStickX/LeftTrigger/etc
    // properties -- analog input goes through a single Axes dictionary,
    // keyed by HID usage (HMAxis) and resolved PER PROFILE (a wheel's
    // "stick" is a different HID usage than a gamepad's). See Bind()
    // in the constructor for the discovery step and the SDK's own
    // HMGamepadStateHelpers.StandardAxes, which this mirrors.
    private readonly Setter setAxes, setButtons, setHat;
    private readonly Type axesDictType;
    private readonly object? axisLeftX, axisLeftY, axisRightX, axisRightY, axisLeftTrigger, axisRightTrigger;
    private readonly bool hasAnyAxis;
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
        submitArgs = [boxedState];

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

        axesInstance = (System.Collections.IDictionary)(Activator.CreateInstance(axesDictType)
            ?? throw new InvalidOperationException($"Could not instantiate {axesDictType.Name} for HMGamepadState.Axes."));
        setAxes.Apply(boxedState, axesInstance);

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
            if (source is null || Convert.ToInt32(source) != 2) // HMOutputSource.XInput
            {
                return;
            }

            if (TryReadBytes(ReadMember(packet, "Data"), out var bytes)
                && HidMaestroRumbleDecoder.TryDecodeXInput(bytes.Span, out var low, out var high))
            {
                RumbleReceived?.Invoke(low, high);
            }
        }
        catch (Exception exception)
        {
            LogOutputWarningOnce(exception);
        }
    }

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

            if (ReadMember(eventArgs, "Fields") is IReadOnlyDictionary<string, object> fields
                && HidMaestroRumbleDecoder.TryDecodeSemanticFields(fields, out var low, out var high))
            {
                RumbleReceived?.Invoke(low, high);
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
    /// Submits one input frame. Sticks in [-1,1], triggers in [0,1].
    /// Returns false if the underlying reflected call failed — callers
    /// should count consecutive failures and stop calling after a few,
    /// rather than eating the same exception every frame forever.
    /// </summary>
    public bool Submit(
        float lx, float ly, float rx, float ry, float lt, float rt,
        IReadOnlyList<(string ButtonName, bool Down)> buttons, string hatName)
    {
        if (disposed)
        {
            return false;
        }

        try
        {
            // Sticks arrive as [-1,1]; HMAxis values are uniform [0,1]
            // with 0.5 = center on signed axes (StandardAxes' contract).
            // Triggers arrive already [0,1] (0 = released), which IS the
            // unsigned-axis convention, so they pass through unscaled.
            if (axisLeftX is not null) { axesInstance[axisLeftX] = (lx + 1f) * 0.5f; }
            if (axisLeftY is not null) { axesInstance[axisLeftY] = (ly + 1f) * 0.5f; }
            if (axisRightX is not null) { axesInstance[axisRightX] = (rx + 1f) * 0.5f; }
            if (axisRightY is not null) { axesInstance[axisRightY] = (ry + 1f) * 0.5f; }
            if (axisLeftTrigger is not null) { axesInstance[axisLeftTrigger] = lt; }
            if (axisRightTrigger is not null) { axesInstance[axisRightTrigger] = rt; }

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
            setButtons.Apply(boxedState, Enum.ToObject(buttonEnumType, mask));

            object hat;
            try { hat = Enum.Parse(hatEnumType, hatName, ignoreCase: true); }
            catch { hat = Enum.ToObject(hatEnumType, 0); }
            setHat.Apply(boxedState, hat);

            _ = submitState.Invoke(controller, submitArgs);

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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
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
