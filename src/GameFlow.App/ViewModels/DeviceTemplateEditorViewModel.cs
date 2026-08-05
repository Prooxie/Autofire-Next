using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.HidMaestro;
using GameFlow.Infrastructure.Runtime.Templates;

namespace GameFlow.App.ViewModels;

/// <summary>Output-kind option for the template editor's combo box.</summary>
public sealed record OutputKindOption(VirtualControllerKind Kind, string Label);

/// <summary>
/// One HIDMaestro catalog profile in the template editor's profile combo
/// box. <see cref="Key"/> is the catalog id (empty for the "default for
/// the selected output device" sentinel).
/// </summary>
public sealed record SlotOutputProfileOption(string Key, string Label);

/// <summary>
/// Edits the <see cref="DeviceOutputTemplate"/> for the device currently
/// selected in the Devices view. Loads a detached copy from the
/// <see cref="DeviceTemplateStore"/>, binds the UI to it, and saves on
/// every change. Output kinds gate which sections apply: the runtime-built
/// device shape for the generic kind, lighting for the DualShock family,
/// adaptive triggers for DualSense.
///
/// <para>Beyond the curated kinds, the editor lists HIDMaestro's broad
/// handheld and arcade-controller catalog when the SDK is present.
/// Specialized wheel and flight-simulation profiles remain hidden until
/// GameFlow has dedicated mapping UI for their additional controls.
/// Picking a profile also re-classifies the template's kind family so
/// the dashboard theme follows the selection.</para>
/// </summary>
public sealed class DeviceTemplateEditorViewModel : ViewModelBase
{
    private readonly DeviceTemplateStore store;
    private readonly HidMaestroProfileCatalogService profileCatalog;

    private DeviceOutputTemplate? template;
    private bool loading;
    private Action<DeviceOutputTemplate>? externalSaver;

    public DeviceTemplateEditorViewModel(
        DeviceTemplateStore store,
        GameFlow.Infrastructure.Localization.ILocalizationService localization,
        HidMaestroProfileCatalogService profileCatalog)
    {
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));
        localization.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(EmitLabel));
            OnPropertyChanged(nameof(EmitTooltip));
            OnPropertyChanged(nameof(OutputDeviceLabel));
            OnPropertyChanged(nameof(OutputProfileLabel));
            OnPropertyChanged(nameof(OutputProfileTooltip));
            OnPropertyChanged(nameof(DemoPreviewLabel));
            OnPropertyChanged(nameof(DemoPreviewTooltip));
            RebuildProfileOptions();
        };
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));

        OutputKindOptions =
        [
            .. GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.SelectableKinds
                .Select(k => new OutputKindOption(k, GameFlow.Infrastructure.Runtime.HidMaestro.HidMaestroProfiles.LabelFor(k))),
        ];

        RebuildProfileOptions();   // seed the default sentinel synchronously
        LoadProfileCatalogAsync(); // then swap in the real catalog off-thread
    }

    public IReadOnlyList<OutputKindOption> OutputKindOptions { get; }

    /// <summary>
    /// HIDMaestro catalog profiles offered for this slot: a "default for
    /// the selected output device" sentinel first, then the live catalog
    /// once enumerated (the curated built-ins until then / when the SDK
    /// isn't present).
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<SlotOutputProfileOption> OutputProfileOptions { get; } = [];

    /// <summary>True once the catalog offers more than the default sentinel — drives the combo's visibility.</summary>
    public bool HasOutputProfileOptions => OutputProfileOptions.Count > 1;

    private IReadOnlyList<HidMaestroCatalogProfile> loadedCatalog = [];

    private async void LoadProfileCatalogAsync()
    {
        try
        {
            var profiles = await profileCatalog.GetProfilesAsync();
            Dispatcher.UIThread.Post(() =>
            {
                loadedCatalog = profiles;
                RebuildProfileOptions();
            });
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Template editor: HIDMaestro profile catalog load failed.");
        }
    }

    private void RebuildProfileOptions()
    {
        OutputProfileOptions.Clear();
        OutputProfileOptions.Add(new SlotOutputProfileOption(string.Empty, DefaultProfileOptionLabel));
        foreach (var profile in loadedCatalog)
        {
            if (!profile.IsDeployable)
            {
                // The SDK's own HMProfile.IsDeployable said no — these
                // entries throw "has no HID descriptor and cannot be
                // deployed" from CreateController every single time,
                // and no amount of retrying fixes that. Not offering
                // them spares a pick that's guaranteed to fail.
                continue;
            }
            if (!HidMaestroProfileCatalogService.IsGameControllerProfile(profile))
            {
                continue;
            }
            var label = string.IsNullOrWhiteSpace(profile.Vendor)
                ? $"{profile.Name}  ·  {profile.Id}"
                : $"{profile.Vendor} — {profile.Name}  ·  {profile.Id}";
            OutputProfileOptions.Add(new SlotOutputProfileOption(profile.Id, label));
        }

        // Selection re-resolves from the template by key.
        OnPropertyChanged(nameof(HasOutputProfileOptions));
        OnPropertyChanged(nameof(SelectedOutputProfile));
    }

    /// <summary>True when a device that supports an output template is loaded.</summary>
    private readonly GameFlow.Infrastructure.Localization.ILocalizationService localization;

    public bool HasTemplate => template is not null;

    // ─── Localized labels ───
    public string EmitLabel        => localization["TemplateEmitLabel"];
    public string EmitTooltip      => localization["TemplateEmitTooltip"];
    public string OutputDeviceLabel => localization["TemplateOutputDeviceLabel"];
    public string OutputProfileLabel => Loc("TemplateOutputProfileLabel", "HIDMaestro profile");
    public string OutputProfileTooltip => Loc("TemplateOutputProfileTooltip",
        "Exactly which controller this slot presents to games. \"Default\" uses the standard profile for the " +
        "output device above; the profile list covers HIDMaestro's broad gamepad and arcade-controller catalog. " +
        "Wheel and flight-simulation profiles are hidden for now.");
    private string DefaultProfileOptionLabel => Loc("TemplateOutputProfileDefaultOption", "Default for the selected output device");

    /// <summary>PO lookup with an English fallback for keys not yet translated (the localizer returns the key itself for unknown ids).</summary>
    private string Loc(string key, string fallback)
    {
        var hit = localization[key];
        return string.IsNullOrEmpty(hit) || string.Equals(hit, key, StringComparison.Ordinal) ? fallback : hit;
    }

    /// <summary>
    /// Loads the template for the given device. Templates apply to
    /// gamepads and joysticks (devices that can drive a virtual
    /// controller); keyboards/mice clear the editor.
    /// </summary>
    public void LoadFor(string? deviceId, DeviceCategory category)
    {
        var supported = !string.IsNullOrWhiteSpace(deviceId)
            && (category == DeviceCategory.Gamepad || category == DeviceCategory.Joystick);

        loading = true;
        try
        {
            externalSaver = null;
            template = supported ? store.GetOrCreate(deviceId!) : null;
            RaiseAll();
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>
    /// Loads an explicit template (slot mode). Edits are persisted through
    /// <paramref name="saver"/> instead of the per-device store — used by
    /// the slot editor, which saves into the slot registry.
    /// </summary>
    public void LoadTemplate(DeviceOutputTemplate template, Action<DeviceOutputTemplate> saver)
    {
        loading = true;
        try
        {
            externalSaver = saver;
            this.template = template?.Clone();
            RaiseAll();
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Clears the editor (no slot/device selected).</summary>
    public void Clear()
    {
        loading = true;
        try
        {
            externalSaver = null;
            template = null;
            RaiseAll();
        }
        finally
        {
            loading = false;
        }
    }

    private void Commit()
    {
        if (loading || template is null)
        {
            return;
        }
        if (externalSaver is not null)
        {
            externalSaver(template);
        }
        else
        {
            store.Save(template);
        }
    }

    // ── Enable ──
    public bool Enabled
    {
        get => template?.Enabled ?? false;
        set => SetField(t => t.Enabled = value, template?.Enabled, value);
    }

    // ── Output kind ──
    public OutputKindOption? SelectedOutputKind
    {
        get => OutputKindOptions.FirstOrDefault(o => o.Kind == (template?.OutputKind ?? VirtualControllerKind.Xbox360));
        set
        {
            if (template is null || value is null || template.OutputKind == value.Kind)
            {
                return;
            }
            template.OutputKind = value.Kind;
            // An explicit kind pick means "the default device of this
            // kind" — clear any specific catalog profile so the two
            // combos can't silently contradict each other.
            template.OutputProfileId = string.Empty;
            Commit();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedOutputProfile));
            OnPropertyChanged(nameof(ShowGenericShape));
        }
    }

    // ── HIDMaestro catalog profile ──

    /// <summary>
    /// The specific HIDMaestro catalog profile this slot emits, or the
    /// default sentinel. Picking a profile also re-classifies
    /// <see cref="DeviceOutputTemplate.OutputKind"/> into the profile's
    /// family (DualSense profile → DualSense kind, wheel → generic, …)
    /// so the dashboard theme and the kind-gated editor sections follow
    /// the selected output controller.
    /// </summary>
    public SlotOutputProfileOption? SelectedOutputProfile
    {
        get
        {
            var key = template?.OutputProfileId ?? string.Empty;
            return OutputProfileOptions.FirstOrDefault(
                o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? OutputProfileOptions.FirstOrDefault();
        }
        set
        {
            if (template is null || value is null
                || string.Equals(template.OutputProfileId ?? string.Empty, value.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            template.OutputProfileId = value.Key;
            if (!string.IsNullOrWhiteSpace(value.Key))
            {
                var catalogEntry = loadedCatalog.FirstOrDefault(
                    p => string.Equals(p.Id, value.Key, StringComparison.OrdinalIgnoreCase));
                template.OutputKind = profileCatalog.ClassifyFamily(value.Key, catalogEntry?.Name);
            }
            Commit();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedOutputKind));
            OnPropertyChanged(nameof(ShowGenericShape));
        }
    }

    /// <summary>
    /// The runtime-built device shape only applies when the generic kind
    /// is selected WITHOUT an explicit catalog profile (an explicit
    /// profile defines its own shape).
    /// </summary>
    public bool ShowGenericShape =>
        template?.OutputKind == VirtualControllerKind.GenericDirectInput
        && string.IsNullOrWhiteSpace(template?.OutputProfileId);

    // ── Demo preview ──

    /// <summary>
    /// Drive this slot with the built-in demo waveform instead of its
    /// assigned devices — an end-to-end preview of the virtual
    /// controller (dashboard panels AND the real HIDMaestro output move)
    /// with no physical pad attached.
    /// </summary>
    public bool DemoPreview
    {
        get => template?.DemoPreview ?? false;
        set => SetField(t => t.DemoPreview = value, template?.DemoPreview, value);
    }

    public string DemoPreviewLabel => Loc("TemplateDemoPreviewLabel", "Demo preview");
    public string DemoPreviewTooltip => Loc("TemplateDemoPreviewTooltip",
        "Animates this virtual controller with the built-in demo timeline instead of its assigned devices — " +
        "sticks sweep, triggers pulse, buttons cycle. The mapping profiles and the real virtual controller " +
        "output all run, so you can watch it in the dashboard or test it inside a game with no physical " +
        "controller. Turn off to hand control back to the assigned devices.");

    // ── Generic device shape ──
    public int ThumbstickCount
    {
        get => template?.ThumbstickCount ?? 0;
        set => SetField(t => t.ThumbstickCount = Math.Clamp(value, 0, 4), template?.ThumbstickCount, value);
    }

    public int TriggerCount
    {
        get => template?.TriggerCount ?? 0;
        set => SetField(t => t.TriggerCount = Math.Clamp(value, 0, 8), template?.TriggerCount, value);
    }

    public int ButtonCount
    {
        get => template?.ButtonCount ?? 0;
        set => SetField(t => t.ButtonCount = Math.Clamp(value, 0, 128), template?.ButtonCount, value);
    }

    public int PovCount
    {
        get => template?.PovCount ?? 0;
        set => SetField(t => t.PovCount = Math.Clamp(value, 0, 4), template?.PovCount, value);
    }

    public string ProductString
    {
        get => template?.ProductString ?? string.Empty;
        set => SetField(t => t.ProductString = value ?? string.Empty, template?.ProductString, value ?? string.Empty);
    }

    /// <summary>Vendor id the generic device advertises, editable as hex ("0xBEEF" / "BEEF") or decimal.</summary>
    public string GenericVidText
    {
        get => template is null ? string.Empty : $"0x{template.GenericVendorId:X4}";
        set
        {
            if (template is null || !TryParseUShort(value, out var parsed) || template.GenericVendorId == parsed)
            {
                OnPropertyChanged(); // re-normalise the display either way
                return;
            }
            template.GenericVendorId = parsed;
            Commit();
            OnPropertyChanged();
        }
    }

    /// <summary>Product id the generic device advertises, editable as hex ("0xF001" / "F001") or decimal.</summary>
    public string GenericPidText
    {
        get => template is null ? string.Empty : $"0x{template.GenericProductId:X4}";
        set
        {
            if (template is null || !TryParseUShort(value, out var parsed) || template.GenericProductId == parsed)
            {
                OnPropertyChanged();
                return;
            }
            template.GenericProductId = parsed;
            Commit();
            OnPropertyChanged();
        }
    }

    private static bool TryParseUShort(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ushort.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    // ── helpers ──

    /// <summary>
    /// Applies a change to the active template when the value actually
    /// differs, commits it through the active saver, and raises
    /// property-changed for the calling property. No-ops while no
    /// template is loaded.
    /// </summary>
    private void SetField<T>(
        Action<DeviceOutputTemplate> apply,
        T? oldValue,
        T newValue,
        [CallerMemberName] string? propertyName = null)
    {
        if (template is null)
        {
            return;
        }

        if (EqualityComparer<T?>.Default.Equals(oldValue, newValue))
        {
            return;
        }

        apply(template);
        Commit();
        OnPropertyChanged(propertyName);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(SelectedOutputKind));
        OnPropertyChanged(nameof(SelectedOutputProfile));
        OnPropertyChanged(nameof(HasOutputProfileOptions));
        OnPropertyChanged(nameof(ShowGenericShape));
        OnPropertyChanged(nameof(DemoPreview));
        OnPropertyChanged(nameof(ThumbstickCount));
        OnPropertyChanged(nameof(TriggerCount));
        OnPropertyChanged(nameof(ButtonCount));
        OnPropertyChanged(nameof(PovCount));
        OnPropertyChanged(nameof(ProductString));
        OnPropertyChanged(nameof(GenericVidText));
        OnPropertyChanged(nameof(GenericPidText));
    }
}
