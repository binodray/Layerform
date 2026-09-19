using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Pixels;

/// <summary>Blurs and helpers standing in for the Core Image filters the Mac app uses.</summary>
public static class PixelFilters
{
    /// <summary>Gaussian kernel weights for a standard deviation, out to three deviations.</summary>
    private static float[] Kernel(double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var weights = new float[radius * 2 + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double w = Math.Exp(-(i * i) / (2 * sigma * sigma));
            weights[i + radius] = (float)w;
            sum += w;
        }
        for (int i = 0; i < weights.Length; i++) weights[i] = (float)(weights[i] / sum);
        return weights;
    }

    /// <summary>
    /// Separable Gaussian blur of premultiplied RGBA. Unclamped (CIGaussianBlur on an unclamped image): pixels beyond the
    /// edges are transparent, so a blur softens the layer's edges. Large deviations use three box passes.
    /// </summary>
    public static RasterImage GaussianBlur(RasterImage image, double sigma)
    {
        int w = image.Width, h = image.Height;
        var data = new float[w * h * 4];
        var src = image.Pixels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w * 4; x++) data[y * w * 4 + x] = src[y * image.RowBytes + x];
        if (sigma > 0.3)
        {
            if (sigma > 20) { for (int pass = 0; pass < 3; pass++) BoxBlur(data, w, h, 4, BoxRadius(sigma)); }
            else { var k = Kernel(sigma); Convolve(data, w, h, 4, k, horizontal: true, clamp: false); Convolve(data, w, h, 4, k, horizontal: false, clamp: false); }
        }
        var bitmap = PixelOps.NewRgba(w, h, false);
        var target = bitmap.GetPixelSpan();
        for (int i = 0; i < w * h; i++)
        {
            int p = i * 4;
            byte a = (byte)Math.Clamp((int)MathF.Round(data[p + 3]), 0, 255);
            target[p + 3] = a;
            for (int c = 0; c < 3; c++) target[p + c] = (byte)Math.Min(a, Math.Clamp((int)MathF.Round(data[p + c]), 0, 255));
        }
        return RasterImage.Adopt(bitmap);
    }

    /// <summary>Gaussian blur of gray values; <paramref name="clamp"/> repeats the edge instead of fading to black.</summary>
    public static MaskImage GaussianBlurGray(MaskImage image, double sigma, bool clamp)
    {
        int w = image.Width, h = image.Height;
        var data = new float[w * h];
        var src = image.Pixels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) data[y * w + x] = src[y * image.RowBytes + x];
        var k = Kernel(sigma);
        Convolve(data, w, h, 1, k, true, clamp);
        Convolve(data, w, h, 1, k, false, clamp);
        var bytes = new byte[w * h];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp((int)MathF.Round(data[i]), 0, 255);
        return MaskImage.FromPixels(w, h, bytes);
    }

    private static int BoxRadius(double sigma)
    {
        // Three box passes of width 2r+1 approximate a Gaussian of deviation sqrt(((2r+1)^2 - 1) / 4).
        return Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
    }

    private static void BoxBlur(float[] data, int w, int h, int channels, int radius)
    {
        float span = radius * 2 + 1;
        var temp = new float[data.Length];
        Parallel.For(0, h, y =>
        {
            var sum = new float[channels];
            int row = y * w * channels;
            for (int x = -radius; x <= radius; x++)
                if (x >= 0 && x < w) for (int c = 0; c < channels; c++) sum[c] += data[row + x * channels + c];
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < channels; c++) temp[row + x * channels + c] = sum[c] / span;
                int outIndex = x - radius, inIndex = x + radius + 1;
                if (outIndex >= 0) for (int c = 0; c < channels; c++) sum[c] -= data[row + outIndex * channels + c];
                if (inIndex < w) for (int c = 0; c < channels; c++) sum[c] += data[row + inIndex * channels + c];
            }
        });
        Parallel.For(0, w, x =>
        {
            var sum = new float[channels];
            for (int y = -radius; y <= radius; y++)
                if (y >= 0 && y < h) for (int c = 0; c < channels; c++) sum[c] += temp[(y * w + x) * channels + c];
            for (int y = 0; y < h; y++)
            {
                for (int c = 0; c < channels; c++) data[(y * w + x) * channels + c] = sum[c] / span;
                int outIndex = y - radius, inIndex = y + radius + 1;
                if (outIndex >= 0) for (int c = 0; c < channels; c++) sum[c] -= temp[(outIndex * w + x) * channels + c];
                if (inIndex < h) for (int c = 0; c < channels; c++) sum[c] += temp[(inIndex * w + x) * channels + c];
            }
        });
    }

    private static void Convolve(float[] data, int w, int h, int channels, float[] kernel, bool horizontal, bool clamp)
    {
        int radius = kernel.Length / 2;
        var source = (float[])data.Clone();
        int outer = horizontal ? h : w, inner = horizontal ? w : h;
        Parallel.For(0, outer, o =>
        {
            for (int i = 0; i < inner; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float sum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int j = i + k;
                        if (j < 0 || j >= inner)
                        {
                            if (!clamp) continue;
                            j = Math.Clamp(j, 0, inner - 1);
                        }
                        int index = horizontal ? (o * w + j) * channels + c : (j * w + o) * channels + c;
                        sum += source[index] * kernel[k + radius];
                    }
                    int target = horizontal ? (o * w + i) * channels + c : (i * w + o) * channels + c;
                    data[target] = sum;
                }
            }
        });
    }

    /// <summary>CIMotionBlur-like streak: a Gaussian along the angle (degrees counterclockwise) with deviation
    /// <paramref name="radius"/>, sampled bilinearly, transparent past the edges.</summary>
    public static RasterImage MotionBlur(RasterImage image, double radius, double angleDegrees)
    {
        int w = image.Width, h = image.Height;
        if (radius < 0.5) return image;
        double angle = angleDegrees * Math.PI / 180;
        double ux = Math.Cos(angle), uy = -Math.Sin(angle);
        int reach = (int)Math.Ceiling(radius * 3);
        var weights = new float[reach * 2 + 1];
        float total = 0;
        for (int i = -reach; i <= reach; i++) { weights[i + reach] = (float)Math.Exp(-(i * i) / (2 * radius * radius)); total += weights[i + reach]; }
        for (int i = 0; i < weights.Length; i++) weights[i] /= total;
        var src = image.CopyPixels();
        int stride = image.RowBytes;
        var bitmap = PixelOps.NewRgba(w, h, false);
        var basePtr = bitmap.GetPixels();
        int tstride = bitmap.RowBytes;
        Parallel.For(0, h, y =>
        {
            var row = NativeMemory.Span(basePtr + y * tstride, w * 4);
            Span<float> sum = stackalloc float[4];
            for (int x = 0; x < w; x++)
            {
                sum.Clear();
                for (int k = -reach; k <= reach; k++)
                {
                    double sx = x + ux * k, sy = y + uy * k;
                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    double fx = sx - x0, fy = sy - y0;
                    float wk = weights[k + reach];
                    for (int j = 0; j < 2; j++)
                        for (int i = 0; i < 2; i++)
                        {
                            int px = x0 + i, py = y0 + j;
                            if (px < 0 || py < 0 || px >= w || py >= h) continue;
                            float wb = (float)((i == 1 ? fx : 1 - fx) * (j == 1 ? fy : 1 - fy)) * wk;
                            if (wb == 0) continue;
                            int p = py * stride + px * 4;
                            for (int c = 0; c < 4; c++) sum[c] += src[p + c] * wb;
                        }
                }
                byte a = (byte)Math.Clamp((int)MathF.Round(sum[3]), 0, 255);
                row[x * 4 + 3] = a;
                for (int c = 0; c < 3; c++) row[x * 4 + c] = (byte)Math.Min(a, Math.Clamp((int)MathF.Round(sum[c]), 0, 255));
            }
        });
        return RasterImage.Adopt(bitmap);
    }

    /// <summary>coverage × adjusted + (1 − coverage) × original over an image's own pixel grid (PixelAdjust.blend).</summary>
    public static RasterImage BlendThroughSelection(RasterImage adjusted, RasterImage original, Editing.SelectionClip selection, Affine pixelToDocument)
    {
        int w = adjusted.Width, h = adjusted.Height;
        var coverage = Editing.SelectionRaster.CoverageOnGrid(selection, w, h, pixelToDocument);
        var bitmap = PixelOps.NewRgba(w, h, false);
        var target = bitmap.GetPixelSpan();
        var a = adjusted.Pixels;
        var o = original.Pixels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double m = coverage[y * w + x] / 255.0;
                int p = y * adjusted.RowBytes + x * 4, q = y * original.RowBytes + x * 4, t = y * bitmap.RowBytes + x * 4;
                for (int c = 0; c < 4; c++) target[t + c] = (byte)Math.Round(o[q + c] + (a[p + c] - o[q + c]) * m);
            }
        return RasterImage.Adopt(bitmap);
    }

    public static MaskImage BlendThroughSelection(MaskImage adjusted, MaskImage original, Editing.SelectionClip selection, Affine pixelToDocument)
    {
        int w = adjusted.Width, h = adjusted.Height;
        var coverage = Editing.SelectionRaster.CoverageOnGrid(selection, w, h, pixelToDocument);
        var bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double m = coverage[y * w + x] / 255.0;
                bytes[y * w + x] = (byte)Math.Round(original.ValueAt(x, y) + (adjusted.ValueAt(x, y) - original.ValueAt(x, y)) * m);
            }
        return MaskImage.FromPixels(w, h, bytes);
    }
}

/// <summary>Cropping images to what is there (PixelFilter.trimmed).</summary>
public static class PixelFilter
{
    public static (RasterImage Image, LayerTransform Transform) Trimmed(RasterImage image, LayerTransform placed)
    {
        var (l, t, r, b) = PixelOps.AlphaBounds(image.Pixels, image.Width, image.Height, image.RowBytes);
        var crop = new SKRectI(l, t, r, b);
        if (crop.Width < 1 || crop.Height < 1 || (crop.Width == image.Width && crop.Height == image.Height)) return (image, placed);
        var cropped = image.Crop(crop);
        var size = new SizeD(crop.Width * placed.Size.Width / image.Width, crop.Height * placed.Size.Height / image.Height);
        var toDocument = LayerTransform.PixelToDocument(placed, image.Width, image.Height);
        var middle = toDocument.Apply(new PointD((crop.Left + crop.Right) / 2.0, (crop.Top + crop.Bottom) / 2.0));
        return (cropped, placed with { Size = size, Origin = new PointD(middle.X - size.Width / 2, middle.Y - size.Height / 2) });
    }
}
