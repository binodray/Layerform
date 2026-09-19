using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

public sealed class ExportException : Exception
{
    public ExportException(string message) : base(message) { }
    public static ExportException TooLarge() => new("Image export supports canvases up to 100 megapixels and 30,000 pixels per side.");
    public static ExportException Render() => new("The canvas could not be rendered. Try a smaller canvas.");
}

/// <summary>Composites a saved project snapshot exactly as the Mac ImageExporter does.</summary>
public sealed class SnapshotCompositeSource : ICompositeSource
{
    private readonly ProjectSnapshot snapshot;
    private readonly Dictionary<Guid, ProjectLayerRecord> records;
    public IReadOnlyList<Guid> RenderOrder { get; }

    public SnapshotCompositeSource(ProjectSnapshot snapshot)
    {
        this.snapshot = snapshot;
        records = snapshot.Manifest.Layers.ToDictionary(l => l.Id);
        RenderOrder = LayerHierarchy.VisibleLayers(snapshot.Manifest.Layers).Select(l => l.Id).ToList();
    }

    public Guid? Parent(Guid id) => records.TryGetValue(id, out var r) ? r.ParentId : null;
    public Guid? MaskSource(Guid id) => records.TryGetValue(id, out var r) ? r.MaskSourceId : null;
    public LayerAdjustment? Adjustment(Guid id) => records.TryGetValue(id, out var r) ? r.Adjustment : null;
    public double Opacity(Guid id) => records.TryGetValue(id, out var r) ? r.Opacity ?? 1 : 1;
    public LayerBlendMode Blend(Guid id) => records.TryGetValue(id, out var r) ? r.BlendMode ?? LayerBlendMode.Normal : LayerBlendMode.Normal;

    public ClipMask? FolderClip(Guid folderId)
    {
        if (!records.TryGetValue(folderId, out var folder)) return null;
        return snapshot.Mask(folder)?.EnabledImage is { } image ? new TransformedMaskClip(image, folder.Transform) : null;
    }

    public ClipMask? AdjustmentClip(Guid id) => FolderClip(id);

    public void DrawOwn(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips)
    {
        if (!records.TryGetValue(id, out var layer) || !snapshot.Images.TryGetValue(id, out var asset)) return;
        var mask = snapshot.Mask(layer) is { } owned
            ? MaskPlacement.ClipImage(owned, owned.Placement, layer.Transform, asset.Image.Width, asset.Image.Height)
            : null;
        LayerRenderer.Draw(surface, asset.Image, layer.Transform, layer.Opacity ?? 1, layer.BlendMode ?? LayerBlendMode.Normal, mask, clips);
    }
}

public sealed record ExportRaster(RasterImage Image, double Resolution);

public static class ImageExporter
{
    public static ExportRaster Render(ProjectSnapshot snapshot)
    {
        int width = snapshot.Manifest.Width, height = snapshot.Manifest.Height;
        if (width < 1 || width > 30_000 || height < 1 || height > 30_000 || (long)width * height > 100_000_000) throw ExportException.TooLarge();
        foreach (var layer in snapshot.Manifest.Layers)
        {
            if ((layer.ImageFile != null && !snapshot.Images.ContainsKey(layer.Id)) || (layer.MaskFile != null && !snapshot.Masks.ContainsKey(layer.Id)))
                throw ProjectException.MissingImage();
        }
        LiveMaskGraph.Validate(snapshot.Manifest.Layers);
        var bitmap = PixelOps.NewRgba(width, height);
        using (var surface = new RenderSurface(bitmap, Affine.Identity))
        {
            CompositeRenderer.Render(new SnapshotCompositeSource(snapshot), surface);
            surface.Canvas.Flush();
        }
        return new ExportRaster(RasterImage.Adopt(bitmap), snapshot.Manifest.Resolution ?? 72);
    }

    public static byte[] PngData(ProjectSnapshot snapshot)
    {
        var raster = Render(snapshot);
        return Codecs.EncodePng(raster.Image, raster.Resolution);
    }

    /// <summary>Flattens onto an opaque matte and encodes JPEG (quality 0–1), with the document resolution.</summary>
    public static byte[] Jpeg(ExportRaster raster, double quality, PaletteColor matte)
    {
        var image = raster.Image;
        using var flattened = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(matte.ToSK());
            canvas.DrawImage(image.Image, 0, 0);
        }
        using var pixmap = flattened.PeekPixels();
        return Codecs.EncodeJpeg(pixmap, (int)Math.Round(Math.Clamp(quality, 0, 1) * 100), raster.Resolution);
    }

    public static void WriteAtomically(byte[] data, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, data);
        try { File.Move(temporary, full, true); }
        catch { File.Delete(temporary); throw; }
    }

    public static void ExportPng(ProjectSnapshot snapshot, string path) => WriteAtomically(PngData(snapshot), path);
}
