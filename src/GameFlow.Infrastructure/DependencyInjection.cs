using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Requirements;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameFlow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every Infrastructure service: profiles, settings,
    /// localization, the device catalog, the slot runtime, effects, and the
    /// network services (web controller, stream overlay, DSU).
    /// </summary>
    public static IServiceCollection AddGameFlowInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        _ = services.Configure<AppRuntimeOptions>(configuration.GetSection("Runtime"));
        _ = services.AddMemoryCache();
        _ = services.AddPortableObjectLocalization(options => options.ResourcesPath = "Localization");

        _ = services.AddSingleton<IProfileRepository, JsonProfileRepository>();
        _ = services.AddSingleton<ProfileSession>();

        // The user-settings service depends on ILogLevelSwitch, which the App
        // layer registers from HostBuilderFactory after wiring it into the
        // Serilog config. Tests that need to resolve IUserSettingsService
        // without the App must register their own ILogLevelSwitch first.
        _ = services.AddSingleton<IUserSettingsService, UserSettingsService>();

        _ = services.AddSingleton<ILocalizationService, LocalizationService>();

        _ = services.AddSingleton<RuntimeSnapshotStore>();
        _ = services.AddSingleton<InputDeviceCatalog>();
        _ = services.AddSingleton<Runtime.Templates.DeviceTemplateStore>();
        _ = services.AddSingleton<Runtime.Input.ButtonMapStore>();
        if (OperatingSystem.IsWindows())
        {
            _ = services.AddSingleton<Runtime.Input.WindowsRawInputReader>();
            _ = services.AddSingleton<Runtime.Input.IKeyboardStateSource>(sp => sp.GetRequiredService<Runtime.Input.WindowsRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IMouseStateSource>(sp => sp.GetRequiredService<Runtime.Input.WindowsRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IRawInputAttacher>(sp => sp.GetRequiredService<Runtime.Input.WindowsRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IMouseOutputWriter, Runtime.Input.Win32MouseOutputWriter>();
            _ = services.AddSingleton<Runtime.Profiles.IForegroundAppReader, Runtime.Profiles.WindowsForegroundAppReader>();
        }
        else if (OperatingSystem.IsLinux())
        {
            // evdev (read) + uinput (write) — real keyboard/mouse-as-a-
            // source reading AND real touchpad-mouse output, both under
            // Runtime/Input/Linux/. No Linux equivalent of "attach to a
            // window" exists for evdev (reads are already system-wide
            // once permitted), so IRawInputAttacher reuses the same
            // NullRawInputAttacher the "else" branch below uses.
            _ = services.AddSingleton<Runtime.Input.Linux.LinuxRawInputReader>();
            _ = services.AddSingleton<Runtime.Input.IKeyboardStateSource>(sp => sp.GetRequiredService<Runtime.Input.Linux.LinuxRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IMouseStateSource>(sp => sp.GetRequiredService<Runtime.Input.Linux.LinuxRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IRawInputAttacher, Runtime.Input.NullRawInputAttacher>();
            _ = services.AddSingleton<Runtime.Input.IMouseOutputWriter, Runtime.Input.Linux.LinuxMouseOutputWriter>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // IOHIDManager (read) + CGEventPost (write) — see
            // Runtime/Input/Mac/. Reading moved off CGEventTap because
            // IOHIDManager reports which device a value came from, so
            // IKeyboardStateSource/IMouseStateSource answer per-device
            // here now, with the aggregate reads kept as the union and
            // as those interfaces' documented fallback. Reading needs
            // Input Monitoring consent, which MacRawInputReader requests
            // and logs explicitly. No window-attach concept here either
            // — same NullRawInputAttacher as Linux.
            _ = services.AddSingleton<Runtime.Input.Mac.MacRawInputReader>();
            _ = services.AddSingleton<Runtime.Input.IKeyboardStateSource>(sp => sp.GetRequiredService<Runtime.Input.Mac.MacRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IMouseStateSource>(sp => sp.GetRequiredService<Runtime.Input.Mac.MacRawInputReader>());
            _ = services.AddSingleton<Runtime.Input.IRawInputAttacher, Runtime.Input.NullRawInputAttacher>();
            _ = services.AddSingleton<Runtime.Input.IMouseOutputWriter, Runtime.Input.Mac.MacMouseOutputWriter>();
        }
        else
        {
            _ = services.AddSingleton<Runtime.Input.IKeyboardStateSource, Runtime.Input.NullKeyboardStateSource>();
            _ = services.AddSingleton<Runtime.Input.IMouseStateSource, Runtime.Input.NullMouseStateSource>();
            _ = services.AddSingleton<Runtime.Input.IRawInputAttacher, Runtime.Input.NullRawInputAttacher>();
            _ = services.AddSingleton<Runtime.Input.IMouseOutputWriter, Runtime.Input.NullMouseOutputWriter>();
        }
        // Foreground-window reading is Windows-only for now; the null
        // reader leaves ProfileAutoSwitchService inert rather than
        // guessing, so the service, its settings and its UI all behave
        // the same everywhere and simply never fire off Windows.
        // Registered after the platform branches so it fills the gap
        // without any of them having to opt out.
        // TryAdd, not Add: the Windows branch above already registered the
        // real reader, and it returns void rather than the builder, so no
        // discard here.
        services.TryAddSingleton<Runtime.Profiles.IForegroundAppReader, Runtime.Profiles.NullForegroundAppReader>();
        _ = services.AddSingleton<Runtime.Profiles.ProfileAutoSwitchService>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<Runtime.Profiles.ProfileAutoSwitchService>());

        _ = services.AddSingleton<Runtime.Slots.SlotRegistry>();
        _ = services.AddSingleton<Runtime.Slots.SlotSnapshotStore>();
        _ = services.AddSingleton<IInputSourceFactory, DefaultInputSourceFactory>();
        _ = services.AddSingleton<IOutputSinkFactory, DefaultOutputSinkFactory>();
        _ = services.AddSingleton<Runtime.HidMaestro.HidMaestroProfileCatalogService>();
        _ = services.AddSingleton<Runtime.Slots.PhysicalPanelPinService>();
        _ = services.AddSingleton<Runtime.DeviceCategoryOverrideStore>();
        _ = services.AddHostedService<RuntimeCoordinator>();
        _ = services.AddHostedService<RawInputEnumerationService>();

        // Web controller: the hub is shared state between the socket
        // server (writes phone input) and the input source (reads it),
        // so it must be a singleton BOTH resolve to — registering the
        // server as a hosted service alone would give it a separate instance.
        // Per-slot-per-device tuning. Singleton: the runtime tick reads
        // it every frame and the UI writes it on every slider drag, so
        // both must see the same instance.
        _ = services.AddSingleton<Runtime.DeviceSettingsStore>();

        // The theme registry is a process-wide singleton rather than a
        // container-owned one because an Avalonia control also needs it
        // and cannot be injected into. Registering the shared instance
        // means both reach the same scan.
        _ = services.AddSingleton(_ => Theming.ThemeRegistry.Shared);

        _ = services.AddSingleton<Runtime.Web.WebControllerHub>();
        _ = services.AddSingleton<Runtime.Web.Overlay.OverlayFeed>();
        _ = services.AddSingleton<Runtime.Web.WindowsFirewallAccess>();
        _ = services.AddSingleton<Runtime.Web.WebControllerServer>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<Runtime.Web.WebControllerServer>());
        _ = services.AddHostedService<Runtime.Web.WebControllerEnumerationService>();

        // Controller effects (rumble / lighting / adaptive triggers).
        // The service is registered even where no backend exists: it parks
        // itself when the writer reports unsupported, and producers can
        // publish unconditionally rather than null-checking everywhere.
        //
        // The mailbox is the hand-off: the effects thread decides what and
        // when, the SDL worker performs the write on the thread that owns
        // the device handles. Writing from the effects thread directly
        // would contend with SDL's device lock across a blocking Bluetooth
        // transfer — the freeze that got effects deleted the first time.
        _ = services.AddSingleton<Runtime.Effects.RumbleFeedbackStore>();
        _ = services.AddSingleton<Runtime.Effects.ControllerEffectMailbox>();
        _ = services.AddSingleton<Runtime.Effects.IControllerEffectWriter,
                                  Runtime.Effects.MailboxControllerEffectWriter>();
        _ = services.AddSingleton<Runtime.Effects.ControllerEffectsService>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<Runtime.Effects.ControllerEffectsService>());

        // The producer is what turns saved settings into output. Without
        // it the whole effects chain is present and inert — which is the
        // state it shipped in once, so it is registered right beside the
        // consumer it feeds.
        _ = services.AddSingleton<Runtime.Effects.ControllerEffectProducer>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<Runtime.Effects.ControllerEffectProducer>());

        // DSU / Cemuhook motion server. Same two-line shape as the web
        // controller above and for the same reason: the Dashboard reads
        // IsRunning / ConnectedClientCount off this instance, so it has to
        // resolve to the SAME object the host is running. Registering it
        // with AddHostedService<T>() alone would give the UI a second,
        // permanently-idle instance to report on.
        _ = services.AddSingleton<Runtime.Motion.DsuServer>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<Runtime.Motion.DsuServer>());

        // Step 3 of the roadmap: requirement & update checks.
        _ = services.AddSingleton<IRequirementChecker, DefaultRequirementChecker>();
        _ = services.AddSingleton<IUpdateChecker, GitHubUpdateChecker>();
        _ = services.AddSingleton<IUpdateInstaller, DefaultUpdateInstaller>();

        return services;
    }
}
