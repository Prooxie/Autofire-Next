using Microsoft.Extensions.Logging;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Templates;
#if HIDMAESTRO_SDK
using HIDMaestro; // HMContext, HMController, HMProfile, HMProfileBuilder, HidDescriptorBuilder, HMGamepadState, HMButton, HMHat, HMOutputPacket
#endif

namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// Output sink that emits a virtual controller through HIDMaestro
/// (https://github.com/hifihedgehog/HIDMaestro) — a user-mode virtual
/// game-controller platform for Windows. It presents as real hardware
/// to DirectInput, XInput, SDL3, the browser Gamepad API and
/// WGI/GameInput, with no kernel driver, EV certificate, or reboot
/// (UMDF2 + a locally-trusted self-signed cert). MIT-licensed.
///
/// <para><b>Activation has two tiers:</b></para>
/// <list type="number">
/// <item><b>Compile-time SDK</b> (<c>#if HIDMAESTRO_SDK</c>, below) — used
/// when the project is built with the HIDMaestro.Core assembly referenced
/// and the compile symbol defined. Fastest path, no reflection.</item>
/// <item><b>Runtime dynamic bridge</b> (<see cref="HidMaestroDynamic"/>,
/// the tier that actually matters for ordinary builds) — if
/// <c>HIDMaestro.Core.dll</c> is simply dropped next to the executable,
/// it's loaded via reflection and driven the same way. No rebuild.</item>
/// </list>
/// <para>
/// HIDMaestro is the sole Windows output backend. If neither tier can
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
#if HIDMAESTRO_SDK
// ── Tier 1: compile-time SDK implementation ──
// Active only when the project defines the HIDMAESTRO_SDK compile symbol
// AND references the HIDMaestro.Core assembly. Verified against the
// SDK's example/SdkDemo/Program.cs:
//   ctx.LoadDefaultProfiles(); ctx.InstallDriver();
//   using var ctrl = ctx.CreateController(ctx.GetProfile("xbox-360-wired"));
//   ctrl.OutputReceived += (controller, packet) => { ... }; // rumble/FFB
//   ctrl.SubmitState(in state);  // sticks [-1,1], triggers [0,1]
// Verified (v1.3.9+, source cross-checked from github.com/hifihedgehog/
// HIDMaestro): HMGamepadState has NO LeftStickX/RightStickX/LeftTrigger/
// RightTrigger fields — analog goes through a single
// Dictionary<HMAxis,float> Axes field, populated via
// HMGamepadStateHelpers.StandardAxes(profile, ...), which resolves the
// correct HID axis per profile (HMProfile.Sticks/Triggers). An earlier
// version of this comment (and this file) assumed named per-stick
// properties from an older SDK layout — that shape doesn't exist in the
// open-source release and caused every controller creation to fail with
// "HMGamepadState is missing expected member(s)" for every profile,
// always, regardless of elevation. Buttons(HMButton flags) and
// Hat(HMHat) are unchanged and confirmed; HMHat None + the eight compass
// octants; SubmitState is the canonical submit method;
// HMButton.{A,B,X,Y,LeftBumper,RightBumper,Guide,Share} are confirmed,
// and Back/Start/LeftStick/RightStick are ALSO confirmed by name
// (contra the previous comment's "inferred" flag — the enum spells them
// exactly that way; see HMButton's XML doc for the Sony/Xbox aliasing).
// STILL INFERRED: the rumble byte offsets in OnOutputReceived.

public sealed class HidMaestroOutputSink : IOutputSink, GameFlow.Infrastructure.Runtime.Slots.IConfigurableOutputSink, IRumbleFeedbackSource
{
    private readonly ILogger<HidMaestroOutputSink> logger;
    private readonly object gate = new();

    private HMContext? context;
    private HMController? controller;
    private HMProfile? profile;
    private DeviceOutputTemplate template = new();
    private bool connected;
    private bool creationFailed;
    private bool disposed;

    public HidMaestroOutputSink(ILogger<HidMaestroOutputSink> logger)
    {
        this.logger = logger;
    }

    public string DisplayName => profile is null
        ? "HIDMaestro virtual controller"
        : $"HIDMaestro — {profile.Name}";

    /// <summary>Raised when the consuming game sends rumble (low, high) in 0–1.</summary>
    public event Action<double, double>? RumbleReceived;

    public (ushort Vid, ushort Pid)? OwnedHardwareSignature =>
        profile is not null
            ? (profile.VendorId, profile.ProductId)
            : HidMaestroProfiles.ResolveHardwareSignature(template.OutputKind);

    /// <inheritdoc />
    public DateTimeOffset? OwnedSignatureActivatedAt => connected ? activatedAtUtc : null;

    private DateTimeOffset activatedAtUtc;

    /// <summary>Earliest UTC time the next creation attempt is allowed after a failure (cooldown).</summary>
    private DateTimeOffset retryCreateAfterUtc;

    /// <summary>
    /// Applies a device's output template — picks the virtual-controller
    /// profile to present. Switching to a different profile tears down
    /// and recreates the controller.
    /// </summary>
    public void Configure(DeviceOutputTemplate template)
    {
        if (template is null)
        {
            return;
        }
        lock (gate)
        {
            var fingerprintChanged = !string.Equals(
                Fingerprint(this.template), Fingerprint(template), StringComparison.Ordinal);
            this.template = template.Clone();
            if (!fingerprintChanged)
            {
                // FULL no-op for an identical emit-shape — including when
                // creation previously FAILED. Rebuilds fire on every
                // profile save and slot edit; resetting the failure latch
                // here turned one failed creation (e.g. not running as
                // Administrator) into an endless stream of retry attempts
                // that other software (Steam!) saw as controllers
                // appearing and vanishing. A failed fingerprint stays
                // failed until the template actually changes.
                return;
            }
            creationFailed = false;
            TeardownController();
        }
    }

    private static string Fingerprint(DeviceOutputTemplate t) =>
        $"{t.OutputKind}|{t.OutputProfileId}|{t.ThumbstickCount}|{t.TriggerCount}|{t.ButtonCount}|{t.PovCount}|{t.ProductString}|{t.GenericVendorId:X4}|{t.GenericProductId:X4}";

    public ValueTask WriteAsync(ControllerSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        HMController? active;
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }
            EnsureConnected();
            active = controller;
        }

        if (active is null)
        {
            return ValueTask.CompletedTask;
        }

        // HMGamepadState.Axes drives every analog input (v1.3.9+ — no
        // LeftStickX/RightStickX/LeftTrigger/etc fields); the profile
        // supplies which HID axis each logical slot maps to.
        var state = BuildState(snapshot, active.Profile);
        active.SubmitState(in state);
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (connected)
        {
            return;
        }
        if (creationFailed)
        {
            if (DateTimeOffset.UtcNow < retryCreateAfterUtc)
            {
                return;
            }
            creationFailed = false; // cooldown elapsed — one fresh attempt
        }
        try
        {
            context ??= new HMContext();
            _ = context.LoadDefaultProfiles();   // load embedded catalog before GetProfile
            context.InstallDriver();             // no-op when already installed; needs elevation on first run

            profile = ResolveProfile(context, template);
            controller = context.CreateController(profile);
            controller.OutputReceived += OnOutputReceived;   // game rumble/haptics/FFB → physical pad
            connected = true;
            activatedAtUtc = DateTimeOffset.UtcNow;

            // Tell device enumeration this pad is ours. It is about to
            // reappear through SDL indistinguishable from real hardware —
            // its VID/PID deliberately impersonate the controller it
            // emulates — and without this claim it would show up in the
            // input picker as a physical pad that could be fed back into
            // another slot.
            VirtualDeviceIdentity.ClaimSerial(profile.Id);

            logger.LogInformation("HIDMaestro controller created for profile {ProfileId}.", profile.Id);
        }
        catch (Exception exception)
        {
            // Latched WITH a cooldown: retrying an identical create
            // every frame just repeats the same failure at tick rate,
            // but latching forever meant "no controller is ever created"
            // when the first attempt failed (e.g. the app wasn't
            // elevated yet). One retry per cooldown window recovers
            // automatically once the blocker is gone, and is far too
            // slow to read as a creation storm. Configure() with a
            // changed template still resets immediately.
            creationFailed = true;
            retryCreateAfterUtc = DateTimeOffset.UtcNow.AddSeconds(45);
            logger.LogError(exception,
                "Failed to create HIDMaestro controller; this slot has no output until its template changes " +
                "or the app restarts.{ElevationHint}",
                Environment.IsPrivilegedProcess
                    ? string.Empty
                    : " GameFlow is NOT running elevated — HIDMaestro needs administrator rights; restart as Administrator.");
            TeardownController();
        }
    }

    /// <summary>
    /// Resolves the HMProfile the template asks for: the explicit
    /// catalog id when set, the runtime-built generic profile for the
    /// generic kind, or the first kind candidate present in the catalog.
    /// </summary>
    private static HMProfile ResolveProfile(HMContext context, DeviceOutputTemplate template)
    {
        if (!string.IsNullOrWhiteSpace(template.OutputProfileId))
        {
            return context.GetProfile(template.OutputProfileId)
                ?? throw new InvalidOperationException(
                    $"HIDMaestro profile '{template.OutputProfileId}' not found in the loaded catalog.");
        }

        if (template.OutputKind == VirtualControllerKind.GenericDirectInput)
        {
            return BuildGenericProfile(template);
        }

        foreach (var candidate in HidMaestroProfiles.GetCandidateProfileIds(template.OutputKind))
        {
            if (context.GetProfile(candidate) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException(
            $"No catalog profile found for kind {template.OutputKind} " +
            $"(tried: {string.Join(", ", HidMaestroProfiles.GetCandidateProfileIds(template.OutputKind))}).");
    }

    /// <summary>
    /// Authors the generic DirectInput device from the template's shape.
    /// HMGamepadState models two sticks, two triggers, and one hat, so
    /// the shape is clamped to what can actually be driven.
    /// </summary>
    private static HMProfile BuildGenericProfile(DeviceOutputTemplate template)
    {
        var descriptor = new HidDescriptorBuilder().Gamepad();
        var sticks = Math.Clamp(template.ThumbstickCount, 0, 2);
        if (sticks >= 1) { descriptor = descriptor.AddStick("Left", 16); }
        if (sticks >= 2) { descriptor = descriptor.AddStick("Right", 16); }
        var triggers = Math.Clamp(template.TriggerCount, 0, 2);
        if (triggers >= 1) { descriptor = descriptor.AddTrigger("Left", 8); }
        if (triggers >= 2) { descriptor = descriptor.AddTrigger("Right", 8); }
        descriptor = descriptor.AddButtons(Math.Clamp(template.ButtonCount, 1, 128));
        if (template.PovCount >= 1) { descriptor = descriptor.AddHat(); }

        var productString = string.IsNullOrWhiteSpace(template.ProductString)
            ? "GameFlow Game Controller"
            : template.ProductString;

        return new HMProfileBuilder()
            .Id($"gameflow-custom-{template.GenericVendorId:x4}{template.GenericProductId:x4}")
            .Name(productString)
            .Vendor("GameFlow")
            .Vid(template.GenericVendorId).Pid(template.GenericProductId)
            .ProductString(productString)
            .ManufacturerString("GameFlow")
            .Type("gamepad")
            .Connection("usb")
            .FromDescriptorBuilder(descriptor)
            .Build();
    }

    // ── State mapping (verified HMGamepadState surface) ──

    private static HMGamepadState BuildState(ControllerSnapshot s, HMProfile profile)
    {
        // v1.3.9+: no LeftStickX/RightStickX/LeftTrigger/etc fields —
        // HMGamepadStateHelpers.StandardAxes resolves the profile's
        // actual HID axis for each of the six standard slots (a wheel's
        // "stick" is a different usage than a gamepad's) and returns a
        // ready Axes dictionary. Sticks [-1,+1] in our snapshot map to
        // StandardAxes' [0,1] uniform range (0.5 = center); triggers
        // [0,1] pass straight through. If a target shows inverted Y on
        // hardware, negate the leftStickY/rightStickY arguments.
        return new HMGamepadState
        {
            Axes = HMGamepadStateHelpers.StandardAxes(
                profile,
                leftStickX:  (Math.Clamp(s.LeftStick.X,  -1f, 1f) + 1f) * 0.5f,
                leftStickY:  (Math.Clamp(s.LeftStick.Y,  -1f, 1f) + 1f) * 0.5f,
                rightStickX: (Math.Clamp(s.RightStick.X, -1f, 1f) + 1f) * 0.5f,
                rightStickY: (Math.Clamp(s.RightStick.Y, -1f, 1f) + 1f) * 0.5f,
                leftTrigger:  Math.Clamp(s.LeftTrigger,  0f, 1f),
                rightTrigger: Math.Clamp(s.RightTrigger, 0f, 1f)),
            Buttons = MapButtons(s),
            Hat = MapHat(s),
        };
    }

    private static HMButton MapButtons(ControllerSnapshot s)
    {
        // A/B/X/Y, bumpers, Guide, Share are confirmed HMButton names.
        // Back/Start and the thumb clicks follow XInput-standard naming —
        // CONFIRM these four against the HMButton enum in your SDK build.
        HMButton b = HMButton.None;
        void Set(ButtonId id, HMButton flag) { if (s.IsPressed(id)) b |= flag; }
        Set(ButtonId.South, HMButton.A);
        Set(ButtonId.East,  HMButton.B);
        Set(ButtonId.West,  HMButton.X);
        Set(ButtonId.North, HMButton.Y);
        Set(ButtonId.LeftShoulder,  HMButton.LeftBumper);
        Set(ButtonId.RightShoulder, HMButton.RightBumper);
        Set(ButtonId.Guide, HMButton.Guide);
        Set(ButtonId.Touchpad, HMButton.Share);     // DualSense Create/Share
        Set(ButtonId.Back,  HMButton.Back);          // ← confirm (View/Menu?)
        Set(ButtonId.Start, HMButton.Start);         // ← confirm
        Set(ButtonId.LeftStick,  HMButton.LeftThumb);   // ← confirm
        Set(ButtonId.RightStick, HMButton.RightThumb);  // ← confirm
        return b;
    }

    private static HMHat MapHat(ControllerSnapshot s)
    {
        bool up = s.IsPressed(ButtonId.DpadUp);
        bool down = s.IsPressed(ButtonId.DpadDown);
        bool left = s.IsPressed(ButtonId.DpadLeft);
        bool right = s.IsPressed(ButtonId.DpadRight);
        if (up && right) return HMHat.NorthEast;
        if (down && right) return HMHat.SouthEast;
        if (down && left) return HMHat.SouthWest;
        if (up && left) return HMHat.NorthWest;
        if (up) return HMHat.North;
        if (right) return HMHat.East;
        if (down) return HMHat.South;
        if (left) return HMHat.West;
        return HMHat.None;
    }

    // ── Output (rumble / haptics / FFB) ──
    // OutputReceived delivers the raw wire bytes the game sent to the
    // virtual pad; the consumer decodes + forwards. This is a best-effort
    // decode of the common rumble layout into normalized (low, high) →
    // RumbleReceived, which the slot runtime forwards to the physical
    // device. VERIFY these byte offsets against the output
    // handler for the profiles you emit.
    private void OnOutputReceived(HMController sender, HMOutputPacket packet)
    {
        var data = packet.Data;
        if (data is null || data.Length == 0)
        {
            return;
        }

        double low = 0, high = 0;
        if (data.Length >= 5)
        {
            // Common XUSB SET_STATE vibration: [type, size, 0, big, small, …]
            low  = data[3] / 255.0;
            high = data[4] / 255.0;
        }
        else if (data.Length >= 2)
        {
            low  = data[0] / 255.0;
            high = data[1] / 255.0;
        }

        if (low > 0 || high > 0)
        {
            RumbleReceived?.Invoke(low, high);
        }
    }

    private void TeardownController()
    {
        try
        {
            if (controller is not null)
            {
                controller.OutputReceived -= OnOutputReceived;
            }
            controller?.Dispose();
        }
        catch (Exception exception) { logger.LogDebug(exception, "HIDMaestro controller dispose error."); }
        controller = null;
        profile = null;
        connected = false;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }
            disposed = true;
            TeardownController();
            try { context?.Dispose(); }
            catch (Exception exception) { logger.LogDebug(exception, "HIDMaestro context dispose error."); }
            context = null;
        }
        return ValueTask.CompletedTask;
    }
}
#else
// ── Tier 2: what an ordinary (non-SDK) build actually runs ──
// This is the class that compiles for essentially every real install,
// since referencing the SDK assembly at build time is not something a
// normal build does.
//
// HIDMaestro is the sole Windows output backend. It's either active
// (dynamic bridge found a working HIDMaestro.Core.dll) or it is not —
// in which case DisplayName and the log say exactly why, and WriteAsync
// is a documented no-op. There is no fallback provider to substitute.
public sealed class HidMaestroOutputSink : IOutputSink, GameFlow.Infrastructure.Runtime.Slots.IConfigurableOutputSink
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
                return activeState == "active" ? dynamicActivatedAtUtc : null;
            }
        }
    }

    private DateTimeOffset dynamicActivatedAtUtc;

    /// <summary>Cooldown twin of the SDK tier's <c>retryCreateAfterUtc</c>.</summary>
    private DateTimeOffset dynamicRetryCreateAfterUtc;

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
            activeState = "unresolved";
            unavailableReason = null;
        }
        old?.Controller.Dispose();
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
            lock (gate)
            {
                if (ReferenceEquals(activeHandle, handle))
                {
                    activeHandle = null;
                    activeState = "unavailable";
                    unavailableReason = "submit failed repeatedly — see log";
                    // Longer cooldown than a creation failure: a retry
                    // here creates a NEW device, so cycling must be rare.
                    dynamicRetryCreateAfterUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                }
            }
            handle.Controller.Dispose();
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
            activeState = "unavailable";
            logger.LogWarning("HIDMaestro requested on a non-Windows platform — this slot has no output.");
            return;
        }

        if (!HidMaestroDynamic.IsAvailable(logger))
        {
            unavailableReason = HidMaestroDynamic.StatusDescription;
            activeState = "unavailable";
            logger.LogWarning(
                "HIDMaestro is not available ({Status}) — this slot has NO output until HIDMaestro.Core.dll " +
                "is in place. There is no fallback output provider.",
                HidMaestroDynamic.StatusDescription);
            return;
        }

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
            unavailableReason = creationFailure;
            dynamicRetryCreateAfterUtc = DateTimeOffset.UtcNow.AddSeconds(45);
            activeState = "unavailable";
            logger.LogError(
                "HIDMaestro controller creation failed ({Failure}) — this slot has no output until this is " +
                "resolved. There is no fallback output provider.",
                creationFailure);
            return;
        }

        activeHandle = handle;
        activeState = "active";
        dynamicActivatedAtUtc = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "HIDMaestro (dynamic) active: profile {ProfileId} ('{ProfileName}', VID/PID {Signature}).",
            handle.ProfileId, handle.ProfileName,
            handle.HardwareSignature is { } sig ? $"{sig.Vid:X4}:{sig.Pid:X4}" : "unknown");
    }

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

    /// <summary>Maps a snapshot onto the dynamic bridge's button-name/hat-name submit call.</summary>
    private static bool SubmitDynamic(DynamicHidMaestroController controller, ControllerSnapshot s)
    {
        // Same HMButton name mapping as the compile-time tier (see the
        // #if HIDMAESTRO_SDK branch's MapButtons for the confirmed vs.
        // inferred split); the dynamic bridge logs once per session if
        // any of these names don't exist on the real HMButton enum.
        var buttons = new List<(string ButtonName, bool Down)>(12)
        {
            ("A", s.IsPressed(ButtonId.South)),
            ("B", s.IsPressed(ButtonId.East)),
            ("X", s.IsPressed(ButtonId.West)),
            ("Y", s.IsPressed(ButtonId.North)),
            ("LeftBumper",  s.IsPressed(ButtonId.LeftShoulder)),
            ("RightBumper", s.IsPressed(ButtonId.RightShoulder)),
            ("Guide", s.IsPressed(ButtonId.Guide)),
            ("Share", s.IsPressed(ButtonId.Touchpad)),
            ("Back",  s.IsPressed(ButtonId.Back)),
            ("Start", s.IsPressed(ButtonId.Start)),
            ("LeftThumb",  s.IsPressed(ButtonId.LeftStick)),
            ("RightThumb", s.IsPressed(ButtonId.RightStick)),
        };

        return controller.Submit(
            Math.Clamp(s.LeftStick.X,  -1f, 1f),
            Math.Clamp(s.LeftStick.Y,  -1f, 1f),
            Math.Clamp(s.RightStick.X, -1f, 1f),
            Math.Clamp(s.RightStick.Y, -1f, 1f),
            Math.Clamp(s.LeftTrigger,  0f, 1f),
            Math.Clamp(s.RightTrigger, 0f, 1f),
            buttons,
            ResolveHatName(s));
    }

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
        }

        handle?.Controller.Dispose();
        return ValueTask.CompletedTask;
    }
}
#endif
