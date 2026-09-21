using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Editing;

/// <summary>
/// Selection coverage for one region of the document (Selection.swift). Applied as a clip, soft edges blend partially;
/// with no coverage (an explicit empty selection) it clips everything away.
/// </summary>
public sealed class SelectionClip
{
    public RectD Rect { get; }
    public MaskImage? Coverage { get; }
    public SelectionClip(RectD rect, MaskImage? coverage) { Rect = rect; Coverage = coverage; }

    /// <summary>Coverage (0–255) at a document point, nearest pixel; 0 outside the region.</summary>
    public byte At(double x, double y)
    {
        if (Coverage == null) return 0;
        int ix = (int)Math.Floor(x - Rect.X), iy = (int)Math.Floor(y - Rect.Y);
        if (ix < 0 || iy < 0 || ix >= Coverage.Width || iy >= Coverage.Height) return 0;
        return Coverage.ValueAt(ix, iy);
    }

    /// <summary>A clip for compositing: the coverage placed over its document rectangle.</summary>
    public Rendering.ClipMask? ToClipMask() => Coverage is { } c
        ? new Rendering.TransformedMaskClip(c, new LayerTransform(Rect.Origin, Rect.Size, Sampling: LayerSampling.Nearest))
        : null;
}

public static class SelectionRaster
{
    public static SKPaint FillPaint(bool antialias) => new() { Color = SKColors.White, IsAntialias = antialias, Style = SKPaintStyle.Fill };

    /// <summary>Coverage for just the selected part of the canvas, ready to clip edits.</summary>
    public static SelectionClip Clip(this DocumentSelection selection, SizeD canvas)
    {
        var spread = Math.Ceiling(selection.Feather * 2) + 1;
        var region = selection.Bounds.Inset(-spread, -spread).Integral.Intersect(new RectD(0, 0, canvas.Width, canvas.Height));
        if (selection.IsEmpty || region.IsNull || region.Width < 1 || region.Height < 1) return new SelectionClip(RectD.Zero, null);
        using var alpha = PixelOps.NewAlpha((int)region.Width, (int)region.Height);
        using (var canvasSk = new SKCanvas(alpha))
        {
            canvasSk.Translate((float)-region.X, (float)-region.Y);
            using var paint = FillPaint(selection.Antialiased);
            canvasSk.DrawPath(selection.SharedPath, paint);
        }
        return new SelectionClip(region, Blurred(alpha, selection.Feather));
    }

    /// <summary>Grayscale coverage at document resolution (white = selected).</summary>
    public static MaskImage Coverage(this DocumentSelection selection, int width, int height)
    {
        using var alpha = PixelOps.NewAlpha(width, height);
        using (var canvas = new SKCanvas(alpha))
        {
            using var paint = FillPaint(selection.Antialiased);
            canvas.DrawPath(selection.SharedPath, paint);
        }
        return Blurred(alpha, selection.Feather);
    }

    private static MaskImage Blurred(SKBitmap alpha, double feather)
    {
        if (feather <= 0) return PixelOps.AlphaToMaskCopy(alpha);
        using var blurred = PixelOps.NewAlpha(alpha.Width, alpha.Height);
        using (var canvas = new SKCanvas(blurred))
        using (var image = SKImage.FromBitmap(alpha))
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur((float)(feather / 2), (float)(feather / 2)), BlendMode = SKBlendMode.Src })
            canvas.DrawImage(image, 0, 0, paint);
        return PixelOps.AlphaToMaskCopy(blurred);
    }

    /// <summary>Selection coverage rasterized on an image's own pixel grid (PixelAdjust.coverage).</summary>
    public static byte[] CoverageOnGrid(SelectionClip clip, int width, int height, Affine pixelToDocument)
    {
        var result = new byte[width * height];
        if (clip.Coverage == null) return result;
        bool axisAligned = Math.Abs(pixelToDocument.B) < 1e-9 && Math.Abs(pixelToDocument.C) < 1e-9;
        if (axisAligned && Math.Abs(pixelToDocument.A - 1) < 1e-9 && Math.Abs(pixelToDocument.D - 1) < 1e-9)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    result[y * width + x] = clip.At(x + 0.5 + pixelToDocument.Tx, y + 0.5 + pixelToDocument.Ty);
            return result;
        }
        // General placement: draw the coverage through the inverse mapping, smoothly.
        using var alpha = PixelOps.NewAlpha(width, height);
        using (var canvas = new SKCanvas(alpha))
        {
            var toPixels = pixelToDocument.Inverted();
            var placed = Affine.Translation(clip.Rect.X, clip.Rect.Y).Concat(toPixels);
            canvas.SetMatrix(placed.ToSK());
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
            canvas.DrawImage(clip.Coverage.AlphaImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        var span = alpha.GetPixelSpan();
        for (int y = 0; y < height; y++) span.Slice(y * alpha.RowBytes, width).CopyTo(result.AsSpan(y * width));
        return result;
    }

    public static SKPath Rectangle(RectD rect)
    {
        var path = new SKPath();
        path.AddRect(rect.ToSK());
        return path;
    }
}
