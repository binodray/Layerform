using Compositor.Imaging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using System.Globalization;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Compositor.App.Platform;

public sealed class ImageImportException : Exception
{
    public ImageImportException(string message, Exception? inner = null) : base(message, inner) { }
    public static ImageImportException Unreadable(Exception? e = null) => new("The image could not be read. It may be damaged or unavailable.", e);
    public static ImageImportException Unsupported() => new("Choose an image format supported by Windows.");
    public static ImageImportException TooLarge() => new("This import exceeds the current 100-megapixel document budget or 30,000-pixel side limit.");
}

/// <summary>Decodes images with Windows Imaging Component: EXIF orientation applied, colour managed to sRGB.</summary>
public static class ImageImport
{
    public static readonly string[] Extensions =
    {
        ".jpg", ".jpeg", ".png", ".svg", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".gif", ".ico", ".webp", ".avif", ".jxr", ".wdp", ".hdp",
    };

    public static async Task<ImageAsset> DecodeAsync(string path, long remainingPixels)
    {
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var svg = await DecodeSvgAsync(path, remainingPixels);
            return PixelOps.Asset(svg, Path.GetFileNameWithoutExtension(path));
        }
        StorageFile file;
        try { file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)); }
        catch (Exception e) { throw ImageImportException.Unreadable(e); }
        using var stream = await file.OpenReadAsync();
        var image = await DecodeStreamAsync(stream, remainingPixels);
        return PixelOps.Asset(image, Path.GetFileNameWithoutExtension(path));
    }

    private static async Task<RasterImage> DecodeSvgAsync(string path, long remainingPixels)
    {
        string xml;
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            if (info.Length > 32 * 1024 * 1024) throw ImageImportException.TooLarge();
            xml = await File.ReadAllTextAsync(info.FullName);
        }
        catch (ImageImportException) { throw; }
        catch (Exception e) { throw ImageImportException.Unreadable(e); }

        try
        {
            var (width, height) = SvgSize(xml);
            if (width > 30_000 || height > 30_000 || (long)width * height > remainingPixels) throw ImageImportException.TooLarge();
            var device = CanvasDevice.GetSharedDevice();
            using var document = CanvasSvgDocument.LoadFromXml(device, xml);
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                drawing.DrawSvg(document, new Windows.Foundation.Size(width, height));
            }
            var bgra = target.GetPixelBytes();
            for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            return RasterImage.FromPixels(width, height, bgra);
        }
        catch (ImageImportException) { throw; }
        catch (Exception e) { throw new ImageImportException("The SVG could not be rendered. Check that it is a valid SVG image.", e); }
    }

    private static (int Width, int Height) SvgSize(string xml)
    {
        var root = XDocument.Parse(xml, LoadOptions.None).Root;
        if (root == null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
            throw ImageImportException.Unsupported();
        var viewBox = ParseViewBox(root.Attribute("viewBox")?.Value);
        double? width = ParseSvgLength(root.Attribute("width")?.Value);
        double? height = ParseSvgLength(root.Attribute("height")?.Value);
        if (width == null && height != null && viewBox is { } vb1) width = height * vb1.Width / vb1.Height;
        if (height == null && width != null && viewBox is { } vb2) height = width * vb2.Height / vb2.Width;
        width ??= viewBox?.Width ?? 300;
        height ??= viewBox?.Height ?? 150;
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width.Value) || !double.IsFinite(height.Value))
            throw ImageImportException.Unsupported();
        return ((int)Math.Ceiling(width.Value), (int)Math.Ceiling(height.Value));
    }

    private static (double Width, double Height)? ParseViewBox(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 4 || !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) || !(width > 0) || !(height > 0)) return null;
        return (width, height);
    }

    private static double? ParseSvgLength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimEnd().EndsWith('%')) return null;
        text = text.Trim();
        int split = 0;
        while (split < text.Length && (char.IsDigit(text[split]) || text[split] is '+' or '-' or '.' or 'e' or 'E')) split++;
        if (split == 0 || !double.TryParse(text[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var unit = text[split..].Trim().ToLowerInvariant();
        var factor = unit switch { "" or "px" => 1, "pt" => 96.0 / 72, "pc" => 16, "in" => 96, "cm" => 96 / 2.54, "mm" => 96 / 25.4, "q" => 96 / 101.6, _ => double.NaN };
        return double.IsFinite(factor) ? value * factor : null;
    }

    public static async Task<RasterImage> DecodeBytesAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        return await DecodeStreamAsync(stream, 100_000_000);
    }

    private static async Task<RasterImage> DecodeStreamAsync(IRandomAccessStream stream, long remainingPixels)
    {
        BitmapDecoder decoder;
        try { decoder = await BitmapDecoder.CreateAsync(stream); }
        catch (Exception e) { throw ImageImportException.Unreadable(e); }
        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        if (width == 0 || height == 0) throw ImageImportException.Unreadable();
        if (width > 30_000 || height > 30_000 || (long)width * height > remainingPixels) throw ImageImportException.TooLarge();
        PixelDataProvider pixels;
        try
        {
            pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        }
        catch (Exception e) { throw ImageImportException.Unreadable(e); }
        var bytes = pixels.DetachPixelData();
        return RasterImage.FromPixels((int)width, (int)height, bytes);
    }
}
