using System.Runtime.InteropServices;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Sdl;
using Microsoft.Extensions.Logging;

namespace GameFlow.Infrastructure.Runtime;

public sealed class SdlUnifiedInputSource : IInputSource, GameFlow.Infrastructure.Runtime.Slots.IMultiDeviceInputSource
{
    private readonly Lock syncRoot = new();
    private readonly ILogger<SdlUnifiedInputSource> logger;
    private readonly InputDeviceCatalog inputDeviceCatalog;
    private readonly GameFlow.Infrastructure.Runtime.Input.ButtonMapStore buttonMapStore;
    private readonly GameFlow.Infrastructure.Runtime.Input.IKeyboardStateSource keyboardStateSource;
    private readonly GameFlow.Infrastructure.Runtime.Input.IMouseStateSource mouseStateSource;

    /// <summary>Phones connected to the web controller server. Answered directly in <see cref="ReadDevice"/> — they never touch SDL.</summary>
    private readonly GameFlow.Infrastructure.Runtime.Web.WebControllerHub webControllerHub;
    private OpenedDevice? openedDevice;
    // Per-slot device handles (slot mode), keyed by catalog device id —
    // independent of the single `openedDevice` used by the legacy
    // single-pipeline ReadAsync path. SDL ref-counts opens, so a device
    // can be open here and as `openedDevice` simultaneously.
    private readonly Dictionary<string, OpenedDevice> slotHandles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps each device's stable id (built from VID+PID+name — see
    /// RefreshDeviceCatalog) to whatever live SDL instance id it currently
    /// resolves to, rebuilt on every catalog refresh. TryParseDeviceId
    /// resolves through this instead of decoding a number out of the id
    /// string, which is what lets a slot's saved device assignment survive
    /// an app restart or a controller reconnect — the id itself is stable;
    /// only its live mapping changes.
    /// </summary>
    private readonly Dictionary<string, uint> liveInstanceIdByStableId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DevicePowerInfo> powerByDeviceId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Handles opened purely to read battery for devices no slot has
    /// opened. Held open rather than opened per poll — see
    /// <see cref="RefreshPowerStateCache"/>.
    /// </summary>
    private readonly Dictionary<string, OpenedDevice> telemetryHandles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the effects thread posts. Null when effects are not wired up.</summary>
    private readonly GameFlow.Infrastructure.Runtime.Effects.ControllerEffectMailbox? effectMailbox;
    private IntPtr rawInspectionHandle = IntPtr.Zero;
    private string? rawInspectionHandleId;

    // Stabilizes device display names: SDL can hand back a slightly
    // different name for the same device on successive polls, which would
    // otherwise make the label flicker every tick. First non-empty name
    // per id wins until the device disappears.
    private readonly Dictionary<string, string> stableNames = new(StringComparer.Ordinal);
    private bool disposed;
    private bool sdlInitialized;

    // ── Dedicated SDL worker (owns every SDL call after the ctor) ──
    private Thread? worker;
    private volatile bool stopRequested;
    private volatile string currentOperation = "idle";
    private long lastLoopTimestampTicks = DateTime.UtcNow.Ticks;
    private System.Threading.Timer? stallWatchdog;
    private DateTime nextCatalogRefreshUtc = DateTime.MinValue;
    private DateTime nextPowerRefreshUtc = DateTime.MinValue;
    private readonly object primaryGate = new();
    private ControllerSnapshot latestPrimary = ControllerSnapshot.Empty("SDL3 unified input");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ControllerSnapshot> slotSnapshotsById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> requestedSlotDeviceIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> openRetryNotBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When each open handle first went missing from enumeration. See
    /// <see cref="PruneStaleSlotHandles"/> — a handle is only released
    /// after the absence persists, because a momentary gap in SDL's device
    /// list is normal and closing on it caused a permanent reopen loop.
    /// </summary>
    private readonly Dictionary<string, DateTime> missingSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a device must stay absent before its handle is released.</summary>
    private static readonly TimeSpan StaleHandleGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Device ids already reported as unknown, so a slot pointing at a pad
    /// that no longer exists warns once rather than on every tick.
    /// Cleared when the device reappears.
    /// </summary>
    private readonly HashSet<string> unknownDeviceWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loggedNonSdlSkips = new(StringComparer.OrdinalIgnoreCase);

    public SdlUnifiedInputSource(ILogger<SdlUnifiedInputSource> logger, InputDeviceCatalog inputDeviceCatalog, GameFlow.Infrastructure.Runtime.Input.ButtonMapStore buttonMapStore, GameFlow.Infrastructure.Runtime.Input.IKeyboardStateSource keyboardStateSource, GameFlow.Infrastructure.Runtime.Input.IMouseStateSource mouseStateSource, GameFlow.Infrastructure.Runtime.Web.WebControllerHub webControllerHub, GameFlow.Infrastructure.Runtime.Effects.ControllerEffectMailbox? effectMailbox = null)
    {
        this.logger = logger;
        this.inputDeviceCatalog = inputDeviceCatalog;
        this.buttonMapStore = buttonMapStore;
        this.keyboardStateSource = keyboardStateSource;
        this.mouseStateSource = mouseStateSource;
        this.webControllerHub = webControllerHub;
        this.effectMailbox = effectMailbox;
        if (effectMailbox is not null) { effectMailbox.HasCollector = true; }

        SdlInterop.SetMainReady();
        // SDL_JOYSTICK_THREAD = 0 (was 1). With the background joystick thread
        // ON, SDL polls devices on its own thread while holding the internal
        // joystick lock (SDL_LockJoysticks). A Bluetooth DualSense HID transfer
        // can stall inside that poll, and because our per-tick GetGamepad* reads
        // — and any lightbar/rumble write — need the SAME lock, the runtime
        // thread then blocks acquiring it and the whole app freezes (intermittent,
        // because it depends on BT timing). We already pump SDL manually under
        // syncRoot in BOTH ReadAsync and PumpForSlots (SDL_AUTO_UPDATE_JOYSTICKS
        // is 0), so the dedicated thread buys us nothing and only introduces the
        // contention. Turning it off means input reads happen on our thread via
        // SDL_UpdateGamepads (non-blocking reads of already-delivered reports)
        // with no second thread holding the lock.
        _ = SdlInterop.SetHint(SdlInterop.HintJoystickThread, "0");
        _ = SdlInterop.SetHint(SdlInterop.HintJoystickAllowBackgroundEvents, "1");
        _ = SdlInterop.SetHint(SdlInterop.HintJoystickHidApi, "1");
        _ = SdlInterop.SetHint(SdlInterop.HintJoystickDirectInput, "1");
        _ = SdlInterop.SetHint(SdlInterop.HintXInputEnabled, "1");
        _ = SdlInterop.SetHint(SdlInterop.HintAutoUpdateJoysticks, "0");
        // Enhanced reports on DS4/DS5 pads.
        //
        // This was pinned OFF to stop a real bug: sending the enhanced-mode
        // switch wedges inside the HID driver on some Bluetooth stacks and
        // SDL_OpenGamepad never returns — "freeze the moment a DualSense
        // joins a slot". That hazard is unchanged and is why this is not
        // simply switched on.
        //
        // But the justification attached to it — "we don't consume
        // gyro/touch-sensor data, so simple reports are sufficient" — has
        // been overtaken. Simple reports carry no battery state, no motion
        // and no touchpad, so that one line silently disables the battery
        // readout, DualSense gyro (and therefore the DSU motion server that
        // feeds Cemu/Dolphin/Yuzu/Ryujinx) and touchpad input. All three
        // are advertised features. Leaving it off is not neutral; it just
        // moves the breakage somewhere less obvious than a freeze.
        //
        // So it is a choice now, defaulting to ON because the features it
        // gates are ones the app claims to have. GAMEFLOW_ENHANCED_REPORTS=0
        // restores the old behaviour without a rebuild — deliberately an
        // environment variable, because if this DOES wedge the pad the
        // window may be unresponsive and the in-app settings unreachable.
        var enhanced = !string.Equals(
            Environment.GetEnvironmentVariable("GAMEFLOW_ENHANCED_REPORTS"),
            "0",
            StringComparison.Ordinal);

        _ = SdlInterop.SetHint("SDL_JOYSTICK_ENHANCED_REPORTS", enhanced ? "1" : "0");

        logger.LogInformation(
            enhanced
                ? "SDL enhanced reports ENABLED — battery, gyro and touchpad available on DualSense/DS4. "
                  + "If a DualSense freezes the app when it joins a slot, set GAMEFLOW_ENHANCED_REPORTS=0."
                : "SDL enhanced reports disabled by GAMEFLOW_ENHANCED_REPORTS — battery, gyro and touchpad "
                  + "will be unavailable on DualSense/DS4.");

        if (!SdlInterop.Init(SdlInterop.InitGamepad | SdlInterop.InitJoystick))
        {
            throw new InvalidOperationException($"SDL3 input initialization failed: {SdlInterop.GetError()}");
        }

        sdlInitialized = true;
        TryLoadOptionalMappings();
        RefreshDeviceCatalog();
        logger.LogInformation("Initialized SDL3 unified input provider.");

        StartWorker();
    }

    private void StartWorker()
    {
        worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SDL3-Input",
        };
        worker.Start();
        stallWatchdog = new System.Threading.Timer(
            _ => CheckWorkerStall(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The only thread that talks to SDL after construction. Pumps events,
    /// refreshes the catalog, reads the primary device and every requested
    /// slot device, and publishes snapshots for the lock-free facades. If a
    /// device call blocks (Bluetooth open handshake), only this thread
    /// stalls — the UI and runtime keep running on published snapshots and
    /// the watchdog makes the stall visible in the log.
    /// </summary>
    private void WorkerLoop()
    {
        logger.LogInformation("SDL worker thread started (owns all SDL device I/O).");
        var interval = TimeSpan.FromMilliseconds(4);

        while (!stopRequested && !disposed)
        {
            var started = DateTime.UtcNow;
            try
            {
                ControllerSnapshot primary;
                lock (syncRoot)
                {
                    if (disposed)
                    {
                        break;
                    }

                    currentOperation = "pump";
                    SdlInterop.UpdateJoysticks();
                    SdlInterop.UpdateGamepads();

                    // Full device enumeration (native queries + list build)
                    // is pointless 250x/s — 4 Hz keeps hotplug latency at
                    // worst 250 ms while cutting steady-state work.
                    var utcNow = DateTime.UtcNow;
                    if (utcNow >= nextCatalogRefreshUtc)
                    {
                        nextCatalogRefreshUtc = utcNow.AddMilliseconds(250);
                        RefreshDeviceCatalog();
                        PruneStaleSlotHandles();
                    }

                    currentOperation = "read primary";
                    primary = ReadSnapshotCore();
                    PollRawInspection();

                    foreach (var deviceId in requestedSlotDeviceIds.Keys)
                    {
                        currentOperation = "read " + deviceId;
                        var snapshot = WorkerReadSlotDevice(deviceId);
                        if (snapshot is not null)
                        {
                            slotSnapshotsById[deviceId] = snapshot;
                        }
                    }

                    RefreshPowerStateCache(utcNow);
                    ApplyPendingEffects();

                    currentOperation = "idle";
                }

                lock (primaryGate)
                {
                    latestPrimary = primary;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SDL worker tick failed; continuing.");
            }

            _ = Interlocked.Exchange(ref lastLoopTimestampTicks, DateTime.UtcNow.Ticks);

            // Drift-free pacing against an absolute deadline. Sleep(1) is
            // bounded by the OS timer resolution (1–15.6 ms), so the real
            // rate lands between ~64 Hz and ~250 Hz depending on the
            // system — plenty for input, and no busy spinning.
            var deadline = started + interval;
            for (var left = deadline - DateTime.UtcNow; left > TimeSpan.Zero; left = deadline - DateTime.UtcNow)
            {
                Thread.Sleep(1);
            }
        }

        logger.LogInformation("SDL worker thread exiting.");
    }

    private void CheckWorkerStall()
    {
        if (disposed || stopRequested)
        {
            return;
        }

        var last = new DateTime(Interlocked.Read(ref lastLoopTimestampTicks), DateTimeKind.Utc);
        var stalled = DateTime.UtcNow - last;
        if (stalled > TimeSpan.FromSeconds(3))
        {
            logger.LogWarning(
                "SDL worker stalled for {Seconds:F0}s inside '{Operation}' — a device call is blocking (Bluetooth HID handshake?). The UI stays responsive; input resumes if the call returns.",
                stalled.TotalSeconds,
                currentOperation);
        }
    }

    public string DisplayName => "SDL3 unified input";

    public ValueTask<ControllerSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Lock-free facade: never touches SDL (see ReadDevice for rationale).
        var devices = inputDeviceCatalog.Devices;
        if (devices.Count > 0)
        {
            var selected = ResolveSelectedDevice(devices);
            if (selected is not null)
            {
                var synthesized = SynthesizeNonSdl(selected.Id, selected);
                if (synthesized is not null)
                {
                    return ValueTask.FromResult(synthesized);
                }
            }
        }

        lock (primaryGate)
        {
            return ValueTask.FromResult(latestPrimary);
        }
    }

    // ── Slot mode (IMultiDeviceInputSource) ──

    /// <summary>
    /// No-op: the dedicated SDL worker pumps continuously. Kept for the
    /// IMultiDeviceInputSource contract; callers must never reach SDL.
    /// </summary>
    public void PumpForSlots()
    {
    }

    /// <summary>
    /// Reads the snapshot for one device id using a cached per-slot
    /// handle (opened on first use). Returns an empty snapshot if the
    /// device isn't present or can't be opened.
    /// </summary>
    public ControllerSnapshot ReadDevice(string deviceId)
    {
        if (disposed || string.IsNullOrWhiteSpace(deviceId))
        {
            return ControllerSnapshot.Empty("No device") with { Timestamp = DateTimeOffset.UtcNow };
        }

        // Web controller pads are served straight from the hub — they
        // never touch SDL. Intercepting here rather than teaching
        // SlotRuntime about a second source keeps every existing slot
        // feature (multi-device merge, per-slot profiles, the whole rule
        // pipeline) working on a phone with no special-casing at all.
        var webPadIndex = Web.WebControllerDeviceScanner.TryParsePadIndex(deviceId);
        if (webPadIndex >= 0)
        {
            return webControllerHub.GetSnapshot(webPadIndex);
        }

        // Lock-free by design: keyboards/mice are synthesized from Raw Input
        // (no SDL), everything else is served from the snapshot the SDL
        // worker last published. Callers can NEVER block behind a device
        // open — the freeze this replaces was SDL_OpenGamepad wedging in a
        // Bluetooth HID handshake while holding the lock every reader needed.
        _ = inputDeviceCatalog.TryGetById(deviceId, out var info);
        var synthesized = SynthesizeNonSdl(deviceId, info);
        if (synthesized is not null)
        {
            return synthesized;
        }

        _ = requestedSlotDeviceIds.TryAdd(deviceId, 0);
        return slotSnapshotsById.TryGetValue(deviceId, out var published)
            ? published
            : ControllerSnapshot.Empty(info?.DisplayName ?? deviceId) with { Timestamp = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Synthesizes a gamepad snapshot for Raw Input keyboards/mice (SDL-free);
    /// returns null for devices this helper doesn't cover.
    /// </summary>
    private ControllerSnapshot? SynthesizeNonSdl(string deviceId, InputDeviceInfo? info)
    {
            if (info?.Category == DeviceCategory.Keyboard)
            {
                // Per-device first; when that handle reports nothing,
                // fall back to the union of ALL keyboards — Windows
                // frequently splits one physical keyboard into several
                // Raw Input handles, and the assigned id isn't always
                // the one keystrokes arrive under.
                var pressed = keyboardStateSource.GetPressedKeys(deviceId);
                if (pressed.Count == 0)
                {
                    pressed = keyboardStateSource.GetPressedKeysAggregate();
                }
                return GameFlow.Infrastructure.Runtime.Input.KeyboardGamepadSynthesizer
                    .Synthesize(info.DisplayName ?? deviceId, pressed)
                    with
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        // Carried unreduced so a full-layout keyboard theme
                        // can light every key — the synthesized Buttons above
                        // only cover the handful that map onto a ButtonId.
                        PressedKeys = pressed
                    };
            }
            if (info?.Category == DeviceCategory.Mouse)
            {
                var frame = mouseStateSource.ReadMouseFrame(deviceId);
                if (frame is { Dx: 0, Dy: 0, WheelDelta: 0, Left: false, Right: false, Middle: false, Button4: false, Button5: false })
                {
                    frame = mouseStateSource.ReadMouseFrameAggregate();
                }
                return GameFlow.Infrastructure.Runtime.Input.MouseGamepadSynthesizer
                    .Synthesize(info.DisplayName ?? deviceId, frame)
                    with { Timestamp = DateTimeOffset.UtcNow };
            }
        return null;
    }

    /// <summary>Worker-only: opens (lazily) and reads one slot device. Blocking SDL calls live here, on the worker.</summary>
    private ControllerSnapshot? WorkerReadSlotDevice(string deviceId)
    {
            var device = EnsureSlotDeviceOpened(deviceId);
            if (device is null)
            {
                return ControllerSnapshot.Empty(deviceId) with { Timestamp = DateTimeOffset.UtcNow };
            }

            // Gate the crash trace on THIS slot device, then mark it traced
            // once a full read survives. Without this the slot path never
            // silenced the breadcrumb: it wrote ~20 File.AppendAllText lines
            // every tick at 250 Hz, all while holding syncRoot — which
            // stalled the runtime loop and froze the app the instant a
            // controller was mapped to a slot.
            activeTraceDeviceId = deviceId;
            var snapshot = device.Kind == DeviceKind.Gamepad
                ? ReadGamepadSnapshot(device)
                : ReadJoystickSnapshot(device);
            tracedDeviceIds.Add(deviceId);
            return snapshot;
    }

    private OpenedDevice? EnsureSlotDeviceOpened(string deviceId)
    {
        if (slotHandles.TryGetValue(deviceId, out var cached))
        {
            return cached;
        }

        if (!TryParseDeviceId(deviceId, out var kind, out var instanceId))
        {
            return null;
        }

        // Do not open what enumeration does not list.
        //
        // A slot keeps the device id it was configured with, and those ids
        // are derived from the enumeration signature — so a pad that was
        // unplugged, or that now enumerates differently, leaves a slot
        // pointing at an id the catalog no longer has. SDL will still
        // happily open the raw instance, so the open SUCCEEDS, and then
        // PruneStaleSlotHandles releases it again because it is not in the
        // catalog. Open, release, open, release, forever — visible in the
        // log as one device cycling every few seconds and, before the
        // grace period was added, four times a second.
        //
        // The catalog is the authority on what exists. If it does not list
        // the device, there is nothing to open, and saying so once is far
        // better than rediscovering it on every tick.
        if (!inputDeviceCatalog.TryGetById(deviceId, out _))
        {
            if (unknownDeviceWarned.Add(deviceId))
            {
                logger.LogWarning(
                    "Slot references device {DeviceId}, which is not present. It will be ignored until it "
                    + "reappears — reassign the slot if the pad was replaced.",
                    deviceId);
            }

            return null;
        }

        // Failed opens back off so a device that refuses to open (or is
        // mid-Bluetooth-handshake) isn't hammered at 250 Hz.
        if (openRetryNotBefore.TryGetValue(deviceId, out var notBefore) && DateTime.UtcNow < notBefore)
        {
            return null;
        }

        currentOperation = "opening " + deviceId;
        logger.LogDebug("Opening SDL device {DeviceId} (kind {Kind})…", deviceId, kind);
        var openTimer = System.Diagnostics.Stopwatch.StartNew();

        var handle = kind == DeviceKind.Gamepad
            ? SdlInterop.OpenGamepad(instanceId)
            : SdlInterop.OpenJoystick(instanceId);

        openTimer.Stop();
        currentOperation = "idle";
        if (openTimer.ElapsedMilliseconds > 500)
        {
            logger.LogWarning(
                "SDL device {DeviceId} took {ElapsedMs} ms to open — slow Bluetooth HID handshake.",
                deviceId, openTimer.ElapsedMilliseconds);
        }

        if (handle == IntPtr.Zero)
        {
            openRetryNotBefore[deviceId] = DateTime.UtcNow.AddSeconds(2);
            logger.LogWarning(
                "SDL device {DeviceId} failed to open after {ElapsedMs} ms: {Error}. Retrying in 2 s.",
                deviceId, openTimer.ElapsedMilliseconds, SdlInterop.GetError());
            return null;
        }

        _ = openRetryNotBefore.Remove(deviceId);
        logger.LogDebug("SDL device {DeviceId} opened in {ElapsedMs} ms.", deviceId, openTimer.ElapsedMilliseconds);

        // Motion sensors are OPT-IN in SDL — without this call every
        // SDL_GetGamepadSensorData read returns nothing, even on a pad
        // that fully supports gyro. Failures are non-fatal: a pad
        // without the sensor just keeps reporting zero motion.
        if (kind == DeviceKind.Gamepad)
        {
            TryEnableMotionSensors(handle, deviceId);
        }

        var displayName = inputDeviceCatalog.TryGetById(deviceId, out var catalogInfo)
            ? catalogInfo!.DisplayName
            : deviceId;
        var opened = new OpenedDevice(deviceId, displayName, instanceId, kind, handle);
        slotHandles[deviceId] = opened;
        return opened;
    }

    /// <summary>
    /// Turns on gyro + accelerometer if the pad has them. Both are
    /// independent: a pad can have one without the other (and the
    /// Player/World reference frames degrade to Local when the
    /// accelerometer is absent — see GyroMapRule).
    /// </summary>
    private void TryEnableMotionSensors(IntPtr handle, string deviceId)
    {
        try
        {
            var hasGyro = SdlInterop.GamepadHasSensor(handle, SdlInterop.SensorType.Gyro);
            var hasAccel = SdlInterop.GamepadHasSensor(handle, SdlInterop.SensorType.Accel);

            if (hasGyro)
            {
                _ = SdlInterop.SetGamepadSensorEnabled(handle, SdlInterop.SensorType.Gyro, true);
            }
            if (hasAccel)
            {
                _ = SdlInterop.SetGamepadSensorEnabled(handle, SdlInterop.SensorType.Accel, true);
            }

            if (hasGyro || hasAccel)
            {
                logger.LogInformation(
                    "SDL device {DeviceId}: motion sensors enabled (gyro: {HasGyro}, accelerometer: {HasAccel}).",
                    deviceId, hasGyro, hasAccel);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "SDL device {DeviceId}: enabling motion sensors failed.", deviceId);
        }
    }

    /// <summary>
    /// Reads gyro + accelerometer for this tick. Returns all-zero with
    /// hasGyro=false on pads without the sensor, which is what lets the
    /// pipeline distinguish "held perfectly still" from "no gyro here".
    /// </summary>
    private (bool HasGyro, float Pitch, float Yaw, float Roll, float AccelX, float AccelY, float AccelZ)
        ReadMotionState(IntPtr gamepad)
    {
        var gyro = new float[3];
        var accel = new float[3];

        ReadBreadcrumb("gamepad: reading GYRO sensor data");
        var gyroOk = SdlInterop.GetGamepadSensorData(gamepad, SdlInterop.SensorType.Gyro, gyro, 3);

        ReadBreadcrumb("gamepad: reading ACCEL sensor data");
        var accelOk = SdlInterop.GetGamepadSensorData(gamepad, SdlInterop.SensorType.Accel, accel, 3);

        if (!gyroOk && !accelOk)
        {
            return (false, 0f, 0f, 0f, 0f, 0f, 0f);
        }

        // Index order is SDL's documented contract: [0]=pitch, [1]=yaw, [2]=roll.
        return (gyroOk,
            gyroOk ? gyro[0] : 0f, gyroOk ? gyro[1] : 0f, gyroOk ? gyro[2] : 0f,
            accelOk ? accel[0] : 0f, accelOk ? accel[1] : 0f, accelOk ? accel[2] : 0f);
    }

    /// <summary>
    /// Closes cached slot handles for devices that have been gone long
    /// enough to count as unplugged.
    ///
    /// <para>
    /// The absence must be SUSTAINED. This previously closed a handle the
    /// instant its device was missing from the catalog, and the catalog is
    /// rebuilt from a live SDL enumeration that is briefly incomplete
    /// around any hotplug event. A single such gap closed a perfectly good
    /// handle, the next tick reopened it, and the cycle repeated for as
    /// long as the app ran: measured at four open/close pairs per second,
    /// 5326 of them in one session, which is both real work on the runtime
    /// thread and 4.6% of the log file.
    /// </para>
    ///
    /// <para>
    /// A genuinely unplugged pad is still released, just a couple of
    /// seconds later — which no user can perceive, and which costs far
    /// less than reopening a live device four times a second.
    /// </para>
    /// </summary>
    private void PruneStaleSlotHandles()
    {
        if (slotHandles.Count == 0)
        {
            return;
        }

        var live = new HashSet<string>(
            inputDeviceCatalog.Devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        // An empty enumeration is never trustworthy: SDL reports no devices
        // for a moment while it re-scans. Treating that as "everything was
        // unplugged" would close every handle at once.
        if (live.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var id in slotHandles.Keys.ToList())
        {
            if (live.Contains(id))
            {
                _ = missingSince.Remove(id);
                _ = unknownDeviceWarned.Remove(id);
                continue;
            }

            if (!missingSince.TryGetValue(id, out var firstMissedUtc))
            {
                missingSince[id] = now;
                continue;
            }

            if (now - firstMissedUtc >= StaleHandleGrace)
            {
                _ = missingSince.Remove(id);
                logger.LogInformation(
                    "Releasing SDL device {DeviceId}: absent from enumeration for {Seconds:F0} s.",
                    id, (now - firstMissedUtc).TotalSeconds);
                CloseSlotHandle(id);
            }
        }
    }

    private void CloseSlotHandle(string deviceId)
    {
        if (!slotHandles.TryGetValue(deviceId, out var device))
        {
            return;
        }

        try
        {
            if (device.Kind == DeviceKind.Gamepad)
            {
                SdlInterop.CloseGamepad(device.Handle);
            }
            else
            {
                SdlInterop.CloseJoystick(device.Handle);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Error closing slot device handle {DeviceId}.", deviceId);
        }
        finally
        {
            _ = slotHandles.Remove(deviceId);
            _ = tracedDeviceIds.Remove(deviceId);
        }
    }

    private void CloseSlotHandles()
    {
        foreach (var id in slotHandles.Keys.ToList())
        {
            CloseSlotHandle(id);
        }

        // Telemetry-only handles are opened by the battery poll and are
        // not owned by any slot, so nothing else would ever release them.
        foreach (var id in telemetryHandles.Keys.ToList())
        {
            if (!telemetryHandles.Remove(id, out var device))
            {
                continue;
            }

            try
            {
                if (device.Kind == DeviceKind.Gamepad)
                {
                    SdlInterop.CloseGamepad(device.Handle);
                }
                else
                {
                    SdlInterop.CloseJoystick(device.Handle);
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Error closing telemetry handle {DeviceId} on shutdown.", id);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // Stop the SDL worker before touching any SDL state. If the worker
        // is wedged inside a blocking native call (Bluetooth HID), do NOT
        // wait on it — skip SDL cleanup entirely so shutdown stays instant;
        // process teardown reclaims the native resources.
        stopRequested = true;
        stallWatchdog?.Dispose();
        var workerThread = worker;
        if (workerThread is not null && workerThread.IsAlive && !workerThread.Join(TimeSpan.FromSeconds(2)))
        {
            logger.LogWarning(
                "SDL worker did not stop within 2 s (stuck in '{Operation}'); skipping SDL cleanup for fast shutdown.",
                currentOperation);
            disposed = true;
            return ValueTask.CompletedTask;
        }

        lock (syncRoot)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            CloseRawInspectionHandle();
            CloseSlotHandles();
            CloseOpenedDevice();

            if (sdlInitialized)
            {
                try
                {
                    SdlInterop.QuitSubSystem(SdlInterop.InitGamepad | SdlInterop.InitJoystick);
                    SdlInterop.Quit();
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "SDL3 shutdown reported an error.");
                }

                sdlInitialized = false;
            }
        }

        inputDeviceCatalog.Clear("ProviderStatus_NoActiveProvider");
        return ValueTask.CompletedTask;
    }

    // ─── Crash breadcrumbs ─────────────────────────────────────────────────
    //
    // A misbehaving controller can crash the SDL native layer while we read
    // it — an access violation inside SDL3.dll that a managed try/catch
    // cannot intercept. To find WHICH call dies, we append a line to a small
    // file (opened, written and closed each time, so it's flushed to disk)
    // immediately before each read phase. After a crash, the LAST line in
    // that file names the call that took the process down. Gated per device
    // via tracedDeviceIds so we only trace until each device's first full
    // read survives — otherwise this would hammer the disk at the 250 Hz
    // poll rate (which is exactly what the per-slot read path used to do).
    // Device ids whose first full read has already been traced. Once a
    // device's first read survives we stop breadcrumbing IT, so the log
    // never grows past the handful of lines that matter for a crash — in
    // either the single-pipeline path OR the per-slot path. An id is removed
    // when its device is closed, so a reconnect is traced afresh.
    private readonly HashSet<string> tracedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    // The device whose read is currently in flight; this is what
    // ReadBreadcrumb gates against. Set at the top of each read path.
    private string? activeTraceDeviceId;
    private static bool processStartBreadcrumbWritten;
    // Absolute ceiling on breadcrumb lines per process. A crash trace only
    // needs the last handful of calls, so if anything ever crosses this we
    // stop writing entirely — a runaway trace can then never stall the
    // runtime loop. Normal operation writes only a few dozen lines; this is
    // a safety net independent of the per-device gate.
    private const int MaxBreadcrumbLines = 1000;
    private static int breadcrumbLineCount;
    private static readonly string ReadBreadcrumbPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GAMEFLOW", "read-crash-breadcrumb.log");

    private void ReadBreadcrumb(string step)
    {
        // Always write a one-time process-start marker so runs are easy to
        // tell apart in an appended log (important for crash-loop cases).
        if (!processStartBreadcrumbWritten)
        {
            processStartBreadcrumbWritten = true;
            try
            {
                System.IO.File.AppendAllText(
                    ReadBreadcrumbPath,
                    $"{Environment.NewLine}========== process start {DateTime.Now:yyyy-MM-dd HH:mm:ss} =========={Environment.NewLine}");
            }
            catch
            {
                // best-effort
            }
        }

        // Suppress once THIS device's first full read has survived. The
        // process-start marker above is always written; per-device tracing
        // stops here so a mapped controller can't hammer the disk at the
        // 250 Hz poll rate (the slot path used to leak exactly that way:
        // ~20 File.AppendAllText calls every tick, all under syncRoot).
        if (activeTraceDeviceId is not null && tracedDeviceIds.Contains(activeTraceDeviceId))
        {
            return;
        }

        // Belt-and-suspenders: even if some future path leaks the gate above,
        // never let the trace exceed a hard line budget per process. Once
        // crossed, go silent so disk I/O can't freeze the runtime loop.
        if (System.Threading.Volatile.Read(ref breadcrumbLineCount) > MaxBreadcrumbLines)
        {
            return;
        }
        try
        {
            System.IO.File.AppendAllText(
                ReadBreadcrumbPath,
                $"{DateTime.Now:HH:mm:ss.fff}  {step}{Environment.NewLine}");
            System.Threading.Interlocked.Increment(ref breadcrumbLineCount);
        }
        catch
        {
            // Breadcrumbs are best-effort diagnostics; never let them throw.
        }
    }

    private ControllerSnapshot ReadSnapshotCore()
    {
        var devices = inputDeviceCatalog.Devices;
        if (devices.Count == 0)
        {
            CloseOpenedDevice();
            inputDeviceCatalog.SetProviderStatus("ProviderStatus_SdlNoGamepads");
            return ControllerSnapshot.Empty("No SDL3 device detected") with { Timestamp = DateTimeOffset.UtcNow };
        }

        var selected = ResolveSelectedDevice(devices);
        if (selected is null)
        {
            CloseOpenedDevice();
            inputDeviceCatalog.SetProviderStatus("ProviderStatus_NoControllerSelected");
            return ControllerSnapshot.Empty("Select a controller") with { Timestamp = DateTimeOffset.UtcNow };
        }

        // Point the breadcrumb gate at the device about to be read, then —
        // via the finally below — mark it traced no matter HOW this tick
        // exits (opened, failed to open, read cleanly, or threw). Without
        // that guarantee, a selected device SDL can't open (e.g. a raw-input
        // mouse/keyboard chosen for the preview) returns early before being
        // marked traced, the gate never closes, and the breadcrumb storms
        // 'active device changed' / 'opening device' every tick at the poll
        // rate — flooding the disk under syncRoot and freezing the app.
        activeTraceDeviceId = selected.Id;
        var alreadyTraced = tracedDeviceIds.Contains(selected.Id);
        try
        {
            if (!alreadyTraced)
            {
                ReadBreadcrumb($"==== active device changed -> '{selected.DisplayName}' ({selected.Id}) ====");
            }

            ReadBreadcrumb($"opening device '{selected.DisplayName}' id={selected.Id}");
            if (!EnsureDeviceOpened(selected))
            {
                return ControllerSnapshot.Empty(selected.DisplayName) with { Timestamp = DateTimeOffset.UtcNow };
            }
            ReadBreadcrumb($"opened OK, kind={openedDevice!.Kind} handle={openedDevice!.Handle:X} — reading…");

            try
            {
                return openedDevice!.Kind == DeviceKind.Gamepad
                    ? ReadGamepadSnapshot(openedDevice)
                    : ReadJoystickSnapshot(openedDevice);
            }
            catch (Exception exception)
            {
                // A managed exception reading the active device must NOT tear
                // down the runtime loop — otherwise a saved profile pointing
                // at a problematic device crash-loops on startup. Log it,
                // breadcrumb it, and fall back to an empty snapshot so the
                // app stays alive.
                ReadBreadcrumb($"READ EXCEPTION {exception.GetType().Name}: {exception.Message}");
                logger.LogError(exception, "Failed to read snapshot for {Device}.", openedDevice!.DisplayName);
                inputDeviceCatalog.SetProviderStatus($"Could not read {openedDevice!.DisplayName}; input disabled for it.");
                return ControllerSnapshot.Empty(openedDevice!.DisplayName) with { Timestamp = DateTimeOffset.UtcNow };
            }
        }
        finally
        {
            // First read attempt for this device is complete — close the gate
            // so the trace can never repeat for it at the poll rate, whatever
            // path we exited through.
            tracedDeviceIds.Add(selected.Id);
        }
    }

    private InputDeviceInfo? ResolveSelectedDevice(IReadOnlyList<InputDeviceInfo> devices)
    {
        var selectedId = inputDeviceCatalog.SelectedDeviceId;
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var selected = devices.FirstOrDefault(device => string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return devices[0];
    }

    private bool EnsureDeviceOpened(InputDeviceInfo device)
    {
        if (openedDevice is not null && string.Equals(openedDevice.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        CloseOpenedDevice();

        if (!TryParseDeviceId(device.Id, out var kind, out var instanceId))
        {
            // Not an SDL device id (e.g. a "rawinput-*" keyboard/mouse the
            // user selected). This source simply doesn't own that device —
            // that's expected, not an error, so we stay quiet instead of
            // flashing "Unable to parse…" in the status line.
            if (loggedNonSdlSkips.Add(device.Id))
            {
                logger.LogDebug(
                    "SDL source skipping non-SDL device id '{DeviceId}' (logged once per device).", device.Id);
            }
            return false;
        }

        currentOperation = "opening " + device.Id;
        logger.LogInformation("Opening SDL device {DeviceId} (kind {Kind})…", device.Id, kind);
        var openTimer = System.Diagnostics.Stopwatch.StartNew();

        var handle = kind == DeviceKind.Gamepad
            ? SdlInterop.OpenGamepad(instanceId)
            : SdlInterop.OpenJoystick(instanceId);

        openTimer.Stop();
        currentOperation = "idle";
        logger.LogInformation(
            "SDL device {DeviceId} open finished in {ElapsedMs} ms (handle {Ok}).",
            device.Id, openTimer.ElapsedMilliseconds, handle != IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            inputDeviceCatalog.SetProviderStatus($"Failed to open {device.DisplayName}: {SdlInterop.GetError()}");
            return false;
        }

        openedDevice = new OpenedDevice(device.Id, device.DisplayName, instanceId, kind, handle);
        return true;
    }

    /// <summary>
    /// Overrides the canonical button states with the device's saved
    /// remap, reading each mapped button from its raw joystick index.
    /// No-op when the device has no map. Lets controllers whose buttons
    /// are recognized in a different order be normalized.
    /// </summary>
    private void ApplyButtonMap(OpenedDevice device, Dictionary<ButtonId, bool> buttons)
    {
        var map = buttonMapStore.GetOrNull(device.DeviceId);
        if (map is null || map.Buttons.Count == 0)
        {
            return;
        }

        var joystick = device.Kind == DeviceKind.Gamepad
            ? SdlInterop.GetGamepadJoystick(device.Handle)
            : device.Handle;
        if (joystick == IntPtr.Zero)
        {
            return;
        }

        foreach (var (buttonId, rawIndex) in map.Buttons)
        {
            buttons[buttonId] = SdlInterop.GetJoystickButton(joystick, rawIndex);
        }
    }

    private ControllerSnapshot ReadGamepadSnapshot(OpenedDevice device)
    {
        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());

        ReadBreadcrumb("gamepad: reading buttons");
        buttons[ButtonId.South] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.South);
        buttons[ButtonId.East] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.East);
        buttons[ButtonId.West] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.West);
        buttons[ButtonId.North] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.North);
        buttons[ButtonId.LeftShoulder] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.LeftShoulder);
        buttons[ButtonId.RightShoulder] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.RightShoulder);
        buttons[ButtonId.Back] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.Back);
        buttons[ButtonId.Start] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.Start);
        buttons[ButtonId.Guide] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.Guide);
        buttons[ButtonId.LeftStick] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.LeftStick);
        buttons[ButtonId.RightStick] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.RightStick);
        buttons[ButtonId.DpadUp] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.DpadUp);
        buttons[ButtonId.DpadDown] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.DpadDown);
        buttons[ButtonId.DpadLeft] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.DpadLeft);
        buttons[ButtonId.DpadRight] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.DpadRight);
        buttons[ButtonId.Paddle1] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.LeftPaddle1);
        buttons[ButtonId.Paddle2] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.RightPaddle1);
        buttons[ButtonId.Paddle3] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.LeftPaddle2);
        buttons[ButtonId.Paddle4] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.RightPaddle2);
        buttons[ButtonId.Touchpad] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.Touchpad);
        buttons[ButtonId.Misc1] = SdlInterop.GetGamepadButton(device.Handle, SdlInterop.GamepadButton.Misc1);

        ReadBreadcrumb("gamepad: reading triggers");
        var leftTrigger = NormalizeGamepadTrigger(SdlInterop.GetGamepadAxis(device.Handle, SdlInterop.GamepadAxis.LeftTrigger));
        var rightTrigger = NormalizeGamepadTrigger(SdlInterop.GetGamepadAxis(device.Handle, SdlInterop.GamepadAxis.RightTrigger));

        buttons[ButtonId.LeftTriggerButton] = leftTrigger >= 0.65f;
        buttons[ButtonId.RightTriggerButton] = rightTrigger >= 0.65f;

        ReadBreadcrumb("gamepad: reading TOUCHPAD (DualSense-specific)");
        var touchContacts = ReadTouchState(device.Handle);
        ReadBreadcrumb("gamepad: touchpad read returned OK");

        var motion = ReadMotionState(device.Handle);
        ReadBreadcrumb("gamepad: motion read returned OK");
        if (touchContacts.Count > 0)
        {
            buttons[ButtonId.Touchpad] = true;
        }

        inputDeviceCatalog.SetProviderStatus($"Using SDL3 mapped gamepad: {device.DisplayName}.");

        ApplyButtonMap(device, buttons);

        ReadBreadcrumb("gamepad: reading sticks + vendor/product");
        var vendorId  = SdlInterop.GetGamepadVendor(device.Handle);
        var productId = SdlInterop.GetGamepadProduct(device.Handle);
        var leftStick = new StickVector(
            NormalizeSignedAxis(SdlInterop.GetGamepadAxis(device.Handle, SdlInterop.GamepadAxis.LeftX)),
            -NormalizeSignedAxis(SdlInterop.GetGamepadAxis(device.Handle, SdlInterop.GamepadAxis.LeftY))).Clamp();
        var rightStick = new StickVector(
            NormalizeSignedAxis(SdlInterop.GetGamepadAxis(device.Handle, SdlInterop.GamepadAxis.RightX)),
            -NormalizeSignedAxis(SdlInterop.GetGamepadAxis(device.Handle, SdlInterop.GamepadAxis.RightY))).Clamp();

        // Disambiguates the freeze. If a log ends at "reading sticks +
        // vendor/product" with no line below, the stall is in one of the SDL
        // gamepad reads just above — i.e. blocked on the SDL joystick lock during
        // a Bluetooth transfer. If "snapshot built OK" appears, the read path is
        // healthy and any remaining stall is downstream (pipeline / output / LED).
        ReadBreadcrumb("gamepad: snapshot built OK");

        return new ControllerSnapshot
        {
            DeviceName = device.DisplayName,
            VendorId   = vendorId,
            ProductId  = productId,
            LeftStick = leftStick,
            RightStick = rightStick,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
            HasGyro = motion.HasGyro,
            GyroPitch = motion.Pitch,
            GyroYaw = motion.Yaw,
            GyroRoll = motion.Roll,
            AccelX = motion.AccelX,
            AccelY = motion.AccelY,
            AccelZ = motion.AccelZ,
            Buttons = buttons,
            Timestamp = DateTimeOffset.UtcNow
        }.WithTouchContacts(touchContacts);
    }

    private ControllerSnapshot ReadJoystickSnapshot(OpenedDevice device)
    {
        var buttons = ButtonState.Clone(ButtonState.CreateEmptyMap());
        var buttonCount = Math.Max(0, SdlInterop.GetNumJoystickButtons(device.Handle));
        var axisCount = Math.Max(0, SdlInterop.GetNumJoystickAxes(device.Handle));
        var hatCount = Math.Max(0, SdlInterop.GetNumJoystickHats(device.Handle));

        if (buttonCount > 0)
        {
            buttons[ButtonId.South] = SdlInterop.GetJoystickButton(device.Handle, 0);
        }

        if (buttonCount > 1)
        {
            buttons[ButtonId.East] = SdlInterop.GetJoystickButton(device.Handle, 1);
        }

        if (buttonCount > 2)
        {
            buttons[ButtonId.West] = SdlInterop.GetJoystickButton(device.Handle, 2);
        }

        if (buttonCount > 3)
        {
            buttons[ButtonId.North] = SdlInterop.GetJoystickButton(device.Handle, 3);
        }

        if (buttonCount > 4)
        {
            buttons[ButtonId.LeftShoulder] = SdlInterop.GetJoystickButton(device.Handle, 4);
        }

        if (buttonCount > 5)
        {
            buttons[ButtonId.RightShoulder] = SdlInterop.GetJoystickButton(device.Handle, 5);
        }

        if (buttonCount > 6)
        {
            buttons[ButtonId.Back] = SdlInterop.GetJoystickButton(device.Handle, 6);
        }

        if (buttonCount > 7)
        {
            buttons[ButtonId.Start] = SdlInterop.GetJoystickButton(device.Handle, 7);
        }

        if (buttonCount > 8)
        {
            buttons[ButtonId.LeftStick] = SdlInterop.GetJoystickButton(device.Handle, 8);
        }

        if (buttonCount > 9)
        {
            buttons[ButtonId.RightStick] = SdlInterop.GetJoystickButton(device.Handle, 9);
        }

        if (buttonCount > 10)
        {
            buttons[ButtonId.Guide] = SdlInterop.GetJoystickButton(device.Handle, 10);
        }

        if (buttonCount > 11)
        {
            buttons[ButtonId.Misc1] = SdlInterop.GetJoystickButton(device.Handle, 11);
        }

        if (hatCount > 0)
        {
            var hat = SdlInterop.GetJoystickHat(device.Handle, 0);
            buttons[ButtonId.DpadUp] = (hat & SdlInterop.HatUp) == SdlInterop.HatUp;
            buttons[ButtonId.DpadRight] = (hat & SdlInterop.HatRight) == SdlInterop.HatRight;
            buttons[ButtonId.DpadDown] = (hat & SdlInterop.HatDown) == SdlInterop.HatDown;
            buttons[ButtonId.DpadLeft] = (hat & SdlInterop.HatLeft) == SdlInterop.HatLeft;
        }

        var leftTrigger = axisCount > 4
            ? NormalizePositiveHalfAxis(SdlInterop.GetJoystickAxis(device.Handle, 4))
            : 0f;
        var rightTrigger = axisCount > 5
            ? NormalizePositiveHalfAxis(SdlInterop.GetJoystickAxis(device.Handle, 5))
            : 0f;

        buttons[ButtonId.LeftTriggerButton] = leftTrigger >= 0.65f;
        buttons[ButtonId.RightTriggerButton] = rightTrigger >= 0.65f;

        inputDeviceCatalog.SetProviderStatus($"Using SDL3 generic joystick or HID fallback: {device.DisplayName}. Mapping is provisional until the binding editor is finished.");

        ApplyButtonMap(device, buttons);

        return new ControllerSnapshot
        {
            DeviceName = device.DisplayName,
            VendorId   = SdlInterop.GetJoystickVendor(device.Handle),
            ProductId  = SdlInterop.GetJoystickProduct(device.Handle),
            LeftStick = new StickVector(
                axisCount > 0 ? NormalizeSignedAxis(SdlInterop.GetJoystickAxis(device.Handle, 0)) : 0f,
                axisCount > 1 ? -NormalizeSignedAxis(SdlInterop.GetJoystickAxis(device.Handle, 1)) : 0f).Clamp(),
            RightStick = new StickVector(
                axisCount > 2 ? NormalizeSignedAxis(SdlInterop.GetJoystickAxis(device.Handle, 2)) : 0f,
                axisCount > 3 ? -NormalizeSignedAxis(SdlInterop.GetJoystickAxis(device.Handle, 3)) : 0f).Clamp(),
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
            TouchContactCount = 0,
            Buttons = buttons,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Reads every touchpad/finger slot the hardware reports and returns
    /// one <see cref="TouchContact"/> per finger currently down.
    /// SDL_GetGamepadTouchpadFinger already returns x/y/pressure; this
    /// used to discard all three (`out _, out _, out _`) and keep only
    /// the down flag, then later kept just the first finger's position.
    ///
    /// <para>
    /// Finger slots are numbered ACROSS touchpads, not per touchpad, so
    /// a pad with two surfaces can't collide two different fingers onto
    /// the same <see cref="TouchContact.FingerIndex"/> — the recognizer
    /// tracks fingers by that index, and a collision would read as one
    /// finger teleporting between surfaces.
    /// </para>
    /// </summary>
    private List<TouchContact> ReadTouchState(IntPtr gamepad)
    {
        ReadBreadcrumb("touchpad: GetNumGamepadTouchpads");
        var touchpads = Math.Max(0, SdlInterop.GetNumGamepadTouchpads(gamepad));
        var contacts = new List<TouchContact>(2);
        var slotBase = 0;

        for (var touchpadIndex = 0; touchpadIndex < touchpads; touchpadIndex++)
        {
            ReadBreadcrumb($"touchpad: GetNumGamepadTouchpadFingers (tp {touchpadIndex})");
            var fingers = Math.Max(0, SdlInterop.GetNumGamepadTouchpadFingers(gamepad, touchpadIndex));
            for (var fingerIndex = 0; fingerIndex < fingers; fingerIndex++)
            {
                ReadBreadcrumb($"touchpad: GetGamepadTouchpadFinger (tp {touchpadIndex}, finger {fingerIndex})");
                if (!SdlInterop.GetGamepadTouchpadFinger(gamepad, touchpadIndex, fingerIndex, out var down, out var x, out var y, out var pressure))
                {
                    continue;
                }

                if (down != 0)
                {
                    contacts.Add(new TouchContact(slotBase + fingerIndex, x, y, pressure));
                }
            }

            slotBase += fingers;
        }

        return contacts;
    }

    private void RefreshDeviceCatalog()
    {
        var devices = new List<InputDeviceInfo>();
        var liveMap = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        // Same (vid,pid,name) signature can legitimately repeat — two
        // identical controller models plugged in at once. Disambiguated
        // with an ordinal suffix so both get distinct, if not perfectly
        // reconnect-order-stable, ids rather than colliding into one.
        var seenSignatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        string BuildStableId(string kindTag, ushort vid, ushort pid, string name)
        {
            var signature = $"{vid:X4}:{pid:X4}:{name}";
            var ordinal = seenSignatureCounts.TryGetValue(signature, out var count) ? count + 1 : 1;
            seenSignatureCounts[signature] = ordinal;
            var suffix = ordinal > 1 ? $"-{ordinal}" : string.Empty;
            return $"sdl-{kindTag}-{StableHash(signature)}{suffix}";
        }

        var gamepadsPointer = SdlInterop.GetGamepads(out var gamepadCount);
        try
        {
            for (var index = 0; index < gamepadCount; index++)
            {
                var instanceId = ReadJoystickId(gamepadsPointer, index);
                var name = SdlInterop.ReadString(SdlInterop.GetGamepadNameForIdPointer(instanceId));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"SDL Gamepad {instanceId}";
                }

                var vid = SdlInterop.GetGamepadVendorForId(instanceId);
                var pid = SdlInterop.GetGamepadProductForId(instanceId);
                var gamepadId = BuildStableId("gamepad", vid, pid, name);
                liveMap[gamepadId] = instanceId;
                var power = GetCachedPower(gamepadId);

                // A virtual pad impersonates real hardware down to VID/PID,
                // so the OS path is the only thing that gives it away.
                var gamepadPath = SdlInterop.ReadString(SdlInterop.GetGamepadPathForIdPointer(instanceId));

                devices.Add(new InputDeviceInfo(
                    gamepadId,
                    StableName(gamepadId, name),
                    true,
                    false,
                    vid,
                    pid,
                    true,
                    DeviceCategory.Gamepad,
                    power.Percentage,
                    power.State,
                    VirtualDeviceIdentity.IsVirtual(gamepadPath, serial: null)));
            }
        }
        finally
        {
            if (gamepadsPointer != IntPtr.Zero)
            {
                SdlInterop.Free(gamepadsPointer);
            }
        }

        var joysticksPointer = SdlInterop.GetJoysticks(out var joystickCount);
        try
        {
            for (var index = 0; index < joystickCount; index++)
            {
                var instanceId = ReadJoystickId(joysticksPointer, index);
                if (SdlInterop.IsGamepad(instanceId))
                {
                    continue;
                }

                var name = SdlInterop.ReadString(SdlInterop.GetJoystickNameForIdPointer(instanceId));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"SDL Joystick {instanceId}";
                }

                var vid = SdlInterop.GetJoystickVendorForId(instanceId);
                var pid = SdlInterop.GetJoystickProductForId(instanceId);
                var joystickId = BuildStableId("joystick", vid, pid, name);
                liveMap[joystickId] = instanceId;
                var power = GetCachedPower(joystickId);

                var joystickPath = SdlInterop.ReadString(SdlInterop.GetJoystickPathForIdPointer(instanceId));

                devices.Add(new InputDeviceInfo(
                    joystickId,
                    StableName(joystickId, name),
                    true,
                    false,
                    vid,
                    pid,
                    false,
                    DeviceCategory.Joystick,
                    power.Percentage,
                    power.State,
                    VirtualDeviceIdentity.IsVirtual(joystickPath, serial: null)));
            }
        }
        finally
        {
            if (joysticksPointer != IntPtr.Zero)
            {
                SdlInterop.Free(joysticksPointer);
            }
        }

        // Rebuilt in place — safe because RefreshDeviceCatalog and every
        // TryParseDeviceId caller run on the single SDL worker thread
        // under the same lock, never concurrently.
        liveInstanceIdByStableId.Clear();
        foreach (var (id, instanceId) in liveMap)
        {
            liveInstanceIdByStableId[id] = instanceId;
        }

        // Drop cached names for devices that are no longer present, so a
        // reconnect re-captures a fresh name.
        if (stableNames.Count > 0)
        {
            var liveIds = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var staleId in stableNames.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                stableNames.Remove(staleId);
            }
        }

        if (powerByDeviceId.Count > 0)
        {
            var liveIds = devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleId in powerByDeviceId.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                powerByDeviceId.Remove(staleId);
            }
        }

        inputDeviceCatalog.ReplaceDevices("sdl", devices);

        if (devices.Count == 0)
        {
            inputDeviceCatalog.SetProviderStatus("ProviderStatus_SdlNoGamepads");
            return;
        }

        // ProviderStatus_SdlActive's translation is "SDL3 unified input active — {0} gamepad(s) detected"
        inputDeviceCatalog.SetProviderStatus("ProviderStatus_SdlActive", devices.Count);
    }

    /// <summary>
    /// Samples battery state from devices the worker already has open. It
    /// deliberately does not open every unassigned controller just to read
    /// charge: Bluetooth opens are the most failure-prone SDL operation in
    /// this process. Selected and slot-assigned controllers already have a
    /// safe worker-owned handle, which covers the devices users are actively
    /// working with without adding a new freeze path.
    /// </summary>
    /// <summary>
    /// Applies rumble and LED state posted by the effects thread.
    ///
    /// <para>
    /// This runs on the SDL worker thread ON PURPOSE. SDL takes its device
    /// lock for the duration of a write, and over Bluetooth that write is
    /// a blocking HID transfer of a millisecond or more. Performed from
    /// any other thread it contends with this loop's own reads, which is
    /// how effects froze the runtime when they were last wired up and why
    /// the calls were deleted. Here the cost lands on the thread that was
    /// going to be busy with that device anyway.
    /// </para>
    ///
    /// <para>
    /// The effects thread has already coalesced and rate-limited, so what
    /// arrives here is at most one write per device per pass.
    /// </para>
    /// </summary>
    private void ApplyPendingEffects()
    {
        if (effectMailbox is null || !effectMailbox.TryCollect(out var writes))
        {
            return;
        }

        foreach (var write in writes)
        {
            if (!slotHandles.TryGetValue(write.DeviceId, out var device) &&
                !telemetryHandles.TryGetValue(write.DeviceId, out device))
            {
                continue;
            }

            // Gamepad-only: the LED and rumble entry points are the gamepad
            // API, and a raw joystick has neither.
            if (device.Kind != DeviceKind.Gamepad)
            {
                continue;
            }

            currentOperation = "effects " + write.DeviceId;

            try
            {
                var state = write.State;

                // Duration 0 means "until told otherwise" in SDL. That is
                // what we want: the effects thread owns when this stops,
                // and a timeout here would cut a sustained rumble short.
                _ = SdlInterop.RumbleGamepad(
                    device.Handle,
                    ToRumbleMagnitude(state.LowFrequencyRumble),
                    ToRumbleMagnitude(state.HighFrequencyRumble),
                    0);

                if (state.LedColor is { } led)
                {
                    _ = SdlInterop.SetGamepadLED(device.Handle, led.R, led.G, led.B);
                }
            }
            catch (Exception exception)
            {
                // A pad unplugged mid-write is ordinary. One bad device
                // must not stop the others, and must not kill the loop.
                logger.LogDebug(exception, "Effect write to {DeviceId} failed.", write.DeviceId);
            }
        }

        currentOperation = "idle";
    }

    private static ushort ToRumbleMagnitude(double value) =>
        (ushort)Math.Clamp(Math.Round(value * ushort.MaxValue), 0, ushort.MaxValue);

    private void RefreshPowerStateCache(DateTime utcNow)
    {
        if (utcNow < nextPowerRefreshUtc)
        {
            return;
        }

        // Poll fast until every known device has answered once, then back
        // off. A charge level changes over minutes, so 5 s is plenty for
        // steady state — but that interval also governed the FIRST
        // reading, so a freshly connected pad showed nothing for up to
        // five seconds, which reads as the feature being broken. The
        // catch-up rate costs one cheap call per device.
        var everyoneAnswered = powerByDeviceId.Count > 0
            && inputDeviceCatalog.Devices
                .Where(d => d.IsGamepad || d.Category == DeviceCategory.Joystick)
                .All(d => powerByDeviceId.ContainsKey(d.Id));

        nextPowerRefreshUtc = utcNow.AddMilliseconds(everyoneAnswered ? 5000 : 400);

        var opened = new Dictionary<string, OpenedDevice>(StringComparer.OrdinalIgnoreCase);
        if (openedDevice is not null)
        {
            opened[openedDevice.DeviceId] = openedDevice;
        }
        foreach (var device in slotHandles.Values)
        {
            opened[device.DeviceId] = device;
        }

        // Battery for devices that are connected but not assigned to a
        // slot.
        //
        // SDL only reports power through an OPEN handle — there is no
        // query by instance id — and until now the only handles that
        // existed were the ones slots had opened. So a pad sitting
        // connected and unassigned reported no battery at all, which is
        // every pad on a machine whose slots are demo previews. That is
        // exactly the case the Devices page exists to show.
        //
        // The handles are opened once and KEPT. Opening per refresh would
        // reintroduce the open/release churn that cost four SDL opens a
        // second before it was fixed; SDL refcounts opens of the same
        // instance, so holding one here does not disturb a slot opening
        // the same pad.
        foreach (var info in inputDeviceCatalog.Devices)
        {
            if (opened.ContainsKey(info.Id) || !(info.IsGamepad || info.Category == DeviceCategory.Joystick))
            {
                continue;
            }

            if (telemetryHandles.TryGetValue(info.Id, out var existing))
            {
                opened[info.Id] = existing;
                continue;
            }

            if (!TryParseDeviceId(info.Id, out var telemetryKind, out var telemetryInstance))
            {
                continue;
            }

            var handle = telemetryKind == DeviceKind.Gamepad
                ? SdlInterop.OpenGamepad(telemetryInstance)
                : SdlInterop.OpenJoystick(telemetryInstance);

            if (handle == IntPtr.Zero)
            {
                continue;
            }

            var telemetry = new OpenedDevice(info.Id, info.DisplayName, telemetryInstance, telemetryKind, handle);
            telemetryHandles[info.Id] = telemetry;
            opened[info.Id] = telemetry;
            logger.LogDebug("Opened {DeviceId} for battery telemetry (not assigned to a slot).", info.Id);
        }

        CloseOrphanedTelemetryHandles();

        foreach (var device in opened.Values)
        {
            try
            {
                int percent;
                var nativeState = device.Kind == DeviceKind.Gamepad
                    ? SdlInterop.GetGamepadPowerInfo(device.Handle, out percent)
                    : SdlInterop.GetJoystickPowerInfo(device.Handle, out percent);

                var state = nativeState switch
                {
                    SdlInterop.PowerState.OnBattery => DeviceBatteryState.OnBattery,
                    SdlInterop.PowerState.Charging => DeviceBatteryState.Charging,
                    SdlInterop.PowerState.Charged => DeviceBatteryState.Charged,
                    SdlInterop.PowerState.NoBattery => DeviceBatteryState.NoBattery,
                    _ => DeviceBatteryState.Unknown,
                };

                int? percentage = percent is >= 0 and <= 100
                    ? percent
                    : state == DeviceBatteryState.Charged ? 100 : null;

                // Log the first real reading per device, and any state
                // change after that (on battery -> charging -> charged).
                // Percentage alone is deliberately not logged on change:
                // it moves constantly and would be noise.
                var previous = GetCachedPower(device.DeviceId);
                if (previous.State != state)
                {
                    logger.LogInformation(
                        "Battery for {DeviceName}: {State}{Percent}.",
                        device.DisplayName,
                        state,
                        percentage is null ? string.Empty : $" · {percentage}%");
                }

                powerByDeviceId[device.DeviceId] = new DevicePowerInfo(percentage, state);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "SDL battery query failed for {DeviceId}.", device.DeviceId);
            }
        }
    }

    /// <summary>
    /// Releases telemetry-only handles for devices that have left the
    /// catalog, so unplugging a pad does not leak its handle for the life
    /// of the process.
    /// </summary>
    private void CloseOrphanedTelemetryHandles()
    {
        if (telemetryHandles.Count == 0)
        {
            return;
        }

        var live = new HashSet<string>(
            inputDeviceCatalog.Devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        // Same reasoning as PruneStaleSlotHandles: an empty enumeration is
        // a re-scan in progress, not every device vanishing at once.
        if (live.Count == 0)
        {
            return;
        }

        foreach (var id in telemetryHandles.Keys.ToList())
        {
            if (live.Contains(id))
            {
                continue;
            }

            if (telemetryHandles.Remove(id, out var stale))
            {
                try
                {
                    if (stale.Kind == DeviceKind.Gamepad)
                    {
                        SdlInterop.CloseGamepad(stale.Handle);
                    }
                    else
                    {
                        SdlInterop.CloseJoystick(stale.Handle);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Error closing telemetry handle {DeviceId}.", id);
                }

                _ = powerByDeviceId.Remove(id);
            }
        }
    }

    private DevicePowerInfo GetCachedPower(string deviceId) =>
        powerByDeviceId.TryGetValue(deviceId, out var power)
            ? power
            : DevicePowerInfo.Unknown;

    /// <summary>
    /// Returns a stable display name for a device id: the first non-empty
    /// name seen wins and is reused on every later poll, so SDL handing
    /// back a slightly different string each tick can't flicker the label.
    /// </summary>
    private string StableName(string deviceId, string freshName)
    {
        if (stableNames.TryGetValue(deviceId, out var cached))
        {
            return cached;
        }

        if (!string.IsNullOrWhiteSpace(freshName))
        {
            stableNames[deviceId] = freshName;
        }

        return freshName;
    }

    private void TryLoadOptionalMappings()
    {
        try
        {
            var mappingFile = Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb.txt");
            if (!File.Exists(mappingFile))
            {
                return;
            }

            var added = SdlInterop.AddGamepadMappingsFromFile(mappingFile);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Loaded {MappingCount} SDL3 gamepad mapping entries from {MappingFile}.", added, mappingFile);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Optional SDL3 gamepad mappings could not be loaded.");
        }
    }

    /// <summary>
    /// Polls raw axis/button/hat state for the single device the Devices
    /// view is inspecting (if any) and publishes it to the catalog. Runs
    /// inside the SDL pump lock, so SDL state is coherent. The target is
    /// opened as a *joystick* to expose the physical layout directly,
    /// independent of any gamepad mapping; this can be the same device
    /// that is also open as the active gamepad (SDL ref-counts handles).
    /// </summary>
    private void PollRawInspection()
    {
        var targetId = inputDeviceCatalog.RawInspectionTargetId;

        // Nothing to inspect — release any handle we held.
        if (string.IsNullOrWhiteSpace(targetId))
        {
            if (rawInspectionHandle != IntPtr.Zero)
            {
                CloseRawInspectionHandle();
            }
            return;
        }

        // If the device being inspected is the SAME one the runtime already
        // has open as its active input, DO NOT open a second SDL handle on
        // it. Opening one physical device twice in the same process (once as
        // a gamepad for the runtime, once as a joystick here) on the SDL
        // thread crashed the app on gamepad selection. Instead, derive the
        // joystick handle from the runtime's existing gamepad handle — the
        // same trick ApplyButtonMap uses — so there is only ever one open.
        IntPtr joystick;
        if (openedDevice is not null &&
            string.Equals(openedDevice.DeviceId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            if (rawInspectionHandle != IntPtr.Zero)
            {
                CloseRawInspectionHandle();
            }
            joystick = openedDevice.Kind == DeviceKind.Gamepad
                ? SdlInterop.GetGamepadJoystick(openedDevice.Handle)
                : openedDevice.Handle;
        }
        else
        {
            // A device the runtime isn't holding — open our own joystick
            // handle, reopening when the target changes. Guarded: a bad
            // handle or a device that vanished mid-open must not crash.
            if (!string.Equals(rawInspectionHandleId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                CloseRawInspectionHandle();

                if (!TryParseDeviceId(targetId, out _, out var instanceId))
                {
                    inputDeviceCatalog.PublishRawInspection(null);
                    return;
                }

                try
                {
                    rawInspectionHandle = SdlInterop.OpenJoystick(instanceId);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "SDL3 raw inspection open failed for {DeviceId}.", targetId);
                    rawInspectionHandle = IntPtr.Zero;
                }

                rawInspectionHandleId = targetId;

                if (rawInspectionHandle == IntPtr.Zero)
                {
                    rawInspectionHandleId = null;
                    inputDeviceCatalog.PublishRawInspection(null);
                    return;
                }
            }

            joystick = rawInspectionHandle;
        }

        if (joystick == IntPtr.Zero)
        {
            inputDeviceCatalog.PublishRawInspection(null);
            return;
        }

        try
        {
            var axisCount = Math.Max(0, SdlInterop.GetNumJoystickAxes(joystick));
            var buttonCount = Math.Max(0, SdlInterop.GetNumJoystickButtons(joystick));
            var hatCount = Math.Max(0, SdlInterop.GetNumJoystickHats(joystick));

            var axes = new short[axisCount];
            for (var i = 0; i < axisCount; i++)
            {
                axes[i] = SdlInterop.GetJoystickAxis(joystick, i);
            }

            var buttons = new bool[buttonCount];
            for (var i = 0; i < buttonCount; i++)
            {
                buttons[i] = SdlInterop.GetJoystickButton(joystick, i);
            }

            var hats = new byte[hatCount];
            for (var i = 0; i < hatCount; i++)
            {
                hats[i] = SdlInterop.GetJoystickHat(joystick, i);
            }

            inputDeviceCatalog.PublishRawInspection(
                new RawDeviceSnapshot(targetId, axes, buttons, hats));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "SDL3 raw inspection read failed for {DeviceId}.", targetId);
            CloseRawInspectionHandle();
            inputDeviceCatalog.PublishRawInspection(null);
        }
    }

    private void CloseRawInspectionHandle()
    {
        if (rawInspectionHandle != IntPtr.Zero)
        {
            try
            {
                SdlInterop.CloseJoystick(rawInspectionHandle);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "SDL3 raw inspection close reported an error.");
            }
        }

        rawInspectionHandle = IntPtr.Zero;
        rawInspectionHandleId = null;
    }

    private void CloseOpenedDevice()
    {
        if (openedDevice is null)
        {
            return;
        }

        try
        {
            if (openedDevice.Kind == DeviceKind.Gamepad)
            {
                SdlInterop.CloseGamepad(openedDevice.Handle);
            }
            else
            {
                SdlInterop.CloseJoystick(openedDevice.Handle);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "SDL3 device close reported an error.");
        }
        finally
        {
            _ = tracedDeviceIds.Remove(openedDevice.DeviceId);
            openedDevice = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, nameof(SdlUnifiedInputSource));
    }

    private static float NormalizeSignedAxis(short value)
    {
        return value == short.MinValue ? -1f : Math.Clamp(value / 32767f, -1f, 1f);
    }

    private static float NormalizePositiveHalfAxis(short value)
    {
        return Math.Clamp(Math.Max(0f, NormalizeSignedAxis(value)), 0f, 1f);
    }

    private static float NormalizeGamepadTrigger(short value)
    {
        return Math.Clamp(value / 32767f, 0f, 1f);
    }

    private static uint ReadJoystickId(IntPtr pointer, int index)
    {
        return pointer == IntPtr.Zero
                ? 0u
                : unchecked((uint)Marshal.ReadInt32(pointer, index * sizeof(int)));
    }

    /// <summary>
    /// Resolves a device id to its kind and CURRENT live SDL instance id.
    /// The id itself is a stable hardware fingerprint (see
    /// RefreshDeviceCatalog's BuildStableId) — it never changes for the
    /// same physical device — so this looks up what that fingerprint
    /// currently maps to rather than decoding a number out of the string.
    /// Returns false both for malformed ids and for a well-formed,
    /// previously-valid id whose device simply isn't connected right now
    /// (the caller can't distinguish "unknown" from "not currently
    /// present," which is fine: both mean "nothing to read").
    /// </summary>
    private bool TryParseDeviceId(string? deviceId, out DeviceKind kind, out uint instanceId)
    {
        kind = DeviceKind.Gamepad;
        instanceId = 0;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        var parts = deviceId.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "sdl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        kind = string.Equals(parts[1], "joystick", StringComparison.OrdinalIgnoreCase)
            ? DeviceKind.Joystick
            : DeviceKind.Gamepad;

        return liveInstanceIdByStableId.TryGetValue(deviceId, out instanceId);
    }

    /// <summary>FNV-1a, lowercase hex — same algorithm used for Raw Input device ids, kept local since it's only needed here.</summary>
    private static string StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash.ToString("x8");
    }

    private enum DeviceKind
    {
        Gamepad,
        Joystick
    }

    private sealed record OpenedDevice(string DeviceId, string DisplayName, uint InstanceId, DeviceKind Kind, IntPtr Handle);

    private readonly record struct DevicePowerInfo(int? Percentage, DeviceBatteryState State)
    {
        public static DevicePowerInfo Unknown { get; } = new(null, DeviceBatteryState.Unknown);
    }
}
