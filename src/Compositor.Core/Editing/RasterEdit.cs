using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;
using SkiaSharp;

namespace Compositor.Editing;

/// <summary>A clone or blur sample: document-size pixels to copy from and the offset from brush to source.</summary>
public sealed record CloneSample(RasterImage? Image, MaskImage? Mask, SizeD Offset);

public enum GradientShape { Linear, Radial }
public enum GradientStyle { ForegroundToBackground, ForegroundToTransparent }

public sealed record GradientSettings(GradientShape Shape = GradientShape.Linear, GradientStyle Style = GradientStyle.ForegroundToTransparent,
    bool Reversed = false, double Opacity = 1);

/// <summary>A colour with straight alpha, 0–1 components (a gradient stop).</summary>
public readonly record struct ColorStop(double R, double G, double B, double A);

/// <summary>
/// A tiled raster edit of a layer's pixels or mask (BrushStroke.swift): brush strokes, erasing, spot healing, clone
/// stamp, blur, fills, gradients, clearing and moving selected pixels. Only touched 256-pixel tiles allocate; the grid
/// is the layer's own pixel grid grown to cover the canvas, so painting past a layer's edge grows it.
/// </summary>
public sealed class RasterEdit
{
    public const int TileSize = 256;
    public ImageLayer Layer { get; }
    public bool IsMask { get; }
    public int Width { get; }
    public int Height { get; }
    public BrushSettings Settings { get; }
    public RectD Canvas { get; }
    public Affine PixelToDocument { get; }
    private readonly Affine documentToPixel;
    public RectD SourceRect { get; }
    public LayerTransform PaintTransform { get; }
    private readonly RasterImage? source;
    private readonly MaskImage? sourceMask;
    private readonly int bpp;
    public long PixelLimit { get; set; } = 100_000_000;
    public SelectionClip? SelectionClip { get; set; }
    public CloneSample? Clone { get; set; }
    public bool IsBlur { get; set; }
    public bool ReplacesWithClone { get; set; }
    public string? EditName { get; set; }
    public RectD? DirtyDocumentRect { get; private set; }
    public int Revision { get; private set; }

    private RectD? allocatedBounds;
    private PointD? previous;
    private readonly List<PointD> samples = new();
    private readonly Dictionary<int, byte[]?> tailBackup = new();
    private double distanceToNext;

    public sealed class Tile
    {
        public SKRectI Rect;
        public byte[] Base = Array.Empty<byte>();
        public byte[] Pixels = Array.Empty<byte>();
        public byte[]? Coverage;
        public byte[]? Selection;
        public bool HasImage;
        public int Version;
        internal SKImage? preview;
        internal int previewVersion = -1;
    }
    private readonly Dictionary<int, Tile> tiles = new();
    private readonly Dictionary<int, SKRectI> dirtyTiles = new();
    private int Columns => (Width + TileSize - 1) / TileSize;

    public IEnumerable<Tile> Patches => tiles.Values.Where(t => t.HasImage);
    public bool HasPatches => tiles.Values.Any(t => t.HasImage);

    public RasterEdit(ImageLayer layer, bool mask, BrushSettings settings, SizeD canvas)
    {
        Layer = layer;
        IsMask = mask;
        Settings = settings;
        Canvas = new RectD(0, 0, canvas.Width, canvas.Height);
        bpp = mask ? 1 : 4;
        // A mask on its own placement is painted in its own grid; otherwise the grid is the layer's.
        LayerTransform? placement = mask ? layer.Mask?.Placement : null;
        var baseTransform = placement ?? layer.Transform;
        int originalWidth = placement != null ? layer.Mask!.Asset.Image.Width : layer.Asset?.Image.Width ?? (int)Math.Round(layer.Size.Width);
        int originalHeight = placement != null ? layer.Mask!.Asset.Image.Height : layer.Asset?.Image.Height ?? (int)Math.Round(layer.Size.Height);
        if (originalWidth < 1 || originalWidth > 30_000 || originalHeight < 1 || originalHeight > 30_000) throw ProjectException.TooLarge();
        var originalMapping = LayerTransform.PixelToDocument(baseTransform, originalWidth, originalHeight);
        var originalBounds = new RectD(0, 0, originalWidth, originalHeight);
        var extent = mask ? originalBounds : originalBounds.Union(Canvas.Apply(originalMapping.Inverted()).Integral);
        if (extent.Width > 1_000_000_000 || extent.Height > 1_000_000_000) throw ProjectException.TooLarge();
        Width = (int)extent.Width;
        Height = (int)extent.Height;
        SourceRect = originalBounds.OffsetBy(-extent.X, -extent.Y);
        PixelToDocument = originalMapping.Translated(extent.X, extent.Y);
        documentToPixel = PixelToDocument.Inverted();
        var expanded = baseTransform with
        {
            Size = new SizeD(Width * baseTransform.Size.Width / originalWidth, Height * baseTransform.Size.Height / originalHeight),
        };
        var center = originalMapping.Apply(new PointD(extent.MidX, extent.MidY));
        PaintTransform = expanded with { Origin = new PointD(center.X - expanded.Size.Width / 2, center.Y - expanded.Size.Height / 2) };
        if (Width < 1 || Height < 1 || !double.IsFinite(settings.Diameter) || settings.Diameter < 1 || settings.Diameter > 2000
            || !double.IsFinite(settings.Hardness) || settings.Hardness < 0 || settings.Hardness > 1
            || !double.IsFinite(settings.Opacity) || settings.Opacity < 0.01 || settings.Opacity > 1) throw ProjectException.TooLarge();
        source = mask ? null : layer.Asset?.Image;
        sourceMask = mask ? layer.Mask?.Asset.Image : null;
    }

    private bool HasSource => source != null || sourceMask != null;

    // MARK: Strokes

    /// <summary>Samples arrive sparsely, so dabs follow a centripetal Catmull–Rom curve through them; the newest piece
    /// is a provisional straight tail, replaced by the curve when the next sample arrives or on Flush.</summary>
    public void Append(PointD point)
    {
        if (!point.IsFinite || Math.Abs(point.X) > 10_000_000 || Math.Abs(point.Y) > 10_000_000) return;
        if (samples.Count > 0 && samples[^1] == point) return;
        var changed = RemoveTail();
        samples.Add(point);
        if (samples.Count > 4) samples.RemoveAt(0);
        int count = samples.Count;
        if (count == 1) Walk(point, changed);
        else if (count >= 3) Curve(samples[count - 3], samples[count - 2], samples[Math.Max(0, count - 4)], samples[count - 1], changed);
        if (count >= 2) DrawTail(samples[count - 2], point, changed);
        Publish(changed);
    }

    public void Flush()
    {
        var changed = RemoveTail();
        int count = samples.Count;
        if (count >= 2)
        {
            Curve(samples[count - 2], samples[count - 1], samples[Math.Max(0, count - 3)], samples[count - 1], changed);
            var last = samples[count - 1];
            samples.Clear();
            samples.Add(last);
        }
        Publish(changed);
    }

    private IEnumerable<int> KeysFor(RectD documentBox)
    {
        var box = documentBox.Intersect(Canvas);
        if (box.IsNull || box.IsEmpty) yield break;
        var affected = box.Apply(documentToPixel).Integral.Intersect(new RectD(0, 0, Width, Height));
        if (affected.IsNull || affected.IsEmpty) yield break;
        int columns = Columns;
        for (int y = (int)affected.MinY / TileSize; y <= ((int)Math.Ceiling(affected.MaxY) - 1) / TileSize; y++)
            for (int x = (int)affected.MinX / TileSize; x <= ((int)Math.Ceiling(affected.MaxX) - 1) / TileSize; x++)
                yield return y * columns + x;
    }

    private void DrawTail(PointD start, PointD end, HashSet<int> changed)
    {
        double reach = Settings.Diameter / 2 + 2;
        var box = new RectD(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)).Inset(-reach, -reach);
        foreach (var key in KeysFor(box))
            tailBackup[key] = tiles.TryGetValue(key, out var t) && t.Coverage != null ? (byte[])t.Coverage.Clone() : null;
        var saved = (previous, distanceToNext);
        Walk(end, changed);
        (previous, distanceToNext) = saved;
    }

    private HashSet<int> RemoveTail()
    {
        var restored = new HashSet<int>();
        foreach (var (key, backup) in tailBackup)
        {
            if (!tiles.TryGetValue(key, out var tile) || tile.Coverage == null) continue;
            if (backup != null) Buffer.BlockCopy(backup, 0, tile.Coverage, 0, backup.Length);
            else Array.Clear(tile.Coverage);
            dirtyTiles[key] = new SKRectI(0, 0, tile.Rect.Width, tile.Rect.Height);
            restored.Add(key);
        }
        tailBackup.Clear();
        return restored;
    }

    private void Curve(PointD start, PointD end, PointD before, PointD after, HashSet<int> changed)
    {
        static double Knot(double t, PointD a, PointD b) => t + Math.Max(0.0001, Math.Sqrt(a.DistanceTo(b)));
        static PointD Mix(PointD a, PointD b, double ta, double tb, double t)
        {
            double wa = (tb - t) / (tb - ta), wb = (t - ta) / (tb - ta);
            return new PointD(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb);
        }
        double t0 = 0, t1 = Knot(t0, before, start), t2 = Knot(t1, start, end), t3 = Knot(t2, end, after);
        int pieces = Math.Max(1, (int)Math.Ceiling(start.DistanceTo(end) / 2));
        for (int index = 1; index <= pieces; index++)
        {
            double t = t1 + (t2 - t1) * index / pieces;
            var a1 = Mix(before, start, t0, t1, t); var a2 = Mix(start, end, t1, t2, t); var a3 = Mix(end, after, t2, t3, t);
            var b1 = Mix(a1, a2, t0, t2, t); var b2 = Mix(a2, a3, t1, t3, t);
            Walk(index == pieces ? end : Mix(b1, b2, t1, t2, t), changed);
        }
    }

    public static double SpacingFraction(double hardness) => hardness >= 1 ? 0.015 : 0.025;

    private void Walk(PointD point, HashSet<int> changed)
    {
        double spacing = Math.Max(0.25, Settings.Diameter * SpacingFraction(Settings.Hardness));
        if (previous is { } prev)
        {
            double dx = point.X - prev.X, dy = point.Y - prev.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0)
            {
                double distance = distanceToNext;
                while (distance <= length)
                {
                    Dab(new PointD(prev.X + dx * distance / length, prev.Y + dy * distance / length), changed);
                    distance += spacing;
                }
                distanceToNext = distance - length;
            }
        }
        else
        {
            Dab(point, changed);
            distanceToNext = spacing;
        }
        previous = point;
    }

    /// <summary>Soft-brush falloff between the hardness radius and the rim: a normalized Gaussian reaching zero at the rim.</summary>
    public static double Falloff(double u)
    {
        const double k = 2.5;
        return Math.Max(0, (Math.Exp(-k * u * u) - Math.Exp(-k)) / (1 - Math.Exp(-k)));
    }

    private void Dab(PointD point, HashSet<int> changed)
    {
        double radius = Settings.Diameter / 2;
        var circle = new RectD(point.X - radius, point.Y - radius, radius * 2, radius * 2);
        var clipped = circle.Intersect(Canvas);
        if (clipped.IsNull || clipped.IsEmpty) return;
        var affected = clipped.Apply(documentToPixel).Integral.Intersect(new RectD(0, 0, Width, Height));
        if (affected.IsNull || affected.IsEmpty) return;
        bool hard = Settings.Hardness >= 1;
        double inner = radius * Settings.Hardness;
        double span = Math.Max(1e-9, radius - inner);
        var m = PixelToDocument;
        foreach (var key in KeysFor(circle))
        {
            AllocateTile(key);
            var tile = tiles[key];
            var local = Intersect(affected, tile.Rect);
            if (local.Width <= 0 || local.Height <= 0) continue;
            tile.Coverage ??= new byte[tile.Rect.Width * tile.Rect.Height];
            var cov = tile.Coverage;
            int tw = tile.Rect.Width;
            for (int gy = local.Top; gy < local.Bottom; gy++)
            {
                for (int gx = local.Left; gx < local.Right; gx++)
                {
                    double px = gx + 0.5, py = gy + 0.5;
                    double dxDoc = m.A * px + m.C * py + m.Tx, dyDoc = m.B * px + m.D * py + m.Ty;
                    if (dxDoc < 0 || dyDoc < 0 || dxDoc >= Canvas.Width || dyDoc >= Canvas.Height) continue;
                    double ddx = dxDoc - point.X, ddy = dyDoc - point.Y;
                    double d = Math.Sqrt(ddx * ddx + ddy * ddy);
                    double t;
                    if (hard) t = Math.Clamp(radius - d + 0.5, 0, 1);
                    else if (d >= radius) continue;
                    else t = d <= inner ? 1 : Falloff((d - inner) / span);
                    if (t <= 0) continue;
                    int index = (gy - tile.Rect.Top) * tw + gx - tile.Rect.Left;
                    int c = cov[index];
                    int v = (int)Math.Round(t * 255);
                    // Hard tips keep their antialiased silhouette (lighten); soft tips accumulate (screen).
                    cov[index] = (byte)(hard ? Math.Max(c, v) : c + v - (c * v + 127) / 255);
                }
            }
            var dirty = new SKRectI(local.Left - tile.Rect.Left, local.Top - tile.Rect.Top, local.Right - tile.Rect.Left, local.Bottom - tile.Rect.Top);
            dirtyTiles[key] = dirtyTiles.TryGetValue(key, out var existing) ? SKRectI.Union(existing, dirty) : dirty;
            changed.Add(key);
        }
    }

    private static SKRectI Intersect(RectD a, SKRectI b)
    {
        int left = Math.Max((int)a.MinX, b.Left), top = Math.Max((int)a.MinY, b.Top);
        int right = Math.Min((int)Math.Ceiling(a.MaxX), b.Right), bottom = Math.Min((int)Math.Ceiling(a.MaxY), b.Bottom);
        return new SKRectI(left, top, Math.Max(left, right), Math.Max(top, bottom));
    }

    private void AllocateTile(int key)
    {
        if (tiles.ContainsKey(key)) return;
        int columns = Columns, x = key % columns, y = key / columns;
        var rect = new SKRectI(x * TileSize, y * TileSize, Math.Min(Width, (x + 1) * TileSize), Math.Min(Height, (y + 1) * TileSize));
        var rectD = new RectD(rect.Left, rect.Top, rect.Width, rect.Height);
        var nextBounds = allocatedBounds is { } a ? a.Union(rectD) : (HasSource ? SourceRect.Union(rectD) : rectD);
        if (nextBounds.Width > 30_000 || nextBounds.Height > 30_000 || nextBounds.Width * nextBounds.Height > PixelLimit) throw ProjectException.TooLarge();
        allocatedBounds = nextBounds;
        var tile = new Tile { Rect = rect, Base = new byte[rect.Width * rect.Height * bpp] };
        FillFromSource(rect, tile.Base);
        tile.Pixels = (byte[])tile.Base.Clone();
        if (SelectionClip != null) tile.Selection = SelectionOnGrid(rect);
        tiles[key] = tile;
    }

    /// <summary>The original pixels (or mask values) that fall in a grid rectangle.</summary>
    private void FillFromSource(SKRectI rect, byte[] target)
    {
        int w = rect.Width;
        int sx0 = (int)SourceRect.X, sy0 = (int)SourceRect.Y;
        if (source != null)
        {
            var pixels = source.Pixels;
            int stride = source.RowBytes;
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                int sy = y - sy0;
                if (sy < 0 || sy >= source.Height) continue;
                int x0 = Math.Max(rect.Left, sx0), x1 = Math.Min(rect.Right, sx0 + source.Width);
                if (x1 <= x0) continue;
                pixels.Slice(sy * stride + (x0 - sx0) * 4, (x1 - x0) * 4).CopyTo(target.AsSpan(((y - rect.Top) * w + x0 - rect.Left) * 4));
            }
        }
        else if (sourceMask != null)
        {
            int mw = (int)SourceRect.Width, mh = (int)SourceRect.Height;
            bool sameSize = sourceMask.Width == mw && sourceMask.Height == mh;
            var pixels = sourceMask.Pixels;
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                int sy = y - sy0;
                if (sy < 0 || sy >= mh) continue;
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    int sx = x - sx0;
                    if (sx < 0 || sx >= mw) continue;
                    int mx = sameSize ? sx : Math.Min(sourceMask.Width - 1, (int)((sx + 0.5) * sourceMask.Width / mw));
                    int my = sameSize ? sy : Math.Min(sourceMask.Height - 1, (int)((sy + 0.5) * sourceMask.Height / mh));
                    target[(y - rect.Top) * w + x - rect.Left] = pixels[my * sourceMask.RowBytes + mx];
                }
            }
        }
    }

    private byte[] SelectionOnGrid(SKRectI rect)
    {
        var result = new byte[rect.Width * rect.Height];
        var clip = SelectionClip!;
        if (clip.Coverage == null) return result;
        var m = PixelToDocument;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                result[(y - rect.Top) * rect.Width + x - rect.Left] = clip.At(m.A * px + m.C * py + m.Tx, m.B * px + m.D * py + m.Ty);
            }
        return result;
    }

    private static readonly (double R, double G, double B) HealingWash = (0.12, 0.12, 0.12);

    private void Publish(HashSet<int> changed)
    {
        DirtyDocumentRect = null;
        foreach (var key in changed)
        {
            if (!tiles.TryGetValue(key, out var tile)) continue;
            if (tile.Coverage != null)
            {
                var full = new SKRectI(0, 0, tile.Rect.Width, tile.Rect.Height);
                var dirty = dirtyTiles.TryGetValue(key, out var d) ? d : full;
                dirtyTiles.Remove(key);
                dirty.Intersect(full);
                if (dirty.Width > 0 && dirty.Height > 0) Recompose(tile, dirty);
            }
            tile.HasImage = true;
            tile.Version++;
            var rect = new RectD(tile.Rect.Left, tile.Rect.Top, tile.Rect.Width, tile.Rect.Height).Apply(PixelToDocument).Intersect(Canvas);
            if (!rect.IsNull) DirtyDocumentRect = DirtyDocumentRect is { } r ? r.Union(rect) : rect;
        }
        Revision++;
    }

    /// <summary>Rebuilds part of a tile: original + colour × coverage × opacity (the stroke-wide opacity cap), or the
    /// clone sample, the healing wash, or erasing — through the selection.</summary>
    private void Recompose(Tile tile, SKRectI dirty)
    {
        int tw = tile.Rect.Width;
        var cov = tile.Coverage!;
        var sel = tile.Selection;
        bool clipped = SelectionClip != null;
        double opacity = Settings.Opacity;
        var m = PixelToDocument;
        byte colorR = To8(Settings.Red), colorG = To8(Settings.Green), colorB = To8(Settings.Blue);
        for (int y = dirty.Top; y < dirty.Bottom; y++)
        {
            for (int x = dirty.Left; x < dirty.Right; x++)
            {
                int i = y * tw + x;
                double c = cov[i] / 255.0;
                if (clipped) c *= (sel?[i] ?? 0) / 255.0;
                if (IsMask)
                {
                    byte baseV = tile.Base[i];
                    if (c <= 0) { tile.Pixels[i] = baseV; continue; }
                    double value;
                    if (Clone?.Mask is { } cloneMask && IsBlur)
                    {
                        var doc = Doc(m, tile.Rect.Left + x + 0.5, tile.Rect.Top + y + 0.5);
                        value = SampleGray(cloneMask, doc.X - Clone.Offset.Width, doc.Y - Clone.Offset.Height);
                        double a = c * opacity;
                        tile.Pixels[i] = (byte)Math.Round(baseV + (value - baseV) * a);
                    }
                    else
                    {
                        double a = c * opacity;
                        tile.Pixels[i] = (byte)Math.Round(baseV + (colorR - baseV) * a);
                    }
                    continue;
                }
                int p = i * 4;
                if (c <= 0)
                {
                    tile.Pixels[p] = tile.Base[p]; tile.Pixels[p + 1] = tile.Base[p + 1];
                    tile.Pixels[p + 2] = tile.Base[p + 2]; tile.Pixels[p + 3] = tile.Base[p + 3];
                    continue;
                }
                if (Clone?.Image is { } cloneImage)
                {
                    var doc = Doc(m, tile.Rect.Left + x + 0.5, tile.Rect.Top + y + 0.5);
                    var s = SampleRgba(cloneImage, doc.X - Clone.Offset.Width, doc.Y - Clone.Offset.Height);
                    if (ReplacesWithClone)
                    {
                        for (int k = 0; k < 4; k++) tile.Pixels[p + k] = (byte)Math.Round(tile.Base[p + k] + (s[k] - tile.Base[p + k]) * c);
                    }
                    else
                    {
                        double a = c * opacity;
                        double inv = 1 - s[3] / 255.0 * a;
                        for (int k = 0; k < 4; k++) tile.Pixels[p + k] = (byte)Math.Clamp(Math.Round(s[k] * a + tile.Base[p + k] * inv), 0, 255);
                    }
                }
                else if (Settings.Healing)
                {
                    double a = c * 0.45;
                    SrcOverColor(tile, p, To8(HealingWash.R), To8(HealingWash.G), To8(HealingWash.B), a);
                }
                else if (Settings.Erasing)
                {
                    double keep = 1 - c * opacity;
                    for (int k = 0; k < 4; k++) tile.Pixels[p + k] = (byte)Math.Round(tile.Base[p + k] * keep);
                }
                else SrcOverColor(tile, p, colorR, colorG, colorB, c * opacity);
            }
        }
    }

    private static PointD Doc(Affine m, double x, double y) => new(m.A * x + m.C * y + m.Tx, m.B * x + m.D * y + m.Ty);
    private static byte To8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);

    private static void SrcOverColor(Tile tile, int p, byte r, byte g, byte b, double a)
    {
        double inv = 1 - a;
        tile.Pixels[p] = (byte)Math.Round(r * a + tile.Base[p] * inv);
        tile.Pixels[p + 1] = (byte)Math.Round(g * a + tile.Base[p + 1] * inv);
        tile.Pixels[p + 2] = (byte)Math.Round(b * a + tile.Base[p + 2] * inv);
        tile.Pixels[p + 3] = (byte)Math.Round(255 * a + tile.Base[p + 3] * inv);
    }

    /// <summary>Bilinear premultiplied sample (transparent outside).</summary>
    private static double[] SampleRgba(RasterImage image, double x, double y)
    {
        var result = new double[4];
        double fx = x - 0.5, fy = y - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        var pixels = image.Pixels;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int sx = x0 + i, sy = y0 + j;
                if (sx < 0 || sy < 0 || sx >= image.Width || sy >= image.Height) continue;
                double w = (i == 1 ? tx : 1 - tx) * (j == 1 ? ty : 1 - ty);
                int p = sy * image.RowBytes + sx * 4;
                for (int k = 0; k < 4; k++) result[k] += pixels[p + k] * w;
            }
        return result;
    }

    private static double SampleGray(MaskImage image, double x, double y)
    {
        double fx = x - 0.5, fy = y - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0, sum = 0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int sx = Math.Clamp(x0 + i, 0, image.Width - 1), sy = Math.Clamp(y0 + j, 0, image.Height - 1);
                sum += image.ValueAt(sx, sy) * (i == 1 ? tx : 1 - tx) * (j == 1 ? ty : 1 - ty);
            }
        return sum;
    }

    // MARK: Whole-canvas edits

    /// <summary>Runs <paramref name="paint"/> for every covered grid pixel, starting from each tile's original content,
    /// clipped to the canvas and the selection (only the layer's existing pixels with <paramref name="withinSource"/>).</summary>
    private void PaintCanvas(bool withinSource, Action<Tile, int, PointD, double> paint)
    {
        var area = Canvas;
        if (SelectionClip != null) area = area.Intersect(SelectionClip.Rect);
        if (area.IsNull || area.IsEmpty) return;
        var affected = area.Apply(documentToPixel).Integral.Intersect(new RectD(0, 0, Width, Height));
        if (withinSource) affected = affected.Intersect(SourceRect);
        if (affected.IsNull || affected.IsEmpty) return;
        int columns = Columns;
        var m = PixelToDocument;
        for (int ty = (int)affected.MinY / TileSize; ty <= ((int)Math.Ceiling(affected.MaxY) - 1) / TileSize; ty++)
            for (int tx = (int)affected.MinX / TileSize; tx <= ((int)Math.Ceiling(affected.MaxX) - 1) / TileSize; tx++)
            {
                int key = ty * columns + tx;
                AllocateTile(key);
                var tile = tiles[key];
                Buffer.BlockCopy(tile.Base, 0, tile.Pixels, 0, tile.Base.Length);
                int tw = tile.Rect.Width;
                for (int y = 0; y < tile.Rect.Height; y++)
                    for (int x = 0; x < tw; x++)
                    {
                        int gx = tile.Rect.Left + x, gy = tile.Rect.Top + y;
                        if (withinSource && !SourceRect.Contains(new PointD(gx + 0.5, gy + 0.5))) continue;
                        var doc = Doc(m, gx + 0.5, gy + 0.5);
                        if (doc.X < 0 || doc.Y < 0 || doc.X >= Canvas.Width || doc.Y >= Canvas.Height) continue;
                        double coverage = 1;
                        if (SelectionClip != null) coverage = (tile.Selection?[y * tw + x] ?? 0) / 255.0;
                        if (coverage <= 0) continue;
                        paint(tile, y * tw + x, doc, coverage);
                    }
                tile.HasImage = true;
                tile.Version++;
            }
        DirtyDocumentRect = Canvas;
        Revision++;
    }

    /// <summary>Fills the selection (or the whole canvas) with a solid colour.</summary>
    public void Fill(PaletteColor color)
    {
        byte r = color.R8, g = color.G8, b = color.B8;
        PaintCanvas(false, (tile, i, _, coverage) =>
        {
            if (IsMask) { tile.Pixels[i] = (byte)Math.Round(tile.Base[i] + (r - tile.Base[i]) * coverage); return; }
            int p = i * 4;
            SrcOverColor(tile, p, r, g, b, coverage);
        });
    }

    /// <summary>Erases image pixels to transparency inside the selection, only where pixels exist.</summary>
    public void ClearPixels()
    {
        PaintCanvas(true, (tile, i, _, coverage) =>
        {
            int p = i * 4;
            for (int k = 0; k < 4; k++) tile.Pixels[p + k] = (byte)Math.Round(tile.Base[p + k] * (1 - coverage));
        });
    }

    /// <summary>A gradient over the whole canvas (or the selection) composited onto the original pixels.</summary>
    public void FillGradient(GradientShape shape, PointD start, PointD end, ColorStop first, ColorStop last, double opacity)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y, lengthSquared = dx * dx + dy * dy, radius = Math.Sqrt(lengthSquared);
        opacity = Math.Clamp(opacity, 0, 1);
        PaintCanvas(false, (tile, i, doc, coverage) =>
        {
            double t = shape == GradientShape.Linear
                ? (lengthSquared > 0 ? ((doc.X - start.X) * dx + (doc.Y - start.Y) * dy) / lengthSquared : 0)
                : (radius > 0 ? doc.DistanceTo(start) / radius : 0);
            t = Math.Clamp(t, 0, 1);
            double r = first.R + (last.R - first.R) * t, g = first.G + (last.G - first.G) * t, b = first.B + (last.B - first.B) * t;
            double a = (first.A + (last.A - first.A) * t) * opacity * coverage;
            if (IsMask) { tile.Pixels[i] = (byte)Math.Round(tile.Base[i] + (r * 255 - tile.Base[i]) * a); return; }
            SrcOverColor(tile, i * 4, To8(r), To8(g), To8(b), a);
        });
    }

    // MARK: Moving selected pixels

    private (byte[] Pixels, SKRectI Rect)? lifted;
    private readonly HashSet<int> moveTiles = new();

    /// <summary>Cuts the selected pixels out of the original image. False when nothing is lifted.</summary>
    public bool LiftSelection()
    {
        if (IsMask || source == null || SelectionClip?.Coverage == null) return false;
        var region = SelectionClip.Rect.Apply(documentToPixel).Integral.Intersect(SourceRect);
        if (region.IsNull || region.Width < 1 || region.Height < 1) return false;
        var rect = region.ToSKI();
        var pixels = new byte[rect.Width * rect.Height * 4];
        var m = PixelToDocument;
        var src = source.Pixels;
        int sx0 = (int)SourceRect.X, sy0 = (int)SourceRect.Y;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                var doc = Doc(m, x + 0.5, y + 0.5);
                double coverage = SelectionClip.At(doc.X, doc.Y) / 255.0;
                if (coverage <= 0) continue;
                int sp = (y - sy0) * source.RowBytes + (x - sx0) * 4, tp = ((y - rect.Top) * rect.Width + x - rect.Left) * 4;
                for (int k = 0; k < 4; k++) pixels[tp + k] = (byte)Math.Round(src[sp + k] * coverage);
            }
        lifted = (pixels, rect);
        return true;
    }

    /// <summary>Rebuilds the affected tiles: the selection becomes a transparent hole (unless duplicating) and the lifted
    /// pixels are placed <paramref name="offset"/> document pixels away.</summary>
    public void MoveLifted(SizeD offset, bool duplicate = false)
    {
        if (lifted is not { } lift || SelectionClip == null) return;
        var zero = documentToPixel.Apply(PointD.Zero);
        var moved = documentToPixel.Apply(new PointD(offset.Width, offset.Height));
        double ox = moved.X - zero.X, oy = moved.Y - zero.Y;
        var target = new RectD(lift.Rect.Left + ox, lift.Rect.Top + oy, lift.Rect.Width, lift.Rect.Height);
        bool whole = target.X == Math.Round(target.X) && target.Y == Math.Round(target.Y);
        var needed = new RectD(lift.Rect.Left, lift.Rect.Top, lift.Rect.Width, lift.Rect.Height).Union(target).Integral
            .Intersect(new RectD(0, 0, Width, Height));
        var keys = new HashSet<int>(moveTiles);
        if (!needed.IsNull && !needed.IsEmpty)
        {
            int columns = Columns;
            for (int ty = (int)needed.MinY / TileSize; ty <= ((int)Math.Ceiling(needed.MaxY) - 1) / TileSize; ty++)
                for (int tx = (int)needed.MinX / TileSize; tx <= ((int)Math.Ceiling(needed.MaxX) - 1) / TileSize; tx++)
                {
                    int key = ty * columns + tx;
                    AllocateTile(key);
                    keys.Add(key);
                }
        }
        var m = PixelToDocument;
        Span<double> s = stackalloc double[4];
        foreach (var key in keys)
        {
            var tile = tiles[key];
            Buffer.BlockCopy(tile.Base, 0, tile.Pixels, 0, tile.Base.Length);
            int tw = tile.Rect.Width;
            for (int y = 0; y < tile.Rect.Height; y++)
                for (int x = 0; x < tw; x++)
                {
                    int gx = tile.Rect.Left + x, gy = tile.Rect.Top + y, p = (y * tw + x) * 4;
                    if (!duplicate)
                    {
                        double hole = (tile.Selection?[y * tw + x] ?? 0) / 255.0;
                        if (hole > 0) for (int k = 0; k < 4; k++) tile.Pixels[p + k] = (byte)Math.Round(tile.Pixels[p + k] * (1 - hole));
                    }
                    // Lifted pixel under this grid pixel (nearest for whole-pixel moves, bilinear otherwise).
                    double lx = gx + 0.5 - target.X, ly = gy + 0.5 - target.Y;
                    if (lx < 0 || ly < 0 || lx >= lift.Rect.Width || ly >= lift.Rect.Height) continue;
                    if (whole)
                    {
                        int q = ((int)ly * lift.Rect.Width + (int)lx) * 4;
                        for (int k = 0; k < 4; k++) s[k] = lift.Pixels[q + k];
                    }
                    else BilinearLifted(lift.Pixels, lift.Rect.Width, lift.Rect.Height, lx, ly, s);
                    double inv = 1 - s[3] / 255.0;
                    for (int k = 0; k < 4; k++) tile.Pixels[p + k] = (byte)Math.Clamp(Math.Round(s[k] + tile.Pixels[p + k] * inv), 0, 255);
                }
            tile.HasImage = true;
            tile.Version++;
        }
        moveTiles.Clear();
        moveTiles.UnionWith(keys);
        DirtyDocumentRect = Canvas;
        Revision++;
        _ = m;
    }

    private static void BilinearLifted(byte[] pixels, int w, int h, double x, double y, Span<double> result)
    {
        result.Clear();
        double fx = x - 0.5, fy = y - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int sx = x0 + i, sy = y0 + j;
                if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                double wgt = (i == 1 ? tx : 1 - tx) * (j == 1 ? ty : 1 - ty);
                int p = (sy * w + sx) * 4;
                for (int k = 0; k < 4; k++) result[k] += pixels[p + k] * wgt;
            }
    }

    // MARK: Spot healing

    /// <summary>Once the stroke ends: rebuilds the painted area from nearby texture and writes it into the tiles.</summary>
    public void Heal()
    {
        if (!Settings.Healing || IsMask) return;
        RectD? painted = null;
        foreach (var tile in tiles.Values)
        {
            if (tile.Coverage == null) continue;
            var (x0, y0, x1, y1) = PixelOps.CoverageBounds(tile.Coverage, tile.Rect.Width, tile.Rect.Height, tile.Rect.Width);
            if (x1 <= x0 || y1 <= y0) continue;
            var rect = new RectD(x0 + tile.Rect.Left, y0 + tile.Rect.Top, x1 - x0, y1 - y0);
            painted = painted is { } p ? p.Union(rect) : rect;
        }
        if (painted is not { } spot) return;
        double reach = (Math.Max(spot.Width, spot.Height) + 32) * 3.2;
        var region = spot.Inset(-reach, -reach).Intersect(new RectD(0, 0, Width, Height)).Integral;
        int w = (int)region.Width, h = (int)region.Height, rx = (int)region.X, ry = (int)region.Y;
        var pixels = new byte[w * h * 4];
        var placed = new SKRectI(rx, ry, rx + w, ry + h);
        FillFromSource(placed, pixels);
        var coverage = new byte[w * h];
        foreach (var tile in tiles.Values)
        {
            if (tile.Coverage == null) continue;
            for (int y = 0; y < tile.Rect.Height; y++)
            {
                int gy = tile.Rect.Top + y - ry;
                if (gy < 0 || gy >= h) continue;
                for (int x = 0; x < tile.Rect.Width; x++)
                {
                    int gx = tile.Rect.Left + x - rx;
                    if (gx < 0 || gx >= w) continue;
                    coverage[gy * w + gx] = tile.Coverage[y * tile.Rect.Width + x];
                }
            }
        }
        Kernels.SpotHeal(pixels, coverage, w, h, w * 4, (float)Settings.Opacity, (int)Settings.HealingMode, (uint)Random.Shared.NextInt64(0, uint.MaxValue));
        foreach (var tile in tiles.Values)
        {
            if (tile.Coverage == null) continue;
            int tw = tile.Rect.Width;
            Buffer.BlockCopy(tile.Base, 0, tile.Pixels, 0, tile.Base.Length);
            for (int y = 0; y < tile.Rect.Height; y++)
            {
                int gy = tile.Rect.Top + y - ry;
                if (gy < 0 || gy >= h) continue;
                for (int x = 0; x < tw; x++)
                {
                    int gx = tile.Rect.Left + x - rx;
                    if (gx < 0 || gx >= w) continue;
                    int p = (y * tw + x) * 4, q = (gy * w + gx) * 4;
                    double sel = SelectionClip == null ? 1 : (tile.Selection?[y * tw + x] ?? 0) / 255.0;
                    for (int k = 0; k < 4; k++)
                        tile.Pixels[p + k] = (byte)Math.Round(tile.Base[p + k] + (pixels[q + k] - tile.Base[p + k]) * sel);
                }
            }
            tile.HasImage = true;
            tile.Version++;
        }
        Revision++;
    }

    // MARK: Committing

    public RectD CommittedBounds => (allocatedBounds ?? SourceRect).Integral;
    public LayerTransform CommittedTransform => TransformFor(CommittedBounds);

    public LayerTransform TransformFor(RectD bounds)
    {
        var center = PixelToDocument.Apply(new PointD(bounds.MidX, bounds.MidY));
        var size = new SizeD(bounds.Width * PaintTransform.Size.Width / Width, bounds.Height * PaintTransform.Size.Height / Height);
        return PaintTransform with { Size = size, Origin = new PointD(center.X - size.Width / 2, center.Y - size.Height / 2) };
    }

    /// <summary>The assembled grid content inside <paramref name="crop"/> (grid pixels).</summary>
    private byte[] Assemble(SKRectI crop)
    {
        var result = new byte[crop.Width * crop.Height * bpp];
        FillFromSource(crop, result);
        foreach (var tile in tiles.Values)
        {
            if (!tile.HasImage) continue;
            var overlap = SKRectI.Intersect(tile.Rect, crop);
            if (overlap.Width <= 0 || overlap.Height <= 0) continue;
            for (int y = overlap.Top; y < overlap.Bottom; y++)
                Buffer.BlockCopy(tile.Pixels, ((y - tile.Rect.Top) * tile.Rect.Width + overlap.Left - tile.Rect.Left) * bpp,
                    result, ((y - crop.Top) * crop.Width + overlap.Left - crop.Left) * bpp, overlap.Width * bpp);
        }
        return result;
    }

    /// <summary>Brush strokes: painting only adds alpha, so the kept bounds are the original bounds plus the painted tiles.</summary>
    public (ImageAsset? Asset, MaskAsset? Mask, LayerTransform Transform, RectD Bounds) PaintSnapshot()
    {
        RectD? bounds = HasSource ? SourceRect : null;
        if (!IsMask)
            foreach (var tile in tiles.Values)
            {
                var (l, t, r, b) = PixelOps.AlphaBounds(tile.Pixels, tile.Rect.Width, tile.Rect.Height, tile.Rect.Width * 4);
                if (r <= l || b <= t) continue;
                var rect = new RectD(l + tile.Rect.Left, t + tile.Rect.Top, r - l, b - t);
                bounds = bounds is { } existing ? existing.Union(rect) : rect;
            }
        var crop = (bounds ?? CommittedBounds).Integral;
        var cropI = crop.ToSKI();
        var pixels = Assemble(cropI);
        if (IsMask)
        {
            var mask = MaskImage.FromPixels(cropI.Width, cropI.Height, pixels);
            return (null, PixelOps.MaskAssetFrom(mask), TransformFor(crop), crop);
        }
        var image = RasterImage.FromPixels(cropI.Width, cropI.Height, pixels);
        return (PixelOps.Asset(image, Layer.Name), null, TransformFor(crop), crop);
    }

    /// <summary>Fills, gradients, clears and moves: the whole allocated area, cropped to what has alpha.</summary>
    public (ImageAsset? Asset, MaskAsset? Mask, RectD PixelBounds) CommitRender()
    {
        var bounds = CommittedBounds;
        var boundsI = bounds.ToSKI();
        var pixels = Assemble(boundsI);
        if (IsMask) return (null, PixelOps.MaskAssetFrom(MaskImage.FromPixels(boundsI.Width, boundsI.Height, pixels)), bounds);
        var (l, t, r, b) = PixelOps.AlphaBounds(pixels, boundsI.Width, boundsI.Height, boundsI.Width * 4);
        var crop = r <= l ? new SKRectI(0, 0, boundsI.Width, boundsI.Height) : new SKRectI(l, t, r, b);
        var full = RasterImage.FromPixels(boundsI.Width, boundsI.Height, pixels);
        var image = crop.Width == boundsI.Width && crop.Height == boundsI.Height ? full : full.Crop(crop);
        return (PixelOps.Asset(image, Layer.Name), null, new RectD(crop.Left + bounds.X, crop.Top + bounds.Y, crop.Width, crop.Height));
    }

    /// <summary>A layer mask grown with its layer: new area revealed, existing coverage aligned.</summary>
    public MaskAsset ExpandMask(MaskAsset asset, RectD crop)
    {
        if (SourceRect == crop) return asset;
        var cropI = crop.ToSKI();
        var bytes = new byte[cropI.Width * cropI.Height];
        Array.Fill(bytes, (byte)255);
        int ox = (int)(SourceRect.X - crop.X), oy = (int)(SourceRect.Y - crop.Y);
        int sw = (int)SourceRect.Width, sh = (int)SourceRect.Height;
        var mask = asset.Image;
        bool same = mask.Width == sw && mask.Height == sh;
        for (int y = 0; y < sh; y++)
        {
            int ty = y + oy;
            if (ty < 0 || ty >= cropI.Height) continue;
            for (int x = 0; x < sw; x++)
            {
                int tx = x + ox;
                if (tx < 0 || tx >= cropI.Width) continue;
                int mx = same ? x : Math.Min(mask.Width - 1, (int)((x + 0.5) * mask.Width / sw));
                int my = same ? y : Math.Min(mask.Height - 1, (int)((y + 0.5) * mask.Height / sh));
                bytes[ty * cropI.Width + tx] = mask.ValueAt(mx, my);
            }
        }
        return PixelOps.MaskAssetFrom(MaskImage.FromPixels(cropI.Width, cropI.Height, bytes));
    }

    // MARK: Preview

    /// <summary>A tile's current pixels as an image for drawing (cached until the tile changes).</summary>
    public SKImage TileImage(Tile tile)
    {
        if (tile.preview != null && tile.previewVersion == tile.Version) return tile.preview;
        tile.preview?.Dispose();
        var info = IsMask
            ? new SKImageInfo(tile.Rect.Width, tile.Rect.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            : new SKImageInfo(tile.Rect.Width, tile.Rect.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        tile.preview = SKImage.FromPixelCopy(info, tile.Pixels, tile.Rect.Width * bpp);
        tile.previewVersion = tile.Version;
        return tile.preview;
    }

    public RasterImage? SourceImage => source;
    public MaskImage? SourceMaskImage => sourceMask;
}
