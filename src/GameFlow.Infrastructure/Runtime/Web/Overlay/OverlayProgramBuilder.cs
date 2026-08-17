using System.Collections.Concurrent;
using System.Globalization;
using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Flee;
using GameFlow.Infrastructure.Theming.Models;

namespace GameFlow.Infrastructure.Runtime.Web.Overlay;

/// <summary>
/// Compiles an <see cref="InstalledTheme"/> into an
/// <see cref="OverlayProgram"/>.
///
/// <para>
/// The walk mirrors <c>ThemeSurface.RenderNode</c> case for case, and it
/// has to: any disagreement shows up as an overlay that draws a theme
/// differently from the app drawing the same theme, which is the one
/// thing the overlay must never do. Where that method pushes a transform
/// and recurses, this one emits a node and recurses; where it evaluates
/// an expression, this one reserves a value slot. The two notes worth
/// carrying across are that a node's own X/Y is applied ONCE by the
/// walker (a slider adding its own X again double-translated every stick,
/// which is a bug this codebase already fixed once), and that children
/// render inside their parent's frame in every case including the leaf
/// ones.
/// </para>
///
/// <para>
/// Results are cached per theme: the compile is pure, the input is a file
/// on disk that only changes on a registry refresh, and an OBS source
/// reconnecting should not re-walk a several-hundred-node tree.
/// </para>
/// </summary>
public static class OverlayProgramBuilder
{
    private static readonly ConcurrentDictionary<string, OverlayProgram> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compiles <paramref name="theme"/>, or returns the cached result.
    /// The cache key includes the theme's directory so two registries
    /// disagreeing about an id cannot serve each other's art.
    /// </summary>
    public static OverlayProgram Build(InstalledTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Cache.GetOrAdd($"{theme.Id}|{theme.DirectoryPath}", _ => Compile(theme));
    }

    /// <summary>Drops every compiled theme. Called when the registry refreshes.</summary>
    public static void ClearCache() => Cache.Clear();

    private static OverlayProgram Compile(InstalledTheme theme)
    {
        var document = theme.Document;
        var state = new BuildState(document);
        var nodes = new List<OverlayNode>();

        foreach (var child in document.Children)
        {
            var built = state.Visit(child);
            if (built is not null)
            {
                nodes.Add(built);
            }
        }

        return new OverlayProgram
        {
            ThemeId = theme.Id,
            ThemeName = string.IsNullOrWhiteSpace(document.Name) ? theme.DisplayName : document.Name,
            Width = document.Width > 0 ? document.Width : 1,
            Height = document.Height > 0 ? document.Height : 1,
            Nodes = nodes,
            ImagePaths = state.ImagePaths,
            ValueSlots = state.ValueSlots,
            TrailPads = state.TrailPads,
            MissingImages = state.MissingImages
        };
    }

    /// <summary>
    /// Accumulates the flat side-tables while the tree is walked. A class
    /// rather than ref parameters purely so the recursion reads like the
    /// renderer it mirrors.
    /// </summary>
    private sealed class BuildState(ThemeDocument document)
    {
        private readonly ThemeDocument document = document;
        private readonly Dictionary<string, int> imageIndex = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ImagePaths { get; } = [];
        public List<FleeNode> ValueSlots { get; } = [];
        public List<OverlayTrailPad> TrailPads { get; } = [];
        public List<string> MissingImages { get; } = [];

        public OverlayNode? Visit(ThemeNode node)
        {
            var children = new List<OverlayNode>(node.Children.Count);
            foreach (var child in node.Children)
            {
                var built = Visit(child);
                if (built is not null)
                {
                    children.Add(built);
                }
            }

            return node switch
            {
                ShowHideNode show => Base("show", node, children) with
                {
                    Value = Slot(show.Input)
                },

                SliderNode slider => Base("slide", node, children) with
                {
                    ValueX = Slot(slider.InputX),
                    ValueY = Slot(slider.InputY),
                    ValueR = Slot(slider.InputR)
                },

                TrailPadNode trail => BuildTrailPad(trail, children),

                ImageNode image => Base("img", node, children) with
                {
                    Image = Image(image.ImagePath),
                    W = image.Width,
                    H = image.Height,
                    Center = image.Center
                },

                LightbarNode light => Base("light", node, children) with
                {
                    Image = Image(light.ImagePath),
                    W = light.Width,
                    H = light.Height,
                    Center = light.Center
                },

                PBarNode bar => Base("bar", node, children) with
                {
                    Value = Slot(bar.Input),
                    Min = Slot(bar.Min),
                    Max = Slot(bar.Max),
                    Dir = bar.Direction.ToString().ToLowerInvariant(),
                    Image = string.IsNullOrEmpty(bar.ImagePath) ? -1 : Image(bar.ImagePath),
                    Fg = ToCssColor(bar.Foreground),
                    Bg = ToCssColor(bar.Background),
                    W = bar.Width,
                    H = bar.Height,
                    Center = bar.Center
                },

                // GroupNode and anything added to the schema later. The
                // renderer's own default case does the same thing —
                // transform and recurse — so an unknown node type shows
                // its children rather than swallowing them.
                _ => Base("g", node, children)
            };
        }

        private OverlayNode BuildTrailPad(TrailPadNode trail, List<OverlayNode> children)
        {
            TrailPads.Add(new OverlayTrailPad(trail.Input, trail.InputX, trail.InputY));
            return Base("trail", trail, children) with
            {
                Pad = TrailPads.Count - 1,
                Image = Image(trail.ImagePath),
                W = trail.Width,
                H = trail.Height,
                // A trailpad marker is always centred on the contact.
                // The renderer hard-codes this in DrawTrailPadMarker
                // rather than reading a `center` flag, so this does too.
                Center = true
            };
        }

        private static OverlayNode Base(string kind, ThemeNode node, List<OverlayNode> children) =>
            new()
            {
                Kind = kind,
                X = node.X,
                Y = node.Y,
                R = node.Rotation,
                Children = children
            };

        private int Slot(FleeNode expression)
        {
            ValueSlots.Add(expression);
            return ValueSlots.Count - 1;
        }

        /// <summary>
        /// Resolves an image reference to an index, or -1 when the file is
        /// not on disk. Resolution goes through the same forgiving
        /// <see cref="ThemeAssetResolver"/> the app uses, so a pack whose
        /// manifest paths disagree with its own folder layout — the norm
        /// for third-party VSCView packs — renders here exactly as far as
        /// it renders in the app.
        /// </summary>
        private int Image(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return -1;
            }

            if (imageIndex.TryGetValue(imagePath, out var existing))
            {
                return existing;
            }

            var resolved = ThemeAssetResolver.Resolve(
                imagePath, document.BaseDirectory, document.ThemesRootDirectory);

            if (resolved is null)
            {
                if (!MissingImages.Contains(imagePath, StringComparer.OrdinalIgnoreCase))
                {
                    MissingImages.Add(imagePath);
                }
                // Cached as absent too: a pack referencing one missing
                // file forty times should probe the filesystem once.
                imageIndex[imagePath] = -1;
                return -1;
            }

            ImagePaths.Add(resolved);
            var index = ImagePaths.Count - 1;
            imageIndex[imagePath] = index;
            return index;
        }
    }

    /// <summary>
    /// Converts a theme's 8-digit <c>AARRGGBB</c> hex to the
    /// <c>#RRGGBBAA</c> CSS wants. Returns null for anything unparseable
    /// so the page can skip the fill rather than paint an accidental
    /// black rectangle over the art.
    /// </summary>
    internal static string? ToCssColor(string? argb)
    {
        if (string.IsNullOrWhiteSpace(argb))
        {
            return null;
        }

        var hex = argb.TrimStart('#');
        if (hex.Length == 6)
        {
            // VSCView allows the alpha to be omitted; opaque is the
            // sensible reading and matches how the app's HexBrush treats it.
            hex = "FF" + hex;
        }

        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return null;
        }

        var a = (packed >> 24) & 0xFF;
        var r = (packed >> 16) & 0xFF;
        var g = (packed >> 8) & 0xFF;
        var b = packed & 0xFF;
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }
}
