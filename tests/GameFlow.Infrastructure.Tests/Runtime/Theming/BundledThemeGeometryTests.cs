using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Theming;

public sealed class BundledThemeGeometryTests
{
    [Theory]
    [InlineData("DS4 V2 Glacier White")]
    [InlineData("DS4 V2 Gold")]
    [InlineData("DS4 V2 Jet Black")]
    [InlineData("DS4 V2 Magma Red")]
    [InlineData("DS4 V2 Midnight Blue")]
    public void DualShock4_touchpad_overlay_matches_the_controller_art(string variant)
    {
        var repositoryRoot = FindRepositoryRoot();
        var themesRoot = Path.Combine(repositoryRoot, "themes");
        var themePath = Path.Combine(
            themesRoot,
            "dualshock-4-default",
            variant,
            "theme.json");

        var document = ThemeJsonLoader.LoadFromFile(themePath, themesRoot);
        var touchpad = Descendants(document.Children)
            .OfType<ImageNode>()
            .Single(node => node.ImagePath.EndsWith(
                "DS4-Touchpad-Cick.png",
                StringComparison.OrdinalIgnoreCase));

        // These variants use the controller artwork's native 1466x783
        // coordinate space. The touchpad overlay is likewise native-size;
        // stretching it changes both the live touch feedback and the
        // opacity-mask hover outline produced from this same node.
        Assert.False(touchpad.Center);
        Assert.Equal(492, touchpad.X);
        Assert.Equal(150, touchpad.Y);
        Assert.Equal(482, touchpad.Width);
        Assert.Equal(289, touchpad.Height);
    }

    private static IEnumerable<ThemeNode> Descendants(IEnumerable<ThemeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in Descendants(node.Children))
            {
                yield return child;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameFlow.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the GameFlow repository above {AppContext.BaseDirectory}.");
    }
}
