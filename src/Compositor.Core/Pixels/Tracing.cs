using Compositor.Imaging;
using SkiaSharp;

namespace Compositor.Pixels;

public sealed class WandException : Exception
{
    public WandException(string message) : base(message) { }
    public static WandException TooDetailed() => new("That selection is too detailed to outline. Try a different Tolerance, or turn on Contiguous.");
    public static WandException Memory() => new("There isn’t enough memory to make that selection.");
}

/// <summary>Ports of WandPixels.c (matching and outline tracing) and MaskTracing.swift.</summary>
public static class Tracing
{
    private const int East = 1, South = 2, West = 4, North = 8;
    private const long EdgeLimit = 8_000_000;

    private static bool Matches(ReadOnlySpan<byte> rgba, int i, Span<int> reference, int tolerance)
    {
        for (int c = 0; c < 4; c++)
        {
            int d = rgba[i + c] - reference[c];
            if (d < -tolerance || d > tolerance) return false;
        }
        return true;
    }

    /// <summary>wand_mask: 255 for matching pixels (4-connected from the seed when contiguous), 0 elsewhere.</summary>
    public static long WandMask(ReadOnlySpan<byte> rgba, int width, int height, int stride, int seedX, int seedY, int radius,
        int tolerance, bool contiguous, byte[] mask)
    {
        Array.Clear(mask);
        if (width == 0 || height == 0 || seedX >= width || seedY >= height || seedX < 0 || seedY < 0) return 0;
        int x0 = Math.Max(0, seedX - radius), x1 = Math.Min(width - 1, seedX + radius);
        int y0 = Math.Max(0, seedY - radius), y1 = Math.Min(height - 1, seedY + radius);
        Span<long> sums = stackalloc long[4];
        long samples = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++, samples++)
                for (int c = 0; c < 4; c++) sums[c] += rgba[y * stride + x * 4 + c];
        Span<int> reference = stackalloc int[4];
        for (int c = 0; c < 4; c++) reference[c] = (int)((sums[c] + samples / 2) / samples);
        long count = 0;
        if (!contiguous)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (Matches(rgba, y * stride + x * 4, reference, tolerance)) { mask[y * width + x] = 255; count++; }
            return count;
        }
        var stack = new Stack<(int X, int Y)>();
        stack.Push((seedX, seedY));
        while (stack.Count > 0)
        {
            var (sx, sy) = stack.Pop();
            int rowOffset = sy * stride, outOffset = sy * width;
            if (mask[outOffset + sx] != 0 || !Matches(rgba, rowOffset + sx * 4, reference, tolerance)) continue;
            int left = sx, right = sx;
            while (left > 0 && mask[outOffset + left - 1] == 0 && Matches(rgba, rowOffset + (left - 1) * 4, reference, tolerance)) left--;
            while (right + 1 < width && mask[outOffset + right + 1] == 0 && Matches(rgba, rowOffset + (right + 1) * 4, reference, tolerance)) right++;
            Array.Fill(mask, (byte)255, outOffset + left, right - left + 1);
            count += right - left + 1;
            for (int side = 0; side < 2; side++)
            {
                if (side == 0 ? sy == 0 : sy + 1 >= height) continue;
                int ny = side == 0 ? sy - 1 : sy + 1;
                int nrow = ny * stride, nout = ny * width;
                bool inRun = false;
                for (int nx = left; nx <= right; nx++)
                {
                    bool candidate = mask[nout + nx] == 0 && Matches(rgba, nrow + nx * 4, reference, tolerance);
                    if (candidate && !inRun) stack.Push((nx, ny));
                    inRun = candidate;
                }
            }
        }
        return count;
    }

    private static int TurnRight(int d) => d == North ? East : d << 1;
    private static int TurnLeft(int d) => d == East ? North : d >> 1;

    /// <summary>
    /// wand_trace: the outline of a mask's nonzero pixels along pixel edges, as closed loops of corner points. Outer
    /// boundaries run clockwise and holes counterclockwise (y down), so the winding rule fills exactly those pixels.
    /// </summary>
    public static SKPath? Trace(ReadOnlySpan<byte> mask, int width, int height, bool smooth = false)
    {
        if (width == 0 || height == 0) return null;
        int stride = width + 1;
        var output = new byte[stride * (height + 1)];
        long edges = 0;
        for (int y = 0; y < height; y++)
        {
            var row = mask.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                if (row[x] == 0) continue;
                if (y == 0 || mask[(y - 1) * width + x] == 0) { output[y * stride + x] |= East; edges++; }
                if (x + 1 == width || row[x + 1] == 0) { output[y * stride + x + 1] |= South; edges++; }
                if (y + 1 == height || mask[(y + 1) * width + x] == 0) { output[(y + 1) * stride + x + 1] |= West; edges++; }
                if (x == 0 || row[x - 1] == 0) { output[(y + 1) * stride + x] |= North; edges++; }
            }
            if (edges > EdgeLimit) throw WandException.TooDetailed();
        }
        var path = new SKPath { FillType = SKPathFillType.Winding };
        var points = new List<SKPoint>();
        bool any = false;
        for (int start = 0; start < output.Length; start++)
        {
            while (output[start] != 0)
            {
                points.Clear();
                int v = start, heading = 0, initial = 0;
                do
                {
                    int bits = output[v], d;
                    if (heading == 0) d = bits & -bits;
                    else if ((bits & TurnRight(heading)) != 0) d = TurnRight(heading);
                    else if ((bits & heading) != 0) d = heading;
                    else if ((bits & TurnLeft(heading)) != 0) d = TurnLeft(heading);
                    else d = bits & -bits;
                    if (d == 0) break;
                    output[v] &= (byte)~d;
                    if (d != heading) points.Add(new SKPoint(v % stride, v / stride));
                    if (heading == 0) initial = d;
                    heading = d;
                    v = d == East ? v + 1 : d == West ? v - 1 : d == South ? v + stride : v - stride;
                } while (v != start);
                if (heading == initial && points.Count > 0) points.RemoveAt(0);
                if (points.Count >= 3)
                {
                    path.AddPoly(smooth ? SmoothClosed(points) : points.ToArray(), true);
                    any = true;
                }
            }
        }
        return any ? path : null;
    }

    /// <summary>
    /// Rounds the pixel-sized corners in a traced wand outline without materially
    /// moving long straight edges. This turns staircase boundaries into short
    /// diagonals that rasterize with partial coverage instead of jagged pixels.
    /// </summary>
    private static SKPoint[] SmoothClosed(IReadOnlyList<SKPoint> points)
    {
        var result = new SKPoint[points.Count * 2];
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            float inset = MathF.Min(0.5f, length * 0.25f);
            float ux = length > 0 ? dx / length : 0, uy = length > 0 ? dy / length : 0;
            result[i * 2] = new SKPoint(a.X + ux * inset, a.Y + uy * inset);
            result[i * 2 + 1] = new SKPoint(b.X - ux * inset, b.Y - uy * inset);
        }
        return result;
    }

    /// <summary>Outline of a mask's pixels darker than 50% gray, in its top-left pixel coordinates.</summary>
    public static SKPath? DarkPixels(MaskImage mask)
    {
        var bytes = new byte[mask.Width * mask.Height];
        var src = mask.Pixels;
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++) bytes[y * mask.Width + x] = src[y * mask.RowBytes + x] < 128 ? (byte)255 : (byte)0;
        return Trace(bytes, mask.Width, mask.Height);
    }

    /// <summary>Outline of an image's pixels that are at least 50% opaque.</summary>
    public static SKPath? OpaquePixels(RasterImage image)
    {
        var bytes = new byte[image.Width * image.Height];
        var src = image.Pixels;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) bytes[y * image.Width + x] = src[y * image.RowBytes + x * 4 + 3] >= 128 ? (byte)255 : (byte)0;
        return Trace(bytes, image.Width, image.Height);
    }
}
