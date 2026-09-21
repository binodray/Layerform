using System.Collections.Immutable;
using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    public ProjectSnapshot? ProjectSnapshot()
    {
        if (document == null) return null;
        var images = new Dictionary<Guid, ImageAsset>();
        var masks = new Dictionary<Guid, MaskAsset>();
        var layers = document.Layers.Select(layer =>
        {
            if (layer.Asset is { } asset) images[layer.Id] = asset;
            if (layer.Mask is { } mask) masks[layer.Id] = mask.Asset;
            return layer.HierarchyRecord() with { Shape = layer.LiveShape?.Style };
        }).ToList();
        var manifest = new ProjectManifest
        {
            Resolution = document.Resolution, DocumentId = document.Id, Width = document.Width, Height = document.Height,
            ActiveLayerId = ActiveLayerId, Layers = layers,
        };
        return new ProjectSnapshot(manifest, images, masks);
    }

    /// <summary>Called only after the entire package has validated and loaded.</summary>
    public void InstallProject(ProjectSnapshot snapshot, string path)
    {
        CollapsedGroupIds = new HashSet<Guid>();
        isMaskSelected = false;
        CropRect = null;
        Slices.Clear();
        SliceDraft = null;
        TransformEditState = null;
        var m = snapshot.Manifest;
        Document = new CanvasDocument
        {
            Id = m.DocumentId, Width = m.Width, Height = m.Height, Resolution = m.Resolution ?? 72,
            Layers = m.Layers.Select(r => new ImageLayer
            {
                Id = r.Id, Asset = snapshot.Images.GetValueOrDefault(r.Id), Name = r.Name, IsVisible = r.IsVisible, IsLocked = r.IsLocked == true, Transform = r.Transform,
                ParentId = r.ParentId, IsGroup = r.IsGroup == true, Opacity = r.Opacity ?? 1, BlendMode = r.BlendMode ?? LayerBlendMode.Normal,
                Mask = snapshot.Mask(r), MaskSourceId = r.MaskSourceId, Adjustment = r.Adjustment,
                Shape = LayerShape.Loaded(r.Shape, snapshot.Images.GetValueOrDefault(r.Id)?.Image),
                Text = r.Text is { } text && snapshot.Images.GetValueOrDefault(r.Id)?.Image is { } textImage ? new LayerText(text, textImage) : null,
            }).ToImmutableList(),
        };
        ActiveLayerId = m.ActiveLayerId;
        ProjectPath = path;
        RenamingLayerId = null;
        History.Reset();
        Viewport.Fit(document!.Size);
        InvalidateCanvas();
    }

    public void ClearProject()
    {
        CollapsedGroupIds = new HashSet<Guid>();
        isMaskSelected = false;
        CropRect = null;
        Slices.Clear();
        SliceDraft = null;
        TransformEditState = null;
        Document = null;
        ActiveLayerId = null;
        RenamingLayerId = null;
        ProjectPath = null;
        History.Reset();
    }

    public void CreateNewProject(int width, int height)
    {
        if (IsProjectBusy || IsImporting || width < 1 || width > 30_000 || height < 1 || height > 30_000) return;
        ClearProject();
        CreateDocument(width, height, emptyLayer: true);
    }

    public string Title => ProjectPath != null ? Path.GetFileNameWithoutExtension(ProjectPath.TrimEnd('\\', '/')) : DefaultName;
    public string DefaultName { get; set; } = "Untitled";

    /// <summary>The document composited at 1:1 as the canvas shows it (merge, copy merged, samples).</summary>
    public RasterImage RenderComposite(CanvasDocument doc)
    {
        if ((long)doc.Width * doc.Height > 100_000_000) throw ExportException.TooLarge();
        var bitmap = PixelOps.NewRgba(doc.Width, doc.Height);
        using (var surface = new RenderSurface(bitmap, Affine.Identity))
            CompositeRenderer.Render(new LiveCompositeSource(this, doc), surface);
        return RasterImage.Adopt(bitmap);
    }

    /// <summary>Renders the visible part of the document for the canvas, including edits in progress.</summary>
    public void RenderCanvas(RenderSurface surface, Guid? hiddenLayerId = null)
    {
        if (document == null) return;
        CompositeRenderer.Render(new LiveCompositeSource(this, document, includePreviews: true, hiddenLayerId), surface);
    }

    internal RasterEdit? LiveMaskEdit(Guid id) =>
        new[] { brushStroke, GradientEdit?.Raster }.FirstOrDefault(e => e != null && e.Layer.Id == id && e.IsMask);
}

/// <summary>A clip made of a raster edit's mask: the old mask with the edit's tiles over it.</summary>
public sealed class RasterEditMaskClip : ClipMask
{
    private readonly RasterEdit edit;
    public RasterEditMaskClip(RasterEdit edit) { this.edit = edit; }

    public override void ApplyDstIn(SKCanvas canvas, RenderSurface surface, SKRect deviceRect)
    {
        using var layerPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.SaveLayer(deviceRect, layerPaint);
        DrawMask(canvas, surface);
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>The composed mask as alpha, drawn with Src into the current layer.</summary>
    internal void DrawMask(SKCanvas canvas, RenderSurface surface)
    {
        var toDevice = edit.PixelToDocument.Concat(surface.DocumentToDevice);
        using var src = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        if (edit.SourceMaskImage is { } old)
        {
            canvas.Save();
            var r = edit.SourceRect;
            var matrix = Affine.Scale(r.Width / old.Width, r.Height / old.Height).Concat(Affine.Translation(r.X, r.Y)).Concat(toDevice);
            canvas.SetMatrix(matrix.ToSK());
            canvas.DrawImage(old.AlphaImage, 0, 0, sampling, src);
            canvas.Restore();
        }
        using var tilePaint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        foreach (var tile in edit.Patches)
        {
            canvas.Save();
            canvas.SetMatrix(Affine.Translation(tile.Rect.Left, tile.Rect.Top).Concat(toDevice).ToSK());
            canvas.DrawImage(edit.TileImage(tile), 0, 0, sampling, tilePaint);
            canvas.Restore();
        }
    }
}

/// <summary>Reveals everything outside a layer's original rectangle and applies its mask inside (paint outside the old
/// raster is revealed while painting, as the committed mask will be expanded with white).</summary>
public sealed class RevealOutsideMaskClip : ClipMask
{
    private readonly MaskImage mask;
    private readonly LayerTransform transform;
    public RevealOutsideMaskClip(MaskImage mask, LayerTransform transform) { this.mask = mask; this.transform = transform; }

    public override void ApplyDstIn(SKCanvas canvas, RenderSurface surface, SKRect deviceRect)
    {
        using var layerPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.SaveLayer(deviceRect, layerPaint);
        canvas.Clear(SKColors.White);
        var matrix = LayerTransform.PixelToDocument(transform, mask.Width, mask.Height).Concat(surface.DocumentToDevice);
        canvas.SetMatrix(matrix.ToSK());
        using var src = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawImage(mask.AlphaImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), src);
        canvas.Restore();
        canvas.Restore();
    }
}

/// <summary>
/// The live document as the canvas shows it (EditorCanvas.drawLayers and drawLiveComposite): transforms in progress,
/// brush strokes, gradients, moved pixels, distortions and colour previews.
/// </summary>
public sealed class LiveCompositeSource : ICompositeSource
{
    private readonly EditorSession session;
    private readonly CanvasDocument document;
    private readonly Dictionary<Guid, ImageLayer> byId;
    private readonly bool previews;
    private readonly Guid? hiddenLayerId;
    public IReadOnlyList<Guid> RenderOrder { get; }

    public LiveCompositeSource(EditorSession session, CanvasDocument document, bool includePreviews = false, Guid? hiddenLayerId = null)
    {
        this.session = session;
        this.document = document;
        previews = includePreviews;
        this.hiddenLayerId = hiddenLayerId;
        byId = document.Layers.ToDictionary(l => l.Id);
        RenderOrder = document.RenderLayers().Select(l => l.Id).ToList();
    }

    public Guid? Parent(Guid id) => byId.GetValueOrDefault(id)?.ParentId;
    public Guid? MaskSource(Guid id) => byId.GetValueOrDefault(id)?.MaskSourceId;
    public LayerAdjustment? Adjustment(Guid id) => byId.GetValueOrDefault(id)?.Adjustment;
    public double Opacity(Guid id)
    {
        double opacity = byId.GetValueOrDefault(id)?.Opacity ?? 1;
        var parent = Parent(id);
        for (int depth = 0; parent is { } p && depth < 64; depth++)
        {
            opacity *= byId.GetValueOrDefault(p)?.Opacity ?? 1;
            parent = Parent(p);
        }
        return Math.Clamp(opacity, 0, 1);
    }
    public LayerBlendMode Blend(Guid id) => byId.TryGetValue(id, out var l) ? session.DisplayedBlendMode(l) : LayerBlendMode.Normal;

    public ClipMask? FolderClip(Guid folderId)
    {
        if (!byId.TryGetValue(folderId, out var folder) || folder.Mask is not { IsEnabled: true } mask) return null;
        if (previews && session.LiveMaskEdit(folderId) is { } edit) return new RasterEditMaskClip(edit);
        return new TransformedMaskClip(mask.Asset.Image, session.DisplayedTransform(folder));
    }

    public ClipMask? AdjustmentClip(Guid id)
    {
        if (!byId.TryGetValue(id, out var layer) || layer.Mask is not { IsEnabled: true } mask) return null;
        if (previews && session.LiveMaskEdit(id) is { } edit) return new RasterEditMaskClip(edit);
        return new TransformedMaskClip(mask.Asset.Image, layer.Transform);
    }

    public void DrawOwn(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips)
    {
        if (id == hiddenLayerId) return;
        if (!byId.TryGetValue(id, out var layer)) return;
        var blend = session.DisplayedBlendMode(layer);
        if (!previews)
        {
            if (layer.Asset?.Image is not { } plain) return;
            var t = session.DisplayedTransform(layer);
            var m = layer.Mask is { } owned ? MaskPlacement.ClipImage(owned, session.DisplayedMaskPlacement(layer), t, plain.Width, plain.Height) : null;
            LayerRenderer.Draw(surface, plain, t, Opacity(id), blend, m, clips);
            return;
        }
        var stroke = session.BrushStroke?.Layer.Id == id ? session.BrushStroke
            : session.GradientEdit?.Raster.Layer.Id == id ? session.GradientEdit.Raster
            : session.PixelMove?.Raster.Layer.Id == id ? session.PixelMove.Raster : null;
        if (layer.Asset == null && stroke == null) return;
        // Smudge or Liquify in progress: the layer as the stroke has reshaped it, across the canvas.
        if (session.WarpStroke is { } warp && warp.Layer.Id == id)
        {
            var canvasTransform = LayerTransform.Canvas(document.Width, document.Height);
            var warpMask = layer.Mask is { } owned ? MaskPlacement.ClipImage(owned, layer.MaskTransform, canvasTransform, warp.Width, warp.Height, 2048) : null;
            var warpImage = RasterImage.FromPixels(warp.Width, warp.Height, warp.Image.PeekPixels().GetPixelSpan());
            LayerRenderer.Draw(surface, warpImage, canvasTransform, Opacity(id), blend, warpMask, clips);
            return;
        }
        if (stroke == null && session.DistortPreviewFor(layer) is { } distorted)
        {
            LayerRenderer.Draw(surface, distorted.Image, distorted.Transform, Opacity(id), blend, distorted.Mask, clips);
            return;
        }
        var transform = (stroke is { IsMask: false } ? stroke.PaintTransform : (LayerTransform?)null) ?? session.DisplayedTransform(layer);
        MaskImage? mask = null;
        if (layer.Mask is { } ownedMask)
        {
            if (session.MaskDistortPreview(layer) is { } md) mask = md;
            else if (session.DisplayedMaskPlacement(layer) is not { } placement) mask = ownedMask.EnabledImage;
            else
            {
                var owner = stroke?.Layer ?? layer;
                var baseTransform = stroke == null ? transform : owner.Transform;
                double drawn = Math.Max(baseTransform.Size.Width, baseTransform.Size.Height) * surface.DeviceScale;
                double steady = Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(64, drawn))));
                mask = MaskPlacement.ClipImage(ownedMask, placement, baseTransform,
                    owner.Asset?.Image.Width ?? (int)Math.Round(baseTransform.Size.Width), owner.Asset?.Image.Height ?? (int)Math.Round(baseTransform.Size.Height),
                    session.TransformEditState != null ? Math.Min(2048, steady) : steady);
            }
        }
        if (stroke == null && session.ShapeTransformPreview(layer, transform) is { } shaped)
        {
            LayerRenderer.Draw(surface, shaped, transform, Opacity(id), blend, mask, clips);
            return;
        }
        if (stroke is { IsMask: false })
        {
            // Painting pixels previews exactly as the layer will look: old pixels with the stroke's tiles over them.
            var allClips = new List<ClipMask>();
            if (mask != null) allClips.Add(new RevealOutsideMaskClip(mask, stroke.Layer.Transform));
            allClips.AddRange(clips);
            DrawEditPreview(surface, stroke, transform, Opacity(id), blend, allClips);
            return;
        }
        if (stroke is { IsMask: true })
        {
            // Painting the mask: the layer through the mask as it will be once committed.
            if (layer.Asset?.Image is not { } image) return;
            var allClips = new List<ClipMask> { new RasterEditMaskClip(stroke) };
            allClips.AddRange(clips);
            LayerRenderer.Draw(surface, image, transform, Opacity(id), blend, null, allClips);
            return;
        }
        var source = session.FilterEdit?.PreviewImage(id) ?? session.Levels?.PreviewImage(id) ?? session.HueSaturation?.PreviewImage(id) ?? layer.Asset?.Image;
        if (source != null) LayerRenderer.Draw(surface, source, transform, Opacity(id), blend, mask, clips);
    }

    /// <summary>The layer's original pixels with a raster edit's tiles over them, blended and clipped as one layer.</summary>
    private static void DrawEditPreview(RenderSurface surface, RasterEdit edit, LayerTransform transform, double opacity, LayerBlendMode blend, IReadOnlyList<ClipMask> clips)
    {
        var deviceRect = LayerRenderer.DeviceBounds(surface, transform);
        if (deviceRect.IsEmpty) return;
        var canvas = surface.Canvas;
        using var layerPaint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255)), BlendMode = blend.ToSkia() };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(deviceRect);
        canvas.SaveLayer(deviceRect, layerPaint);
        var toDevice = edit.PixelToDocument.Concat(surface.DocumentToDevice);
        if (edit.SourceImage is { } old)
        {
            var oldTransform = edit.Layer.Transform;
            using var plain = new SKPaint { IsAntialias = oldTransform.Sampling != LayerSampling.Nearest };
            LayerRenderer.DrawImageOnly(canvas, surface, old, oldTransform, plain, deviceRect);
        }
        double scale = Math.Sqrt(toDevice.A * toDevice.A + toDevice.B * toDevice.B);
        var sampling = scale >= 1 && edit.Layer.Transform.Sampling == LayerSampling.Nearest
            ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear);
        using var tilePaint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        foreach (var tile in edit.Patches)
        {
            canvas.Save();
            canvas.SetMatrix(Affine.Translation(tile.Rect.Left, tile.Rect.Top).Concat(toDevice).ToSK());
            canvas.DrawImage(edit.TileImage(tile), 0, 0, sampling, tilePaint);
            canvas.Restore();
        }
        foreach (var clip in clips) clip.ApplyDstIn(canvas, surface, deviceRect);
        canvas.Restore();
        canvas.Restore();
    }
}

/// <summary>One open project in a tab.</summary>
public sealed class ProjectTab
{
    public Guid Id { get; } = Guid.NewGuid();
    public EditorSession Session { get; }
    public string Title => Session.Title;
    public ProjectTab(string name, IEditorHost host)
    {
        Session = new EditorSession { DefaultName = name, Host = host };
    }
}

/// <summary>Projects in tabs (ProjectWorkspace.swift).</summary>
public sealed class ProjectWorkspace
{
    private readonly IEditorHost host;
    private int nextNumber = 2;
    public List<ProjectTab> Tabs { get; } = new();
    public Guid SelectedId { get; private set; }
    public bool IsManaging { get; set; }
    public event Action? Changed;

    public ProjectWorkspace(IEditorHost host)
    {
        this.host = host;
        var first = new ProjectTab("Untitled 1", host);
        Tabs.Add(first);
        SelectedId = first.Id;
    }

    public ProjectTab Current => Tabs.FirstOrDefault(t => t.Id == SelectedId) ?? Tabs[0];

    public bool CanSwitch
    {
        get
        {
            var s = Current.Session;
            return !IsManaging && s.CanStartProjectOperation && s.HueSaturation == null && s.FilterEdit == null && s.GradientEdit == null
                && s.PixelMove == null && s.ColorPicker == null;
        }
    }

    public ProjectTab AddTab(bool reuseEmpty = true)
    {
        if (reuseEmpty && Tabs.Count == 1 && Current.Session.Document == null) return Current;
        var tab = new ProjectTab($"Untitled {nextNumber}", host);
        nextNumber++;
        Tabs.Add(tab);
        SelectedId = tab.Id;
        Changed?.Invoke();
        return tab;
    }

    public void Select(Guid id)
    {
        if (id == SelectedId || !CanSwitch || Tabs.All(t => t.Id != id)) return;
        Current.Session.CommitTransform();
        SelectedId = id;
        Changed?.Invoke();
    }

    public void SelectForce(Guid id) { SelectedId = id; Changed?.Invoke(); }

    public void RemoveTab(Guid id)
    {
        int index = Tabs.FindIndex(t => t.Id == id);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0)
        {
            // The permanent empty workspace starts a fresh numbering sequence.
            nextNumber = 2;
            var tab = new ProjectTab("Untitled 1", host);
            Tabs.Add(tab);
            SelectedId = tab.Id;
        }
        else if (SelectedId == id) SelectedId = Tabs[Math.Min(index, Tabs.Count - 1)].Id;
        Changed?.Invoke();
    }

    public ProjectTab? TabForPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        return Tabs.FirstOrDefault(t => t.Session.ProjectPath is { } p && string.Equals(Path.GetFullPath(p).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a loaded project as a tab, replacing a lone empty tab.</summary>
    public ProjectTab AddLoaded(ProjectSnapshot snapshot, string path)
    {
        var tab = new ProjectTab(Path.GetFileNameWithoutExtension(path), host);
        tab.Session.InstallProject(snapshot, path);
        if (Tabs.Count == 1 && Current.Session.Document == null) Tabs.Clear();
        Tabs.Add(tab);
        SelectedId = tab.Id;
        Changed?.Invoke();
        return tab;
    }

    /// <summary>The order closing asks about unsaved projects: the visible tab first, then the rest.</summary>
    public IEnumerable<ProjectTab> QuitOrder => new[] { Current }.Concat(Tabs.Where(t => t.Id != Current.Id));

    /// <summary>Drag a layer (and anything inside it) from one project into another, baking live masks from outside it.</summary>
    public async Task CopyLayer(Guid id, Guid? destination, PointD? point = null)
    {
        if (!CanSwitch) return;
        var sourceTab = Tabs.FirstOrDefault(t => t.Session.Document?.Layers.Any(l => l.Id == id) == true);
        if (sourceTab == null || !sourceTab.Session.CanEditLayers || sourceTab.Session.ProjectSnapshot() is not { } snapshot
            || sourceTab.Session.Document is not { } sourceDocument) return;
        if (destination == sourceTab.Id) return;
        ProjectTab target;
        if (destination is { } d)
        {
            if (Tabs.FirstOrDefault(t => t.Id == d) is not { } existing || !existing.Session.CanStartProjectOperation) return;
            target = existing;
        }
        else target = AddTab(reuseEmpty: false);
        if (target.Session.Document != null && !target.Session.CanEditLayers) return;
        var included = sourceTab.Session.DescendantIds(id);
        included.Add(id);
        var copied = sourceDocument.Layers.Where(l => included.Contains(l.Id)).ToList();
        long used = target.Session.UsedImagePixels, added = copied.Sum(l => l.Asset is { } a ? (long)a.Image.Width * a.Image.Height : 0);
        if (used + added > 100_000_000) { target.Session.ImportError = "The copied layers exceed this project’s 100-megapixel limit."; Changed?.Invoke(); return; }
        IsManaging = true;
        try
        {
            for (int i = 0; i < copied.Count; i++)
            {
                if (copied[i].MaskSourceId is not { } source || included.Contains(source)) continue;
                if (copied[i].Adjustment != null) { copied[i] = copied[i] with { MaskSourceId = null }; continue; }
                var layerId = copied[i].Id;
                var baked = await Task.Run(() => LiveMaskBaker.Bake(snapshot, layerId));
                copied[i] = copied[i] with { Asset = baked ?? copied[i].Asset, MaskSourceId = null };
            }
            var mapping = copied.ToDictionary(l => l.Id, _ => Guid.NewGuid());
            var size = target.Session.Document?.Size ?? sourceDocument.Size;
            var anchor = copied.FirstOrDefault(l => l.Id == id)?.Transform.Center ?? new PointD(sourceDocument.Width / 2.0, sourceDocument.Height / 2.0);
            var center = point ?? new PointD(size.Width / 2, size.Height / 2);
            double dx = center.X - anchor.X, dy = center.Y - anchor.Y;
            var layers = copied.Select(layer => layer with
            {
                Id = mapping[layer.Id],
                Transform = layer.Transform with { Origin = layer.Transform.Origin.Offset(dx, dy) },
                Mask = layer.Mask is { Placement: { } p } m ? m with { Placement = p with { Origin = p.Origin.Offset(dx, dy) } } : layer.Mask,
                ParentId = layer.ParentId is { } parent && mapping.TryGetValue(parent, out var np) ? np : null,
                MaskSourceId = layer.MaskSourceId is { } ms && mapping.TryGetValue(ms, out var nm) ? nm : null,
            }).ToList();
            var s = target.Session;
            s.BeginEdit("Copy Layers from Project");
            if (s.Document == null) s.CreateDocument((int)size.Width, (int)size.Height);
            s.Document = s.Document! with { Layers = s.Document.Layers.AddRange(layers) };
            s.ActiveLayerId = mapping[id];
            s.EndEdit();
            SelectedId = target.Id;
        }
        catch (Exception e) { target.Session.ImportError = e.Message; }
        finally { IsManaging = false; Changed?.Invoke(); }
    }
}
