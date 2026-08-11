using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Core.Models.Rules;

namespace GameFlow.Infrastructure.Profiles;

public static class ProfileDefaults
{
    public static ProfileDocument CreateSpeedrunnerDefault()
    {
        return new()
        {
            Id = "default-profile",
            Name = "Default Profile",
            Version = 5,
            PollingRateHz = 1000,
            InputProvider = "sdl",
            // The platform's sole real output backend (HIDMaestro on
            // Windows; preview elsewhere) — see OutputProviderPolicy.
            OutputProvider = Runtime.OutputProviderPolicy.Resolve(null),
            PreferredInputDeviceId = string.Empty,
            Ui = new UiPreferences
            {
                LanguageCode = "en",
                ShowPreviewPane = true,
                Theme = "System",
                StartMinimized = false,
                PhysicalControllerStyle = ControllerVisualStyle.Auto,
                VirtualControllerStyle = ControllerVisualStyle.PlayStation5,
                ShowRawMonitor = true
            },
            Rules =
            [
                new StickThresholdRule
                {
                    Id = "right-stick-threshold",
                    Name = "Right Stick Threshold",
                    TargetStick = StickId.Right,
                    Deadzone = 0.25f,
                    FullAt = 0.90f
                },
                new StickAutofireRule
                {
                    Id = "right-to-left-autofire",
                    Name = "Right Stick -> Left Stick Autofire",
                    SourceStick = StickId.Right,
                    TargetStick = StickId.Left,
                    BlendMode = StickBlendMode.Additive,
                    Timing = new PulseTimingOptions
                    {
                        HoldMs = 128,
                        ReleaseMs = 32
                    }
                },
                new FreezeLastDirectionRule
                {
                    Id = "freeze-last-direction",
                    Name = "Freeze Last Direction",
                    CaptureStick = StickId.Left,
                    TargetStick = StickId.Left,
                    ActivationButton = ButtonId.LeftShoulder,
                    Timing = new PulseTimingOptions
                    {
                        HoldMs = 128,
                        ReleaseMs = 32
                    }
                }
            ]
        };
    }
}
