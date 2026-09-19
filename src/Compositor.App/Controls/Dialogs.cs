using Compositor.Editing;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Compositor.App.Controls;

public static class Dialogs
{
    private static string Bytes(long width, long height)
    {
        double bytes = width * height * 4.0;
        return bytes >= 1 << 30 ? $"{bytes / (1 << 30):0.#} GB" : bytes >= 1 << 20 ? $"{bytes / (1 << 20):0.#} MB" : $"{bytes / 1024:0} KB";
    }

    /// <summary>Canvas Size (CanvasSizeSheet.swift).</summary>
    public static async Task<CanvasSizeOptions?> CanvasSize(XamlRoot root, CanvasDocument document, PaletteColor foreground, PaletteColor background)
    {
        var draft = new CanvasSizeDraft(document.Width, document.Height, document.Resolution);
        var binder = new Binder();
        int anchor = 4;
        string extension = "Transparent";
        var custom = PaletteColor.White;
        string[] anchorNames = { "Top left", "Top center", "Top right", "Middle left", "Center", "Middle right", "Bottom left", "Bottom center", "Bottom right" };
        var dialog = new ContentDialog { XamlRoot = root, Title = "Canvas Size", PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        var summary = Ui.Label("", 12, brush: Ui.Secondary);
        void Sync() { binder.Update(); }
        binder.Add(() =>
        {
            summary.Text = draft.Valid
                ? $"New: {(int)Math.Round(draft.Width)} × {(int)Math.Round(draft.Height)} pixels · {Bytes((long)Math.Round(draft.Width), (long)Math.Round(draft.Height))} uncompressed"
                : "Final dimensions must be 1–30,000 pixels per side.";
            summary.Foreground = draft.Valid ? Ui.Secondary : new SolidColorBrush(Colors.Orange);
            dialog.IsPrimaryButtonEnabled = draft.Valid;
        });
        var units = Ui.Picker(Enum.GetValues<CanvasUnit>().Select(u => (u, u.ToString())).ToList(), () => draft.Unit, u => { draft.Unit = u; Sync(); }, binder, 200);
        FrameworkElement Dimension(string title, bool widthAxis) => Ui.Row(8, new TextBlock { Text = title, Width = 60, VerticalAlignment = VerticalAlignment.Center },
            Ui.Number(() => draft.Displayed(widthAxis), v => { draft.Set(v, widthAxis); Sync(); }, binder, 120, 3));
        var relative = Ui.Check("Relative to current dimensions", () => draft.Relative, v => { draft.Relative = v; Sync(); }, binder);
        var locked = Ui.Check("Lock original aspect ratio", () => draft.Locked, v => { draft.Locked = v; if (v) draft.Set(draft.Displayed(true), true); Sync(); }, binder);
        var anchorGrid = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        for (int i = 0; i < 3; i++) { anchorGrid.RowDefinitions.Add(new RowDefinition()); anchorGrid.ColumnDefinitions.Add(new ColumnDefinition()); }
        var anchorLabel = Ui.Label(anchorNames[anchor], 12, true);
        for (int index = 0; index < 9; index++)
        {
            int captured = index;
            var button = new Button { Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(15) };
            ToolTipService.SetToolTip(button, anchorNames[index]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, anchorNames[index]);
            binder.Add(() => button.Content = new Ellipse(captured == anchor));
            button.Click += (_, _) => { anchor = captured; anchorLabel.Text = anchorNames[anchor]; binder.Update(); };
            Grid.SetRow(button, index / 3);
            Grid.SetColumn(button, index % 3);
            anchorGrid.Children.Add(button);
        }
        var extensionChoices = new[] { "Transparent", "Foreground", "Background", "Black", "White", "Custom" };
        var customHex = new TextBox { Width = 90, Text = custom.Hex, Visibility = Visibility.Collapsed, PlaceholderText = "RRGGBB" };
        var extensionPicker = Ui.Picker(extensionChoices.Select(c => (c, c)).ToList(), () => extension, v => { extension = v; customHex.Visibility = v == "Custom" ? Visibility.Visible : Visibility.Collapsed; }, binder, 180);
        dialog.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14, Width = 400,
                Children =
                {
                    Ui.Label($"Current: {document.Width} × {document.Height} pixels"),
                    Ui.Label($"{Bytes(document.Width, document.Height)} uncompressed RGBA canvas", 12, brush: Ui.Secondary),
                    Ui.Row(8, Ui.Label("Units"), units), Dimension("Width", true), Dimension("Height", false), relative, locked, summary,
                    Ui.Row(24, new StackPanel { Spacing = 8, Children = { Ui.Label("Anchor"), anchorGrid } },
                        new StackPanel { Spacing = 6, Margin = new Thickness(0, 28, 0, 0), Width = 220, Children = { anchorLabel,
                            new TextBlock { Text = "Keeps this point fixed. Artwork is not scaled; cropped content remains outside the canvas.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Secondary } } }),
                    Ui.Row(8, Ui.Label("Canvas extension"), extensionPicker, customHex),
                },
            },
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !draft.Valid) return null;
        PaletteColor? fill = extension switch
        {
            "Transparent" => null, "Black" => PaletteColor.Black, "White" => PaletteColor.White,
            "Foreground" => foreground, "Background" => background, _ => PaletteColor.FromHex(customHex.Text) ?? PaletteColor.White,
        };
        return new CanvasSizeOptions((int)Math.Round(draft.Width), (int)Math.Round(draft.Height), anchor, fill);
    }

    private sealed class Ellipse : UserControl
    {
        public Ellipse(bool filled)
        {
            Content = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 12, Height = 12, StrokeThickness = 1.5,
                Stroke = filled ? new SolidColorBrush(Colors.DodgerBlue) : Ui.Secondary,
                Fill = filled ? new SolidColorBrush(Colors.DodgerBlue) : null,
            };
        }
    }

    /// <summary>Image Size (ImageSizeSheet.swift).</summary>
    public static async Task<ImageSizeOptions?> ImageSize(XamlRoot root, CanvasDocument document)
    {
        double width = document.Width, height = document.Height, resolution = document.Resolution;
        bool locked = true, resample = true;
        string unit = "Pixels";
        var sampling = LayerSampling.High;
        var binder = new Binder();
        var dialog = new ContentDialog { XamlRoot = root, Title = "Image Size", PrimaryButtonText = "Resize", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        bool Valid() => double.IsFinite(width) && double.IsFinite(height) && double.IsFinite(resolution) && resolution >= 1 && resolution <= 9600
            && Math.Round(width) >= 1 && Math.Round(width) <= 30_000 && Math.Round(height) >= 1 && Math.Round(height) <= 30_000
            && (!resample || Math.Round(width) * Math.Round(height) <= 100_000_000);
        double Display(double pixels, int original) => unit switch
        {
            "Percent" => pixels / original * 100, "Inches" => pixels / resolution, "Centimeters" => pixels / resolution * 2.54, _ => pixels,
        };
        void SetDimension(double value, bool isWidth)
        {
            if (!double.IsFinite(value) || value <= 0) return;
            if (!resample)
            {
                resolution = (isWidth ? width : height) / value * (unit == "Centimeters" ? 2.54 : 1);
                return;
            }
            double pixels = unit switch
            {
                "Percent" => value / 100 * (isWidth ? document.Width : document.Height),
                "Inches" => value * resolution, "Centimeters" => value / 2.54 * resolution, _ => value,
            };
            if (isWidth) { if (locked) height = pixels * height / width; width = pixels; }
            else { if (locked) width = pixels * width / height; height = pixels; }
        }
        var result = Ui.Label("", 12);
        binder.Add(() =>
        {
            bool valid = Valid();
            result.Text = valid ? $"Result: {(int)Math.Round(width)} × {(int)Math.Round(height)} pixels" : "Use 1–30,000 pixels per side, up to 100 megapixels, and 1–9,600 pixels/inch.";
            result.Foreground = valid ? Ui.Secondary : new SolidColorBrush(Colors.Orange);
            dialog.IsPrimaryButtonEnabled = valid;
        });
        var unitOptions = new[] { "Pixels", "Percent", "Inches", "Centimeters" };
        var units = Ui.Picker(unitOptions.Select(u => (u, u)).ToList(), () => unit, u => { if (!resample && (u == "Pixels" || u == "Percent")) return; unit = u; binder.Update(); }, binder, 200);
        FrameworkElement Dimension(string title, bool isWidth) => Ui.Row(8, new TextBlock { Text = title, Width = 75, VerticalAlignment = VerticalAlignment.Center },
            Ui.Number(() => Display(isWidth ? width : height, isWidth ? document.Width : document.Height), v => { SetDimension(v, isWidth); binder.Update(); }, binder, 120, 3));
        var lockBox = Ui.Check("Lock aspect ratio", () => locked, v => { locked = v; binder.Update(); }, binder);
        binder.Add(() => lockBox.IsEnabled = resample);
        var resolutionField = Ui.Number(() => resolution, v =>
        {
            double old = resolution;
            if (!(v > 0) || !double.IsFinite(v)) return;
            resolution = v;
            if (resample && (unit == "Inches" || unit == "Centimeters") && old > 0) { width *= v / old; height *= v / old; }
            binder.Update();
        }, binder, 90, 3);
        var samplingPicker = Ui.Picker(Enum.GetValues<LayerSampling>().Select(s => (s, s.ToName())).ToList(), () => sampling, s => sampling = s, binder, 180);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Secondary };
        var resampleBox = Ui.Check("Resample", () => resample, v =>
        {
            resample = v;
            if (!v)
            {
                width = document.Width; height = document.Height; locked = true;
                if (unit is "Pixels" or "Percent") unit = "Inches";
            }
            binder.Update();
        }, binder);
        binder.Add(() =>
        {
            samplingPicker.Visibility = resample ? Visibility.Visible : Visibility.Collapsed;
            note.Text = resample ? "Resizes layer pixels and applies existing transforms. Undo restores the originals." : "Only print dimensions and resolution change. Pixels stay unchanged.";
        });
        dialog.Content = new StackPanel
        {
            Spacing = 14, Width = 380,
            Children =
            {
                Ui.Label($"Current: {document.Width} × {document.Height} pixels", 12, brush: Ui.Secondary), Ui.Row(8, Ui.Label("Units"), units),
                Dimension("Width", true), Dimension("Height", false), lockBox,
                Ui.Row(8, Ui.Label("Resolution"), resolutionField, Ui.Label("pixels/inch", 12, brush: Ui.Secondary)), resampleBox,
                Ui.Row(8, Ui.Label("Sampling"), samplingPicker), note, result,
            },
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !Valid()) return null;
        return new ImageSizeOptions((int)Math.Round(width), (int)Math.Round(height), resolution, sampling);
    }

    private static double lastJpegQuality = 0.85;

    /// <summary>Export JPEG with a live encoded preview (JPEGExportSheet.swift). Returns the encoded bytes.</summary>
    public static async Task<byte[]?> JpegExport(XamlRoot root, ExportRaster raster)
    {
        double quality = lastJpegQuality;
        var matte = PaletteColor.White;
        byte[]? encoded = null;
        var dialog = new ContentDialog { XamlRoot = root, Title = "Export JPEG", PrimaryButtonText = "Export…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        var image = new Image { Stretch = Stretch.Uniform };
        var progress = new ProgressRing { IsActive = true, Width = 28, Height = 28 };
        var previewBox = new Grid { Width = 560, Height = 330, Background = Ui.Solid(255, 31, 31, 31), Children = { image, progress } };
        var sizeText = Ui.Label("Updating preview…", 12, brush: Ui.Secondary);
        var qualityText = Ui.Label($"{(int)Math.Round(quality * 100)}%");
        var slider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.01, Value = quality, Width = 380, VerticalAlignment = VerticalAlignment.Center };
        var matteHex = new TextBox { Text = matte.Hex, Width = 90 };
        CancellationTokenSource? cancel = null;
        async Task Encode()
        {
            cancel?.Cancel();
            var token = (cancel = new CancellationTokenSource()).Token;
            progress.IsActive = true;
            progress.Visibility = Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = false;
            sizeText.Text = "Updating preview…";
            try
            {
                await Task.Delay(200, token);
                double q = quality;
                var m = matte;
                var data = await Task.Run(() => ImageExporter.Jpeg(raster, q, m), token);
                token.ThrowIfCancellationRequested();
                encoded = data;
                var preview = await Task.Run(() =>
                {
                    var decoded = Codecs.DecodeRgba(data);
                    double factor = Math.Min(1, 1000.0 / Math.Max(decoded.Width, decoded.Height));
                    int w = Math.Max(1, (int)(decoded.Width * factor)), h = Math.Max(1, (int)(decoded.Height * factor));
                    var small = PixelOps.NewRgba(w, h);
                    using (var canvas = new SkiaSharp.SKCanvas(small))
                        canvas.DrawImage(decoded.Image, new SkiaSharp.SKRect(0, 0, w, h), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
                    return small;
                }, token);
                image.Source = Thumbnails.ToWriteable(preview);
                preview.Dispose();
                sizeText.Text = $"{data.Length / 1024.0:0} KB · encoded preview, fitted to window";
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { sizeText.Text = e.Message; }
            finally { if (!token.IsCancellationRequested) { progress.IsActive = false; progress.Visibility = Visibility.Collapsed; } }
        }
        slider.ValueChanged += (_, e) => { quality = Math.Round(e.NewValue, 2); qualityText.Text = $"{(int)Math.Round(quality * 100)}%"; _ = Encode(); };
        matteHex.LostFocus += (_, _) => { if (PaletteColor.FromHex(matteHex.Text) is { } c) { matte = c; _ = Encode(); } matteHex.Text = matte.Hex; };
        dialog.Content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                previewBox, Ui.Row(10, Ui.Label("Quality"), slider, qualityText),
                Ui.Row(8, Ui.Label("Background for transparency"), matteHex),
                Ui.Label($"{raster.Image.Width} × {raster.Image.Height} px · sRGB", 12, brush: Ui.Secondary), sizeText,
            },
        };
        _ = Encode();
        var result = await dialog.ShowAsync();
        cancel?.Cancel();
        if (result != ContentDialogResult.Primary || encoded == null) return null;
        lastJpegQuality = quality;
        return encoded;
    }

    public static async Task<ContentDialogResult> SaveChanges(XamlRoot root, string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = $"Save changes to {name}?", Content = "Your changes will be lost if you don’t save them.",
            PrimaryButtonText = "Save", SecondaryButtonText = "Don’t Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync();
    }

    public static async Task<bool?> ConfirmReplace(XamlRoot root, string fileName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = $"Replace {fileName}?", Content = "A file with this name already exists. Replacing it overwrites it.",
            PrimaryButtonText = "Replace", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Error(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = root, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK" };
        await dialog.ShowAsync();
    }

    public static async Task<bool?> ChooseMaskColor(XamlRoot root, bool background)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = background ? "Mask background" : "Mask foreground",
            PrimaryButtonText = "Black · Hide", SecondaryButtonText = "White · Reveal", CloseButtonText = "Cancel",
        };
        var result = await dialog.ShowAsync();
        return result switch { ContentDialogResult.Primary => false, ContentDialogResult.Secondary => true, _ => null };
    }
}

/// <summary>A New-canvas preset, as Photoshop's New Document offers them.</summary>
public sealed record CanvasTemplate(string Name, int Width, int Height, double Resolution = 72)
{
    public static readonly CanvasTemplate[] All =
    {
        new("HD", 1920, 1080),
        new("4K UHD", 3840, 2160),
        new("Square post", 1080, 1080),
        new("Portrait post", 1080, 1350),
        new("Story / Reel", 1080, 1920),
        new("YouTube thumbnail", 1280, 720),
        new("Social share", 1200, 630),
        new("Phone wallpaper", 1440, 2560),
        new("Desktop wallpaper", 2560, 1440),
        new("A4 · 300 ppi", 2480, 3508, 300),
        new("US Letter · 300 ppi", 2550, 3300, 300),
        new("Poster 18×24 · 150 ppi", 2700, 3600, 150),
    };
}

/// <summary>The welcome view shown on an empty canvas (NewCanvasSheet.swift), with Layer Form's templates.</summary>
public sealed class NewCanvasView : UserControl
{
    private readonly TextBox width = new() { Text = "1920", FontSize = 14, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent) };
    private readonly TextBox height = new() { Text = "1080", FontSize = 14, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent) };
    private readonly TextBlock message = Ui.Label("Transparent canvas · sRGB", 13, brush: Ui.Secondary);
    private readonly Button create;
    private readonly List<(CanvasTemplate Template, Button Card)> cards = new();
    private double resolution = 72;

    public NewCanvasView(Action<int, int, double> onCreate, Action onOpen, Action onOpenImage)
    {
        void Create()
        {
            if (CanvasDocument.ValidDimension(width.Text) is { } w && CanvasDocument.ValidDimension(height.Text) is { } h) onCreate(w, h, resolution);
        }
        create = Ui.Capsule("Create canvas", Create, accent: true);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(create, "createCanvas");
        width.TextChanged += (_, _) => Validate();
        height.TextChanged += (_, _) => Validate();
        foreach (var box in new[] { width, height })
            box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && create.IsEnabled) Create(); };
        FrameworkElement Dimension(string title, TextBox box)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, title);
            var field = new Grid { Padding = new Thickness(12), CornerRadius = new CornerRadius(7), Background = Ui.Solid(40, 255, 255, 255), Width = 150 };
            field.ColumnDefinitions.Add(new ColumnDefinition());
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            field.Children.Add(box);
            var px = Ui.Label("px", 13, brush: Ui.Secondary);
            Grid.SetColumn(px, 1);
            field.Children.Add(px);
            return new StackPanel { Spacing = 8, Children = { Ui.Label(title, 13, true), field } };
        }
        var swap = Ui.IconButton(Icons.Lucide(LucideIcons.ArrowLeftRight, 16), () => { (width.Text, height.Text) = (height.Text, width.Text); }, "Swap width and height", 32, 32);
        swap.VerticalAlignment = VerticalAlignment.Bottom;
        swap.Margin = new Thickness(0, 0, 0, 8);

        // Templates, three across; the chosen one is outlined in the accent colour.
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < (CanvasTemplate.All.Length + 2) / 3; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < CanvasTemplate.All.Length; i++)
        {
            var template = CanvasTemplate.All[i];
            double scale = 22.0 / Math.Max(template.Width, template.Height);
            var preview = new Border
            {
                Width = Math.Max(6, template.Width * scale), Height = Math.Max(6, template.Height * scale), CornerRadius = new CornerRadius(2),
                BorderBrush = Ui.Secondary, BorderThickness = new Thickness(1.2), VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new StackPanel
            {
                Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = template.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    Ui.Label($"{template.Width} × {template.Height}", 11, brush: Ui.Secondary),
                },
            };
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(preview);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var card = new Button
            {
                Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
                Background = Ui.Solid(22, 255, 255, 255),
            };
            ToolTipService.SetToolTip(card, $"{template.Name} · {template.Width} × {template.Height} px · {template.Resolution:0} ppi · double-click to create");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, template.Name);
            card.Click += (_, _) =>
            {
                width.Text = template.Width.ToString();
                height.Text = template.Height.ToString();
                resolution = template.Resolution;
                Validate();
            };
            card.DoubleTapped += (_, _) => Create();
            Grid.SetRow(card, i / 3);
            Grid.SetColumn(card, i % 3);
            grid.Children.Add(card);
            cards.Add((template, card));
        }

        var actions = new Grid();
        actions.Children.Add(Ui.Row(10, Ui.Capsule("Open project", onOpen), Ui.Capsule("Open image", onOpenImage)));
        create.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Children.Add(create);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Background = Ui.Solid(255, 0x2A, 0x2A, 0x2C), CornerRadius = new CornerRadius(12), MaxWidth = 640, Padding = new Thickness(28),
                Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 22,
                    Children =
                    {
                        new StackPanel { Spacing = 6, Children = { Ui.Label("New canvas", 22, true), Ui.Label("A blank space for your next composition.", 13, brush: Ui.Secondary) } },
                        new StackPanel { Spacing = 10, Children = { Ui.Label("Templates", 13, true), grid } },
                        Ui.Row(12, Dimension("Width", width), swap, Dimension("Height", height)),
                        message, actions,
                    },
                },
            },
        };
        card = (Border)((ScrollViewer)Content).Content;
        Validate();
    }

    private readonly Border card;

    /// <summary>Whether a point (in this view) is on the New canvas card rather than the empty workspace around it.</summary>
    public bool IsOverCard(Windows.Foundation.Point point)
    {
        var bounds = card.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, card.ActualWidth, card.ActualHeight));
        return bounds.Contains(point);
    }

    public void Suggest(int w, int h)
    {
        width.Text = w.ToString();
        height.Text = h.ToString();
    }

    public void FocusWidth() => width.Focus(FocusState.Programmatic);

    private void Validate()
    {
        var w = CanvasDocument.ValidDimension(width.Text);
        var h = CanvasDocument.ValidDimension(height.Text);
        bool valid = w != null && h != null;
        var match = cards.FirstOrDefault(c => c.Template.Width == w && c.Template.Height == h).Template;
        resolution = match?.Resolution ?? 72;
        foreach (var (template, card) in cards)
            card.BorderBrush = ReferenceEquals(template, match) ? new SolidColorBrush(Ui.Accent) : Ui.Solid(20, 255, 255, 255);
        message.Text = !valid ? "Enter whole numbers from 1 to 30,000 pixels."
            : $"Transparent canvas · sRGB · {resolution:0} ppi" + (match != null ? $" · {match.Name}" : "");
        message.Foreground = valid ? Ui.Secondary : new SolidColorBrush(Colors.Orange);
        create.IsEnabled = valid;
    }
}
