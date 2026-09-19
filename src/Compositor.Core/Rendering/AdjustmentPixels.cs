using Compositor.Geometry;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>
/// Colour adjustments applied in place to RGBA premultiplied pixels. Ports of LevelsPixels.c, AdjustPixels.c, and the
/// Swift Hue/Saturation colour cube, Curves, Exposure, Gradient Map and Grain.
/// </summary>
public static class AdjustmentPixels
{
    /// <summary>Applies an adjustment layer's settings. <paramref name="region"/> is the document area the pixels cover.</summary>
    public static void Apply(LayerAdjustment adjustment, SKBitmap pixels, RectD region)
    {
        switch (adjustment.Kind)
        {
            case AdjustmentKind.HueSaturation: HueSaturation(pixels, adjustment.ResolvedHsv); break;
            case AdjustmentKind.Levels: Levels(pixels, adjustment.Levels); break;
            case AdjustmentKind.Curves: Curves(pixels, adjustment.Curves); break;
            case AdjustmentKind.Exposure: Exposure(pixels, adjustment.Exposure); break;
            case AdjustmentKind.GradientMap: GradientMap(pixels, adjustment.GradientMap); break;
            case AdjustmentKind.Grain:
                double unitsPerPixel = region.Width / Math.Max(1, pixels.Width);
                Grain(pixels, adjustment.Grain, region.X, region.Y, unitsPerPixel, null);
                break;
        }
    }

    /// <summary>levels_apply: tables are 3 × 256 floats (0–1) indexed by unpremultiplied channel value.</summary>
    public static void LevelsApply(Span<byte> pixels, int count, float[] tables)
    {
        for (int i = 0; i < count; i++)
        {
            int p = i * 4;
            float alpha = pixels[p + 3];
            if (alpha == 0) continue;
            for (int channel = 0; channel < 3; channel++)
            {
                float x = MathF.Min(255, pixels[p + channel] * 255.0f / alpha);
                int lo = (int)x, hi = lo < 255 ? lo + 1 : 255;
                int t = channel * 256;
                float result = tables[t + lo] + (tables[t + hi] - tables[t + lo]) * (x - lo);
                pixels[p + channel] = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(result * alpha, MidpointRounding.AwayFromZero)));
            }
        }
    }

    public static float[] LevelsTables(LevelsSettings settings)
    {
        var tables = new float[768];
        var channels = new[] { LevelsChannel.Red, LevelsChannel.Green, LevelsChannel.Blue };
        for (int c = 0; c < 3; c++)
            for (int v = 0; v < 256; v++) tables[c * 256 + v] = (float)settings.Apply(v / 255.0, channels[c]);
        return tables;
    }

    /// <summary>
    /// LevelsFilter.run. Semi-transparent pixels are unpremultiplied before levels_apply and premultiplied again after,
    /// exactly as the Mac app does (levels_apply itself also divides by alpha); reproduced for identical output.
    /// </summary>
    public static void Levels(SKBitmap bitmap, LevelsSettings settings)
    {
        if (settings.IsIdentity) return;
        var tables = LevelsTables(settings);
        var pixels = bitmap.GetPixelSpan();
        int count = bitmap.Width * bitmap.Height;
        for (int i = 0; i < count; i++)
        {
            int a = pixels[i * 4 + 3];
            if (a <= 0 || a >= 255) continue;
            for (int k = 0; k < 3; k++) pixels[i * 4 + k] = (byte)Math.Min(255, (pixels[i * 4 + k] * 255 + a / 2) / a);
        }
        LevelsApply(pixels, count, tables);
        for (int i = 0; i < count; i++)
        {
            int a = pixels[i * 4 + 3];
            if (a <= 0 || a >= 255) continue;
            for (int k = 0; k < 3; k++) pixels[i * 4 + k] = (byte)((pixels[i * 4 + k] * a + 127) / 255);
        }
    }

    public static void Curves(SKBitmap bitmap, CurvesSettings curves)
    {
        if (!curves.IsValid) return;
        var tables = new float[768];
        for (int channel = 1; channel <= 3; channel++)
            for (int v = 0; v < 256; v++)
                tables[(channel - 1) * 256 + v] = (float)(curves.Value(curves.Value(v, channel), 0) / 255);
        LevelsApply(bitmap.GetPixelSpan(), bitmap.Width * bitmap.Height, tables);
    }

    public static void Exposure(SKBitmap bitmap, ExposureSettings settings)
    {
        if (!settings.IsValid) return;
        var table = settings.Table();
        var tables = new float[768];
        for (int c = 0; c < 3; c++) Array.Copy(table, 0, tables, c * 256, 256);
        LevelsApply(bitmap.GetPixelSpan(), bitmap.Width * bitmap.Height, tables);
    }

    public static void GradientMap(SKBitmap bitmap, GradientMapSettings settings)
    {
        if (!settings.IsValid) return;
        var (dark, light) = settings.Ends;
        var table = new byte[768];
        static byte Q(double v) => (byte)Math.Min(255, Math.Max(0, Math.Round(v * 255, MidpointRounding.AwayFromZero)));
        for (int index = 0; index < 256; index++)
        {
            double t = index / 255.0;
            table[index * 3] = Q(dark.Red + (light.Red - dark.Red) * t);
            table[index * 3 + 1] = Q(dark.Green + (light.Green - dark.Green) * t);
            table[index * 3 + 2] = Q(dark.Blue + (light.Blue - dark.Blue) * t);
        }
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * stride, bitmap.Width * 4);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int p = x * 4;
                uint a = row[p + 3];
                if (a == 0) continue;
                uint r = row[p], g = row[p + 1], b = row[p + 2];
                if (a < 255)
                {
                    r = Math.Min(255, (r * 255u + a / 2) / a);
                    g = Math.Min(255, (g * 255u + a / 2) / a);
                    b = Math.Min(255, (b * 255u + a / 2) / a);
                }
                uint level = (2126u * r + 7152u * g + 722u * b + 5000u) / 10000u;
                int color = (int)Math.Min(255, level) * 3;
                row[p] = (byte)((table[color] * a + 127u) / 255u);
                row[p + 1] = (byte)((table[color + 1] * a + 127u) / 255u);
                row[p + 2] = (byte)((table[color + 2] * a + 127u) / 255u);
            }
        }
    }

    private static uint Mix32(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    private static float Lattice(long ix, long iy, uint seed)
    {
        uint h = Mix32(unchecked((uint)ix * 0x9E3779B1U) ^ Mix32(unchecked((uint)iy * 0x85EBCA77U) ^ seed));
        return (h & 0xFFFFU) / 65535.0f + (h >> 16) / 65535.0f - 1.0f;
    }

    private static float Clamp255(float v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>adjust_grain: film grain fixed in document space by the seed.</summary>
    public static void Grain(SKBitmap bitmap, GrainSettings settings, double originX, double originY, double unitsPerPixel, uint? seedOverride)
    {
        if (!settings.IsValid || !(unitsPerPixel > 0) || !double.IsFinite(unitsPerPixel)) return;
        double amount = settings.Amount, size = settings.Size, roughness = settings.Roughness;
        if (!(amount > 0)) return;
        if (!(size > 0)) size = 1;
        uint seed = seedOverride ?? settings.Seed;
        float strength = (float)(amount > 100 ? 1.0 : amount / 100.0) * 0.35f * 255.0f;
        float rough = (float)(roughness < 0 ? 0.0 : roughness > 100 ? 1.0 : roughness / 100.0);
        uint fineSeed = Mix32(seed ^ 0xA511E9B3U);
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes, width = bitmap.Width;
        for (int y = 0; y < bitmap.Height; y++)
        {
            double v = originY + (y + 0.5) * unitsPerPixel;
            double cellY = Math.Floor(v / size);
            float ty = (float)(v / size - cellY);
            ty = ty * ty * (3.0f - 2.0f * ty);
            long iy = (long)cellY, fineY = (long)Math.Floor(v);
            var row = pixels.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int p = x * 4;
                uint a = row[p + 3];
                if (a == 0) continue;
                double u = originX + (x + 0.5) * unitsPerPixel;
                double cellX = Math.Floor(u / size);
                float tx = (float)(u / size - cellX);
                tx = tx * tx * (3.0f - 2.0f * tx);
                long ix = (long)cellX;
                float n00 = Lattice(ix, iy, seed), n10 = Lattice(ix + 1, iy, seed);
                float n01 = Lattice(ix, iy + 1, seed), n11 = Lattice(ix + 1, iy + 1, seed);
                float top = n00 + (n10 - n00) * tx, bottom = n01 + (n11 - n01) * tx;
                float smooth = (top + (bottom - top) * ty) * 1.6f;
                float fine = Lattice((long)Math.Floor(u), fineY, fineSeed);
                float noise = smooth + (fine - smooth) * rough;
                float unpremultiply = a == 255 ? 1.0f : 255.0f / a;
                float r = row[p] * unpremultiply, g = row[p + 1] * unpremultiply, b = row[p + 2] * unpremultiply;
                float level = (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255.0f;
                if (level > 1) level = 1;
                float delta = noise * strength * (0.4f + 2.4f * level * (1.0f - level));
                float coverage = a / 255.0f;
                row[p] = (byte)(Clamp255(r + delta) * coverage + 0.5f);
                row[p + 1] = (byte)(Clamp255(g + delta) * coverage + 0.5f);
                row[p + 2] = (byte)(Clamp255(b + delta) * coverage + 0.5f);
            }
        }
    }

    // MARK: Hue/Saturation

    public const int CubeDimension = 33;

    public static (double Shift, double Saturation, double Lightness)[] HueResponse(HueSaturationSettings settings)
    {
        var result = new (double, double, double)[361];
        for (int degree = 0; degree <= 360; degree++)
        {
            double shift = 0, saturation = 0, lightness = 0;
            foreach (var (range, adjustment) in settings.Adjustments)
            {
                if (adjustment == RangeAdjustment.Zero) continue;
                double weight = settings.Weight(range, degree);
                if (!(weight > 0)) continue;
                shift += adjustment.Hue * weight;
                saturation += adjustment.Saturation * weight;
                lightness += adjustment.Lightness * weight;
            }
            result[degree] = (shift, saturation, lightness);
        }
        return result;
    }

    public static float[] Cube(HueSaturationSettings settings)
    {
        var response = HueResponse(settings);
        int dim = CubeDimension;
        var values = new float[dim * dim * dim * 3];
        double step = dim - 1;
        int index = 0;
        for (int blue = 0; blue < dim; blue++)
            for (int green = 0; green < dim; green++)
                for (int red = 0; red < dim; red++)
                {
                    var c = AdjustColor(red / step, green / step, blue / step, settings, response);
                    values[index++] = (float)c.R;
                    values[index++] = (float)c.G;
                    values[index++] = (float)c.B;
                }
        return values;
    }

    public static (double R, double G, double B) AdjustColor(double red, double green, double blue, HueSaturationSettings settings,
        (double Shift, double Saturation, double Lightness)[]? response = null)
    {
        var (hue, saturation, lightness) = ToHsl(red, green, blue);
        double lightnessAmount;
        if (settings.Colorize)
        {
            hue = settings.Hue % 360;
            saturation = Math.Min(1, Math.Max(0, settings.Saturation / 100));
            lightnessAmount = settings.Lightness / 100;
        }
        else
        {
            var table = response ?? HueResponse(settings);
            var sampled = table[Math.Min(table.Length - 1, Math.Max(0, (int)Math.Round(hue, MidpointRounding.AwayFromZero)))];
            lightnessAmount = sampled.Lightness / 100;
            hue = (hue + sampled.Shift) % 360;
            if (hue < 0) hue += 360;
            saturation = Math.Min(1, Math.Max(0, saturation * (1 + sampled.Saturation / 100)));
        }
        double amount = Math.Min(1, Math.Max(-1, lightnessAmount));
        lightness = amount >= 0 ? lightness + (1 - lightness) * amount : lightness * (1 + amount);
        return ToRgb(hue, saturation, Math.Min(1, Math.Max(0, lightness)));
    }

    public static double ShiftedHue(double hue, HueSaturationSettings settings)
    {
        double shift = 0;
        foreach (var (range, adjustment) in settings.Adjustments)
            if (adjustment.Hue != 0) shift += adjustment.Hue * settings.Weight(range, hue);
        double shifted = (hue + shift) % 360;
        return shifted < 0 ? shifted + 360 : shifted;
    }

    private static (double H, double S, double L) ToHsl(double red, double green, double blue)
    {
        double high = Math.Max(red, Math.Max(green, blue)), low = Math.Min(red, Math.Min(green, blue));
        double lightness = (high + low) / 2, delta = high - low;
        if (!(delta > 0)) return (0, 0, lightness);
        double saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (high == red) hue = (green - blue) / delta;
        else if (high == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return (hue, Math.Min(1, saturation), lightness);
    }

    private static (double R, double G, double B) ToRgb(double hue, double saturation, double lightness)
    {
        if (!(saturation > 0)) return (lightness, lightness, lightness);
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hue / 60;
        double second = chroma * (1 - Math.Abs(sector % 2 - 1));
        double b0 = lightness - chroma / 2;
        (double r, double g, double b) = ((int)sector) switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };
        return (Math.Min(1, Math.Max(0, r + b0)), Math.Min(1, Math.Max(0, g + b0)), Math.Min(1, Math.Max(0, b + b0)));
    }

    /// <summary>CIColorCube equivalent: unpremultiply, trilinear lookup, premultiply (in float).</summary>
    public static void HueSaturation(SKBitmap bitmap, HueSaturationSettings settings)
    {
        if (settings.IsIdentity) return;
        var cube = Cube(settings);
        ApplyCube(bitmap, cube, CubeDimension);
    }

    public static void ApplyCube(SKBitmap bitmap, float[] cube, int dim)
    {
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        var basePtr = bitmap.GetPixels();
        Parallel.For(0, height, y =>
        {
            var row = Imaging.NativeMemory.Span(basePtr + y * stride, width * 4);
            float scale = dim - 1;
            for (int x = 0; x < width; x++)
            {
                int p = x * 4;
                byte a8 = row[p + 3];
                if (a8 == 0) continue;
                float a = a8 / 255f;
                float r = Math.Min(1f, row[p] / 255f / a), g = Math.Min(1f, row[p + 1] / 255f / a), b = Math.Min(1f, row[p + 2] / 255f / a);
                float fr = r * scale, fg = g * scale, fb = b * scale;
                int r0 = Math.Min(dim - 2, (int)fr), g0 = Math.Min(dim - 2, (int)fg), b0 = Math.Min(dim - 2, (int)fb);
                float dr = fr - r0, dg = fg - g0, db = fb - b0;
                Span<float> result = stackalloc float[3];
                for (int c = 0; c < 3; c++)
                {
                    float Sample(int ri, int gi, int bi) => cube[((bi * dim + gi) * dim + ri) * 3 + c];
                    float c00 = Sample(r0, g0, b0) + (Sample(r0 + 1, g0, b0) - Sample(r0, g0, b0)) * dr;
                    float c10 = Sample(r0, g0 + 1, b0) + (Sample(r0 + 1, g0 + 1, b0) - Sample(r0, g0 + 1, b0)) * dr;
                    float c01 = Sample(r0, g0, b0 + 1) + (Sample(r0 + 1, g0, b0 + 1) - Sample(r0, g0, b0 + 1)) * dr;
                    float c11 = Sample(r0, g0 + 1, b0 + 1) + (Sample(r0 + 1, g0 + 1, b0 + 1) - Sample(r0, g0 + 1, b0 + 1)) * dr;
                    float c0 = c00 + (c10 - c00) * dg, c1 = c01 + (c11 - c01) * dg;
                    result[c] = c0 + (c1 - c0) * db;
                }
                for (int c = 0; c < 3; c++)
                    row[p + c] = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(result[c], 0, 1) * a * 255f), 0, a8);
            }
        });
    }
}
