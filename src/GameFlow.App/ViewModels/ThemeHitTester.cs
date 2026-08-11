using GameFlow.Infrastructure.Theming.Flee;
using GameFlow.Infrastructure.Theming.Models;

namespace GameFlow.App.ViewModels;

/// <summary>
/// Result of a hit test against a theme document — the logical element
/// id, the bounding rect of the element that was hit (in theme-local
/// coordinates, i.e. the same space the theme.json x/y/width/height
/// fields use), and the theme-relative path of the element's own art.
/// <see cref="GameFlow.App.Views.ThemeSurface"/> uses that art as an
/// opacity mask so the hover/press highlight takes the SHAPE of the
/// button itself (a tinted silhouette of the same overlay the theme
/// shows during play) rather than an axis-aligned rectangle that fits
/// nothing. <see cref="ShapeImagePath"/> is null when the element has
/// no image of its own (colour-only pbar) — the surface then falls back
/// to a rounded rect.
/// </summary>
public sealed record ThemeHitResult(string ElementId, Avalonia.Rect Bounds, string? ShapeImagePath = null);

/// <summary>
/// Resolves a click in theme-local coordinates to a logical controller
/// element id (e.g. <c>"south"</c>, <c>"east"</c>, <c>"l1"</c>) so
/// click-to-map can route through the existing
/// <see cref="ControllerVisualStateViewModel.SelectElementCommand"/>
/// pipeline that the programmatic art already uses.
///
/// <para>
/// The mapping is derived from <see cref="ShowHideNode.Input"/>'s string
/// representation: a theme element wrapped in <c>{ "type": "showhide",
/// "input": "quad_right:s", ... }</c> declares "this region of the
/// canvas is the South face button". A click whose theme-local
/// coordinates fall inside the descendant <see cref="ImageNode"/>'s
/// rectangle is mapped back to <c>"south"</c>.
/// </para>
///
/// <para>
/// Walks the tree in render order and returns the LAST hit, mirroring
/// the visual stacking — clicks on overlapping regions go to the topmost
/// (last-drawn) element, which matches user expectation.
/// </para>
/// </summary>
public static class ThemeHitTester
{
    /// <summary>
    /// Logical element id returned for a theme's <c>touch_center:click</c>
    /// region. Consumers should use <see cref="IsTouchpadHit"/> instead of
    /// comparing against the source expression, because hit results carry
    /// logical control ids rather than Flee variable names.
    /// </summary>
    public const string TouchpadElementId = "Touchpad";

    public static bool IsTouchpadHit(ThemeHitResult? hit) =>
        string.Equals(hit?.ElementId, TouchpadElementId, StringComparison.Ordinal);

    /// <summary>
    /// Tests a theme-local (x, y) against the document's interactive
    /// elements. Returns the matched element + bounds, or
    /// <see langword="null"/> when the point doesn't fall on any
    /// mappable region.
    /// </summary>
    public static ThemeHitResult? TryHit(ThemeDocument document, double x, double y)
    {
        ThemeHitResult? topmost = null;
        foreach (var node in document.Children)
        {
            WalkAndHit(node, x, y, 0, 0, null, ref topmost);
        }
        return topmost;
    }

    /// <summary>
    /// Recursive walk that accumulates the transform offset from each
    /// node's X/Y and tracks the nearest enclosing <c>showhide</c>
    /// input expression. Rotation is intentionally ignored — themes
    /// rarely rotate hit-targets, and an axis-aligned rectangle is much
    /// cheaper to test than a rotated quad. Click-to-map remains
    /// usable in the rare-rotated case; the user might just need to
    /// click slightly off-centre.
    /// </summary>
    private static void WalkAndHit(
        ThemeNode node,
        double x, double y,
        double offsetX, double offsetY,
        string? currentInputBinding,
        ref ThemeHitResult? topmost)
    {
        var nx = offsetX + node.X;
        var ny = offsetY + node.Y;

        switch (node)
        {
            case ShowHideNode show:
                // Treat the input expression as the binding for everything
                // underneath unless a nested showhide overrides it.
                {
                    var nestedBinding = FirstVariableBinding(show.Input) ?? currentInputBinding;
                    foreach (var child in show.Children)
                    {
                        WalkAndHit(child, x, y, nx, ny, nestedBinding, ref topmost);
                    }
                }
                return;

            case SliderNode slider:
                // Stick zones are special: we want a SINGLE hit result
                // for the whole stick area that distinguishes "clicked
                // the centre" (L3 / R3 click) from "clicked the outer
                // ring" (analog axes), rather than letting every nested
                // ImageNode return the same click id and clobber the
                // analog selection. Handled out-of-line.
                HitTestSlider(slider, x, y, nx, ny, ref topmost);
                return;

            case ImageNode image:
                {
                    var rectX = nx - (image.Center ? image.Width  / 2 : 0);
                    var rectY = ny - (image.Center ? image.Height / 2 : 0);
                    if (image.Width > 0 && image.Height > 0 &&
                        x >= rectX && x < rectX + image.Width &&
                        y >= rectY && y < rectY + image.Height)
                    {
                        // Bind the click to the nearest showhide input
                        // we walked through. The first child of a theme
                        // document is typically the bare base-image
                        // (no enclosing showhide), so that one resolves
                        // to null and we skip it — which is the
                        // desired behaviour (clicks on the controller
                        // body shouldn't map to a button).
                        var id = MapInputExpressionToElementId(currentInputBinding);
                        if (id is not null)
                        {
                            topmost = new ThemeHitResult(id,
                                new Avalonia.Rect(rectX, rectY, image.Width, image.Height),
                                string.IsNullOrWhiteSpace(image.ImagePath) ? null : image.ImagePath);
                        }
                    }
                    foreach (var child in image.Children)
                    {
                        WalkAndHit(child, x, y, nx, ny, currentInputBinding, ref topmost);
                    }
                }
                return;

            case PBarNode bar:
                // Triggers — clicking the trigger pull area maps to the
                // trigger button. We treat the entire bar rect as the
                // hit area regardless of the current pull state.
                {
                    var rectX = nx - (bar.Center ? bar.Width  / 2 : 0);
                    var rectY = ny - (bar.Center ? bar.Height / 2 : 0);
                    if (bar.Width > 0 && bar.Height > 0 &&
                        x >= rectX && x < rectX + bar.Width &&
                        y >= rectY && y < rectY + bar.Height)
                    {
                        var expr = FirstVariableBinding(bar.Input);
                        var triggerId = MapInputExpressionToElementId(expr);
                        if (triggerId is not null)
                        {
                            topmost = new ThemeHitResult(triggerId,
                                new Avalonia.Rect(rectX, rectY, bar.Width, bar.Height),
                                string.IsNullOrWhiteSpace(bar.ImagePath) ? null : bar.ImagePath);
                        }
                    }
                    foreach (var child in bar.Children)
                    {
                        WalkAndHit(child, x, y, nx, ny, currentInputBinding, ref topmost);
                    }
                }
                return;

            default:
                foreach (var child in node.Children)
                {
                    WalkAndHit(child, x, y, nx, ny, currentInputBinding, ref topmost);
                }
                return;
        }
    }

    /// <summary>
    /// Walks a Flee AST and returns the first colon-containing variable
    /// name found — that's the one we treat as the binding for hit-test
    /// purposes. Compound expressions like <c>"bumpers:l or bumpers:r"</c>
    /// resolve to <c>"bumpers:l"</c>, which is good enough for
    /// click-to-map (the user can right-click to disambiguate later).
    /// </summary>
    private static string? FirstVariableBinding(FleeNode node)
    {
        switch (node)
        {
            case VariableNode v when v.Name.Contains(':'):
                return v.Name;

            case UnaryNode u:
                return FirstVariableBinding(u.Operand);

            case BinaryNode b:
                return FirstVariableBinding(b.Left) ?? FirstVariableBinding(b.Right);

            case CallNode c:
                foreach (var arg in c.Arguments)
                {
                    var inner = FirstVariableBinding(arg);
                    if (inner is not null) { return inner; }
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Maps a theme variable binding string (e.g.
    /// <c>"quad_right:s"</c>) to the element id the rest of the app
    /// uses (e.g. <c>"south"</c>). Returns <see langword="null"/> for
    /// inputs that aren't mappable to a button.
    /// </summary>
    /// <summary>
    /// Maps a Flee variable binding from a theme's input expression
    /// (e.g. <c>"quad_right:s"</c>) to a logical element id that the
    /// downstream <see cref="ControlRuleMatcher"/> and
    /// <see cref="ControlMappingDialogViewModel"/> can resolve. The
    /// returned strings must match the <c>ButtonId</c> enum names
    /// (PascalCase, no hyphens) — <c>ControlRuleMatcher.TryResolveButtonId</c>
    /// falls back to <c>Enum.TryParse(key.Split('.', 2)[0], …)</c>, so
    /// anything that isn't a valid enum identifier silently fails and
    /// every UI field downstream of the selection ends up blank.
    ///
    /// <para>
    /// For analog sticks the slider hit-test (in <see cref="WalkAndHit"/>)
    /// chooses between <c>"LeftStick"</c> (analog axes) and
    /// <c>"LeftStick.Button"</c> (the L3 click) based on click distance
    /// from the stick's centre, so this table only carries the click
    /// variants that come directly from a theme's <c>:click</c>
    /// expression.
    /// </para>
    /// </summary>
    private static string? MapInputExpressionToElementId(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return null;
        }

        return binding.Trim().ToLowerInvariant() switch
        {
            // Face buttons — Enum.TryParse picks these up case-insensitively
            "quad_right:s" => "South",
            "quad_right:e" => "East",
            "quad_right:w" => "West",
            "quad_right:n" => "North",

            // D-pad — must match enum casing (DpadUp, not dpad-up)
            "quad_left:n" => "DpadUp",
            "quad_left:s" => "DpadDown",
            "quad_left:w" => "DpadLeft",
            "quad_left:e" => "DpadRight",

            // Bumpers
            "bumpers:l" => "LeftShoulder",
            "bumpers:r" => "RightShoulder",

            // Triggers (both the digital "bumper2" and analog "trigger"
            // variants resolve to the same logical click button — the
            // user pressing the trigger past its threshold).
            "bumpers2:l" or "triggers:l" or "triggers:l:analog" or "triggers:l:stage2"
                => "LeftTrigger.Button",
            "bumpers2:r" or "triggers:r" or "triggers:r:analog" or "triggers:r:stage2"
                => "RightTrigger.Button",

            // Menu / system
            "menu:l" => "Back",
            "menu:r" => "Start",
            "home" or "home:home" => "Guide",
            // No "mute" enum entry — closest available is Misc1, which
            // is what controllers expose the mute button as in raw HID.
            "home:mute" => "Misc1",

            // Stick clicks (analog axes handled by HitTestSlider below)
            "stick_left:click"  => "LeftStick.Button",
            "stick_right:click" => "RightStick.Button",

            // Touchpad
            "touch_center:click" => TouchpadElementId,

            _ => null,
        };
    }

    /// <summary>
    /// Best-effort detection of which analog stick a slider node
    /// represents, based on the slider's InputX/InputY formulas.
    /// </summary>
    private static string? GuessSliderStickBinding(SliderNode slider)
    {
        var x = FirstVariableBinding(slider.InputX);
        var y = FirstVariableBinding(slider.InputY);

        var probe = x ?? y;
        if (probe is null) { return null; }

        if (probe.Contains("stick_left",  StringComparison.OrdinalIgnoreCase))
        {
            return "stick_left:click";
        }
        if (probe.Contains("stick_right", StringComparison.OrdinalIgnoreCase))
        {
            return "stick_right:click";
        }
        return null;
    }

    /// <summary>
    /// Hit-tests an analog stick (slider) zone with centre-vs-outer
    /// differentiation:
    /// <list type="bullet">
    /// <item><b>Inside 30 % of the stick-well radius</b> → resolves to
    ///   <c>LeftStick.Button</c> or <c>RightStick.Button</c>
    ///   (the L3/R3 click).</item>
    /// <item><b>Outside that radius, still within the well rect</b> →
    ///   resolves to <c>LeftStick</c> or <c>RightStick</c>
    ///   (the analog axes — both X and Y under one mapping).</item>
    /// </list>
    /// Uses the slider's first ImageNode child as the bounding rect
    /// (that's the rest-state stick well image in every generated
    /// theme); the click overlay nested inside a ShowHide sits on top
    /// of it with identical bounds, so we don't lose anything by not
    /// walking deeper.
    /// </summary>
    private static void HitTestSlider(
        SliderNode slider,
        double x, double y,
        double offsetX, double offsetY,
        ref ThemeHitResult? topmost)
    {
        // Primary = first ImageNode child (the always-rendered stick
        // well). All variants generated by themes/_generate.py put
        // that image first in the slider's children list.
        ImageNode? primary = null;
        foreach (var c in slider.Children)
        {
            if (c is ImageNode im) { primary = im; break; }
        }
        if (primary is null) { return; }

        var rectX = offsetX + primary.X - (primary.Center ? primary.Width  / 2 : 0);
        var rectY = offsetY + primary.Y - (primary.Center ? primary.Height / 2 : 0);

        if (primary.Width <= 0 || primary.Height <= 0) { return; }
        if (x < rectX || x >= rectX + primary.Width)  { return; }
        if (y < rectY || y >= rectY + primary.Height) { return; }

        // Determine which stick this is from the slider's InputX/Y
        // formula (e.g. "stick_left:x * 25" → left stick).
        var binding = GuessSliderStickBinding(slider) ?? string.Empty;
        var isLeft = binding.Contains("stick_left", StringComparison.OrdinalIgnoreCase);

        // Normalised distance from the centre of the well's ellipse.
        // Distance is measured in unit-circle space so non-square
        // wells still get a sensible "inner 30 %" zone.
        var cx = rectX + primary.Width  / 2;
        var cy = rectY + primary.Height / 2;
        var dx = (x - cx) / (primary.Width  / 2);
        var dy = (y - cy) / (primary.Height / 2);
        var distNorm = Math.Sqrt(dx * dx + dy * dy);

        const double centerThreshold = 0.30;
        var id = distNorm < centerThreshold
            ? (isLeft ? "LeftStick.Button" : "RightStick.Button")
            : (isLeft ? "LeftStick" : "RightStick");

        topmost = new ThemeHitResult(id,
            new Avalonia.Rect(rectX, rectY, primary.Width, primary.Height),
            string.IsNullOrWhiteSpace(primary.ImagePath) ? null : primary.ImagePath);
    }
}
