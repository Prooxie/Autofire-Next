using GameFlow.Infrastructure.Theming.Flee;

namespace GameFlow.Infrastructure.Runtime.Web.Overlay;

/// <summary>
/// A theme compiled into something a browser can draw.
///
/// <para>
/// The overlay does NOT ship theme.json to the page. A theme's behaviour
/// lives in Flee expressions — <c>"inputY": "triggers:l:analog * 14"</c> —
/// and honouring them in the browser would mean a second implementation
/// of the expression language and of <c>ControllerStateSymbols</c>, in a
/// different language, kept in agreement with this one forever. The two
/// would drift, and the failure mode is a theme that renders subtly wrong
/// only in OBS.
/// </para>
///
/// <para>
/// So the split is: everything static about the theme — the tree, the
/// geometry, which image goes where — is sent once as this program, and
/// every expression is replaced by an INDEX into a flat array of values
/// that the server evaluates and sends per frame. The page is then a
/// dumb tree-walker with no notion of what a trigger is. Adding an
/// expression form to the theme engine cannot break it, and the numbers
/// on screen are by construction the same numbers the app's own surface
/// drew from.
/// </para>
/// </summary>
public sealed record OverlayProgram
{
    /// <summary>Theme registry id, echoed back so the page can label itself.</summary>
    public required string ThemeId { get; init; }

    /// <summary>Theme display name ("Midnight Black").</summary>
    public required string ThemeName { get; init; }

    /// <summary>Theme-local canvas size. Treat as a viewBox — the page scales it to the browser source.</summary>
    public required double Width { get; init; }
    public required double Height { get; init; }

    /// <summary>The drawable tree, in paint order.</summary>
    public required IReadOnlyList<OverlayNode> Nodes { get; init; }

    /// <summary>
    /// Absolute on-disk path of every image the tree references, indexed
    /// by the <see cref="OverlayNode.Image"/> values inside it.
    ///
    /// <para>
    /// The page asks for art by INDEX, never by path. That is what makes
    /// the asset route safe without a traversal check: there is no
    /// caller-supplied path to sanitise, and the only files reachable
    /// over HTTP are ones this build already resolved out of the theme
    /// itself. A request for an index that does not exist is a 404 and
    /// nothing more.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> ImagePaths { get; init; }

    /// <summary>
    /// Every expression in the theme, flattened. The per-frame message is
    /// this array evaluated against a snapshot, in this order; nodes refer
    /// to entries by index.
    /// </summary>
    public required IReadOnlyList<FleeNode> ValueSlots { get; init; }

    /// <summary>
    /// One entry per <c>trailpad</c> node, indexed by
    /// <see cref="OverlayNode.Pad"/>. Kept out of
    /// <see cref="ValueSlots"/> because a trailpad's position expressions
    /// are evaluated once PER FINGER — with that finger substituted as
    /// contact 0, since themes only ever reference contact 0 — so a
    /// single slot could not hold the answer.
    /// </summary>
    public required IReadOnlyList<OverlayTrailPad> TrailPads { get; init; }

    /// <summary>Images the theme references that could not be found on disk. Diagnostic only; the tree simply omits them.</summary>
    public required IReadOnlyList<string> MissingImages { get; init; }
}

/// <summary>
/// The position expressions of one <c>trailpad</c> node, re-evaluated per
/// contact. See <see cref="OverlayProgram.TrailPads"/>.
/// </summary>
public sealed record OverlayTrailPad(FleeNode Visible, FleeNode X, FleeNode Y);

/// <summary>
/// One node of the compiled tree. A single record rather than a
/// hierarchy: this is a wire DTO that becomes untyped JSON the moment it
/// leaves, so the field set the page reads is easier to keep honest when
/// it is all visible in one place. <see cref="Kind"/> says which fields
/// mean anything.
///
/// <para>
/// Index fields are -1 when absent, matching the page's checks. Names are
/// short because this is JSON on a socket, and the tree for a full
/// controller theme is a few hundred nodes.
/// </para>
/// </summary>
public sealed record OverlayNode
{
    /// <summary>
    /// <c>g</c> group · <c>img</c> image · <c>light</c> lightbar ·
    /// <c>show</c> conditional · <c>slide</c> translation ·
    /// <c>trail</c> touch marker · <c>bar</c> progress fill.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Position in theme-local pixels, and rotation in degrees clockwise.</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public double R { get; init; }

    /// <summary>Index into <see cref="OverlayProgram.ImagePaths"/>, or -1.</summary>
    public int Image { get; init; } = -1;

    /// <summary>Size in theme-local pixels. 0 means "the bitmap's own size", resolved by the page once the image loads.</summary>
    public double W { get; init; }
    public double H { get; init; }

    /// <summary>Anchor at the centre rather than the top-left.</summary>
    public bool Center { get; init; }

    /// <summary>Value-slot index: visibility for <c>show</c>, current value for <c>bar</c>. -1 when unused.</summary>
    public int Value { get; init; } = -1;

    /// <summary>Value-slot indices for a <c>slide</c>'s offset and rotation. -1 when unused.</summary>
    public int ValueX { get; init; } = -1;
    public int ValueY { get; init; } = -1;
    public int ValueR { get; init; } = -1;

    /// <summary>Value-slot indices for a <c>bar</c>'s range. -1 when unused.</summary>
    public int Min { get; init; } = -1;
    public int Max { get; init; } = -1;

    /// <summary>Fill direction for a <c>bar</c>: <c>up</c>, <c>down</c>, <c>left</c> or <c>right</c>.</summary>
    public string? Dir { get; init; }

    /// <summary>CSS colours for an image-less <c>bar</c>, converted from the theme's ARGB hex.</summary>
    public string? Fg { get; init; }
    public string? Bg { get; init; }

    /// <summary>Index into <see cref="OverlayProgram.TrailPads"/> for a <c>trail</c>, else -1.</summary>
    public int Pad { get; init; } = -1;

    public IReadOnlyList<OverlayNode> Children { get; init; } = [];
}
