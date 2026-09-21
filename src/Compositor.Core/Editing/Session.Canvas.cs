using System.Collections.Immutable;
using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

/// <summary>An uncommitted gradient on one layer or mask; endpoints in document pixels.</summary>
public sealed class GradientEdit
{
    public RasterEdit Raster { get; }
    public PointD Start { get; set; }
    public PointD End { get; set; }
    public GradientEdit(RasterEdit raster, PointD start) { Raster = raster; Start = start; End = start; }
    public bool HasLine => Start.DistanceTo(End) >= 0.5;
}

/// <summary>A shape being dragged out with the Shape tool, in whole document pixels.</summary>
public sealed record ShapeDraft(ShapeKind Kind, PointD Anchor, RectD Rect, double CornerRadius = 0, PointD? End = null, int Sides = 5, double Weight = 4,
    ShapeCorners? Corners = null)
{
    /// <summary>A line drawn from bottom-left to top-right (otherwise top-left to bottom-right).</summary>
    public bool Rising => End is { } e && (e.X - Anchor.X) * (e.Y - Anchor.Y) < 0;
    /// <summary>Only the settings this kind uses are kept, so a rectangle or ellipse matches what the Mac format stores.</summary>
    public LayerShapeStyle Style(PaletteColor color) => new(Kind, color.Red, color.Green, color.Blue, Rounds(Kind) ? CornerRadius : 0,
        Kind is ShapeKind.Polygon or ShapeKind.Star ? Sides : 5, Kind == ShapeKind.Line ? Weight : 4, Kind == ShapeKind.Line && Rising,
        Kind is ShapeKind.Rectangle or ShapeKind.Triangle ? Corners : null);

    /// <summary>Shapes with corners to round (not ellipses or lines).</summary>
    public static bool Rounds(ShapeKind kind) => kind is not (ShapeKind.Ellipse or ShapeKind.Line);
}

public enum CanvasUnit { Pixels, Percent, Inches, Centimeters }

public sealed class CanvasSizeDraft
{
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public double Resolution { get; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Relative { get; set; }
    public bool Locked { get; set; }
    public CanvasUnit Unit { get; set; } = CanvasUnit.Pixels;

    public CanvasSizeDraft(int width, int height, double resolution)
    {
        OriginalWidth = width; OriginalHeight = height; Width = width; Height = height; Resolution = resolution;
    }

    public bool Valid => double.IsFinite(Width) && double.IsFinite(Height)
        && Math.Round(Width) >= 1 && Math.Round(Width) <= 30_000 && Math.Round(Height) >= 1 && Math.Round(Height) <= 30_000;

    public double Displayed(bool widthAxis)
    {
        double original = widthAxis ? OriginalWidth : OriginalHeight;
        double pixels = (widthAxis ? Width : Height) - (Relative ? original : 0);
        return Unit switch
        {
            CanvasUnit.Pixels => pixels, CanvasUnit.Percent => pixels / original * 100,
            CanvasUnit.Inches => pixels / Resolution, _ => pixels / Resolution * 2.54,
        };
    }

    public void Set(double value, bool widthAxis)
    {
        double original = widthAxis ? OriginalWidth : OriginalHeight;
        double pixels = Unit switch
        {
            CanvasUnit.Pixels => value, CanvasUnit.Percent => value / 100 * original,
            CanvasUnit.Inches => value * Resolution, _ => value / 2.54 * Resolution,
        };
        double final = pixels + (Relative ? original : 0);
        if (widthAxis) { Width = final; if (Locked) Height = final * OriginalHeight / OriginalWidth; }
        else { Height = final; if (Locked) Width = final * OriginalWidth / OriginalHeight; }
    }
}

public sealed record CanvasSizeOptions(int Width, int Height, int Anchor = 4, PaletteColor? Fill = null, PointD? ContentOffset = null)
{
    public PointD Offset(int fromWidth, int fromHeight)
    {
        if (ContentOffset is { } offset) return offset;
        return new PointD(Math.Floor((double)(Width - fromWidth) * (Anchor % 3) / 2), Math.Floor((double)(Height - fromHeight) * (Anchor / 3) / 2));
    }
}

public sealed record ImageSizeOptions(int Width, int Height, double Resolution, LayerSampling Sampling = LayerSampling.High);

public static class CropGeometry
{
    public static RectD Snapped(RectD rect)
    {
        rect = rect.Standardized;
        double x = Math.Round(rect.MinX, MidpointRounding.AwayFromZero), y = Math.Round(rect.MinY, MidpointRounding.AwayFromZero);
        return new RectD(x, y, Math.Max(1, Math.Round(rect.MaxX, MidpointRounding.AwayFromZero) - x), Math.Max(1, Math.Round(rect.MaxY, MidpointRounding.AwayFromZero) - y));
    }

    public static bool Valid(RectD rect) => double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width >= 1 && rect.Width <= 30_000 && rect.Height >= 1 && rect.Height <= 30_000 && Math.Abs(rect.X) <= 1_000_000 && Math.Abs(rect.Y) <= 1_000_000;

    public static RectD Create(PointD start, PointD end, double? ratio, bool symmetric = false)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (ratio is { } r)
        {
            if (Math.Abs(dx) > Math.Abs(dy) * r) dy = (dy < 0 ? -1 : 1) * Math.Abs(dx) / r;
            else dx = (dx < 0 ? -1 : 1) * Math.Abs(dy) * r;
        }
        if (symmetric) return Snapped(new RectD(start.X - Math.Abs(dx), start.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2));
        return Snapped(new RectD(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Abs(dx), Math.Abs(dy)));
    }
}

public enum DragModeKind { Move, Resize, Rotate, Distort, Create }
public readonly record struct DragMode(DragModeKind Kind, int Index = 0)
{
    public static readonly DragMode MoveMode = new(DragModeKind.Move);
    public static readonly DragMode RotateMode = new(DragModeKind.Rotate);
    public static readonly DragMode CreateMode = new(DragModeKind.Create);
    public static DragMode Resize(int index) => new(DragModeKind.Resize, index);
    public static DragMode Distort(int index) => new(DragModeKind.Distort, index);
}

/// <summary>A transform-handle drag (LayerTransform.swift TransformDrag).</summary>
public sealed record TransformDrag(LayerTransform Original, PointD Start, DragMode Mode, PointD[]? OriginalCorners = null)
{
    public PointD[]? CornersTo(PointD point, bool shift = false)
    {
        if (OriginalCorners is not { } corners) return null;
        var result = (PointD[])corners.Clone();
        double dx = point.X - Start.X, dy = point.Y - Start.Y;
        if (shift) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
        int[] moved;
        switch (Mode.Kind)
        {
            case DragModeKind.Distort: moved = Mode.Index % 2 == 0 ? new[] { Mode.Index / 2 } : new[] { Mode.Index / 2, (Mode.Index / 2 + 1) % 4 }; break;
            case DragModeKind.Move: moved = new[] { 0, 1, 2, 3 }; break;
            default: return null;
        }
        foreach (var c in moved) result[c] = result[c].Offset(dx, dy);
        return result;
    }

    public LayerTransform Updated(PointD point, bool lockRatio, bool shift, bool option = false)
    {
        var result = Original;
        switch (Mode.Kind)
        {
            case DragModeKind.Distort: break;
            case DragModeKind.Move:
            {
                double dx = point.X - Start.X, dy = point.Y - Start.Y;
                if (shift) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
                result = result with { Origin = result.Origin.Offset(dx, dy) };
                break;
            }
            case DragModeKind.Rotate:
            {
                var c = Original.Center;
                double delta = Math.Atan2(point.Y - c.Y, point.X - c.X) - Math.Atan2(Start.Y - c.Y, Start.X - c.X);
                double rotation = result.Rotation + delta * 180 / Math.PI;
                if (shift) rotation = LayerTransform.RoundSwift(rotation / 15) * 15;
                result = result with { Rotation = rotation };
                break;
            }
            case DragModeKind.Resize:
            {
                var handle = LayerTransform.Handles[Mode.Index];
                var anchorUnit = option ? new PointD(0.5, 0.5) : new PointD(1 - handle.X, 1 - handle.Y);
                var anchor = Original.Point(anchorUnit);
                var initialHandle = Original.Point(handle);
                double dx = initialHandle.X + point.X - Start.X - anchor.X, dy = initialHandle.Y + point.Y - Start.Y - anchor.Y;
                double span = option ? 2 : 1, rad = Original.Radians, cos = Math.Cos(rad), sin = Math.Sin(rad);
                double localX = (dx * cos + dy * sin) * span, localY = (-dx * sin + dy * cos) * span;
                double sx = handle.X * 2 - 1, sy = handle.Y * 2 - 1;
                double width = sx == 0 ? Original.Size.Width : Math.Max(1, localX * sx);
                double height = sy == 0 ? Original.Size.Height : Math.Max(1, localY * sy);
                if (lockRatio != shift)
                {
                    double factor;
                    if (sx == 0) factor = height / Original.Size.Height;
                    else if (sy == 0) factor = width / Original.Size.Width;
                    else factor = Math.Max(1 / Math.Min(Original.Size.Width, Original.Size.Height),
                        (localX * sx * Original.Size.Width + localY * sy * Original.Size.Height)
                        / (Original.Size.Width * Original.Size.Width + Original.Size.Height * Original.Size.Height));
                    width = Original.Size.Width * factor;
                    height = Original.Size.Height * factor;
                }
                double offsetX = (0.5 - anchorUnit.X) * width, offsetY = (0.5 - anchorUnit.Y) * height;
                var center = new PointD(anchor.X + offsetX * cos - offsetY * sin, anchor.Y + offsetX * sin + offsetY * cos);
                result = result with { Size = new SizeD(width, height), Origin = new PointD(center.X - width / 2, center.Y - height / 2) };
                break;
            }
        }
        return result.IsValid ? result : Original;
    }
}

public sealed record CropDrag(PointD Start, RectD Original, DragMode Mode)
{
    public RectD Updated(PointD point, double? ratio, bool symmetric = false)
    {
        switch (Mode.Kind)
        {
            case DragModeKind.Create: return CropGeometry.Create(Start, point, ratio, symmetric);
            case DragModeKind.Move: return CropGeometry.Snapped(Original.OffsetBy(point.X - Start.X, point.Y - Start.Y));
            default:
                var transform = new LayerTransform(Original.Origin, Original.Size);
                var next = new TransformDrag(transform, Start, DragMode.Resize(Mode.Index)).Updated(point, ratio != null, false, symmetric);
                return CropGeometry.Snapped(new RectD(next.Origin, next.Size));
        }
    }
}

/// <summary>Crop edges snap to nearby layer and canvas edges while dragging.</summary>
public sealed record CropSnap(double[] Xs, double[] Ys, double Tolerance)
{
    private double? Nearest(double value, double[] targets)
    {
        double? best = null;
        foreach (var target in targets)
        {
            if (Math.Abs(target - value) > Tolerance) continue;
            if (best is { } current && Math.Abs(current - value) <= Math.Abs(target - value)) continue;
            best = target;
        }
        return best;
    }

    public RectD Apply(RectD rect, CropDrag drag, PointD point, double? ratio, bool symmetric = false)
    {
        if (!(Tolerance > 0)) return rect;
        bool horizontal, vertical;
        switch (drag.Mode.Kind)
        {
            case DragModeKind.Move:
                double Shift(double[] edges, double[] targets)
                {
                    double? best = null;
                    foreach (var edge in edges)
                        if (Nearest(edge, targets) is { } t && (best == null || Math.Abs(t - edge) < Math.Abs(best.Value))) best = t - edge;
                    return best ?? 0;
                }
                return rect.OffsetBy(Shift(new[] { rect.MinX, rect.MaxX }, Xs), Shift(new[] { rect.MinY, rect.MaxY }, Ys));
            case DragModeKind.Create:
                if (ratio != null) return rect;
                horizontal = vertical = true;
                break;
            default:
                if (ratio != null) return rect;
                var handle = LayerTransform.Handles[drag.Mode.Index];
                horizontal = handle.X != 0.5; vertical = handle.Y != 0.5;
                break;
        }
        var result = rect;
        if (horizontal)
        {
            if (Math.Abs(point.X - result.MinX) <= Math.Abs(point.X - result.MaxX))
            {
                if (Nearest(result.MinX, Xs) is { } x && x < result.MaxX) result = new RectD(x, result.Y, result.MaxX - x, result.Height);
            }
            else if (Nearest(result.MaxX, Xs) is { } x2 && x2 > result.MinX) result = result with { Width = x2 - result.MinX };
        }
        if (vertical)
        {
            if (Math.Abs(point.Y - result.MinY) <= Math.Abs(point.Y - result.MaxY))
            {
                if (Nearest(result.MinY, Ys) is { } y && y < result.MaxY) result = new RectD(result.X, y, result.Width, result.MaxY - y);
            }
            else if (Nearest(result.MaxY, Ys) is { } y2 && y2 > result.MinY) result = result with { Height = y2 - result.MinY };
        }
        if (symmetric)
        {
            var center = drag.Mode.Kind == DragModeKind.Create ? drag.Start : new PointD(drag.Original.MidX, drag.Original.MidY);
            if (horizontal)
            {
                double half = point.X >= center.X ? result.MaxX - center.X : center.X - result.MinX;
                if (half >= 0.5) result = result with { X = center.X - half, Width = half * 2 };
            }
            if (vertical)
            {
                double half = point.Y >= center.Y ? result.MaxY - center.Y : center.Y - result.MinY;
                if (half >= 0.5) result = result with { Y = center.Y - half, Height = half * 2 };
            }
        }
        return result;
    }
}

public static class TransformSnap
{
    /// <summary>How close, in screen points, a guide comes before it snaps.</summary>
    public const double Distance = 10;

    public static (SizeD Offset, double? X, double? Y) Offset(RectD box, IReadOnlyList<double> xs, IReadOnlyList<double> ys, double tolerance)
    {
        var h = Shift(new[] { box.MinX, box.MidX, box.MaxX }, xs, tolerance);
        var v = Shift(new[] { box.MinY, box.MidY, box.MaxY }, ys, tolerance);
        return (new SizeD(h.Move, v.Move), h.Target, v.Target);
    }

    private static (double Move, double? Target) Shift(double[] guides, IReadOnlyList<double> targets, double tolerance)
    {
        (double Move, double Target)? best = null;
        foreach (var g in guides)
            foreach (var t in targets)
            {
                double move = t - g;
                if (Math.Abs(move) > tolerance) continue;
                if (best is { } current && Math.Abs(current.Move) <= Math.Abs(move)) continue;
                best = (move, t);
            }
        return (best?.Move ?? 0, best?.Target);
    }
}

public sealed partial class EditorSession
{
    public GradientEdit? GradientEdit { get; private set; }
    public ShapeDraft? ShapeDraft { get; private set; }
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public double ShapeCornerRadius { get; set; }
    /// <summary>Sides of the Polygon tool and points of the Star tool.</summary>
    public int ShapeSides { get; set; } = 5;
    /// <summary>Separate corner radii for rectangles and triangles; null rounds every corner by <see cref="ShapeCornerRadius"/>.</summary>
    public ShapeCorners? ShapeCornerRadii { get; set; }
    /// <summary>The Line tool's thickness in pixels.</summary>
    public double ShapeLineWeight { get; set; } = 4;
    private readonly Dictionary<Guid, (SizeD Size, RasterImage Image)> shapeTransformPreviewCache = new();
    public const int MaxShapePixels = 100_000_000;

    // MARK: Gradient (Gradient.swift)

    public void BeginGradient(PointD point)
    {
        if (Tool != NavigationTool.Gradient || !(CanPaint || GradientEdit != null) || ActiveLayer is not { } layer) return;
        if (GradientEdit is { } edit && edit.Raster.Layer.Id == layer.Id && edit.Raster.IsMask == IsMaskSelected)
        {
            edit.Start = point;
            edit.End = point;
            RefreshGradient();
            return;
        }
        if (!CanPaint) return;
        FinishOpacityEdit();
        try { GradientEdit = new GradientEdit(MakeRasterEdit(layer), point); InvalidateCanvas(); }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    public void MoveGradient(PointD? start = null, PointD? end = null)
    {
        if (GradientEdit is not { } edit) return;
        if (start is { } s) edit.Start = s;
        if (end is { } e) edit.End = e;
        RefreshGradient();
    }

    public void RefreshGradient()
    {
        if (GradientEdit is not { } edit) return;
        if (edit.HasLine)
        {
            try
            {
                var (first, last) = GradientColors(edit.Raster.IsMask);
                edit.Raster.FillGradient(gradientSettings.Shape, edit.Start, edit.End, first, last, gradientSettings.Opacity);
            }
            catch (Exception e) { CancelGradient(); BrushError = e.Message; return; }
        }
        InvalidateCanvas();
    }

    public (ColorStop First, ColorStop Last) GradientColors(bool mask)
    {
        ColorStop Stop(PaletteColor c, double alpha) => mask ? new ColorStop(c.Red, c.Red, c.Red, alpha) : new ColorStop(c.Red, c.Green, c.Blue, alpha);
        var foreground = PaletteColorFor(false);
        var colors = gradientSettings.Style == GradientStyle.ForegroundToBackground
            ? (Stop(foreground, 1), Stop(PaletteColorFor(true), 1))
            : (Stop(foreground, 1), Stop(foreground, 0));
        return gradientSettings.Reversed ? (colors.Item2, colors.Item1) : colors;
    }

    public void EndGradientDrag()
    {
        if (GradientEdit?.HasLine == false) CancelGradient();
    }

    public void CancelGradient()
    {
        if (GradientEdit == null) return;
        GradientEdit = null;
        InvalidateCanvas();
    }

    public async Task CommitGradient()
    {
        if (GradientEdit is not { } edit || IsProjectBusy) return;
        if (!edit.HasLine) { CancelGradient(); return; }
        try { await CommitRasterEdit(edit.Raster, edit.Raster.IsMask ? "Gradient Mask" : "Gradient"); }
        catch (Exception e) { BrushError = e.Message; }
        if (GradientEdit == edit) CancelGradient();
    }

    /// <summary>Switching tools, layers or targets applies the pending gradient, as in Photoshop.</summary>
    public void ResolveGradient()
    {
        if (GradientEdit == null) return;
        _ = CommitGradient();
    }

    // MARK: Shapes (ShapeTool.swift)

    public static SKPath ShapePath(ShapeKind kind, RectD rect, double cornerRadius = 0) =>
        ShapePath(new LayerShapeStyle(kind, 0, 0, 0, cornerRadius), rect);

    /// <summary>The filled outline of a shape laid out in <paramref name="rect"/>.</summary>
    public static SKPath ShapePath(LayerShapeStyle style, RectD rect)
    {
        var path = new SKPath();
        double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2, rx = rect.Width / 2, ry = rect.Height / 2;
        switch (style.Kind)
        {
            case ShapeKind.Ellipse:
                path.AddOval(rect.ToSK());
                break;
            case ShapeKind.Line:
            {
                // The ends sit half the weight inside the box, so the stroke fills it exactly.
                double h = style.Weight / 2;
                double x0 = rect.X + Math.Min(h, rx), x1 = rect.X + rect.Width - Math.Min(h, rx);
                double top = rect.Y + Math.Min(h, ry), bottom = rect.Y + rect.Height - Math.Min(h, ry);
                using var line = new SKPath();
                line.MoveTo((float)x0, (float)(style.Rising ? bottom : top));
                line.LineTo((float)x1, (float)(style.Rising ? top : bottom));
                using var pen = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(0.5, style.Weight), StrokeCap = SKStrokeCap.Butt };
                pen.GetFillPath(line, path);
                break;
            }
            case ShapeKind.Triangle:
                // Corners clockwise from the top point, each with its own radius.
                RoundedPolygon(path, new[]
                {
                    new SKPoint((float)cx, (float)rect.Y), new SKPoint((float)(rect.X + rect.Width), (float)(rect.Y + rect.Height)),
                    new SKPoint((float)rect.X, (float)(rect.Y + rect.Height)),
                }, style.Radius);
                break;
            case ShapeKind.Polygon:
            case ShapeKind.Star:
            {
                int sides = Math.Clamp(style.Sides, 3, 100);
                bool star = style.Kind == ShapeKind.Star;
                int count = star ? sides * 2 : sides;
                var points = new SKPoint[count];
                for (int i = 0; i < count; i++)
                {
                    double angle = -Math.PI / 2 + i * 2 * Math.PI / count;
                    double scale = star && i % 2 == 1 ? 0.5 : 1;
                    points[i] = new SKPoint((float)(cx + Math.Cos(angle) * rx * scale), (float)(cy + Math.Sin(angle) * ry * scale));
                }
                RoundedPolygon(path, points, _ => style.CornerRadius);
                break;
            }
            default:
            {
                // Each corner rounds on its own (top-left, top-right, bottom-right, bottom-left), up to half the shorter side.
                double limit = Math.Min(rx, ry);
                var radii = Enumerable.Range(0, 4).Select(i => (float)Math.Clamp(style.Radius(i), 0, limit)).ToArray();
                if (radii.All(r => r <= 0)) { path.AddRect(rect.ToSK()); break; }
                using var round = new SKRoundRect();
                round.SetRectRadii(rect.ToSK(), radii.Select(r => new SKPoint(r, r)).ToArray());
                path.AddRoundRect(round);
                break;
            }
        }
        return path;
    }

    /// <summary>A closed polygon whose corners are rounded by tangent arcs, each radius shrunk to fit its two edges.</summary>
    private static void RoundedPolygon(SKPath path, IReadOnlyList<SKPoint> points, Func<int, double> radius)
    {
        int n = points.Count;
        static SKPoint Mid(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        // Start halfway along the last edge, so every corner (the first included) can be an arc.
        path.MoveTo(Mid(points[n - 1], points[0]));
        for (int i = 0; i < n; i++)
        {
            SKPoint p = points[i], prev = points[(i + n - 1) % n], next = points[(i + 1) % n];
            double ax = prev.X - p.X, ay = prev.Y - p.Y, bx = next.X - p.X, by = next.Y - p.Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            double r = Math.Max(0, radius(i));
            if (r > 0.01 && la > 0 && lb > 0)
            {
                double cos = Math.Clamp((ax * bx + ay * by) / (la * lb), -1, 1), half = Math.Acos(cos) / 2;
                // The arc touches each edge r / tan(θ/2) from the corner; it must stay within half of each edge.
                r = Math.Min(r, Math.Min(la, lb) / 2 * Math.Tan(half));
            }
            if (r > 0.01) path.ArcTo(p, Mid(p, next), (float)r);
            else path.LineTo(p);
        }
        path.Close();
    }

    public void BeginShape(PointD point)
    {
        if (Tool != NavigationTool.Shape || !CanEditLayers || !point.IsFinite) return;
        var anchor = new PointD(Math.Round(point.X, MidpointRounding.AwayFromZero), Math.Round(point.Y, MidpointRounding.AwayFromZero));
        ShapeDraft = new ShapeDraft(ShapeKind, anchor, new RectD(anchor, SizeD.Zero), Editing.ShapeDraft.Rounds(ShapeKind) ? ShapeCornerRadius : 0,
            ShapeKind == ShapeKind.Line ? anchor : null, ShapeSides, ShapeLineWeight, ShapeCornerRadii);
        InvalidateCanvas();
    }

    /// <summary>Shift makes a square or circle (for a line, snaps it to 45°); Alt draws from the centre.</summary>
    public void DragShape(PointD point, bool square, bool fromCenter)
    {
        if (ShapeDraft is not { } draft || !point.IsFinite) return;
        if (draft.Kind == ShapeKind.Line)
        {
            var end = point;
            if (square)
            {
                double dx = point.X - draft.Anchor.X, dy = point.Y - draft.Anchor.Y;
                double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), length = Math.Sqrt(dx * dx + dy * dy);
                end = new PointD(draft.Anchor.X + Math.Round(Math.Cos(angle) * length, 6), draft.Anchor.Y + Math.Round(Math.Sin(angle) * length, 6));
            }
            var start = fromCenter ? new PointD(2 * draft.Anchor.X - end.X, 2 * draft.Anchor.Y - end.Y) : draft.Anchor;
            double h = draft.Weight / 2;
            var box = new RectD(Math.Min(start.X, end.X) - h, Math.Min(start.Y, end.Y) - h,
                Math.Abs(end.X - start.X) + draft.Weight, Math.Abs(end.Y - start.Y) + draft.Weight);
            ShapeDraft = draft with { Rect = box, End = end };
        }
        else ShapeDraft = draft with { Rect = DragBox.Rect(draft.Anchor, point, square, fromCenter) };
        InvalidateCanvas();
    }

    public void CancelShape()
    {
        if (ShapeDraft == null) return;
        ShapeDraft = null;
        InvalidateCanvas();
    }

    /// <summary>Shift+U steps through the shape tools, as Photoshop's does.</summary>
    public void ToggleShapeKind()
    {
        CancelShape();
        var kinds = Enum.GetValues<ShapeKind>();
        ShapeKind = kinds[(Array.IndexOf(kinds, ShapeKind) + 1) % kinds.Length];
        Notify();
    }

    public void FinishShape()
    {
        if (ShapeDraft is not { } draft) return;
        ShapeDraft = null;
        var rect = draft.Rect;
        bool line = draft.Kind == ShapeKind.Line;
        bool tooShort = line && draft.End is { } e && Math.Abs(e.X - draft.Anchor.X) + Math.Abs(e.Y - draft.Anchor.Y) < 1;
        if (!CanEditLayers || document == null || rect.Width < 1 || rect.Height < 1 || tooShort) { InvalidateCanvas(); return; }
        // A line's box grows to whole pixels, so the layer lines up with the canvas grid.
        if (line)
            rect = new RectD(Math.Floor(rect.X), Math.Floor(rect.Y),
                Math.Ceiling(rect.X + rect.Width) - Math.Floor(rect.X), Math.Ceiling(rect.Y + rect.Height) - Math.Floor(rect.Y));
        if ((long)rect.Width * (long)rect.Height > MaxShapePixels) { BrushError = "That shape is too large. A shape can cover up to 100 megapixels."; Notify(); return; }
        try
        {
            var style = draft.Style(ForegroundColor);
            var image = ShapeImage(style, rect.Size);
            AddPixelLayer(image, rect.Origin, NextShapeName(draft.Kind), draft.Kind.Name(), dropsSelection: false, shape: new LayerShape(style, image));
        }
        catch (Exception ex) { BrushError = ex.Message; Notify(); }
    }

    public string NextShapeName(ShapeKind kind)
    {
        var names = new HashSet<string>(document?.Layers.Select(l => l.Name) ?? Enumerable.Empty<string>());
        int number = 1;
        while (names.Contains($"{kind.Name()} {number}")) number++;
        return $"{kind.Name()} {number}";
    }

    /// <summary>A shape layer scaled to a new size draws its shape again, so a rounded corner keeps its radius and a line
    /// keeps its weight.</summary>
    public void RedrawShape(int index)
    {
        if (document?.Layers[index] is not { } layer || layer.LiveShape is not { } shape || layer.Asset is not { } asset) return;
        int width = Math.Max(1, (int)Math.Round(layer.Transform.Size.Width, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(layer.Transform.Size.Height, MidpointRounding.AwayFromZero));
        if ((width == asset.Image.Width && height == asset.Image.Height) || (long)width * height > MaxShapePixels) return;
        var image = ShapeImage(shape.Style, new SizeD(width, height));
        var mask = layer.Mask is { Placement: null } m ? m with { Placement = layer.MaskTransform } : layer.Mask;
        ReplaceLayer(index, layer with { Asset = PixelOps.Asset(image, asset.Name), Shape = new LayerShape(shape.Style, image), Mask = mask });
    }

    /// <summary>While a rounded rectangle or a line is being scaled, the shape drawn at the dragged size (at most 2048 across).</summary>
    public RasterImage? ShapeTransformPreview(ImageLayer layer, LayerTransform transform)
    {
        if (TransformEditState == null || layer.LiveShape is not { } shape
            || !(shape.Style.CornerRadius > 0 || shape.Style.Corners != null || shape.Style.Kind == ShapeKind.Line))
        {
            if (shapeTransformPreviewCache.Count > 0 && TransformEditState == null) shapeTransformPreviewCache.Clear();
            return null;
        }
        var size = transform.Size;
        if (size.Width < 1 || size.Height < 1 || (Math.Abs(size.Width - shape.Image.Width) < 0.5 && Math.Abs(size.Height - shape.Image.Height) < 0.5)) return null;
        double factor = Math.Min(1, 2048 / Math.Max(size.Width, size.Height));
        var drawn = new SizeD(Math.Max(1, Math.Round(size.Width * factor)), Math.Max(1, Math.Round(size.Height * factor)));
        if (shapeTransformPreviewCache.TryGetValue(layer.Id, out var cached) && cached.Size == drawn) return cached.Image;
        var image = ShapeImage(shape.Style.Scaled(factor), drawn);
        shapeTransformPreviewCache[layer.Id] = (drawn, image);
        return image;
    }

    public static RasterImage ShapeImage(ShapeKind kind, SizeD size, PaletteColor color, double cornerRadius = 0) =>
        ShapeImage(new LayerShapeStyle(kind, color.Red, color.Green, color.Blue, cornerRadius), size);

    public static RasterImage ShapeImage(LayerShapeStyle style, SizeD size)
    {
        var bitmap = PixelOps.NewRgba((int)size.Width, (int)size.Height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = style.Color.ToSK(), IsAntialias = true })
        using (var path = ShapePath(style, new RectD(0, 0, size.Width, size.Height)))
            canvas.DrawPath(path, paint);
        return RasterImage.Adopt(bitmap);
    }

    // MARK: Crop (Crop.swift)

    public (double[] Xs, double[] Ys) TransformSnapTargets(ISet<Guid> moving)
    {
        if (document == null) return (Array.Empty<double>(), Array.Empty<double>());
        var xs = new List<double> { 0, document.Width / 2.0, document.Width };
        var ys = new List<double> { 0, document.Height / 2.0, document.Height };
        if (ShowsLayoutGrid && SnapsToLayoutGrid)
        {
            for (double x = GridSpacing; x < document.Width; x += GridSpacing) xs.Add(x);
            for (double y = GridSpacing; y < document.Height; y += GridSpacing) ys.Add(y);
        }
        foreach (var layer in document.RenderLayers().Where(l => l.Asset != null && !moving.Contains(l.Id)))
        {
            var corners = DisplayedTransform(layer).Corners();
            double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X), minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
            xs.AddRange(new[] { R(minX), R((minX + maxX) / 2), R(maxX) });
            ys.AddRange(new[] { R(minY), R((minY + maxY) / 2), R(maxY) });
        }
        return (xs.ToArray(), ys.ToArray());
    }

    private static double R(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    public LayerTransform SnappedMove(LayerTransform draft, ISet<Guid> moving, double tolerance)
    {
        var corners = draft.Corners();
        double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X), minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
        var box = new RectD(minX, minY, maxX - minX, maxY - minY);
        var targets = TransformSnapTargets(moving);
        var snap = TransformSnap.Offset(box, targets.Xs, targets.Ys, tolerance);
        SnapGuides = (snap.X is { } x ? new[] { x } : Array.Empty<double>(), snap.Y is { } y ? new[] { y } : Array.Empty<double>());
        if (snap.Offset == SizeD.Zero) return draft;
        return draft with { Origin = draft.Origin.Offset(snap.Offset.Width, snap.Offset.Height) };
    }

    public (double[] Xs, double[] Ys) CropSnapTargets()
    {
        if (document == null) return (Array.Empty<double>(), Array.Empty<double>());
        var xs = new List<double> { 0, document.Width };
        var ys = new List<double> { 0, document.Height };
        if (ShowsLayoutGrid && SnapsToLayoutGrid)
        {
            for (double x = GridSpacing; x < document.Width; x += GridSpacing) xs.Add(x);
            for (double y = GridSpacing; y < document.Height; y += GridSpacing) ys.Add(y);
        }
        foreach (var layer in document.RenderLayers().Where(l => l.Asset != null))
        {
            var corners = DisplayedTransform(layer).Corners();
            xs.AddRange(new[] { R(corners.Min(c => c.X)), R(corners.Max(c => c.X)) });
            ys.AddRange(new[] { R(corners.Min(c => c.Y)), R(corners.Max(c => c.Y)) });
        }
        return (xs.ToArray(), ys.ToArray());
    }

    public RectD? VisibleCropRect => Tool == NavigationTool.Crop && document != null ? CropRect ?? new RectD(0, 0, document.Width, document.Height) : null;

    public double? CropRatio => CropRatioChoice switch
    {
        "Original" => document is { } d ? (double)d.Width / d.Height : null,
        "1:1" => 1, "4:3" => 4.0 / 3, "16:9" => 16.0 / 9, _ => null,
    };

    public void CancelCrop()
    {
        if (CropRect == null) return;
        CropRect = null;
        InvalidateCanvas();
    }

    public void ChangeCropRatio()
    {
        if (VisibleCropRect is not { } rect || CropRatio is not { } ratio) return;
        double height = rect.Width / ratio;
        var next = CropGeometry.Snapped(new RectD(rect.X, rect.MidY - height / 2, rect.Width, height));
        if (CropGeometry.Valid(next)) { CropRect = next; InvalidateCanvas(); }
    }

    public async Task CommitCrop()
    {
        if (!CanStartProjectOperation || CropRect is not { } rect || !CropGeometry.Valid(rect) || ProjectSnapshot() is not { } snapshot) return;
        IsProjectBusy = true;
        try
        {
            var result = await Task.Run(() => CanvasResizer.Resize(snapshot, new CanvasSizeOptions((int)rect.Width, (int)rect.Height, ContentOffset: new PointD(-rect.X, -rect.Y))));
            IsProjectBusy = false;
            CropRect = null;
            ApplyDocumentSize(result, "Crop");
        }
        catch (Exception e) { CropError = e.Message; }
        finally { IsProjectBusy = false; InvalidateCanvas(); }
    }

    public async Task ResizeCanvas(CanvasSizeOptions options)
    {
        if (ProjectSnapshot() is not { } snapshot) return;
        CancelCrop();
        CommitTransform();
        IsProjectBusy = true;
        try
        {
            var result = await Task.Run(() => CanvasResizer.Resize(snapshot, options));
            IsProjectBusy = false;
            ApplyDocumentSize(result, "Canvas Size");
        }
        finally { IsProjectBusy = false; InvalidateCanvas(); }
    }

    public async Task ResizeImage(ImageSizeOptions options)
    {
        if (ProjectSnapshot() is not { } snapshot) return;
        CancelCrop();
        CommitTransform();
        IsProjectBusy = true;
        try
        {
            var result = await Task.Run(() => ImageResizer.Resize(snapshot, options));
            IsProjectBusy = false;
            ApplyDocumentSize(result, "Image Size");
        }
        finally { IsProjectBusy = false; InvalidateCanvas(); }
    }

    public void ApplyDocumentSize(ProjectSnapshot snapshot, string actionName)
    {
        if (document?.Id != snapshot.Manifest.DocumentId) return;
        BeginEdit(actionName);
        var m = snapshot.Manifest;
        var previous = document.Layers.ToDictionary(l => l.Id);
        Document = document with
        {
            Width = m.Width, Height = m.Height, Resolution = m.Resolution ?? 72,
            Layers = m.Layers.Select(r => new ImageLayer
            {
                Id = r.Id, Asset = snapshot.Images.GetValueOrDefault(r.Id), Name = r.Name, IsVisible = r.IsVisible, IsLocked = r.IsLocked == true, Transform = r.Transform,
                ParentId = r.ParentId, IsGroup = r.IsGroup == true, Opacity = r.Opacity ?? 1, BlendMode = r.BlendMode ?? LayerBlendMode.Normal,
                Mask = snapshot.Mask(r), MaskSourceId = r.MaskSourceId, Adjustment = r.Adjustment,
                // A layer whose pixels didn't change stays the shape it was.
                Shape = previous.TryGetValue(r.Id, out var old) && old.Shape is { } s && ReferenceEquals(snapshot.Images.GetValueOrDefault(r.Id)?.Image, s.Image) ? s : null,
                Text = previous.TryGetValue(r.Id, out var oldText) && oldText.Text is { } t && ReferenceEquals(snapshot.Images.GetValueOrDefault(r.Id)?.Image, t.Image) ? t : null,
            }).ToImmutableList(),
            Selection = null,
        };
        EndEdit();
        Viewport.Fit(document.Size);
        InvalidateCanvas();
    }
}

/// <summary>Canvas Size and Crop: layers keep their pixels and move by the offset (CanvasResizer.swift).</summary>
public static class CanvasResizer
{
    public static ProjectSnapshot Resize(ProjectSnapshot snapshot, CanvasSizeOptions options)
    {
        if (options.Width < 1 || options.Width > 30_000 || options.Height < 1 || options.Height > 30_000 || options.Anchor < 0 || options.Anchor > 8)
            throw ProjectException.TooLarge();
        var old = snapshot.Manifest;
        var offset = options.Offset(old.Width, old.Height);
        if (!offset.IsFinite || Math.Abs(offset.X) > 1_000_000 || Math.Abs(offset.Y) > 1_000_000) throw ProjectException.Invalid();
        if (options.Width == old.Width && options.Height == old.Height && offset == PointD.Zero) return snapshot;
        var layers = new List<ProjectLayerRecord>();
        foreach (var layer in old.Layers)
        {
            var transform = layer.Transform with { Origin = layer.Transform.Origin.Offset(offset.X, offset.Y) };
            if (!transform.IsValid) throw ProjectException.TooLarge();
            layers.Add(layer with
            {
                Transform = transform,
                MaskPlacement = layer.MaskPlacement is { } p ? p with { Origin = p.Origin.Offset(offset.X, offset.Y) } : null,
            });
        }
        var images = new Dictionary<Guid, ImageAsset>(snapshot.Images);
        if (options.Fill is { } color && (options.Width > old.Width || options.Height > old.Height))
        {
            long used = images.Values.Sum(a => (long)a.Image.Width * a.Image.Height);
            if ((long)options.Width * options.Height > 100_000_000 - used || layers.Count >= 10_000) throw ProjectException.TooLarge();
            var bitmap = PixelOps.NewRgba(options.Width, options.Height, false);
            bitmap.Erase(color.ToSK());
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.ClipRect(new SKRect((float)offset.X, (float)offset.Y, (float)(offset.X + old.Width), (float)(offset.Y + old.Height)));
                canvas.Clear(SKColors.Transparent);
            }
            var id = Guid.NewGuid();
            images[id] = PixelOps.Asset(RasterImage.Adopt(bitmap), "Canvas Extension");
            layers.Insert(0, new ProjectLayerRecord
            {
                Id = id, Name = "Canvas Extension", IsVisible = true, Transform = LayerTransform.Canvas(options.Width, options.Height),
                ImageFile = ProjectLayerRecord.ImageFileName(id),
            });
        }
        var manifest = old with { Width = options.Width, Height = options.Height, Layers = layers };
        return snapshot with { Manifest = manifest, Images = images };
    }
}

/// <summary>Image Size: every layer rasterized through its transform at the new scale (ImageResizer.swift).</summary>
public static class ImageResizer
{
    public static ProjectSnapshot Resize(ProjectSnapshot snapshot, ImageSizeOptions options)
    {
        if (options.Width < 1 || options.Width > 30_000 || options.Height < 1 || options.Height > 30_000
            || !double.IsFinite(options.Resolution) || options.Resolution < 1 || options.Resolution > 9600) throw ProjectException.TooLarge();
        var old = snapshot.Manifest;
        if (old.Width == options.Width && old.Height == options.Height)
            return snapshot with { Manifest = old with { Resolution = options.Resolution } };
        if ((long)options.Width * options.Height > 100_000_000) throw ProjectException.TooLarge();
        double sx = (double)options.Width / old.Width, sy = (double)options.Height / old.Height;
        var images = new Dictionary<Guid, ImageAsset>();
        var masks = new Dictionary<Guid, MaskAsset>();
        long usedPixels = 0, usedMaskPixels = 0;
        var layers = new List<ProjectLayerRecord>();
        foreach (var layer in old.Layers)
        {
            var corners = layer.Transform.Corners().Select(p => new PointD(p.X * sx, p.Y * sy)).ToArray();
            double left = Math.Floor(corners.Min(c => c.X)), top = Math.Floor(corners.Min(c => c.Y));
            int width = (int)(Math.Ceiling(corners.Max(c => c.X)) - left), height = (int)(Math.Ceiling(corners.Max(c => c.Y)) - top);
            var transform = new LayerTransform(new PointD(left, top), new SizeD(width, height), Sampling: options.Sampling);
            if (!transform.IsValid) throw ProjectException.TooLarge();
            var mapping = Affine.Scale(sx, sy).Concat(Affine.Translation(-left, -top));
            var sourceTransform = layer.Transform with { Sampling = options.Sampling };
            if (layer.ImageFile != null)
            {
                if (width < 1 || width > 30_000 || height < 1 || height > 30_000 || (long)width * height > 100_000_000 - usedPixels) throw ProjectException.TooLarge();
                usedPixels += (long)width * height;
                if (!snapshot.Images.TryGetValue(layer.Id, out var source)) throw ProjectException.MissingImage();
                var bitmap = PixelOps.NewRgba(width, height);
                using (var surface = new RenderSurface(bitmap, mapping)) LayerRenderer.Draw(surface, source.Image, sourceTransform);
                images[layer.Id] = PixelOps.Asset(RasterImage.Adopt(bitmap), source.Name);
            }
            if (layer.MaskFile != null)
            {
                if (!snapshot.Masks.TryGetValue(layer.Id, out var source)) throw ProjectException.MissingImage();
                if (source.Image.IsUniform1x1 || layer.MaskPlacement != null) masks[layer.Id] = source;
                else
                {
                    if (width < 1 || width > 30_000 || height < 1 || height > 30_000 || (long)width * height > 100_000_000 - usedMaskPixels) throw ProjectException.TooLarge();
                    usedMaskPixels += (long)width * height;
                    using var gray = PixelOps.NewRgba(width, height, false);
                    gray.Erase(SKColors.Black);
                    using (var surface = new RenderSurface(gray, mapping)) LayerRenderer.DrawCoverage(surface, source.Image, sourceTransform);
                    masks[layer.Id] = PixelOps.MaskAssetFrom(PixelOps.GrayFromRed(gray));
                }
            }
            layers.Add(layer with
            {
                Transform = transform,
                MaskPlacement = layer.MaskPlacement is { } p ? p.Placing(p.UnitToDocument.Concat(Affine.Scale(sx, sy))) : null,
            });
        }
        var manifest = old with { Resolution = options.Resolution, Width = options.Width, Height = options.Height, Layers = layers };
        return new ProjectSnapshot(manifest, images, masks);
    }
}
