using GameFlow.Infrastructure.Runtime;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

/// <summary>
/// The Touchpad tab appears for a slot when an assigned device reports a
/// touch surface, so this classification decides whether the whole
/// feature is reachable for a given pad.
/// </summary>
public sealed class TouchpadCapabilityTests
{
    private static InputDeviceInfo Device(ushort vid, ushort pid) =>
        new("id", "Pad", VendorId: vid, ProductId: pid, IsGamepad: true, Category: DeviceCategory.Gamepad);

    [Theory]
    [InlineData(0x054C, 0x05C4)] // DualShock 4 v1
    [InlineData(0x054C, 0x09CC)] // DualShock 4 v2
    [InlineData(0x054C, 0x0BA0)] // DualShock 4 wireless adapter
    [InlineData(0x054C, 0x0CE6)] // DualSense
    [InlineData(0x054C, 0x0DF2)] // DualSense Edge
    public void Pads_with_a_touch_surface_are_recognized(ushort vid, ushort pid)
    {
        Assert.True(Device(vid, pid).HasTouchpad);
    }

    [Theory]
    [InlineData(0x045E, 0x028E)] // Xbox 360
    [InlineData(0x045E, 0x0B12)] // Xbox Series X|S
    [InlineData(0x057E, 0x2009)] // Switch Pro
    [InlineData(0x054C, 0x0268)] // DualShock 3 — PlayStation, but no touch surface
    public void Pads_without_one_are_not(ushort vid, ushort pid)
    {
        Assert.False(Device(vid, pid).HasTouchpad);
    }

    [Fact]
    public void Unknown_hardware_reports_no_touch_surface()
    {
        // Unknown must fail closed: showing the tab on a pad that reports
        // nothing would offer a page of controls that silently do nothing.
        Assert.False(Device(0x1234, 0x5678).HasTouchpad);
        Assert.False(Device(0, 0).HasTouchpad);
    }
}
