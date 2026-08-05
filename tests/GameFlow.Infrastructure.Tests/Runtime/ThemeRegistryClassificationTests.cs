using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Runtime;
using GameFlow.Infrastructure.Theming;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class ThemeRegistryClassificationTests
{
    [Theory]
    [InlineData("simple-gamepad-xbox", ControllerVisualStyle.SimpleGamepad)]
    [InlineData("simple-gamepad-nintendo", ControllerVisualStyle.SimpleGamepad)]
    [InlineData("simple-gamepad-playstation", ControllerVisualStyle.SimpleGamepad)]
    [InlineData("simple-gamepad-default", ControllerVisualStyle.SimpleGamepad)]
    public void Simple_gamepad_legends_stay_in_the_simple_gamepad_family(
        string folder,
        ControllerVisualStyle expected)
    {
        Assert.Equal(expected, ThemeRegistry.GuessStyleFromFolderName(folder));
    }

    [Fact]
    public void Battery_is_exposed_only_when_a_percentage_is_reported()
    {
        var reported = new InputDeviceInfo(
            "pad", "Wireless pad", BatteryPercentage: 73,
            BatteryState: DeviceBatteryState.OnBattery);
        var unknown = new InputDeviceInfo("pad-2", "USB pad");

        Assert.True(reported.HasBattery);
        Assert.False(unknown.HasBattery);
    }
}
