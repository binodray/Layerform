using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace Compositor.App.Controls;

/// <summary>Layers-panel thumbnails framed by the whole canvas, as Photoshop shows them (CanvasThumbnail.swift).</summary>
public static class Thumbnails
{
    private static readonly Dictionary<(object?, LayerTransform, SizeD, int, bool), WriteableBitmap> cache = new();

    public static SizeD FittedSize(SizeD canvas, double box)
    {
        if (!(canvas.Width > 0) || !(canvas.Height > 0)) return new SizeD(box, box);
        double scale = box / Math.Max(canvas.Width, canvas.Height);
        return new SizeD(Math.Max(1, Math.Round(canvas.Width * scale)), Math.Max(1, Math.Round(canvas.Height * scale)));
    }

    public static WriteableBitmap Layer(RasterImage? thumbnail, LayerTransform transform, SizeD canvas, int box, double scale)
    {
        var key = ((object?)thumbnail, transform, canvas, box, false);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var bitmap = Render(canvas, box, scale, (surface, size, perPixel) =>
        {
            var c = surface.Canvas;
            c.Clear(new SKColor(56, 56, 56));
            float tile = (float)(6 * scale);
            using var paint = new SKPaint { Color = new SKColor(82, 82, 82) };
            for (int row = 0; row < Math.Ceiling(size.Height / tile); row++)
                for (int column = 0; column < Math.Ceiling(size.Width / tile); column++)
                    if ((row + column) % 2 == 0) c.DrawRect(column * tile, row * tile, tile, tile, paint);
            if (thumbnail != null) LayerRenderer.Draw(surface, thumbnail, transform);
        });
        Remember(key, bitmap);
        return bitmap;
    }

    public static WriteableBitmap Mask(MaskImage thumbnail, LayerTransform transform, SizeD canvas, int box, double scale)
    {
        var key = ((object?)thumbnail, transform, canvas, box, true);
        if (cache.TryGetValue(key, out var hit)) return hit;
        byte tone = (byte)Math.Round(MaskPlacement.EdgeTone(thumbnail) * 255);
        var bitmap = Render(canvas, box, scale, (surface, _, _) =>
        {
            surface.Canvas.Clear(new SKColor(tone, tone, tone));
            LayerRenderer.DrawCoverage(surface, thumbnail, transform);
        });
        Remember(key, bitmap);
        return bitmap;
    }

    private static void Remember((object?, LayerTransform, SizeD, int, bool) key, WriteableBitmap bitmap)
    {
        if (cache.Count > 400) cache.Clear();
        cache[key] = bitmap;
    }

    private static WriteableBitmap Render(SizeD canvas, int box, double scale, Action<RenderSurface, SizeD, double> draw)
    {
        var points = FittedSize(canvas, box);
        int width = Math.Max(1, (int)Math.Round(points.Width * scale)), height = Math.Max(1, (int)Math.Round(points.Height * scale));
        double perPixel = canvas.Width > 0 ? width / canvas.Width : 1;
        using var bitmap = PixelOps.NewRgba(width, height);
        using (var surface = new RenderSurface(bitmap, Affine.Scale(perPixel, perPixel))) draw(surface, new SizeD(width, height), perPixel);
        return ToWriteable(bitmap);
    }

    /// <summary>RGBA premultiplied into a WriteableBitmap (BGRA premultiplied).</summary>
    public static WriteableBitmap ToWriteable(SKBitmap rgba)
    {
        var result = new WriteableBitmap(rgba.Width, rgba.Height);
        var source = rgba.GetPixelSpan();
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        for (int y = 0; y < rgba.Height; y++)
            for (int x = 0; x < rgba.Width; x++)
            {
                int s = y * rgba.RowBytes + x * 4, t = (y * rgba.Width + x) * 4;
                bytes[t] = source[s + 2]; bytes[t + 1] = source[s + 1]; bytes[t + 2] = source[s]; bytes[t + 3] = source[s + 3];
            }
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(result.PixelBuffer))
            stream.Write(bytes, 0, bytes.Length);
        result.Invalidate();
        return result;
    }
}
