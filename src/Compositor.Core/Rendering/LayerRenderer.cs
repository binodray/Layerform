using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>
/// A bitmap being rendered into, with the mapping from document pixels to its device pixels. Stands in for the
/// Core Graphics bitmap contexts the Mac app draws layers into.
/// </summary>
public sealed class RenderSurface : IDisposable
{
    public SKBitmap Bitmap { get; }
    public SKCanvas Canvas { get; }
    public Affine DocumentToDevice { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    public SKRectI DeviceBounds => new(0, 0, Width, Height);
    private readonly bool ownsBitmap;

    public RenderSurface(int width, int height, Affine documentToDevice)
        : this(PixelOps.NewRgba(Math.Max(1, width), Math.Max(1, height)), documentToDevice, true) { }

    public RenderSurface(SKBitmap bitmap, Affine documentToDevice, bool ownsBitmap = false)
    {
        Bitmap = bitmap;
        Canvas = new SKCanvas(bitmap);
        DocumentToDevice = documentToDevice;
        this.ownsBitmap = ownsBitmap;
    }

    /// <summary>A transparent surface laid out exactly like this one.</summary>
    public RenderSurface Sibling() => new(Width, Height, DocumentToDevice);

    /// <summary>Device pixels per document pixel along x.</summary>
    public double DeviceScale => Math.Sqrt(DocumentToDevice.A * DocumentToDevice.A + DocumentToDevice.B * DocumentToDevice.B);

    /// <summary>The document area this surface covers (upright bounds).</summary>
    public RectD DocumentRegion => new RectD(0, 0, Width, Height).Apply(DocumentToDevice.Inverted());

    public Span<byte> Pixels
    {
        get { Canvas.Flush(); return Bitmap.GetPixelSpan(); }
    }

    public void Dispose()
    {
        Canvas.Dispose();
        if (ownsBitmap) Bitmap.Dispose();
    }
}

/// <summary>A clip that multiplies what is drawn by coverage (Core Graphics' clip(to:mask:)).</summary>
public abstract class ClipMask
{
    /// <summary>Multiplies the destination by this clip's coverage over <paramref name="deviceRect"/> (DstIn).</summary>
    public abstract void ApplyDstIn(SKCanvas canvas, RenderSurface surface, SKRect deviceRect);
}

/// <summary>A gray mask stretched over a transform's rectangle; outside the rectangle nothing shows.</summary>
public sealed class TransformedMaskClip : ClipMask
{
    public MaskImage Mask { get; }
    public LayerTransform Transform { get; }
    public TransformedMaskClip(MaskImage mask, LayerTransform transform) { Mask = mask; Transform = transform; }

    public override void ApplyDstIn(SKCanvas canvas, RenderSurface surface, SKRect deviceRect)
    {
        var toDevice = LayerTransform.PixelToDocument(Transform, Mask.Width, Mask.Height).Concat(surface.DocumentToDevice);
        double drawnWidth = Transform.Size.Width * surface.DeviceScale;
        var sampling = Transform.Sampling;
        var (image, level) = sampling == LayerSampling.Nearest ? (Mask, 0)
            : DownsampleCache.Mask(Mask, DownsampleCache.Level(drawnWidth / Math.Max(1, Mask.Width)));
        var matrix = Affine.Scale(1 << level, 1 << level).Concat(toDevice);
        double finalFactor = drawnWidth / Math.Max(1, Mask.Width) * (1 << level);
        using var shader = SKShader.CreateImage(image.AlphaImage, SKShaderTileMode.Decal, SKShaderTileMode.Decal,
            LayerRenderer.Sampling(sampling, finalFactor), matrix.ToSK());
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstIn, IsAntialias = sampling != LayerSampling.Nearest };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawRect(deviceRect, paint);
        canvas.Restore();
    }
}

/// <summary>Coverage already rendered at the surface's device pixels (a live mask source's alpha).</summary>
public sealed class DeviceCoverageClip : ClipMask
{
    public SKImage Alpha { get; }
    public DeviceCoverageClip(SKImage alpha) { Alpha = alpha; }
    public override void ApplyDstIn(SKCanvas canvas, RenderSurface surface, SKRect deviceRect)
    {
        using var shader = SKShader.CreateImage(Alpha, SKShaderTileMode.Decal, SKShaderTileMode.Decal);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstIn };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawRect(deviceRect, paint);
        canvas.Restore();
    }
}

/// <summary>Draws layers into render surfaces (LayerRenderer.swift).</summary>
public static class LayerRenderer
{
    public static readonly SKCubicResampler HighQuality = SKCubicResampler.CatmullRom;

    /// <summary>The filter for the last resample, <paramref name="finalFactor"/> device pixels per (reduced) image pixel.
    /// Shrinking uses bilinear (the sharp halvings did the heavy reduction); enlarging keeps the layer's setting.</summary>
    public static SKSamplingOptions Sampling(LayerSampling sampling, double finalFactor)
    {
        if (sampling == LayerSampling.Nearest) return new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        if (finalFactor <= 1 || sampling == LayerSampling.Smooth) return new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        return new SKSamplingOptions(HighQuality);
    }

    /// <summary>The device-space quad a transform covers, as upright bounds clipped to the surface.</summary>
    public static SKRect DeviceBounds(RenderSurface surface, LayerTransform transform, float outset = 2)
    {
        var bounds = new RectD(transform.Origin.X, transform.Origin.Y, transform.Size.Width, transform.Size.Height);
        var corners = transform.Corners().Select(surface.DocumentToDevice.Apply).ToArray();
        double x0 = corners.Min(p => p.X), y0 = corners.Min(p => p.Y), x1 = corners.Max(p => p.X), y1 = corners.Max(p => p.Y);
        var rect = new SKRect((float)x0 - outset, (float)y0 - outset, (float)x1 + outset, (float)y1 + outset);
        if (!rect.IntersectsWith(new SKRect(0, 0, surface.Width, surface.Height))) return SKRect.Empty;
        rect.Intersect(new SKRect(0, 0, surface.Width, surface.Height));
        _ = bounds;
        return rect;
    }

    /// <summary>
    /// Draws <paramref name="image"/> placed by <paramref name="transform"/>, with opacity, blend mode, the layer's own
    /// mask (stretched over the layer's pixel grid) and any clips — Core Graphics' clipped, blended draw.
    /// </summary>
    public static void Draw(RenderSurface surface, RasterImage image, LayerTransform transform, double opacity = 1,
        LayerBlendMode blend = LayerBlendMode.Normal, MaskImage? mask = null, IReadOnlyList<ClipMask>? clips = null)
    {
        var deviceRect = DeviceBounds(surface, transform);
        if (deviceRect.IsEmpty || opacity <= 0) return;
        var canvas = surface.Canvas;
        bool masked = mask != null || (clips != null && clips.Count > 0);
        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        if (!masked)
        {
            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), BlendMode = blend.ToSkia(), IsAntialias = transform.Sampling != LayerSampling.Nearest };
            DrawImageOnly(canvas, surface, image, transform, paint, deviceRect);
            return;
        }
        using var layerPaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), BlendMode = blend.ToSkia() };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(deviceRect);
        canvas.SaveLayer(deviceRect, layerPaint);
        using (var plain = new SKPaint { IsAntialias = transform.Sampling != LayerSampling.Nearest })
            DrawImageOnly(canvas, surface, image, transform, plain, deviceRect);
        if (mask != null) new TransformedMaskClip(mask, transform).ApplyDstIn(canvas, surface, deviceRect);
        if (clips != null) foreach (var clip in clips) clip.ApplyDstIn(canvas, surface, deviceRect);
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>The image alone through the paint, using a sharp reduction when it lands much smaller than its pixels.</summary>
    public static void DrawImageOnly(SKCanvas canvas, RenderSurface surface, RasterImage image, LayerTransform transform, SKPaint paint, SKRect deviceRect)
    {
        double drawnWidth = transform.Size.Width * surface.DeviceScale;
        var (source, level) = transform.Sampling == LayerSampling.Nearest ? (image, 0)
            : DownsampleCache.Image(image, DownsampleCache.Level(drawnWidth / Math.Max(1, image.Width)));
        double finalFactor = drawnWidth / Math.Max(1, image.Width) * (1 << level);
        var matrix = Affine.Scale(1 << level, 1 << level)
            .Concat(LayerTransform.PixelToDocument(transform, image.Width, image.Height))
            .Concat(surface.DocumentToDevice);
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(deviceRect);
        canvas.SetMatrix(matrix.ToSK());
        canvas.DrawImage(source.Image, 0, 0, Sampling(transform.Sampling, finalFactor), paint);
        canvas.Restore();
    }

    /// <summary>A mask's coverage placed by a transform, drawn as values into an opaque RGBA (gray) surface.</summary>
    public static void DrawCoverage(RenderSurface surface, MaskImage mask, LayerTransform transform)
    {
        var deviceRect = DeviceBounds(surface, transform);
        if (deviceRect.IsEmpty) return;
        var canvas = surface.Canvas;
        var gray = mask.GrayImage;
        var matrix = LayerTransform.PixelToDocument(transform, mask.Width, mask.Height).Concat(surface.DocumentToDevice);
        using var paint = new SKPaint { IsAntialias = transform.Sampling != LayerSampling.Nearest, BlendMode = SKBlendMode.SrcOver };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.SetMatrix(matrix.ToSK());
        canvas.DrawImage(gray, 0, 0, Sampling(transform.Sampling, transform.Size.Width * surface.DeviceScale / Math.Max(1, mask.Width)), paint);
        canvas.Restore();
    }

    /// <summary>Multiplies the whole surface by clips' coverage (for adjustment results and group draws).</summary>
    public static byte[]? CoverageBuffer(RenderSurface surface, IReadOnlyList<ClipMask> clips)
    {
        if (clips.Count == 0) return null;
        using var alpha = PixelOps.NewAlpha(surface.Width, surface.Height, 255);
        using (var canvas = new SKCanvas(alpha))
        {
            var rect = new SKRect(0, 0, surface.Width, surface.Height);
            foreach (var clip in clips) clip.ApplyDstIn(canvas, surface, rect);
        }
        var result = new byte[surface.Width * surface.Height];
        var span = alpha.GetPixelSpan();
        for (int y = 0; y < surface.Height; y++) span.Slice(y * alpha.RowBytes, surface.Width).CopyTo(result.AsSpan(y * surface.Width));
        return result;
    }
}
