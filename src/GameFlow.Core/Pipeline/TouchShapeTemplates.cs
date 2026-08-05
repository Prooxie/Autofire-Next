using GameFlow.Core.Enums;

namespace GameFlow.Core.Pipeline;

/// <summary>
/// Reference figures for <see cref="TouchGestureKind.Shape"/>, and the
/// matcher that scores a traced stroke against them.
///
/// <para>
/// The approach is the classic $1 unistroke recognizer, minus its
/// rotation-invariance step. That omission is deliberate rather than a
/// simplification: $1 rotates each candidate to a canonical angle so a
/// figure drawn tilted still matches, but here orientation IS the
/// signal — a Z rotated ninety degrees is not a Z, and a checkmark
/// flipped is not a checkmark. Keeping strokes in the frame the user
/// drew them in is what lets those two coexist as distinct templates.
/// </para>
///
/// <para>
/// Start point is handled separately: closed figures are compared
/// against every cyclic rotation of their template, so a square begun
/// at any corner still matches, while open figures (Z, checkmark) are
/// matched only in the order drawn. Direction survives both — reversing
/// a stroke is not a rotation of it, which is exactly what keeps a
/// clockwise circle from scoring as a counter-clockwise one.
/// </para>
/// </summary>
public static class TouchShapeTemplates
{
    /// <summary>
    /// Points each stroke is resampled to before comparison. 32 is the
    /// $1 recognizer's own working figure — enough to preserve a
    /// checkmark's corner, few enough that comparing all 32 cyclic
    /// rotations of the four closed templates stays trivial.
    /// </summary>
    public const int ResampleCount = 32;

    /// <summary>
    /// Half the diagonal of the unit box strokes are normalized into.
    /// Worst-case mean distance between two normalized strokes, so
    /// dividing by it maps the raw distance onto a 0..1 score.
    /// </summary>
    private static readonly float HalfDiagonal = MathF.Sqrt(2f) / 2f;

    private sealed record Template(TouchShape Shape, Point[] Points, bool Closed);

    /// <summary>A point in normalized stroke space (centered on the origin, spanning the unit box).</summary>
    public readonly record struct Point(float X, float Y);

    private static readonly Template[] Templates = BuildTemplates();

    /// <summary>
    /// Scores <paramref name="path"/> against every template and returns
    /// the best. Score is 0..1; the caller compares it against the
    /// rule's threshold. Returns null when the path is too short to say
    /// anything meaningful about.
    /// </summary>
    public static (TouchShape Shape, float Score)? Match(IReadOnlyList<Point> path)
    {
        // Under four samples there is no figure to speak of — two points
        // are a line segment and three a corner, either of which would
        // score respectably against whichever template happens to start
        // out the same way.
        if (path is null || path.Count < 4)
        {
            return null;
        }

        var candidate = Normalize(path);
        if (candidate is null)
        {
            return null;
        }

        TouchShape bestShape = default;
        var bestScore = float.NegativeInfinity;

        foreach (var template in Templates)
        {
            var score = template.Closed
                ? BestRotatedScore(candidate, template.Points)
                : Score(candidate, template.Points, 0);

            if (score > bestScore)
            {
                bestScore = score;
                bestShape = template.Shape;
            }
        }

        return bestScore <= float.NegativeInfinity ? null : (bestShape, bestScore);
    }

    /// <summary>
    /// Best score across every choice of template starting point — how a
    /// square drawn from the bottom-right corner still matches one whose
    /// template starts top-left.
    /// </summary>
    private static float BestRotatedScore(Point[] candidate, Point[] template)
    {
        var best = float.NegativeInfinity;
        for (var offset = 0; offset < template.Length; offset++)
        {
            var score = Score(candidate, template, offset);
            if (score > best)
            {
                best = score;
            }
        }
        return best;
    }

    private static float Score(Point[] candidate, Point[] template, int offset)
    {
        var total = 0f;
        for (var i = 0; i < candidate.Length; i++)
        {
            var t = template[(i + offset) % template.Length];
            var dx = candidate[i].X - t.X;
            var dy = candidate[i].Y - t.Y;
            total += MathF.Sqrt((dx * dx) + (dy * dy));
        }

        var mean = total / candidate.Length;
        return Math.Clamp(1f - (mean / HalfDiagonal), 0f, 1f);
    }

    /// <summary>
    /// Resamples to <see cref="ResampleCount"/> equidistant points,
    /// scales into the unit box, and centers on the centroid. Returns
    /// null for a degenerate path (every sample in the same spot), which
    /// has no scale to normalize by.
    /// </summary>
    public static Point[]? Normalize(IReadOnlyList<Point> path)
    {
        var resampled = Resample(path, ResampleCount);
        if (resampled is null)
        {
            return null;
        }

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in resampled)
        {
            minX = MathF.Min(minX, p.X);
            minY = MathF.Min(minY, p.Y);
            maxX = MathF.Max(maxX, p.X);
            maxY = MathF.Max(maxY, p.Y);
        }

        var width = maxX - minX;
        var height = maxY - minY;
        const float Epsilon = 1e-5f;

        // Thin strokes are scaled UNIFORMLY rather than stretched to fill
        // the box. Per-axis scaling is what makes the recognizer
        // indifferent to how large or how squat a figure was drawn, but
        // applied to a near-straight stroke it magnifies a millimetre of
        // hand wobble into a full-height excursion — and a swipe with an
        // amplified wobble is a passable imitation of a Z or a checkmark.
        // Since a matched shape suppresses the swipe, that misfire costs
        // the user the gesture they actually made. Staying uniform keeps
        // a line looking like a line, so it scores badly against every
        // template and falls through to the swipe classifier, which is
        // the honest reading.
        //
        // The 0.15 ratio is the $1 recognizer's own "thin bounding box"
        // cutoff, and sits well below the aspect of any of the six
        // templates (the flattest, the checkmark, is about 1:1).
        const float ThinAspectRatio = 0.15f;
        var longSide = MathF.Max(width, height);
        var uniform = MathF.Min(width, height) < ThinAspectRatio * longSide;
        var scaleX = uniform ? longSide : width;
        var scaleY = uniform ? longSide : height;
        if (scaleX < Epsilon || scaleY < Epsilon)
        {
            return null;
        }

        var scaled = new Point[resampled.Length];
        float sumX = 0f, sumY = 0f;
        for (var i = 0; i < resampled.Length; i++)
        {
            var x = (resampled[i].X - minX) / scaleX;
            var y = (resampled[i].Y - minY) / scaleY;
            scaled[i] = new Point(x, y);
            sumX += x;
            sumY += y;
        }

        var centroidX = sumX / scaled.Length;
        var centroidY = sumY / scaled.Length;
        for (var i = 0; i < scaled.Length; i++)
        {
            scaled[i] = new Point(scaled[i].X - centroidX, scaled[i].Y - centroidY);
        }

        return scaled;
    }

    /// <summary>
    /// Walks the path laying down <paramref name="count"/> points at
    /// equal arc-length intervals, so comparison is against the figure's
    /// SHAPE rather than against how fast it was drawn — otherwise a
    /// stroke that paused mid-way would pile up samples at the pause and
    /// match nothing.
    /// </summary>
    private static Point[]? Resample(IReadOnlyList<Point> path, int count)
    {
        var totalLength = 0f;
        for (var i = 1; i < path.Count; i++)
        {
            totalLength += Distance(path[i - 1], path[i]);
        }

        if (totalLength <= 1e-6f)
        {
            return null;
        }

        var interval = totalLength / (count - 1);
        var result = new List<Point>(count) { path[0] };
        var accumulated = 0f;

        // Indexed rather than foreach because a segment can yield several
        // output points (when it is longer than the interval) and is then
        // re-entered from the freshly emitted point.
        var previous = path[0];
        for (var i = 1; i < path.Count; i++)
        {
            var current = path[i];
            var segment = Distance(previous, current);
            if (segment <= 0f)
            {
                previous = current;
                continue;
            }

            if (accumulated + segment >= interval && result.Count < count)
            {
                var t = (interval - accumulated) / segment;
                var next = new Point(
                    previous.X + (t * (current.X - previous.X)),
                    previous.Y + (t * (current.Y - previous.Y)));
                result.Add(next);
                previous = next;
                accumulated = 0f;
                i--; // re-examine this segment from the new point
                continue;
            }

            accumulated += segment;
            previous = current;
        }

        // Floating-point drift can leave the walk a fraction short of the
        // final point; pad with the path's true end rather than returning
        // a short array the comparison loop would index past.
        while (result.Count < count)
        {
            result.Add(path[^1]);
        }

        return [.. result];
    }

    private static float Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Builds the reference figures. Each is written as its corner
    /// points and then run through the same resample+normalize the
    /// candidate gets, so template and candidate are always compared in
    /// identical terms.
    ///
    /// <para>Coordinates are in surface convention: Y grows DOWNWARD.
    /// That is what makes "clockwise" below read as clockwise to
    /// someone looking at the pad.</para>
    /// </summary>
    private static Template[] BuildTemplates()
    {
        var circleClockwise = Circle(clockwise: true);
        var circleCounter = Circle(clockwise: false);

        // Square, from the top-left corner going clockwise and closing
        // back onto the start.
        Point[] square =
        [
            new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f), new(0f, 0f)
        ];

        // Triangle: apex, bottom-right, bottom-left, back to the apex.
        Point[] triangle =
        [
            new(0.5f, 0f), new(1f, 1f), new(0f, 1f), new(0.5f, 0f)
        ];

        // Z: across the top, diagonally back down-left, across the bottom.
        Point[] z =
        [
            new(0f, 0f), new(1f, 0f), new(0f, 1f), new(1f, 1f)
        ];

        // Checkmark: short stroke down-right into the low corner, then
        // the long stroke up-right, finishing higher than it started.
        Point[] checkmark =
        [
            new(0f, 0.45f), new(0.35f, 1f), new(1f, 0f)
        ];

        return
        [
            Build(TouchShape.CircleClockwise, circleClockwise, closed: true),
            Build(TouchShape.CircleCounterClockwise, circleCounter, closed: true),
            Build(TouchShape.Square, square, closed: true),
            Build(TouchShape.Triangle, triangle, closed: true),
            Build(TouchShape.Z, z, closed: false),
            Build(TouchShape.Checkmark, checkmark, closed: false),
        ];
    }

    private static Template Build(TouchShape shape, Point[] outline, bool closed)
    {
        var normalized = Normalize(outline)
            // Templates are compile-time constants that always have
            // extent on both axes, so this cannot fire in practice; the
            // throw documents that rather than letting a null slip into
            // the match loop and NRE at the first gesture.
            ?? throw new InvalidOperationException($"Shape template '{shape}' failed to normalize.");
        return new Template(shape, normalized, closed);
    }

    /// <summary>
    /// A circle starting at twelve o'clock. On a y-down surface an
    /// increasing angle sweeps top → right → bottom → left, which is
    /// clockwise as seen by the user.
    /// </summary>
    private static Point[] Circle(bool clockwise)
    {
        const int Steps = 32;
        var points = new Point[Steps + 1];
        for (var i = 0; i <= Steps; i++)
        {
            var sweep = 2f * MathF.PI * i / Steps;
            var angle = (-MathF.PI / 2f) + (clockwise ? sweep : -sweep);
            points[i] = new Point(MathF.Cos(angle), MathF.Sin(angle));
        }
        return points;
    }
}
