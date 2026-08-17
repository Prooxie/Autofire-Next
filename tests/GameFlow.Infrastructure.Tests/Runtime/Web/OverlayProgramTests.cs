using System.Globalization;
using System.Text.Json;
using GameFlow.Core.Enums;
using GameFlow.Core.Models;
using GameFlow.Infrastructure.Runtime.Web.Overlay;
using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Flee;
using GameFlow.Infrastructure.Theming.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime.Web;

/// <summary>
/// The OBS overlay's theme compiler and wire format.
///
/// <para>
/// The contract being pinned is that the page stays a dumb tree-walker:
/// every expression becomes an index, the page is handed numbers, and
/// nothing about what a trigger means crosses the socket. A test that
/// asserted on rendered pixels would not catch the failure that matters
/// here, which is the two sides disagreeing about what index 7 is.
/// </para>
/// </summary>
public sealed class OverlayProgramTests
{
    private static InstalledTheme Theme(params ThemeNode[] children) =>
        new("test-theme", "Test", "C:\\themes\\test",
            new ThemeDocument
            {
                Name = "Test Skin",
                Width = 400,
                Height = 300,
                Children = children,
                // No BaseDirectory, so no image resolves — which is the
                // point for most of these: geometry and slots are
                // independent of whether art is on disk.
            },
            ControllerVisualStyle.SimpleGamepad);

    private static OverlayProgram Compile(params ThemeNode[] children)
    {
        // The builder caches per (id, directory), so every test needs a
        // distinct one or the second would get the first's tree.
        OverlayProgramBuilder.ClearCache();
        return OverlayProgramBuilder.Build(Theme(children));
    }

    private static FleeNode Expression(string source) => FleeParser.Parse(source);

    [Fact]
    public void EveryExpressionBecomesAValueSlot()
    {
        var program = Compile(new SliderNode
        {
            InputX = Expression("stick_left:x * 20"),
            InputY = Expression("stick_left:y * 20"),
            InputR = Expression("0")
        });

        var slider = program.Nodes[0];
        Assert.Equal("slide", slider.Kind);
        Assert.Equal(3, program.ValueSlots.Count);
        // Distinct indices: sharing one would make both axes read the
        // same number and pin every stick to a diagonal.
        Assert.Equal([0, 1, 2], new[] { slider.ValueX, slider.ValueY, slider.ValueR });
    }

    [Fact]
    public void AFrameCarriesOneNumberPerSlotInSlotOrder()
    {
        var program = Compile(new SliderNode
        {
            InputX = Expression("stick_left:x * 20"),
            InputY = Expression("stick_left:y * 20")
        });

        var snapshot = ControllerSnapshot.Empty()
            .WithStick(StickId.Left, new StickVector(0.5f, -0.25f));

        var values = Values(program, snapshot);

        Assert.Equal(program.ValueSlots.Count, values.Length);
        Assert.Equal(10, values[0], 3);
        // +5, not -5. ControllerStateSymbols flips stick Y because
        // VSCView themes are authored positive-down while StickVector is
        // positive-up — so the overlay inherits the same flip the app's
        // own surface draws with, which is the whole point of evaluating
        // server-side.
        Assert.Equal(5, values[1], 3);
    }

    [Fact]
    public void AShowHideNodeReportsItsOwnVisibility()
    {
        var program = Compile(new ShowHideNode { Input = Expression("quad_right:s") });

        var pressed = ButtonState.Clone(ButtonState.CreateEmptyMap());
        pressed[ButtonId.South] = true;

        Assert.Equal(0, Values(program, ControllerSnapshot.Empty())[0], 3);
        Assert.Equal(1, Values(program, ControllerSnapshot.Empty().WithButtons(pressed))[0], 3);
    }

    /// <summary>
    /// One marker per trailpad NODE, each reading its own contact.
    /// Substituting each finger in as contact 0 — which is what the app's
    /// dashboard chrome does — would light the first node twice and the
    /// second never, because after substitution there is no contact 1 for
    /// it to find.
    /// </summary>
    [Fact]
    public void EachTrailPadReportsItsOwnFinger()
    {
        var program = Compile(
            new TrailPadNode
            {
                Input = Expression("touch_center:0:touch"),
                InputX = Expression("touch_center:0:x * 100"),
                InputY = Expression("touch_center:0:y * 100")
            },
            new TrailPadNode
            {
                Input = Expression("touch_center:1:touch"),
                InputX = Expression("touch_center:1:x * 100"),
                InputY = Expression("touch_center:1:y * 100")
            });

        var snapshot = ControllerSnapshot.Empty().WithTouchContacts(
        [
            new TouchContact(0, 0.25f, 0.5f),
            new TouchContact(1, 0.75f, 0.5f)
        ]);

        var pads = Frame(program, snapshot).GetProperty("pads");

        Assert.Equal(2, pads.GetArrayLength());
        // -50 and +50: touch x is reported -1..1 about the centre, so two
        // fingers a quarter either side of it land symmetrically.
        Assert.Equal(-50, pads[0][0].GetDouble(), 3);
        Assert.Equal(50, pads[1][0].GetDouble(), 3);
    }

    [Fact]
    public void ATrailPadWithNoFingerDownReportsNull()
    {
        var program = Compile(new TrailPadNode
        {
            Input = Expression("touch_center:0:touch"),
            InputX = Expression("touch_center:0:x * 100"),
            InputY = Expression("touch_center:0:y * 100")
        });

        var pads = Frame(program, ControllerSnapshot.Empty()).GetProperty("pads");

        Assert.Equal(JsonValueKind.Null, pads[0].ValueKind);
    }

    /// <summary>
    /// A frame is a JSON array of numbers, and on a machine whose culture
    /// writes 0,5 for a half it would parse as two elements — shifting
    /// every value after it and misplacing every control from that point
    /// on, but only for some users.
    /// </summary>
    [Fact]
    public void NumbersAreCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");

            var program = Compile(new SliderNode { InputX = Expression("stick_left:x * 1") });
            var snapshot = ControllerSnapshot.Empty().WithStick(StickId.Left, new StickVector(0.5f, 0));

            var frame = OverlayProtocol.BuildFrame(program, new ControllerStateSymbols(), snapshot, null);

            Assert.Contains("0.5", frame, StringComparison.Ordinal);
            // A slider always reserves three slots — X, Y and rotation —
            // whether or not the theme sets them, so the count that
            // proves nothing split is 3, not 1.
            Assert.Equal(
                program.ValueSlots.Count,
                JsonDocument.Parse(frame).RootElement.GetProperty("v").GetArrayLength());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// A theme can divide by a control that happens to read zero. NaN is
    /// not valid JSON, so emitting it would break the parse for the whole
    /// frame rather than misdrawing one node.
    /// </summary>
    [Fact]
    public void ANonFiniteValueBecomesZeroRatherThanInvalidJson()
    {
        var program = Compile(new SliderNode { InputX = Expression("1 / stick_left:x") });

        var frame = OverlayProtocol.BuildFrame(
            program, new ControllerStateSymbols(), ControllerSnapshot.Empty(), null);

        Assert.Equal(0, JsonDocument.Parse(frame).RootElement.GetProperty("v")[0].GetDouble(), 3);
    }

    [Fact]
    public void AssetUrlsAreRootRelativeSoTheTrailingSlashCannotMatter()
    {
        // /overlay and /overlay/ both serve the page. A bare "asset?..."
        // resolves against the directory, so it would 404 on one spelling
        // and work on the other.
        var json = OverlayProtocol.BuildProgram(Compile(new GroupNode()) with
        {
            ImagePaths = ["C:\\themes\\test\\body.png"]
        });

        var url = JsonDocument.Parse(json).RootElement.GetProperty("images")[0].GetString();

        Assert.StartsWith("/overlay/asset?", url, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", url, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page must never receive a filesystem path. It asks for art by
    /// index, which is what makes the asset route safe without a
    /// traversal check.
    /// </summary>
    [Fact]
    public void TheProgramCarriesNoFilesystemPaths()
    {
        var json = OverlayProtocol.BuildProgram(Compile(new GroupNode()) with
        {
            ImagePaths = ["C:\\Users\\someone\\themes\\pack\\body.png"]
        });

        Assert.DoesNotContain("someone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body.png", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownNodeTypeStillRendersItsChildren()
    {
        // The renderer's default case transforms and recurses rather than
        // dropping the subtree, so a schema addition degrades to "drawn
        // without its own effect" instead of a hole in the artwork.
        var program = Compile(new GroupNode
        {
            X = 10,
            Children = [new GroupNode { X = 5 }]
        });

        Assert.Equal("g", program.Nodes[0].Kind);
        Assert.Equal(10, program.Nodes[0].X);
        _ = Assert.Single(program.Nodes[0].Children);
    }

    [Theory]
    [InlineData("FF204080", "#204080FF")]   // AARRGGBB → RRGGBBAA
    [InlineData("204080", "#204080FF")]     // alpha omitted reads as opaque
    [InlineData("00000000", "#00000000")]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    public void ThemeColoursBecomeCssColours(string input, string? expected) =>
        Assert.Equal(expected, OverlayProgramBuilder.ToCssColor(input));

    [Fact]
    public void AMissingImageIsRecordedRatherThanFailingTheCompile()
    {
        var program = Compile(new ImageNode { ImagePath = "assets/not-here.png" });

        // A pack that ships a manifest referencing art it does not
        // contain is common enough to have its own known-issues entry;
        // the rest of the controller still has to draw.
        Assert.Equal(-1, program.Nodes[0].Image);
        Assert.Empty(program.ImagePaths);
        _ = Assert.Single(program.MissingImages);
    }

    private static double[] Values(OverlayProgram program, ControllerSnapshot snapshot) =>
        [.. Frame(program, snapshot).GetProperty("v").EnumerateArray().Select(v => v.GetDouble())];

    private static JsonElement Frame(OverlayProgram program, ControllerSnapshot snapshot) =>
        JsonDocument
            .Parse(OverlayProtocol.BuildFrame(program, new ControllerStateSymbols(), snapshot, null))
            .RootElement;
}
