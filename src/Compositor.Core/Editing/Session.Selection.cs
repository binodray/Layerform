using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public enum LassoKind { Freehand, Polygonal, Rectangle, Ellipse }
public enum SelectionMode { Replace, Add, Subtract }
public enum WandSampleSize { Point = 0, ThreeByThree = 1, FiveByFive = 2 }

public sealed record WandSettings(int Tolerance = 32, WandSampleSize SampleSize = WandSampleSize.Point, bool Contiguous = true, bool SampleAllLayers = false);

/// <summary>A lasso or marquee outline being drawn, in document pixels.</summary>
public sealed record LassoDraft(List<PointD> Points, PointD? Cursor, SelectionMode Mode, LassoKind Kind, PointD? Anchor = null);

public static class DragBox
{
    /// <summary>The whole-pixel box a drag spans; <paramref name="square"/> evens the sides, <paramref name="fromCenter"/>
    /// grows it around the anchor.</summary>
    public static RectD Rect(PointD anchor, PointD point, bool square, bool fromCenter)
    {
        double dx = Math.Round(point.X, MidpointRounding.AwayFromZero) - anchor.X, dy = Math.Round(point.Y, MidpointRounding.AwayFromZero) - anchor.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        return fromCenter
            ? new RectD(anchor.X - Math.Abs(dx), anchor.Y - Math.Abs(dy), Math.Abs(dx) * 2, Math.Abs(dy) * 2)
            : new RectD(Math.Min(anchor.X, anchor.X + dx), Math.Min(anchor.Y, anchor.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }
}

/// <summary>Selected pixels being dragged: the lifted raster plus the outline it started from.</summary>
public sealed class PixelMove
{
    public RasterEdit Raster { get; }
    public DocumentSelection Origin { get; }
    public bool Duplicate { get; }
    public SizeD Offset { get; set; }
    public PixelMove(RasterEdit raster, DocumentSelection origin, bool duplicate) { Raster = raster; Origin = origin; Duplicate = duplicate; }
    public DocumentSelection MovedSelection => Origin.Transformed(Affine.Translation(Offset.Width, Offset.Height));
}

public sealed record PixelClipboard(RasterImage Image, PointD Origin, long ClipboardSequence);

public sealed partial class EditorSession
{
    public LassoDraft? LassoDraft { get; private set; }
    public LassoKind LassoKind { get; set; } = LassoKind.Freehand;
    public LassoKind MarqueeKind { get; set; } = LassoKind.Rectangle;
    public SelectionMode SelectionModeChoice { get; set; } = SelectionMode.Replace;
    public SelectionMode? HeldSelectionMode { get; private set; }
    public DocumentSelection? SelectionMoveOrigin { get; private set; }
    public PixelMove? PixelMove { get; private set; }
    public PixelClipboard? PixelClipboard { get; private set; }
    public bool SelectionAntialiased { get; set; } = true;
    public WandSettings WandSettings { get; set; } = new();
    public int SelectionExpandAmount { get; set; } = 1;
    public int SelectionContractAmount { get; set; } = 1;

    public bool CanEditSelection => CanEditLayers;

    public SelectionMode SelectionModeFor(bool shift, bool option) => option ? SelectionMode.Subtract : shift ? SelectionMode.Add : SelectionModeChoice;
    public SelectionMode LassoCursorMode(bool shift, bool option) => LassoDraft?.Mode ?? SelectionModeFor(shift, option);
    public SelectionMode DisplayedSelectionMode => LassoDraft?.Mode ?? HeldSelectionMode ?? SelectionModeChoice;

    public void UpdateHeldSelectionKeys(bool shift, bool option)
    {
        SelectionMode? held = option ? SelectionMode.Subtract : shift ? SelectionMode.Add : null;
        if (HeldSelectionMode != held) { HeldSelectionMode = held; Notify(); }
    }

    public void BeginLasso(PointD point, SelectionMode mode)
    {
        if (!Tool.IsSelectionTool() || Tool == NavigationTool.Wand || !CanEditSelection || SelectionMoveOrigin != null) return;
        if (Tool == NavigationTool.Marquee)
        {
            var anchor = new PointD(Math.Round(point.X, MidpointRounding.AwayFromZero), Math.Round(point.Y, MidpointRounding.AwayFromZero));
            LassoDraft = new LassoDraft(new List<PointD> { anchor }, null, mode, MarqueeKind, anchor);
        }
        else LassoDraft = new LassoDraft(new List<PointD> { point }, null, mode, LassoKind);
        InvalidateCanvas();
    }

    public void DragMarquee(PointD point, bool square, bool fromCenter)
    {
        if (LassoDraft is not { Kind: LassoKind.Rectangle or LassoKind.Ellipse, Anchor: { } anchor } draft || !point.IsFinite) return;
        var rect = DragBox.Rect(anchor, point, square, fromCenter);
        LassoDraft = draft with
        {
            Points = new List<PointD> { new(rect.MinX, rect.MinY), new(rect.MaxX, rect.MinY), new(rect.MaxX, rect.MaxY), new(rect.MinX, rect.MaxY) },
        };
        InvalidateCanvas();
    }

    public void ExtendLasso(PointD point)
    {
        if (LassoDraft is not { } draft || !point.IsFinite) return;
        if (draft.Points.Count > 0 && draft.Points[^1].DistanceTo(point) < 0.25) return;
        LassoDraft = draft with { Points = new List<PointD>(draft.Points) { point } };
        InvalidateCanvas();
    }

    public void MoveLassoCursor(PointD? point)
    {
        if (LassoDraft is { } draft) { LassoDraft = draft with { Cursor = point }; InvalidateCanvas(); }
    }

    public void RemoveLastLassoPoint()
    {
        if (LassoDraft is not { } draft) return;
        var points = new List<PointD>(draft.Points);
        points.RemoveAt(points.Count - 1);
        LassoDraft = points.Count == 0 ? null : draft with { Points = points };
        InvalidateCanvas();
    }

    public void CancelLasso()
    {
        if (LassoDraft == null) return;
        LassoDraft = null;
        InvalidateCanvas();
    }

    public void PressMarqueeKey() => SelectTool(NavigationTool.Marquee);
    public void PressLassoKey() => SelectTool(NavigationTool.Lasso);
    public void ToggleMarqueeKind() { CancelLasso(); MarqueeKind = MarqueeKind == LassoKind.Rectangle ? LassoKind.Ellipse : LassoKind.Rectangle; Notify(); }
    public void ToggleLassoKind() { CancelLasso(); LassoKind = LassoKind == LassoKind.Freehand ? LassoKind.Polygonal : LassoKind.Freehand; Notify(); }

    /// <summary>Closes the outline and combines it with the selection; a click enclosing nothing deselects in New mode.</summary>
    public void FinishLasso()
    {
        if (LassoDraft is not { } draft) return;
        LassoDraft = null;
        var outline = new SKPath { FillType = SKPathFillType.Winding };
        if (draft.Kind == LassoKind.Ellipse && draft.Points.Count == 4)
        {
            double x0 = draft.Points.Min(p => p.X), y0 = draft.Points.Min(p => p.Y), x1 = draft.Points.Max(p => p.X), y1 = draft.Points.Max(p => p.Y);
            outline.AddOval(new SKRect((float)x0, (float)y0, (float)x1, (float)y1));
        }
        else if (draft.Points.Count > 0) outline.AddPoly(draft.Points.Select(p => p.ToSK()).ToArray(), true);
        var bounds = outline.Bounds;
        if (!(draft.Points.Count >= 3 || draft.Kind == LassoKind.Ellipse) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            if (draft.Mode == SelectionMode.Replace) Deselect();
            InvalidateCanvas();
            return;
        }
        ApplySelection(outline, draft.Mode, draft.Kind switch
        {
            LassoKind.Freehand => "Lasso", LassoKind.Polygonal => "Polygonal Lasso", LassoKind.Ellipse => "Elliptical Marquee", _ => "Rectangular Marquee",
        });
    }

    public void ApplySelection(SKPath shape, SelectionMode mode, string name)
    {
        if (document == null || !CanEditSelection) return;
        using var canvas = SelectionRaster.Rectangle(new RectD(0, 0, document.Width, document.Height));
        var clipped = shape.Op(canvas, SKPathOp.Intersect) ?? new SKPath();
        SKPath result;
        switch (mode)
        {
            case SelectionMode.Replace: result = clipped; break;
            case SelectionMode.Add: result = Selection is { } current ? current.SharedPath.Op(clipped, SKPathOp.Union) ?? clipped : clipped; break;
            default:
                if (Selection is not { } existing) return;
                result = existing.SharedPath.Op(clipped, SKPathOp.Difference) ?? new SKPath();
                break;
        }
        SetSelection(new DocumentSelection(result, SelectionAntialiased), name);
    }

    public void SetSelection(DocumentSelection? value, string name)
    {
        if (document == null || !CanEditSelection || Equals(value, Selection)) return;
        BeginEdit(name);
        Document = document with { Selection = value };
        EndEdit();
    }

    public bool CanMoveSelection(PointD point) =>
        Selection is { IsEmpty: false } selection && CanEditSelection && LassoDraft == null && selection.Contains(point);

    public bool BeginSelectionMove()
    {
        if (SelectionMoveOrigin != null || Selection is not { IsEmpty: false } selection || !CanEditSelection) return false;
        BeginEdit("Move Selection");
        SelectionMoveOrigin = selection;
        return true;
    }

    public void MoveSelection(SizeD offset)
    {
        if (SelectionMoveOrigin is not { } origin) return;
        var shift = Affine.Translation(Math.Round(offset.Width, MidpointRounding.AwayFromZero), Math.Round(offset.Height, MidpointRounding.AwayFromZero));
        Document = document! with { Selection = origin.Transformed(shift) };
    }

    public void EndSelectionMove()
    {
        if (SelectionMoveOrigin == null) return;
        SelectionMoveOrigin = null;
        EndEdit();
    }

    public void NudgeSelection(double dx, double dy)
    {
        if (!BeginSelectionMove()) return;
        MoveSelection(new SizeD(dx, dy));
        EndSelectionMove();
    }

    public bool CanModifySelection => Selection?.IsEmpty == false && CanEditSelection && LassoDraft == null;
    public void ExpandSelection(int amount) => ResizeSelection(amount, "Expand Selection");
    public void ContractSelection(int amount) => ResizeSelection(-amount, "Contract Selection");

    private void ResizeSelection(double delta, string name)
    {
        if (document == null || Selection is not { } current || !CanModifySelection || delta == 0 || Math.Abs(delta) > 500) return;
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(Math.Abs(delta) * 2), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, StrokeMiter = 10 };
        using var band = new SKPath();
        stroke.GetFillPath(current.SharedPath, band);
        SKPath result;
        if (delta > 0)
        {
            using var canvas = SelectionRaster.Rectangle(new RectD(0, 0, document.Width, document.Height));
            using var grown = current.SharedPath.Op(band, SKPathOp.Union) ?? new SKPath(current.SharedPath);
            result = grown.Op(canvas, SKPathOp.Intersect) ?? new SKPath();
        }
        else result = current.SharedPath.Op(band, SKPathOp.Difference) ?? new SKPath();
        SetSelection(new DocumentSelection(result, current.Antialiased), name);
    }

    public void SelectAll()
    {
        if (document == null) return;
        SetSelection(new DocumentSelection(SelectionRaster.Rectangle(new RectD(0, 0, document.Width, document.Height))), "Select All");
    }

    public void Deselect()
    {
        if (Selection == null) return;
        SetSelection(null, "Deselect");
    }

    public void InvertSelection()
    {
        if (document == null || Selection is not { } current) return;
        using var canvas = SelectionRaster.Rectangle(new RectD(0, 0, document.Width, document.Height));
        SetSelection(new DocumentSelection(canvas.Op(current.SharedPath, SKPathOp.Difference) ?? new SKPath(), current.Antialiased), "Inverse");
    }

    /// <summary>Ctrl-click a mask thumbnail: the mask's black (hidden) areas become the selection.</summary>
    public void LoadMaskSelection(Guid layerId, SelectionMode mode = SelectionMode.Replace)
    {
        if (!CanEditSelection || document?.Layer(layerId) is not { Mask: { } mask } layer) return;
        if (Tracing.DarkPixels(mask.Asset.Image) is not { } traced) { Host.Beep(); return; }
        traced.Transform(LayerTransform.PixelToDocument(layer.MaskTransform, mask.Asset.Image.Width, mask.Asset.Image.Height).ToSK());
        ApplySelection(traced, mode, "Load Mask Selection");
    }

    /// <summary>Ctrl-click a layer thumbnail: the layer's ≥50% opaque pixels become the selection.</summary>
    public void LoadLayerSelection(Guid layerId, SelectionMode mode = SelectionMode.Replace)
    {
        if (!CanEditSelection || document?.Layer(layerId) is not { IsGroup: false, Asset: { } asset } layer) { Host.Beep(); return; }
        if (Tracing.OpaquePixels(asset.Image) is not { } traced) { Host.Beep(); return; }
        traced.Transform(LayerTransform.PixelToDocument(layer.Transform, asset.Image.Width, asset.Image.Height).ToSK());
        ApplySelection(traced, mode, "Load Layer Selection");
    }

    /// <summary>The Magic Wand: pixels similar to the one clicked, combined with the selection by mode, one undo step.</summary>
    public async Task MagicWand(PointD point, SelectionMode mode)
    {
        if (!CanEditSelection || IsProjectBusy || SelectionMoveOrigin != null || document is not { } doc) return;
        if (point.X < 0 || point.Y < 0 || point.X >= doc.Width || point.Y >= doc.Height) return;
        var sample = WandSample(doc);
        if (sample == null) return;
        var settings = WandSettings;
        bool antialiased = SelectionAntialiased;
        IsProjectBusy = true;
        Notify();
        SKPath? path = null;
        string? error = null;
        try
        {
            path = await Task.Run(() =>
            {
                var mask = new byte[sample.Width * sample.Height];
                long count = Tracing.WandMask(sample.Pixels, sample.Width, sample.Height, sample.RowBytes, (int)Math.Floor(point.X), (int)Math.Floor(point.Y),
                    (int)settings.SampleSize, Math.Clamp(settings.Tolerance, 0, 255), settings.Contiguous, mask);
                return count > 0 ? Tracing.Trace(mask, sample.Width, sample.Height, antialiased) : null;
            });
        }
        catch (Exception e) { error = e.Message; }
        IsProjectBusy = false;
        if (error != null) { BrushError = error; Notify(); return; }
        if (document?.Id != doc.Id) return;
        if (path == null) { if (mode == SelectionMode.Replace) Deselect(); Notify(); return; }
        if (mode == SelectionMode.Replace) SetSelection(new DocumentSelection(path, SelectionAntialiased), "Magic Wand");
        else ApplySelection(path, mode, "Magic Wand");
    }

    private RasterImage? WandSample(CanvasDocument doc)
    {
        try
        {
            if (WandSettings.SampleAllLayers) return RenderComposite(doc);
            var bitmap = PixelOps.NewRgba(doc.Width, doc.Height);
            if (ActiveLayer is { IsGroup: false, Asset: { } asset } layer)
            {
                using var surface = new RenderSurface(bitmap, Affine.Identity);
                LayerRenderer.Draw(surface, asset.Image, DisplayedTransform(layer));
            }
            return RasterImage.Adopt(bitmap);
        }
        catch { return null; }
    }

    // MARK: Pixel edits (SelectionEdits.swift)

    public bool CanEditPixels => CanPaint;

    public async Task FillSelection(bool background)
    {
        if (!CanEditPixels || ActiveLayer is not { } layer) return;
        var value = PaletteColorFor(background);
        var color = IsMaskSelected ? new PaletteColor(value.Red, value.Red, value.Red) : value;
        await ApplyPixelEdit(layer, IsMaskSelected ? "Fill Mask" : "Fill", e => e.Fill(color));
    }

    /// <summary>Delete with a selection: image pixels become transparent; on a mask it fills with the background colour.</summary>
    public async Task ClearSelectedPixels()
    {
        if (Selection == null || !CanEditPixels || ActiveLayer is not { } layer) return;
        if (IsMaskSelected) { await FillSelection(true); return; }
        if (layer.Asset == null) return;
        await ApplyPixelEdit(layer, "Clear", e => e.ClearPixels());
    }

    /// <summary>The Delete key: clears the selection when there is one; otherwise deletes the targeted mask or layer.</summary>
    public async Task DeleteKeyPressed()
    {
        if (Selection != null) await ClearSelectedPixels();
        else await DeleteLayerOrMask();
    }

    public async Task DeleteLayerOrMask()
    {
        if (IsMaskSelected && ActiveLayer?.Mask != null && SelectedLayerIds.Count <= 1) DeleteLayerMask();
        else await DeleteSelectedLayers();
    }

    public bool CanInvert
    {
        get
        {
            if (document == null || ActiveLayer is not { } layer || IsProjectBusy || IsImporting || brushStroke != null || PixelMove != null
                || RenamingLayerId != null || SelectedLayerIds.Count != 1 || (layer.IsGroup && !IsMaskSelected)
                || !EffectiveVisibleIds.Contains(layer.Id) || Selection?.IsEmpty == true) return false;
            return IsMaskSelected ? layer.Mask?.IsEnabled == true : layer.Asset != null;
        }
    }

    /// <summary>Ctrl+I: inverts the layer's colours (transparency kept) or its mask, within the selection.</summary>
    public async Task InvertPixels()
    {
        if (!CanInvert) return;
        CommitTransform();
        if (GradientEdit != null) await CommitGradient();
        if (!CanInvert || document is not { } doc || ActiveLayer is not { } layer) return;
        bool mask = IsMaskSelected;
        FinishOpacityEdit();
        IsProjectBusy = true;
        try
        {
            var clip = Selection?.Clip(doc.Size);
            object result;
            if (mask)
            {
                var image = layer.Mask!.Asset.Image;
                int lw = layer.Asset?.Image.Width ?? (int)Math.Round(layer.Size.Width), lh = layer.Asset?.Image.Height ?? (int)Math.Round(layer.Size.Height);
                var transform = layer.MaskTransform;
                result = await Task.Run(() =>
                {
                    var source = image;
                    if (clip != null && source.IsUniform1x1) source = MaskImage.Solid(source.ValueAt(0, 0), lw, lh);
                    var bytes = source.CopyPixels();
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(255 - bytes[i]);
                    var inverted = MaskImage.FromPixels(source.Width, source.Height, bytes);
                    return clip == null ? (object)inverted
                        : PixelFilters.BlendThroughSelection(inverted, source, clip, LayerTransform.PixelToDocument(transform, source.Width, source.Height));
                });
            }
            else
            {
                var image = layer.Asset!.Image;
                var transform = layer.Transform;
                result = await Task.Run(() =>
                {
                    var bitmap = image.CopyBitmap();
                    var p = bitmap.GetPixelSpan();
                    for (int i = 0; i + 3 < p.Length; i += 4)
                    {
                        byte a = p[i + 3];
                        p[i] = (byte)(a - p[i]); p[i + 1] = (byte)(a - p[i + 1]); p[i + 2] = (byte)(a - p[i + 2]);
                    }
                    var inverted = RasterImage.Adopt(bitmap);
                    return clip == null ? (object)inverted
                        : PixelFilters.BlendThroughSelection(inverted, image, clip, LayerTransform.PixelToDocument(transform, image.Width, image.Height));
                });
            }
            IsProjectBusy = false;
            if (document?.Layer(layer.Id) is not { } current || !ReferenceEquals(current.Asset?.Image, layer.Asset?.Image)
                || !ReferenceEquals(current.Mask?.Asset.Image, layer.Mask?.Asset.Image)) return;
            BeginEdit(mask ? "Invert Mask" : "Invert");
            if (mask) UpdateLayer(layer.Id, l => l with { Mask = l.Mask!.Replacing(PixelOps.MaskAssetFrom((MaskImage)result)) });
            else UpdateLayer(layer.Id, l => l with { Asset = PixelOps.Asset((RasterImage)result, current.Name), IsGroup = false });
            EndEdit();
        }
        catch (Exception e) { BrushError = e.Message; }
        finally { IsProjectBusy = false; InvalidateCanvas(); }
    }

    // MARK: Moving selected pixels (Ctrl-drag / Ctrl-arrow)

    public bool BeginPixelMove(bool duplicate = false)
    {
        if (PixelMove != null || Selection is not { IsEmpty: false } selection || !CanPaint || IsMaskSelected || ActiveLayer is not { Asset: not null } layer) return false;
        try
        {
            var raster = MakeRasterEdit(layer);
            if (!raster.LiftSelection()) return false;
            FinishOpacityEdit();
            PixelMove = new PixelMove(raster, selection, duplicate);
            InvalidateCanvas();
            return true;
        }
        catch (Exception e) { BrushError = e.Message; Notify(); return false; }
    }

    public void MovePixels(SizeD offset)
    {
        if (PixelMove is not { } move) return;
        var rounded = new SizeD(Math.Round(offset.Width, MidpointRounding.AwayFromZero), Math.Round(offset.Height, MidpointRounding.AwayFromZero));
        try { move.Raster.MoveLifted(rounded, move.Duplicate); }
        catch (Exception e) { CancelPixelMove(); BrushError = e.Message; Notify(); return; }
        move.Offset = rounded;
        InvalidateCanvas();
    }

    /// <summary>The outline to draw: during a pixel move the original shifted; during a selection transform, following the handles.</summary>
    public DocumentSelection? DisplayedSelection
    {
        get
        {
            if (PixelMove?.MovedSelection is { } moved) return moved;
            if (TransformEditState is { } edit && FloatingSelectionTransform(edit) is { } matrix && Selection is { } selection)
                return selection.Transformed(matrix);
            return Selection;
        }
    }

    public async Task FinishPixelMove()
    {
        if (PixelMove is not { } move || IsProjectBusy) return;
        if (move.Offset != SizeD.Zero)
        {
            var moved = move.MovedSelection;
            try { await CommitRasterEdit(move.Raster, move.Duplicate ? "Duplicate Pixels" : "Move Pixels", () => Document = document! with { Selection = moved }); }
            catch (Exception e) { BrushError = e.Message; }
        }
        PixelMove = null;
        InvalidateCanvas();
    }

    public void CancelPixelMove()
    {
        if (PixelMove == null) return;
        PixelMove = null;
        InvalidateCanvas();
    }

    public async Task NudgePixels(double dx, double dy)
    {
        if (!BeginPixelMove()) { Host.Beep(); return; }
        MovePixels(new SizeD(dx, dy));
        await FinishPixelMove();
    }

    private async Task ApplyPixelEdit(ImageLayer layer, string name, Action<RasterEdit> paint)
    {
        FinishOpacityEdit();
        try
        {
            var edit = MakeRasterEdit(layer);
            paint(edit);
            if (!edit.HasPatches) return;
            await CommitRasterEdit(edit, name);
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    // MARK: Clipboard (SelectionClipboard.swift)

    /// <summary>Whole-pixel bounds of what Copy takes: the selection, or the canvas without one.</summary>
    public RectD? SelectionCopyRegion()
    {
        if (document == null) return null;
        var canvas = new RectD(0, 0, document.Width, document.Height);
        var bounds = Selection?.Bounds ?? canvas;
        const double tolerance = 0.001;
        double minX = Math.Floor(bounds.MinX + tolerance), minY = Math.Floor(bounds.MinY + tolerance);
        var region = new RectD(minX, minY, Math.Ceiling(bounds.MaxX - tolerance) - minX, Math.Ceiling(bounds.MaxY - tolerance) - minY).Intersect(canvas);
        return region.IsNull || region.Width < 1 || region.Height < 1 ? null : region;
    }

    public bool CanCopyPixels => CanEditLayers && ActiveLayer is { } layer && (!layer.IsGroup || IsMaskSelected) && Selection?.IsEmpty != true
        && (IsMaskSelected ? layer.Mask != null : layer.Asset != null);

    /// <summary>The active layer's pixels (or mask as opaque gray) as they sit on the canvas, clipped to the selection.</summary>
    public (RasterImage Image, RectD Region)? RenderSelectedPixels(ImageLayer layer, bool mask)
    {
        if (document == null) return null;
        var clip = Selection?.Clip(document.Size);
        if (clip != null && clip.Coverage == null) return null;
        if (SelectionCopyRegion() is not { } region) return null;
        var bitmap = PixelOps.NewRgba((int)region.Width, (int)region.Height);
        using (var surface = new RenderSurface(bitmap, Affine.Translation(-region.X, -region.Y)))
        {
            var clips = clip?.ToClipMask() is { } c ? new[] { c } : Array.Empty<ClipMask>();
            var transform = DisplayedTransform(layer);
            if (mask && layer.Mask is { } owned)
            {
                var placement = DisplayedMaskPlacement(layer);
                using var gray = PixelOps.NewRgba(bitmap.Width, bitmap.Height, false);
                byte background = placement == null ? (byte)0 : MaskPlacement.Background(owned.Asset.Thumbnail);
                gray.Erase(new SKColor(background, background, background));
                using (var graySurface = new RenderSurface(gray, surface.DocumentToDevice))
                    LayerRenderer.DrawCoverage(graySurface, owned.Asset.Image, placement ?? transform);
                gray.SetImmutable();
                var grayImage = RasterImage.Adopt(gray.Copy());
                LayerRenderer.Draw(surface, grayImage, LayerTransform.Canvas(region.Width, region.Height) with { Origin = region.Origin, Sampling = LayerSampling.Nearest }, clips: clips);
            }
            else if (!mask && layer.Asset is { } asset)
                LayerRenderer.Draw(surface, asset.Image, transform, clips: clips);
            else return null;
        }
        return (RasterImage.Adopt(bitmap), region);
    }

    public bool CanCopyMerged => CanEditLayers && Selection?.IsEmpty != true && document?.RenderLayers().Any(l => l.Asset != null) == true;

    /// <summary>Ctrl+Shift+C: the selection across every visible layer, composited as the canvas shows it.</summary>
    public (RasterImage Image, RectD Region)? RenderMergedPixels()
    {
        if (document == null) return null;
        var clip = Selection?.Clip(document.Size);
        if (clip != null && clip.Coverage == null) return null;
        if (SelectionCopyRegion() is not { } region) return null;
        var composite = PixelOps.NewRgba((int)region.Width, (int)region.Height);
        using (var surface = new RenderSurface(composite, Affine.Translation(-region.X, -region.Y)))
            CompositeRenderer.Render(new LiveCompositeSource(this, document), surface);
        var merged = RasterImage.Adopt(composite);
        if (clip?.ToClipMask() is not { } c) return (merged, region);
        var bitmap = PixelOps.NewRgba((int)region.Width, (int)region.Height);
        using (var surface = new RenderSurface(bitmap, Affine.Translation(-region.X, -region.Y)))
            LayerRenderer.Draw(surface, merged, new LayerTransform(region.Origin, region.Size, Sampling: LayerSampling.Nearest), clips: new[] { c });
        return (RasterImage.Adopt(bitmap), region);
    }

    public void CopyMergedSelection()
    {
        if (!CanCopyMerged) return;
        try
        {
            if (RenderMergedPixels() is not { } copied) { Host.Beep(); return; }
            Store(copied);
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    public void CopySelection()
    {
        if (!CanCopyPixels || ActiveLayer is not { } layer) return;
        try
        {
            if (RenderSelectedPixels(layer, IsMaskSelected) is not { } copied) { Host.Beep(); return; }
            Store(copied);
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    private void Store((RasterImage Image, RectD Region) copied)
    {
        Host.SetClipboardImage(copied.Image);
        PixelClipboard = new PixelClipboard(copied.Image, copied.Region.Origin, Host.ClipboardSequence);
    }

    public async Task CutSelection()
    {
        if (Selection == null || !CanCopyPixels) return;
        CopySelection();
        await ClearSelectedPixels();
    }

    public bool CanPaste => document != null && CanEditLayers
        && ((PixelClipboard is { } clip && clip.ClipboardSequence == Host.ClipboardSequence) || Host.ClipboardHasImage);

    /// <summary>Ctrl+V: pixels copied here go back where they came from; images from other apps are centred.</summary>
    public void Paste()
    {
        if (!CanPaste || document == null) return;
        if (PixelClipboard is { } clip && clip.ClipboardSequence == Host.ClipboardSequence)
            AddPixelLayer(clip.Image, clip.Origin, NextLayerName(), "Paste");
        else if (Host.GetClipboardImage() is { } external)
        {
            var origin = new PointD(Math.Floor((document.Width - external.Width) / 2.0), Math.Floor((document.Height - external.Height) / 2.0));
            AddPixelLayer(external, origin, NextLayerName(), "Paste");
        }
        else Host.Beep();
    }

    /// <summary>Ctrl+J: the selection's pixels become a new layer in place; with no selection the layer is duplicated.</summary>
    public void LayerViaCopy()
    {
        if (!CanEditLayers || ActiveLayer is not { IsGroup: false } layer || Selection?.IsEmpty == true) return;
        if (Selection == null) { DuplicateActiveLayer(); return; }
        try
        {
            if (RenderSelectedPixels(layer, IsMaskSelected) is not { } copied) { Host.Beep(); return; }
            AddPixelLayer(copied.Image, copied.Region.Origin, NextLayerName(), "Layer via Copy");
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    public void DuplicateActiveLayer()
    {
        if (!CanEditLayers || ActiveLayer is not { IsGroup: false } layer) return;
        int index = document!.IndexOf(layer.Id);
        var copy = layer with { Id = Guid.NewGuid(), Name = $"{layer.Name} copy" };
        BeginEdit("Duplicate Layer");
        SetLayers(document.Layers.Insert(index + 1, copy));
        ActiveLayerId = copy.Id;
        EndEdit();
    }

    /// <summary>Alt-drag in the Layers panel: a copy placed where it was dropped, as one undo step.</summary>
    public bool DuplicateLayer(Guid id, Guid? parent, Guid? above = null, bool atBottom = false)
    {
        if (!CanEditLayers || document?.Layer(id) is not { IsGroup: false } || !CanPlaceLayer(id, parent)) return false;
        BeginEdit("Duplicate Layer");
        try
        {
            SelectLayer(id);
            DuplicateActiveLayer();
            if (ActiveLayerId is not { } copy || copy == id) return false;
            return PlaceLayer(copy, parent, above, atBottom);
        }
        finally { EndEdit(); }
    }

    /// <summary>Inserts pixels as a new layer above the active one; pasting drops the selection, a drawn shape keeps it.</summary>
    public void AddPixelLayer(RasterImage image, PointD origin, string name, string editName, bool dropsSelection = true, LayerShape? shape = null)
    {
        if (document == null) return;
        var layer = ImageLayer.FromAsset(PixelOps.Asset(image, name), origin) with
        {
            Name = name, Shape = shape, ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId,
        };
        int index = ActiveLayerId is { } a && document.IndexOf(a) is >= 0 and var i ? i + 1 : document.Layers.Count;
        FinishOpacityEdit();
        BeginEdit(editName);
        var doc = document with { Layers = document.Layers.Insert(index, layer) };
        if (dropsSelection) doc = doc with { Selection = null };
        Document = doc;
        ActiveLayerId = layer.Id;
        EndEdit();
    }
}
