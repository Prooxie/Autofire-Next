using GameFlow.Infrastructure.Localization;

namespace GameFlow.Infrastructure.Runtime;

/// <summary>
/// Catalog of input devices currently visible to the active providers.
/// Multiple sources (SDL3 gamepads, Raw Input keyboards/mice) publish
/// device lists under a source key; the catalog merges them, applies the
/// ignore filters, and raises <see cref="Updated"/> only when the merged
/// view actually changes. Lookups by id are O(1) via <see cref="TryGetById"/>.
/// </summary>
public sealed class InputDeviceCatalog
{
    private readonly ILocalizationService localization;
    private readonly DeviceCategoryOverrideStore categoryOverrides;
    private readonly Lock gate = new();

    private IReadOnlyList<InputDeviceInfo> devices = [];
    private Dictionary<string, InputDeviceInfo> devicesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<InputDeviceInfo>> devicesBySource = new(StringComparer.Ordinal);
    private IReadOnlySet<string> ignoredDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<(ushort Vid, ushort Pid, DateTimeOffset ActivatedAt)> ignoredHardwareSignatures = [];

    /// <summary>
    /// UTC time each device id was first observed this session. Ignore
    /// entries only apply to devices that appeared AT/AFTER their
    /// sink's activation, so a real pad of the same model that was
    /// already connected stays visible while the app's own emitted
    /// device (which enumerates only after creation) is filtered.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> firstSeenById = new(StringComparer.OrdinalIgnoreCase);
    private string? selectedDeviceId;
    private string? rawInspectionTargetId;
    private RawDeviceSnapshot? rawInspection;

    /// <summary>
    /// Provider-status localization key (or English literal — the localizer
    /// falls through to the input string when no resource is found, so input
    /// sources that don't yet have a translated key still display their text).
    /// Resolved through <see cref="ILocalizationService"/> on every read so
    /// that culture changes are reflected immediately without callers having
    /// to push a new value.
    /// </summary>
    private string? statusKey;

    /// <summary>
    /// Format arguments interpolated into the resolved <see cref="statusKey"/>
    /// string when it contains <c>{0}</c>-style placeholders.
    /// </summary>
    private object?[] statusArgs = [];

    /// <summary>
    /// Localization key consulted when <see cref="statusKey"/> is null
    /// (i.e. the catalog has just been cleared or freshly created).
    /// </summary>
    private readonly string defaultStatusKey = "ProviderStatus_NoActiveProvider";

    /// <param name="localization">Localization service used to resolve the catalog's
    /// idle/no-provider status string.</param>
    /// <param name="categoryOverrides">Saved user overrides applied to detected device categories.</param>
    public InputDeviceCatalog(ILocalizationService localization, DeviceCategoryOverrideStore categoryOverrides)
    {
        this.localization = localization;
        this.localization.CultureChanged += OnCultureChanged;
        this.categoryOverrides = categoryOverrides;
        this.categoryOverrides.OverrideChanged += (_, _) => ReapplyOverrides();
    }

    /// <summary>Raised whenever the catalog's device list, status, or selection changes.</summary>
    public event EventHandler? Updated;

    /// <summary>
    /// Raised when a fresh raw-inspection snapshot is published for the
    /// device the Devices view is watching. Fires at the input loop's
    /// cadence while a target is set, so handlers must be cheap and
    /// marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler? RawInspectionUpdated;

    /// <summary>Snapshot of the currently visible input devices, with ignored ids filtered out.</summary>
    public IReadOnlyList<InputDeviceInfo> Devices
    {
        get
        {
            lock (gate)
            {
                return devices;
            }
        }
    }

    /// <summary>Identifier of the currently selected device, or null for "automatic selection".</summary>
    public string? SelectedDeviceId
    {
        get
        {
            lock (gate)
            {
                return selectedDeviceId;
            }
        }
    }

    /// <summary>End-user-facing description of the active provider's current state.</summary>
    public string ProviderStatus
    {
        get
        {
            lock (gate)
            {
                return ResolveProviderStatus();
            }
        }
    }

    /// <summary>
    /// Marks the given device id as currently selected. Pass null to revert to
    /// automatic selection.
    /// </summary>
    /// <param name="deviceId">The device id to mark selected, or null/empty for auto.</param>
    public void SetSelectedDevice(string? deviceId)
    {
        lock (gate)
        {
            if (string.Equals(selectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            selectedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        }

        RaiseUpdated();
    }

    /// <summary>
    /// Id of the device the Devices view wants raw per-frame state for,
    /// or null when nothing is being inspected. Read by the SDL source
    /// each input tick. Setting it does not raise <see cref="Updated"/>
    /// (it must not trigger a device-list rebuild).
    /// </summary>
    public string? RawInspectionTargetId
    {
        get
        {
            lock (gate)
            {
                return rawInspectionTargetId;
            }
        }
    }

    /// <summary>
    /// Sets (or clears, with null) the device the SDL source should poll
    /// for raw inspection. When cleared, the last published snapshot is
    /// dropped so stale state can't linger in the UI.
    /// </summary>
    public void SetRawInspectionTarget(string? deviceId)
    {
        var normalized = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        bool cleared;

        lock (gate)
        {
            if (string.Equals(rawInspectionTargetId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            rawInspectionTargetId = normalized;
            cleared = normalized is null && rawInspection is not null;
            if (cleared)
            {
                rawInspection = null;
            }
        }

        if (cleared)
        {
            RawInspectionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Latest raw-inspection snapshot, or null when none is available.</summary>
    public RawDeviceSnapshot? RawInspection
    {
        get
        {
            lock (gate)
            {
                return rawInspection;
            }
        }
    }

    /// <summary>
    /// Publishes a fresh raw-inspection snapshot from the SDL source and
    /// notifies listeners. A snapshot whose id no longer matches the
    /// current target is ignored (the view moved on).
    /// </summary>
    public void PublishRawInspection(RawDeviceSnapshot? snapshot)
    {
        lock (gate)
        {
            if (snapshot is not null
                && !string.Equals(rawInspectionTargetId, snapshot.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            rawInspection = snapshot;
        }

        RawInspectionUpdated?.Invoke(this, EventArgs.Empty);
    }
    /// <param name="nextDevices">The new device list. May be null or empty.</param>
    /// <remarks>Back-compat overload — attributes the devices to the "default" source.</remarks>
    public void ReplaceDevices(IReadOnlyList<InputDeviceInfo>? nextDevices)
        => ReplaceDevices("default", nextDevices);

    /// <summary>
    /// Replaces the devices contributed by a single enumeration source
    /// (e.g. <c>"sdl"</c> for controllers, <c>"rawinput"</c> for
    /// keyboard/mouse). Other sources' devices are preserved, so multiple
    /// backends can populate the catalog without clobbering each other.
    /// The merged list is filtered against the ignore lists and deduped by
    /// id (first source wins). <see cref="Updated"/> fires only when the
    /// merged result actually changes.
    /// </summary>
    /// <param name="source">Stable key identifying the enumeration backend.</param>
    /// <param name="nextDevices">That source's current devices. Null/empty clears the source.</param>
    public void ReplaceDevices(string source, IReadOnlyList<InputDeviceInfo>? nextDevices)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            source = "default";
        }

        lock (gate)
        {
            if (nextDevices is null || nextDevices.Count == 0)
            {
                devicesBySource.Remove(source);
            }
            else
            {
                devicesBySource[source] = [.. nextDevices];
            }

            var merged = MergeSources();

            // No-op if the resulting set is identical (value equality on the
            // record covers every field). Prevents per-tick churn/flicker.
            if (devices.SequenceEqual(merged))
            {
                return;
            }

            devices = merged;
            RebuildIndex();
        }

        RaiseUpdated();
    }

    /// <summary>Flattens all source slices into one filtered, deduped list (caller holds <see cref="gate"/>).</summary>
    private IReadOnlyList<InputDeviceInfo> MergeSources()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var raw in devicesBySource.Values.SelectMany(list => list))
        {
            if (!firstSeenById.ContainsKey(raw.Id))
            {
                firstSeenById[raw.Id] = now;
            }
        }

        return
        [
            .. devicesBySource.Values
                .SelectMany(list => list)
                .Where(d => !ignoredDeviceIds.Contains(d.Id))
                .Where(d => !MatchesIgnoredHardwareSignature(d))
                .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(categoryOverrides.Apply)
        ];
    }

    /// <summary>
    /// Re-runs the merge against the current source slices without
    /// waiting for the next enumeration tick — called when a category
    /// override is set/cleared so the correction is visible immediately
    /// rather than on the next poll.
    /// </summary>
    private void ReapplyOverrides()
    {
        lock (gate)
        {
            var merged = MergeSources();
            if (devices.SequenceEqual(merged))
            {
                return;
            }
            devices = merged;
            RebuildIndex();
        }
        RaiseUpdated();
    }

    /// <summary>
    /// Sets the provider-status string. <paramref name="keyOrLiteral"/> is
    /// resolved through <see cref="ILocalizationService"/> on every read so
    /// culture changes are reflected without callers having to push a new
    /// value. If no resource matches, the localizer falls through to the
    /// input string verbatim — making this method safe to call with either
    /// a known localization key (e.g. <c>ProviderStatus_XInputActive</c>) or
    /// an English literal during incremental localization rollout.
    /// </summary>
    /// <param name="keyOrLiteral">
    /// The localization key (preferred) or English literal to display. Pass
    /// null or empty to revert to the localized default
    /// (<c>ProviderStatus_NoActiveProvider</c>).
    /// </param>
    /// <param name="args">
    /// Optional <see cref="string.Format(string, object?[])"/> arguments
    /// interpolated into the resolved string. Use these for keys whose
    /// translation contains <c>{0}</c>-style placeholders, e.g.
    /// <c>"ProviderStatus_XInputActive"</c> + <c>controllerCount</c>.
    /// </param>
    public void SetProviderStatus(string? keyOrLiteral, params object?[] args)
    {
        var normalisedKey  = string.IsNullOrWhiteSpace(keyOrLiteral) ? null : keyOrLiteral;
        var normalisedArgs = args ?? [];

        lock (gate)
        {
            if (string.Equals(statusKey, normalisedKey, StringComparison.Ordinal)
                && ArgsEqual(statusArgs, normalisedArgs))
            {
                return;
            }
            statusKey  = normalisedKey;
            statusArgs = normalisedArgs;
        }

        RaiseUpdated();
    }

    /// <summary>
    /// Replaces the set of device ids that should be hidden from
    /// <see cref="Devices"/>. Useful for filtering the runtime's own virtual
    /// output device out of the input source selection list.
    /// </summary>
    /// <param name="ids">Device ids to ignore (case-insensitive). Pass an empty
    /// collection to clear the filter.</param>
    public void SetIgnoredDeviceIds(IEnumerable<string>? ids)
    {
        var next = new HashSet<string>(
            ids?.Where(id => !string.IsNullOrWhiteSpace(id)) ?? [],
            StringComparer.OrdinalIgnoreCase);

        bool changed;
        lock (gate)
        {
            changed = !next.SetEquals(ignoredDeviceIds);
            ignoredDeviceIds = next;

            if (changed && devices.Count > 0)
            {
                // Re-apply the filter to the existing list.
                devices = [.. devices.Where(d => !ignoredDeviceIds.Contains(d.Id))];
                RebuildIndex();
            }
        }

        if (changed)
        {
            RaiseUpdated();
        }
    }

    /// <summary>
    /// Replaces the set of (vendor id, product id) pairs that should be
    /// hidden from <see cref="Devices"/>. The runtime uses this to hide its
    /// own virtual output device (e.g. a HIDMaestro-emitted Xbox 360 controller advertises
    /// VID 0x045E PID 0x028E and would otherwise re-appear in the input
    /// source dropdown, creating a confusing self-referential entry).
    ///
    /// A real Xbox/DS4 plugged into the same machine matches the same
    /// signature — so this filter only fires when the user has explicitly
    /// chosen the matching virtual output sink.
    /// </summary>
    /// <param name="signatures">
    /// VID/PID pairs to ignore. Pass an empty collection to clear the filter.
    /// </param>
    public void SetIgnoredHardwareSignatures(IEnumerable<(ushort Vid, ushort Pid, DateTimeOffset ActivatedAt)>? signatures)
    {
        var next = (signatures ?? []).Distinct().ToArray();

        bool changed;
        lock (gate)
        {
            changed = !ignoredHardwareSignatures.SequenceEqual(next);
            ignoredHardwareSignatures = next;

            if (changed && devices.Count > 0)
            {
                // Re-apply the filter to the existing list.
                devices = [.. devices.Where(d => !MatchesIgnoredHardwareSignature(d))];
                RebuildIndex();
            }
        }

        if (changed)
        {
            RaiseUpdated();
        }
    }

    /// <summary>
    /// Resets the catalog: empties the device list and updates the status.
    /// </summary>
    /// <param name="statusKeyOrLiteral">
    /// Optional localization key or English literal describing why the catalog
    /// was cleared. Pass null to revert to the localized default ("no active
    /// provider").
    /// </param>
    /// <param name="args">Format arguments for the status string, when applicable.</param>
    public void Clear(string? statusKeyOrLiteral = null, params object?[] args)
    {
        var normalisedKey  = string.IsNullOrWhiteSpace(statusKeyOrLiteral) ? null : statusKeyOrLiteral;
        var normalisedArgs = args ?? [];

        lock (gate)
        {
            devices    = [];
            RebuildIndex();
            statusKey  = normalisedKey;
            statusArgs = normalisedArgs;
        }

        RaiseUpdated();
    }

    /// <summary>
    /// Resolves the user-visible provider status. Must be called under <see cref="gate"/>.
    /// </summary>
    /// <returns>The fully-localized status string (or the literal text if no resource matched).</returns>
    private string ResolveProviderStatus()
    {
        var key      = statusKey ?? defaultStatusKey;
        var template = localization[key];

        if (statusArgs.Length == 0)
        {
            return template;
        }

        // Best-effort string.Format. If the template is a literal that doesn't
        // have placeholders we return it unchanged; if string.Format throws on
        // a malformed template we surface the raw template rather than crash.
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, statusArgs);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// True when <paramref name="device"/> matches one of the registered
    /// (vendor id, product id) pairs in <see cref="ignoredHardwareSignatures"/>.
    /// </summary>
    /// <param name="device">The device to check.</param>
    /// <returns>True if the device is hardware-signature-filtered.</returns>
    private bool MatchesIgnoredHardwareSignature(InputDeviceInfo device)
    {
        if (ignoredHardwareSignatures.Count == 0 || (device.VendorId == 0 && device.ProductId == 0))
        {
            return false;
        }

        var firstSeen = firstSeenById.TryGetValue(device.Id, out var seen)
            ? seen
            : DateTimeOffset.UtcNow;

        for (var i = 0; i < ignoredHardwareSignatures.Count; i++)
        {
            var sig = ignoredHardwareSignatures[i];
            if (sig.Vid == device.VendorId && sig.Pid == device.ProductId
                && firstSeen >= sig.ActivatedAt.AddSeconds(-3))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reference-and-value-equality compare for the status args array.</summary>
    /// <param name="a">First args array.</param>
    /// <param name="b">Second args array.</param>
    /// <returns>True if both arrays carry the same values in the same order.</returns>
    private static bool ArgsEqual(object?[] a, object?[] b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (!Equals(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Reacts to a culture change by re-raising <see cref="Updated"/> so any UI
    /// re-reads the (now possibly different) localized status.
    /// </summary>
    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseUpdated();
    }

    /// <summary>Fires the <see cref="Updated"/> event.</summary>
    /// <summary>O(1) lookup of a visible device by catalog id (case-insensitive). Returns false when the id is unknown or filtered out.</summary>
    public bool TryGetById(string deviceId, out InputDeviceInfo? device)
    {
        lock (gate)
        {
            if (!string.IsNullOrEmpty(deviceId) && devicesById.TryGetValue(deviceId, out var found))
            {
                device = found;
                return true;
            }
        }
        device = null;
        return false;
    }

    /// <summary>Rebuilds the by-id index. Callers must hold <see cref="gate"/>.</summary>
    private void RebuildIndex()
    {
        var index = new Dictionary<string, InputDeviceInfo>(devices.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            index[device.Id] = device;
        }
        devicesById = index;
    }

    private void RaiseUpdated()
    {
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
