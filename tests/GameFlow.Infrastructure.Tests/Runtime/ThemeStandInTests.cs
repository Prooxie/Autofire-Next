using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Configuration;
using GameFlow.Infrastructure.Theming;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

/// <summary>
/// What a controller panel draws when its style ships no theme pack.
///
/// <para>
/// Two styles have none: <see cref="ControllerVisualStyle.PlayStation3"/>,
/// which every DualShock 3 resolves to — including any PS2 pad behind a
/// converter, since those present the DualShock 3's VID/PID — and the
/// legacy generic <see cref="ControllerVisualStyle.Xbox"/> that older
/// persisted preferences still carry. Both rendered an empty panel.
/// </para>
/// </summary>
public sealed class ThemeStandInTests
{
    [Fact]
    public void APlayStation3PadStandsInWithTheDualShock4Layout()
    {
        // Closest first: a DualShock 4 has every control a DualShock 3
        // has, so nothing the pad can do goes undrawn.
        Assert.Equal(ControllerVisualStyle.PlayStation4, ThemeRegistry.StandInStyles(ControllerVisualStyle.PlayStation3)[0]);
    }

    [Fact]
    public void TheLegacyGenericXboxStyleStandsInWithAModernXboxLayout()
    {
        Assert.Equal(ControllerVisualStyle.XboxOne, ThemeRegistry.StandInStyles(ControllerVisualStyle.Xbox)[0]);
    }

    [Theory]
    [InlineData(ControllerVisualStyle.PlayStation4)]
    [InlineData(ControllerVisualStyle.PlayStation5)]
    [InlineData(ControllerVisualStyle.Xbox360)]
    [InlineData(ControllerVisualStyle.XboxSeries)]
    [InlineData(ControllerVisualStyle.NintendoSwitch)]
    [InlineData(ControllerVisualStyle.Keyboard)]
    [InlineData(ControllerVisualStyle.Mouse)]
    public void AStyleThatShipsItsOwnPackHasNoStandIn(ControllerVisualStyle style)
    {
        // Substitution exists to replace an empty panel, not to paper
        // over a theme the user deliberately removed — those styles must
        // still reach the "install a theme" message.
        Assert.Empty(ThemeRegistry.StandInStyles(style));
    }

    [Fact]
    public void AStyleWithAThemeInstalledResolvesToItself()
    {
        using var themes = new TemporaryThemes();
        themes.Add("dualshock-4-default");
        themes.Add("dualsense-default");
        var registry = themes.LoadRegistry();

        Assert.Equal(
            ControllerVisualStyle.PlayStation4,
            registry.ResolveRenderableStyle(ControllerVisualStyle.PlayStation4));
    }

    [Fact]
    public void PlayStation3ResolvesToPlayStation4WhenOnlyThatIsInstalled()
    {
        using var themes = new TemporaryThemes();
        themes.Add("dualshock-4-default");
        var registry = themes.LoadRegistry();

        var resolved = registry.ResolveRenderableStyle(ControllerVisualStyle.PlayStation3);

        Assert.Equal(ControllerVisualStyle.PlayStation4, resolved);
        Assert.NotEmpty(registry.GetThemesForStyle(resolved));
    }

    [Fact]
    public void PlayStation3FallsThroughToPlayStation5WhenNoDualShock4IsInstalled()
    {
        using var themes = new TemporaryThemes();
        themes.Add("dualsense-default");
        var registry = themes.LoadRegistry();

        Assert.Equal(
            ControllerVisualStyle.PlayStation5,
            registry.ResolveRenderableStyle(ControllerVisualStyle.PlayStation3));
    }

    [Fact]
    public void PlayStation3StaysItselfWhenNothingCanStandIn()
    {
        // Nothing to draw and nothing to borrow: the style must come back
        // unchanged so the panel shows the "install a theme" message
        // rather than silently picking an unrelated controller.
        using var themes = new TemporaryThemes();
        themes.Add("xbox-one-default");
        var registry = themes.LoadRegistry();

        Assert.Equal(
            ControllerVisualStyle.PlayStation3,
            registry.ResolveRenderableStyle(ControllerVisualStyle.PlayStation3));
    }

    [Fact]
    public void ExactMatchingIsUnaffectedBySubstitution()
    {
        // The picker's contract: only real DualShock 4 skins are
        // selectable for a DualShock 4, so a short skin name stays
        // unambiguous. Resolving a stand-in must not have loosened it.
        using var themes = new TemporaryThemes();
        themes.Add("dualshock-4-default");
        themes.Add("dualsense-default");
        var registry = themes.LoadRegistry();

        var ps4 = registry.GetThemesForStyle(ControllerVisualStyle.PlayStation4);

        Assert.Single(ps4);
        Assert.Empty(registry.GetThemesForStyle(ControllerVisualStyle.PlayStation3));
    }

    /// <summary>
    /// A throwaway themes directory, pointed at through the same override
    /// the settings UI uses. Restores whatever was configured before, so
    /// the process-wide path is left as it was found.
    /// </summary>
    private sealed class TemporaryThemes : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), "gameflow-theme-standin-" + Guid.NewGuid().ToString("N"));
        private readonly string? previous = AppPathOverrides.ThemesDirectory;

        public TemporaryThemes()
        {
            Directory.CreateDirectory(root);
            AppPathOverrides.ThemesDirectory = root;
        }

        /// <summary>
        /// Adds a theme folder. Style classification comes from the folder
        /// name, so the name is the whole fixture — the document just has
        /// to parse.
        /// </summary>
        public void Add(string folderName)
        {
            var dir = Path.Combine(root, folderName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "theme.json"),
                """{ "name": "Test", "width": 100, "height": 100, "children": [] }""");
        }

        public ThemeRegistry LoadRegistry()
        {
            var registry = new ThemeRegistry();
            registry.Refresh();
            return registry;
        }

        public void Dispose()
        {
            AppPathOverrides.ThemesDirectory = previous;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }
}
