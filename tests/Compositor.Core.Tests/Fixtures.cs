using Compositor.Imaging;
using SkiaSharp;

namespace Compositor.Tests;

/// <summary>Builds `.comp` packages on disk the way the Mac app writes them, for compatibility tests.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CompositorTests-" + Guid.NewGuid().ToString("N"));
    public TempFolder() { Directory.CreateDirectory(Path); }
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public static class Fixtures
{
    public static string Id(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>A solid RGBA PNG (straight colour) of the given size.</summary>
    public static byte[] SolidPng(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(r, g, b, a));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>An RGBA PNG from a per-pixel function returning straight colour.</summary>
    public static byte[] Png(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>An 8-bit grayscale PNG without alpha: the only mask layout the Mac app accepts.</summary>
    public static byte[] GrayPng(int width, int height, Func<int, int, byte> value)
    {
        var bytes = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bytes[y * width + x] = value(x, y);
        return Codecs.EncodeGrayPng(MaskImage.FromPixels(width, height, bytes));
    }

    public static string WritePackage(string folder, string name, string manifest, Dictionary<string, byte[]> images)
    {
        var package = System.IO.Path.Combine(folder, name);
        Directory.CreateDirectory(System.IO.Path.Combine(package, "images"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(package, "manifest.json"), manifest);
        foreach (var (file, data) in images) System.IO.File.WriteAllBytes(System.IO.Path.Combine(package, "images", file), data);
        return package;
    }

    /// <summary>A layer record in the Mac encoder's shape (sorted keys, arrays for points and sizes).</summary>
    public static string Layer(Guid id, string name, string transform, string? extra = null, bool visible = true) =>
        "{\n" + (extra is null ? "" : extra + ",\n") +
        $"      \"id\" : \"{Id(id)}\",\n      \"isVisible\" : {(visible ? "true" : "false")},\n      \"name\" : \"{name}\",\n      \"transform\" : {transform}\n    }}";

    public static string Transform(double x, double y, double w, double h, double rotation = 0, bool flipX = false, bool flipY = false, string sampling = "High quality") =>
        $"{{\n        \"flipX\" : {(flipX ? "true" : "false")},\n        \"flipY\" : {(flipY ? "true" : "false")},\n        \"origin\" : [\n          {x},\n          {y}\n        ],\n        \"rotation\" : {rotation},\n        \"sampling\" : \"{sampling}\",\n        \"size\" : [\n          {w},\n          {h}\n        ]\n      }}";

    public static string Manifest(Guid document, int width, int height, int version, Guid? active, IEnumerable<string> layers, double? resolution = 72) =>
        "{\n" + (active is { } a ? $"  \"activeLayerID\" : \"{Id(a)}\",\n" : "") +
        $"  \"colorSpace\" : \"sRGB\",\n  \"documentID\" : \"{Id(document)}\",\n  \"format\" : \"com.compositor.project\",\n  \"height\" : {height},\n" +
        "  \"layers\" : [\n    " + string.Join(",\n    ", layers) + "\n  ],\n" +
        (resolution is { } r ? $"  \"resolution\" : {r},\n" : "") +
        $"  \"version\" : {version},\n  \"width\" : {width}\n}}";

    public static (byte R, byte G, byte B, byte A) Straight(RasterImage image, int x, int y)
    {
        var (r, g, b, a) = image.PixelAt(x, y);
        if (a == 0) return (0, 0, 0, 0);
        return ((byte)Math.Min(255, (r * 255 + a / 2) / a), (byte)Math.Min(255, (g * 255 + a / 2) / a), (byte)Math.Min(255, (b * 255 + a / 2) / a), a);
    }
}
