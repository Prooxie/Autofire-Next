using System.Collections.Concurrent;
using GameFlow.App.ViewModels;
using GameFlow.Core.Models;
using GameFlow.Core.Enums;
using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Flee;
using GameFlow.Infrastructure.Theming.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Serilog;

namespace GameFlow.App.Views;

/// <summary>
/// Avalonia control that paints a parsed VSCView-compatible
/// <see cref="ThemeDocument"/> against a live
/// <see cref="ControllerSnapshot"/>. The host pushes <see cref="ActiveTheme"/>
/// and snapshot updates imperatively via <see cref="UpdateState"/>;
/// no styled-property bindings are involved on the hot path.
///
/// <para>
/// The control also fires <see cref="Clicked"/> on pointer-pressed with
/// the click position translated into theme-local coordinates, so the
/// host can hit-test the click against the theme's interactive elements
/// via <see cref="GameFlow.App.ViewModels.ThemeHitTester"/>. This is the
/// foundation for the "click a button on the controller to map it"
/// workflow.
/// </para>
/// </summary>
public sealed class ThemeSurface : Control
{
    /// <summary>
    /// Bitmap cache keyed on the absolute resolved path of the image.
    /// Stores <see langword="null"/> for paths that failed to load so a
    /// single missing PNG does not cause the whole tree to throw on
    /// every render tick. Shared across instances because two
    /// ThemeSurfaces (physical + virtual) typically reuse the same
    /// base PNG.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Bitmap?> BitmapCache = new();

    /// <summary>
    /// Image references that could not be resolved, keyed
    /// <c>{themeId}|{imagePath}</c>. Separate from
    /// <see cref="BitmapCache"/> because that one is keyed on the RESOLVED
    /// absolute path, which by definition does not exist for a miss — so a
    /// failure had no cache entry and was retried on every single frame.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> ResolveFailureCache = new();

    /// <summary>Paths currently being decoded, so concurrent surfaces queue one decode between them.</summary>
    private static readonly ConcurrentDictionary<string, byte> DecodesInFlight = new();

    /// <summary>
    /// Display-sized copies of theme art, keyed on the absolute resolved
    /// path — the same key <see cref="BitmapCache"/> uses, so one entry
    /// serves every surface drawing that image.
    ///
    /// <para>
    /// This exists so the per-frame draw can use a CHEAP filter without
    /// the art getting worse. Theme images are authored at document
    /// resolution — a DualSense body is 1467x816 — and drawn into a panel
    /// a few hundred pixels wide, which is a large enough downscale that a
    /// cheap filter aliases visibly. Resampling once, at high quality,
    /// into a copy the size it is actually drawn at moves that cost off
    /// the frame: what remains is a near-1:1 draw, where the filter choice
    /// stops mattering. See <see cref="Render"/> for the measurements.
    /// </para>
    ///
    /// <para>
    /// One entry per path rather than one per (path, size): a superseded
    /// size is dropped on the next request. The old bitmap is NOT disposed
    /// — the compositor may still hold it for a frame already submitted,
    /// and a disposed bitmap there is a crash, whereas an unreferenced one
    /// is a collection. Sizes snap up by <see cref="ScaleStepFraction"/> so
    /// dragging a window edge re-scales in steps instead of once per pixel
    /// of drag.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, ScaledBitmap> ScaledCache = new();

    /// <summary>Paths currently being re-scaled, so concurrent surfaces queue one scale between them.</summary>
    private static readonly ConcurrentDictionary<string, byte> ScalesInFlight = new();

    /// <summary>A display-sized copy of one theme image, and the size it was built for.</summary>
    private readonly record struct ScaledBitmap(Bitmap Bitmap, PixelSize Size);

    /// <summary>
    /// Target sizes snap up to a step this fraction of themselves. A window
    /// being dragged produces a new width every frame, and without a step
    /// each one would queue a fresh scale of every image in the theme.
    ///
    /// <para>
    /// The step is RELATIVE, not a fixed pixel count, because it bounds
    /// something that matters: whatever the step is, a cached copy can
    /// overshoot its destination by that much, and the frame draws it with
    /// the cheap filter. An absolute quantum bounds that overshoot at a
    /// ratio for large images and at 2x for small ones — a 33 px draw
    /// snapping to 64 — which is precisely the downscale the cheap filter
    /// cannot take. A relative one holds it near 1.125x at every size.
    /// </para>
    /// </summary>
    private const double ScaleStepFraction = 0.125;

    /// <summary>Floor for the relative step, so tiny images do not re-scale on every pixel.</summary>
    private const int MinimumScaleStep = 4;

    /// <summary>
    /// Only pre-scale when the source is at least this many times larger
    /// than the target, by area. Below it the resampling being avoided is
    /// not worth a second copy of the image in memory, and scaling to a
    /// near-identical size would visibly soften the art for nothing.
    /// </summary>
    private const double ScaleWorthwhileRatio = 2.0;

    /// <summary>
    /// Raised on the UI thread when a background decode lands, so surfaces
    /// that drew without the art can pick it up. Static because the cache
    /// is: one decode serves every surface using that image.
    /// </summary>
    private static event Action? BitmapDecoded;

    private readonly ControllerStateSymbols symbols = new();

    private InstalledTheme? activeTheme;
    private ControllerSnapshot snapshot = ControllerSnapshot.Empty();
    private string lightColor = "#00000000";
    private IBrush? lightbarBrush;
    // The snapshot we last actually painted. UpdateState compares incoming
    // frames against THIS (not merely the previous frame) so slow continuous
    // movement still repaints once it accumulates past the threshold, while a
    // stream of visually-identical frames is dropped.
    private ControllerSnapshot? lastRenderedSnapshot;

    // One-shot diagnostic guard so the first live button press
    // reaching the surface gets a single Info-level log line —
    // useful for diagnosing "no feedback" reports.
    private bool firstButtonPressLogged;

    /// <summary>
    /// Whether the last <see cref="UpdateState"/> found this surface on
    /// screen. Used to force a repaint on the transition back into view.
    /// </summary>
    private bool wasOnScreen;

    // Throttled feedback-diagnostic state. Once per second per surface
    // we dump the snapshot's pressed-button count + the eval result of
    // every top-level showhide expression, so users diagnosing "no live
    // feedback" can confirm whether the snapshot is reaching the
    // surface and whether the Flee symbol table is resolving the
    // expected variables.
    private DateTime lastFeedbackDiagnostic = DateTime.MinValue;

    public ThemeSurface()
    {
        // Intentionally NOT focusable — pointer events (Move/Pressed/
        // Exited) fire regardless of focusability, but Focusable=true
        // would put the surface into the Tab cycle and let it steal
        // keyboard focus from neighbouring controls when clicked. That
        // turned out to break the variant ComboBox's popup: opening the
        // dropdown would briefly transfer focus to the surface, the
        // popup would lose its keyboard chain, and the next pointer
        // event would close the popup before the user could select an
        // item. Click-to-map works fine without focus.
        Focusable = false;

        // Control doesn't expose a Background styled property like
        // Panel/Border do — but we still need the full bounds to
        // catch pointer-pressed for click-to-map. The Render method
        // below draws a transparent fill across Bounds before any
        // theme art, which gives Avalonia a hit-testable region.
    }

    /// <summary>
    /// Theme currently being rendered, or <see langword="null"/> when
    /// no theme is set. Renamed from <c>Theme</c> to avoid colliding
    /// with the inherited <see cref="StyledElement.Theme"/> styled
    /// property.
    /// </summary>
    public InstalledTheme? ActiveTheme
    {
        get => activeTheme;
        set
        {
            if (ReferenceEquals(activeTheme, value)) { return; }
            var previous = activeTheme;
            activeTheme = value;

            // Log every theme change — not just the first. Useful for
            // diagnosing "I picked a different skin and nothing
            // changed" reports: if this line appears in the log with
            // the new theme name, the surface IS receiving the new
            // theme and the bug is downstream (render path). If it
            // doesn't appear, the variant ComboBox isn't propagating
            // its change up through SelectedThemeVariant and the bug
            // is upstream.
            if (value is not null)
            {
                Log.Information(
                    "ThemeSurface theme {Verb}: '{Name}' from {Dir} ({Children} root child(ren), canvas {W}x{H}).",
                    previous is null ? "loaded" : "swapped",
                    value.Document.Name, value.Document.BaseDirectory,
                    value.Document.Children.Count,
                    value.Document.Width, value.Document.Height);
            }
            else
            {
                Log.Information("ThemeSurface theme cleared.");
            }
            highlightMaskCache.Clear();
            lightbarMaskCache.Clear();
            hoveredHit = null;
            pressedHit = null;
            PrewarmArt(value);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Pushes a new snapshot and schedules a redraw — but only when
    /// the snapshot reference actually differs. The poll timer in
    /// <see cref="GameFlow.App.Views.ControllerSurface"/> calls this
    /// every 33 ms; the upstream VM, however, only re-assigns its
    /// <c>snapshot</c> field when there's a real input change (its
    /// own dirty-check short-circuits idle ticks). So this ref-equality
    /// guard collapses idle ticks into no-ops, which keeps the surface
    /// from forcing a window-wide re-composite every 33 ms — that
    /// re-composite was interfering with the variant-picker ComboBox's
    /// popup hover state ("flickering over the choice").
    /// </summary>
    /// <summary>
    /// Repaints once on becoming visible again. <see cref="UpdateState"/>
    /// suppresses invalidation while hidden, so without this a surface
    /// returning to view would keep showing whatever frame was current
    /// when it left until the next state change — which, for a pad sitting
    /// still, could be a long time.
    /// </summary>
    /// <summary>
    /// Subscribes to background decode completions while attached.
    /// Unsubscribed on detach — the event is static, so a surface that
    /// stayed subscribed would be kept alive by it for the life of the
    /// process, and every closed dashboard would leave another one behind
    /// repainting itself.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BitmapDecoded += OnBitmapDecoded;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        BitmapDecoded -= OnBitmapDecoded;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Starts a decode for every image the theme references, including
    /// the ones an idle render never touches.
    /// </summary>
    /// <remarks>
    /// Theme bitmaps decode on a background thread and the first request
    /// for one returns nothing. For art the ordinary render draws that is
    /// invisible — it is faulted in during the first paint and cached long
    /// before anyone can interact. The click and active overlays are drawn
    /// only while a control is held, so the first request for them is the
    /// user's first HOVER: the highlight has no silhouette to use for that
    /// frame and falls back to a rectangle, then corrects itself once the
    /// decode lands. Correct, but the wrong shape flashes up first, and it
    /// happens once per control.
    ///
    /// <para>
    /// Walking the document when the theme is set moves that work to a
    /// moment when nothing is waiting on it. The decodes are already
    /// asynchronous and deduplicated by path across every surface, so a
    /// pack shared by eight panels is still decoded once.
    /// </para>
    /// </remarks>
    /// <summary>
    /// True while any theme bitmap is still being decoded.
    /// </summary>
    /// <remarks>
    /// Startup faults in the better part of three hundred images across a
    /// multi-slot dashboard, and every completed decode posts a repaint.
    /// The dispatcher is genuinely saturated for that stretch — but by
    /// work that ends, and that says nothing about what the machine can
    /// sustain. The adaptive tick rate used to read it as a verdict on the
    /// hardware and step 165 Hz down to its 10 Hz floor inside a second
    /// and a half, then stay there.
    /// </remarks>
    internal static bool HasPendingDecodes => !DecodesInFlight.IsEmpty;

    private static void PrewarmArt(InstalledTheme? theme)
    {
        if (theme is null)
        {
            return;
        }

        foreach (var node in theme.Document.Children)
        {
            PrewarmNode(node, theme);
        }
    }

    private static void PrewarmNode(ThemeNode node, InstalledTheme theme)
    {
        var path = node switch
        {
            ImageNode image => image.ImagePath,
            LightbarNode lightbar => lightbar.ImagePath,
            TrailPadNode trail => trail.ImagePath,
            PBarNode bar => bar.ImagePath,
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(path))
        {
            // Return value deliberately discarded: the point is to start
            // the decode, which the first call does on its way to null.
            _ = LoadBitmap(path, theme);
        }

        foreach (var child in node.Children)
        {
            PrewarmNode(child, theme);
        }
    }

    private void OnBitmapDecoded()
    {
        // Only the surfaces actually on screen need the repaint; the rest
        // will pick the art up whenever they next come into view.
        if (activeTheme is not null && IsEffectivelyVisible)
        {
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == Visual.IsVisibleProperty &&
            change.GetNewValue<bool>() &&
            activeTheme is not null)
        {
            InvalidateVisual();
        }
    }

    /// <summary>
    /// True when any part of this surface is actually inside the clip the
    /// renderer will apply — i.e. it is on screen, not merely present.
    ///
    /// <para>
    /// <see cref="Visual.IsEffectivelyVisible"/> is not enough. The
    /// dashboard lists every slot in a ScrollViewer, and a panel scrolled
    /// out of view is still "visible" by that definition: it is in the
    /// tree, its parents are visible, and nothing about it is collapsed.
    /// It just is not on screen. With a stack of controllers that is most
    /// of them, and each one was repainting tens of megapixel-scale layers
    /// per tick for nothing — which is how the dispatcher ended up 8.7
    /// seconds behind.
    /// </para>
    ///
    /// <para>
    /// Cost scales with the number of slots, so this is the difference
    /// between paying for what is on screen and paying for everything the
    /// user has ever added.
    /// </para>
    /// </summary>
    private bool IsWithinViewport()
    {
        // Walk up to the nearest scroll viewport and test against it.
        // Avalonia's own TransformedBounds/Clip is not public API here, so
        // the intersection is computed directly.
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            // Never laid out; nothing is on screen yet.
            return false;
        }

        var viewport = this.FindAncestorOfType<ScrollViewer>();
        if (viewport is null)
        {
            // Not inside a scroller, so visibility alone decided it.
            return true;
        }

        var topLeft = this.TranslatePoint(default, viewport);
        if (topLeft is null)
        {
            return false;
        }

        var inViewport = new Rect(topLeft.Value, bounds.Size);
        var visible = new Rect(viewport.Bounds.Size);

        return inViewport.Intersects(visible);
    }

    public void UpdateState(ControllerSnapshot newSnapshot)
    {
        if (ReferenceEquals(snapshot, newSnapshot)) { return; }
        snapshot = newSnapshot;
        if (activeTheme is null) { return; }

        // A surface the user cannot see must not ask to be repainted.
        // Several of these live in the tree at once — the dashboard's
        // physical and virtual panels plus the tuning tab's — and every
        // one of them is fed by the same runtime tick regardless of which
        // tab is on top. Each repaint composites tens of megapixel-scale
        // layers, so invalidating the hidden ones spends most of the
        // frame budget on pixels that are never shown.
        //
        // The snapshot is still stored, so becoming visible again paints
        // current state rather than a stale frame.
        var onScreen = IsEffectivelyVisible && IsWithinViewport();
        if (!onScreen)
        {
            wasOnScreen = false;
            return;
        }

        // Coming back on screen forces one repaint, bypassing the dirty
        // check below. While off screen we deliberately stopped
        // invalidating, so the composition layer still holds whatever was
        // drawn when it left — and for a pad sitting still the dirty check
        // would agree nothing changed and leave that stale frame up.
        if (!wasOnScreen)
        {
            wasOnScreen = true;
            lastRenderedSnapshot = newSnapshot;
            InvalidateVisual();
            return;
        }

        // Value-based dirty check. The runtime hands us a fresh snapshot
        // object every tick (new Timestamp) even when the pad is at rest, and
        // a connected controller streams continuously — so without this we'd
        // repaint the entire theme tree 30x/sec for visually identical state,
        // which is what made the whole UI sluggish whenever a controller was
        // attached. Only repaint when something the art can actually show has
        // changed; otherwise keep the latest snapshot but skip the paint.
        // A cooling trail and burning sparks are animations with no input
        // behind them: the snapshot stops changing the moment the finger
        // stops or lifts, and without this they would freeze mid-fade and
        // sit there until something else happened to force a paint.
        if (touchTrail.HasPoints || touchParticles.HasParticles)
        {
            lastRenderedSnapshot = newSnapshot;
            InvalidateVisual();
            return;
        }

        if (lastRenderedSnapshot is not null
            && SnapshotVisuals.AreEquivalent(lastRenderedSnapshot, newSnapshot))
        {
            return;
        }

        lastRenderedSnapshot = newSnapshot;
        InvalidateVisual();
    }

    /// <summary>
    /// Updates the colour consumed by <see cref="LightbarNode"/> elements.
    /// The brush is parsed once when settings change, never in the render
    /// loop. Invalid or transparent values simply switch the light off.
    /// </summary>
    public void UpdateLightColor(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "#00000000" : value;
        if (string.Equals(lightColor, normalized, StringComparison.OrdinalIgnoreCase)) { return; }

        lightColor = normalized;
        lightbarBrush = IsTransparentColor(normalized) ? null : HexBrush(normalized);
        InvalidateVisual();
    }

    /// <summary>
    /// When true, the surface renders ONLY the controller's base image
    /// (the first <see cref="ImageNode"/> child of the theme document)
    /// and skips every <c>showhide</c>/<c>pbar</c>/active overlay. This
    /// fulfils the "physical view = original model only, no live
    /// feedback" rule used for the input-side panel. The virtual panel
    /// keeps the full live-feedback render.
    /// </summary>
    public bool IsPhysicalView
    {
        get => isPhysicalView;
        set
        {
            if (isPhysicalView == value) { return; }
            isPhysicalView = value;
            InvalidateVisual();
        }
    }
    private bool isPhysicalView;

    /// <summary>
    /// Fires when the user clicks anywhere on the surface. Carries the
    /// click position in theme-local (canvas) coordinates so the host
    /// can hit-test it against the theme's interactive elements without
    /// having to know about display scaling, letterboxing or DPI.
    /// </summary>
    public event EventHandler<ThemeClickEventArgs>? Clicked;

    /// <summary>
    /// Last transform applied in Render — captured here so
    /// OnPointerPressed can invert it without re-walking the document.
    /// (uniformScale, offsetX, offsetY)
    /// </summary>
    private (double Scale, double OffsetX, double OffsetY) lastTransform;

    /// <summary>
    /// Theme units to DEVICE pixels for the frame being drawn — the
    /// uniform fit scale times the top level's render scaling. What
    /// <see cref="ForDisplay"/> needs in order to know the size a bitmap
    /// will actually occupy on the physical display, which is not the
    /// same as its size in the control's own coordinates at any DPI other
    /// than 100%.
    /// </summary>
    private double frameDeviceScale = 1.0;

    // ─── Hover + click-to-map highlight state ────────────────────────────
    //
    // Painted on top of all the regular theme content at the end of
    // Render, in the SHAPE of the element itself: the element's own art
    // (the same overlay the theme shows for a real press) is used as an
    // opacity mask and filled with the highlight tint — so hovering the
    // South button lights up a South-button silhouette, not a yellow
    // rectangle. Hover = lighter tint; held-down = stronger tint. There
    // is deliberately NO persistent selection state anymore: the
    // highlight exists only while the pointer is over/pressing the
    // element and disappears on release/leave (it used to stick around
    // until you clicked empty space, which read as a glitch). Clicked
    // still fires for the host so the VM's SelectElement pipeline runs.

    private ThemeHitResult? hoveredHit;

    /// <summary>Elements whose highlight path has been reported once.</summary>
    private readonly HashSet<string> highlightDiagnosed = new(StringComparer.Ordinal);
    private ThemeHitResult? pressedHit;

    /// <summary>
    /// Cached hand cursor used when the pointer is over a mappable
    /// element. Allocating a new <see cref="Cursor"/> every move tick
    /// was wasteful — this stays alive for the process lifetime.
    /// </summary>
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>
    /// Highlight tint. Bright amber so it contrasts cleanly with the
    /// typical dark UI as well as the cyan-teal of the active-press
    /// overlays in the asset pack. Alpha differs per state: hover is a
    /// clear "this is clickable", held-down is a solid "you're pressing
    /// this" — both filling the element's whole silhouette (outline and
    /// interior together, since the mask covers the full shape).
    /// </summary>
    private static readonly SolidColorBrush HighlightBrush =
        new(Color.FromArgb(0xE6, 0xFF, 0xC3, 0x00));

    /// <summary>
    /// Same amber, lighter. Hover and press draw the identical silhouette,
    /// so alpha is the only thing left to tell "you could click this" from
    /// "you are pressing this".
    /// </summary>
    private static readonly SolidColorBrush HoverHighlightBrush =
        new(Color.FromArgb(0x8C, 0xFF, 0xC3, 0x00));

    private static readonly Pen HighlightOutlinePen = new(HighlightBrush, 2);
    private static readonly Pen HoverOutlinePen = new(HoverHighlightBrush, 2);

    /// <summary>
    /// Per-path opacity-mask brushes for the silhouette highlight. Tiny
    /// (a handful of interactive elements per theme) but rebuilt at
    /// most once per art path instead of once per 30 Hz repaint.
    /// </summary>
    private readonly Dictionary<string, ImageBrush?> highlightMaskCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-theme light masks, reused across live animation frames.</summary>
    private readonly Dictionary<string, ImageBrush?> lightbarMaskCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sliders whose one-shot diagnostic has fired (see <see cref="LogSliderDiagnosticsOnce"/>).</summary>
    private readonly HashSet<SliderNode> sliderDiagnosticsLogged = [];

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Always defer to the parent's allocation. Returning the theme's
        // huge native size (1534x954) tells the layout system the
        // control wants that much space, which in an unconstrained
        // panel makes the whole window overflow. Stretch alignment in
        // the parent then gives us whatever space is actually free.
        var w = double.IsInfinity(availableSize.Width)  ? 480 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 280 : availableSize.Height;
        return new Size(w, h);
    }

    /// <inheritdoc/>
    // Render-cost telemetry: averages paint duration and reports every 5 s,
    // warning when repaints are expensive enough to throttle the UI thread.
    private double renderCostAccumMs;
    private int renderCostSamples;
    private DateTime lastRenderCostReportUtc = DateTime.UtcNow;

    public override void Render(DrawingContext context)
    {
        var renderTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
        base.Render(context);

        // Transparent fill: gives the control a hit-test surface so
        // OnPointerPressed fires on clicks anywhere in Bounds, not
        // just on rendered theme art. Zero visual cost.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var theme = activeTheme;
        if (theme is null) { return; }

        symbols.UpdateSnapshot(snapshot);

        // One-shot diagnostic: confirm the first time we see live
        // button data flow into the surface. Useful for debugging
        // "no feedback" reports — if this never fires while the user
        // is actively pressing buttons, the bug is upstream in the
        // input pipeline / VM update chain, NOT in the theme engine.
        if (!firstButtonPressLogged)
        {
            var pressedNow = snapshot.Buttons.PressedCount;
            if (pressedNow > 0)
            {
                firstButtonPressLogged = true;
                Log.Information(
                    "ThemeSurface[{Mode}] first live button press: device={Device} pressed={Pressed}",
                    isPhysicalView ? "physical" : "virtual",
                    snapshot.DeviceName,
                    pressedNow);
            }
        }

        // Throttled feedback diagnostic: once per second per surface, log
        // snapshot state + showhide eval results so the input → symbols →
        // expression chain can be confirmed.
        //
        // Debug level, and gated on Debug being ENABLED before any of the
        // work is done. Both matter. At Information this ran for every
        // surface on screen — two per slot, up to sixteen slots — and each
        // pass walks the node tree, evaluates an expression per ShowHide
        // node, and builds a string. That produced 60+ MB of log per day
        // and, worse, buried real warnings: diagnosing the theme artwork
        // regression meant grepping past hundreds of thousands of these
        // lines. A diagnostic that drowns the log it writes to has
        // negative value.
        //
        // The IsEnabled check is not redundant with the level: Serilog
        // evaluates arguments eagerly, so without it the tree walk and
        // string building would still happen on every tick and simply be
        // discarded.
        var now = DateTime.UtcNow;
        if ((now - lastFeedbackDiagnostic).TotalSeconds >= 1.0 &&
            Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
        {
            lastFeedbackDiagnostic = now;
            var pressed = snapshot.Buttons.PressedCount;
            var samples = new System.Text.StringBuilder();
            foreach (var node in theme.Document.Children)
            {
                if (node is ShowHideNode show && samples.Length < 200)
                {
                    var val = show.Input.Evaluate(symbols);
                    if (samples.Length > 0) { samples.Append(", "); }
                    var ast = show.Input;
                    var varName = ast is VariableNode v ? v.Name : ast.GetType().Name;
                    samples.Append(varName).Append('=').Append(val);
                }
            }
            Log.Debug(
                "ThemeSurface[{Mode}] tick: device={Device} pressed={Pressed}/{Total} L=({LX:F2},{LY:F2}) R=({RX:F2},{RY:F2}) LT={LT:F2} RT={RT:F2} showhide=[{Samples}]",
                isPhysicalView ? "physical" : "virtual",
                snapshot.DeviceName,
                pressed, ButtonState.Count,
                snapshot.LeftStick.X, snapshot.LeftStick.Y,
                snapshot.RightStick.X, snapshot.RightStick.Y,
                snapshot.LeftTrigger, snapshot.RightTrigger,
                samples.ToString());
        }

        var doc = theme.Document;
        if (doc.Width <= 0 || doc.Height <= 0) { return; }

        var scaleX = Bounds.Width / doc.Width;
        var scaleY = Bounds.Height / doc.Height;
        var uniform = Math.Min(scaleX, scaleY);
        if (uniform <= 0) { return; }

        var renderedW = doc.Width * uniform;
        var renderedH = doc.Height * uniform;
        var offsetX = (Bounds.Width - renderedW) / 2;
        var offsetY = (Bounds.Height - renderedH) / 2;

        // Snap the origin to a whole DEVICE pixel.
        //
        // Centring produces a fractional offset almost always, and a
        // fractional origin means every bitmap in the theme is resampled
        // across pixel boundaries and every thin outline lands between two
        // pixels — so the art looks soft and the outlines look slightly
        // misplaced. It varies with DPI, resolution and aspect ratio
        // because all three change how a fractional control coordinate
        // maps onto the device grid: at 125% scaling an offset of x.5
        // control units is x.625 device pixels, which cannot be drawn
        // crisply at any filter quality.
        //
        // Rounding in DEVICE space and converting back is what makes this
        // correct at fractional scalings; rounding the control coordinate
        // alone would still leave a fractional device offset.
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scaling > 0)
        {
            offsetX = Math.Round(offsetX * scaling, MidpointRounding.AwayFromZero) / scaling;
            offsetY = Math.Round(offsetY * scaling, MidpointRounding.AwayFromZero) / scaling;
        }

        // Capture transform so OnPointerPressed can invert it without
        // re-walking the document. Stored in display (control) pixels.
        lastTransform = (uniform, offsetX, offsetY);

        // Device pixels per theme unit, for the display-sized bitmap cache.
        // Uses the same scaling the origin snap above rounds against, so a
        // cached copy is built for the size the art is genuinely rasterised
        // at rather than for its size in control coordinates.
        frameDeviceScale = uniform * (scaling > 0 ? scaling : 1.0);

        // The frame's default filter is the CHEAP one. This is the single
        // largest lever on repaint cost, and the earlier note in
        // docs/known-issues.md — that HighQuality versus LowQuality made no
        // measurable difference — is wrong. Measured directly on
        // 2026-08-11 against the seven layers an idle DualSense panel
        // composites, drawing into a RenderTargetBitmap the size of a real
        // panel:
        //
        //   panel width          430      700     1100
        //   authored + High     2.98     7.79    10.66  ms/frame
        //   authored + Low      0.35     0.89     2.03
        //   prescaled + High    1.60     4.32    10.60
        //   prescaled + Low     0.34     0.83     2.08
        //
        // Three things follow. The cost is the high-quality FILTER, per
        // DESTINATION pixel — 1100-wide costs 3.5x what 430-wide does off
        // identical sources, so it is not the source size and not the layer
        // count. Pre-scaling alone buys under 2x and nothing at all once
        // the panel is large. And pre-scaling plus the cheap filter is
        // indistinguishable from the cheap filter alone.
        //
        // So the filter is what makes it fast, and ScaledCache is what
        // makes the filter safe: a cheap filter aliases when it is doing a
        // 3x downscale, which is exactly what the authored art needs.
        // Drawing a display-sized copy instead leaves it doing almost
        // nothing, where cheap and expensive look the same. DrawImage
        // pushes HighQuality back for the frame or two before a copy
        // exists, so the art never passes through a cheap large downscale.
        //
        // EdgeMode.Antialias stays: it smooths the implicit transform edges
        // that the PNG's alpha channel crosses at non-1:1 scales, and it is
        // not what costs.
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.LowQuality,
            EdgeMode = EdgeMode.Antialias,
        }))
        using (context.PushTransform(Matrix.CreateScale(uniform, uniform) *
                                     Matrix.CreateTranslation(offsetX, offsetY)))
        {
            // Both physical and virtual panels render the full theme.
            // Each surface independently consumes its own snapshot
            // (physical = input source, virtual = output emitted to
            // HIDMaestro), so feedback animates naturally in each panel from
            // its respective source. The IsPhysicalView flag is kept
            // on the surface for potential future use (e.g. a
            // "passive view" toggle) but it no longer gates render
            // output — users have asked for live feedback on both
            // sides.
            foreach (var node in doc.Children)
            {
                RenderNode(context, node, theme);
            }

            // Click-to-map highlights painted on top so they're never
            // occluded by overlay images. While the pointer is held down
            // on an element only the stronger pressed tint paints,
            // otherwise the lighter hover tint — one shape, two alphas.
            // Nothing persists once the pointer releases or leaves.
            if (pressedHit is not null)
            {
                DrawElementHighlight(context, pressedHit, pressed: true);
            }
            else if (hoveredHit is not null)
            {
                DrawElementHighlight(context, hoveredHit, pressed: false);
            }

            // Touch contacts, drawn last so nothing occludes them.
            //
            // This used to happen only inside a TrailPad node and only for
            // the SECOND finger onward, which meant it drew nothing at all
            // on a theme without that node and nothing for a single touch —
            // the reported "no dots". Falling back to the touchpad's own
            // hit region makes it independent of how a pack is authored.
            if (!sawTrailPadThisFrame)
            {
                DrawContactsOverTouchRegion(context);
            }

            sawTrailPadThisFrame = false;
        }
        }
        finally
        {
            renderTimer.Stop();
            renderCostAccumMs += renderTimer.Elapsed.TotalMilliseconds;
            renderCostSamples++;
            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - lastRenderCostReportUtc).TotalSeconds >= 5.0 && renderCostSamples > 0)
            {
                var avgMs = renderCostAccumMs / renderCostSamples;
                if (avgMs > 15.0)
                {
                    // The bitmap filter — the thing that used to dominate
                    // this number — is no longer in the frame path; see the
                    // measurements in Render. If this still fires, the cost
                    // is somewhere this has not looked: the node walk, the
                    // expression evaluation, or the lightbar's opacity
                    // mask, which still stretches an authored-size source.
                    // Do not assume it is the layer count; that answer was
                    // measured and turned out to be wrong once already.
                    Log.Warning(
                        "ThemeSurface[{Mode}] repaint averaging {AvgMs:F1} ms over {Frames} frames — paint cost is "
                        + "throttling the UI, and it is no longer the bitmap filter. See docs/known-issues.md P1.",
                        isPhysicalView ? "physical" : "virtual", avgMs, renderCostSamples);
                }
                else
                {
                    Log.Debug(
                        "ThemeSurface[{Mode}] repaint avg {AvgMs:F2} ms over {Frames} frames.",
                        isPhysicalView ? "physical" : "virtual", avgMs, renderCostSamples);
                }
                renderCostAccumMs = 0;
                renderCostSamples = 0;
                lastRenderCostReportUtc = nowUtc;
            }
        }
    }

    /// <summary>
    /// Pointer-moved handler — runs the hit-tester at the cursor and
    /// updates <see cref="hoveredHit"/> if the result changes.
    /// Invalidates only on actual change so we don't redraw every
    /// mouse-move event at the cursor's native polling rate.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Hover-to-map is a physical-input affordance only — there's
        // nothing to configure by hovering the virtual/output side, and
        // showing the same highlight there was misleading (it looked
        // clickable but silently did nothing useful).
        if (activeTheme is null) { return; }
        if (lastTransform.Scale <= 0) { return; }

        var p = e.GetPosition(this);
        var localX = (p.X - lastTransform.OffsetX) / lastTransform.Scale;
        var localY = (p.Y - lastTransform.OffsetY) / lastTransform.Scale;

        var doc = activeTheme.Document;
        ThemeHitResult? newHit = null;
        if (localX >= 0 && localY >= 0 && localX <= doc.Width && localY <= doc.Height)
        {
            newHit = ThemeHitTester.TryHit(doc, localX, localY);
        }

        if (newHit?.ElementId != hoveredHit?.ElementId)
        {
            hoveredHit = newHit;
            // Cursor affordance — Hand when over a mappable element,
            // default arrow otherwise. The user sees "this is
            // clickable" before they even press.
            Cursor = newHit is not null ? HandCursor : Cursor.Default;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Pointer-exited handler — clears the hover highlight when the
    /// cursor leaves the surface entirely.
    /// </summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hoveredHit is not null || pressedHit is not null)
        {
            hoveredHit = null;
            pressedHit = null;
            Cursor = Cursor.Default;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Pointer-pressed handler translates the click into theme-local
    /// (canvas) coordinates and fires <see cref="Clicked"/>. Uses the
    /// transform captured by the last <see cref="Render"/> pass — if
    /// the surface hasn't rendered yet, the click is ignored.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Same restriction as hover — click-to-map configures a physical
        // button's behavior, which is meaningless on the virtual/output
        // side.
        if (Clicked is null) { return; }
        if (activeTheme is null) { return; }
        if (lastTransform.Scale <= 0) { return; }

        // Only respond to primary-button clicks; let middle/right
        // bubble through for context-menu binding elsewhere.
        var pointerProps = e.GetCurrentPoint(this).Properties;
        if (!pointerProps.IsLeftButtonPressed) { return; }

        var p = e.GetPosition(this);
        var localX = (p.X - lastTransform.OffsetX) / lastTransform.Scale;
        var localY = (p.Y - lastTransform.OffsetY) / lastTransform.Scale;

        var doc = activeTheme.Document;
        if (localX < 0 || localY < 0 || localX > doc.Width || localY > doc.Height)
        {
            return;
        }

        // Light the pressed element for exactly as long as the button
        // is physically held — mirroring how the theme's own press
        // overlay behaves during play. OnPointerReleased / capture-lost
        // / pointer-exited all clear it; nothing persists afterwards.
        var pressed = ThemeHitTester.TryHit(doc, localX, localY);
        if (pressed?.ElementId != pressedHit?.ElementId)
        {
            pressedHit = pressed;
            InvalidateVisual();
        }

        Clicked.Invoke(this, new ThemeClickEventArgs(localX, localY));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ClearPressedHighlight();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Click-to-map typically opens the mapping window on click, which
    /// can steal pointer capture before Released reaches this surface —
    /// capture-lost is the reliable "the press is over" signal in that
    /// case.
    /// </remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ClearPressedHighlight();
    }

    private void ClearPressedHighlight()
    {
        if (pressedHit is not null)
        {
            pressedHit = null;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Tints one hit element through its own artwork, so the highlight
    /// takes the SHAPE of the control — an irregular bumper, a D-pad arm,
    /// a trigger — instead of a rectangle that fits none of them.
    ///
    /// <para>
    /// Hover and press share this one path, differing only in alpha. Hover
    /// used to be drawn a completely different way: eight copies of the
    /// mask, shifted by a fixed 1.6–2.25, formed a ring, and the entire
    /// document was then repainted through the unshifted mask to carve the
    /// interior back out. Both halves were broken.
    /// </para>
    ///
    /// <para>
    /// The offsets were in THEME units, and the surface scales the whole
    /// document to fit the panel. At a dashboard's usual scale of about
    /// 0.29 that ring came out well under one device pixel — a smear
    /// rather than an outline — and it thickened as the window grew, which
    /// is exactly the "hover looks wrong unless the window is maximised"
    /// report. Nothing about the ring was expressed in the units it was
    /// eventually drawn in.
    /// </para>
    ///
    /// <para>
    /// The interior-restoring repaint was worse: it walked and drew every
    /// node in the document, clipped to the hovered element's mask
    /// RECTANGLE, so any neighbouring artwork overlapping that rectangle
    /// painted over the element too — the "sometimes it is not done right"
    /// half. It also put a second full render of the theme inside every
    /// frame the pointer was over a control.
    /// </para>
    /// </summary>
    private void DrawElementHighlight(DrawingContext ctx, ThemeHitResult hit, bool pressed)
    {
        var theme = activeTheme;
        var mask = theme is null ? null : GetHighlightMask(theme, hit.ShapeImagePath);
        var brush = pressed ? HighlightBrush : HoverHighlightBrush;

        // One line per element, the first time it is highlighted. A
        // highlight that comes out as a plain rectangle has two causes that
        // look identical on screen — no silhouette art for the element, so
        // the rounded-rect fallback below is drawing, or art that resolved
        // but whose opacity mask is not being applied — and nothing in the
        // log distinguished them.
        if (highlightDiagnosed.Add(hit.ElementId))
        {
            Log.Information(
                "Highlight {Element}: art={Art}, mask={Mask}, bounds={W:F0}x{H:F0} at ({X:F0},{Y:F0}) — drawing {Path}.",
                hit.ElementId,
                hit.ShapeImagePath ?? "<none declared>",
                mask is null ? "NOT LOADED" : "loaded",
                hit.Bounds.Width, hit.Bounds.Height, hit.Bounds.X, hit.Bounds.Y,
                mask is not null ? "silhouette"
                    : IsRoundControl(hit.ElementId) ? "ellipse fallback" : "rounded-rect fallback");
        }

        if (mask is not null)
        {
            using (ctx.PushOpacityMask(mask, hit.Bounds))
            {
                ctx.FillRectangle(brush, hit.Bounds);
            }

            return;
        }

        // No art to silhouette — a colour-only pbar, or a pack missing the
        // overlay. Fill and outline together so it still reads as a soft
        // button shape rather than a hard box.
        var pen = pressed ? HighlightOutlinePen : HoverOutlinePen;

        if (IsRoundControl(hit.ElementId))
        {
            ctx.DrawEllipse(brush, pen, hit.Bounds.Center,
                hit.Bounds.Width / 2, hit.Bounds.Height / 2);
            return;
        }

        var radius = Math.Min(8, Math.Min(hit.Bounds.Width, hit.Bounds.Height) / 3);
        ctx.DrawRectangle(brush, pen, hit.Bounds, radius, radius);
    }

    private static bool IsRoundControl(string elementId) => elementId is
        "South" or "East" or "West" or "North" or "Guide" or
        "LeftStick" or "RightStick" or
        "LeftStick.Button" or "RightStick.Button";

    private ImageBrush? GetHighlightMask(InstalledTheme theme, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        if (highlightMaskCache.TryGetValue(imagePath, out var cached))
        {
            return cached;
        }

        var bitmap = LoadBitmap(imagePath, theme);

        // A null here is almost never "no art" — it is "not decoded YET".
        // Theme bitmaps decode on a background thread and the first call
        // for any image always returns null, with a repaint posted once it
        // lands. Caching that null made it permanent, and it landed on
        // exactly one class of art: the click/active overlays. Those are
        // drawn only while a control is held, so unlike the D-pad arms,
        // face buttons and trigger fills they are never decoded by an
        // ordinary render — the first hover was always the first request,
        // always got null, and cached it for the session. That is why L1,
        // R1, the touchpad, Options/Share and the PS button fell back to a
        // rectangle while everything else silhouetted correctly.
        //
        // Leaving it uncached costs one resolve per frame while a decode is
        // in flight, which is a dictionary probe: LoadBitmap short-circuits
        // both a completed decode and a genuinely missing file from its own
        // caches.
        if (bitmap is null)
        {
            return null;
        }

        var mask = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        highlightMaskCache[imagePath] = mask;
        return mask;
    }

    /// <summary>
    /// Render walker used exclusively for the physical-view panel.
    /// Draws every passive element (base images, lightbar, trigger
    /// bodies, stick wells at rest) and skips every active-feedback
    /// element (<see cref="ShowHideNode"/> button overlays,
    /// <see cref="PBarNode"/> trigger fills, and the click overlays
    /// nested inside <see cref="SliderNode"/>). The slider's own
    /// deflection translation is also dropped so the stick sits in
    /// its neutral position regardless of live input.
    ///
    /// <para>
    /// This sits next to <see cref="RenderNode"/> rather than gating
    /// behaviour with an <c>if (isPhysicalView)</c> per case branch
    /// because the rules differ at every node type, and a single
    /// flag-checking walker would be harder to reason about than two
    /// small parallel walkers.
    /// </para>
    /// </summary>
    private void RenderNodeStaticOnly(DrawingContext ctx, ThemeNode node, InstalledTheme owner)
    {
        var translation = Matrix.CreateTranslation(node.X, node.Y);
        var transform = node.Rotation != 0
            ? Matrix.CreateRotation(node.Rotation * Math.PI / 180.0) * translation
            : translation;

        switch (node)
        {
            case ShowHideNode:
            case PBarNode:
            case TrailPadNode:
                // Active feedback — skip entirely in physical view.
                return;

            case ImageNode image:
                // One frame for the bitmap AND the children — the
                // preamble already holds this node's X/Y, DrawImage adds
                // only the center shift, and children apply their own
                // X/Y via their own preambles. That's the whole VSCView
                // contract; anything more double-translates.
                using (ctx.PushTransform(transform))
                {
                    DrawImage(ctx, image, owner);
                    foreach (var child in image.Children)
                    {
                        RenderNodeStaticOnly(ctx, child, owner);
                    }
                }
                return;

            case LightbarNode lightbar:
                using (ctx.PushTransform(transform))
                {
                    DrawLightbar(ctx, lightbar, owner);
                    foreach (var child in lightbar.Children)
                    {
                        RenderNodeStaticOnly(ctx, child, owner);
                    }
                }
                return;

            case SliderNode slider:
                // Render at neutral position — drop the InputX/InputY
                // deflection that the virtual view applies, so the
                // stick sits in its well regardless of live input. The
                // node's own X/Y still applies (children are relative
                // to it).
                using (ctx.PushTransform(transform))
                {
                    foreach (var child in slider.Children)
                    {
                        RenderNodeStaticOnly(ctx, child, owner);
                    }
                }
                return;

            default:
                using (ctx.PushTransform(transform))
                {
                    foreach (var child in node.Children)
                    {
                        RenderNodeStaticOnly(ctx, child, owner);
                    }
                }
                return;
        }
    }

    private void RenderNode(DrawingContext ctx, ThemeNode node, InstalledTheme owner)
    {
        var translation = Matrix.CreateTranslation(node.X, node.Y);
        var transform = node.Rotation != 0
            ? Matrix.CreateRotation(node.Rotation * Math.PI / 180.0) * translation
            : translation;

        switch (node)
        {
            case ShowHideNode show:
                if (show.Input.Evaluate(symbols) == 0) { return; }
                using (ctx.PushTransform(transform))
                {
                    foreach (var child in show.Children) { RenderNode(ctx, child, owner); }
                }
                return;

            case SliderNode slider:
            {
                LogSliderDiagnosticsOnce(slider, symbols, transform);
                // Deflection ONLY: the walker's preamble at the top of
                // this method already applied the node's own X/Y (every
                // node renders inside its translated frame — that IS the
                // VSCView contract). Adding slider.X here again was a
                // double-translation that displaced every stick.
                var t =
                    Matrix.CreateTranslation(slider.InputX.Evaluate(symbols),
                                             slider.InputY.Evaluate(symbols)) *
                    transform;
                var rotR = slider.InputR.Evaluate(symbols);
                if (rotR != 0)
                {
                    t = Matrix.CreateRotation(rotR * Math.PI / 180.0) * t;
                }
                using (ctx.PushTransform(t))
                {
                    foreach (var child in slider.Children) { RenderNode(ctx, child, owner); }
                }
                return;
            }

            case TrailPadNode trailPad:
            {
                if (trailPad.Input.Evaluate(symbols) == 0) { return; }

                var t =
                    Matrix.CreateTranslation(trailPad.InputX.Evaluate(symbols),
                                             trailPad.InputY.Evaluate(symbols)) *
                    transform;
                using (ctx.PushTransform(t))
                {
                    DrawTrailPadMarker(ctx, trailPad, owner);
                    foreach (var child in trailPad.Children) { RenderNode(ctx, child, owner); }
                }

                DrawAdditionalContacts(ctx, trailPad, transform);
                sawTrailPadThisFrame = true;
                return;
            }

            case ImageNode image:
            {
                // See the static path's note: one frame, no extra child
                // translation — the preamble is the single application
                // of this node's X/Y.
                using (ctx.PushTransform(transform))
                {
                    DrawImage(ctx, image, owner);
                    foreach (var child in image.Children) { RenderNode(ctx, child, owner); }
                }
                return;
            }

            case LightbarNode lightbar:
                using (ctx.PushTransform(transform))
                {
                    DrawLightbar(ctx, lightbar, owner);
                    foreach (var child in lightbar.Children) { RenderNode(ctx, child, owner); }
                }
                return;

            case PBarNode bar:
                using (ctx.PushTransform(transform))
                {
                    RenderPBar(ctx, bar, owner);
                    foreach (var child in bar.Children) { RenderNode(ctx, child, owner); }
                }
                return;

            default:
                using (ctx.PushTransform(transform))
                {
                    foreach (var child in node.Children) { RenderNode(ctx, child, owner); }
                }
                return;
        }
    }

    /// <summary>
    /// Bitmap-only draw for an <see cref="ImageNode"/> — does NOT recurse
    /// into the node's children. Used both by <see cref="RenderNode"/>
    /// (which then handles children itself) and the physical-only
    /// render branch (which deliberately stops at the leaf).
    ///
    /// <para>
    /// Caller is responsible for pushing the node's own
    /// translation/rotation transform — this method draws into the
    /// already-translated coordinate space.
    /// </para>
    /// </summary>
    private void DrawImage(DrawingContext ctx, ImageNode image, InstalledTheme owner)
    {
        var bmp = LoadBitmap(image.ImagePath, owner);
        if (bmp is null) { return; }

        var w  = image.Width  > 0 ? image.Width  : bmp.PixelSize.Width;
        var h  = image.Height > 0 ? image.Height : bmp.PixelSize.Height;
        var dx = image.Center ? -w / 2 : 0;
        var dy = image.Center ? -h / 2 : 0;
        DrawFitted(ctx, bmp, owner, image.ImagePath, new Rect(dx, dy, w, h));
    }

    /// <summary>
    /// Draws one theme bitmap into <paramref name="destination"/> using its
    /// display-sized copy, falling back to the authored source — under the
    /// expensive filter — for the frame or two before that copy exists.
    ///
    /// <para>
    /// The filter is pushed here rather than once per frame because it has
    /// to follow which bitmap was actually returned. The frame's default is
    /// cheap, which is correct for a copy already at its drawn size and
    /// wrong for a source three times too big: that combination is the one
    /// that aliases.
    /// </para>
    /// </summary>
    private void DrawFitted(
        DrawingContext ctx, Bitmap source, InstalledTheme owner, string imagePath, Rect destination)
    {
        var bitmap = ForDisplay(source, owner, imagePath, destination.Width, destination.Height, out var isDisplaySized);

        if (isDisplaySized)
        {
            ctx.DrawImage(bitmap, destination);
            return;
        }

        using (ctx.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
            EdgeMode = EdgeMode.Antialias,
        }))
        {
            ctx.DrawImage(bitmap, destination);
        }
    }

    /// <summary>
    /// Returns the copy of <paramref name="source"/> closest to the size it
    /// is about to be drawn at, queueing one if it does not exist yet.
    ///
    /// <para>
    /// Never blocks and never returns null: while a copy is being built the
    /// caller gets the full-size original, or the previous size's copy if
    /// there is one. Building it on the UI thread instead would cost a
    /// visible stall on every window resize, across every surface on
    /// screen.
    /// </para>
    ///
    /// <para>
    /// <paramref name="isDisplaySized"/> reports whether the returned
    /// bitmap is close enough to its destination to be drawn with a cheap
    /// filter. It is true both for a fresh copy and for an image that never
    /// needed one because it was already near its drawn size.
    /// </para>
    /// </summary>
    private Bitmap ForDisplay(
        Bitmap source, InstalledTheme owner, string imagePath,
        double drawWidth, double drawHeight, out bool isDisplaySized)
    {
        isDisplaySized = true;

        if (frameDeviceScale <= 0 || drawWidth <= 0 || drawHeight <= 0)
        {
            return source;
        }

        var targetWidth = Math.Min(Quantize(drawWidth * frameDeviceScale), source.PixelSize.Width);
        var targetHeight = Math.Min(Quantize(drawHeight * frameDeviceScale), source.PixelSize.Height);
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return source;
        }

        var sourceArea = (double)source.PixelSize.Width * source.PixelSize.Height;
        var targetArea = (double)targetWidth * targetHeight;
        if (sourceArea < targetArea * ScaleWorthwhileRatio)
        {
            // Already close enough to its drawn size — the cheap filter has
            // almost nothing to do, and a copy would cost memory to save
            // nothing.
            return source;
        }

        var target = new PixelSize(targetWidth, targetHeight);
        var key = $"{owner.Id}|{imagePath}";

        if (ScaledCache.TryGetValue(key, out var cached))
        {
            if (cached.Size != target)
            {
                BeginScale(key, source, target);
            }

            // A copy built for a nearby size is still a display-sized
            // bitmap; the residual is at most one quantum and the cheap
            // filter handles it.
            return cached.Bitmap;
        }

        BeginScale(key, source, target);
        isDisplaySized = false;
        return source;
    }

    /// <summary>
    /// Rounds a device-pixel extent up to the next
    /// <see cref="ScaleStepFraction"/> step of itself, so a cached copy
    /// overshoots its destination by at most that fraction.
    /// </summary>
    private static int Quantize(double devicePixels)
    {
        if (!double.IsFinite(devicePixels) || devicePixels <= 0)
        {
            return 0;
        }

        var step = Math.Max(MinimumScaleStep, (int)Math.Round(devicePixels * ScaleStepFraction));
        var steps = (int)Math.Ceiling(devicePixels / step);
        return steps * step;
    }

    /// <summary>
    /// Builds a display-sized copy off the UI thread, then asks every
    /// surface to repaint so it gets picked up — the same hand-off the
    /// initial decode uses, for the same reason.
    /// </summary>
    private static void BeginScale(string key, Bitmap source, PixelSize target)
    {
        // One scale per image, however many surfaces ask at once. A
        // dashboard of eight slots sharing a theme would otherwise do the
        // same work eight times over.
        if (!ScalesInFlight.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var scaled = source.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
                ScaledCache[key] = new ScaledBitmap(scaled, target);

                Avalonia.Threading.Dispatcher.UIThread.Post(
                    static () => BitmapDecoded?.Invoke(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                // Losing the scaled copy costs frame time, not correctness —
                // the full-size source still draws. Debug, not Warning.
                Log.Debug(ex, "Could not build a display-sized copy of {Image}.", key);
            }
            finally
            {
                _ = ScalesInFlight.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// Per-finger colours. Distinct hues rather than shades, so which
    /// finger is which is readable at a glance and does not depend on
    /// judging brightness.
    /// </summary>
    private static readonly Color[] ContactColors =
    [
        Color.FromRgb(0x4F, 0x9C, 0xFF),  // blue
        Color.FromRgb(0xFF, 0x6B, 0x35),  // orange
        Color.FromRgb(0x4A, 0xDE, 0x80),  // green
        Color.FromRgb(0xE8, 0x79, 0xF0),  // magenta
        Color.FromRgb(0xFB, 0xBF, 0x24),  // amber
    ];

    private static readonly IBrush[] ContactBrushes =
        [.. ContactColors.Select(c => (IBrush)new ImmutableSolidColorBrush(c))];

    /// <summary>
    /// The colour a trail point has just been laid down at — near-white,
    /// so a moving finger has a hot head. Every point cools from here
    /// through its finger's own colour and out.
    /// </summary>
    private static readonly Color TrailCoreColor = Color.FromRgb(0xFF, 0xF4, 0xD6);

    /// <summary>
    /// What a point cools TO before it disappears. A deep oxidised red,
    /// which is what makes the trail read as an ember rather than as a
    /// smear of the finger's colour at decreasing opacity.
    /// </summary>
    private static readonly Color TrailEmberColor = Color.FromRgb(0x8C, 0x1F, 0x04);

    /// <summary>Recent positions per finger, for the ember trail.</summary>
    private readonly TouchTrail touchTrail = new();

    /// <summary>Sparks thrown off each fingertip, in place of a marker dot.</summary>
    private readonly TouchParticles touchParticles = new();

    /// <summary>
    /// Monotonic clock for trail ageing. A Stopwatch rather than
    /// DateTime.UtcNow so a clock adjustment mid-gesture cannot make every
    /// point look expired — or make one look like it is from the future.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch trailClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Draws every touch contact beyond the first, each in its own
    /// colour.
    ///
    /// <para>
    /// The position comes from re-evaluating the theme's OWN
    /// <c>InputX</c>/<c>InputY</c> expressions with that finger
    /// substituted as the primary contact. The alternative — deriving the
    /// touchpad's rectangle in theme coordinates and mapping the
    /// normalised contact onto it — would be guesswork per theme, and
    /// would drift the moment a pack positioned its pad differently. This
    /// way a theme that renders one finger correctly renders five
    /// correctly, with no re-authoring.
    /// </para>
    ///
    /// <para>
    /// The first contact is skipped: the theme already drew it, with its
    /// own artwork.
    /// </para>
    /// </summary>
    /// <summary>Set while a frame rendered a TrailPad, so the fallback overlay does not double-draw.</summary>
    private bool sawTrailPadThisFrame;

    /// <summary>
    /// Draws every touch contact inside the touchpad's own hit region.
    ///
    /// <para>
    /// The fallback for themes with no TrailPad node — which is most of
    /// them. The region is found by probing the hit tester at the centre
    /// of the document for the touchpad element, so the rectangle comes
    /// from the theme's own geometry rather than being guessed.
    /// </para>
    /// </summary>
    private void DrawContactsOverTouchRegion(DrawingContext ctx)
    {
        if (activeTheme is null)
        {
            return;
        }

        var contacts = snapshot.TouchContacts;
        var now = trailClock.Elapsed.TotalSeconds;

        // Record before the early-out on an empty contact list: a lifted
        // finger's trail still has to finish cooling and its sparks still
        // have to burn out, and nothing else would be ageing them.
        touchTrail.Record(contacts, now);
        touchParticles.Emit(contacts, now);

        if (contacts.Count == 0 && !touchTrail.HasPoints && !touchParticles.HasParticles)
        {
            return;
        }

        var region = ResolveTouchRegion(activeTheme);
        if (region is not { } bounds || bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        Point ToSurface(double x, double y) => new(
            bounds.X + (bounds.Width * Math.Clamp(x, 0d, 1d)),
            bounds.Y + (bounds.Height * Math.Clamp(y, 0d, 1d)));

        // Trails under the live contacts, so a dot is never buried by its
        // own history. Every finger's whole trail is drawn before any
        // live marker, rather than trail-then-marker per finger, so two
        // crossing fingers do not have one marker hidden by the other's
        // tail.
        foreach (var contact in AllTrailedFingers(contacts))
        {
            var color = ContactColors[FingerColorIndex(contact)];
            var samples = touchTrail.Samples(contact);

            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                var age = TouchTrail.NormalizedAge(sample, now);

                // Cools white -> the finger's colour -> oxidised red, and
                // spreads as it goes, the way an ember's glow does. The
                // finger's own colour sits in the MIDDLE of that ramp
                // rather than at the head, so which finger drew a trail is
                // still readable while the head stays hot.
                var tint = age < 0.35
                    ? Blend(TrailCoreColor, color, age / 0.35)
                    : Blend(color, TrailEmberColor, (age - 0.35) / 0.65);

                var opacity = Math.Pow(1 - age, 1.6) * (0.35 + (0.65 * Math.Clamp(sample.Pressure, 0d, 1d)));
                if (opacity <= 0.01)
                {
                    continue;
                }

                var radius = 3.5 + (7.5 * age);
                var brush = new ImmutableSolidColorBrush(tint, opacity);
                ctx.DrawEllipse(brush, null, ToSurface(sample.X, sample.Y), radius, radius);
            }
        }

        // Sparks, in place of the marker dot inside a ring. Drawn over the
        // trail and under the fingertip glow, so the brightest thing on
        // screen is still exactly where the finger is.
        foreach (var particle in touchParticles.Particles)
        {
            var age = particle.Age(now);
            var color = ContactColors[FingerColorIndex(particle.FingerIndex)];

            // Same cooling ramp as the trail — white-hot, through the
            // finger's colour, out to ember — so the sparks and the trail
            // read as one effect rather than two overlapping ones.
            var tint = age < 0.3
                ? Blend(TrailCoreColor, color, age / 0.3)
                : Blend(color, TrailEmberColor, (age - 0.3) / 0.7);

            var opacity = Math.Pow(1 - age, 1.4);
            if (opacity <= 0.02)
            {
                continue;
            }

            // Shrinks as it burns out, which is what makes a spark read as
            // a spark rather than as a dot that fades.
            var radius = particle.Scale * (3.2 * (1 - (0.7 * age)));
            ctx.DrawEllipse(
                new ImmutableSolidColorBrush(tint, opacity),
                null,
                ToSurface(particle.XAt(now), particle.YAt(now)),
                radius,
                radius);
        }

        // The fingertip itself: a soft glow rather than a hard dot in a
        // ring. Two translucent discs, the outer one scaled by pressure,
        // so the contact point stays unambiguous while the sparks do the
        // work of looking like fire.
        for (var i = 0; i < contacts.Count && i < ContactColors.Length; i++)
        {
            var contact = contacts[i];
            var point = ToSurface(contact.X, contact.Y);
            var color = ContactColors[FingerColorIndex(contact.FingerIndex)];
            var pressure = Math.Clamp(contact.Pressure, 0f, 1f);

            ctx.DrawEllipse(new ImmutableSolidColorBrush(color, 0.22), null, point, 11 + (7 * pressure), 11 + (7 * pressure));
            ctx.DrawEllipse(new ImmutableSolidColorBrush(color, 0.45), null, point, 6.5, 6.5);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(TrailCoreColor, 0.95), null, point, 3, 3);
        }
    }

    /// <summary>
    /// Finger slots that need a trail drawn: the ones down now, plus any
    /// still cooling after being lifted.
    /// </summary>
    private IEnumerable<int> AllTrailedFingers(IReadOnlyList<TouchContact> contacts)
    {
        foreach (var contact in contacts)
        {
            yield return contact.FingerIndex;
        }

        // A lifted finger's slot is no longer in the snapshot, so its
        // remaining points would never be drawn and the trail would vanish
        // the instant contact ended instead of fading out.
        for (var slot = 0; slot < ContactColors.Length; slot++)
        {
            if (touchTrail.Samples(slot).Count > 0
                && !contacts.Any(c => c.FingerIndex == slot))
            {
                yield return slot;
            }
        }
    }

    /// <summary>
    /// Colour follows the hardware finger SLOT, not the position in the
    /// contact list. Lifting an earlier finger shifts the list, which
    /// would otherwise recolour every remaining finger mid-gesture — and
    /// recolour its trail out from under it.
    /// </summary>
    private static int FingerColorIndex(int fingerIndex) =>
        ((fingerIndex % ContactColors.Length) + ContactColors.Length) % ContactColors.Length;

    /// <summary>Linear blend between two colours.</summary>
    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0d, 1d);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    /// <summary>
    /// The touchpad element's rectangle in theme coordinates, cached per
    /// theme. Probing walks a coarse grid because the hit tester answers
    /// point queries only — there is no enumeration of regions — and the
    /// pad's position varies per pack.
    /// </summary>
    private Rect? ResolveTouchRegion(InstalledTheme theme)
    {
        if (touchRegionTheme == theme.Id)
        {
            return touchRegionBounds;
        }

        touchRegionTheme = theme.Id;
        touchRegionBounds = null;

        var doc = theme.Document;
        for (var yStep = 1; yStep < 20 && touchRegionBounds is null; yStep++)
        {
            for (var xStep = 1; xStep < 20; xStep++)
            {
                var hit = GameFlow.App.ViewModels.ThemeHitTester.TryHit(
                    doc, doc.Width * xStep / 20d, doc.Height * yStep / 20d);

                if (hit is { } touchpadHit && ThemeHitTester.IsTouchpadHit(touchpadHit))
                {
                    touchRegionBounds = touchpadHit.Bounds;
                    break;
                }
            }
        }

        return touchRegionBounds;
    }

    private string? touchRegionTheme;
    private Rect? touchRegionBounds;

    private void DrawAdditionalContacts(DrawingContext ctx, TrailPadNode trailPad, Matrix transform)
    {
        var contacts = snapshot.TouchContacts;
        if (contacts.Count <= 1)
        {
            return;
        }

        var restore = snapshot;

        try
        {
            for (var i = 1; i < contacts.Count && i < ContactBrushes.Length; i++)
            {
                var contact = contacts[i];

                // Substitute this finger as contact 0 so the theme's
                // expressions — which reference finger 0 — resolve to it.
                symbols.UpdateSnapshot(restore with
                {
                    TouchContacts = [contact],
                    TouchX = contact.X,
                    TouchY = contact.Y,
                });

                var position = Matrix.CreateTranslation(
                    trailPad.InputX.Evaluate(symbols),
                    trailPad.InputY.Evaluate(symbols)) * transform;

                using (ctx.PushTransform(position))
                {
                    var brush = ContactBrushes[i % ContactBrushes.Length];

                    // Pressure-sensitive radius, with a floor so a light
                    // touch is still visible.
                    var radius = 14 + (10 * Math.Clamp(contact.Pressure, 0f, 1f));

                    ctx.DrawEllipse(null, new Pen(brush, 3), new Point(0, 0), radius, radius);
                    ctx.DrawEllipse(brush, null, new Point(0, 0), 4, 4);
                }
            }
        }
        finally
        {
            // Always restore: every later node in this frame reads these
            // symbols, and leaving a substituted finger in place would
            // move buttons and sticks too.
            symbols.UpdateSnapshot(restore);
        }
    }

    private void DrawTrailPadMarker(DrawingContext ctx, TrailPadNode trailPad, InstalledTheme owner)
    {
        if (string.IsNullOrWhiteSpace(trailPad.ImagePath)) { return; }

        var bitmap = LoadBitmap(trailPad.ImagePath, owner);
        if (bitmap is null) { return; }

        var width = trailPad.Width > 0 ? trailPad.Width : bitmap.PixelSize.Width;
        var height = trailPad.Height > 0 ? trailPad.Height : bitmap.PixelSize.Height;
        DrawFitted(ctx, bitmap, owner, trailPad.ImagePath, new Rect(-width / 2, -height / 2, width, height));
    }

    private void DrawLightbar(DrawingContext ctx, LightbarNode lightbar, InstalledTheme owner)
    {
        if (lightbarBrush is null || string.IsNullOrWhiteSpace(lightbar.ImagePath)) { return; }

        var bitmap = LoadBitmap(lightbar.ImagePath, owner);
        if (bitmap is null) { return; }

        if (!lightbarMaskCache.TryGetValue(lightbar.ImagePath, out var mask))
        {
            mask = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            lightbarMaskCache[lightbar.ImagePath] = mask;
        }
        if (mask is null) { return; }

        var width = lightbar.Width > 0 ? lightbar.Width : bitmap.PixelSize.Width;
        var height = lightbar.Height > 0 ? lightbar.Height : bitmap.PixelSize.Height;
        var bounds = new Rect(
            lightbar.Center ? -width / 2 : 0,
            lightbar.Center ? -height / 2 : 0,
            width,
            height);
        using (ctx.PushOpacityMask(mask, bounds))
        {
            ctx.FillRectangle(lightbarBrush, bounds);
        }
    }

    private void RenderPBar(DrawingContext ctx, PBarNode bar, InstalledTheme owner)
    {
        var value = bar.Input.Evaluate(symbols);
        var min   = bar.Min.Evaluate(symbols);
        var max   = bar.Max.Evaluate(symbols);
        var range = max - min;
        var ratio = range == 0 ? 0 : Math.Clamp((value - min) / range, 0, 1);

        var w = bar.Width;
        var h = bar.Height;
        var dx = bar.Center ? -w / 2 : 0;
        var dy = bar.Center ? -h / 2 : 0;

        Rect fillRect = bar.Direction switch
        {
            PBarDirection.Right => new Rect(dx,             dy,             w * ratio, h),
            PBarDirection.Left  => new Rect(dx + w * (1-ratio), dy,         w * ratio, h),
            PBarDirection.Up    => new Rect(dx,             dy + h * (1-ratio), w,     h * ratio),
            PBarDirection.Down  => new Rect(dx,             dy,             w,         h * ratio),
            _                   => new Rect(dx,             dy,             w * ratio, h),
        };

        if (!string.IsNullOrEmpty(bar.ImagePath))
        {
            var bmp = LoadBitmap(bar.ImagePath, owner);
            if (bmp is not null)
            {
                using (ctx.PushClip(fillRect))
                {
                    // Full rect, clipped to the fill — so the whole image
                    // is filtered however little of it shows, which is why
                    // this draw goes through the display-sized copy like
                    // any other.
                    DrawFitted(ctx, bmp, owner, bar.ImagePath, new Rect(dx, dy, w, h));
                }
            }
            return;
        }

        var bgBrush = HexBrush(bar.Background);
        var fgBrush = HexBrush(bar.Foreground);
        if (bgBrush is not null) { ctx.FillRectangle(bgBrush, new Rect(dx, dy, w, h)); }
        if (fgBrush is not null && ratio > 0) { ctx.FillRectangle(fgBrush, fillRect); }
    }

    /// <summary>
    /// Resolves and loads an image. Returns <see langword="null"/> on
    /// any failure so a single missing or corrupt PNG does NOT abort
    /// the whole render pass. The null is cached so subsequent ticks
    /// don't repeatedly re-attempt the same broken path.
    /// </summary>
    /// <summary>
    /// One-shot per slider node: logs its position, first meaningfully
    /// non-zero evaluated deflection, and child summary. Exists to
    /// debug third-party themes whose sticks render missing or
    /// misplaced — the log answers "did the expression evaluate, to
    /// what magnitude, and does the child art exist" without needing
    /// the theme's JSON in hand.
    /// </summary>
    private void LogSliderDiagnosticsOnce(SliderNode slider, GameFlow.Infrastructure.Theming.Flee.IFleeSymbols symbols, Matrix accumulated)
    {
        // Checked before the HashSet probe, not after: until a slider has
        // moved this returns early WITHOUT recording itself, so the probe
        // and the two expression evaluations below repeat for every slider
        // on every frame for as long as the stick sits still — which is
        // most of the time.
        if (!Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
        {
            return;
        }

        if (sliderDiagnosticsLogged.Contains(slider))
        {
            return;
        }

        var ix = slider.InputX.Evaluate(symbols);
        var iy = slider.InputY.Evaluate(symbols);
        if (Math.Abs(ix) < 0.01 && Math.Abs(iy) < 0.01)
        {
            return; // wait for actual movement so the logged magnitudes mean something
        }

        _ = sliderDiagnosticsLogged.Add(slider);
        var firstChild = slider.Children.FirstOrDefault();

        // The accumulated transform is the decisive piece: node.X/Y alone
        // can look perfectly correct while the parent chain places the
        // whole group somewhere wrong. Logging where the stick ACTUALLY
        // lands on the document, versus where the theme says its base
        // is, separates "wrong base position" (parent chain) from "wrong
        // deflection" (input expression / sign) — two different bugs that
        // look identical on screen.
        var restingPoint = accumulated.Transform(new Point(slider.X, slider.Y));
        var deflectedPoint = accumulated.Transform(new Point(slider.X + ix, slider.Y + iy));

        Log.Debug(
            "Theme slider diagnostic: node=({X},{Y}) deflection=({Ix:F1},{Iy:F1})px " +
            "resolvedResting=({RX:F1},{RY:F1}) resolvedDeflected=({DX:F1},{DY:F1}) " +
            "children={Count} firstChild={Kind} {Detail}",
            slider.X, slider.Y, ix, iy,
            restingPoint.X, restingPoint.Y, deflectedPoint.X, deflectedPoint.Y,
            slider.Children.Count,
            firstChild?.GetType().Name ?? "(none)",
            firstChild is ImageNode img ? $"image='{img.ImagePath}' at ({img.X},{img.Y}) {img.Width}x{img.Height} center={img.Center}" : string.Empty);
    }

    private static Bitmap? LoadBitmap(string imagePath, InstalledTheme owner)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) { return null; }

        var baseDir = owner.Document.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
        {
            return TryLoadAvares(imagePath);
        }

        // A FAILED lookup is cached too, keyed on the unresolved reference.
        // This is load-bearing, not tidiness: the three packs that ship
        // manifests referencing art they do not contain would otherwise
        // re-run the resolver, re-probe the embedded assets (which costs a
        // thrown exception per miss) and re-log a warning on EVERY frame.
        // Measured at ~900 KB of log per 40 seconds before this cache.
        var missKey = $"{owner.Id}|{imagePath}";
        if (ResolveFailureCache.ContainsKey(missKey))
        {
            return null;
        }

        // Resolution lives in ThemeAssetResolver (Infrastructure) so it can
        // be tested against real folder layouts. Its fallbacks exist
        // because shipped packs disagree with their own manifests about
        // where the art sits — exactly the kind of rule that regresses
        // silently while it has no coverage, which is how every non-Generic
        // controller ended up rendering blank.
        var resolved = ThemeAssetResolver.Resolve(
            imagePath, baseDir, owner.Document.ThemesRootDirectory);

        if (resolved is null)
        {
            // Nothing on disk anywhere in the pack; the art may still ship
            // as an embedded app asset.
            var embedded = TryLoadAvares(imagePath);
            if (embedded is null)
            {
                // Warn ONCE per missing image. The condition is permanent —
                // the file is not there — so repeating it every frame adds
                // nothing and buries everything else.
                if (ResolveFailureCache.TryAdd(missKey, 0))
                {
                    Log.Warning(
                        "Theme image not found on disk or anywhere in its pack: {Image} (theme {Theme}). "
                        + "This image will render as missing; the pack is incomplete.",
                        imagePath, owner.Id);
                }
            }

            return embedded;
        }

        var absolute = resolved;
        return GetOrBeginDecode(Path.GetFullPath(absolute));
    }

    /// <summary>
    /// Returns a decoded theme bitmap, or <see langword="null"/> while one
    /// is still being decoded on a background thread.
    ///
    /// <para>
    /// Decoding used to happen inline, inside <see cref="Render"/>, on the
    /// UI thread. That is fine for one small image and catastrophic at the
    /// scale this app actually runs at: a themed slot references ~47
    /// images, several of them ~1467x816 PNGs, and a dashboard with eight
    /// slots spanning six controller kinds faults in the better part of
    /// three hundred of them. Measured, that blocked the dispatcher for
    /// 7.9 SECONDS in one stretch — the "one refresh per several seconds"
    /// stall, and the reason opening the tab appeared to hang.
    /// </para>
    ///
    /// <para>
    /// Returning null for the first frame or two costs a partially drawn
    /// controller that completes a moment later. Blocking the UI thread
    /// costs the whole application. The art streams in instead.
    /// </para>
    /// </summary>
    private static Bitmap? GetOrBeginDecode(string absolute)
    {
        if (BitmapCache.TryGetValue(absolute, out var cached))
        {
            return cached;
        }

        // One decode per path, however many surfaces ask for it at once.
        // Eight slots sharing a theme would otherwise each queue the same
        // work, which is how a shared cache turns into eight times the I/O.
        if (!DecodesInFlight.TryAdd(absolute, 0))
        {
            return null;
        }

        _ = Task.Run(() =>
        {
            Bitmap? decoded = null;
            try
            {
                if (File.Exists(absolute))
                {
                    decoded = new Bitmap(absolute);
                }
                else
                {
                    Log.Warning("Theme image not found on disk: {Path}", absolute);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load theme image {Path}.", absolute);
            }

            // Cached even on failure, so a broken file is attempted once
            // rather than re-queued on every frame.
            BitmapCache[absolute] = decoded;
            _ = DecodesInFlight.TryRemove(absolute, out _);

            if (decoded is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    static () => BitmapDecoded?.Invoke(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        });

        return null;
    }


    private static Bitmap? TryLoadAvares(string relative)
    {
        try
        {
            var uri = new Uri($"avares://GameFlow.App/Assets/Themes/{relative.Replace('\\', '/')}");
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private static IBrush? HexBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) { return null; }
        try
        {
            var clean = hex.TrimStart('#');
            if (clean.Length == 6) { clean = "FF" + clean; }
            if (clean.Length != 8) { return null; }
            var argb = Convert.ToUInt32(clean, 16);
            return new SolidColorBrush(Color.FromUInt32(argb));
        }
        catch { return null; }
    }

    private static bool IsTransparentColor(string value)
    {
        if (string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase)) { return true; }

        var clean = value.Trim().TrimStart('#');
        return clean.Length == 8
            && string.Equals(clean[..2], "00", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Carries a click position in theme-local (canvas) coordinates, i.e.
/// the same coordinate space the theme.json's <c>x</c>/<c>y</c> fields
/// use. The host typically funnels this into
/// <see cref="GameFlow.App.ViewModels.ThemeHitTester"/> to resolve it
/// to a logical button id.
/// </summary>
public sealed class ThemeClickEventArgs(double x, double y) : EventArgs
{
    /// <summary>Theme-local X coordinate of the click.</summary>
    public double X { get; } = x;

    /// <summary>Theme-local Y coordinate of the click.</summary>
    public double Y { get; } = y;
}
