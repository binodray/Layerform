using System.Runtime.InteropServices;
using SkiaSharp;

namespace Compositor.Imaging;

/// <summary>
/// An immutable image: 8-bit sRGB RGBA, premultiplied, rows top-down (Core Graphics' premultipliedLast). Identity is
/// reference identity: history snapshots share images and never copy pixels, as the Mac app shares CGImages.
/// </summary>
public sealed class RasterImage
{
    public int Width { get; }
    public int Height { get; }
    public SKImage Image { get; }
    private readonly SKBitmap bitmap;
    private readonly long pressure;

    private RasterImage(SKBitmap bitmap)
    {
        this.bitmap = bitmap;
        Width = bitmap.Width;
        Height = bitmap.Height;
        bitmap.SetImmutable();
        Image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Could not wrap bitmap.");
        pressure = (long)Width * Height * 4;
        if (pressure > 0) GC.AddMemoryPressure(pressure);
    }

    ~RasterImage()
    {
        if (pressure > 0) GC.RemoveMemoryPressure(pressure);
    }

    public static SKImageInfo Info(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

    /// <summary>Takes ownership of an RGBA-premultiplied bitmap; the bitmap must not be changed afterwards.</summary>
    public static RasterImage Adopt(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888 || bitmap.AlphaType != SKAlphaType.Premul)
        {
            var converted = new SKBitmap(Info(bitmap.Width, bitmap.Height));
            using (var canvas = new SKCanvas(converted))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(bitmap, 0, 0, paint);
            }
            bitmap.Dispose();
            bitmap = converted;
        }
        return new RasterImage(bitmap);
    }

    public static RasterImage FromPixels(int width, int height, ReadOnlySpan<byte> rgbaPremultiplied)
    {
        var bitmap = new SKBitmap(Info(width, height));
        rgbaPremultiplied[..(width * height * 4)].CopyTo(bitmap.GetPixelSpan());
        return new RasterImage(bitmap);
    }

    public static RasterImage Blank(int width, int height)
    {
        var bitmap = new SKBitmap(Info(width, height));
        bitmap.Erase(SKColors.Transparent);
        return new RasterImage(bitmap);
    }

    public int RowBytes => bitmap.RowBytes;
    public ReadOnlySpan<byte> Pixels => bitmap.GetPixelSpan();

    /// <summary>A writable copy of the pixels, the same layout.</summary>
    public SKBitmap CopyBitmap()
    {
        var copy = new SKBitmap(Info(Width, Height));
        Pixels.CopyTo(copy.GetPixelSpan());
        return copy;
    }

    public byte[] CopyPixels() => Pixels.ToArray();

    /// <summary>The pixels inside <paramref name="rect"/> (clipped to the image) as a new image.</summary>
    public RasterImage Crop(SKRectI rect)
    {
        rect.Intersect(new SKRectI(0, 0, Width, Height));
        if (rect.Width <= 0 || rect.Height <= 0) return Blank(1, 1);
        var bitmap = new SKBitmap(Info(rect.Width, rect.Height));
        var source = Pixels;
        var target = bitmap.GetPixelSpan();
        int rowBytes = rect.Width * 4;
        for (int y = 0; y < rect.Height; y++)
            source.Slice((rect.Top + y) * RowBytes + rect.Left * 4, rowBytes).CopyTo(target.Slice(y * bitmap.RowBytes, rowBytes));
        return new RasterImage(bitmap);
    }

    /// <summary>Straight (unpremultiplied) RGBA at one pixel.</summary>
    public (byte R, byte G, byte B, byte A) PixelAt(int x, int y)
    {
        var p = Pixels.Slice(y * RowBytes + x * 4, 4);
        return (p[0], p[1], p[2], p[3]);
    }
}

/// <summary>
/// An immutable 8-bit gray coverage image without alpha (white reveals, black hides), the layout of project mask
/// files. <see cref="AlphaImage"/> views the same pixels as alpha for compositing.
/// </summary>
public sealed class MaskImage
{
    public int Width { get; }
    public int Height { get; }
    private readonly SKBitmap bitmap;
    private SKImage? gray;
    private SKImage? alpha;
    private readonly long pressure;

    private MaskImage(SKBitmap bitmap)
    {
        this.bitmap = bitmap;
        Width = bitmap.Width;
        Height = bitmap.Height;
        bitmap.SetImmutable();
        pressure = (long)Width * Height;
        if (pressure > 0) GC.AddMemoryPressure(pressure);
    }

    ~MaskImage()
    {
        if (pressure > 0) GC.RemoveMemoryPressure(pressure);
    }

    public static SKImageInfo Info(int width, int height) => new(width, height, SKColorType.Gray8, SKAlphaType.Opaque);

    public static MaskImage Adopt(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Gray8)
        {
            // Alpha-8 bitmaps carry coverage in the same byte layout.
            if (bitmap.ColorType == SKColorType.Alpha8)
            {
                var copy = new SKBitmap(Info(bitmap.Width, bitmap.Height));
                CopyRows(bitmap.GetPixelSpan(), bitmap.RowBytes, copy.GetPixelSpan(), copy.RowBytes, bitmap.Width, bitmap.Height);
                bitmap.Dispose();
                bitmap = copy;
            }
            else throw new ArgumentException("Masks are 8-bit gray.");
        }
        return new MaskImage(bitmap);
    }

    private static void CopyRows(ReadOnlySpan<byte> source, int sourceStride, Span<byte> target, int targetStride, int width, int height)
    {
        for (int y = 0; y < height; y++) source.Slice(y * sourceStride, width).CopyTo(target.Slice(y * targetStride, width));
    }

    public static MaskImage FromPixels(int width, int height, ReadOnlySpan<byte> gray)
    {
        var bitmap = new SKBitmap(Info(width, height));
        var target = bitmap.GetPixelSpan();
        for (int y = 0; y < height; y++) gray.Slice(y * width, width).CopyTo(target.Slice(y * bitmap.RowBytes, width));
        return new MaskImage(bitmap);
    }

    public static MaskImage Solid(byte value, int width = 1, int height = 1)
    {
        var bitmap = new SKBitmap(Info(width, height));
        bitmap.GetPixelSpan().Fill(value);
        return new MaskImage(bitmap);
    }

    public int RowBytes => bitmap.RowBytes;
    public ReadOnlySpan<byte> Pixels => bitmap.GetPixelSpan();
    public bool IsUniform1x1 => Width == 1 && Height == 1;
    public byte ValueAt(int x, int y) => Pixels[y * RowBytes + x];

    public SKImage GrayImage => gray ??= SKImage.FromBitmap(bitmap);

    /// <summary>The coverage as an alpha-only image over the same memory (kept alive by this object).</summary>
    public SKImage AlphaImage
    {
        get
        {
            if (alpha != null) return alpha;
            var info = new SKImageInfo(Width, Height, SKColorType.Alpha8, SKAlphaType.Premul);
            alpha = SKImage.FromPixels(info, bitmap.GetPixels(), bitmap.RowBytes)
                ?? SKImage.FromPixelCopy(info, bitmap.GetPixels(), bitmap.RowBytes);
            return alpha;
        }
    }

    public byte[] CopyPixels()
    {
        var result = new byte[Width * Height];
        var source = Pixels;
        for (int y = 0; y < Height; y++) source.Slice(y * RowBytes, Width).CopyTo(result.AsSpan(y * Width, Width));
        return result;
    }

    public SKBitmap CopyBitmap()
    {
        var copy = new SKBitmap(Info(Width, Height));
        Pixels.CopyTo(copy.GetPixelSpan());
        return copy;
    }

    public MaskImage Crop(SKRectI rect)
    {
        rect.Intersect(new SKRectI(0, 0, Width, Height));
        if (rect.Width <= 0 || rect.Height <= 0) return Solid(255);
        var bitmap = new SKBitmap(Info(rect.Width, rect.Height));
        var source = Pixels;
        var target = bitmap.GetPixelSpan();
        for (int y = 0; y < rect.Height; y++)
            source.Slice((rect.Top + y) * RowBytes + rect.Left, rect.Width).CopyTo(target.Slice(y * bitmap.RowBytes, rect.Width));
        return new MaskImage(bitmap);
    }
}

/// <summary>A layer's pixels with its Layers-panel thumbnail and name (the Mac ImportedImage).</summary>
public sealed record ImageAsset(RasterImage Image, RasterImage Thumbnail, string Name);

/// <summary>A mask's pixels with its thumbnail.</summary>
public sealed record MaskAsset(MaskImage Image, MaskImage Thumbnail, string Name = "Layer Mask");

internal static class NativeMemory
{
    public static unsafe Span<byte> Span(IntPtr pointer, int length) => new((void*)pointer, length);
}
