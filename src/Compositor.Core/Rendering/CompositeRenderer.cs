using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>What the compositor needs to know about a document's layers. Implemented for saved snapshots (export)
/// and for the live editor (with previews of edits in progress).</summary>
public interface ICompositeSource
{
    /// <summary>Visible pixel and adjustment layers, bottom to top (folders are pass-through and never drawn).</summary>
    IReadOnlyList<Guid> RenderOrder { get; }
    Guid? Parent(Guid id);
    Guid? MaskSource(Guid id);
    LayerAdjustment? Adjustment(Guid id);
    double Opacity(Guid id);
    LayerBlendMode Blend(Guid id);
    /// <summary>A folder's enabled mask as a clip, or null.</summary>
    ClipMask? FolderClip(Guid folderId);
    /// <summary>An adjustment layer's own enabled mask as a clip, or null.</summary>
    ClipMask? AdjustmentClip(Guid id);
    /// <summary>Draws one layer's own pixels (with its opacity, blend mode and mask) under the given clips.</summary>
    void DrawOwn(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips);
}

/// <summary>
/// The layer compositor (LiveMaskRenderer.swift, FolderMaskClip.draw and AdjustmentSurface): folders are
/// pass-through with their masks multiplied into every layer inside; clipping stacks share their base's alpha;
/// other live-mask links clip by the source's coverage; adjustment layers change everything drawn beneath them.
/// </summary>
public sealed class CompositeRenderer
{
    private readonly ICompositeSource source;
    private readonly RenderSurface target;
    private readonly Dictionary<Guid, DeviceCoverageClip?> coverageCache = new();
    private readonly HashSet<Guid> visiting = new();
    private readonly Dictionary<Guid, List<Guid>> stacks = new();
    private readonly HashSet<Guid> stacked = new();
    private readonly List<IDisposable> owned = new();

    private CompositeRenderer(ICompositeSource source, RenderSurface target)
    {
        this.source = source;
        this.target = target;
    }

    /// <summary>Composites every visible layer into <paramref name="target"/>.</summary>
    public static void Render(ICompositeSource source, RenderSurface target)
    {
        var renderer = new CompositeRenderer(source, target);
        try { renderer.Run(); }
        finally { foreach (var d in renderer.owned) d.Dispose(); }
    }

    private void Run()
    {
        var ids = source.RenderOrder;
        PrepareStacks(ids);
        var folderClips = new Dictionary<Guid, ClipMask?>();
        foreach (var id in ids)
        {
            var clips = new List<ClipMask>();
            var folder = source.Parent(id);
            for (int depth = 0; folder is { } current && depth < 64; depth++)
            {
                if (!folderClips.TryGetValue(current, out var clip)) folderClips[current] = clip = source.FolderClip(current);
                if (clip != null) clips.Add(clip);
                folder = source.Parent(current);
            }
            DrawComposite(id, target, clips);
        }
    }

    /// <summary>Clipping stacks: a base followed by its contiguous clipped siblings share the base's alpha.</summary>
    private void PrepareStacks(IReadOnlyList<Guid> ids)
    {
        for (int index = 0; index < ids.Count; index++)
        {
            var baseId = ids[index];
            if (source.MaskSource(baseId) != null || source.Adjustment(baseId) != null) continue;
            var children = new List<Guid>();
            for (int j = index + 1; j < ids.Count; j++)
            {
                var child = ids[j];
                if (source.MaskSource(child) != baseId || source.Parent(child) != source.Parent(baseId)) break;
                children.Add(child);
            }
            if (children.Count == 0) continue;
            stacks[baseId] = children;
            foreach (var c in children) stacked.Add(c);
        }
    }

    private void DrawComposite(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips)
    {
        if (stacked.Contains(id)) return;
        if (source.Adjustment(id) != null)
        {
            if (source.MaskSource(id) == null) Adjust(id, surface, clips);
            return;
        }
        if (!stacks.TryGetValue(id, out var children))
        {
            Draw(id, surface, clips);
            return;
        }
        using var group = surface.Sibling();
        source.DrawOwn(id, group, Array.Empty<ClipMask>());
        int w = group.Width, h = group.Height;
        var alpha = new byte[w * h];
        var pixels = group.Pixels;
        PixelOps.ExtractAlpha(pixels, group.Bitmap.RowBytes, alpha, w, w, h);
        PixelOps.UnpremultiplyOpaque(pixels, group.Bitmap.RowBytes, w, h);
        foreach (var child in children)
        {
            if (source.Adjustment(child) != null) Adjust(child, group, Array.Empty<ClipMask>());
            else source.DrawOwn(child, group, Array.Empty<ClipMask>());
        }
        pixels = group.Pixels;
        PixelOps.RestoreAlpha(pixels, group.Bitmap.RowBytes, alpha, w, w, h);
        DrawSurface(group, surface, source.Blend(id), 1, clips);
    }

    /// <summary>Draws another surface of the same layout onto <paramref name="into"/>, blended and clipped.</summary>
    internal static void DrawSurface(RenderSurface from, RenderSurface into, LayerBlendMode blend, double opacity, IReadOnlyList<ClipMask> clips)
    {
        var canvas = into.Canvas;
        var rect = new SKRect(0, 0, into.Width, into.Height);
        from.Canvas.Flush();
        using var image = SKImage.FromBitmap(from.Bitmap);
        using var paint = new SKPaint { BlendMode = blend.ToSkia(), Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255)) };
        canvas.Save();
        canvas.ResetMatrix();
        if (clips.Count == 0)
        {
            canvas.DrawImage(image, 0, 0, paint);
        }
        else
        {
            canvas.SaveLayer(rect, paint);
            canvas.DrawImage(image, 0, 0);
            foreach (var clip in clips) clip.ApplyDstIn(canvas, into, rect);
            canvas.Restore();
        }
        canvas.Restore();
    }

    private void Draw(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips)
    {
        if (source.MaskSource(id) is { } sourceId)
        {
            var coverage = Coverage(sourceId);
            if (coverage == null) return;
            var combined = new List<ClipMask>(clips) { coverage };
            source.DrawOwn(id, surface, combined);
            return;
        }
        source.DrawOwn(id, surface, clips);
    }

    /// <summary>A layer's alpha — its pixels, transform, opacity, mask and upstream live masks — at device pixels.</summary>
    private DeviceCoverageClip? Coverage(Guid id)
    {
        if (coverageCache.TryGetValue(id, out var cached)) return cached;
        if (visiting.Contains(id) || visiting.Count >= 256) return null;
        visiting.Add(id);
        try
        {
            using var pixels = target.Sibling();
            Draw(id, pixels, Array.Empty<ClipMask>());
            var alpha = PixelOps.NewAlpha(pixels.Width, pixels.Height);
            owned.Add(alpha);
            PixelOps.ExtractAlpha(pixels.Pixels, pixels.Bitmap.RowBytes, alpha.GetPixelSpan(), alpha.RowBytes, pixels.Width, pixels.Height);
            alpha.SetImmutable();
            var image = SKImage.FromBitmap(alpha);
            owned.Add(image);
            var clip = new DeviceCoverageClip(image);
            coverageCache[id] = clip;
            return clip;
        }
        finally { visiting.Remove(id); }
    }

    /// <summary>An adjustment layer: everything drawn so far, adjusted, then put back through its mask and clips.</summary>
    private void Adjust(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips)
    {
        var settings = source.Adjustment(id);
        if (settings == null) return;
        int w = surface.Width, h = surface.Height, stride = surface.Bitmap.RowBytes;
        var live = surface.Pixels;
        var original = live.ToArray();
        using var adjusted = PixelOps.NewRgba(w, h, false);
        original.AsSpan().CopyTo(adjusted.GetPixelSpan());
        AdjustmentPixels.Apply(settings, adjusted, surface.DocumentRegion);
        var blend = source.Blend(id);
        if (blend != LayerBlendMode.Normal)
        {
            // Blend colours at full coverage, then restore the original alpha: source-over of two translucent copies
            // would thicken soft edges.
            using var baseBitmap = PixelOps.NewRgba(w, h, false);
            original.AsSpan().CopyTo(baseBitmap.GetPixelSpan());
            var alpha = new byte[w * h];
            PixelOps.ExtractAlpha(baseBitmap.GetPixelSpan(), stride, alpha, w, w, h);
            PixelOps.UnpremultiplyOpaque(baseBitmap.GetPixelSpan(), stride, w, h);
            PixelOps.UnpremultiplyOpaque(adjusted.GetPixelSpan(), stride, w, h);
            using (var canvas = new SKCanvas(baseBitmap))
            {
                adjusted.SetImmutable();
                using var foreground = SKImage.FromBitmap(adjusted);
                using var paint = new SKPaint { BlendMode = blend.ToSkia() };
                canvas.DrawImage(foreground, 0, 0, paint);
            }
            PixelOps.RestoreAlpha(baseBitmap.GetPixelSpan(), stride, alpha, w, w, h);
            WriteBack(surface, original, baseBitmap.GetPixelSpan(), id, clips);
            return;
        }
        WriteBack(surface, original, adjusted.GetPixelSpan(), id, clips);
    }

    private void WriteBack(RenderSurface surface, byte[] original, ReadOnlySpan<byte> adjusted, Guid id, IReadOnlyList<ClipMask> clips)
    {
        int w = surface.Width, h = surface.Height, stride = surface.Bitmap.RowBytes;
        double opacity = source.Opacity(id);
        var allClips = new List<ClipMask>();
        if (source.AdjustmentClip(id) is { } own) allClips.Add(own);
        allClips.AddRange(clips);
        var coverage = LayerRenderer.CoverageBuffer(surface, allClips);
        var live = surface.Pixels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                // Opacity blends the adjusted result with the original (CIBlendWithMask), then the clip lerps it in.
                double m = coverage == null ? 1 : coverage[y * w + x] / 255.0;
                if (m <= 0) continue;
                for (int c = 0; c < 4; c++)
                {
                    double o = original[p + c], a = adjusted[p + c];
                    double v = opacity < 1 ? o + (a - o) * opacity : a;
                    v = o + (v - o) * m;
                    live[p + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        }
    }
}
