using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Runtime.Slots;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Slots;

public sealed class DeviceSettingsTargetResolverTests
{
    [Fact]
    public void Slot_without_physical_hardware_resolves_to_editable_defaults()
    {
        var slot = new ControllerSlot { Id = "slot-a", Index = 1, Name = "Living room pad" };

        var target = DeviceSettingsTargetResolver.Resolve(slot, visibleDevices: []);

        Assert.True(target.IsSlotDefault);
        Assert.Equal(DeviceSettingsStore.SlotDefaultsDeviceId, target.DeviceId);
        Assert.Equal("Living room pad defaults", target.DisplayName);
    }

    [Fact]
    public void Offline_assignment_remains_the_target_when_catalog_is_empty()
    {
        var slot = new ControllerSlot
        {
            Id = "slot-a",
            InputDeviceIds = ["sdl:remembered-pad"],
        };

        var target = DeviceSettingsTargetResolver.Resolve(slot, visibleDevices: []);

        Assert.False(target.IsSlotDefault);
        Assert.Equal("sdl:remembered-pad", target.DeviceId);
        Assert.Equal("sdl:remembered-pad", target.DisplayName);
    }

    [Fact]
    public void Connected_assignment_uses_its_friendly_catalog_name()
    {
        var slot = new ControllerSlot
        {
            Id = "slot-a",
            InputDeviceIds = ["pad-1"],
        };
        InputDeviceInfo[] devices = [new("pad-1", "DualSense")];

        var target = DeviceSettingsTargetResolver.Resolve(slot, devices);

        Assert.Equal("pad-1", target.DeviceId);
        Assert.Equal("DualSense", target.DisplayName);
    }

    [Fact]
    public void Devices_page_selection_keeps_per_device_override_behavior()
    {
        var slot = new ControllerSlot { Id = "slot-a" };

        var target = DeviceSettingsTargetResolver.Resolve(
            slot,
            visibleDevices: [],
            preferredDeviceId: "keyboard-1",
            preferredDisplayName: "My keyboard");

        Assert.False(target.IsSlotDefault);
        Assert.Equal("keyboard-1", target.DeviceId);
        Assert.Equal("My keyboard", target.DisplayName);
    }
}
