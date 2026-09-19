using System.Runtime.CompilerServices;
using Compositor.Imaging;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>
/// Sharp reductions of layer images: a chain of exactly-halved copies, so a draw only ever resamples the last 2× or less
/// (DownsampleCache.swift). Level k pixel i covers source pixels i·2^k ..&lt; (i+1)·2^k, rounding up at the far edges.
/// Copies are keyed by image identity and the least recently used are dropped beyond a pixel budget.
/// </summary>
public static class DownsampleCache
{
    public const int PixelBudget = 100_000_000;
    public const int MaxLevel = 6;

    private sealed class Entry
    {
        public readonly List<object> Levels = new();
        public long LastUse;
        public long Pixels;
    }

    private static readonly ConditionalWeakTable<object, Entry> table = new();
    private static readonly LinkedList<WeakReference<object>> order = new();
    private static readonly object gate = new();
    private static long clock;
    private static long total;

    /// <summary>Halvings to draw from when an image lands <paramref name="factor"/> output pixels per image pixel.</summary>
    public static int Level(double factor)
    {
        if (!double.IsFinite(factor) || !(factor > 0) || factor >= 0.5) return 0;
        return Math.Min(MaxLevel, (int)Math.Floor(Math.Log2(1 / factor)));
    }

    public static (RasterImage Image, int Level) Image(RasterImage image, int wanted)
    {
        var (result, level) = Get(image, wanted, o => Halve((RasterImage)o), o => (long)((RasterImage)o).Width * ((RasterImage)o).Height);
        return ((RasterImage)result, level);
    }

    public static (MaskImage Image, int Level) Mask(MaskImage image, int wanted)
    {
        var (result, level) = Get(image, wanted, o => Halve((MaskImage)o), o => (long)((MaskImage)o).Width * ((MaskImage)o).Height);
        return ((MaskImage)result, level);
    }

    private static (object, int) Get(object image, int wanted, Func<object, object?> halve, Func<object, long> pixels)
    {
        int width = image is RasterImage r ? r.Width : ((MaskImage)image).Width;
        int height = image is RasterImage r2 ? r2.Height : ((MaskImage)image).Height;
        if (wanted < 1 || (width <= 1 && height <= 1)) return (image, 0);
        Entry entry;
        lock (gate)
        {
            clock++;
            if (!table.TryGetValue(image, out entry!))
            {
                entry = new Entry();
                table.Add(image, entry);
                order.AddLast(new WeakReference<object>(image));
            }
            entry.LastUse = clock;
            if (entry.Levels.Count >= wanted) return (entry.Levels[wanted - 1], wanted);
        }
        var levels = new List<object>();
        lock (gate) levels.AddRange(entry.Levels);
        while (levels.Count < wanted)
        {
            var previous = levels.Count > 0 ? levels[^1] : image;
            int pw = previous is RasterImage pr ? pr.Width : ((MaskImage)previous).Width;
            int ph = previous is RasterImage pr2 ? pr2.Height : ((MaskImage)previous).Height;
            if (pw <= 1 && ph <= 1) break;
            var next = halve(previous);
            if (next == null) break;
            levels.Add(next);
        }
        if (levels.Count == 0) return (image, 0);
        lock (gate)
        {
            if (levels.Count > entry.Levels.Count)
            {
                total -= entry.Pixels;
                entry.Levels.Clear();
                entry.Levels.AddRange(levels);
                entry.Pixels = levels.Sum(pixels);
                total += entry.Pixels;
            }
            Evict(image);
        }
        int applied = Math.Min(wanted, levels.Count);
        return (levels[applied - 1], applied);
    }

    private static void Evict(object keep)
    {
        if (total <= PixelBudget) return;
        var candidates = new List<(object Key, Entry Entry, LinkedListNode<WeakReference<object>> Node)>();
        for (var node = order.First; node != null;)
        {
            var next = node.Next;
            if (!node.Value.TryGetTarget(out var key) || !table.TryGetValue(key, out var e)) order.Remove(node);
            else if (!ReferenceEquals(key, keep)) candidates.Add((key, e, node));
            node = next;
        }
        foreach (var (key, e, node) in candidates.OrderBy(c => c.Entry.LastUse))
        {
            if (total <= PixelBudget) break;
            total -= e.Pixels;
            table.Remove(key);
            order.Remove(node);
        }
    }

    // Tent weights for an exact 2× reduction: taps at −1, 0, +1, +2 around each pair.
    private static readonly float[] Taps = { 1 / 8f, 3 / 8f, 3 / 8f, 1 / 8f };

    /// <summary>Exactly half the size, rounded up. Colour images fade to transparent past their edges; masks repeat the
    /// last row and column.</summary>
    public static RasterImage? Halve(RasterImage image)
    {
        int width = (image.Width + 1) / 2, height = (image.Height + 1) / 2;
        var bitmap = PixelOps.NewRgba(width, height, false);
        var source = image.Pixels;
        int sw = image.Width, sh = image.Height, stride = image.RowBytes;
        var target = bitmap.GetPixelSpan();
        int tstride = bitmap.RowBytes;
        // Horizontal pass into floats, then vertical.
        var horizontal = new float[width * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            var row = source.Slice(y * stride, sw * 4);
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int t = 0; t < 4; t++)
                {
                    int sx = 2 * x - 1 + t;
                    if (sx < 0 || sx >= sw) continue;
                    float w = Taps[t];
                    int i = sx * 4;
                    r += row[i] * w; g += row[i + 1] * w; b += row[i + 2] * w; a += row[i + 3] * w;
                }
                int o = (y * width + x) * 4;
                horizontal[o] = r; horizontal[o + 1] = g; horizontal[o + 2] = b; horizontal[o + 3] = a;
            }
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int t = 0; t < 4; t++)
                {
                    int sy = 2 * y - 1 + t;
                    if (sy < 0 || sy >= sh) continue;
                    float w = Taps[t];
                    int i = (sy * width + x) * 4;
                    r += horizontal[i] * w; g += horizontal[i + 1] * w; b += horizontal[i + 2] * w; a += horizontal[i + 3] * w;
                }
                int o = y * tstride + x * 4;
                byte alpha = (byte)Math.Clamp((int)(a + 0.5f), 0, 255);
                target[o + 3] = alpha;
                target[o] = (byte)Math.Min(alpha, Math.Clamp((int)(r + 0.5f), 0, 255));
                target[o + 1] = (byte)Math.Min(alpha, Math.Clamp((int)(g + 0.5f), 0, 255));
                target[o + 2] = (byte)Math.Min(alpha, Math.Clamp((int)(b + 0.5f), 0, 255));
            }
        }
        return RasterImage.Adopt(bitmap);
    }

    public static MaskImage? Halve(MaskImage image)
    {
        int width = (image.Width + 1) / 2, height = (image.Height + 1) / 2;
        var bitmap = PixelOps.NewGray(width, height);
        var source = image.Pixels;
        int sw = image.Width, sh = image.Height, stride = image.RowBytes;
        var target = bitmap.GetPixelSpan();
        int tstride = bitmap.RowBytes;
        var horizontal = new float[width * sh];
        for (int y = 0; y < sh; y++)
        {
            var row = source.Slice(y * stride, sw);
            for (int x = 0; x < width; x++)
            {
                float v = 0;
                for (int t = 0; t < 4; t++) v += row[Math.Clamp(2 * x - 1 + t, 0, sw - 1)] * Taps[t];
                horizontal[y * width + x] = v;
            }
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float v = 0;
                for (int t = 0; t < 4; t++) v += horizontal[Math.Clamp(2 * y - 1 + t, 0, sh - 1) * width + x] * Taps[t];
                target[y * tstride + x] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
            }
        return MaskImage.Adopt(bitmap);
    }
}
