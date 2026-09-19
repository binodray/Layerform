using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public enum HueSampleMode { Replace, Add, Remove }
public enum LevelsSample { Black, Gray, White }
public enum LevelsAuto { Contrast, Color, Neutral }

public sealed record HueTargetDrag(ColorRange Range, double Hue, double Saturation);

/// <summary>One open Hue/Saturation dialog; previews render from a copy up to 8000 pixels across.</summary>
public sealed class HueSaturationEdit
{
    public Guid LayerId { get; }
    public ImageAsset Original { get; }
    public SelectionClip? Selection { get; }
    public Affine PixelToDocument { get; }
    public RasterImage PreviewSource { get; }
    public Affine PreviewPixelToDocument { get; }
    public HueSaturationSettings Settings { get; set; } = new();
    public bool Preview { get; set; } = true;
    public RasterImage? PreparedPreview { get; set; }
    public const int PreviewLimit = 8000;

    public HueSaturationEdit(Guid layerId, ImageAsset original, SelectionClip? selection, LayerTransform transform)
    {
        LayerId = layerId;
        Original = original;
        Selection = selection;
        int w = original.Image.Width, h = original.Image.Height;
        PixelToDocument = LayerTransform.PixelToDocument(transform, w, h);
        double factor = Math.Min(1, (double)PreviewLimit / Math.Max(w, h));
        if (factor < 1)
        {
            int sw = Math.Max(1, (int)(w * factor)), sh = Math.Max(1, (int)(h * factor));
            PreviewSource = LevelsEdit.Scaled(original.Image, sw, sh);
            PreviewPixelToDocument = LayerTransform.PixelToDocument(transform, sw, sh);
        }
        else { PreviewSource = original.Image; PreviewPixelToDocument = PixelToDocument; }
    }

    public RasterImage? PreviewImage(Guid layer) => layer == LayerId ? PreparedPreview : null;

    public static RasterImage Run(RasterImage image, HueSaturationSettings settings, SelectionClip? selection, Affine mapping)
    {
        var bitmap = image.CopyBitmap();
        AdjustmentPixels.HueSaturation(bitmap, settings);
        var result = RasterImage.Adopt(bitmap);
        return selection == null ? result : Pixels.PixelFilters.BlendThroughSelection(result, image, selection, mapping);
    }
}

/// <summary>One open Levels dialog.</summary>
public sealed class LevelsEdit
{
    public Guid LayerId { get; }
    public ImageAsset Original { get; }
    public LayerTransform Transform { get; }
    public SelectionClip? Selection { get; }
    public Affine Mapping { get; }
    public RasterImage PreviewSource { get; }
    public Affine PreviewMapping { get; }
    public LevelsSample? SampleMode { get; set; }
    public LevelsSettings Settings { get; set; } = new();
    public bool Preview { get; set; } = true;
    public bool Committing { get; set; }
    public double[][] Histogram { get; set; } = Enumerable.Range(0, 4).Select(_ => new double[256]).ToArray();
    public bool HistogramReady { get; set; }
    public RasterImage? PreparedPreview { get; set; }

    public LevelsEdit(ImageLayer layer, SelectionClip? selection)
    {
        LayerId = layer.Id;
        Original = layer.Asset!;
        Transform = layer.Transform;
        Selection = selection;
        Mapping = LayerTransform.PixelToDocument(Transform, Original.Image.Width, Original.Image.Height);
        double factor = Math.Min(1, 8000.0 / Math.Max(Original.Image.Width, Original.Image.Height));
        if (factor < 1)
        {
            int w = Math.Max(1, (int)(Original.Image.Width * factor)), h = Math.Max(1, (int)(Original.Image.Height * factor));
            PreviewSource = Scaled(Original.Image, w, h);
            PreviewMapping = LayerTransform.PixelToDocument(Transform, w, h);
        }
        else { PreviewSource = Original.Image; PreviewMapping = Mapping; }
    }

    internal static RasterImage Scaled(RasterImage image, int w, int h)
    {
        var bitmap = PixelOps.NewRgba(w, h);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            canvas.DrawImage(image.Image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return RasterImage.Adopt(bitmap);
    }

    public RasterImage? PreviewImage(Guid id) => Preview && id == LayerId ? PreparedPreview : null;

    public static RasterImage Run(RasterImage image, LevelsSettings settings, SelectionClip? selection, Affine mapping)
    {
        if (settings.IsIdentity) return image;
        var bitmap = image.CopyBitmap();
        AdjustmentPixels.Levels(bitmap, settings);
        var result = RasterImage.Adopt(bitmap);
        return selection == null ? result : Pixels.PixelFilters.BlendThroughSelection(result, image, selection, mapping);
    }

    /// <summary>levels_histogram: RGB is the mean of the channel histograms, alpha- (and selection-) weighted.</summary>
    public static double[][] ComputeHistogram(RasterImage image, SelectionClip? selection, Affine mapping)
    {
        var bins = new double[1024];
        byte[]? coverage = selection == null ? null : SelectionRaster.CoverageOnGrid(selection, image.Width, image.Height, mapping);
        var pixels = image.Pixels;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int p = y * image.RowBytes + x * 4;
                if (pixels[p + 3] == 0) continue;
                double weight = pixels[p + 3] / 255.0 * (coverage == null ? 1 : coverage[y * image.Width + x] / 255.0);
                for (int channel = 0; channel < 3; channel++)
                {
                    int value = (int)Math.Min(255, Math.Round(pixels[p + channel] * 255.0 / pixels[p + 3], MidpointRounding.AwayFromZero));
                    bins[(channel + 1) * 256 + value] += weight;
                    bins[value] += weight / 3.0;
                }
            }
        return Enumerable.Range(0, 4).Select(c => bins.Skip(c * 256).Take(256).ToArray()).ToArray();
    }

    /// <summary>Display-only vertical scaling that caps isolated spikes.</summary>
    public static double HistogramScale(double[] bins)
    {
        var positive = bins.Where(b => double.IsFinite(b) && b > 0).ToList();
        if (positive.Count == 0) return 0;
        double peak = positive.Max();
        var interior = bins.Skip(1).Take(254).Where(b => double.IsFinite(b) && b > 0).OrderBy(b => b).ToList();
        if (interior.Count == 0) return peak;
        double typical = interior[(int)((interior.Count - 1) * 0.95)];
        return Math.Min(peak, typical * 4);
    }

    public static LevelsSettings Auto(LevelsAuto mode, double[][] histogram)
    {
        var result = new LevelsSettings();
        (double, double)? Endpoints(double[] bins)
        {
            double total = bins.Sum();
            if (!(total > 0)) return null;
            double sum = 0; int low = 0, high = 255;
            for (int i = 0; i < 256; i++) { sum += bins[i]; if (sum > total * 0.001) { low = i; break; } }
            sum = 0;
            for (int i = 255; i >= 0; i--) { sum += bins[i]; if (sum > total * 0.001) { high = i; break; } }
            return low < high ? (low, high) : null;
        }
        if (mode == LevelsAuto.Contrast)
        {
            var limits = histogram.Skip(1).Select(Endpoints).Where(e => e != null).Select(e => e!.Value).ToList();
            if (limits.Count > 0)
            {
                double low = limits.Min(l => l.Item1), high = limits.Max(l => l.Item2);
                if (low < high) result = result.WithRange(0, new LevelRange(low, 1, high));
            }
            return result;
        }
        for (int c = 1; c <= 3; c++)
        {
            if (Endpoints(histogram[c]) is not { } e) continue;
            var range = new LevelRange(e.Item1, 1, e.Item2);
            if (mode == LevelsAuto.Neutral)
            {
                double total = histogram[c].Sum();
                double mean = histogram[c].Select((count, index) => range.Apply(index / 255.0) * count).Sum() / total;
                if (mean > 0 && mean < 1) range = range with { Gamma = Math.Clamp(Math.Log(mean) / Math.Log(0.5), 0.1, 9.99) };
            }
            result = result.WithRange(c, range);
        }
        return result;
    }

    /// <summary>Eyedropper samples (unpremultiplied RGB 0–1) calibrate all three channels together.</summary>
    public static LevelsSettings Sampling(LevelsSettings settings, double[] rgb, LevelsSample mode)
    {
        var result = settings.WithRange(0, new LevelRange());
        for (int c = 1; c <= 3; c++)
        {
            var range = result.Ranges[c];
            double v = rgb[c - 1] * 255;
            switch (mode)
            {
                case LevelsSample.Black: range = range with { Black = Math.Min(range.White - 1, Math.Max(0, v)) }; break;
                case LevelsSample.White: range = range with { White = Math.Max(range.Black + 1, Math.Min(255, v)) }; break;
                default:
                    double fraction = (v - range.Black) / (range.White - range.Black);
                    if (!(fraction > 0 && fraction < 1)) continue;
                    range = range with { Gamma = Math.Log(fraction) / Math.Log(0.5) };
                    break;
            }
            result = result.WithRange(c, (range with { OutputBlack = 0, OutputWhite = 255 }).Normalized);
        }
        return result;
    }
}

public abstract record ColorPickerTarget
{
    public sealed record Palette(bool Background) : ColorPickerTarget;
    public sealed record GradientMapEnd(bool Highlights) : ColorPickerTarget;
    public string Title => this switch
    {
        Palette { Background: true } => "Color Picker (Background Color)",
        Palette => "Color Picker (Foreground Color)",
        GradientMapEnd { Highlights: true } => "Color Picker (Gradient Map Highlights)",
        _ => "Color Picker (Gradient Map Shadows)",
    };
}

/// <summary>Hue in degrees, saturation and brightness 0…1: the picker's source of truth, so hue survives grays.</summary>
public record struct PickerHsb(double Hue, double Saturation, double Brightness)
{
    public PickerHsb(PaletteColor color) : this(0, 0, 0) { SetRgb(color); }

    public PaletteColor Rgb
    {
        get
        {
            double h = (Hue % 360 + 360) % 360 / 60, c = Brightness * Saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = Brightness - c;
            var (r, g, b) = ((int)h) switch
            {
                0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
            };
            return new PaletteColor(r + m, g + m, b + m);
        }
    }

    public void SetRgb(PaletteColor color)
    {
        double high = Math.Max(color.Red, Math.Max(color.Green, color.Blue)), low = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        double delta = high - low;
        Brightness = high;
        if (high > 0) Saturation = delta / high;
        if (!(delta > 0)) return;
        double h;
        if (high == color.Red) h = (color.Green - color.Blue) / delta;
        else if (high == color.Green) h = (color.Blue - color.Red) / delta + 2;
        else h = (color.Red - color.Green) / delta + 4;
        h *= 60;
        Hue = h < 0 ? h + 360 : h;
    }
}

public sealed class ColorPickerState
{
    public ColorPickerTarget Target { get; }
    public PaletteColor Original { get; }
    public PickerHsb Hsb;
    public PaletteColor Color => Hsb.Rgb.Quantized();
    public ColorPickerState(ColorPickerTarget target, PaletteColor original)
    {
        Target = target;
        Original = original;
        Hsb = new PickerHsb(original);
    }
}

public sealed partial class EditorSession
{
    public HueSaturationEdit? HueSaturation { get; private set; }
    public LevelsEdit? Levels { get; private set; }
    public HueSampleMode? HueSampleMode { get; set; }
    public bool HueTargeting { get; set; }
    public HueTargetDrag? HueTargetDragState { get; private set; }
    public Guid? AdjustmentEditingId { get; private set; }
    public LayerAdjustment? AdjustmentOriginal { get; private set; }
    public ColorPickerState? ColorPicker { get; private set; }
    private Task? hueTask;
    private HueSaturationSettings? huePending;
    private Task? levelsTask;
    private LevelsSettings? levelsPending;

    // MARK: Palette (ColorPalette.swift)

    public PaletteColor ForegroundColor
    {
        get => new(brushSettings.Red, brushSettings.Green, brushSettings.Blue);
        set => BrushSettings = brushSettings with { Red = value.Red, Green = value.Green, Blue = value.Blue };
    }

    public bool CanEditPalette => !IsProjectBusy && brushStroke == null;

    public PaletteColor PaletteColorFor(bool background)
    {
        if (IsMaskSelected) return (background ? !MaskPaintWhite : MaskPaintWhite) ? PaletteColor.White : PaletteColor.Black;
        return background ? BackgroundColor : ForegroundColor;
    }

    public void SetPaletteColor(PaletteColor color, bool background)
    {
        if (!CanEditPalette) return;
        if (IsMaskSelected) { bool white = color == PaletteColor.White; MaskPaintWhite = background ? !white : white; }
        else if (background) BackgroundColor = color;
        else ForegroundColor = color;
    }

    public void SwapPaletteColors()
    {
        if (!CanEditPalette) return;
        if (IsMaskSelected) MaskPaintWhite = !MaskPaintWhite;
        else { var old = ForegroundColor; ForegroundColor = BackgroundColor; BackgroundColor = old; }
    }

    public void ResetPaletteColors()
    {
        if (!CanEditPalette) return;
        if (IsMaskSelected) MaskPaintWhite = false;
        else { ForegroundColor = PaletteColor.Black; BackgroundColor = PaletteColor.White; }
    }

    public void OpenColorPicker(bool background)
    {
        if (!CanEditPalette || IsMaskSelected) return;
        ColorPicker = new ColorPickerState(new ColorPickerTarget.Palette(background), PaletteColorFor(background));
        Notify();
    }

    public void CloseColorPicker(bool commit)
    {
        if (ColorPicker is { } picker)
        {
            switch (picker.Target)
            {
                case ColorPickerTarget.Palette p:
                    if (commit && !IsMaskSelected) SetPaletteColor(picker.Color, p.Background);
                    break;
                case ColorPickerTarget.GradientMapEnd g:
                    SetGradientMapColor(commit ? picker.Color : picker.Original, g.Highlights);
                    break;
            }
        }
        ColorPicker = null;
        Notify();
    }

    public void OpenGradientMapColorPicker(bool highlights)
    {
        if (!CanEditPalette || ColorPicker != null || FilterEdit is not { Kind: FilterKind.GradientMap, Committing: false } edit) return;
        var value = highlights ? edit.Settings.GradientMap.Highlights : edit.Settings.GradientMap.Shadows;
        ColorPicker = new ColorPickerState(new ColorPickerTarget.GradientMapEnd(highlights), new PaletteColor(value.Red, value.Green, value.Blue));
        Notify();
    }

    public void PreviewGradientMapColor()
    {
        if (ColorPicker is { Target: ColorPickerTarget.GradientMapEnd g } picker) SetGradientMapColor(picker.Color, g.Highlights);
    }

    private void SetGradientMapColor(PaletteColor color, bool highlights)
    {
        if (FilterEdit is not { Kind: FilterKind.GradientMap, Committing: false } edit) return;
        var map = highlights ? edit.Settings.GradientMap with { Highlights = new AdjustmentColor(color) }
                             : edit.Settings.GradientMap with { Shadows = new AdjustmentColor(color) };
        var settings = edit.Settings with { GradientMap = map };
        if (settings == edit.Settings) return;
        UpdateFilter(settings, edit.Preview);
    }

    public void SampleIntoColorPicker(PointD point)
    {
        if (ColorPicker is not { } picker || SampleCompositeColor(point) is not { } color) return;
        picker.Hsb.SetRgb(color);
        Notify();
    }

    /// <summary>Composited sRGB colour of the visible layers at one document pixel; null outside or over transparency.</summary>
    public PaletteColor? SampleCompositeColor(PointD point)
    {
        if (document == null || point.X < 0 || point.Y < 0 || point.X >= document.Width || point.Y >= document.Height) return null;
        var bitmap = PixelOps.NewRgba(1, 1);
        using (var surface = new RenderSurface(bitmap, Affine.Translation(-Math.Floor(point.X), -Math.Floor(point.Y))))
            CompositeRenderer.Render(new LiveCompositeSource(this, document), surface);
        var p = bitmap.GetPixelSpan();
        byte a = p[3];
        if (a == 0) { bitmap.Dispose(); return null; }
        double Channel(byte v) => Math.Round(Math.Min(a, (double)v) / a * 255) / 255;
        var result = new PaletteColor(Channel(p[0]), Channel(p[1]), Channel(p[2]));
        bitmap.Dispose();
        return result;
    }

    // MARK: Hue/Saturation

    public void BeginHueSaturation()
    {
        if (HueSaturation != null || !CanAdjustColors) { Host.Beep(); return; }
        CommitTransform();
        if (GradientEdit != null) ResolveGradient();
        if (document == null || ActiveLayer is not { Asset: { } asset } layer) return;
        try { HueSaturation = new HueSaturationEdit(layer.Id, asset, Selection?.Clip(document.Size), layer.Transform); Notify(); }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    public void UpdateHueSaturation(HueSaturationSettings settings, bool preview)
    {
        if (HueSaturation is not { } edit) return;
        edit.Settings = settings;
        edit.Preview = preview;
        Notify();
        if (PreviewAdjustmentEditing(preview)) return;
        if (!preview || settings.IsIdentity)
        {
            huePending = null;
            edit.PreparedPreview = null;
            InvalidateCanvas();
            return;
        }
        huePending = settings;
        if (hueTask == null) hueTask = RunHuePreviews(edit);
    }

    private async Task RunHuePreviews(HueSaturationEdit edit)
    {
        while (HueSaturation == edit && huePending is { } settings)
        {
            huePending = null;
            var source = edit.PreviewSource;
            var selection = edit.Selection;
            var mapping = edit.PreviewPixelToDocument;
            try
            {
                var image = await Task.Run(() => HueSaturationEdit.Run(source, settings, selection, mapping));
                if (HueSaturation != edit) break;
                edit.PreparedPreview = image;
                InvalidateCanvas();
            }
            catch (Exception e) { BrushError = e.Message; break; }
        }
        hueTask = null;
    }

    public async Task CommitHueSaturation()
    {
        if (FinishAdjustmentEditing(true)) return;
        if (HueSaturation is not { } edit) return;
        HueSampleMode = null;
        HueTargeting = false;
        HueTargetDragState = null;
        huePending = null;
        var settings = edit.Settings;
        try
        {
            if (settings.IsIdentity) return;
            IsProjectBusy = true;
            var image = await Task.Run(() => HueSaturationEdit.Run(edit.Original.Image, settings, edit.Selection, edit.PixelToDocument));
            IsProjectBusy = false;
            if (document?.Layer(edit.LayerId) is not { } current || !ReferenceEquals(current.Asset?.Image, edit.Original.Image)) return;
            BeginEdit("Hue/Saturation");
            UpdateLayer(current.Id, l => l with { Asset = PixelOps.Asset(image, current.Name), IsGroup = false });
            EndEdit();
        }
        catch (Exception e) { BrushError = e.Message; }
        finally { HueSaturation = null; IsProjectBusy = false; InvalidateCanvas(); }
    }

    public void CancelHueSaturation()
    {
        if (FinishAdjustmentEditing(false)) return;
        HueSampleMode = null;
        HueTargeting = false;
        HueTargetDragState = null;
        if (HueSaturation == null) return;
        huePending = null;
        HueSaturation = null;
        InvalidateCanvas();
    }

    public double? SampledHue(PointD point)
    {
        if (SampleCompositeColor(point) is not { } color) return null;
        var hsb = new PickerHsb(color);
        return hsb.Saturation > 0.02 ? hsb.Hue : null;
    }

    public void SampleHueRange(PointD point)
    {
        if (HueSaturation is not { } edit || HueSampleMode is not { } mode) return;
        var settings = edit.Settings;
        if (settings.Range == ColorRange.Master || settings.Colorize || SampledHue(point) is not { } hue) { Host.Beep(); return; }
        var band = mode switch
        {
            Editing.HueSampleMode.Replace => settings.Band.CenteredOn(hue),
            Editing.HueSampleMode.Add => settings.Band.Including(hue),
            _ => settings.Band.Excluding(hue),
        };
        UpdateHueSaturation(settings.WithBand(band), edit.Preview);
    }

    public bool BeginHueTargeting(PointD point)
    {
        if (HueSaturation is not { } edit || !HueTargeting || edit.Settings.Colorize || SampledHue(point) is not { } hue) { Host.Beep(); return false; }
        var settings = edit.Settings;
        var range = ColorRanges.Colors.OrderBy(r => settings.Weight(r, hue)).Last();
        settings = settings with { Range = range };
        var adjustment = settings.Adjustments.TryGetValue(range, out var a) ? a : RangeAdjustment.Zero;
        HueTargetDragState = new HueTargetDrag(range, adjustment.Hue, adjustment.Saturation);
        UpdateHueSaturation(settings, edit.Preview);
        return true;
    }

    public void DragHueTargeting(double viewDelta, bool adjustsHue)
    {
        if (HueSaturation is not { } edit || HueTargetDragState is not { } drag) return;
        var current = edit.Settings.Adjustments.TryGetValue(drag.Range, out var a) ? a : RangeAdjustment.Zero;
        var next = adjustsHue ? current with { Hue = Math.Clamp(drag.Hue + viewDelta / 2, -180, 180) }
                              : current with { Saturation = Math.Clamp(drag.Saturation + viewDelta / 2, -100, 100) };
        UpdateHueSaturation(edit.Settings.WithAdjustment(drag.Range, next), edit.Preview);
    }

    public void EndHueTargeting() => HueTargetDragState = null;

    // MARK: Levels

    public void BeginLevels()
    {
        if (Levels != null || HueSaturation != null || !CanAdjustColors) return;
        _ = BeginLevelsAsync();
    }

    private async Task BeginLevelsAsync()
    {
        if (GradientEdit != null) await CommitGradient();
        CommitTransform(); CancelCrop(); CancelLasso();
        if (ActiveLayer is not { } layer || document == null) return;
        try
        {
            var edit = new LevelsEdit(layer, Selection?.Clip(document.Size));
            Levels = edit;
            Notify();
            await LoadHistogram(edit);
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    private async Task LoadHistogram(LevelsEdit edit)
    {
        var source = edit.PreviewSource;
        var selection = edit.Selection;
        var mapping = edit.PreviewMapping;
        var bins = await Task.Run(() => LevelsEdit.ComputeHistogram(source, selection, mapping));
        if (Levels != edit) return;
        edit.Histogram = bins;
        edit.HistogramReady = true;
        Notify();
    }

    public void UpdateLevels(LevelsSettings settings, bool preview)
    {
        if (Levels is not { } edit || edit.Committing) return;
        edit.Settings = settings;
        edit.Preview = preview;
        Notify();
        if (PreviewAdjustmentEditing(preview)) return;
        if (!preview || settings.IsIdentity) { levelsPending = null; edit.PreparedPreview = null; InvalidateCanvas(); return; }
        levelsPending = settings;
        if (levelsTask == null) levelsTask = RunLevelsPreviews(edit);
    }

    private async Task RunLevelsPreviews(LevelsEdit edit)
    {
        while (Levels == edit && levelsPending is { } settings)
        {
            levelsPending = null;
            var source = edit.PreviewSource;
            var selection = edit.Selection;
            var mapping = edit.PreviewMapping;
            var image = await Task.Run(() => LevelsEdit.Run(source, settings, selection, mapping));
            if (Levels != edit) break;
            if (edit.Preview && !edit.Settings.IsIdentity) { edit.PreparedPreview = image; InvalidateCanvas(); }
        }
        levelsTask = null;
    }

    public void CancelLevels()
    {
        if (FinishAdjustmentEditing(false)) return;
        if (Levels is not { } edit || edit.Committing) return;
        Levels = null;
        levelsPending = null;
        InvalidateCanvas();
    }

    public async Task CommitLevels()
    {
        if (FinishAdjustmentEditing(true)) return;
        if (Levels is not { } edit || edit.Committing) return;
        if (edit.Settings.IsIdentity) { CancelLevels(); return; }
        edit.Committing = true;
        IsProjectBusy = true;
        try
        {
            var settings = edit.Settings;
            var image = await Task.Run(() => LevelsEdit.Run(edit.Original.Image, settings, edit.Selection, edit.Mapping));
            IsProjectBusy = false;
            if (document?.Layer(edit.LayerId) is not { } current || !ReferenceEquals(current.Asset?.Image, edit.Original.Image) || current.Transform != edit.Transform) return;
            BeginEdit("Levels");
            UpdateLayer(current.Id, l => l with { Asset = PixelOps.Asset(image, current.Name), IsGroup = false });
            EndEdit();
        }
        catch (Exception e) { BrushError = e.Message; }
        finally { Levels = null; IsProjectBusy = false; InvalidateCanvas(); }
    }

    public void AutoLevels(LevelsAuto mode)
    {
        if (Levels is not { HistogramReady: true, Committing: false } edit) return;
        edit.SampleMode = null;
        UpdateLevels(LevelsEdit.Auto(mode, edit.Histogram), edit.Preview);
    }

    public void SampleLevels(PointD point)
    {
        if (Levels is not { SampleMode: { } mode, Committing: false } edit || document == null) return;
        if (point.X < 0 || point.Y < 0 || point.X >= document.Width || point.Y >= document.Height) return;
        var pixel = edit.Mapping.Inverted().Apply(point);
        int x = (int)Math.Floor(pixel.X), y = (int)Math.Floor(pixel.Y);
        if (x < 0 || y < 0 || x >= edit.Original.Image.Width || y >= edit.Original.Image.Height) return;
        var (r, g, b, a) = edit.Original.Image.PixelAt(x, y);
        if (a == 0) return;
        var rgb = new[] { Math.Min(1, (double)r / a), Math.Min(1, (double)g / a), Math.Min(1, (double)b / a) };
        UpdateLevels(LevelsEdit.Sampling(edit.Settings, rgb, mode), edit.Preview);
    }

    // MARK: Adjustment layers (LayerAdjustment.swift, AdjustmentEditing.swift)

    public void AddAdjustment(AdjustmentKind kind)
    {
        if (!CanEditLayers || document == null || document.Layers.Count >= 10_000) return;
        var adjustment = new LayerAdjustment(kind);
        if (kind == AdjustmentKind.GradientMap)
            adjustment = adjustment with { GradientMapSettings = new GradientMapSettings { Shadows = new AdjustmentColor(ForegroundColor), Highlights = new AdjustmentColor(BackgroundColor) } };
        if (kind == AdjustmentKind.Grain)
            adjustment = adjustment with { GrainSettings = new GrainSettings(Seed: (uint)Random.Shared.NextInt64(0, uint.MaxValue)) };
        var layer = ImageLayer.Blank(kind.ToName(), document.Size) with
        {
            Adjustment = adjustment, ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId,
        };
        int index = ActiveLayerId is { } a && document.IndexOf(a) is >= 0 and var i ? i + 1 : document.Layers.Count;
        BeginEdit($"New {kind.ToName()} Adjustment");
        SetLayers(document.Layers.Insert(index, layer));
        if (layer.ParentId is { } parent) CollapsedGroupIds.Remove(parent);
        ActiveLayerId = layer.Id;
        EndEdit();
        AdjustmentEditingId = layer.Id;
        _ = BeginAdjustmentEditing(layer.Id);
    }

    public void UpdateAdjustment(Guid id, LayerAdjustment value)
    {
        if (document?.IndexOf(id) is not (>= 0 and var index) || !value.IsValid) return;
        ReplaceLayer(index, document.Layers[index] with { Adjustment = value });
    }

    public void EditAdjustment(Guid id)
    {
        if (!CanEditLayers || document?.Layer(id)?.Adjustment == null) return;
        ActiveLayerId = id;
        AdjustmentEditingId = id;
        _ = BeginAdjustmentEditing(id);
    }

    /// <summary>Uses the ordinary colour editors but sends their changes to the layer's settings; the source (everything
    /// beneath) is only for the histogram and sampling.</summary>
    public async Task BeginAdjustmentEditing(Guid id)
    {
        if (AdjustmentEditingId != id || AdjustmentOriginal != null || Levels != null || HueSaturation != null || FilterEdit != null
            || ProjectSnapshot() is not { } snapshot) return;
        var record = snapshot.Manifest.Layers.FirstOrDefault(l => l.Id == id);
        if (record?.Adjustment is not { } original) return;
        var underneath = LayerHierarchy.Entries(snapshot.Manifest.Layers).TakeWhile(e => e.Layer.Id != id).Select(e => e.Layer.Id).ToHashSet();
        var manifest = snapshot.Manifest with
        {
            Layers = snapshot.Manifest.Layers.Select(l => l.IsGroup != true && !underneath.Contains(l.Id) ? l with { IsVisible = false } : l).ToList(),
        };
        try
        {
            var raster = await Task.Run(() => ImageExporter.Render(snapshot with { Manifest = manifest }));
            if (AdjustmentEditingId != id || AdjustmentOriginal != null) return;
            var asset = PixelOps.Asset(raster.Image, "Adjustment input");
            var layer = ImageLayer.FromAsset(asset, PointD.Zero);
            switch (original.Kind)
            {
                case AdjustmentKind.Levels:
                    var levels = new LevelsEdit(layer, null) { Settings = original.Levels };
                    Levels = levels;
                    AdjustmentOriginal = original;
                    BeginEdit($"Edit {original.Kind.ToName()} Adjustment");
                    Notify();
                    await LoadHistogram(levels);
                    return;
                case AdjustmentKind.HueSaturation:
                    HueSaturation = new HueSaturationEdit(layer.Id, asset, null, layer.Transform) { Settings = original.ResolvedHsv };
                    break;
                default:
                    var settings = new FilterSettings
                    {
                        Curves = original.Curves, Exposure = original.Exposure, GradientMap = original.GradientMap, Grain = original.Grain,
                    };
                    FilterEdit = new FilterEdit(original.Kind.ToFilterKind() ?? FilterKind.Curves, layer, null, settings);
                    break;
            }
            AdjustmentOriginal = original;
            BeginEdit($"Edit {original.Kind.ToName()} Adjustment");
            Notify();
        }
        catch (Exception e)
        {
            if (AdjustmentEditingId == id) AdjustmentEditingId = null;
            BrushError = e.Message;
            Notify();
        }
    }

    private LayerAdjustment? EditedAdjustment
    {
        get
        {
            if (AdjustmentOriginal is not { } value) return null;
            switch (value.Kind)
            {
                case AdjustmentKind.Levels: return Levels == null ? null : value with { Levels = Levels.Settings };
                case AdjustmentKind.HueSaturation: return HueSaturation == null ? null : value with { HsvSettings = HueSaturation.Settings };
                default:
                    if (FilterEdit is not { } filter) return null;
                    return value.Kind switch
                    {
                        AdjustmentKind.Exposure => value with { ExposureSettings = filter.Settings.Exposure },
                        AdjustmentKind.GradientMap => value with { GradientMapSettings = filter.Settings.GradientMap },
                        AdjustmentKind.Grain => value with { GrainSettings = filter.Settings.Grain },
                        _ => value with { Curves = filter.Settings.Curves },
                    };
            }
        }
    }

    public bool PreviewAdjustmentEditing(bool preview)
    {
        if (AdjustmentEditingId is not { } id || AdjustmentOriginal is not { } original || EditedAdjustment is not { } value) return false;
        UpdateAdjustment(id, preview ? value : original);
        return true;
    }

    /// <summary>OK keeps the edited settings; Cancel (and closing the panel) restores them.</summary>
    public bool FinishAdjustmentEditing(bool commit)
    {
        if (AdjustmentEditingId is not { } id || AdjustmentOriginal is not { } original) return false;
        UpdateAdjustment(id, commit ? (EditedAdjustment ?? original) : original);
        huePending = null;
        levelsPending = null;
        HueSampleMode = null;
        HueTargeting = false;
        HueTargetDragState = null;
        Levels = null;
        HueSaturation = null;
        FilterEdit = null;
        EndEdit();
        AdjustmentOriginal = null;
        AdjustmentEditingId = null;
        InvalidateCanvas();
        return true;
    }
}
