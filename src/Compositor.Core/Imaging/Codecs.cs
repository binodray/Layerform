using System.Buffers.Binary;
using SkiaSharp;

namespace Compositor.Imaging;

/// <summary>PNG facts read straight from the IHDR chunk.</summary>
public readonly record struct PngHeader(int Width, int Height, int BitDepth, int ColorType)
{
    /// <summary>PNG colour type 0: grayscale without alpha.</summary>
    public bool IsGray => ColorType == 0;
}

public static class Codecs
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

    public static PngHeader? ReadPngHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 33 || !IsPng(data)) return null;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        return new PngHeader(width, height, data[24], data[25]);
    }

    /// <summary>Decodes any supported image to RGBA premultiplied without colour conversion (sRGB assumed).</summary>
    public static RasterImage DecodeRgba(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unreadable image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Image decode failed: {result}.");
        }
        return RasterImage.Adopt(bitmap);
    }

    /// <summary>Decodes a gray PNG (or any image, by its luminance) to 8-bit gray coverage.</summary>
    public static MaskImage DecodeGray(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unreadable mask.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Mask decode failed: {result}.");
        }
        return MaskImage.Adopt(bitmap);
    }

    public static byte[] EncodePng(RasterImage image, double? resolution = null)
    {
        using var pixmap = image.Image.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6))
            ?? throw new InvalidOperationException("PNG encoding failed.");
        var bytes = data.ToArray();
        return resolution is { } dpi ? WithPngResolution(bytes, dpi) : bytes;
    }

    /// <summary>Masks are written as 8-bit grayscale PNGs without alpha, which the Mac app requires.</summary>
    public static byte[] EncodeGrayPng(MaskImage mask)
    {
        using var pixmap = mask.GrayImage.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6))
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return data.ToArray();
    }

    public static byte[] EncodeJpeg(SKPixmap pixmap, int quality, double resolution)
    {
        using var data = pixmap.Encode(new SKJpegEncoderOptions(Math.Clamp(quality, 0, 100), SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
            ?? throw new InvalidOperationException("JPEG encoding failed.");
        return WithJpegResolution(data.ToArray(), resolution);
    }

    /// <summary>Adds (or replaces) the pHYs chunk so the PNG records pixels per inch.</summary>
    public static byte[] WithPngResolution(byte[] png, double dpi)
    {
        if (!IsPng(png) || !(dpi > 0)) return png;
        uint perMeter = (uint)Math.Round(dpi / 0.0254);
        var output = new MemoryStream(png.Length + 21);
        output.Write(png, 0, 8);
        int offset = 8;
        bool written = false;
        while (offset + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = png.AsSpan(offset + 4, 4);
            int total = 12 + length;
            if (offset + total > png.Length) break;
            bool isPhys = type.SequenceEqual("pHYs"u8);
            if (!isPhys) output.Write(png, offset, total);
            if (!written && type.SequenceEqual("IHDR"u8))
            {
                Span<byte> body = stackalloc byte[9];
                BinaryPrimitives.WriteUInt32BigEndian(body, perMeter);
                BinaryPrimitives.WriteUInt32BigEndian(body[4..], perMeter);
                body[8] = 1;
                WriteChunk(output, "pHYs"u8, body);
                written = true;
            }
            offset += total;
        }
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        output.Write(type);
        output.Write(body);
        uint crc = Crc32(type, body);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint[]? crcTable;
    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var table = crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        uint crc = 0xFFFFFFFFu;
        foreach (var x in a) crc = table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Sets the JFIF APP0 density to pixels per inch (inserting a JFIF header when missing).</summary>
    public static byte[] WithJpegResolution(byte[] jpeg, double dpi)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || !(dpi > 0)) return jpeg;
        ushort density = (ushort)Math.Clamp(Math.Round(dpi), 1, 65535);
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.Length > 18 && jpeg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8))
        {
            var copy = (byte[])jpeg.Clone();
            copy[13] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(14, 2), density);
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(16, 2), density);
            return copy;
        }
        var app0 = new byte[] { 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 1, 0, 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(14, 2), density);
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(16, 2), density);
        var output = new byte[jpeg.Length + app0.Length];
        output[0] = 0xFF; output[1] = 0xD8;
        app0.CopyTo(output, 2);
        Array.Copy(jpeg, 2, output, 2 + app0.Length, jpeg.Length - 2);
        return output;
    }

    /// <summary>Reads pHYs pixels-per-inch from a PNG, when present.</summary>
    public static double? ReadPngResolution(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png)) return null;
        int offset = 8;
        while (offset + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset, 4));
            if (length < 0 || offset + 12 + length > png.Length) return null;
            if (png.Slice(offset + 4, 4).SequenceEqual("pHYs"u8) && length == 9)
            {
                uint x = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8, 4));
                return png[offset + 16] == 1 ? x * 0.0254 : null;
            }
            if (png.Slice(offset + 4, 4).SequenceEqual("IDAT"u8)) return null;
            offset += 12 + length;
        }
        return null;
    }
}
