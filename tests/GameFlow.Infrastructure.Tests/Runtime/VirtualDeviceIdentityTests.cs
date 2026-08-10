using GameFlow.Infrastructure.Runtime;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

/// <summary>
/// A virtual pad impersonates real hardware down to its VID/PID, which is
/// what makes games accept it — and also what let it be selected back in
/// as an input source, so a slot could be fed from another slot's output
/// indefinitely.
/// </summary>
public sealed class VirtualDeviceIdentityTests : IDisposable
{
    public VirtualDeviceIdentityTests() => VirtualDeviceIdentity.ReleaseAll();

    public void Dispose() => VirtualDeviceIdentity.ReleaseAll();

    [Fact]
    public void ARealPadIsNotVirtual()
    {
        Assert.False(VirtualDeviceIdentity.IsVirtual(
            @"\\?\HID#VID_054C&PID_0CE6#7&1a2b3c4d&0&0000", serial: null));
    }

    [Fact]
    public void AHidMaestroPathIsRecognisedWithoutHavingBeenClaimed()
    {
        // Covers pads left behind by a previous run that did not shut down
        // cleanly — nothing in this process created them, so the claim
        // registry is empty and only the path gives them away.
        Assert.True(VirtualDeviceIdentity.IsVirtual(
            @"\\?\ROOT#HIDMAESTRO#0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", serial: null));
    }

    [Fact]
    public void PathMatchingIsCaseInsensitive()
    {
        Assert.True(VirtualDeviceIdentity.IsVirtual(@"\\?\root#hidmaestro#0001", serial: null));
    }

    [Fact]
    public void AClaimedSerialIsRecognised()
    {
        VirtualDeviceIdentity.ClaimSerial("xbox-360-wired");

        Assert.True(VirtualDeviceIdentity.IsVirtual(
            @"\\?\HID#VID_045E&PID_028E#1&2b3c4d5e&0&0000", "xbox-360-wired"));
    }

    [Fact]
    public void AReleasedSerialStopsBeingRecognised()
    {
        VirtualDeviceIdentity.ClaimSerial("ds4-v2");
        VirtualDeviceIdentity.Release("ds4-v2", path: null);

        Assert.False(VirtualDeviceIdentity.IsVirtual("/dev/input/js0", "ds4-v2"));
    }

    [Fact]
    public void AClaimedPathIsRecognised()
    {
        VirtualDeviceIdentity.ClaimPath("/dev/input/event42");

        Assert.True(VirtualDeviceIdentity.IsVirtual("/dev/input/event42", serial: null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void UnknownDevicesAreTreatedAsRealRatherThanVirtual(string? path, string? serial)
    {
        // Erring the other way would make a real pad unselectable with no
        // way for the user to override it — a worse failure than leaving
        // one virtual pad in the list.
        Assert.False(VirtualDeviceIdentity.IsVirtual(path, serial));
    }

    [Fact]
    public void ClaimingIsIdempotent()
    {
        VirtualDeviceIdentity.ClaimSerial("dualsense");
        VirtualDeviceIdentity.ClaimSerial("dualsense");
        VirtualDeviceIdentity.Release("dualsense", path: null);

        // One release must actually clear it — a duplicate claim leaving a
        // second entry behind would keep the pad hidden after the slot
        // that owned it went away.
        Assert.False(VirtualDeviceIdentity.IsVirtual(null, "dualsense"));
    }
}

public sealed class InputDeviceInfoAssignabilityTests
{
    [Theory]
    [InlineData(DeviceCategory.Gamepad)]
    [InlineData(DeviceCategory.Joystick)]
    [InlineData(DeviceCategory.Keyboard)]
    [InlineData(DeviceCategory.Mouse)]
    public void RealInputDevicesAreAssignable(DeviceCategory category)
    {
        var device = new InputDeviceInfo("id", "Pad", Category: category);
        Assert.True(device.IsAssignableAsInput);
    }

    [Fact]
    public void AVirtualPadIsNeverAssignableHoweverItIsCategorised()
    {
        var device = new InputDeviceInfo(
            "id", "Virtual Xbox 360", Category: DeviceCategory.Gamepad, IsVirtual: true);

        Assert.False(device.IsAssignableAsInput);
    }

    [Fact]
    public void UnknownCategoriesAreNotAssignable()
    {
        var device = new InputDeviceInfo("id", "Something", Category: DeviceCategory.Unknown);
        Assert.False(device.IsAssignableAsInput);
    }
}
