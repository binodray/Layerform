using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Compositor.Imaging;

/// <summary>Ports of BrushPixels.c and AdjustPixels.c helpers over RGBA premultiplied rows.</summary>
public static class PixelOps
{
    /// <summary>Half-open bounds of nonzero alpha; all zero when empty (brush_alpha_bounds).</summary>
    public static (int Left, int Top, int Right, int Bottom) AlphaBounds(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        int left = width, right = 0, top = height, bottom = 0;
        for (int y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * stride, width * 4);
            int first = 0;
            while (first < width && row[first * 4 + 3] == 0) first++;
            if (first == width) continue;
            int last = width;
            while (last > first && row[(last - 1) * 4 + 3] == 0) last--;
            if (first < left) left = first;
            if (last > right) right = last;
            if (y < top) top = y;
            bottom = y + 1;
        }
        return right == 0 ? (0, 0, 0, 0) : (left, top, right, bottom);
    }

    /// <summary>Half-open bounds of nonzero gray bytes; all zero when empty (heal_coverage_bounds).</summary>
    public static (int Left, int Top, int Right, int Bottom) CoverageBounds(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        int x0 = width, y0 = height, x1 = 0, y1 = 0;
        for (int y = 0; y < height; y++)
        {
            var row = gray.Slice(y * stride, width);
            for (int x = 0; x < width; x++)
            {
                if (row[x] == 0) continue;
                if (x < x0) x0 = x;
                if (x + 1 > x1) x1 = x + 1;
                if (y < y0) y0 = y;
                if (y + 1 > y1) y1 = y + 1;
            }
        }
        return x1 <= x0 || y1 <= y0 ? (0, 0, 0, 0) : (x0, y0, x1, y1);
    }

    public static void ExtractAlpha(ReadOnlySpan<byte> rgba, int rgbaStride, Span<byte> gray, int grayStride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * rgbaStride);
            var target = gray.Slice(y * grayStride);
            for (int x = 0; x < width; x++) target[x] = row[x * 4 + 3];
        }
    }

    public static void UnpremultiplyOpaque(Span<byte> rgba, int stride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            var p = rgba.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                uint a = p[i + 3];
                for (int c = 0; c < 3; c++)
                {
                    uint v = a != 0 ? (p[i + c] * 255u + a / 2) / a : 0;
                    p[i + c] = (byte)(v > 255 ? 255 : v);
                }
                p[i + 3] = 255;
            }
        }
    }

    public static void RestoreAlpha(Span<byte> rgba, int stride, ReadOnlySpan<byte> alpha, int alphaStride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            var p = rgba.Slice(y * stride, width * 4);
            var arow = alpha.Slice(y * alphaStride);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                uint a = arow[x];
                for (int c = 0; c < 3; c++) p[i + c] = (byte)((p[i + c] * a + 127) / 255);
                p[i + 3] = (byte)a;
            }
        }
    }

    public static void ClampPremultiplied(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte a = rgba[i + 3];
            if (rgba[i] > a) rgba[i] = a;
            if (rgba[i + 1] > a) rgba[i + 1] = a;
            if (rgba[i + 2] > a) rgba[i + 2] = a;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Premultiply(byte value, byte alpha) => (byte)((value * alpha + 127) / 255);

    /// <summary>A new RGBA bitmap with <paramref name="image"/> drawn into it unchanged.</summary>
    public static SKBitmap Copy(RasterImage image) => image.CopyBitmap();

    public static SKBitmap NewRgba(int width, int height, bool clear = true)
    {
        var bitmap = new SKBitmap(RasterImage.Info(width, height));
        if (clear) bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    public static SKBitmap NewAlpha(int width, int height, byte fill = 0)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        bitmap.GetPixelSpan().Fill(fill);
        return bitmap;
    }

    public static SKBitmap NewGray(int width, int height, byte fill = 0)
    {
        var bitmap = new SKBitmap(MaskImage.Info(width, height));
        bitmap.GetPixelSpan().Fill(fill);
        return bitmap;
    }

    /// <summary>A 96-pixel-max thumbnail (the Layers panel preview), smoothly reduced.</summary>
    public static RasterImage Thumbnail(RasterImage image)
    {
        double factor = Math.Min(1, 96.0 / Math.Max(image.Width, image.Height));
        int width = Math.Max(1, (int)(image.Width * factor)), height = Math.Max(1, (int)(image.Height * factor));
        if (width == image.Width && height == image.Height) return image;
        var bitmap = NewRgba(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(image.Image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        return RasterImage.Adopt(bitmap);
    }

    public static MaskImage Thumbnail(MaskImage image)
    {
        double factor = Math.Min(1, 96.0 / Math.Max(image.Width, image.Height));
        int width = Math.Max(1, (int)(image.Width * factor)), height = Math.Max(1, (int)(image.Height * factor));
        if (width == image.Width && height == image.Height) return image;
        var alpha = NewAlpha(width, height);
        using (var canvas = new SKCanvas(alpha))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawImage(image.AlphaImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        return MaskImage.Adopt(alpha);
    }

    public static ImageAsset Asset(RasterImage image, string name) => new(image, Thumbnail(image), name);
    public static MaskAsset MaskAssetFrom(MaskImage image) => new(image, Thumbnail(image));

    /// <summary>The red channel of an opaque RGBA bitmap as gray (masks rendered in RGBA for gray-value blending).</summary>
    public static MaskImage GrayFromRed(SKBitmap rgba)
    {
        var gray = NewGray(rgba.Width, rgba.Height);
        var source = rgba.GetPixelSpan();
        var target = gray.GetPixelSpan();
        int w = rgba.Width, h = rgba.Height, stride = rgba.RowBytes, gstride = gray.RowBytes;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) target[y * gstride + x] = source[y * stride + x * 4];
        return MaskImage.Adopt(gray);
    }

    /// <summary>An opaque RGBA bitmap holding the mask's gray values in every channel.</summary>
    public static SKBitmap RgbaFromGray(MaskImage mask)
    {
        var bitmap = NewRgba(mask.Width, mask.Height, false);
        var target = bitmap.GetPixelSpan();
        var source = mask.Pixels;
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                byte v = source[y * mask.RowBytes + x];
                int i = y * bitmap.RowBytes + x * 4;
                target[i] = v; target[i + 1] = v; target[i + 2] = v; target[i + 3] = 255;
            }
        return bitmap;
    }

    /// <summary>An alpha-8 (or RGBA, by alpha) bitmap's coverage copied into a new mask; the bitmap is left alone.</summary>
    public static MaskImage AlphaToMaskCopy(SKBitmap source)
    {
        var gray = NewGray(source.Width, source.Height);
        var target = gray.GetPixelSpan();
        var pixels = source.GetPixelSpan();
        if (source.ColorType == SKColorType.Alpha8 || source.ColorType == SKColorType.Gray8)
        {
            for (int y = 0; y < source.Height; y++)
                pixels.Slice(y * source.RowBytes, source.Width).CopyTo(target.Slice(y * gray.RowBytes, source.Width));
        }
        else ExtractAlpha(pixels, source.RowBytes, target, gray.RowBytes, source.Width, source.Height);
        return MaskImage.Adopt(gray);
    }

    /// <summary>The alpha of a bitmap as coverage.</summary>
    public static MaskImage AlphaToMask(SKBitmap alpha8)
    {
        if (alpha8.ColorType == SKColorType.Alpha8) return MaskImage.Adopt(alpha8);
        var gray = NewGray(alpha8.Width, alpha8.Height);
        ExtractAlpha(alpha8.GetPixelSpan(), alpha8.RowBytes, gray.GetPixelSpan(), gray.RowBytes, alpha8.Width, alpha8.Height);
        return MaskImage.Adopt(gray);
    }
}
