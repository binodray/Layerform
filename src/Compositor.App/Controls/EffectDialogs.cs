using Compositor.Editing;
using Compositor.Imaging;
using Compositor.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;

namespace Compositor.App.Controls;

/// <summary>The layer right-click effects — Change Color, Gradient Overlay, Add Shadow — each with a live preview.</summary>
public static class EffectDialogs
{
    private const int PreviewSize = 260;

    /// <summary>A small copy of the layer, so previews stay instant on big images.</summary>
    private static (RasterImage Image, double Scale) Small(RasterImage image)
    {
        double scale = Math.Min(1, (double)PreviewSize / Math.Max(image.Width, image.Height));
        if (scale >= 1) return (image, 1);
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var bitmap = PixelOps.NewRgba(w, h);
        using (var canvas = new SKCanvas(bitmap))
            canvas.DrawImage(image.Image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return (RasterImage.Adopt(bitmap), scale);
    }

    /// <summary>The preview on a checkerboard, as the canvas shows transparency.</summary>
    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap Checkered(RasterImage image, Action<SKCanvas>? drawUnder = null, int pad = 0)
    {
        int w = image.Width + 2 * pad, h = image.Height + 2 * pad;
        using var bitmap = PixelOps.NewRgba(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var dark = new SKPaint { Color = new SKColor(82, 82, 82) };
            canvas.Clear(new SKColor(56, 56, 56));
            for (int y = 0; y < h; y += 8) for (int x = 0; x < w; x += 8) if ((x / 8 + y / 8) % 2 == 0) canvas.DrawRect(x, y, 8, 8, dark);
            drawUnder?.Invoke(canvas);
            canvas.DrawImage(image.Image, pad, pad);
        }
        return Thumbnails.ToWriteable(bitmap);
    }

    private static Border Frame(Image image) => new()
    {
        Child = image, Width = PreviewSize + 40, Height = PreviewSize + 40, CornerRadius = new CornerRadius(8),
        Background = Ui.Solid(255, 0x1B, 0x1B, 0x1B), Padding = new Thickness(8),
    };

    private static Windows.UI.Color ToUi(PaletteColor c) => Windows.UI.Color.FromArgb(255, c.R8, c.G8, c.B8);
    private static PaletteColor FromUi(Windows.UI.Color c) => new(c.R / 255.0, c.G / 255.0, c.B / 255.0);

    /// <summary>A colour swatch button that opens Windows' colour picker.</summary>
    private static Button ColorButton(Func<PaletteColor> get, Action<PaletteColor> set, string tooltip)
    {
        var chip = new Border { Width = 44, Height = 22, CornerRadius = new CornerRadius(5), BorderBrush = Ui.Solid(80, 255, 255, 255), BorderThickness = new Thickness(1) };
        var button = new Button { Content = chip, Padding = new Thickness(3), MinWidth = 0 };
        ToolTipService.SetToolTip(button, tooltip);
        void Show() => chip.Background = new SolidColorBrush(ToUi(get()));
        Show();
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = true, IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box, Color = ToUi(get()),
        };
        picker.ColorChanged += (_, e) => { set(FromUi(e.NewColor)); Show(); };
        button.Flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Right };
        button.Flyout.Opening += (_, _) => picker.Color = ToUi(get());
        return button;
    }

    private static ContentDialog Dialog(XamlRoot root, string title, string ok)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, PrimaryButtonText = ok, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        // Wide enough for the preview beside the controls.
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;
        return dialog;
    }

    private static FrameworkElement Labeled(string title, FrameworkElement control)
    {
        var label = Ui.Label(title, 12);
        label.Width = 70;
        label.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(8, label, control);
    }

    private static Slider Slider(double min, double max, double value, double step = 1) =>
        new() { Minimum = min, Maximum = max, Value = value, StepFrequency = step, Width = 200, VerticalAlignment = VerticalAlignment.Center };

    public static async Task<(PaletteColor Color, RecolorMode Mode)?> ChangeColor(XamlRoot root, RasterImage layer, PaletteColor initial)
    {
        var (small, _) = Small(layer);
        var color = initial;
        var mode = RecolorMode.Replace;
        var preview = new Image { Stretch = Stretch.Uniform };
        void Update() => preview.Source = Checkered(LayerEffects.Recolor(small, color, mode));
        var binder = new Binder();
        var modes = Ui.Segmented(new[] { (RecolorMode.Replace, "Solid color"), (RecolorMode.Tint, "Tint (keep shading)") }, () => mode,
            v => { mode = v; binder.Update(); Update(); }, binder, "Solid fills every visible pixel; Tint keeps the layer's light and shade");
        var colorButton = ColorButton(() => color, c => { color = c; Update(); }, "Choose the new color");
        var dialog = Dialog(root, "Change Color", "Apply");
        dialog.Content = Ui.Row(18, Frame(preview), new StackPanel
        {
            // The labelled mode row needs more room than the helper copy alone. At
            // common display scaling values, a 260-wide column clipped the Tint
            // option and the end of the helper text.
            Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Width = 320,
            Children =
            {
                Labeled("Color", colorButton), Labeled("Mode", modes),
                new TextBlock { Text = "Transparent areas stay transparent. Undo (Ctrl+Z) restores the original.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Secondary },
            },
        });
        Update();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (color, mode) : null;
    }

    public static async Task<GradientOverlaySettings?> Gradient(XamlRoot root, RasterImage layer, PaletteColor start, PaletteColor end)
    {
        var (small, _) = Small(layer);
        var settings = new GradientOverlaySettings(start, end);
        var preview = new Image { Stretch = Stretch.Uniform };
        void Update() => preview.Source = Checkered(LayerEffects.GradientOverlay(small, settings));
        var binder = new Binder();
        var angle = Slider(0, 360, settings.Angle);
        var angleText = Ui.Label($"{settings.Angle:0}°", 12);
        angle.ValueChanged += (_, e) => { settings = settings with { Angle = e.NewValue }; angleText.Text = $"{e.NewValue:0}°"; Update(); };
        var style = Ui.Segmented(new[] { (false, "Linear"), (true, "Radial") }, () => settings.Radial, v => { settings = settings with { Radial = v }; binder.Update(); Update(); }, binder);
        var reverse = Ui.Check("Reverse", () => settings.Reverse, v => { settings = settings with { Reverse = v }; Update(); }, binder);
        var dialog = Dialog(root, "Gradient Overlay", "Apply");
        dialog.Content = Ui.Row(18, Frame(preview), new StackPanel
        {
            Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Width = 300,
            Children =
            {
                Labeled("From", ColorButton(() => settings.Start, c => { settings = settings with { Start = c }; Update(); }, "Start color")),
                Labeled("To", ColorButton(() => settings.End, c => { settings = settings with { End = c }; Update(); }, "End color")),
                Labeled("Style", style), Labeled("Angle", Ui.Row(8, angle, angleText)), reverse,
                new TextBlock { Text = "The gradient fills the layer's visible pixels; transparency is kept.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Secondary },
            },
        });
        Update();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? settings : null;
    }

    public static async Task<ShadowSettings?> Shadow(XamlRoot root, RasterImage layer, double layerScale)
    {
        var (small, scale) = Small(layer);
        var settings = new ShadowSettings(PaletteColor.Black);
        var preview = new Image { Stretch = Stretch.Uniform };
        void Update()
        {
            // Offsets and blur are in canvas pixels; the preview works at the small copy's scale.
            double k = scale / Math.Max(0.0001, layerScale);
            var previewSettings = settings with { OffsetX = settings.OffsetX * k, OffsetY = settings.OffsetY * k, Blur = settings.Blur * k };
            int pad = (int)Math.Ceiling(Math.Max(Math.Abs(previewSettings.OffsetX), Math.Abs(previewSettings.OffsetY)) + previewSettings.Blur * 1.5) + 4;
            var shadow = LayerEffects.Shadow(small, previewSettings, pad);
            preview.Source = Checkered(small, canvas =>
            {
                using var multiply = new SKPaint { BlendMode = SKBlendMode.Multiply };
                canvas.DrawImage(shadow.Image, (float)previewSettings.OffsetX, (float)previewSettings.OffsetY, multiply);
            }, pad);
        }
        var opacity = Slider(0, 100, settings.Opacity * 100);
        var x = Slider(-200, 200, settings.OffsetX);
        var y = Slider(-200, 200, settings.OffsetY);
        var blur = Slider(0, 100, settings.Blur);
        var texts = new Dictionary<Slider, TextBlock>();
        FrameworkElement Row(string title, Slider slider, string unit, Action<double> set)
        {
            var text = Ui.Label($"{slider.Value:0}{unit}", 12);
            slider.ValueChanged += (_, e) => { set(e.NewValue); text.Text = $"{e.NewValue:0}{unit}"; Update(); };
            return Labeled(title, Ui.Row(8, slider, text));
        }
        var dialog = Dialog(root, "Add Shadow", "Add");
        dialog.Content = Ui.Row(18, Frame(preview), new StackPanel
        {
            Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Width = 320,
            Children =
            {
                Labeled("Color", ColorButton(() => settings.Color, c => { settings = settings with { Color = c }; Update(); }, "Shadow color")),
                Row("Opacity", opacity, "%", v => settings = settings with { Opacity = v / 100 }),
                Row("Offset X", x, " px", v => settings = settings with { OffsetX = v }),
                Row("Offset Y", y, " px", v => settings = settings with { OffsetY = v }),
                Row("Blur", blur, " px", v => settings = settings with { Blur = v }),
                new TextBlock { Text = "The shadow is added as its own layer under this one, so you can move or delete it later.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Secondary },
            },
        });
        Update();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? settings : null;
    }
}
