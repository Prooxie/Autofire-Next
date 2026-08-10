using GameFlow.Infrastructure.Theming;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Theming;

/// <summary>
/// These pin the real-world pack layouts that broke controller rendering:
/// every non-Generic controller rendered blank because a pack folder had
/// been renamed and every root-relative art path in its manifests still
/// named the old folder.
/// </summary>
public sealed class ThemeAssetResolverTests : IDisposable
{
    private readonly string root;

    public ThemeAssetResolverTests()
    {
        root = Path.Combine(Path.GetTempPath(), "gf-theme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ThemeAssetResolver.ClearCache();
    }

    public void Dispose()
    {
        ThemeAssetResolver.ClearCache();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RootRelativePathResolvesWhenTheFolderNamesStillMatch()
    {
        var expected = Touch("dualsense", "Image Assets", "L2.png");
        var baseDir = Dir("dualsense");

        Assert.Equal(
            expected,
            ThemeAssetResolver.Resolve(@"\dualsense\Image Assets\L2.png", baseDir, root));
    }

    [Fact]
    public void RootRelativePathSurvivesThePackFolderBeingRenamed()
    {
        // THE regression. Manifests inside dualsense-default still say
        // "\dualsense\...", which resolves to a folder that no longer has
        // the art. Dropping the stale pack segment and resolving from the
        // pack itself is what brings the artwork back.
        var expected = Touch("dualsense-default", "Image Assets", "L2.png");
        var baseDir = Dir("dualsense-default");

        Assert.Equal(
            expected,
            ThemeAssetResolver.Resolve(@"\dualsense\Image Assets\L2.png", baseDir, root));
    }

    [Fact]
    public void AVariantThemeFindsArtOneLevelUpInItsPack()
    {
        // themes/<pack>/<variant>/theme.json with the art in the pack
        // root. The previous fallback only ever looked INSIDE <variant>,
        // so it could never find this.
        var expected = Touch("dualsense-default", "Image Assets", "Lightbar.png");
        var baseDir = Dir("dualsense-default", "Cosmic Red");

        Assert.Equal(
            expected,
            ThemeAssetResolver.Resolve(@"\dualsense\Image Assets\Lightbar.png", baseDir, root));
    }

    [Fact]
    public void ManifestFolderThatDisagreesWithThePackIsFoundByFilename()
    {
        // The DS4 variants ask for "Theme Assets" while the pack ships
        // "Image Assets". Nothing about the path matches, only the file.
        var expected = Touch("dualshock-4-default", "Image Assets", "DS4 V2 Active Button", "DS4_Lightbar_Front.png");
        var baseDir = Dir("dualshock-4-default", "DS4 V2 Gold");

        Assert.Equal(
            expected,
            ThemeAssetResolver.Resolve(
                @"\ds4\Theme Assets\DS4 V2 Active Button\DS4_Lightbar_Front.png", baseDir, root));
    }

    [Fact]
    public void RelativePathAlsoFallsBackIntoThePack()
    {
        // The DualSense base theme asks for "assets/..." while the art is
        // under "Image Assets/Button Colors/...". Relative paths used to
        // get no fallback at all.
        var expected = Touch("dualsense-default", "Image Assets", "Button Colors", "DualSense_L2.png");
        var baseDir = Dir("dualsense-default");

        Assert.Equal(
            expected,
            ThemeAssetResolver.Resolve("assets/DualSense_L2.png", baseDir, root));
    }

    [Fact]
    public void ThePlainRelativePathWinsWhenItExists()
    {
        // The fallbacks must never outrank a path that is simply correct,
        // or a pack with two same-named files renders the wrong one.
        var correct = Touch("pack", "assets", "body.png");
        Touch("pack", "Image Assets", "body.png");

        Assert.Equal(
            correct,
            ThemeAssetResolver.Resolve("assets/body.png", Dir("pack"), root));
    }

    [Fact]
    public void SearchNeverEscapesTheThemesOwnPack()
    {
        // The whole safety argument for the filename search: it may pick a
        // different SUBFOLDER of the same controller, never another
        // controller's art. A DualSense stick must not appear on an Xbox pad.
        Touch("xbox-360-default", "Image Assets", "stick.png");
        var baseDir = Dir("dualsense-default");

        Assert.Null(ThemeAssetResolver.Resolve("assets/stick.png", baseDir, root));
    }

    [Fact]
    public void GenuinelyAbsentArtReportsNullRatherThanAWrongFile()
    {
        Assert.Null(ThemeAssetResolver.Resolve("assets/nope.png", Dir("pack"), root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyImageReferencesResolveToNull(string? imagePath)
    {
        Assert.Null(ThemeAssetResolver.Resolve(imagePath, Dir("pack"), root));
    }

    [Fact]
    public void AMissingBaseDirectoryIsNotAnError()
    {
        Assert.Null(ThemeAssetResolver.Resolve("assets/x.png", null, root));
    }

    [Fact]
    public void PackRootIsTheTopLevelFolderUnderTheThemesRoot()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "dualshock-4-default")),
            Path.GetFullPath(ThemeAssetResolver.GetPackRoot(
                Dir("dualshock-4-default", "DS4 V2 Gold"), root)));
    }

    [Fact]
    public void AThemeSittingDirectlyAtTheThemesRootIsItsOwnPack()
    {
        var baseDir = Dir("simple-gamepad-default");
        Assert.Equal(
            Path.GetFullPath(baseDir),
            Path.GetFullPath(ThemeAssetResolver.GetPackRoot(baseDir, root)));
    }
}
