using GameFlow.App.ViewModels;
using GameFlow.Infrastructure.Theming;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Theming;

public sealed class ThemeHitTesterTests
{
    [Fact]
    public void DualShock4_touchpad_region_uses_the_logical_touchpad_id()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themesRoot = Path.Combine(repositoryRoot, "themes");
        var document = ThemeJsonLoader.LoadFromFile(
            Path.Combine(
                themesRoot,
                "dualshock-4-default",
                "DS4 V2 Jet Black",
                "theme.json"),
            themesRoot);

        var hit = ThemeHitTester.TryHit(document, 733, 294);

        Assert.NotNull(hit);
        Assert.Equal(ThemeHitTester.TouchpadElementId, hit.ElementId);
        Assert.True(ThemeHitTester.IsTouchpadHit(hit));
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
