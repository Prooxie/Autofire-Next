using GameFlow.Infrastructure.Theming.Flee;

namespace GameFlow.Infrastructure.Theming.Models;

/// <summary>
/// Root of a parsed theme document. Mirrors the top-level fields in a
/// VSCView <c>theme.json</c> file:
/// <list type="bullet">
/// <item><c>name</c> — display name shown in the theme picker;</item>
/// <item><c>width</c>, <c>height</c> — canvas size in pixels (NOT the
///   physical pixel size of the base PNG — VSCView lets themes set their
///   own coordinate space and scales output to fit the host window);</item>
/// <item><c>version</c> — theme-structure version (only <c>1</c> shipped
///   to date; reserved for future migrations);</item>
/// <item><c>children</c> — flattened tree of theme elements.</item>
/// </list>
///
/// <para>
/// The document also tracks <see cref="BaseDirectory"/>, which is the
/// folder the theme was loaded from. Image element paths starting with
/// <c>\</c> resolve relative to the parent <c>themes</c> folder (so
/// themes can share assets); all other image paths resolve relative to
/// the theme's own <see cref="BaseDirectory"/>.
/// </para>
/// </summary>
public sealed record ThemeDocument
{
    /// <summary>Display name for the theme picker.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Canvas width in theme-local pixels. Treat as a viewBox.</summary>
    public double Width { get; init; }

    /// <summary>Canvas height in theme-local pixels.</summary>
    public double Height { get; init; }

    /// <summary>
    /// Theme-format version. Only <c>1</c> is shipped; future migrations
    /// would inspect this field before deciding how to deserialise.
    /// </summary>
    public int Version { get; init; } = 1;

    /// <summary>The element tree.</summary>
    public IReadOnlyList<ThemeNode> Children { get; init; } = [];

    /// <summary>
    /// Absolute filesystem path of the directory that contained the
    /// theme.json file. Used to resolve relative <c>image</c> references
    /// at render time. <see langword="null"/> for documents constructed
    /// in-memory (tests, design-time data).
    /// </summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// Sibling-relative root used for <c>\</c>-prefixed image paths
    /// (the "themes/" folder containing this theme and other themes).
    /// Falls back to the parent of <see cref="BaseDirectory"/>.
    /// </summary>
    public string? ThemesRootDirectory { get; init; }
}

/// <summary>
/// Base class for every node in a theme tree. Mirrors VSCView's
/// <c>Item</c> base element: every node carries a position, optional
/// rotation, optional render-quality hints, and may contain children.
///
/// <para>
/// All numeric fields are <see cref="double"/> rather than <see cref="float"/>
/// because the JSON schema doesn't distinguish them and Avalonia's drawing
/// primitives take <see cref="double"/> directly — converting per render
/// tick would burn CPU we'd rather spend elsewhere.
/// </para>
/// </summary>
public abstract record ThemeNode
{
    /// <summary>Horizontal position in theme-local pixels, top-left origin.</summary>
    public double X { get; init; }

    /// <summary>Vertical position in theme-local pixels, top-left origin.</summary>
    public double Y { get; init; }

    /// <summary>
    /// Rotation in degrees, clockwise. Mirrors VSCView's <c>rot</c>
    /// field. Inheritance is multiplicative through parent transforms so
    /// children inherit the rotation of their containing node.
    /// </summary>
    public double Rotation { get; init; }

    /// <summary>
    /// Children rendered above this node in the same coordinate space.
    /// Empty for leaf elements.
    /// </summary>
    public IReadOnlyList<ThemeNode> Children { get; init; } = [];
}

/// <summary>
/// Group node — no rendering of its own, but lets the JSON nest children
/// for grouping / transform inheritance. Maps to VSCView's
/// <c>"type": null</c> case (a plain <c>Item</c>).
/// </summary>
public sealed record GroupNode : ThemeNode;

/// <summary>
/// Static or dynamic image. Mirrors VSCView's <c>GraphicalItem</c> with
/// <c>"type": "image"</c>. Renders <see cref="ImagePath"/> at (X, Y)
/// with the supplied <see cref="Width"/> × <see cref="Height"/>.
/// </summary>
public sealed record ImageNode : ThemeNode
{
    /// <summary>
    /// Path to the image file. A leading <c>\</c> means "resolve from the
    /// themes/ root" (i.e. shared across themes); otherwise the path is
    /// relative to the theme's own folder.
    /// </summary>
    public string ImagePath { get; init; } = string.Empty;

    /// <summary>Width in theme-local pixels. 0 means "use the bitmap's intrinsic width".</summary>
    public double Width { get; init; }

    /// <summary>Height in theme-local pixels. 0 means "use the bitmap's intrinsic height".</summary>
    public double Height { get; init; }

    /// <summary>
    /// When true, the image's centre — not its top-left — sits at (X, Y).
    /// VSCView default is false; we follow.
    /// </summary>
    public bool Center { get; init; }
}

/// <summary>
/// Runtime-coloured controller light. The referenced bitmap supplies the
/// light's alpha mask while the dashboard supplies the live RGB value.
/// Keeping the geometry in the theme lets every controller model place its
/// LEDs accurately without model-specific drawing code in the app.
/// </summary>
public sealed record LightbarNode : ThemeNode
{
    /// <summary>Alpha-mask image for the illuminated region.</summary>
    public string ImagePath { get; init; } = string.Empty;

    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>Anchor at the image centre instead of its top-left.</summary>
    public bool Center { get; init; }
}

/// <summary>
/// Conditional-visibility wrapper. Mirrors VSCView's
/// <c>"type": "showhide"</c>: renders its <see cref="ThemeNode.Children"/>
/// only when the compiled Flee <see cref="Input"/> evaluates to non-zero.
///
/// <para>
/// This is the primary mechanism for "press to highlight" behaviour —
/// a press-state image lives inside a ShowHide whose input is the
/// button's Flee variable. When the button isn't held, the press
/// graphic is skipped entirely (zero draw cost), so an idle controller
/// surface renders almost as cheaply as a static bitmap.
/// </para>
/// </summary>
public sealed record ShowHideNode : ThemeNode
{
    /// <summary>Compiled Flee expression driving the visibility state.</summary>
    public FleeNode Input { get; init; } = new LiteralNode(1);
}

/// <summary>
/// Translation node — moves its children by an offset computed from
/// Flee inputs. Mirrors VSCView's <c>"type": "slider"</c>.
///
/// <para>
/// Typical use: drive a stick "thumb" image so it slides around the
/// analog well as the user pushes the stick.
/// <c>"inputX": "stick_left:x * 20"</c> moves the children 20px right
/// at full deflection.
/// </para>
/// </summary>
public sealed record SliderNode : ThemeNode
{
    /// <summary>Compiled Flee expression for the X offset (pixels).</summary>
    public FleeNode InputX { get; init; } = new LiteralNode(0);

    /// <summary>Compiled Flee expression for the Y offset (pixels).</summary>
    public FleeNode InputY { get; init; } = new LiteralNode(0);

    /// <summary>Compiled Flee expression for an additional rotation offset (degrees).</summary>
    public FleeNode InputR { get; init; } = new LiteralNode(0);
}

/// <summary>
/// Touch-contact marker used by VSCView's <c>trailpad</c> element. The
/// current renderer draws the active marker at its evaluated position;
/// retaining historical trail samples can be added later without changing
/// the theme schema or reintroducing an idle marker.
/// </summary>
public sealed record TrailPadNode : ThemeNode
{
    /// <summary>Non-zero while this touch contact is active.</summary>
    public FleeNode Input { get; init; } = new LiteralNode(0);

    /// <summary>Horizontal marker offset in theme-local pixels.</summary>
    public FleeNode InputX { get; init; } = new LiteralNode(0);

    /// <summary>Vertical marker offset in theme-local pixels.</summary>
    public FleeNode InputY { get; init; } = new LiteralNode(0);

    /// <summary>Marker image stamped at the current touch position.</summary>
    public string ImagePath { get; init; } = string.Empty;

    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>
/// Progress-bar fill direction. Mirrors VSCView's enum exactly so
/// theme.json values pass through unchanged.
/// </summary>
public enum PBarDirection
{
    /// <summary>Fills from bottom to top as the input grows.</summary>
    Up,
    /// <summary>Fills from top to bottom as the input grows.</summary>
    Down,
    /// <summary>Fills from right to left as the input grows.</summary>
    Left,
    /// <summary>Fills from left to right as the input grows.</summary>
    Right
}

/// <summary>
/// Progress-bar / trigger-fill node. Mirrors VSCView's <c>"type": "pbar"</c>.
/// Draws a filled rectangle whose extent is proportional to where the
/// <see cref="Input"/> value falls between <see cref="Min"/> and
/// <see cref="Max"/>. The fill grows in the direction set by
/// <see cref="Direction"/>.
///
/// <para>
/// When an <see cref="ImagePath"/> is set, the image is masked by the
/// current fill rectangle (so a trigger-shaped PNG appears to "fill in"
/// from the top as the user pulls); otherwise a solid colour rectangle
/// is drawn using <see cref="Foreground"/> over <see cref="Background"/>.
/// </para>
/// </summary>
public sealed record PBarNode : ThemeNode
{
    /// <summary>Compiled Flee expression for the current value.</summary>
    public FleeNode Input { get; init; } = new LiteralNode(0);

    /// <summary>Compiled Flee expression for the minimum value (defaults to constant 0).</summary>
    public FleeNode Min { get; init; } = new LiteralNode(0);

    /// <summary>Compiled Flee expression for the maximum value (defaults to constant 1).</summary>
    public FleeNode Max { get; init; } = new LiteralNode(1);

    /// <summary>Direction the bar grows in as the value approaches <see cref="Max"/>.</summary>
    public PBarDirection Direction { get; init; } = PBarDirection.Right;

    /// <summary>Optional image used as the foreground fill.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Foreground colour (8-digit ARGB hex) when no image is provided.</summary>
    public string Foreground { get; init; } = "FFFFFFFF";

    /// <summary>Background colour drawn behind the bar.</summary>
    public string Background { get; init; } = "00000000";

    /// <summary>Width in theme-local pixels.</summary>
    public double Width { get; init; }

    /// <summary>Height in theme-local pixels.</summary>
    public double Height { get; init; }

    /// <summary>Anchor at the centre of the rectangle instead of the top-left.</summary>
    public bool Center { get; init; }
}
