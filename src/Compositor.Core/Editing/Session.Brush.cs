using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    private RasterEdit? brushStroke;
    private WarpStroke? warpStroke;
    public RasterEdit? BrushStroke => brushStroke;
    public WarpStroke? WarpStroke => warpStroke;

    /// <summary>An explicitly empty selection leaves nothing paintable, so painting never starts.</summary>
    public bool CanPaint => CanEditLayers && SelectedLayerIds.Count == 1 && (ActiveLayer?.IsGroup == false || IsMaskSelected)
        && Selection?.IsEmpty != true && ActiveLayerId is { } id && EffectiveVisibleIds.Contains(id)
        && (!IsMaskSelected || ActiveLayer?.Mask?.IsEnabled == true) && (IsMaskSelected || ActiveLayer?.Adjustment == null);

    /// <summary>Tiled raster edit of the active layer's pixels or mask, within the shared pixel budgets.</summary>
    public RasterEdit MakeRasterEdit(ImageLayer layer, BrushSettings? settings = null)
    {
        if (document == null) throw ProjectException.TooLarge();
        var stroke = new RasterEdit(layer, IsMaskSelected, settings ?? new BrushSettings(), document.Size);
        long used = document.Layers.Where(l => l.Id != layer.Id).Sum(l =>
            IsMaskSelected ? (l.Mask is { } m ? (long)m.Asset.Image.Width * m.Asset.Image.Height : 0)
                           : (l.Asset is { } a ? (long)a.Image.Width * a.Image.Height : 0));
        stroke.PixelLimit = 100_000_000 - used;
        stroke.SelectionClip = Selection?.Clip(document.Size);
        if (!IsMaskSelected && layer.Mask != null)
        {
            long maskPixels = document.Layers.Where(l => l.Id != layer.Id).Sum(l => l.Mask is { } m ? (long)m.Asset.Image.Width * m.Asset.Image.Height : 0);
            stroke.PixelLimit = Math.Min(stroke.PixelLimit, 100_000_000 - maskPixels);
        }
        return stroke;
    }

    public void BeginBrush(PointD point)
    {
        if (Tool == NavigationTool.Blur && BlurMode != BlurToolMode.Blur) { BeginWarp(point); return; }
        if (!(Tool == NavigationTool.Brush || Tool == NavigationTool.Blur || (Tool.IsBrushTool() && !IsMaskSelected))
            || !CanPaint || ActiveLayer is not { } layer || document == null) return;
        CloneSample? clone = null;
        if (Tool == NavigationTool.CloneStamp)
        {
            if (CloneStrokeOffset(point) is not { } offset)
            {
                BrushError = "Alt-click where Clone Stamp should copy from first.";
                Notify();
                return;
            }
            if (CloneSampleImage(document) is not { } image) return;
            CloneOffset = offset;
            clone = new CloneSample(image, null, offset);
        }
        if (Tool == NavigationTool.Blur)
        {
            var sample = BlurSample(document, IsMaskSelected);
            if (sample == null) return;
            clone = sample;
        }
        FinishOpacityEdit();
        try
        {
            var settings = brushSettings with
            {
                Healing = Tool == NavigationTool.SpotHealing,
                Erasing = Tool == NavigationTool.Brush && BrushMode == BrushToolMode.Erase && !IsMaskSelected,
                HealingMode = SpotHealingMode,
            };
            if (IsMaskSelected) { double v = MaskPaintWhite ? 1 : 0; settings = settings with { Red = v, Green = v, Blue = v }; }
            var stroke = MakeRasterEdit(layer, settings);
            stroke.Clone = clone;
            stroke.IsBlur = Tool == NavigationTool.Blur;
            brushStroke = stroke;
            stroke.Append(point);
            LastBrushPoint = (point, layer.Id, IsMaskSelected);
            InvalidateCanvas();
        }
        catch (Exception e) { CancelBrush(); BrushError = e.Message; Notify(); }
    }

    public void ContinueBrush(PointD point)
    {
        if (warpStroke != null) { warpStroke.Append(point); if (LastBrushPoint is { } l) LastBrushPoint = l with { Point = point }; InvalidateCanvas(); return; }
        if (brushStroke == null) return;
        try
        {
            brushStroke.Append(point);
            if (LastBrushPoint is { } last) LastBrushPoint = last with { Point = point };
            InvalidateCanvas();
        }
        catch (Exception e) { CancelBrush(); BrushError = e.Message; Notify(); }
    }

    /// <summary>Where a Shift-click paints a line from: the end of the last stroke on the same target.</summary>
    public PointD? ShiftLineStart() =>
        LastBrushPoint is { } last && last.LayerId == ActiveLayerId && last.Mask == IsMaskSelected ? last.Point : null;

    public void CancelBrush()
    {
        warpStroke = null;
        brushStroke = null;
        InvalidateCanvas();
    }

    /// <summary>Mouse-up: flushes the stroke, heals, and commits it as one undo step.</summary>
    public bool FinishBrush()
    {
        if (warpStroke != null)
        {
            if (IsProjectBusy) return false;
            FinishWarp();
            return true;
        }
        if (brushStroke is not { } stroke) return true;
        if (IsProjectBusy) return false;
        try
        {
            stroke.Flush();
            if (stroke.Settings.Healing) stroke.Heal();
            if (stroke.HasPatches) CommitPaintSnapshot(stroke);
        }
        catch (Exception e) { BrushError = e.Message; }
        finally { CancelBrush(); }
        return true;
    }

    public void CommitPaintSnapshot(RasterEdit stroke)
    {
        var result = stroke.PaintSnapshot();
        int index = document?.IndexOf(stroke.Layer.Id) ?? -1;
        if (!result.Transform.IsValid || index < 0) return;
        var current = document!.Layers[index];
        if (!ReferenceEquals(current.Asset?.Image, stroke.Layer.Asset?.Image) || current.Transform != stroke.Layer.Transform) return;
        var mask = current.Mask;
        if (!stroke.IsMask && mask is { Placement: null } original && result.Bounds != stroke.SourceRect)
            mask = original.Replacing(stroke.ExpandMask(original.Asset, result.Bounds));
        BeginEdit(stroke.EditName ?? (stroke.IsMask ? "Paint Mask" : stroke.Settings.Erasing ? "Erase" : stroke.IsBlur ? "Blur"
            : stroke.Clone != null ? "Clone Stamp" : stroke.Settings.Healing ? "Spot Healing" : "Brush Stroke"));
        if (stroke.IsMask)
            ReplaceLayer(index, current with { Mask = current.Mask is { } m ? m.Replacing(result.Mask!) : new LayerMask(result.Mask!) });
        else
            ReplaceLayer(index, current with { Asset = result.Asset, Transform = result.Transform, Mask = mask, IsGroup = false, Adjustment = null });
        EndEdit();
    }

    /// <summary>Fills, gradients, clears and moves: assembles the edit and replaces the pixels or mask as one undo step.</summary>
    public async Task CommitRasterEdit(RasterEdit stroke, string name, Action? alsoApply = null)
    {
        IsProjectBusy = true;
        try
        {
            if (!stroke.CommittedTransform.IsValid) throw ProjectException.TooLarge();
            var result = await Task.Run(stroke.CommitRender);
            var transform = stroke.TransformFor(result.PixelBounds);
            if (!transform.IsValid) throw ProjectException.TooLarge();
            var mask = stroke.Layer.Mask;
            if (!stroke.IsMask && mask is { Placement: null } originalMask)
                mask = originalMask.Replacing(stroke.ExpandMask(originalMask.Asset, result.PixelBounds));
            int index = document?.IndexOf(stroke.Layer.Id) ?? -1;
            if (index < 0) return;
            var current = document!.Layers[index];
            if (!ReferenceEquals(current.Asset?.Image, stroke.Layer.Asset?.Image) || current.Transform != stroke.Layer.Transform
                || !ReferenceEquals(current.Mask?.Asset.Image, stroke.Layer.Mask?.Asset.Image)) return;
            IsProjectBusy = false;
            BeginEdit(name);
            if (stroke.IsMask)
                ReplaceLayer(index, current with { Mask = current.Mask is { } m ? m.Replacing(result.Mask!) : new LayerMask(result.Mask!) });
            else
                ReplaceLayer(index, current with
                {
                    Asset = result.Asset, Transform = transform, IsGroup = false,
                    Mask = mask is { } kept ? kept with { IsEnabled = current.Mask?.IsEnabled ?? kept.IsEnabled } : null,
                });
            alsoApply?.Invoke();
            EndEdit();
        }
        finally { IsProjectBusy = false; Notify(); }
    }

    /// <summary>Tools where number keys set opacity: the brush or gradient, or with Move the selected layers.</summary>
    public bool UsesOpacityKeys => Tool.IsBrushTool() || Tool == NavigationTool.Gradient || Tool == NavigationTool.Move;

    /// <summary>Photoshop-style opacity keys: 1 = 10% … 9 = 90%, 0 = 100%; two quick digits set an exact value.</summary>
    public void TypeOpacityDigit(int digit, double time)
    {
        if (!UsesOpacityKeys || brushStroke != null || IsProjectBusy || digit < 0 || digit > 9) return;
        int percent = digit == 0 ? 100 : digit * 10;
        if (pendingOpacityDigit is { } pending && time - pending.Time < OpacityDigitWindow)
        {
            percent = Math.Max(1, pending.Digit * 10 + digit);
            pendingOpacityDigit = null;
        }
        else pendingOpacityDigit = (digit, time);
        double value = percent / 100.0;
        switch (Tool)
        {
            case NavigationTool.Brush or NavigationTool.SpotHealing or NavigationTool.CloneStamp or NavigationTool.Blur:
                BrushSettings = brushSettings with { Opacity = value }; break;
            case NavigationTool.Gradient: GradientSettings = gradientSettings with { Opacity = value }; break;
            default: SetSelectedLayersOpacity(value); break;
        }
    }

    /// <summary>Shift+[ / Shift+]: hardness in 25% steps.</summary>
    public void ChangeBrushHardness(bool increase)
    {
        if (brushStroke != null) return;
        double quarter = brushSettings.Hardness * 4;
        double step = increase ? Math.Floor(quarter + 0.001) + 1 : Math.Ceiling(quarter - 0.001) - 1;
        BrushSettings = brushSettings with { Hardness = Math.Clamp(step, 0, 4) / 4 };
    }

    public void ChangeBrushSize(bool increase)
    {
        if (brushStroke != null) return;
        double current = brushSettings.Diameter;
        double stepped = increase ? Math.Max(current + 1, Math.Round(current * 1.2, MidpointRounding.AwayFromZero))
                                  : Math.Min(current - 1, Math.Round(current / 1.2, MidpointRounding.AwayFromZero));
        BrushSettings = brushSettings with { Diameter = Math.Clamp(stepped, 1, 2000) };
    }

    // MARK: Clone Stamp (CloneStamp.swift)

    public void SetCloneSource(PointD point)
    {
        if (!point.IsFinite) return;
        CloneSource = point;
        CloneOffset = null;
        Notify();
    }

    public SizeD? CloneStrokeOffset(PointD point)
    {
        if (CloneSource is not { } source) return null;
        return (CloneSettings.Aligned ? CloneOffset : null)
            ?? new SizeD(Math.Round(source.X - point.X, MidpointRounding.AwayFromZero), Math.Round(source.Y - point.Y, MidpointRounding.AwayFromZero));
    }

    public PointD? CloneSamplePoint(PointD point)
    {
        if (CloneSource is not { } source) return null;
        if (CloneOffset is not { } offset || !(CloneSettings.Aligned || brushStroke != null)) return source;
        return new PointD(point.X + offset.Width, point.Y + offset.Height);
    }

    /// <summary>What a stroke copies from, at document size: the active layer alone or every visible layer as shown.</summary>
    public RasterImage? CloneSampleImage(CanvasDocument doc)
    {
        try
        {
            if (CloneSettings.SampleAllLayers) return RenderComposite(doc);
            var bitmap = PixelOps.NewRgba(doc.Width, doc.Height);
            if (ActiveLayer is { Asset: { } asset } layer)
            {
                using var surface = new RenderSurface(bitmap, Affine.Identity);
                LayerRenderer.Draw(surface, asset.Image, DisplayedTransform(layer));
            }
            return RasterImage.Adopt(bitmap);
        }
        catch { return null; }
    }

    // MARK: Blur tool (BlurTool.swift)

    /// <summary>The active layer (or its mask) as shown, at document size, softened by an amount following the brush size.</summary>
    public CloneSample? BlurSample(CanvasDocument doc, bool mask)
    {
        if (ActiveLayer is not { } layer) return null;
        double sigma = Math.Min(30, Math.Max(1.5, brushSettings.Diameter / 10));
        if (mask)
        {
            if (layer.Mask is not { } owned) return null;
            // Past its pixels a mask keeps its edge tone; the mask's own area starts black and coverage adds white.
            var background = MaskPlacement.Background(owned.Asset.Thumbnail);
            using var rgba = PixelOps.NewRgba(doc.Width, doc.Height, false);
            rgba.Erase(new SKColor(background, background, background));
            using (var surface = new RenderSurface(rgba, Affine.Identity))
            {
                var placement = layer.MaskTransform;
                surface.Canvas.SetMatrix(LayerTransform.PixelToDocument(placement, 1, 1).ToSK());
                using (var black = new SKPaint { Color = SKColors.Black, IsAntialias = true }) surface.Canvas.DrawRect(new SKRect(0, 0, 1, 1), black);
                surface.Canvas.ResetMatrix();
                LayerRenderer.DrawCoverage(surface, owned.Asset.Image, placement);
            }
            var gray = PixelOps.GrayFromRed(rgba);
            return new CloneSample(null, PixelFilters.GaussianBlurGray(gray, sigma, clamp: true), SizeD.Zero);
        }
        if (layer.Asset is not { } asset) return null;
        var bitmap = PixelOps.NewRgba(doc.Width, doc.Height);
        using (var surface = new RenderSurface(bitmap, Affine.Identity))
            LayerRenderer.Draw(surface, asset.Image, DisplayedTransform(layer));
        var sharp = RasterImage.Adopt(bitmap);
        return new CloneSample(PixelFilters.GaussianBlur(sharp, sigma), null, SizeD.Zero);
    }

    // MARK: Smudge and Liquify (SmudgeLiquify.swift)

    public void BeginWarp(PointD point)
    {
        if (!CanPaint || IsMaskSelected || ActiveLayer is not { Asset: { } asset } layer || document == null)
        {
            if (IsMaskSelected) { BrushError = "Smudge and Liquify work on a layer's pixels, not its mask."; Notify(); }
            return;
        }
        FinishOpacityEdit();
        try
        {
            var stroke = new WarpStroke(layer, asset.Image, DisplayedTransform(layer), document.Size, BlurMode, brushSettings);
            stroke.Append(point);
            warpStroke = stroke;
            LastBrushPoint = (point, layer.Id, false);
            InvalidateCanvas();
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    /// <summary>Paints the finished Smudge or Liquify result into the layer's pixels along the stroke, as one undo step.</summary>
    public void FinishWarp()
    {
        if (warpStroke is not { } warp) return;
        warpStroke = null;
        InvalidateCanvas();
        var current = document?.Layer(warp.Layer.Id);
        if (warp.Points.Count == 0 || current == null || !ReferenceEquals(current.Asset?.Image, warp.Layer.Asset?.Image)
            || current.Transform != warp.Layer.Transform) return;
        try
        {
            var result = warp.Result();
            // A hard tip a little wider than the brush covers everything the stroke moved.
            var settings = brushSettings with { Diameter = Math.Min(2000, warp.Diameter + 4), Hardness = 1, Opacity = 1 };
            var stroke = MakeRasterEdit(current, settings);
            stroke.Clone = new CloneSample(result, null, SizeD.Zero);
            stroke.ReplacesWithClone = true;
            stroke.EditName = warp.Mode == BlurToolMode.Smudge ? "Smudge" : "Liquify";
            foreach (var p in warp.Points) stroke.Append(p);
            stroke.Flush();
            if (stroke.HasPatches) CommitPaintSnapshot(stroke);
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }
}

/// <summary>A Smudge or Liquify stroke: the active layer as shown, at document size, changed dab by dab.</summary>
public sealed class WarpStroke
{
    public ImageLayer Layer { get; }
    public BlurToolMode Mode { get; }
    public double Diameter { get; }
    private readonly double hardness, strength;
    public int Width { get; }
    public int Height { get; }
    private readonly byte[] pixels;
    public List<PointD> Points { get; } = new();
    private PointD? last;
    private float[] carried = Array.Empty<float>();
    private float[] scratch = Array.Empty<float>();
    public int Revision { get; private set; }
    private SKImage? image;
    private int imageRevision = -1;

    public WarpStroke(ImageLayer layer, RasterImage source, LayerTransform transform, SizeD canvas, BlurToolMode mode, BrushSettings settings)
    {
        Layer = layer;
        Mode = mode;
        Diameter = Math.Max(2, settings.Diameter);
        hardness = Math.Clamp(settings.Hardness, 0, 0.98);
        strength = Math.Clamp(settings.Opacity, 0.01, 1);
        Width = (int)canvas.Width;
        Height = (int)canvas.Height;
        var bitmap = PixelOps.NewRgba(Width, Height);
        using (var surface = new RenderSurface(bitmap, Affine.Identity)) LayerRenderer.Draw(surface, source, transform);
        pixels = bitmap.GetPixelSpan().ToArray();
        bitmap.Dispose();
    }

    private int Radius => (int)Math.Ceiling(Diameter / 2);

    private float Weight(float u)
    {
        if (u >= 1) return 0;
        float h = (float)hardness;
        if (u <= h) return 1;
        float t = (1 - u) / (1 - h);
        return t * t * (3 - 2 * t);
    }

    public void Append(PointD point)
    {
        if (last is not { } from)
        {
            last = point;
            if (Mode == BlurToolMode.Smudge) PickUp(point);
            return;
        }
        double distance = from.DistanceTo(point);
        double spacing = Math.Max(1, Diameter * (Mode == BlurToolMode.Smudge ? 0.08 : 0.025));
        if (distance < spacing) return;
        int steps = (int)Math.Ceiling(distance / spacing);
        var previous = from;
        for (int step = 1; step <= steps; step++)
        {
            double t = (double)step / steps;
            var next = new PointD(from.X + (point.X - from.X) * t, from.Y + (point.Y - from.Y) * t);
            if (Mode == BlurToolMode.Smudge) Smudge(next); else Push(previous, next);
            Points.Add(next);
            previous = next;
        }
        last = point;
        Revision++;
    }

    private void PickUp(PointD center)
    {
        int r = Radius, side = 2 * r + 1;
        carried = new float[side * side * 4];
        int cx = (int)Math.Round(center.X, MidpointRounding.AwayFromZero), cy = (int)Math.Round(center.Y, MidpointRounding.AwayFromZero);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= Height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= Width) continue;
                int p = (y * Width + x) * 4, c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++) carried[c + k] = pixels[p + k];
            }
        }
    }

    private void Smudge(PointD center)
    {
        int r = Radius, side = 2 * r + 1;
        int cx = (int)Math.Round(center.X, MidpointRounding.AwayFromZero), cy = (int)Math.Round(center.Y, MidpointRounding.AwayFromZero);
        float keep = (float)strength, invR = 1 / (float)(Diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= Height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= Width) continue;
                float w = Weight(MathF.Sqrt(dx * dx + dy * dy) * invR);
                if (w <= 0) continue;
                int p = (y * Width + x) * 4, c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++)
                {
                    float under = pixels[p + k];
                    float painted = under + (carried[c + k] - under) * w;
                    pixels[p + k] = (byte)Math.Clamp(MathF.Round(painted, MidpointRounding.AwayFromZero), 0, 255);
                    carried[c + k] = painted + (carried[c + k] - painted) * keep;
                }
            }
        }
    }

    /// <summary>Forward warp: pixels under the brush move with it, most at its centre.</summary>
    private void Push(PointD a, PointD b)
    {
        int r = Radius;
        float mx = (float)((b.X - a.X) * strength), my = (float)((b.Y - a.Y) * strength);
        int margin = (int)Math.Ceiling(Math.Max(Math.Abs(mx), Math.Abs(my))) + 2;
        int cx = (int)Math.Round(b.X, MidpointRounding.AwayFromZero), cy = (int)Math.Round(b.Y, MidpointRounding.AwayFromZero);
        int x0 = Math.Max(0, cx - r - margin), x1 = Math.Min(Width - 1, cx + r + margin);
        int y0 = Math.Max(0, cy - r - margin), y1 = Math.Min(Height - 1, cy + r + margin);
        if (x0 > x1 || y0 > y1) return;
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        if (scratch.Length < cw * ch * 4) scratch = new float[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int p = ((y + y0) * Width + x + x0) * 4, s = (y * cw + x) * 4;
                for (int k = 0; k < 4; k++) scratch[s + k] = pixels[p + k];
            }
        float invR = 1 / (float)(Diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < y0 || y > y1) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < x0 || x > x1) continue;
                float w = Weight(MathF.Sqrt(dx * dx + dy * dy) * invR);
                if (w <= 0) continue;
                float sx = Math.Min(cw - 1, Math.Max(0, x - x0 - mx * w));
                float sy = Math.Min(ch - 1, Math.Max(0, y - y0 - my * w));
                int ix = Math.Min(cw - 2, (int)sx), iy = Math.Min(ch - 2, (int)sy);
                if (ix < 0 || iy < 0) continue;
                float fx = sx - ix, fy = sy - iy;
                int p = (y * Width + x) * 4;
                int s00 = (iy * cw + ix) * 4, s10 = s00 + 4, s01 = s00 + cw * 4, s11 = s01 + 4;
                for (int k = 0; k < 4; k++)
                {
                    float top = scratch[s00 + k] + (scratch[s10 + k] - scratch[s00 + k]) * fx;
                    float bottom = scratch[s01 + k] + (scratch[s11 + k] - scratch[s01 + k]) * fx;
                    pixels[p + k] = (byte)Math.Clamp(MathF.Round(top + (bottom - top) * fy, MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
    }

    /// <summary>The working copy as an image (document size).</summary>
    public SKImage Image
    {
        get
        {
            if (image != null && imageRevision == Revision) return image;
            image?.Dispose();
            image = SKImage.FromPixelCopy(RasterImage.Info(Width, Height), pixels, Width * 4);
            imageRevision = Revision;
            return image;
        }
    }

    public RasterImage Result() => RasterImage.FromPixels(Width, Height, pixels);
}
