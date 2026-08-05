using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Localization;
using GameFlow.Infrastructure.Profiles;
using GameFlow.Infrastructure.Requirements;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAutofireInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        _ = services.Configure<AppRuntimeOptions>(configuration.GetSection("Runtime"));
        _ = services.Configure<Overlay.OverlayOptions>(configuration.GetSection("Overlay"));
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
            // CGEventTap (read) + CGEventPost (write) — see
            // Runtime/Input/Mac/. No per-device distinction exists at
            // this API level (one aggregate stream for every
            // keyboard/mouse), so IKeyboardStateSource/IMouseStateSource
            // route through the aggregate-read fallback path those
            // interfaces already define. No window-attach concept here
            // either — same NullRawInputAttacher as Linux.
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
        _ = services.AddSingleton<Runtime.Slots.SlotRegistry>();
        _ = services.AddSingleton<Runtime.Slots.SlotSnapshotStore>();
        _ = services.AddSingleton<IInputSourceFactory, DefaultInputSourceFactory>();
        _ = services.AddSingleton<IOutputSinkFactory, DefaultOutputSinkFactory>();
        _ = services.AddSingleton<Runtime.HidMaestro.HidMaestroProfileCatalogService>();
        _ = services.AddSingleton<Runtime.Slots.PhysicalPanelPinService>();
        _ = services.AddSingleton<Runtime.DeviceCategoryOverrideStore>();
        _ = services.AddHostedService<RuntimeCoordinator>();
        _ = services.AddHostedService<RawInputEnumerationService>();
        _ = services.AddHostedService<Overlay.OverlayServer>();

        // Web controller: the hub is shared state between the socket
        // server (writes phone input) and the input source (reads it),
        // so it must be a singleton BOTH resolve to — registering the
        // server as a hosted service alone would give it a separate instance.
        // Per-slot-per-device tuning. Singleton: the runtime tick reads
        // it every frame and the UI writes it on every slider drag, so
        // both must see the same instance.
        _ = services.AddSingleton<Runtime.DeviceSettingsStore>();

        _ = services.AddSingleton<Runtime.Web.WebControllerHub>();
        _ = services.AddSingleton<Runtime.Web.WebControllerServer>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<Runtime.Web.WebControllerServer>());
        _ = services.AddHostedService<Runtime.Web.WebControllerEnumerationService>();

        // Step 3 of the roadmap: requirement & update checks.
        _ = services.AddSingleton<IRequirementChecker, DefaultRequirementChecker>();
        _ = services.AddSingleton<IUpdateChecker, GitHubUpdateChecker>();
        _ = services.AddSingleton<IUpdateInstaller, DefaultUpdateInstaller>();

        return services;
    }
}
