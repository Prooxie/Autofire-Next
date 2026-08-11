using GameFlow.Core.Models;
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

    private static DeviceSettingsStore CreateStore(string path) =>
        new(NullLogger<DeviceSettingsStore>.Instance, path);

    private static string TemporarySettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"gameflow-device-settings-{Guid.NewGuid():N}.json");

    private static void DeleteTemporarySettings(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }
}
