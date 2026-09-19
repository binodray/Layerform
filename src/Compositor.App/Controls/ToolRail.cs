using Compositor.Editing;
using Compositor.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App.Controls;

/// <summary>The 56-point tool rail with the colour palette beneath it (ContentView.toolRail, ColorPaletteControls).</summary>
public sealed class ToolRail : UserControl
{
    private readonly Func<EditorSession?> session;
    private readonly StackPanel tools = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<NavigationTool, Button> buttons = new();
    private (NavigationTool Tool, LassoKind Lasso, LassoKind Marquee, BrushToolMode Brush, BlurToolMode Blur, ShapeKind Shape)? iconState;
    private readonly Border foreground = Swatch(), background = Swatch();
    /// <summary>The Marquee and Lasso share one button, which shows (and picks) whichever was used last.</summary>
    private NavigationTool selectionTool = NavigationTool.Marquee;
    public event Action<bool>? OpenColorPicker;
    public event Action<bool>? ChooseMaskColor;

    public ToolRail(Func<EditorSession?> session)
    {
        this.session = session;
        Width = 56;
        foreach (var tool in NavigationTools.Rail.Where(t => t != NavigationTool.Lasso))
        {
            var button = new Button
            {
                Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent),
                Foreground = Ui.Brush("TextFillColorPrimaryBrush"),
            };
            ToolTipService.SetToolTip(button, tool == NavigationTool.Marquee ? "Selection tools: Marquee (M) · Lasso (L) · right-click for all" : tool.Label());
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tool.Label());
            var captured = tool;
            button.Click += (_, _) => session()?.SelectTool(captured == NavigationTool.Marquee ? selectionTool : captured);
            if (GroupFlyout(tool) is { } flyout) button.ContextFlyout = flyout;
            buttons[tool] = button;
            tools.Children.Add(button);
        }
        tools.Children.Add(BuildPalette());
        Content = new ScrollViewer
        {
            Content = tools, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 16, 0, 12),
        };
    }

    /// <summary>Photoshop's tool groups: right-click (or press and hold) a tool to pick one of its variants.</summary>
    private MenuFlyout? GroupFlyout(NavigationTool tool)
    {
        var items = new List<(string Title, string Key, string Icon, Action<EditorSession> Choose, Func<EditorSession, bool> Chosen, NavigationTool Target)>();
        void Add(string title, string key, string icon, Action<EditorSession> choose, Func<EditorSession, bool> chosen, NavigationTool? target = null) =>
            items.Add((title, key, icon, choose, chosen, target ?? tool));
        switch (tool)
        {
            case NavigationTool.Marquee:
                Add("Rectangular Marquee", "M", Icons.LucideData(LucideIcons.SquareDashed), s => s.MarqueeKind = LassoKind.Rectangle, s => s.MarqueeKind == LassoKind.Rectangle);
                Add("Elliptical Marquee", "Shift+M", Icons.LucideData(LucideIcons.CircleDashed), s => s.MarqueeKind = LassoKind.Ellipse, s => s.MarqueeKind == LassoKind.Ellipse);
                Add("Lasso", "L", Icons.LucideData(LucideIcons.Lasso), s => s.LassoKind = LassoKind.Freehand, s => s.LassoKind == LassoKind.Freehand, NavigationTool.Lasso);
                Add("Polygonal Lasso", "Shift+L", Icons.LucideData(LucideIcons.LassoSelect), s => s.LassoKind = LassoKind.Polygonal, s => s.LassoKind == LassoKind.Polygonal, NavigationTool.Lasso);
                break;
            case NavigationTool.Brush:
                Add("Brush", "B", Icons.LucideData(LucideIcons.Paintbrush), s => s.BrushMode = BrushToolMode.Paint, s => s.BrushMode == BrushToolMode.Paint);
                Add("Eraser", "E", Icons.LucideData(LucideIcons.Eraser), s => s.BrushMode = BrushToolMode.Erase, s => s.BrushMode == BrushToolMode.Erase);
                break;
            case NavigationTool.Blur:
                Add("Liquify", "R", Icons.LucideData(LucideIcons.Waves), s => s.BlurMode = BlurToolMode.Liquify, s => s.BlurMode == BlurToolMode.Liquify);
                Add("Blur", "R", Icons.LucideData(LucideIcons.Droplet), s => s.BlurMode = BlurToolMode.Blur, s => s.BlurMode == BlurToolMode.Blur);
                Add("Smudge", "R", Icons.LucideData(LucideIcons.Pointer), s => s.BlurMode = BlurToolMode.Smudge, s => s.BlurMode == BlurToolMode.Smudge);
                break;
            case NavigationTool.Shape:
                foreach (var kind in Enum.GetValues<ShapeKind>())
                {
                    var k = kind;
                    string title = kind switch { ShapeKind.Rectangle => "Rectangle", ShapeKind.Ellipse => "Ellipse", ShapeKind.Line => "Line", ShapeKind.Triangle => "Triangle", ShapeKind.Polygon => "Polygon", _ => "Star" };
                    Add(title + " Tool", "U", Icons.ShapeData(k), s => { s.CancelShape(); s.ShapeKind = k; }, s => s.ShapeKind == k);
                }
                break;
            default:
                return null;
        }
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var s = session();
            foreach (var item in items)
            {
                var entry = new ToggleMenuFlyoutItem
                {
                    Text = item.Title, KeyboardAcceleratorTextOverride = item.Key, IsChecked = s != null && s.Tool == item.Target && item.Chosen(s),
                    Icon = Icons.MenuIcon(item.Icon),
                };
                var choose = item.Choose;
                var target = item.Target;
                entry.Click += (_, _) =>
                {
                    if (session() is not { } current) return;
                    current.SelectTool(target);
                    choose(current);
                    current.Notify();
                };
                flyout.Items.Add(entry);
            }
        };
        return flyout;
    }

    /// <summary>The small corner triangle Photoshop puts on a tool that has variants.</summary>
    private static FrameworkElement WithGroupMark(FrameworkElement icon, bool grouped)
    {
        if (!grouped) return icon;
        var mark = new Microsoft.UI.Xaml.Shapes.Polygon
        {
            Points = { new Windows.Foundation.Point(4, 0), new Windows.Foundation.Point(4, 4), new Windows.Foundation.Point(0, 4) },
            Fill = Ui.Solid(150, 255, 255, 255), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -7, -7), IsHitTestVisible = false,
        };
        return new Grid { Children = { icon, mark } };
    }

    private static Border Swatch() => new()
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Colors.Black), BorderThickness = new Thickness(1),
        Child = new Border { CornerRadius = new CornerRadius(5), BorderBrush = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(1.5) },
    };

    private FrameworkElement BuildPalette()
    {
        var grid = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 8, 0, 0) };
        var backgroundButton = PlainButton(background, () => Pick(true), "Background color");
        backgroundButton.Margin = new Thickness(12, 12, 0, 0);
        var foregroundButton = PlainButton(foreground, () => Pick(false), "Foreground color");
        var swap = Ui.IconButton(Icons.Lucide(LucideIcons.ArrowLeftRight, 10), () => session()?.SwapPaletteColors(), "Swap foreground and background (X)", 12, 12);
        swap.Margin = new Thickness(27, -3, 0, 0);
        var reset = Ui.IconButton(Icons.Lucide(LucideIcons.RotateCcw, 9), () => session()?.ResetPaletteColors(), "Default colors (D)", 12, 12);
        reset.Margin = new Thickness(-1, 27, 0, 0);
        foreach (var e in new FrameworkElement[] { backgroundButton, foregroundButton, swap, reset })
        {
            e.HorizontalAlignment = HorizontalAlignment.Left;
            e.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(e);
        }
        return grid;
    }

    private void Pick(bool isBackground)
    {
        if (session() is not { } s) return;
        if (s.IsMaskSelected) ChooseMaskColor?.Invoke(isBackground);
        else OpenColorPicker?.Invoke(isBackground);
    }

    private static Button PlainButton(UIElement content, Action action, string tooltip)
    {
        var button = new Button
        {
            Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    public void Refresh()
    {
        if (session() is not { } s) return;
        if (s.Tool is NavigationTool.Marquee or NavigationTool.Lasso) selectionTool = s.Tool;
        var state = (s.Tool, s.LassoKind, s.MarqueeKind, s.BrushMode, s.BlurMode, s.ShapeKind);
        if (iconState != state)
        {
            iconState = state;
            foreach (var (tool, button) in buttons)
                button.Content = WithGroupMark(Icons.Tool(tool == NavigationTool.Marquee ? selectionTool : tool, s), button.ContextFlyout != null);
        }
        foreach (var (tool, button) in buttons)
        {
            bool selected = s.Tool == tool || (tool == NavigationTool.Marquee && s.Tool == NavigationTool.Lasso);
            button.Background = selected ? Ui.Solid(31, 255, 255, 255) : new SolidColorBrush(Colors.Transparent);
            button.BorderBrush = selected ? Ui.Solid(36, 255, 255, 255) : new SolidColorBrush(Colors.Transparent);
        }
        foreground.Background = new SolidColorBrush(Ui.Color(s.PaletteColorFor(false)));
        background.Background = new SolidColorBrush(Ui.Color(s.PaletteColorFor(true)));
        tools.Opacity = 1;
        IsEnabled = s.CanEditPalette || true;
    }
}
