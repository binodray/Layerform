using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>Placing masks that were moved apart from their layers (LayerMask.swift).</summary>
public static class MaskPlacement
{
    /// <summary>White or black, whichever most of a mask's edge is (read from its small thumbnail).</summary>
    public static byte Background(MaskImage thumbnail)
    {
        int width = thumbnail.Width, height = thumbnail.Height;
        if (width <= 0 || height <= 0) return 255;
        long total = 0, count = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (y != 0 && y != height - 1 && x != 0 && x != width - 1) continue;
                total += thumbnail.ValueAt(x, y);
                count++;
            }
        return total * 2 >= count * 255 ? (byte)255 : (byte)0;
    }

    /// <summary>The mean gray (0–1) of an image's outermost pixels (CanvasThumbnail.edgeTone).</summary>
    public static double EdgeTone(MaskImage image)
    {
        long total = 0, count = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (y == 0 || y == image.Height - 1 || x == 0 || x == image.Width - 1) { total += image.ValueAt(x, y); count++; }
        return count == 0 ? 1 : total / (double)count / 255;
    }

    /// <summary>
    /// A width × height gray grid stretched over a layer at <paramref name="layer"/>, holding the mask drawn where
    /// <paramref name="placement"/> puts it, <paramref name="background"/> elsewhere.
    /// </summary>
    public static MaskImage Placed(int width, int height, LayerTransform layer, LayerTransform placement, MaskImage mask, byte background)
    {
        using var alpha = PixelOps.NewAlpha(width, height, background);
        using (var canvas = new SKCanvas(alpha))
        {
            var toGrid = LayerTransform.PixelToDocument(placement, mask.Width, mask.Height)
                .Concat(LayerTransform.PixelToDocument(layer, width, height).Inverted());
            canvas.SetMatrix(toGrid.ToSK());
            // Src replaces the background with the mask's values, blending only along antialiased edges.
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
            double factor = placement.Size.Width / Math.Max(1, layer.Size.Width) * width / Math.Max(1, mask.Width);
            var (source, level) = DownsampleCache.Mask(mask, DownsampleCache.Level(factor));
            canvas.Scale(1 << level, 1 << level);
            canvas.DrawImage(source.AlphaImage, 0, 0, new SKSamplingOptions(LayerRenderer.HighQuality), paint);
        }
        return PixelOps.AlphaToMaskCopy(alpha);
    }

    private sealed record CacheKey(MaskImage Mask, LayerTransform Placement, LayerTransform Layer, int Width, int Height)
    {
        public bool Equals(CacheKey? other) => other is not null && ReferenceEquals(Mask, other.Mask) && Placement == other.Placement
            && Layer == other.Layer && Width == other.Width && Height == other.Height;
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Mask), Placement, Layer, Width, Height);
    }
    private static readonly LinkedList<(CacheKey Key, MaskImage Image)> cache = new();
    private static readonly object gate = new();

    /// <summary>
    /// The mask as a layer's renderers take it: stretched over the layer's width × height pixel grid at
    /// <paramref name="layer"/>. Covering the layer (no placement) that is the mask itself; placed apart, it is
    /// resampled into that grid (at most <paramref name="limit"/> pixels across) and cached. Null while disabled.
    /// </summary>
    public static MaskImage? ClipImage(LayerMask mask, LayerTransform? placement, LayerTransform layer, int width, int height, double? limit = null)
    {
        var image = mask.EnabledImage;
        if (image == null) return null;
        if (placement is not { } placed || placed.SamePlacement(layer) || width <= 0 || height <= 0) return image;
        double factor = limit is { } l ? Math.Min(1, Math.Max(1, l) / Math.Max(width, height)) : 1;
        int w = Math.Max(1, (int)Math.Ceiling(width * factor)), h = Math.Max(1, (int)Math.Ceiling(height * factor));
        var key = new CacheKey(image, placed, layer, w, h);
        lock (gate)
        {
            for (var node = cache.First; node != null; node = node.Next)
                if (node.Value.Key.Equals(key))
                {
                    cache.Remove(node);
                    cache.AddFirst(node);
                    return node.Value.Image;
                }
        }
        var result = Placed(w, h, layer, placed, image, Background(mask.Asset.Thumbnail));
        if ((long)w * h > 64_000_000) return result;
        lock (gate)
        {
            cache.AddFirst((key, result));
            long pixels = cache.Sum(e => (long)e.Key.Width * e.Key.Height);
            while (cache.Count > 8 || pixels > 64_000_000)
            {
                var last = cache.Last!;
                pixels -= (long)last.Value.Key.Width * last.Value.Key.Height;
                cache.RemoveLast();
            }
        }
        return result;
    }
}
