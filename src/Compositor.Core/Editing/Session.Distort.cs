using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

/// <summary>Free distortion (Distort.swift): the four corners move independently and Apply resamples the pixels.</summary>
public static class DistortWarp
{
    public static PointD[] Corners(LayerTransform transform) => transform.Corners();

    /// <summary>Four finite corners making a convex, non-degenerate shape.</summary>
    public static bool IsUsable(PointD[] corners)
    {
        if (corners.Length != 4 || corners.Any(c => !c.IsFinite || Math.Abs(c.X) > 1_000_000 || Math.Abs(c.Y) > 1_000_000)) return false;
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i]; var b = corners[(i + 1) % 4]; var c = corners[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) <= 0.01) return false;
            if (sign == 0) sign = cross < 0 ? -1 : 1;
            else if ((cross < 0) != (sign < 0)) return false;
        }
        return true;
    }

    /// <summary>The unit square (corners in handle order) mapped onto <paramref name="c"/>, as a 3×3 matrix.</summary>
    public static double[] Homography(PointD[] c)
    {
        double sx = c[0].X - c[1].X + c[2].X - c[3].X, sy = c[0].Y - c[1].Y + c[2].Y - c[3].Y;
        double g = 0, h = 0;
        if (Math.Abs(sx) > 1e-9 || Math.Abs(sy) > 1e-9)
        {
            double dx1 = c[1].X - c[2].X, dx2 = c[3].X - c[2].X, dy1 = c[1].Y - c[2].Y, dy2 = c[3].Y - c[2].Y;
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) > 1e-12) { g = (sx * dy2 - dx2 * sy) / den; h = (dx1 * sy - sx * dy1) / den; }
        }
        double a = c[1].X - c[0].X + g * c[1].X, b = c[3].X - c[0].X + h * c[3].X, x0 = c[0].X;
        double d = c[1].Y - c[0].Y + g * c[1].Y, e = c[3].Y - c[0].Y + h * c[3].Y, y0 = c[0].Y;
        return new[] { a, b, x0, d, e, y0, g, h, 1 };
    }

    public static PointD Map(double[] m, PointD p)
    {
        double w = m[6] * p.X + m[7] * p.Y + m[8];
        return new PointD((m[0] * p.X + m[1] * p.Y + m[2]) / w, (m[3] * p.X + m[4] * p.Y + m[5]) / w);
    }

    /// <summary>Where each corner of the image's own pixels lands: a flipped layer shows them mirrored.</summary>
    private static PointD[] ImageCorners(PointD[] corners, bool flipX, bool flipY)
    {
        PointD Corner(int x, int y)
        {
            int u = flipX ? 1 - x : x, v = flipY ? 1 - y : y;
            return corners[new[] { 0, 1, 3, 2 }[v * 2 + u]];
        }
        return new[] { Corner(0, 0), Corner(1, 0), Corner(1, 1), Corner(0, 1) };
    }

    private static SKMatrix Matrix(double[] m) => new((float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7], (float)m[8]);

    /// <summary>The image resampled so its corners land on <paramref name="corners"/>, over the shape's whole-pixel bounds.</summary>
    public static (RasterImage Image, LayerTransform Transform) Warp(RasterImage image, LayerTransform transform, PointD[] corners, double? limit = null)
    {
        var (bounds, placed, factor, matrix) = Prepare(image.Width, image.Height, transform, corners, limit);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * factor)), height = Math.Max(1, (int)Math.Ceiling(bounds.Height * factor));
        var bitmap = PixelOps.NewRgba(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.SetMatrix(matrix);
            canvas.DrawImage(image.Image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        return (RasterImage.Adopt(bitmap), placed);
    }

    public static (MaskImage Image, LayerTransform Transform) WarpMaskPlain(MaskImage mask, LayerTransform transform, PointD[] corners, double? limit = null)
    {
        var (bounds, placed, factor, matrix) = Prepare(mask.Width, mask.Height, transform, corners, limit);
        if (mask.IsUniform1x1) return (mask, placed);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * factor)), height = Math.Max(1, (int)Math.Ceiling(bounds.Height * factor));
        using var alpha = PixelOps.NewAlpha(width, height);
        using (var canvas = new SKCanvas(alpha))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.SetMatrix(matrix);
            canvas.DrawImage(mask.AlphaImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        return (PixelOps.AlphaToMaskCopy(alpha), placed);
    }

    private static (RectD Bounds, LayerTransform Placed, double Factor, SKMatrix Matrix) Prepare(int w, int h, LayerTransform transform, PointD[] corners, double? limit)
    {
        if (!IsUsable(corners)) throw ProjectException.Invalid();
        double minX = Math.Floor(corners.Min(c => c.X)), minY = Math.Floor(corners.Min(c => c.Y));
        var bounds = new RectD(minX, minY, Math.Ceiling(corners.Max(c => c.X)) - minX, Math.Ceiling(corners.Max(c => c.Y)) - minY);
        if (bounds.Width < 1 || bounds.Height < 1 || bounds.Width > 30_000 || bounds.Height > 30_000 || bounds.Width * bounds.Height > 100_000_000)
            throw ProjectException.TooLarge();
        var placed = new LayerTransform(bounds.Origin, bounds.Size, Sampling: transform.Sampling);
        double factor = limit is { } l ? Math.Min(1, l / Math.Max(bounds.Width, bounds.Height)) : 1;
        var target = ImageCorners(corners, transform.FlipX, transform.FlipY)
            .Select(p => new PointD((p.X - bounds.X) * factor, (p.Y - bounds.Y) * factor)).ToArray();
        var hom = Homography(target);
        // Image pixels to the unit square, then the perspective onto the target corners.
        var m = new[] { hom[0] / w, hom[1] / h, hom[2], hom[3] / w, hom[4] / h, hom[5], hom[6] / w, hom[7] / h, hom[8] };
        return (bounds, placed, factor, Matrix(m));
    }

    /// <summary>A full-resolution warp cropped to its visible pixels; <c>Crop</c> is in the warp's pixels.</summary>
    public static (RasterImage Image, LayerTransform Transform, SKRectI Crop) WarpTrimmed(RasterImage image, LayerTransform transform, PointD[] corners)
    {
        var warped = Warp(image, transform, corners);
        var full = new SKRectI(0, 0, warped.Image.Width, warped.Image.Height);
        var (l, t, r, b) = PixelOps.AlphaBounds(warped.Image.Pixels, warped.Image.Width, warped.Image.Height, warped.Image.RowBytes);
        var crop = new SKRectI(l, t, r, b);
        if (crop.Width < 1 || crop.Height < 1 || crop == full) return (warped.Image, warped.Transform, full);
        var placed = warped.Transform with
        {
            Origin = warped.Transform.Origin.Offset(crop.Left, crop.Top), Size = new SizeD(crop.Width, crop.Height),
        };
        return (warped.Image.Crop(crop), placed, crop);
    }

    /// <summary>Where a placement's corners land under the perspective taking <paramref name="transform"/> to <paramref name="corners"/>.</summary>
    public static PointD[] Carried(LayerTransform placement, LayerTransform transform, PointD[] corners)
    {
        var toUnit = Affine.Translation(-0.5, -0.5).Concat(Affine.Scale(transform.Size.Width, transform.Size.Height))
            .Concat(Affine.Rotation(transform.Radians)).Concat(Affine.Translation(transform.Center.X, transform.Center.Y)).Inverted();
        var map = Homography(corners);
        return placement.Corners().Select(p => Map(map, toUnit.Apply(p))).ToArray();
    }

    /// <summary>A mask warped with its edge tone outside the shape instead of black.</summary>
    public static (MaskImage Image, LayerTransform Transform) WarpMask(MaskImage mask, LayerTransform transform, PointD[] corners, byte background, double? limit = null)
    {
        var warped = WarpMaskPlain(mask, transform, corners, limit);
        if (background == 0 || ReferenceEquals(warped.Image, mask)) return warped;
        int w = warped.Image.Width, h = warped.Image.Height;
        double sx = w / warped.Transform.Size.Width, sy = h / warped.Transform.Size.Height;
        using var outside = PixelOps.NewAlpha(w, h);
        using (var canvas = new SKCanvas(outside))
        using (var path = new SKPath())
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            path.AddPoly(corners.Select(c => new SKPoint((float)((c.X - warped.Transform.Origin.X) * sx), (float)((c.Y - warped.Transform.Origin.Y) * sy))).ToArray(), true);
            canvas.DrawPath(path, paint);
        }
        var inside = outside.GetPixelSpan();
        var bytes = warped.Image.CopyPixels();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double cover = inside[y * outside.RowBytes + x] / 255.0;
                int i = y * w + x;
                bytes[i] = (byte)Math.Round(background + (bytes[i] - background) * cover);
            }
        return (MaskImage.FromPixels(w, h, bytes), warped.Transform);
    }

    /// <summary>Carries an outline drawn over the original pixels into the distorted shape.</summary>
    public static SKPath? MapPath(SKPath path, Affine pixelToDocument, SizeD pixelSize, LayerTransform transform, PointD[] corners)
    {
        if (!IsUsable(corners) || pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;
        var toPixels = pixelToDocument.Inverted();
        var map = Homography(corners);
        PointD Carry(SKPoint p)
        {
            var pixel = toPixels.Apply(new PointD(p.X, p.Y));
            double u = pixel.X / pixelSize.Width, v = pixel.Y / pixelSize.Height;
            if (transform.FlipX) u = 1 - u;
            if (transform.FlipY) v = 1 - v;
            return Map(map, new PointD(u, v));
        }
        var result = new SKPath { FillType = path.FillType };
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: result.MoveTo(Carry(points[0]).ToSK()); break;
                case SKPathVerb.Line: result.LineTo(Carry(points[1]).ToSK()); break;
                case SKPathVerb.Quad: result.QuadTo(Carry(points[1]).ToSK(), Carry(points[2]).ToSK()); break;
                case SKPathVerb.Conic:
                case SKPathVerb.Cubic:
                    // Flattened: a perspective does not keep conics exact anyway.
                    for (int i = 1; i <= 8; i++)
                    {
                        double t = i / 8.0;
                        var last = verb == SKPathVerb.Cubic ? points[3] : points[2];
                        var q = verb == SKPathVerb.Cubic ? CubicAt(points, t) : QuadAt(points, t);
                        result.LineTo(Carry(q).ToSK());
                        _ = last;
                    }
                    break;
                case SKPathVerb.Close: result.Close(); break;
            }
        }
        return result;
    }

    private static SKPoint QuadAt(SKPoint[] p, double t)
    {
        double u = 1 - t;
        return new SKPoint((float)(u * u * p[0].X + 2 * u * t * p[1].X + t * t * p[2].X), (float)(u * u * p[0].Y + 2 * u * t * p[1].Y + t * t * p[2].Y));
    }

    private static SKPoint CubicAt(SKPoint[] p, double t)
    {
        double u = 1 - t;
        return new SKPoint((float)(u * u * u * p[0].X + 3 * u * u * t * p[1].X + 3 * u * t * t * p[2].X + t * t * t * p[3].X),
                           (float)(u * u * u * p[0].Y + 3 * u * u * t * p[1].Y + 3 * u * t * t * p[2].Y + t * t * t * p[3].Y));
    }
}

public sealed record DistortPreview(RasterImage Image, MaskImage? Mask, LayerTransform Transform);

public sealed partial class EditorSession
{
    private readonly Dictionary<Guid, (PointD[] Corners, LayerTransform Draft, RasterImage Image, MaskImage? Mask, DistortPreview? Result)> distortPreviewCache = new();
    private (PointD[] Corners, LayerTransform Draft, MaskImage Mask, LayerTransform Layer, MaskImage? Result)? maskDistortPreviewCache;

    /// <summary>Ctrl-drag on a handle: the corners start moving freely; the edit then waits for Apply.</summary>
    public void BeginDistort()
    {
        if (TransformEditState is not { Corners: null } edit || !edit.Draft.IsValid) return;
        TransformEditState = edit with { Persistent = true, Corners = DistortWarp.Corners(edit.Draft) };
        InvalidateCanvas();
    }

    public void PreviewCorners(PointD[] corners)
    {
        if (TransformEditState?.Corners == null || !DistortWarp.IsUsable(corners)) return;
        TransformEditState = TransformEditState with { Corners = corners };
        InvalidateCanvas();
    }

    private (LayerTransform Transform, PointD[] Corners)? DistortTarget(ImageLayer layer, TransformEdit edit, PointD[] shape)
    {
        if (edit.Group is not { } group) return edit.LayerId == layer.Id ? (edit.Draft, shape) : null;
        if (!group.Originals.TryGetValue(layer.Id, out var original)) return null;
        var transform = original.Following(group.Box, edit.Draft);
        var corners = DistortWarp.Carried(transform, edit.Draft, shape);
        return DistortWarp.IsUsable(corners) ? (transform, corners) : null;
    }

    /// <summary>The layer warped into the pending distortion, at preview size, for the canvas.</summary>
    public DistortPreview? DistortPreviewFor(ImageLayer layer)
    {
        if (TransformEditState is not { Mask: false, Corners: { } shape } edit || layer.Asset is not { } asset
            || DistortTarget(layer, edit, shape) is not { } target) return null;
        var mask = layer.Mask?.EnabledImage;
        if (distortPreviewCache.TryGetValue(layer.Id, out var cache) && cache.Corners.SequenceEqual(target.Corners) && cache.Draft == target.Transform
            && ReferenceEquals(cache.Image, asset.Image) && ReferenceEquals(cache.Mask, mask)) return cache.Result;
        DistortPreview? result = null;
        try
        {
            var warped = DistortWarp.Warp(asset.Image, target.Transform, target.Corners, 2048);
            MaskImage? warpedMask = null;
            var owned = layer.Mask;
            if (owned is { Placement: null, IsLinked: true } && mask != null)
                warpedMask = DistortWarp.WarpMaskPlain(mask, target.Transform, target.Corners, 2048).Image;
            else if (owned is { IsLinked: true, Placement: { } placed })
            {
                var placement = placed.Following(layer.Transform, target.Transform);
                var carried = DistortWarp.Carried(placement, target.Transform, target.Corners);
                if (DistortWarp.IsUsable(carried))
                {
                    var moved = DistortWarp.WarpMask(owned.Asset.Image, placement, carried, MaskPlacement.Background(owned.Asset.Thumbnail), 2048);
                    warpedMask = MaskPlacement.ClipImage(new LayerMask(new MaskAsset(moved.Image, owned.Asset.Thumbnail), owned.IsEnabled), moved.Transform,
                        warped.Transform, warped.Image.Width, warped.Image.Height, 2048);
                }
            }
            else if (owned != null)
                warpedMask = MaskPlacement.ClipImage(owned, owned.Placement ?? layer.Transform, warped.Transform, warped.Image.Width, warped.Image.Height, 2048);
            result = new DistortPreview(warped.Image, warpedMask, warped.Transform);
        }
        catch { result = null; }
        distortPreviewCache[layer.Id] = (target.Corners, target.Transform, asset.Image, mask, result);
        return result;
    }

    public MaskImage? MaskDistortPreview(ImageLayer layer)
    {
        if (TransformEditState is not { Mask: true, Corners: { } corners } edit || edit.LayerId != layer.Id || layer.Mask is not { IsEnabled: true } owned) return null;
        if (maskDistortPreviewCache is { } cache && cache.Corners.SequenceEqual(corners) && cache.Draft == edit.Draft
            && ReferenceEquals(cache.Mask, owned.Asset.Image) && cache.Layer == layer.Transform) return cache.Result;
        int width = layer.Asset?.Image.Width ?? (int)Math.Round(layer.Size.Width), height = layer.Asset?.Image.Height ?? (int)Math.Round(layer.Size.Height);
        MaskImage? result = null;
        try
        {
            var moved = DistortWarp.WarpMask(owned.Asset.Image, edit.Draft, corners, MaskPlacement.Background(owned.Asset.Thumbnail), 2048);
            result = MaskPlacement.ClipImage(new LayerMask(new MaskAsset(moved.Image, owned.Asset.Thumbnail)), moved.Transform, layer.Transform, width, height, 2048);
        }
        catch { }
        maskDistortPreviewCache = (corners, edit.Draft, owned.Asset.Image, layer.Transform, result);
        return result;
    }

    /// <summary>Apply for a distortion: each layer's pixels and mask are resampled into its shape, as one undo step.</summary>
    public void CommitDistort(TransformEdit edit, PointD[] shape)
    {
        distortPreviewCache.Clear();
        var ids = edit.Group?.Originals.Keys.ToList() ?? new List<Guid> { edit.LayerId };
        BeginEdit(edit.Group == null ? "Distort" : "Distort Layers");
        foreach (var id in ids)
        {
            if (document?.Layer(id) is not { } layer || DistortTarget(layer, edit, shape) is not { } target) continue;
            try { Distort(layer, target.Transform, target.Corners); }
            catch (Exception e) { BrushError = e.Message; }
        }
        EndEdit();
    }

    private void Distort(ImageLayer layer, LayerTransform transform, PointD[] corners)
    {
        if (layer.Asset is not { } asset) return;
        var warped = DistortWarp.WarpTrimmed(asset.Image, transform, corners);
        var mask = layer.Mask;
        if (layer.Mask is { Placement: null, IsLinked: true } original)
        {
            var warpedMask = DistortWarp.WarpMaskPlain(original.Asset.Image, transform, corners);
            var maskAsset = ReferenceEquals(warpedMask.Image, original.Asset.Image) ? original.Asset : PixelOps.MaskAssetFrom(warpedMask.Image.Crop(warped.Crop));
            mask = original.Replacing(maskAsset);
        }
        else if (layer.Mask is { IsLinked: true, Placement: { } placed } linked)
        {
            var placement = placed.Following(layer.Transform, transform);
            var carried = DistortWarp.Carried(placement, transform, corners);
            if (DistortWarp.IsUsable(carried))
            {
                var moved = DistortWarp.WarpMask(linked.Asset.Image, placement, carried, MaskPlacement.Background(linked.Asset.Thumbnail));
                mask = new LayerMask(ReferenceEquals(moved.Image, linked.Asset.Image) ? linked.Asset : PixelOps.MaskAssetFrom(moved.Image),
                    linked.IsEnabled, moved.Transform, true);
            }
        }
        else if (layer.Mask is { } unlinked) mask = unlinked with { Placement = unlinked.Placement ?? layer.Transform };
        UpdateLayer(layer.Id, l => l with { Asset = PixelOps.Asset(warped.Image, layer.Name), Transform = warped.Transform, Mask = mask });
    }

    // MARK: Transforming selected pixels (FloatingSelection.swift)

    public bool CanTransformSelection => TransformEditState == null && CanEditPixels && !IsMaskSelected && Selection is { IsEmpty: false }
        && ActiveLayer?.Asset != null;

    /// <summary>Ctrl+T: transforms the selected pixels when there is a selection, else the layer.</summary>
    public async Task TransformCommand()
    {
        if (CanTransformSelection) await BeginSelectionTransform();
        else BeginTransform();
    }

    public async Task BeginSelectionTransform()
    {
        if (!CanTransformSelection || document is not { } doc || ActiveLayer is not { } source) { Host.Beep(); return; }
        (RasterImage Image, RectD Region) lifted;
        try
        {
            if (RenderSelectedPixels(source, false) is not { } pixels) { Host.Beep(); return; }
            lifted = pixels;
        }
        catch (Exception e) { BrushError = e.Message; Notify(); return; }
        var before = doc;
        var beforeActive = ActiveLayerId;
        // Outer edit: closed by CommitTransform (merge) or CancelTransform (restore).
        BeginEdit("Transform Selection");
        await ClearSelectedPixels();
        int index = document!.IndexOf(source.Id);
        if (index < 0) { Document = before; EndEdit(); return; }
        var floating = ImageLayer.FromAsset(PixelOps.Asset(lifted.Image, "Floating Selection"), lifted.Region.Origin) with
        {
            Name = "Floating Selection", ParentId = source.ParentId, Opacity = source.Opacity, BlendMode = source.BlendMode,
        };
        SetLayers(document.Layers.Insert(index + 1, floating));
        ActiveLayerId = floating.Id;
        Tool = NavigationTool.Move;
        TransformEditState = new TransformEdit
        {
            LayerId = floating.Id, Draft = floating.Transform, Persistent = true,
            Floating = new FloatingTransform(source.Id, before, beforeActive, floating.Transform, lifted.Region.Size),
        };
        InvalidateCanvas();
    }

    public Affine? FloatingSelectionTransform(TransformEdit edit)
    {
        if (edit.Floating is not { } floating) return null;
        int w = (int)floating.PixelSize.Width, h = (int)floating.PixelSize.Height;
        return LayerTransform.PixelToDocument(floating.Original, w, h).Inverted().Concat(LayerTransform.PixelToDocument(edit.Draft, w, h));
    }

    public void MergeFloatingTransform(TransformEdit edit, FloatingTransform floating)
    {
        try
        {
            if (!edit.Draft.IsValid || document?.Layer(edit.LayerId)?.Asset?.Image is not { } pixels || document.Layer(floating.SourceId) is not { } source)
                throw ProjectException.Invalid();
            (RasterImage Image, LayerTransform Transform) placed = edit.Corners is { } c
                ? ((Func<(RasterImage, LayerTransform)>)(() => { var w = DistortWarp.WarpTrimmed(pixels, edit.Draft, c); return (w.Image, w.Transform); }))()
                : (pixels, edit.Draft);
            var merged = FloatingMerge.Merge(placed.Image, placed.Transform, source);
            DocumentSelection? moved = null;
            if (edit.Corners is { } corners)
            {
                var placement = LayerTransform.PixelToDocument(floating.Original, (int)floating.PixelSize.Width, (int)floating.PixelSize.Height);
                if (Selection is { } selection && DistortWarp.MapPath(selection.SharedPath, placement, floating.PixelSize, edit.Draft, corners) is { } path)
                    moved = new DocumentSelection(path, selection.Antialiased, selection.Feather);
            }
            else if (FloatingSelectionTransform(edit) is { } matrix && Selection is { } selection) moved = selection.Transformed(matrix);
            var layers = document.Layers.RemoveAll(l => l.Id == edit.LayerId);
            int index = layers.FindIndex(l => l.Id == source.Id);
            if (index < 0) throw ProjectException.Invalid();
            layers = layers.SetItem(index, source with { Asset = merged.Asset, Transform = merged.Transform, Mask = merged.Mask, IsGroup = false });
            Document = document with { Layers = layers, Selection = moved };
            ActiveLayerId = source.Id;
        }
        catch (Exception e)
        {
            Document = floating.Before;
            ActiveLayerId = floating.BeforeActive;
            BrushError = e.Message;
        }
        finally { EndEdit(); }
    }

    public void CancelFloatingTransform(FloatingTransform floating)
    {
        Document = floating.Before;
        ActiveLayerId = floating.BeforeActive;
        EndEdit();
    }
}

public static class FloatingMerge
{
    /// <summary>Draws floating pixels onto the source layer's own grid, growing the layer (and revealing a grown mask).</summary>
    public static (ImageAsset Asset, LayerTransform Transform, LayerMask? Mask) Merge(RasterImage pixels, LayerTransform transform, ImageLayer source)
    {
        if (source.Asset?.Image is not { } sourceImage) throw ProjectException.Invalid();
        int width = sourceImage.Width, height = sourceImage.Height;
        var toDocument = LayerTransform.PixelToDocument(source.Transform, width, height);
        var toPixels = toDocument.Inverted();
        var floatingBounds = new RectD(0, 0, pixels.Width, pixels.Height).Apply(LayerTransform.PixelToDocument(transform, pixels.Width, pixels.Height)).Apply(toPixels);
        var original = new RectD(0, 0, width, height);
        var extent = original.Union(floatingBounds).Integral;
        if (extent.Width > 30_000 || extent.Height > 30_000 || extent.Width * extent.Height > 100_000_000) throw ProjectException.TooLarge();
        var bitmap = PixelOps.NewRgba((int)extent.Width, (int)extent.Height);
        var target = bitmap.GetPixelSpan();
        int ox = (int)-extent.X, oy = (int)-extent.Y;
        for (int y = 0; y < height; y++) sourceImage.Pixels.Slice(y * sourceImage.RowBytes, width * 4).CopyTo(target.Slice((y + oy) * bitmap.RowBytes + ox * 4));
        using (var surface = new RenderSurface(bitmap, toPixels.Concat(Affine.Translation(-extent.X, -extent.Y))))
            LayerRenderer.Draw(surface, pixels, transform);
        var image = RasterImage.Adopt(bitmap);
        var size = new SizeD(extent.Width * source.Size.Width / width, extent.Height * source.Size.Height / height);
        var center = toDocument.Apply(new PointD(extent.MidX, extent.MidY));
        var merged = source.Transform with { Size = size, Origin = new PointD(center.X - size.Width / 2, center.Y - size.Height / 2) };
        var mask = source.Mask;
        if (source.Mask is { Placement: null } current && extent != original)
        {
            var bytes = new byte[(int)extent.Width * (int)extent.Height];
            Array.Fill(bytes, (byte)255);
            var m = current.Asset.Image;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int mx = m.Width == width ? x : Math.Min(m.Width - 1, x * m.Width / width);
                    int my = m.Height == height ? y : Math.Min(m.Height - 1, y * m.Height / height);
                    bytes[(y + oy) * (int)extent.Width + x + ox] = m.ValueAt(mx, my);
                }
            mask = current.Replacing(PixelOps.MaskAssetFrom(MaskImage.FromPixels((int)extent.Width, (int)extent.Height, bytes)));
        }
        return (PixelOps.Asset(image, source.Name), merged, mask);
    }
}
