using GameFlow.Core.Models;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class DeviceSettingsStoreTests
{
    [Fact]
    public void Untuned_device_inherits_slot_defaults()
    {
        var path = TemporarySettingsPath();
        try
        {
            var store = CreateStore(path);
            var slotDefaults = DeviceSettings.Default with
            {
                LeftStick = new StickSettings { Deadzone = 0.17f },
                Rumble = new RumbleSettings { Gain = 0.65f },
            };

            store.Set("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId, slotDefaults);

            Assert.Equal(slotDefaults, store.GetEffective("slot-a", "offline-pad"));
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void Changing_slot_defaults_invalidates_a_cached_effective_value()
    {
        var path = TemporarySettingsPath();
        try
        {
            var store = CreateStore(path);
            var changed = DeviceSettings.Default with
            {
                LeftStick = new StickSettings { Deadzone = 0.23f },
            };

            Assert.Equal(DeviceSettings.Default, store.GetEffective("slot-a", "pad-1"));

            store.Set("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId, changed);

            Assert.Equal(changed, store.GetEffective("slot-a", "pad-1"));
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void Device_override_wins_over_slot_defaults()
    {
        var path = TemporarySettingsPath();
        try
        {
            var store = CreateStore(path);
            var slotDefaults = DeviceSettings.Default with
            {
                LeftStick = new StickSettings { Deadzone = 0.17f },
            };
            var deviceOverride = DeviceSettings.Default with
            {
                LeftStick = new StickSettings { Deadzone = 0.04f },
            };

            store.Set("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId, slotDefaults);
            store.Set("slot-a", "pad-1", deviceOverride);

            Assert.Equal(deviceOverride, store.GetEffective("slot-a", "pad-1"));
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void Explicit_device_defaults_can_override_and_then_reset_to_slot_defaults()
    {
        var path = TemporarySettingsPath();
        try
        {
            var store = CreateStore(path);
            var slotDefaults = DeviceSettings.Default with
            {
                LeftStick = new StickSettings { Deadzone = 0.17f },
            };

            store.Set("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId, slotDefaults);
            store.Set("slot-a", "pad-1", DeviceSettings.Default);

            Assert.Equal(DeviceSettings.Default, store.GetEffective("slot-a", "pad-1"));

            store.Reset("slot-a", "pad-1");

            Assert.Equal(slotDefaults, store.GetEffective("slot-a", "pad-1"));
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void Slot_defaults_survive_restart_and_apply_to_a_later_device()
    {
        var path = TemporarySettingsPath();
        try
        {
            var expected = DeviceSettings.Default with
            {
                RightTrigger = new TriggerSettings { Deadzone = 0.11f, FullAt = 0.82f },
            };

            CreateStore(path).Set("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId, expected);
            var restored = CreateStore(path);

            Assert.Equal(expected, restored.GetEffective("slot-a", "newly-connected-pad"));
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void Removing_a_slot_deletes_its_defaults_and_device_overrides_only()
    {
        var path = TemporarySettingsPath();
        try
        {
            var store = CreateStore(path);
            var removedDefaults = DeviceSettings.Default with
            {
                LeftStick = new StickSettings { Deadzone = 0.17f },
            };
            var retainedDefaults = DeviceSettings.Default with
            {
                RightStick = new StickSettings { Deadzone = 0.21f },
            };

            store.Set("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId, removedDefaults);
            store.Set("slot-a", "pad-1", removedDefaults);
            store.Set("slot-b", DeviceSettingsStore.SlotDefaultsDeviceId, retainedDefaults);

            store.RemoveSlot("slot-a");

            Assert.False(store.HasSettings("slot-a", DeviceSettingsStore.SlotDefaultsDeviceId));
            Assert.False(store.HasSettings("slot-a", "pad-1"));
            Assert.Equal(DeviceSettings.Default, store.GetEffective("slot-a", "pad-1"));
            Assert.Equal(retainedDefaults, store.GetEffective("slot-b", "pad-2"));

            var restored = CreateStore(path);
            Assert.Equal(DeviceSettings.Default, restored.GetEffective("slot-a", "pad-1"));
            Assert.Equal(retainedDefaults, restored.GetEffective("slot-b", "pad-2"));
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void Rumble_linked_trigger_settings_survive_a_restart()
    {
        var path = TemporarySettingsPath();
        try
        {
            var store = CreateStore(path);
            var tuned = DeviceSettings.Default with
            {
                RightAdaptiveTrigger = new AdaptiveTriggerSettings
                {
                    Mode = AdaptiveTriggerMode.Weapon,
                    Strength = 0.9f,
                    FrequencyHz = 27,
                    FeedbackLink = TriggerFeedbackLink.Vibration,
                    FeedbackAmount = 0.4f,
                },
            };

            store.Set("slot-a", "pad-1", tuned);

            var restored = CreateStore(path);
            var loaded = restored.GetEffective("slot-a", "pad-1").RightAdaptiveTrigger;

            Assert.Equal(TriggerFeedbackLink.Vibration, loaded.FeedbackLink);
            Assert.Equal(0.4f, loaded.FeedbackAmount);
            Assert.Equal(27, loaded.FrequencyHz);
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    [Fact]
    public void A_settings_file_written_before_the_rumble_link_existed_loads_with_it_off()
    {
        // Every profile on disk predates these two fields. Absent must
        // mean "unlinked at full amount", not "linked at zero" — the
        // latter would silently disable a trigger somebody had tuned.
        var path = TemporarySettingsPath();
        try
        {
            File.WriteAllText(path, """
            {
              "slot-a::pad-1": {
                "rightAdaptiveTrigger": { "mode": 2, "strength": 0.9, "frequencyHz": 27 }
              }
            }
            """);

            var loaded = CreateStore(path).GetEffective("slot-a", "pad-1").RightAdaptiveTrigger;

            Assert.Equal(AdaptiveTriggerMode.Weapon, loaded.Mode);
            Assert.Equal(TriggerFeedbackLink.None, loaded.FeedbackLink);
            Assert.Equal(1.0f, loaded.FeedbackAmount);
        }
        finally
        {
            DeleteTemporarySettings(path);
        }
    }

    private static DeviceSettingsStore CreateStore(string path) =>
        new(NullLogger<DeviceSettingsStore>.Instance, path);

    [Fact]
    public void The_default_file_lives_in_the_app_data_folder()
    {
        // Regression: this store resolved %LocalAppData%\AutofireNext
        // directly, with a comment claiming it matched "the rest of the
        // app". The GameFlow rebrand moved everything else to
        // AppPaths.BaseDirectory, so per-device tuning was the only state
        // stranded in the old folder — outside the rebrand's migration,
        // outside AppPathOverrides, and outside anyone's backup of the
        // data folder.
        var store = new DeviceSettingsStore(NullLogger<DeviceSettingsStore>.Instance);

        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, "device-settings.json"),
            store.FilePath);
    }

    [Fact]
    public void An_explicit_path_overrides_the_default()
    {
        var path = TemporarySettingsPath();
        var store = CreateStore(path);

        Assert.Equal(path, store.FilePath);
    }

    private static string TemporarySettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"gameflow-device-settings-{Guid.NewGuid():N}.json");

    private static void DeleteTemporarySettings(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }
}
