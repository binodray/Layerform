using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public enum FilterKind { GaussianBlur, MotionBlur, AddNoise, LensCorrection, RemoveBackground, ContentAwareFill, Curves, Exposure, GradientMap, Grain }

public static class FilterKinds
{
    public static readonly FilterKind[] All = Enum.GetValues<FilterKind>();
    public static string Name(this FilterKind k) => k switch
    {
        FilterKind.GaussianBlur => "Gaussian Blur", FilterKind.MotionBlur => "Motion Blur", FilterKind.AddNoise => "Add Noise",
        FilterKind.LensCorrection => "Lens Correction", FilterKind.RemoveBackground => "Remove Background",
        FilterKind.ContentAwareFill => "Content-Aware Fill", FilterKind.GradientMap => "Gradient Map", _ => k.ToString(),
    };
    public static bool IsAutomatic(this FilterKind k) => k is FilterKind.ContentAwareFill or FilterKind.RemoveBackground;
    public static bool IsImageAdjustment(this FilterKind k) => k is FilterKind.Curves or FilterKind.Exposure or FilterKind.GradientMap or FilterKind.Grain;
    public static FilterKind? ToFilterKind(this AdjustmentKind k) => k switch
    {
        AdjustmentKind.Curves => Editing.FilterKind.Curves, AdjustmentKind.Exposure => Editing.FilterKind.Exposure,
        AdjustmentKind.GradientMap => Editing.FilterKind.GradientMap, AdjustmentKind.Grain => Editing.FilterKind.Grain, _ => null,
    };
}

public enum BackgroundQuality { Basic, Advanced }

public sealed record FilterSettings
{
    public double Radius { get; init; } = 1;
    public double Angle { get; init; }
    public double Distance { get; init; } = 10;
    public double Amount { get; init; } = 10;
    public bool Gaussian { get; init; }
    public bool Monochromatic { get; init; }
    public double Distortion { get; init; }
    public CurvesSettings Curves { get; init; } = new();
    public ExposureSettings Exposure { get; init; } = new();
    public GradientMapSettings GradientMap { get; init; } = new();
    public GrainSettings Grain { get; init; } = new();
    public BackgroundQuality BackgroundQuality { get; init; } = BackgroundQuality.Basic;
    public double RefineEdges { get; init; } = 12;
    public double MatteContrast { get; init; } = 25;
    public double ShiftEdge { get; init; }

    private static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Min(hi, Math.Max(lo, v)) : fallback;
    public FilterSettings Normalized => this with
    {
        Radius = Clamp(Radius, 0.1, 250, 1), Angle = Clamp(Angle, -90, 90, 0), Distance = Clamp(Distance, 1, 2000, 10),
        Amount = Clamp(Amount, 0.1, 400, 10), Distortion = Clamp(Distortion, -100, 100, 0), RefineEdges = Clamp(RefineEdges, 0, 40, 12),
        MatteContrast = Clamp(MatteContrast, 0, 100, 25), ShiftEdge = Clamp(ShiftEdge, -10, 10, 0),
        Exposure = Exposure.Normalized, GradientMap = GradientMap.Normalized, Grain = Grain.Normalized,
    };
}

public sealed record FilterJob(FilterKind Kind, RasterImage Image, FilterSettings Settings, double Scale, SelectionClip? Selection, Affine Mapping, uint Seed = 0);

/// <summary>Finds the foreground subject in an image: white over the subject (the Mac app uses Apple's Vision).</summary>
public interface ISubjectSegmenter
{
    MaskImage SubjectMask(RasterImage image);
}

public static class SubjectRemoval
{
    public static ISubjectSegmenter? Segmenter { get; set; }

    /// <summary>Forget the last mask (after switching models).</summary>
    public static void ClearCache() { lock (gate) cache = null; }

    private static readonly object gate = new();
    private static (RasterImage Key, MaskImage Value)? cache;

    private static MaskImage Raw(RasterImage image)
    {
        lock (gate) if (cache is { } c && ReferenceEquals(c.Key, image)) return c.Value;
        var segmenter = Segmenter ?? throw new InvalidOperationException("Remove Background needs a subject-segmentation model, which isn't available on this PC.");
        var mask = segmenter.SubjectMask(image);
        lock (gate) cache = (image, mask);
        return mask;
    }

    /// <summary>Refine pulls the mask onto the image's edges; Shift Edge grows or shrinks it; Contrast hardens it.</summary>
    private static MaskImage Refined(MaskImage mask, RasterImage guide, FilterSettings settings, double limit)
    {
        if (settings.BackgroundQuality != BackgroundQuality.Advanced) return mask;
        int w = mask.Width, h = mask.Height;
        var values = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) values[y * w + x] = mask.ValueAt(x, y) / 255f;
        if (settings.RefineEdges > 0)
        {
            double factor = Math.Min(1, limit / Math.Max(w, h));
            int sw = Math.Max(1, (int)Math.Round(w * factor)), sh = Math.Max(1, (int)Math.Round(h * factor));
            int steps = Math.Max(1, (int)Math.Round(settings.RefineEdges * factor));
            var smallMask = Resize(values, w, h, sw, sh);
            var gray = new float[w * h];
            var pixels = guide.Pixels;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * guide.RowBytes + x * 4;
                    gray[y * w + x] = (0.299f * pixels[p] + 0.587f * pixels[p + 1] + 0.114f * pixels[p + 2]) / 255f;
                }
            var smallGuide = Resize(gray, w, h, sw, sh);
            var refined = Kernels.GuidedFilter(smallMask, smallGuide, sw, sh, steps, 1e-4f);
            values = Resize(refined, sw, sh, w, h);
        }
        if (settings.ShiftEdge != 0)
        {
            double reach = Math.Abs(settings.ShiftEdge);
            var blurred = PixelFilters.GaussianBlurGray(MaskImage.FromPixels(w, h, values.Select(v => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToArray()), reach / 2, clamp: true);
            float level = settings.ShiftEdge < 0 ? 0.75f : 0.25f;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) values[y * w + x] = blurred.ValueAt(x, y) / 255f >= level ? 1 : 0;
        }
        if (settings.MatteContrast > 0)
        {
            double strength = settings.MatteContrast / 100;
            float slope = (float)(1 / Math.Max(0.02, 1 - strength * 0.98));
            for (int i = 0; i < values.Length; i++) values[i] = Math.Clamp(values[i] * slope + (1 - slope) / 2, 0, 1);
        }
        return MaskImage.FromPixels(w, h, values.Select(v => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToArray());
    }

    private static float[] Resize(float[] source, int w, int h, int tw, int th)
    {
        if (w == tw && h == th) return source;
        var result = new float[tw * th];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                double sx = (x + 0.5) * w / tw - 0.5, sy = (y + 0.5) * h / th - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, w - 1), y0 = Math.Clamp((int)Math.Floor(sy), 0, h - 1);
                int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
                float fx = (float)Math.Clamp(sx - x0, 0, 1), fy = (float)Math.Clamp(sy - y0, 0, 1);
                float top = source[y0 * w + x0] + (source[y0 * w + x1] - source[y0 * w + x0]) * fx;
                float bottom = source[y1 * w + x0] + (source[y1 * w + x1] - source[y1 * w + x0]) * fx;
                result[y * tw + x] = top + (bottom - top) * fy;
            }
        return result;
    }

    public static MaskImage SubjectMask(RasterImage image, MaskImage? existing, FilterSettings settings)
    {
        var subject = Refined(Raw(image), image, settings, double.MaxValue);
        if (existing == null) return subject;
        var bytes = new byte[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) bytes[y * image.Width + x] = (byte)((existing.ValueAt(x, y) * subject.ValueAt(x, y) + 127) / 255);
        return MaskImage.FromPixels(image.Width, image.Height, bytes);
    }

    /// <summary>The preview: the layer with its background made transparent by the same mask the commit lays down.</summary>
    public static RasterImage Run(RasterImage image, FilterSettings settings)
    {
        var mask = Refined(Raw(image), image, settings, 1400);
        var bitmap = image.CopyBitmap();
        var pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int m = mask.ValueAt(x, y), p = y * bitmap.RowBytes + x * 4;
                for (int c = 0; c < 4; c++) pixels[p + c] = (byte)((pixels[p + c] * m + 127) / 255);
            }
        return RasterImage.Adopt(bitmap);
    }
}

public static class FilterRunner
{
    public const double MotionRadiusPerPixel = 0.28867513459481287; // 1 / √12
    public const double LensStrength = 0.35;

    public static RasterImage Run(FilterJob job)
    {
        var settings = job.Settings.Normalized;
        var image = job.Image;
        int w = image.Width, h = image.Height;
        RasterImage result;
        switch (job.Kind)
        {
            case FilterKind.Curves: result = Adjusted(image, b => AdjustmentPixels.Curves(b, settings.Curves)); break;
            case FilterKind.Exposure: result = Adjusted(image, b => AdjustmentPixels.Exposure(b, settings.Exposure)); break;
            case FilterKind.GradientMap: result = Adjusted(image, b => AdjustmentPixels.GradientMap(b, settings.GradientMap)); break;
            case FilterKind.Grain: result = Adjusted(image, b => AdjustmentPixels.Grain(b, settings.Grain, 0, 0, 1 / job.Scale, job.Seed)); break;
            case FilterKind.RemoveBackground: result = SubjectRemoval.Run(image, settings); break;
            case FilterKind.ContentAwareFill: result = ContentFill(job); break;
            case FilterKind.GaussianBlur: result = PixelFilters.GaussianBlur(image, settings.Radius * job.Scale); break;
            case FilterKind.MotionBlur: result = PixelFilters.MotionBlur(image, settings.Distance * job.Scale * MotionRadiusPerPixel, settings.Angle); break;
            case FilterKind.AddNoise:
                result = Adjusted(image, b => Kernels.AddNoise(b.GetPixelSpan(), w, h, b.RowBytes, (float)settings.Amount, settings.Gaussian, settings.Monochromatic, job.Seed));
                break;
            default:
            {
                var destination = PixelOps.NewRgba(w, h);
                Kernels.LensDistort(image.Pixels, destination.GetPixelSpan(), w, h, image.RowBytes, settings.Distortion / 100 * LensStrength);
                result = RasterImage.Adopt(destination);
                break;
            }
        }
        if (job.Selection is not { } selection) return result;
        return PixelFilters.BlendThroughSelection(result, image, selection, job.Mapping);
    }

    private static RasterImage Adjusted(RasterImage image, Action<SKBitmap> body)
    {
        var bitmap = image.CopyBitmap();
        body(bitmap);
        return RasterImage.Adopt(bitmap);
    }

    public static RasterImage ContentFill(FilterJob job)
    {
        if (job.Selection is not { } selection) throw new InvalidOperationException(NoSource);
        int w = job.Image.Width, h = job.Image.Height;
        var bitmap = job.Image.CopyBitmap();
        var coverage = SelectionRaster.CoverageOnGrid(selection, w, h, job.Mapping);
        int result = Kernels.ContentFill(bitmap.GetPixelSpan(), bitmap.RowBytes, coverage, w, w, h);
        if (result == 0) { bitmap.Dispose(); throw new InvalidOperationException(NoSource); }
        return RasterImage.Adopt(bitmap);
    }

    public const string NoSource = "Not enough unselected, opaque image pixels to synthesize a fill. Use a smaller selection with some surrounding image.";
}

/// <summary>One open filter panel (Filters.swift FilterEdit): previews render from a copy no larger than 2048 pixels.</summary>
public sealed class FilterEdit
{
    public FilterKind Kind { get; }
    public Guid LayerId { get; }
    public ImageAsset Original { get; }
    public LayerTransform Transform { get; }
    public SelectionClip? Selection { get; }
    public Affine Mapping { get; private set; }
    public RasterImage PreviewSource { get; private set; }
    public double PreviewScale { get; private set; }
    public Affine PreviewMapping { get; private set; }
    public RasterImage? GrownImage { get; private set; }
    public LayerTransform? GrownTransform { get; private set; }
    public double GrownMargin { get; private set; }
    public FilterSettings Settings { get; set; }
    public bool Preview { get; set; } = true;
    public bool Committing { get; set; }
    public string? PreviewError { get; set; }
    public bool Preparing { get; set; }
    public uint Seed { get; } = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    public RasterImage? PreparedPreview { get; set; }
    public FilterSettings? PreparedSettings { get; set; }
    public FilterJob? Pending { get; set; }
    public Task? PreviewTask { get; set; }
    public const double PreviewLimit = 2048;

    public FilterEdit(FilterKind kind, ImageLayer layer, SelectionClip? selection, FilterSettings settings, RectD? growingTo = null)
    {
        if (layer.Asset is not { } asset) throw ProjectException.Invalid();
        Kind = kind;
        Settings = settings.Normalized;
        LayerId = layer.Id;
        Original = asset;
        Transform = layer.Transform;
        Selection = selection;
        (Mapping, PreviewSource, PreviewScale, PreviewMapping) = Prepared(kind, asset.Image, layer.Transform);
        if (growingTo is { } area)
        {
            var toPixels = LayerTransform.PixelToDocument(layer.Transform, asset.Image.Width, asset.Image.Height).Inverted();
            Grow(area.Apply(toPixels).Integral);
        }
        GrowForBlur();
    }

    public static double BlurMargin(FilterKind kind, FilterSettings settings) => kind switch
    {
        FilterKind.GaussianBlur => settings.Radius * 3 + 2,
        FilterKind.MotionBlur => settings.Distance / 2 + 2,
        _ => 0,
    };

    public bool GrowForBlur()
    {
        double margin = BlurMargin(Kind, Settings);
        if (margin <= GrownMargin) return false;
        var bounds = new RectD(0, 0, Original.Image.Width, Original.Image.Height);
        Grow(bounds.Inset(-Math.Ceiling(margin), -Math.Ceiling(margin)));
        return true;
    }

    private void Grow(RectD extent)
    {
        var bounds = new RectD(0, 0, Original.Image.Width, Original.Image.Height);
        var target = bounds.Union(extent).Integral;
        if (target == bounds) return;
        if (target.Width > 30_000 || target.Height > 30_000 || target.Width * target.Height > 100_000_000) throw ProjectException.TooLarge();
        int tw = (int)target.Width, th = (int)target.Height;
        var bitmap = PixelOps.NewRgba(tw, th);
        var t = bitmap.GetPixelSpan();
        var s = Original.Image.Pixels;
        int ox = (int)-target.X, oy = (int)-target.Y;
        for (int y = 0; y < Original.Image.Height; y++)
            s.Slice(y * Original.Image.RowBytes, Original.Image.Width * 4).CopyTo(t.Slice((y + oy) * bitmap.RowBytes + ox * 4));
        var image = RasterImage.Adopt(bitmap);
        var toDocument = LayerTransform.PixelToDocument(Transform, Original.Image.Width, Original.Image.Height);
        var size = new SizeD(target.Width * Transform.Size.Width / bounds.Width, target.Height * Transform.Size.Height / bounds.Height);
        var middle = toDocument.Apply(new PointD(target.MidX, target.MidY));
        GrownImage = image;
        GrownTransform = Transform with { Size = size, Origin = new PointD(middle.X - size.Width / 2, middle.Y - size.Height / 2) };
        GrownMargin = Math.Min(Math.Min(bounds.MinX - target.MinX, bounds.MinY - target.MinY), Math.Min(target.MaxX - bounds.MaxX, target.MaxY - bounds.MaxY));
        (Mapping, PreviewSource, PreviewScale, PreviewMapping) = Prepared(Kind, image, GrownTransform.Value);
    }

    private static (Affine, RasterImage, double, Affine) Prepared(FilterKind kind, RasterImage source, LayerTransform placed)
    {
        var mapping = LayerTransform.PixelToDocument(placed, source.Width, source.Height);
        double factor = kind is FilterKind.AddNoise or FilterKind.Grain or FilterKind.ContentAwareFill or FilterKind.RemoveBackground
            ? 1 : Math.Min(1, PreviewLimit / Math.Max(source.Width, source.Height));
        if (factor >= 1) return (mapping, source, 1, mapping);
        int w = Math.Max(1, (int)(source.Width * factor)), h = Math.Max(1, (int)(source.Height * factor));
        var bitmap = PixelOps.NewRgba(w, h);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            canvas.DrawImage(source.Image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return (mapping, RasterImage.Adopt(bitmap), (double)w / source.Width, LayerTransform.PixelToDocument(placed, w, h));
    }

    public RasterImage? PreviewImage(Guid id) => Preview && id == LayerId ? PreparedPreview : null;
    public FilterJob PreviewJob => new(Kind, PreviewSource, Settings, PreviewScale, Selection, PreviewMapping, Seed);
}

public sealed partial class EditorSession
{
    public FilterEdit? FilterEdit { get; private set; }
    public FilterSettings FilterSettings { get; set; } = new();

    public bool CanAdjustColors => Levels == null && FilterEdit == null && document != null && ActiveLayer is { } layer && !IsProjectBusy && !IsImporting
        && brushStroke == null && PixelMove == null && RenamingLayerId == null && SelectedLayerIds.Count == 1 && !layer.IsGroup && !IsMaskSelected
        && layer.Asset != null && EffectiveVisibleIds.Contains(layer.Id) && Selection?.IsEmpty != true;

    public bool CanContentAwareFill => CanAdjustColors && !IsMaskSelected && Selection?.IsEmpty == false && FilterEdit == null && HueSaturation == null;

    public async Task BeginFilter(FilterKind kind)
    {
        if (kind == FilterKind.ContentAwareFill && !CanContentAwareFill) return;
        if (FilterEdit != null || HueSaturation != null || !CanAdjustColors) { Host.Beep(); return; }
        if (GradientEdit != null) { await CommitGradient(); await BeginFilter(kind); return; }
        CommitTransform(); CancelCrop(); CancelLasso();
        if (ActiveLayer is not { } layer || document == null) return;
        try
        {
            var settings = FilterSettings;
            if (kind == FilterKind.GradientMap)
                settings = settings with { GradientMap = new GradientMapSettings { Shadows = new AdjustmentColor(ForegroundColor), Highlights = new AdjustmentColor(BackgroundColor) } };
            RectD? area = null;
            if (kind == FilterKind.ContentAwareFill && Selection is { } s)
            {
                var clipped = s.Bounds.Intersect(new RectD(0, 0, document.Width, document.Height));
                if (!clipped.IsNull && !clipped.IsEmpty) area = clipped;
            }
            FilterEdit = new FilterEdit(kind, layer, Selection?.Clip(document.Size), settings, area);
            UpdateFilter(FilterEdit.Settings, true);
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    public void UpdateFilter(FilterSettings settings, bool preview)
    {
        if (FilterEdit is not { } edit || edit.Committing) return;
        edit.Settings = settings.Normalized;
        edit.Preview = preview;
        if (FilterEdit.BlurMargin(edit.Kind, edit.Settings) > edit.GrownMargin)
        {
            try { if (edit.GrowForBlur()) edit.PreparedPreview = null; }
            catch (Exception e) { BrushError = e.Message; }
        }
        if (PreviewAdjustmentEditing(preview)) return;
        if (edit.Kind.IsAutomatic() && edit.PreparedPreview != null && edit.PreparedSettings == edit.Settings) { InvalidateCanvas(); return; }
        if (!preview) { edit.Pending = null; edit.PreparedPreview = null; InvalidateCanvas(); return; }
        edit.Pending = edit.PreviewJob;
        RenderFilterPreview(edit);
        Notify();
    }

    private void RenderFilterPreview(FilterEdit edit)
    {
        if (FilterEdit != edit || edit.PreviewTask != null || edit.Pending == null) return;
        edit.PreviewTask = RunFilterPreviews(edit);
    }

    private async Task RunFilterPreviews(FilterEdit edit)
    {
        while (FilterEdit == edit && edit.Pending is { } job)
        {
            edit.Pending = null;
            edit.Preparing = true;
            edit.PreviewError = null;
            Notify();
            RasterImage? image = null;
            string? error = null;
            try { image = await Task.Run(() => FilterRunner.Run(job)); }
            catch (Exception e) { error = e.Message; }
            if (FilterEdit != edit) return;
            edit.Preparing = false;
            edit.PreviewError = error;
            if (edit.Preview || edit.Kind.IsAutomatic()) { edit.PreparedPreview = image; edit.PreparedSettings = job.Settings; InvalidateCanvas(); }
        }
        edit.PreviewTask = null;
        Notify();
    }

    public void CancelFilter()
    {
        if (ColorPicker?.Target is ColorPickerTarget.GradientMapEnd) CloseColorPicker(false);
        if (FinishAdjustmentEditing(false)) return;
        if (FilterEdit is not { } edit || edit.Committing) return;
        FilterEdit = null;
        InvalidateCanvas();
    }

    public async Task CommitFilter()
    {
        if (ColorPicker?.Target is ColorPickerTarget.GradientMapEnd) CloseColorPicker(true);
        if (FinishAdjustmentEditing(true)) return;
        if (FilterEdit is not { } edit || edit.Committing) return;
        if (edit.Kind.IsAutomatic())
        {
            if (edit.PreviewTask is { } task) await task;
            if (FilterEdit != edit || edit.Committing || edit.PreparedPreview == null || edit.PreviewError != null) return;
        }
        if ((edit.Kind == FilterKind.LensCorrection && edit.Settings.Distortion == 0)
            || (edit.Kind == FilterKind.Exposure && edit.Settings.Exposure == new ExposureSettings())
            || (edit.Kind == FilterKind.Grain && edit.Settings.Grain.Amount == 0)) { CancelFilter(); return; }
        edit.Committing = true;
        FilterSettings = edit.Settings;
        IsProjectBusy = true;
        Notify();
        try
        {
            if (edit.Kind == FilterKind.RemoveBackground) { await CommitBackgroundMask(edit); return; }
            var job = new FilterJob(edit.Kind, edit.GrownImage ?? edit.Original.Image, edit.Settings, 1, edit.Selection, edit.Mapping, edit.Seed);
            var cached = edit.Kind.IsAutomatic() && edit.PreparedSettings == edit.Settings ? edit.PreparedPreview : null;
            var grown = edit.GrownTransform;
            bool spreads = edit.Kind is FilterKind.GaussianBlur or FilterKind.MotionBlur;
            var made = await Task.Run(() =>
            {
                var image = cached ?? FilterRunner.Run(job);
                LayerTransform? placed = grown;
                if (spreads && grown is { } g) { var trimmed = PixelFilter.Trimmed(image, g); image = trimmed.Image; placed = trimmed.Transform; }
                return (Asset: PixelOps.Asset(image, job.Kind.Name()), Transform: placed);
            });
            if (document?.Layer(edit.LayerId) is not { } current || !ReferenceEquals(current.Asset?.Image, edit.Original.Image) || current.Transform != edit.Transform) return;
            var mask = current.Mask;
            if (edit.GrownTransform is { } grownTransform && current.Mask is { Placement: null } owned && (owned.Asset.Image.Width > 1 || owned.Asset.Image.Height > 1))
            {
                var carried = MaskPlacement.ClipImage(owned with { IsEnabled = true }, current.Transform, grownTransform, made.Asset.Image.Width, made.Asset.Image.Height);
                if (carried != null) mask = owned.Replacing(PixelOps.MaskAssetFrom(carried));
            }
            IsProjectBusy = false;
            BeginEdit(edit.Kind.Name());
            UpdateLayer(current.Id, l => l with { Asset = made.Asset, Transform = made.Transform ?? current.Transform, Mask = mask, IsGroup = false });
            EndEdit();
        }
        catch (Exception e) { BrushError = e.Message; }
        finally
        {
            FilterEdit = null;
            IsProjectBusy = false;
            InvalidateCanvas();
        }
    }

    /// <summary>Remove Background as a layer mask: subject white, background black; an existing mask keeps hiding too.</summary>
    private async Task CommitBackgroundMask(FilterEdit edit)
    {
        var source = edit.Original.Image;
        var current = document?.Layer(edit.LayerId);
        var existing = current?.Mask is { Placement: null } owned && owned.Asset.Image.Width == source.Width && owned.Asset.Image.Height == source.Height
            ? owned.Asset.Image : null;
        var selection = edit.Selection;
        var mapping = edit.Mapping;
        var settings = edit.Settings.Normalized;
        try
        {
            var made = await Task.Run(() =>
            {
                var mask = SubjectRemoval.SubjectMask(source, existing, settings);
                if (selection != null)
                    mask = PixelFilters.BlendThroughSelection(mask, existing ?? MaskImage.Solid(255, source.Width, source.Height), selection, mapping);
                return mask;
            });
            if (document?.Layer(edit.LayerId) is not { } layer || !ReferenceEquals(layer.Asset?.Image, edit.Original.Image) || layer.Transform != edit.Transform) return;
            var asset = PixelOps.MaskAssetFrom(made);
            IsProjectBusy = false;
            BeginEdit(edit.Kind.Name());
            UpdateLayer(layer.Id, l => l with { Mask = (l.Mask is { } m ? m.Replacing(asset) : new LayerMask(asset)) with { IsEnabled = true } });
            IsMaskSelected = true;
            EndEdit();
        }
        catch (Exception e) { BrushError = e.Message; }
    }
}
