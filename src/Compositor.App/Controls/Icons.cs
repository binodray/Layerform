using Compositor.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Compositor.App.Controls;

/// <summary>
/// Line icons on an 18-unit grid in the weight of the SF Symbols the Mac app uses. The Mac's own custom drawings
/// (polygonal lasso, clone stamp, dithered gradient) are reproduced from their Swift definitions.
/// </summary>
public static class Icons
{
    public const string FluentFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static Microsoft.UI.Xaml.Media.Geometry Parse(string data) =>
        (Microsoft.UI.Xaml.Media.Geometry)XamlReader.Load($"<Geometry xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>{data}</Geometry>");

    public static FrameworkElement Stroke(string data, double size = 18, double thickness = 1.4, DoubleCollection? dash = null)
    {
        var path = new Path
        {
            Data = Parse(data),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
        };
        if (dash != null) path.StrokeDashArray = dash;
        return Wrap(path, size);
    }

    public static FrameworkElement Fill(string data, double size = 18)
    {
        var path = new Path { Data = Parse(data), Stretch = Stretch.None };
        return Wrap(path, size, fill: true);
    }

    private static FrameworkElement Wrap(Path path, double size, bool fill = false)
    {
        var box = new Viewbox { Width = size, Height = size, Child = new Grid { Width = 18, Height = 18, Children = { path } } };
        // Follow the hosting control's foreground.
        box.Loaded += (_, _) =>
        {
            var brush = FindForeground(box) ?? new SolidColorBrush(Microsoft.UI.Colors.White);
            if (fill) path.Fill = brush; else path.Stroke = brush;
        };
        return box;
    }

    private static Brush? FindForeground(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContentPresenter presenter && presenter.Foreground != null) return presenter.Foreground;
            if (current is Control control && control.Foreground != null) return control.Foreground;
        }
        return null;
    }

    public static FontIcon Glyph(string glyph, double size = 14) => new() { Glyph = glyph, FontFamily = new FontFamily(FluentFont), FontSize = size };

    public const string Add = "", Close = "", Trash = "", Folder = "", Eye = "", EyeOff = "",
        ChevronDown = "", ChevronRight = "", Link = "", ZoomIn = "", ZoomOut = "", Reset = "";

    /// <summary>A Fluent UI System icon (filled outline path) in the hosting control's foreground.</summary>
    public static FrameworkElement Fluent(string data, double size = 18, double viewBox = 24)
    {
        var path = new Path { Data = Parse(Geometry(data)), Stretch = Stretch.None };
        var box = new Viewbox { Width = size, Height = size, Child = new Grid { Width = viewBox, Height = viewBox, Children = { path } } };
        box.Loaded += (_, _) => path.Fill = FindForeground(box) ?? new SolidColorBrush(Microsoft.UI.Colors.White);
        return box;
    }

    /// <summary>XAML path markup with its fill rule: "F0 …" data (Lucide outlines) fills even-odd, Fluent icons non-zero.</summary>
    private static string Geometry(string data) => data.StartsWith("F0 ") ? "F0 " + NormalizePath(data[3..]) : "F1 " + NormalizePath(data);

    /// <summary>SVG path data allows "0-.41.34" and ".75.75"; the XAML parser wants each number separated.</summary>
    private static string NormalizePath(string data)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(data, @"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
        return string.Join(" ", tokens.Select(t => t.Value));
    }

    private static readonly Dictionary<string, string> lucideCache = new();

    /// <summary>A Lucide icon's stroked shapes as one filled outline (24-unit grid), so it draws like the Fluent icons and
    /// can also be a menu's <see cref="PathIcon"/>. The stroke is a little lighter than Lucide's 2, to sit with Windows'.</summary>
    public static string LucideData(string markup, double stroke = 1.65)
    {
        if (lucideCache.TryGetValue(markup, out var cached)) return cached;
        using var shapes = new SkiaSharp.SKPath();
        foreach (System.Text.RegularExpressions.Match element in System.Text.RegularExpressions.Regex.Matches(markup, @"<(\w+)([^>]*)>"))
        {
            var a = System.Text.RegularExpressions.Regex.Matches(element.Groups[2].Value, @"([\w-]+)=""([^""]*)""")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            float F(string key, float fallback = 0) =>
                a.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
            switch (element.Groups[1].Value)
            {
                case "path":
                    if (a.TryGetValue("d", out var d) && SkiaSharp.SKPath.ParseSvgPathData(d) is { } parsed) { shapes.AddPath(parsed); parsed.Dispose(); }
                    break;
                case "rect":
                    float rx = F("rx", F("ry")), ry = F("ry", rx);
                    var rect = SkiaSharp.SKRect.Create(F("x"), F("y"), F("width"), F("height"));
                    if (rx > 0) shapes.AddRoundRect(rect, rx, ry); else shapes.AddRect(rect);
                    break;
                case "circle":
                    shapes.AddCircle(F("cx"), F("cy"), F("r"));
                    break;
                case "ellipse":
                    shapes.AddOval(SkiaSharp.SKRect.Create(F("cx") - F("rx"), F("cy") - F("ry"), 2 * F("rx"), 2 * F("ry")));
                    break;
                case "line":
                    shapes.MoveTo(F("x1"), F("y1"));
                    shapes.LineTo(F("x2"), F("y2"));
                    break;
                case "polyline":
                case "polygon":
                    var numbers = (a.GetValueOrDefault("points") ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    for (int i = 0; i + 1 < numbers.Length; i += 2)
                        if (i == 0) shapes.MoveTo(numbers[i], numbers[i + 1]); else shapes.LineTo(numbers[i], numbers[i + 1]);
                    if (element.Groups[1].Value == "polygon") shapes.Close();
                    break;
            }
        }
        using var pen = new SkiaSharp.SKPaint
        {
            Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = (float)stroke, StrokeCap = SkiaSharp.SKStrokeCap.Round,
            StrokeJoin = SkiaSharp.SKStrokeJoin.Round, IsAntialias = true,
        };
        using var outline = new SkiaSharp.SKPath();
        pen.GetFillPath(shapes, outline);
        // One union, so overlapping strokes don't cancel each other out.
        using var merged = outline.Simplify() ?? new SkiaSharp.SKPath(outline);
        // The union has no overlapping contours, so even-odd filling keeps enclosed areas hollow (a lasso loop, a ring).
        var data = "F0 " + merged.ToSvgPathData();
        lucideCache[markup] = data;
        return data;
    }

    public static FrameworkElement Lucide(string markup, double size = 18) => Fluent(LucideData(markup), size);

    public static string ShapeData(Model.ShapeKind kind) => LucideData(kind switch
    {
        Model.ShapeKind.Ellipse => LucideIcons.Circle,
        Model.ShapeKind.Line => LucideIcons.Slash,
        Model.ShapeKind.Triangle => LucideIcons.Triangle,
        Model.ShapeKind.Polygon => LucideIcons.Pentagon,
        Model.ShapeKind.Star => LucideIcons.Star,
        _ => LucideIcons.Square,
    });

    public static FrameworkElement ShapeIcon(Model.ShapeKind kind, double size = 18) => Fluent(ShapeData(kind), size);

    /// <summary>A Fluent icon for a menu item, scaled from its 24-unit grid to the menu's 16.</summary>
    public static PathIcon MenuIcon(string data, double viewBox = 24)
    {
        var geometry = Parse(Geometry(data));
        geometry.Transform = new ScaleTransform { ScaleX = 16 / viewBox, ScaleY = 16 / viewBox };
        return new PathIcon { Data = geometry };
    }

    /// <summary>The tool rail's icons, and the icon for each tool variant (the rail's right-click groups use the same).</summary>
    public static string ToolData(NavigationTool tool, EditorSession session) => LucideData(tool switch
    {
        NavigationTool.Move => LucideIcons.MousePointer2,
        NavigationTool.Marquee => session.MarqueeKind == LassoKind.Ellipse ? LucideIcons.CircleDashed : LucideIcons.SquareDashed,
        NavigationTool.Lasso => session.LassoKind == LassoKind.Polygonal ? LucideIcons.LassoSelect : LucideIcons.Lasso,
        NavigationTool.Wand => LucideIcons.WandSparkles,
        NavigationTool.Crop => LucideIcons.Crop,
        NavigationTool.Brush => session.BrushMode == BrushToolMode.Erase ? LucideIcons.Eraser : LucideIcons.Paintbrush,
        NavigationTool.SpotHealing => LucideIcons.Bandage,
        NavigationTool.CloneStamp => LucideIcons.Stamp,
        NavigationTool.Blur => session.BlurMode switch { BlurToolMode.Smudge => LucideIcons.Pointer, BlurToolMode.Liquify => LucideIcons.Waves, _ => LucideIcons.Droplet },
        NavigationTool.Shape => session.ShapeKind switch
        {
            Model.ShapeKind.Ellipse => LucideIcons.Circle, Model.ShapeKind.Line => LucideIcons.Slash, Model.ShapeKind.Triangle => LucideIcons.Triangle,
            Model.ShapeKind.Polygon => LucideIcons.Pentagon, Model.ShapeKind.Star => LucideIcons.Star, _ => LucideIcons.Square,
        },
        NavigationTool.Gradient => LucideIcons.Blend,
        NavigationTool.Eyedropper => LucideIcons.Pipette,
        NavigationTool.Hand => LucideIcons.Hand,
        _ => LucideIcons.ZoomIn,
    });

    public static FrameworkElement Tool(NavigationTool tool, EditorSession session) =>
        Fluent(ToolData(tool, session));



}
