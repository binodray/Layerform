using Compositor.Geometry;
using Compositor.Format;
using Compositor.Imaging;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    public LayerTextStyle TextDefaults { get; set; } = new();
    public bool CanEditText => CanEditLayers && !SelectedLayersLocked && SelectedLayerIds.Count <= 1;

    public static string TextLayerName(string content)
    {
        var flattened = string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length == 0 ? "Text" : flattened[..Math.Min(40, flattened.Length)];
    }

    public static RasterImage RenderText(LayerTextStyle style)
    {
        if (!style.IsValid) throw ProjectException.Invalid();
        using var typeface = SKFontManager.Default.MatchFamily(style.FontName) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)style.FontSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor((byte)Math.Round(style.Red * 255), (byte)Math.Round(style.Green * 255), (byte)Math.Round(style.Blue * 255), 255) };
        const float padding = 12;
        float maxWidth = style.BoxSize is { } fixedBox ? (float)Math.Max(1, fixedBox.Width - padding * 2) : 100_000;
        var lines = Wrap(style.Content.Replace("\r\n", "\n").Replace('\r', '\n'), font, (float)style.Tracking, maxWidth);
        if (lines.Count == 0) lines.Add("");
        float measured = lines.Max(line => Measure(line, font, (float)style.Tracking));
        int width = style.BoxSize is { } box ? (int)Math.Ceiling(box.Width) : (int)Math.Ceiling(Math.Max(16, measured + padding * 2 + style.FontSize * 0.1));
        int height = style.BoxSize is { } box2 ? (int)Math.Ceiling(box2.Height) : (int)Math.Ceiling(Math.Max(16, lines.Count * style.LineHeight + padding * 2));
        if (width is < 1 or > 30_000 || height is < 1 or > 30_000 || (long)width * height > 100_000_000) throw ProjectException.TooLarge();
        var bitmap = PixelOps.NewRgba(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var metrics = font.Metrics;
        float baseline = padding - metrics.Ascent;
        for (int i = 0; i < lines.Count; i++)
        {
            float lineWidth = Measure(lines[i], font, (float)style.Tracking);
            float x = style.Alignment switch
            {
                LayerTextAlignment.Center => padding + Math.Max(0, (maxWidth - lineWidth) / 2),
                LayerTextAlignment.Right => padding + Math.Max(0, maxWidth - lineWidth),
                _ => padding,
            };
            foreach (var rune in lines[i].EnumerateRunes())
            {
                string glyph = rune.ToString();
                canvas.DrawText(glyph, x, baseline + (float)(i * style.LineHeight), SKTextAlign.Left, font, paint);
                x += font.MeasureText(glyph, paint) + (float)style.Tracking;
            }
        }
        return RasterImage.Adopt(bitmap);
    }

    private static float Measure(string text, SKFont font, float tracking)
    {
        float width = 0;
        int count = 0;
        foreach (var rune in text.EnumerateRunes()) { width += font.MeasureText(rune.ToString()); count++; }
        return width + Math.Max(0, count - 1) * tracking;
    }

    private static List<string> Wrap(string content, SKFont font, float tracking, float maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in content.Split('\n'))
        {
            if (maxWidth >= 99_999) { result.Add(paragraph); continue; }
            var words = paragraph.Split(' ');
            string line = "";
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, font, tracking) > maxWidth) { result.Add(line); line = word; }
                else line = candidate;
            }
            result.Add(line);
        }
        return result;
    }

    public bool AddTextLayer(LayerTextStyle style, PointD origin)
    {
        if (!CanEditText || document == null || !origin.IsFinite || !style.IsValid) return false;
        try
        {
            var image = RenderText(style);
            var asset = PixelOps.Asset(image, TextLayerName(style.Content));
            var layer = ImageLayer.FromAsset(asset, origin) with { Text = new LayerText(style, image), ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId };
            BeginEdit("New Text Layer");
            SetLayers(document.Layers.Add(layer));
            ActiveLayerId = layer.Id;
            TextDefaults = style;
            EndEdit();
            return true;
        }
        catch (Exception e) { BrushError = e.Message; Notify(); return false; }
    }

    public bool EditTextLayer(Guid id, LayerTextStyle style)
    {
        if (!CanEditLayers || document?.IndexOf(id) is not (>= 0 and var index) || document.Layers[index] is not { IsLocked: false, LiveText: { } old } layer || !style.IsValid) return false;
        try
        {
            var image = RenderText(style);
            var asset = PixelOps.Asset(image, TextLayerName(style.Content));
            var anchor = layer.Transform.Origin;
            var transform = layer.Transform with { Origin = anchor, Size = new SizeD(image.Width, image.Height) };
            BeginEdit("Edit Text");
            ReplaceLayer(index, layer with { Asset = asset, Name = TextLayerName(style.Content), Text = new LayerText(style, image), Transform = transform });
            TextDefaults = style;
            EndEdit();
            return true;
        }
        catch (Exception e) { BrushError = e.Message; Notify(); return false; }
    }

    public bool RecolorText(PaletteColor color)
    {
        if (ActiveLayer is not { LiveText: { } text } layer) return false;
        return EditTextLayer(layer.Id, text.Style with { Red = color.Red, Green = color.Green, Blue = color.Blue });
    }
}
