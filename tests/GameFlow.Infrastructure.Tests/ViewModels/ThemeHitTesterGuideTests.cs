using GameFlow.App.ViewModels;
using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.ViewModels;

/// <summary>
/// Covers hit-testing a theme binding whose variable name carries no
/// colon.
/// </summary>
/// <remarks>
/// Theme bindings are mostly "group:member" — <c>quad_right:s</c>,
/// <c>bumpers:l</c> — and the walker's variable extractor required that
/// colon before it would report a binding at all. Every Sony pack names
/// the PS button plainly <c>home</c>, so it fell through and the button
/// produced no hit: not hoverable, not clickable, not mappable. The
/// mapper's own "home" case existed the whole time and had never been
/// reachable.
/// </remarks>
public sealed class ThemeHitTesterGuideTests
{
    private const string ThemeJson = """
    {
      "name": "probe",
      "width": 1000,
      "height": 800,
      "children": [
        { "type": "showhide", "input": "home",
          "children": [
            { "type": "image", "x": 688, "y": 519,
              "image": "art/home.png", "width": 87, "height": 60 } ] },
        { "type": "showhide", "input": "bumpers:l",
          "children": [
            { "type": "image", "x": 190, "y": 71,
              "image": "art/l1.png", "width": 199, "height": 99 } ] }
      ]
    }
    """;

    private static ThemeDocument Load() => ThemeJsonLoader.LoadFromString(ThemeJson, null, null);

    [Fact]
    public void ThePsButtonIsHittable()
    {
        var hit = ThemeHitTester.TryHit(Load(), 688 + 43, 519 + 30);

        Assert.NotNull(hit);
        Assert.Equal("Guide", hit!.ElementId);
        Assert.Equal(87, hit.Bounds.Width);
        Assert.Equal(60, hit.Bounds.Height);
        Assert.Equal("art/home.png", hit.ShapeImagePath);
    }

    /// <summary>A colon-carrying binding keeps working unchanged.</summary>
    [Fact]
    public void AGroupedBindingStillResolves()
    {
        var hit = ThemeHitTester.TryHit(Load(), 190 + 99, 71 + 49);

        Assert.NotNull(hit);
        Assert.Equal("LeftShoulder", hit!.ElementId);
    }

    /// <summary>A point on no interactive region still resolves to nothing.</summary>
    [Fact]
    public void EmptyCanvasStaysUnmapped() =>
        Assert.Null(ThemeHitTester.TryHit(Load(), 5, 5));
}
