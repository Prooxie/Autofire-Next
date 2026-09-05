using Microsoft.Extensions.Logging;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Templates;

namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// Output sink that emits a virtual controller through HIDMaestro
/// (https://github.com/hifihedgehog/HIDMaestro) — a user-mode virtual
/// game-controller platform for Windows. It presents as real hardware
/// to DirectInput, XInput, SDL3, the browser Gamepad API and
/// WGI/GameInput, with no kernel driver, EV certificate, or reboot
/// (UMDF2 + a locally-trusted self-signed cert). MIT-licensed.
///
/// <para>
/// <b>Activation:</b> the SDK is bound at runtime by
/// <see cref="HidMaestroDynamic"/>. Dropping <c>HIDMaestro.Core.dll</c>
/// next to the executable (or into a <c>HIDMaestro</c> subfolder, or the
/// folder named by <c>GAMEFLOW_HIDMAESTRO_DIR</c>) activates the real sink
/// with no rebuild and no compile symbol.
/// </para>
///
/// <para>
/// There used to be a second, compile-time tier behind
/// <c>#if HIDMAESTRO_SDK</c> that referenced the SDK's types directly. It
/// was removed: nothing defined the symbol and no project referenced the
/// assembly, so it could not be compiled — and therefore could not be
/// compiler-checked — without editing the build first. What it actually
/// did was duplicate the logic below and silently drift away from it.
/// </para>
///
/// <para>
/// HIDMaestro is the sole Windows output backend. If it cannot
/// activate, the slot has no output, and both the log and
/// <see cref="DisplayName"/> say exactly why — including the two causes
/// that account for essentially every real "no controller appears"
/// report: the process not running elevated, and a profile id that
/// doesn't exist in the SDK's catalog. What the sink deploys is decided
/// by the slot's <see cref="DeviceOutputTemplate"/>: an explicit catalog
/// profile id when set (any of HIDMaestro's 225 profiles), the verified
/// default profile for the template's <see cref="VirtualControllerKind"/>
/// otherwise, or — for <see cref="VirtualControllerKind.GenericDirectInput"/>
/// — a profile BUILT at runtime from the template's axis/button/POV
/// counts via <c>HMProfileBuilder</c> + <c>HidDescriptorBuilder</c>.
/// </para>
/// </summary>
public sealed class HidMaestroOutputSink : IOutputSink,
    GameFlow.Infrastructure.Runtime.Slots.IConfigurableOutputSink,
    GameFlow.Infrastructure.Runtime.Slots.IPostRebuildFinalizer,
    IRumbleFeedbackSource
{
    private readonly ILogger<HidMaestroOutputSink> logger;
    private readonly object gate = new();

    private DeviceOutputTemplate template = new();
    private bool disposed;

    // Resolved lazily on first write after each Configure(). Non-null
    // only while HIDMaestro is genuinely active and healthy.
    private DynamicControllerHandle? activeHandle;
    private string activeState = "unresolved"; // "unresolved" | "active" | "unavailable"
    private string? unavailableReason;

    public HidMaestroOutputSink(ILogger<HidMaestroOutputSink> logger)
    {
        this.logger = logger;
    }

    public event Action<double, double>? RumbleReceived;

    /// <summary>
    /// Reflects the real state so the slots list and dashboard show the
    /// truth at a glance: which profile is live, or exactly why none is.
    /// </summary>
    public string DisplayName => activeState switch
    {
        "active" when activeHandle is not null => $"HIDMaestro — {activeHandle.ProfileName}",
        "unavailable" => $"HIDMaestro unavailable — no output ({unavailableReason})",
        _ => "HIDMaestro virtual controller",
    };

    /// <summary>
    /// The identity this sink's emitted device advertises, used to hide
    /// it from the input list (a virtual output selected back in as
    /// input was a real freeze source). Once a controller is live this
    /// is the REAL identity read from the deployed profile — covering
    /// all 225 catalog profiles and runtime-built generics — with the
    /// per-kind well-known pair as the pre-activation fallback.
    /// </summary>
    public (ushort Vid, ushort Pid)? OwnedHardwareSignature
    {
        get
        {
            lock (gate)
            {
                return activeHandle?.HardwareSignature
                    ?? (template.OutputKind == VirtualControllerKind.GenericDirectInput
                        ? (template.GenericVendorId, template.GenericProductId)
                        : HidMaestroProfiles.ResolveHardwareSignature(template.OutputKind));
            }
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? OwnedSignatureActivatedAt
    {
        get
        {
            lock (gate)
            {
                return activeState == "active" || signatureReserved ? dynamicActivatedAtUtc : null;
            }
        }
    }

    private DateTimeOffset dynamicActivatedAtUtc;

    /// <summary>
    /// True from the moment device creation is attempted until the device
    /// is gone or the attempt is known to have failed.
    /// </summary>
    /// <remarks>
    /// The signature used to be published only once creation had returned
    /// successfully, which leaves a window: the device exists in Windows,
    /// and can therefore be enumerated by SDL, before anything has claimed
    /// it. A poll landing in that window publishes GameFlow's own pad as
    /// physical hardware, and it is only withdrawn a tick later when the
    /// signature finally arrives — the "appears as a real controller,
    /// then disappears" flicker.
    ///
    /// <para>
    /// Reserving before the call closes the window, at the cost of also
    /// covering the few hundred milliseconds the call takes. A real pad of
    /// the same model plugged in during exactly that window would be
    /// hidden; one plugged in before it still is not, because the catalog
    /// compares against when each device was FIRST seen.
    /// </para>
    /// </remarks>
    private bool signatureReserved;

    /// <summary>Cooldown twin of the SDK tier's <c>retryCreateAfterUtc</c>.</summary>
    private DateTimeOffset dynamicRetryCreateAfterUtc;

    /// <summary>
    /// How long an "unavailable" verdict is held before it is re-checked.
    ///
    /// <para>
    /// Every path that latches <c>activeState = "unavailable"</c> must also
    /// arm this, because the latch alone does not stop anything: the gate in
    /// <c>EnsureActiveLocked</c> is the cooldown, and an unarmed cooldown is
    /// already in the past. The missing-DLL and non-Windows paths latched
    /// without arming, so every tick fell straight through the latch,
    /// re-probed, and logged again — roughly a thousand identical warnings a
    /// second, to the console and the rolling file sink, on the single most
    /// common failure there is ("HIDMaestro.Core.dll not found").
    /// </para>
    /// </summary>
    private static readonly TimeSpan UnavailableRetryInterval = TimeSpan.FromSeconds(45);

    public void Configure(DeviceOutputTemplate template)
    {
        if (template is null)
        {
            return;
        }

        DynamicControllerHandle? old;
        lock (gate)
        {
            var fingerprintChanged = !string.Equals(
                Fingerprint(this.template), Fingerprint(template), StringComparison.Ordinal);
            this.template = template.Clone();
            if (!fingerprintChanged)
            {
                // FULL no-op for an identical emit-shape — see the SDK
                // tier's comment: an "unavailable" latch (failed creation,
                // give-up) must survive rebuilds, or every profile save
                // retries and the OS sees a controller-creation storm.
                // The latch clears only when the template genuinely
                // changes what device is emitted.
                return;
            }
            old = activeHandle;
            activeHandle = null;
            signatureReserved = false;
            activeState = "unresolved";
            unavailableReason = null;
        }
        if (old is not null)
        {
            TeardownHandle(old);
        }
    }

    /// <summary>
    /// Everything about the template that changes WHAT device is
    /// emitted. Lighting/rumble fields deliberately excluded — they
    /// mustn't tear down a live device.
    /// </summary>
    private static string Fingerprint(DeviceOutputTemplate t) =>
        $"{t.OutputKind}|{t.OutputProfileId}|{t.ThumbstickCount}|{t.TriggerCount}|{t.ButtonCount}|{t.PovCount}|{t.ProductString}|{t.GenericVendorId:X4}|{t.GenericProductId:X4}";

    public ValueTask WriteAsync(ControllerSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DynamicControllerHandle? handle;
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }
            EnsureActiveLocked();
            handle = activeHandle;
        }

        if (handle is null)
        {
            // Unavailable — EnsureActiveLocked already logged exactly why,
            // once, the first time resolution failed for this
            // configuration. No output; nothing silently substituted.
            return ValueTask.CompletedTask;
        }

        var ok = SubmitDynamic(handle.Controller, snapshot);
        if (!ok && !handle.Controller.IsHealthy)
        {
            // The controller itself gave up after too many consecutive
            // reflection failures (logged there). Stop holding a
            // reference to a proven-broken instance so the NEXT write
            // doesn't keep trying it — Configure() (a template change) or
            // a process restart are the paths back to "unresolved".
            var detached = false;
            lock (gate)
            {
                if (ReferenceEquals(activeHandle, handle))
                {
                    activeHandle = null;
                    signatureReserved = false;
                    activeState = "unavailable";
                    unavailableReason = "submit failed repeatedly — see log";
                    // Longer cooldown than a creation failure: a retry
                    // here creates a NEW device, so cycling must be rare.
                    dynamicRetryCreateAfterUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                    detached = true;
                }
            }
            if (detached)
            {
                TeardownHandle(handle);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Resolves the dynamic HIDMaestro bridge once per Configure() call.
    /// Callers must hold <see cref="gate"/>.
    /// </summary>
    private void EnsureActiveLocked()
    {
        if (activeHandle is not null)
        {
            return; // already active for this configuration
        }
        if (activeState == "unavailable")
        {
            // Latched — but only until the cooldown elapses. A single
            // failed creation (typically: not running as Administrator
            // yet) used to be terminal until the template changed, which
            // read as "no controller is ever created". One retry per
            // 45 s window recovers automatically once the blocker is
            // gone and cannot read as a creation storm.
            if (DateTimeOffset.UtcNow < dynamicRetryCreateAfterUtc)
            {
                return;
            }
            activeState = "unresolved";
            unavailableReason = null;
        }

        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "HIDMaestro is only available on Windows.";
            dynamicRetryCreateAfterUtc = DateTimeOffset.UtcNow + UnavailableRetryInterval;
            activeState = "unavailable";
            logger.LogWarning("HIDMaestro requested on a non-Windows platform — this slot has no output.");
            return;
        }

        if (!HidMaestroDynamic.IsAvailable(logger))
        {
            unavailableReason = HidMaestroDynamic.StatusDescription;
            // Armed so the DLL is re-probed periodically rather than on every
            // tick: dropping HIDMaestro.Core.dll in beside a running app still
            // recovers within the window, without the log flood.
            dynamicRetryCreateAfterUtc = DateTimeOffset.UtcNow + UnavailableRetryInterval;
            activeState = "unavailable";
            logger.LogWarning(
                "HIDMaestro is not available ({Status}) — this slot has NO output until HIDMaestro.Core.dll " +
                "is in place. There is no fallback output provider. Re-checking in {RetrySeconds:F0}s.",
                HidMaestroDynamic.StatusDescription,
                UnavailableRetryInterval.TotalSeconds);
            return;
        }

        // Reserved before the device can exist, not after it does — see
        // the note on signatureReserved.
        dynamicActivatedAtUtc = DateTimeOffset.UtcNow;
        signatureReserved = true;

        DynamicControllerHandle? handle;
        string? creationFailure;
        if (template.OutputKind == VirtualControllerKind.GenericDirectInput
            && string.IsNullOrWhiteSpace(template.OutputProfileId))
        {
            var productString = string.IsNullOrWhiteSpace(template.ProductString)
                ? "GameFlow Game Controller"
                : template.ProductString;
            handle = HidMaestroDynamic.TryCreateCustomController(
                profileId: $"gameflow-custom-{template.GenericVendorId:x4}{template.GenericProductId:x4}",
                displayName: productString,
                productString: productString,
                vendorId: template.GenericVendorId,
                productId: template.GenericProductId,
                thumbstickCount: template.ThumbstickCount,
                triggerCount: template.TriggerCount,
                buttonCount: template.ButtonCount,
                povCount: template.PovCount,
                logger, out creationFailure);
        }
        else
        {
            var profileId = ResolveCatalogProfileId();
            handle = HidMaestroDynamic.TryCreateController(profileId, logger, out creationFailure);
        }

        if (handle is null)
        {
            signatureReserved = false;
            unavailableReason = creationFailure;
            dynamicRetryCreateAfterUtc = DateTimeOffset.UtcNow + UnavailableRetryInterval;
            activeState = "unavailable";
            logger.LogError(
                "HIDMaestro controller creation failed ({Failure}) — this slot has no output until this is " +
                "resolved. There is no fallback output provider.",
                creationFailure);
            return;
        }

        handle.Controller.RumbleReceived += OnRumbleReceived;
        activeHandle = handle;
        activeState = "active";
        WarnIfOptionalStateCannotReachTheWire(handle);
        logger.LogInformation(
            "HIDMaestro (dynamic) active: profile {ProfileId} ('{ProfileName}', VID/PID {Signature}).",
            handle.ProfileId, handle.ProfileName,
            handle.HardwareSignature is { } sig ? $"{sig.Vid:X4}:{sig.Pid:X4}" : "unknown");
    }

    /// <summary>
    /// Says once, per pad, when the deployed profile cannot carry the
    /// optional state this sink submits.
    /// </summary>
    /// <remarks>
    /// Battery, motion and touch travel in a Sony report's extended
    /// section, and HIDMaestro runs that section's codec on the input
    /// direction only for a profile that arms it. Every USB Sony profile
    /// leaves both <c>armOn</c> and <c>alwaysArmed</c> unset — checked
    /// directly against the shipped catalogue: <c>dualshock-4-v1-full</c>
    /// and <c>dualsense</c> have neither, while <c>dualsense-bt</c> arms
    /// on a feature read — so the battery byte is never written and the
    /// submitted value goes nowhere.
    ///
    /// <para>
    /// This is invisible from inside GameFlow: the submit succeeds, the
    /// SDK accepts the field, and the number simply does not appear on
    /// the wire. It was diagnosed only by changing the submitted charge
    /// from 3 to 25 and watching the reported value not move. Saying so
    /// costs one line and saves that experiment.
    /// </para>
    /// </remarks>
    private void WarnIfOptionalStateCannotReachTheWire(DynamicControllerHandle handle)
    {
        var profileId = handle.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        var isSony = profileId.StartsWith("dualshock", StringComparison.OrdinalIgnoreCase)
            || profileId.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase);

        // The Bluetooth variants are the ones that arm the codec.
        var isBluetooth = profileId.Contains("-bt", StringComparison.OrdinalIgnoreCase);

        if (!isSony || isBluetooth)
        {
            return;
        }

        logger.LogWarning(
            "HIDMaestro: profile '{ProfileId}' is a USB Sony profile, which does not arm its extended-report "
            + "codec on the input direction — battery, motion and touch are submitted but never reach the wire, "
            + "so a game or Steam reads the driver's default rather than the source pad's real values. "
            + "Choose a Bluetooth Sony profile (for example dualsense-bt) if those need to pass through.",
            profileId);
    }

    private void OnRumbleReceived(double lowFrequency, double highFrequency) =>
        RumbleReceived?.Invoke(lowFrequency, highFrequency);

    private void TeardownHandle(DynamicControllerHandle handle) =>
        HidMaestroRumbleLifecycle.StopAndTeardown(
            RumbleReceived,
            () =>
            {
                handle.Controller.RumbleReceived -= OnRumbleReceived;
                handle.Controller.Dispose();
            });

    /// <summary>
    /// The catalog id this template resolves to: the explicit pick when
    /// set (verified against the catalog, with the kind's defaults as a
    /// safety net if the pick has vanished from a newer SDK), otherwise
    /// the first kind candidate that exists in the loaded catalog.
    /// </summary>
    private string ResolveCatalogProfileId()
    {
        var kindCandidates = HidMaestroProfiles.GetCandidateProfileIds(template.OutputKind);
        List<string> candidates;
        if (!string.IsNullOrWhiteSpace(template.OutputProfileId))
        {
            candidates = new List<string>(kindCandidates.Count + 1) { template.OutputProfileId.Trim() };
            candidates.AddRange(kindCandidates);
        }
        else
        {
            candidates = [.. kindCandidates];
        }

        if (candidates.Count == 0)
        {
            // GenericDirectInput with an explicit profile cleared between
            // Configure and now — fall back to the safest catalog id.
            candidates = ["xbox-360-wired"];
        }

        var resolved = HidMaestroDynamic.TryResolveExistingProfileId(candidates, logger) ?? candidates[0];
        if (!string.IsNullOrWhiteSpace(template.OutputProfileId)
            && !string.Equals(resolved, template.OutputProfileId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "HIDMaestro: this slot's chosen profile '{Chosen}' is not in the loaded catalog — using '{Resolved}' instead.",
                template.OutputProfileId, resolved);
        }
        return resolved;
    }

    /// <summary>
    /// Canonical HMButton names, paired by index with
    /// <see cref="SubmitButtonSources"/>. Resolved against the real HMButton
    /// enum at bind time; the dynamic bridge logs once per session if any of
    /// these names does not exist on it, and skips that one mapping rather
    /// than failing the whole submit.
    /// </summary>
    private static readonly string[] SubmitButtonNames =
    [
        "A", "B", "X", "Y",
        "LeftBumper", "RightBumper",
        "Guide", "Touchpad", "Back", "Start",
        "LeftThumb", "RightThumb",
        "LeftPaddle", "RightPaddle", "LeftPaddle2", "RightPaddle2",
        "Misc1",
    ];

    /// <summary>
    /// The <see cref="ButtonId"/> each entry of <see cref="SubmitButtonNames"/>
    /// reads.
    ///
    /// <para>
    /// The paddles pair by SIDE, not by number, matching how GameFlow reads
    /// them from SDL (<c>Paddle1</c> = SDL's LeftPaddle1, and so on) and how
    /// the SDK's own <c>HMButton</c> documents its split. Numbering is what
    /// the pads themselves disagree about; the side is not.
    /// </para>
    /// </summary>
    private static readonly ButtonId[] SubmitButtonSources =
    [
        ButtonId.South, ButtonId.East, ButtonId.West, ButtonId.North,
        ButtonId.LeftShoulder, ButtonId.RightShoulder,
        ButtonId.Guide, ButtonId.Touchpad, ButtonId.Back, ButtonId.Start,
        ButtonId.LeftStick, ButtonId.RightStick,
        ButtonId.Paddle1, ButtonId.Paddle2, ButtonId.Paddle3, ButtonId.Paddle4,
        ButtonId.Misc1,
    ];

    /// <summary>
    /// Reused button buffer for <see cref="SubmitDynamic"/>.
    ///
    /// <para>
    /// This used to be a fresh 12-entry <see cref="List{T}"/> of
    /// <c>(string, bool)</c> tuples built on every frame. Writes happen once
    /// per slot per tick — up to 1000 times a second each — and the names
    /// never change, so only the twelve booleans are actually new. Safe to
    /// share per sink because writes come from the single runtime tick loop.
    /// </para>
    /// </summary>
    private readonly (string ButtonName, bool Down)[] submitButtonBuffer =
        new (string, bool)[SubmitButtonNames.Length];

    /// <inheritdoc />
    public void FinalizeAfterRebuild()
    {
        // Only meaningful once a controller actually exists; before that
        // there are no names to settle.
        if (activeState != "active")
        {
            return;
        }

        try
        {
            HidMaestroDynamic.FinalizeControllerNames(logger);
        }
        catch (Exception exception)
        {
            // Cosmetic step. A rebuild must not fail because Windows would
            // not settle a friendly name.
            logger.LogDebug(exception, "HIDMaestro: finalizing virtual controller names failed.");
        }
    }

    /// <summary>The submit tables, exposed so tests can assert the mapping without a deployed SDK.</summary>
    internal static string[] SubmitButtonNamesForTests => SubmitButtonNames;

    /// <inheritdoc cref="SubmitButtonNamesForTests"/>
    internal static ButtonId[] SubmitButtonSourcesForTests => SubmitButtonSources;

    /// <summary>Maps a snapshot onto the dynamic bridge's button-name/hat-name submit call.</summary>
    private bool SubmitDynamic(DynamicHidMaestroController controller, ControllerSnapshot s)
    {
        for (int i = 0; i < SubmitButtonNames.Length; i++)
        {
            submitButtonBuffer[i] = (SubmitButtonNames[i], s.IsPressed(SubmitButtonSources[i]));
        }

        LogFirstBatterySubmission(s);

        return controller.Submit(
            Math.Clamp(s.LeftStick.X,  -1f, 1f),
            Math.Clamp(s.LeftStick.Y,  -1f, 1f),
            Math.Clamp(s.RightStick.X, -1f, 1f),
            Math.Clamp(s.RightStick.Y, -1f, 1f),
            Math.Clamp(s.LeftTrigger,  0f, 1f),
            Math.Clamp(s.RightTrigger, 0f, 1f),
            submitButtonBuffer,
            ResolveHatName(s),
            BatteryLevelFor(s),
            s.BatteryCharging,
            s.BatteryPercent is null or >= 100,
            MotionFor(controller, s),
            TouchFor(controller, s));
    }

    /// <summary>Degrees per radian — the snapshot carries SDL's rad/s, the SDK wants deg/s.</summary>
    private const float DegreesPerRadian = 57.29577951308232f;

    /// <summary>Standard gravity in m/s² — the snapshot carries SDL's m/s², the SDK wants g.</summary>
    private const float StandardGravity = 9.80665f;

    /// <summary>
    /// This frame's motion, converted into the SDK's units.
    ///
    /// <para>
    /// The axis frames already agree: GameFlow's snapshot carries SDL's
    /// gyro and accelerometer values unmodified, and the SDK documents its
    /// calibrated members as being in that same SDL sensor frame. Only the
    /// units differ — rad/s to deg/s, and m/s² to g.
    /// </para>
    ///
    /// <para>
    /// A source with no sensor reports <see cref="DynamicHidMaestroController.MotionSubmission.None"/>
    /// rather than zeroes. That distinction is the whole reason
    /// <see cref="ControllerSnapshot.HasGyro"/> exists: "perfectly still"
    /// and "no gyro here" are not the same claim to make to a game, and
    /// this is the same mistake the battery fields made when they went out
    /// unwritten as a flat-battery reading.
    /// </para>
    /// </summary>
    private static DynamicHidMaestroController.MotionSubmission MotionFor(
        DynamicHidMaestroController controller, ControllerSnapshot s) =>
        controller.HasMotionFields ? ConvertMotion(s) : DynamicHidMaestroController.MotionSubmission.None;

    /// <summary>The unit conversion alone, split out so it is testable without a deployed SDK.</summary>
    internal static DynamicHidMaestroController.MotionSubmission ConvertMotion(ControllerSnapshot s)
    {
        if (!s.HasGyro
            || !float.IsFinite(s.AccelX) || !float.IsFinite(s.AccelY) || !float.IsFinite(s.AccelZ)
            || !float.IsFinite(s.GyroPitch * DegreesPerRadian)
            || !float.IsFinite(s.GyroYaw * DegreesPerRadian)
            || !float.IsFinite(s.GyroRoll * DegreesPerRadian))
        {
            return DynamicHidMaestroController.MotionSubmission.None;
        }

        return new DynamicHidMaestroController.MotionSubmission(
            Present: true,
            AccelG: (s.AccelX / StandardGravity, s.AccelY / StandardGravity, s.AccelZ / StandardGravity),
            GyroDps: (s.GyroPitch * DegreesPerRadian, s.GyroYaw * DegreesPerRadian, s.GyroRoll * DegreesPerRadian));
    }

    /// <summary>
    /// This frame's touch surface, converted from the snapshot's
    /// normalized 0..1 into the Sony native ranges the SDK documents
    /// (X 0..1919, Y 0..1079).
    ///
    /// <para>
    /// Falls back to the primary <c>TouchX</c>/<c>TouchY</c> point when the
    /// source reports a touch without placing individual fingers — the same
    /// split the mapping pipeline already honours, and the reason a source
    /// that can only count fingers never fabricates positions for them.
    /// </para>
    ///
    /// <para>
    /// Finger ids come from the contact's own index rather than a counter,
    /// so a finger keeps its id for the life of the contact; the SDK ORs in
    /// the firmware "lifted" flag itself when a finger is reported inactive.
    /// </para>
    /// </summary>
    private static DynamicHidMaestroController.TouchSubmission TouchFor(
        DynamicHidMaestroController controller, ControllerSnapshot s) =>
        controller.HasTouchpadFields ? ConvertTouch(s) : DynamicHidMaestroController.TouchSubmission.None;

    /// <summary>The coordinate conversion alone, split out so it is testable without a deployed SDK.</summary>
    internal static DynamicHidMaestroController.TouchSubmission ConvertTouch(ControllerSnapshot s)
    {
        var contacts = s.TouchContacts;
        if (contacts.Count == 0)
        {
            return s.TouchDown
                ? new DynamicHidMaestroController.TouchSubmission(
                    true, TouchX(s.TouchX), TouchY(s.TouchY), 0,
                    false, 0, 0, 0)
                : DynamicHidMaestroController.TouchSubmission.None;
        }

        var first = contacts[0];
        var hasSecond = contacts.Count > 1;
        var second = hasSecond ? contacts[1] : default;

        return new DynamicHidMaestroController.TouchSubmission(
            true, TouchX(first.X), TouchY(first.Y), (byte)(first.FingerIndex & 0x7F),
            hasSecond, TouchX(second.X), TouchY(second.Y), (byte)(second.FingerIndex & 0x7F));
    }

    /// <summary>Touchpad surface width in the DualSense / DS4 native range.</summary>
    private const int TouchpadMaxX = 1919;

    /// <summary>Touchpad surface height in the DualSense / DS4 native range.</summary>
    private const int TouchpadMaxY = 1079;

    private static ushort TouchX(float normalized) => ToSurface(normalized, TouchpadMaxX);

    private static ushort TouchY(float normalized) => ToSurface(normalized, TouchpadMaxY);

    private static ushort ToSurface(float normalized, int max)
    {
        if (!float.IsFinite(normalized))
        {
            return 0;
        }

        return (ushort)MathF.Round(Math.Clamp(normalized, 0f, 1f) * max);
    }

    /// <summary>
    /// Charge to report on the emitted pad, on the SDK's 0..10 scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These fields were never written at all to begin with, and an
    /// unwritten one is not an absent one: a DualSense or DualShock 4
    /// report always carries a battery, so it went out as zero and every
    /// virtual pad announced itself as nearly flat, forever, whatever was
    /// driving it — a wired pad, a keyboard, or nothing.
    /// </para>
    /// <para>
    /// Writing a percentage was the second half of the same mistake. The
    /// SDK documents the field as "Battery capacity, 0..10 (Sony firmware
    /// convention)", and this was read as meaning the SDK's own property
    /// carries that scale. It does not — it takes a percentage, and
    /// down-scales into the Sony field itself. Rescaling here divided the
    /// charge by ten a second time.
    /// </para>
    /// <para>
    /// Settled by measurement rather than by the wording. With the
    /// division in place a source pad at 25% was submitted as 3 and read
    /// back as 3%, and a source at 65% as 7 and read back as roughly 7% —
    /// consumers take the value as a percentage, exactly and without
    /// rescaling. Two independent readings, both matching the raw number
    /// rather than a tenth of it, which is not a coincidence available to
    /// the 0..10 reading.
    /// </para>
    /// <para>
    /// A source with no battery maps to full, which is what a wired
    /// controller says about itself — the honest answer as well as the
    /// quiet one, since there is no battery here to be low.
    /// </para>
    /// </remarks>
    internal static byte BatteryLevelFor(ControllerSnapshot s) =>
        s.BatteryPercent is { } percent
            ? (byte)Math.Clamp(percent, 0, FullCharge)
            : FullCharge;

    /// <summary>Full charge, as a percentage.</summary>
    private const byte FullCharge = 100;

    /// <summary>
    /// Says once, per sink, what charge this slot is actually sending.
    /// </summary>
    /// <remarks>
    /// "The virtual pad reads 5%" has two completely different causes that
    /// look identical from outside: the source snapshot carrying no charge
    /// at all (so the write is the no-battery fallback), or the value
    /// travelling on a scale the consumer reads differently. A live capture
    /// pinned the emitted pad at exactly 10% across runs while its source
    /// moved 75% to 65% — which is the fallback constant, not a rescaled
    /// measurement — but the log could not prove which of the two it was.
    /// This line prints both halves so the next run settles it.
    /// </remarks>
    private void LogFirstBatterySubmission(ControllerSnapshot s)
    {
        // Two separate firsts, because the first submission of all is
        // almost always too early to mean anything: a pad is created and
        // starts submitting within milliseconds, while SDL's first battery
        // reading for the SOURCE pad lands over a second later. A capture
        // showed exactly that — "no charge reported" at 28.713, the real
        // 65% arriving at 30.055 — which says nothing about the steady
        // state, and the steady state is the thing in question.
        var known = s.BatteryPercent is not null;

        if (batterySubmissionLogged && (batteryKnownLogged || !known))
        {
            return;
        }

        batterySubmissionLogged = true;
        batteryKnownLogged |= known;

        logger.LogInformation(
            "HIDMaestro dynamic: battery submitted ({Which}) — source={Source}, " +
            "written={Written}% (0-{Full}), charging={Charging}, treatedAsFull={Full2}.",
            known ? "first with a real charge — this is the steady state"
                  : "first submission of all — the source may not have been read yet",
            s.BatteryPercent is { } p ? p + "%" : "<none reported by the source pad>",
            BatteryLevelFor(s),
            FullCharge,
            s.BatteryCharging,
            s.BatteryPercent is null or >= 100);
    }

    private bool batterySubmissionLogged;

    /// <summary>Whether a submission carrying a real charge has been reported.</summary>
    private bool batteryKnownLogged;

    private static string ResolveHatName(ControllerSnapshot s)
    {
        bool up = s.IsPressed(ButtonId.DpadUp);
        bool down = s.IsPressed(ButtonId.DpadDown);
        bool left = s.IsPressed(ButtonId.DpadLeft);
        bool right = s.IsPressed(ButtonId.DpadRight);
        if (up && right) return "NorthEast";
        if (down && right) return "SouthEast";
        if (down && left) return "SouthWest";
        if (up && left) return "NorthWest";
        if (up) return "North";
        if (right) return "East";
        if (down) return "South";
        if (left) return "West";
        return "None";
    }

    public ValueTask DisposeAsync()
    {
        DynamicControllerHandle? handle;
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }
            disposed = true;
            handle = activeHandle;
            activeHandle = null;
            signatureReserved = false;
        }

        if (handle is not null)
        {
            TeardownHandle(handle);
        }
        return ValueTask.CompletedTask;
    }
}
