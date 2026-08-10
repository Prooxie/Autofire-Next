using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GameFlow.Core.Models;
using GameFlow.Core.Pipeline;

namespace GameFlow.App.Views;

/// <summary>
/// Draws a stick's response curve and lets the shaping parameters be
/// dragged directly on it.
///
/// <para>
/// The curve is not redrawn from a local copy of the shaping maths — it is
/// sampled from <see cref="DeviceSettingsProcessor.ApplyStick"/>, the same
/// function the runtime tick uses. A graph that approximated the pipeline
/// would eventually disagree with it, and a tuning graph that lies is
/// worse than no graph: the user would tune against the picture and get a
/// different feel in the game.
/// </para>
///
/// <para>
/// Three handles map to the three parameters that actually change the
/// curve's shape at its edges — deadzone (where output leaves zero),
/// anti-deadzone (the output floor just past it), and full-at (where
/// output saturates). Sensitivity and the curve kind reshape the middle
/// and are driven by their own controls, because they have no single
/// meaningful grab point.
/// </para>
/// </summary>
public sealed class ResponseCurveEditor : Control
{
    private const double HandleRadius = 5.5;
    private const int CurveSamples = 96;

    /// <summary>Which handle the pointer captured, if any.</summary>
    private Handle dragging = Handle.None;

    public static readonly StyledProperty<double> DeadzoneProperty =
        AvaloniaProperty.Register<ResponseCurveEditor, double>(nameof(Deadzone), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> AntiDeadzoneProperty =
        AvaloniaProperty.Register<ResponseCurveEditor, double>(nameof(AntiDeadzone), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> FullAtProperty =
        AvaloniaProperty.Register<ResponseCurveEditor, double>(nameof(FullAt), defaultValue: 1.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> SensitivityProperty =
        AvaloniaProperty.Register<ResponseCurveEditor, double>(nameof(Sensitivity), defaultValue: 1.0);

    public static readonly StyledProperty<StickCurve> CurveProperty =
        AvaloniaProperty.Register<ResponseCurveEditor, StickCurve>(nameof(Curve));

    /// <summary>
    /// Live input magnitude, 0–1. Drawn as a moving marker on the curve so
    /// the user can push the stick and watch where their own hardware
    /// actually sits — which is the only way to pick a deadzone that
    /// matches a specific worn pad.
    /// </summary>
    public static readonly StyledProperty<double> LiveInputProperty =
        AvaloniaProperty.Register<ResponseCurveEditor, double>(nameof(LiveInput));

    static ResponseCurveEditor()
    {
        AffectsRender<ResponseCurveEditor>(
            DeadzoneProperty, AntiDeadzoneProperty, FullAtProperty,
            SensitivityProperty, CurveProperty, LiveInputProperty);
    }

    public double Deadzone
    {
        get => GetValue(DeadzoneProperty);
        set => SetValue(DeadzoneProperty, value);
    }

    public double AntiDeadzone
    {
        get => GetValue(AntiDeadzoneProperty);
        set => SetValue(AntiDeadzoneProperty, value);
    }

    public double FullAt
    {
        get => GetValue(FullAtProperty);
        set => SetValue(FullAtProperty, value);
    }

    public double Sensitivity
    {
        get => GetValue(SensitivityProperty);
        set => SetValue(SensitivityProperty, value);
    }

    public StickCurve Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double LiveInput
    {
        get => GetValue(LiveInputProperty);
        set => SetValue(LiveInputProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Square-ish by preference: a response curve read against
        // non-square axes misleads about how steep it is.
        var side = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        return new Size(side, Math.Min(side, 190));
    }

    public override void Render(DrawingContext context)
    {
        var plot = PlotBounds();
        if (plot.Width <= 1 || plot.Height <= 1)
        {
            return;
        }

        var grid = ResolveBrush("AppBorder0", Colors.Gray);
        var accent = ResolveBrush("AppAccent", Color.FromRgb(0x4F, 0x9C, 0xFF));
        var dim = ResolveBrush("AppForegroundDim", Colors.Gray);
        var surface = ResolveBrush("AppSurface0", Color.FromRgb(0x11, 0x18, 0x22));

        context.FillRectangle(surface, plot);

        // Grid at quarters — enough to read a value off, few enough not to
        // compete with the curve itself.
        var gridPen = new Pen(grid, 1, DashStyle.Dash) { LineCap = PenLineCap.Flat };
        for (var i = 1; i < 4; i++)
        {
            var t = i / 4.0;
            var x = plot.X + (plot.Width * t);
            var y = plot.Y + (plot.Height * t);
            context.DrawLine(gridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }

        context.DrawRectangle(new Pen(grid, 1), plot);

        // 1:1 reference, so any shaping is visible as a departure from it.
        context.DrawLine(
            new Pen(dim, 1, DashStyle.Dot),
            new Point(plot.X, plot.Bottom),
            new Point(plot.Right, plot.Y));

        var settings = CurrentSettings();

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var started = false;
            for (var i = 0; i <= CurveSamples; i++)
            {
                var input = (double)i / CurveSamples;
                var output = Sample(settings, input);
                var point = ToPixel(plot, input, output);

                if (!started)
                {
                    ctx.BeginFigure(point, isFilled: false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(point);
                }
            }

            if (started)
            {
                ctx.EndFigure(false);
            }
        }

        context.DrawGeometry(null, new Pen(accent, 2), geometry);

        // Live marker. Drawn after the curve so it is never hidden by it.
        var live = Math.Clamp(LiveInput, 0, 1);
        if (live > 0.001)
        {
            var marker = ToPixel(plot, live, Sample(settings, live));
            context.DrawEllipse(accent, null, marker, 4, 4);
            context.DrawLine(
                new Pen(accent, 1, DashStyle.Dash) { Thickness = 1 },
                new Point(marker.X, plot.Bottom),
                marker);
        }

        DrawHandle(context, accent, ToPixel(plot, Math.Clamp(Deadzone, 0, 1), 0));
        DrawHandle(context, accent, ToPixel(plot, Math.Clamp(Deadzone, 0, 1), Math.Clamp(AntiDeadzone, 0, 1)));
        DrawHandle(context, accent, ToPixel(plot, Math.Clamp(FullAt, 0.05, 1), 1));
    }

    private static void DrawHandle(DrawingContext context, IBrush brush, Point centre) =>
        context.DrawEllipse(Brushes.White, new Pen(brush, 2), centre, HandleRadius, HandleRadius);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var plot = PlotBounds();
        var position = e.GetPosition(this);
        dragging = NearestHandle(plot, position);

        if (dragging != Handle.None)
        {
            e.Pointer.Capture(this);
            ApplyDrag(plot, position);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var plot = PlotBounds();

        if (dragging != Handle.None)
        {
            ApplyDrag(plot, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        // Hand cursor only when a handle is actually grabbable, so the
        // affordance matches what a click will do.
        Cursor = NearestHandle(plot, e.GetPosition(this)) != Handle.None
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (dragging != Handle.None)
        {
            dragging = Handle.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void ApplyDrag(Rect plot, Point position)
    {
        var x = Math.Clamp((position.X - plot.X) / plot.Width, 0, 1);
        var y = Math.Clamp(1 - ((position.Y - plot.Y) / plot.Height), 0, 1);

        switch (dragging)
        {
            case Handle.Deadzone:
                // Never let the deadzone reach or pass full-at: the curve
                // would have zero width and the stick would be a digital
                // switch with no way back except retyping the number.
                Deadzone = Math.Round(Math.Clamp(x, 0, Math.Max(0, FullAt - 0.05)), 3);
                break;

            case Handle.AntiDeadzone:
                AntiDeadzone = Math.Round(Math.Clamp(y, 0, 0.95), 3);
                break;

            case Handle.FullAt:
                FullAt = Math.Round(Math.Clamp(x, Math.Min(1, Deadzone + 0.05), 1), 3);
                break;
        }
    }

    private Handle NearestHandle(Rect plot, Point position)
    {
        const double GrabRadius = 11;

        var deadzone = ToPixel(plot, Math.Clamp(Deadzone, 0, 1), 0);
        var anti = ToPixel(plot, Math.Clamp(Deadzone, 0, 1), Math.Clamp(AntiDeadzone, 0, 1));
        var full = ToPixel(plot, Math.Clamp(FullAt, 0.05, 1), 1);

        // Anti-deadzone is tested first: it sits directly above the
        // deadzone handle on the same x, and when both are near zero they
        // overlap. Preferring it means the upper one stays reachable.
        if (Distance(position, anti) <= GrabRadius) { return Handle.AntiDeadzone; }
        if (Distance(position, deadzone) <= GrabRadius) { return Handle.Deadzone; }
        if (Distance(position, full) <= GrabRadius) { return Handle.FullAt; }

        return Handle.None;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private Rect PlotBounds()
    {
        // Inset by the handle radius so a handle sitting at 0 or 1 is not
        // clipped in half by the control's own edge.
        var pad = HandleRadius + 2;
        return new Rect(
            pad, pad,
            Math.Max(0, Bounds.Width - (pad * 2)),
            Math.Max(0, Bounds.Height - (pad * 2)));
    }

    private static Point ToPixel(Rect plot, double input, double output) =>
        new(plot.X + (plot.Width * Math.Clamp(input, 0, 1)),
            plot.Bottom - (plot.Height * Math.Clamp(output, 0, 1)));

    private StickSettings CurrentSettings() => new()
    {
        Deadzone = (float)Deadzone,
        AntiDeadzone = (float)AntiDeadzone,
        FullAt = (float)FullAt,
        Sensitivity = (float)Sensitivity,
        Curve = Curve
    };

    /// <summary>
    /// Samples the REAL pipeline at one input magnitude. Pushing the value
    /// along +X keeps it a pure magnitude question, which is what the
    /// graph plots.
    /// </summary>
    private static double Sample(StickSettings settings, double input) =>
        DeviceSettingsProcessor.ApplyStick(new StickVector((float)input, 0f), settings).Magnitude;

    private IBrush ResolveBrush(string key, Color fallback) =>
        this.TryFindResource(key, out var found) && found is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    private enum Handle
    {
        None,
        Deadzone,
        AntiDeadzone,
        FullAt
    }
}
