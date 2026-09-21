using System.Globalization;
using Compositor.Editing;
using Compositor.Geometry;
using Compositor.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using SelectionMode = Compositor.Editing.SelectionMode;

namespace Compositor.App.Controls;

/// <summary>The 42-point bar above the canvas with the current tool's options (TransformInspector, BrushControls,
/// LassoControls, GradientControls, ShapeControls, CropControls, NavigationToolHeader).</summary>
public sealed class ToolOptions : UserControl
{
    private readonly Func<EditorSession?> session;
    private readonly Action zoomIn;
    private readonly Action zoomOut;
    private Binder binder = new();
    private object? signature;
    public Action? ReleaseFocus;
    public Action<bool>? OpenColorPicker;

    public ToolOptions(Func<EditorSession?> session, Action zoomIn, Action zoomOut)
    {
        this.session = session;
        this.zoomIn = zoomIn;
        this.zoomOut = zoomOut;
        Height = 42;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    public void Refresh()
    {
        if (session() is not { } s) { Content = null; signature = null; return; }
        object next = (s, s.Tool, s.BrushMode, s.BlurMode, s.IsMaskSelected, s.MarqueeKind, s.LassoKind, s.ShapeKind, s.GradientEdit != null,
            s.CloneSource != null, s.Selection != null, s.Document != null, s.TransformEditState != null, s.CropRect != null, s.ShapeCornerRadii != null);
        if (!Equals(next, signature))
        {
            signature = next;
            binder = new Binder();
            Content = Build(s);
        }
        else binder.Update();
        IsEnabled = !s.IsProjectBusy || s.Tool is NavigationTool.Hand or NavigationTool.Zoom;
    }

    private FrameworkElement Bar(params UIElement[] children)
    {
        var grid = new Grid { Padding = new Thickness(18, 0, 18, 0), ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var row = Ui.Row(12, children);
        var scroll = new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0,
        };
        grid.Children.Add(scroll);
        var zoom = ZoomControls();
        Grid.SetColumn(zoom, 1);
        grid.Children.Add(zoom);
        return grid;
    }

    private FrameworkElement ZoomControls()
    {
        var s = session();
        var fit = Ui.Capsule("Fit", () => s?.Fit());
        ToolTipService.SetToolTip(fit, "Fit canvas in window (Ctrl+0)");
        var actual = Ui.Capsule("100%", () => s?.Zoom(1));
        ToolTipService.SetToolTip(actual, "Actual pixels (Ctrl+1)");
        var plus = Ui.IconButton(Icons.Fluent(Icons.LucideData(LucideIcons.ZoomIn), 17), zoomIn, "Zoom in (Ctrl++)", 32, 30);
        var minus = Ui.IconButton(Icons.Fluent(Icons.LucideData(LucideIcons.ZoomOut), 17), zoomOut, "Zoom out (Ctrl+−)", 32, 30);
        bool enabled = s?.Document != null;
        fit.IsEnabled = actual.IsEnabled = plus.IsEnabled = minus.IsEnabled = enabled;
        var row = Ui.Row(6, new Border { Width = 1, Height = 24, Background = Ui.Divider, Margin = new Thickness(2, 0, 8, 0) }, fit, actual, plus, minus);
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    private void Release() => ReleaseFocus?.Invoke();

    private FrameworkElement Build(EditorSession s)
    {
        switch (s.Tool)
        {
            case NavigationTool.Move: return Transform(s);
            case NavigationTool.Brush or NavigationTool.SpotHealing or NavigationTool.CloneStamp or NavigationTool.Blur: return Brush(s);
            case NavigationTool.Marquee or NavigationTool.Lasso or NavigationTool.Wand: return Selection(s);
            case NavigationTool.Gradient: return Gradient(s);
            case NavigationTool.Type:
                return Bar(Ui.Title("Type"), Ui.Label("Click to type or select existing text. Edit it live in Window â€º Character.", 11, brush: Ui.Secondary));
            case NavigationTool.Shape: return Shape(s);
            case NavigationTool.Crop: return Crop(s);
            case NavigationTool.Slice:
            {
                var rows = Ui.Number(() => s.SliceRows, v => s.SliceRows = Math.Clamp((int)Math.Round(v), 1, 100), binder, 54, 0, 1, Release, "Slice rows");
                var columns = Ui.Number(() => s.SliceColumns, v => s.SliceColumns = Math.Clamp((int)Math.Round(v), 1, 100), binder, 54, 0, 1, Release, "Slice columns");
                return Bar(Ui.Title("Slice"), Ui.Label("Rows", 11, brush: Ui.Secondary), rows,
                    Ui.Label("Columns", 11, brush: Ui.Secondary), columns,
                    Ui.Capsule("Create Grid", () => s.CreateSliceGrid(s.SliceRows, s.SliceColumns), accent: true),
                    Ui.Capsule("Clear", s.ClearSlices),
                    Ui.Label("Drag on the canvas to add a custom slice.", 11, brush: Ui.Secondary));
            }
            case NavigationTool.Eyedropper:
                return Bar(Ui.Title("Eyedropper"), Ui.Check("Sample Ring", () => s.ShowsSampleRing, v => s.ShowsSampleRing = v, binder));
            case NavigationTool.Hand: return Bar(Ui.Title("Pan"));
            case NavigationTool.Zoom:
            {
                var field = Ui.Number(() => s.Viewport.Zoom * 100, v => { if (v > 0 && !s.IsProjectBusy) s.Zoom(Math.Clamp(v, 0.1, 3200) / 100); },
                    binder, 72, 2, 1, Release, "Zoom percentage");
                ToolTipService.SetToolTip(field, "Zoom percentage (0.1–3200%). Press Enter to apply.");
                field.IsEnabled = s.Document != null;
                return Bar(Ui.Title("Zoom"), Ui.WithUnit(field, "%"));
            }
            default: return Bar(Ui.Title("Select a tool"));
        }
    }

    // MARK: Transform (TransformInspector.swift)

    private LayerTransform Value(EditorSession s) => s.TransformEditState?.Draft
        ?? (s.ActiveLayer is { } layer ? s.EditedTransform(layer) : new LayerTransform(PointD.Zero, new SizeD(1, 1)));

    private SizeD PixelSize(EditorSession s) => s.TransformPixelSize ?? s.ActiveLayer?.Size ?? Value(s).Size;

    private void Change(EditorSession s, Func<LayerTransform, LayerTransform> update)
    {
        if (s.TransformEditState == null) s.BeginTransform();
        if (s.TransformEditState?.Draft is not { } value) return;
        s.PreviewTransform(update(value));
    }

    private FrameworkElement Transform(EditorSession s)
    {
        var title = Ui.Title(s.TransformTargetsMask ? "Transform Mask" : "Transform");
        var auto = Ui.Check("Auto Select", () => s.TransformAutoSelect, v => s.TransformAutoSelect = v, binder,
            "Select layers by clicking the canvas. When off, hold Ctrl to select a layer.");
        var controls = Ui.Check("Show Controls", () => s.ShowsTransformControls, v => { s.ShowsTransformControls = v; s.InvalidateCanvas(); }, binder,
            "Show the transform box and handles (Ctrl+H). When hidden, drag anywhere to move the layer.");
        FrameworkElement Field(string label, Func<double> get, Action<double> set, int decimals = 2)
        {
            var box = Ui.Number(get, set, binder, 58, decimals, 1, Release, $"Transform {label}");
            return Ui.Row(4, Ui.Label(label, 11, brush: Ui.Secondary), box);
        }
        var x = Field("X", () => Value(s).Origin.X, v => Change(s, t => t with { Origin = t.Origin with { X = v } }));
        var y = Field("Y", () => Value(s).Origin.Y, v => Change(s, t => t with { Origin = t.Origin with { Y = v } }));
        var w = Field("W", () => Value(s).Size.Width, v => Resize(s, v, true));
        var h = Field("H", () => Value(s).Size.Height, v => Resize(s, v, false));
        var link = new ToggleButton(s);
        var scale = Ui.Row(4, Ui.Label("Scale", 11, brush: Ui.Secondary),
            Ui.Number(() => Value(s).ScalePercent(PixelSize(s)), v => { if (v > 0) Change(s, t => t.ScaledToPercent(v, PixelSize(s))); }, binder, 58, 2, 1, Release, "Transform scale"),
            Ui.Label("%", 11, brush: Ui.Secondary));
        ToolTipService.SetToolTip(scale, "Scale width and height together, about the center");
        var rotationField = Ui.Number(() => Value(s).Rotation, v => Change(s, t => t with { Rotation = v % 360 }),
            binder, 58, 2, 1, Release, "Transform rotation");
        ToolTipService.SetToolTip(rotationField, "Rotation in degrees; updates the selected image live");
        var rotation = Ui.Row(4, Ui.Label("Rotate", 11, brush: Ui.Secondary), rotationField, Ui.Label("°", 11, brush: Ui.Secondary));
        var sampling = Ui.Picker(LayerSamplingNames.All.Select(v => (v, v.ToName())).ToList(), () => Value(s).Sampling,
            v => Change(s, t => t with { Sampling = v }), binder, 150);
        var flipH = Ui.Capsule("Flip H", () => Change(s, t => t with { FlipX = !t.FlipX }));
        var flipV = Ui.Capsule("Flip V", () => Change(s, t => t with { FlipY = !t.FlipY }));
        var fields = Ui.Row(12, x, y, w, h, link.Element, scale, rotation, sampling, flipH, flipV);
        binder.Add(() => fields.IsHitTestVisible = (s.CanTransform || s.TransformEditState != null) && s.TransformEditState?.Corners == null);
        binder.Add(() => fields.Opacity = fields.IsHitTestVisible ? 1 : 0.45);
        var aligns = Ui.Row(2);
        foreach (var (edge, icon, name) in new[] { (AlignEdge.Left, Icons.LucideData(LucideIcons.AlignStartVertical), "Align left edges"), (AlignEdge.HorizontalCenter, Icons.LucideData(LucideIcons.AlignCenterVertical), "Align horizontal centers"), (AlignEdge.Right, Icons.LucideData(LucideIcons.AlignEndVertical), "Align right edges"), (AlignEdge.Top, Icons.LucideData(LucideIcons.AlignStartHorizontal), "Align top edges"), (AlignEdge.VerticalCenter, Icons.LucideData(LucideIcons.AlignCenterHorizontal), "Align vertical centers"), (AlignEdge.Bottom, Icons.LucideData(LucideIcons.AlignEndHorizontal), "Align bottom edges") })
        {
            var e = edge; var tip = name;
            var button = Ui.IconButton(Icons.Fluent(icon, 16), () => s.Align(e), name, 28, 26);
            binder.Add(() => { button.IsEnabled = s.CanAlign; ToolTipService.SetToolTip(button, $"{tip} to {s.AlignReference}"); });
            aligns.Children.Add(button);
        }
        foreach (var (axis, icon, name) in new[] { (DistributeAxis.Horizontal, Icons.LucideData(LucideIcons.AlignHorizontalDistributeCenter), "Distribute horizontal centers (3 or more layers)"), (DistributeAxis.Vertical, Icons.LucideData(LucideIcons.AlignVerticalDistributeCenter), "Distribute vertical centers (3 or more layers)") })
        {
            var a = axis;
            var button = Ui.IconButton(Icons.Fluent(icon, 16), () => s.Distribute(a), name, 28, 26);
            binder.Add(() => button.IsEnabled = s.CanDistribute);
            aligns.Children.Add(button);
        }
        aligns.VerticalAlignment = VerticalAlignment.Center;
        var arrange = Ui.Row(12, fields, new Border { Width = 1, Height = 20, Background = Ui.Divider, VerticalAlignment = VerticalAlignment.Center }, aligns);
        var cancel = Ui.Capsule("Cancel", () => s.CancelTransform());
        var apply = Ui.Capsule("Apply", () => s.CommitTransform(), accent: true);
        binder.Add(() => { cancel.IsEnabled = apply.IsEnabled = s.TransformEditState != null; });
        var grid = new Grid { Padding = new Thickness(18, 0, 18, 0), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Ui.Row(12, title, auto, controls);
        var scroll = new ScrollViewer { Content = arrange, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalAlignment = VerticalAlignment.Center };
        var right = Ui.Row(8, cancel, apply);
        var zoom = ZoomControls();
        Grid.SetColumn(scroll, 1);
        Grid.SetColumn(right, 2);
        Grid.SetColumn(zoom, 3);
        grid.Children.Add(left);
        grid.Children.Add(scroll);
        grid.Children.Add(right);
        grid.Children.Add(zoom);
        return grid;
    }

    private sealed class ToggleButton
    {
        public FrameworkElement Element { get; }
        public ToggleButton(EditorSession s)
        {
            var toggle = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = Icons.Glyph(Icons.Link, 12), Padding = new Thickness(6, 3, 6, 3), MinWidth = 0, CornerRadius = new CornerRadius(6),
                IsChecked = s.LocksTransformRatio, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(toggle, "Lock aspect ratio");
            toggle.Checked += (_, _) => s.LocksTransformRatio = true;
            toggle.Unchecked += (_, _) => s.LocksTransformRatio = false;
            Element = toggle;
        }
    }

    private void Resize(EditorSession s, double number, bool width)
    {
        if (number < 1) return;
        Change(s, value =>
        {
            var size = value.Size;
            if (width)
            {
                if (s.LocksTransformRatio) size = size with { Height = size.Height * number / size.Width };
                size = size with { Width = number };
            }
            else
            {
                if (s.LocksTransformRatio) size = size with { Width = size.Width * number / size.Height };
                size = size with { Height = number };
            }
            return value with { Size = size };
        });
    }

    // MARK: Brushes (BrushControls.swift)

    private FrameworkElement Brush(EditorSession s)
    {
        string title = s.Tool == NavigationTool.SpotHealing ? "Spot Healing" : s.Tool == NavigationTool.CloneStamp ? "Clone Stamp"
            : s.Tool == NavigationTool.Blur ? "Smear" : s.BrushMode == BrushToolMode.Erase ? "Eraser" : "Brush";
        var items = new List<UIElement> { Ui.Title(title) };
        if (s.Tool == NavigationTool.Brush)
            items.Add(Ui.Segmented(new[] { (BrushToolMode.Paint, "Paint"), (BrushToolMode.Erase, "Erase") }, () => s.BrushMode,
                v => { s.BrushMode = v; s.Notify(); }, binder, "Paint with the foreground color (B), or erase pixels away (E)"));
        if (s.Tool == NavigationTool.Blur)
            items.Add(Ui.Segmented(new[] { (BlurToolMode.Liquify, "Liquify"), (BlurToolMode.Blur, "Blur"), (BlurToolMode.Smudge, "Smudge") },
                () => s.BlurMode, v => { s.BlurMode = v; s.Notify(); }, binder, "Liquify pushes pixels · Blur softens · Smudge drags color along"));
        if (s.Tool == NavigationTool.SpotHealing)
            items.Add(Ui.Segmented(Enum.GetValues<SpotHealingMode>().Select(m => (m, m.Name())).ToList(), () => s.SpotHealingMode,
                v => { s.SpotHealingMode = v; s.Notify(); }, binder));
        if (s.Tool == NavigationTool.CloneStamp)
        {
            items.Add(Ui.Check("Aligned", () => s.CloneSettings.Aligned, v => s.CloneSettings = s.CloneSettings with { Aligned = v }, binder,
                "Keep the source moving with the brush between strokes; off starts every stroke at the source point"));
            items.Add(Ui.Segmented(new[] { (false, "This Layer"), (true, "All Layers") }, () => s.CloneSettings.SampleAllLayers,
                v => { s.CloneSettings = s.CloneSettings with { SampleAllLayers = v }; s.Notify(); }, binder,
                "Copy from the active layer only, or from every visible layer as shown"));
        }
        items.Add(Ui.Label("Size"));
        items.Add(Ui.WithUnit(Ui.Number(() => s.BrushSettings.Diameter, v => s.BrushSettings = s.BrushSettings with { Diameter = Math.Clamp(v, 1, 2000) },
            binder, 48, 0, 1, Release, "Size"), "px"));
        items.Add(Ui.Label("Hardness"));
        items.Add(Ui.Slider(0, 1, () => s.BrushSettings.Hardness, v => s.BrushSettings = s.BrushSettings with { Hardness = v }, binder));
        items.Add(Ui.WithUnit(Ui.Number(() => s.BrushSettings.Hardness * 100, v => s.BrushSettings = s.BrushSettings with { Hardness = Math.Clamp(v / 100, 0, 1) },
            binder, 42, 0, 1, Release, "Hardness"), "%"));
        items.Add(Ui.Label(s.Tool == NavigationTool.Blur ? "Strength" : "Opacity"));
        items.Add(Ui.Slider(0.01, 1, () => s.BrushSettings.Opacity, v => s.BrushSettings = s.BrushSettings with { Opacity = v }, binder));
        var opacity = Ui.Number(() => s.BrushSettings.Opacity * 100, v => s.BrushSettings = s.BrushSettings with { Opacity = Math.Clamp(v, 1, 100) / 100 },
            binder, 42, 0, 1, Release, "Opacity");
        ToolTipService.SetToolTip(opacity, "Press 1–9 for 10–90%, 0 for 100%");
        items.Add(Ui.WithUnit(opacity, "%"));
        if (s.IsMaskSelected)
            items.Add(Ui.Picker(new[] { (false, "Black · Hide"), (true, "White · Reveal") }, () => s.MaskPaintWhite, v => s.MaskPaintWhite = v, binder, 150));
        else if (s.Tool != NavigationTool.CloneStamp && s.Tool != NavigationTool.Blur)
            items.Add(Ui.Row(6, Ui.Label("Color"), Ui.Swatch(() => s.ForegroundColor, () => OpenColorPicker?.Invoke(false), binder, tooltip: "Foreground color")));
        if (s.Tool == NavigationTool.CloneStamp && s.CloneSource == null) items.Add(Ui.Label("Alt-click to set the source", brush: Ui.Secondary));
        if (s.IsMaskSelected) items.Add(Ui.Label("Mask", brush: Ui.Secondary));
        return Bar(items.ToArray());
    }

    // MARK: Selection tools (LassoControls.swift)

    private FrameworkElement Selection(EditorSession s)
    {
        var items = new List<UIElement> { Ui.Title(s.Tool == NavigationTool.Marquee ? "Marquee" : s.Tool == NavigationTool.Wand ? "Magic Wand" : "Lasso") };
        if (s.Tool == NavigationTool.Marquee)
            items.Add(Ui.Segmented(new[] { (LassoKind.Rectangle, "Rectangle"), (LassoKind.Ellipse, "Ellipse") }, () => s.MarqueeKind,
                v => { s.CancelLasso(); s.MarqueeKind = v; s.Notify(); }, binder, "Press M to switch between Rectangle and Ellipse"));
        if (s.Tool == NavigationTool.Lasso)
            items.Add(Ui.Segmented(new[] { (LassoKind.Freehand, "Freehand"), (LassoKind.Polygonal, "Polygonal") }, () => s.LassoKind,
                v => { s.CancelLasso(); s.LassoKind = v; s.Notify(); }, binder, "Press L to switch between Freehand and Polygonal"));
        items.Add(Ui.Segmented(new[] { (SelectionMode.Replace, "New"), (SelectionMode.Add, "Add"), (SelectionMode.Subtract, "Subtract") },
            () => s.DisplayedSelectionMode, v => { s.SelectionModeChoice = v; s.Notify(); }, binder, "Hold Shift to add or Alt to subtract for one outline"));
        if (s.Tool == NavigationTool.Wand)
        {
            var tolerance = Ui.Number(() => s.WandSettings.Tolerance, v => s.WandSettings = s.WandSettings with { Tolerance = (int)Math.Clamp(Math.Round(v), 0, 255) },
                binder, 44, 0, 1, Release, "Tolerance");
            ToolTipService.SetToolTip(tolerance, "How far each color channel (0–255) can differ from the clicked color and still be selected");
            items.Add(Ui.Row(6, Ui.Label("Tolerance"), tolerance));
            items.Add(Ui.Picker(new[] { (WandSampleSize.Point, "Point Sample"), (WandSampleSize.ThreeByThree, "3 by 3 Average"), (WandSampleSize.FiveByFive, "5 by 5 Average") },
                () => s.WandSettings.SampleSize, v => s.WandSettings = s.WandSettings with { SampleSize = v }, binder));
            items.Add(Ui.Segmented(new[] { (false, "This Layer"), (true, "All Layers") }, () => s.WandSettings.SampleAllLayers,
                v => { s.WandSettings = s.WandSettings with { SampleAllLayers = v }; s.Notify(); }, binder));
            items.Add(Ui.Check("Contiguous", () => s.WandSettings.Contiguous, v => s.WandSettings = s.WandSettings with { Contiguous = v }, binder,
                "Select only similar pixels connected to the one you click; off selects them everywhere"));
        }
        if (s.Tool == NavigationTool.Lasso || s.Tool == NavigationTool.Wand || (s.Tool == NavigationTool.Marquee && s.MarqueeKind == LassoKind.Ellipse))
            items.Add(Ui.Check("Anti-alias", () => s.SelectionAntialiased, v => s.SelectionAntialiased = v, binder, "Smooth selection edges; turn off for hard pixel edges"));
        items.Add(Ui.VerticalDivider());
        FrameworkElement Modify(string title, Func<int> get, Action<int> set, Action action)
        {
            var button = Ui.Capsule(title, action);
            var field = Ui.Number(() => get(), v => set((int)Math.Clamp(Math.Round(v), 1, 500)), binder, 40, 0, 1, Release, title);
            var row = Ui.Row(6, button, Ui.WithUnit(field, "px"));
            binder.Add(() => row.Opacity = s.CanModifySelection ? 1 : 0.45);
            binder.Add(() => row.IsHitTestVisible = s.CanModifySelection);
            ToolTipService.SetToolTip(row, $"{title} the selection by this many pixels");
            return row;
        }
        items.Add(Modify("Expand", () => s.SelectionExpandAmount, v => s.SelectionExpandAmount = v, () => s.ExpandSelection(s.SelectionExpandAmount)));
        items.Add(Modify("Contract", () => s.SelectionContractAmount, v => s.SelectionContractAmount = v, () => s.ContractSelection(s.SelectionContractAmount)));
        items.Add(Modify("Feather", () => s.SelectionFeatherAmount, v => s.SelectionFeatherAmount = Math.Min(250, v), () => s.FeatherSelection(s.SelectionFeatherAmount)));
        if (s.Selection is { } selection)
        {
            if (selection.IsEmpty) items.Add(Ui.Label("Empty selection", brush: Ui.Secondary));
            items.Add(Ui.Capsule("Deselect", s.Deselect));
        }
        return Bar(items.ToArray());
    }

    // MARK: Gradient (GradientControls.swift)

    private FrameworkElement Gradient(EditorSession s)
    {
        var swatch = new Border { Width = 72, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Solid(128, 0, 0, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        binder.Add(() =>
        {
            var (first, last) = s.GradientColors(false);
            var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
            brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb((byte)(first.A * 255), (byte)(first.R * 255), (byte)(first.G * 255), (byte)(first.B * 255)) });
            brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb((byte)(last.A * 255), (byte)(last.R * 255), (byte)(last.G * 255), (byte)(last.B * 255)) });
            swatch.Background = brush;
        });
        var items = new List<UIElement>
        {
            Ui.Segmented(new[] { (GradientShape.Linear, "Linear"), (GradientShape.Radial, "Radial") }, () => s.GradientSettings.Shape,
                v => s.GradientSettings = s.GradientSettings with { Shape = v }, binder, "Linear runs along the line; Radial spreads out from the start point"),
            new Border { Background = Ui.Solid(255, 191, 191, 191), CornerRadius = new CornerRadius(3), Child = swatch },
            Ui.Picker(new[] { (GradientStyle.ForegroundToBackground, "FG to BG"), (GradientStyle.ForegroundToTransparent, "FG to Transparent") },
                () => s.GradientSettings.Style, v => s.GradientSettings = s.GradientSettings with { Style = v }, binder, 142),
            Ui.Check("Reverse", () => s.GradientSettings.Reversed, v => s.GradientSettings = s.GradientSettings with { Reversed = v }, binder),
            Ui.Label("Opacity"),
            Ui.Slider(0.01, 1, () => s.GradientSettings.Opacity, v => s.GradientSettings = s.GradientSettings with { Opacity = v }, binder),
            Ui.WithUnit(Ui.Number(() => s.GradientSettings.Opacity * 100, v => s.GradientSettings = s.GradientSettings with { Opacity = Math.Clamp(v, 1, 100) / 100 },
                binder, 42, 0, 1, Release, "Opacity"), "%"),
        };
        if (s.IsMaskSelected) items.Add(Ui.Label("Mask", brush: Ui.Secondary));
        if (s.GradientEdit != null)
        {
            items.Add(Ui.Capsule("Cancel", s.CancelGradient));
            items.Add(Ui.Capsule("Apply", () => _ = s.CommitGradient(), accent: true));
        }
        return Bar(items.ToArray());
    }

    // MARK: Shape (ShapeControls.swift)

    private FrameworkElement Shape(EditorSession s)
    {
        var kinds = Enum.GetValues<ShapeKind>().Select(k => (k, (Func<FrameworkElement>)(() => Icons.ShapeIcon(k, 16)), k.Name())).ToList();
        var items = new List<UIElement>
        {
            Ui.Title("Shape"),
            Ui.IconSegmented(kinds, () => s.ShapeKind, v => { s.CancelShape(); s.ShapeKind = v; s.Notify(); }, binder),
        };
        if (ShapeDraft.Rounds(s.ShapeKind))
        {
            // One radius for every corner, or (rectangles and triangles) a radius per corner.
            bool canSplit = s.ShapeKind is ShapeKind.Rectangle or ShapeKind.Triangle;
            if (!canSplit || s.ShapeCornerRadii == null)
            {
                var radius = Ui.Row(6, Ui.Label("Radius"),
                    Ui.Slider(0, 200, () => Math.Min(200, s.ShapeCornerRadius), v => s.ShapeCornerRadius = Math.Round(v), binder),
                    Ui.WithUnit(Ui.Number(() => s.ShapeCornerRadius, v => s.ShapeCornerRadius = Math.Clamp(v, 0, 5000), binder, 48, 0, 1, Release, "Radius"), "px"));
                ToolTipService.SetToolTip(radius, "Round the corners by this many pixels; 0 keeps them sharp");
                items.Add(radius);
            }
            if (canSplit)
            {
                items.Add(Ui.Check("All corners", () => s.ShapeCornerRadii == null, v =>
                {
                    s.ShapeCornerRadii = v ? null : ShapeCorners.All(s.ShapeCornerRadius);
                    s.Notify();
                }, binder, "Untick to give each corner its own radius"));
                if (s.ShapeCornerRadii != null)
                {
                    var corners = s.ShapeKind == ShapeKind.Rectangle
                        ? new[] { ("↖", "Top-left corner"), ("↗", "Top-right corner"), ("↘", "Bottom-right corner"), ("↙", "Bottom-left corner") }
                        : new[] { ("▲", "Top corner"), ("◢", "Bottom-right corner"), ("◣", "Bottom-left corner") };
                    for (int i = 0; i < corners.Length; i++)
                    {
                        int corner = i;
                        var field = Ui.Number(() => s.ShapeCornerRadii?[corner] ?? 0,
                            v => s.ShapeCornerRadii = (s.ShapeCornerRadii ?? ShapeCorners.All(s.ShapeCornerRadius)).With(corner, Math.Clamp(v, 0, 5000)),
                            binder, 44, 0, 1, Release, corners[i].Item2);
                        var row = Ui.Row(3, Ui.Label(corners[i].Item1, 12), field);
                        ToolTipService.SetToolTip(row, corners[i].Item2 + " radius in pixels");
                        items.Add(row);
                    }
                }
            }
        }
        if (s.ShapeKind is ShapeKind.Polygon or ShapeKind.Star)
        {
            string title = s.ShapeKind == ShapeKind.Star ? "Points" : "Sides";
            var sides = Ui.Row(6, Ui.Label(title),
                Ui.Slider(3, 20, () => Math.Min(20, s.ShapeSides), v => s.ShapeSides = (int)Math.Round(v), binder),
                Ui.Number(() => s.ShapeSides, v => s.ShapeSides = (int)Math.Clamp(Math.Round(v), 3, 100), binder, 40, 0, 1, Release, title));
            items.Add(sides);
        }
        if (s.ShapeKind == ShapeKind.Line)
        {
            var weight = Ui.Row(6, Ui.Label("Weight"),
                Ui.Slider(1, 100, () => Math.Min(100, s.ShapeLineWeight), v => s.ShapeLineWeight = Math.Round(v), binder),
                Ui.WithUnit(Ui.Number(() => s.ShapeLineWeight, v => s.ShapeLineWeight = Math.Clamp(v, 1, 1000), binder, 44, 0, 1, Release, "Weight"), "px"));
            ToolTipService.SetToolTip(weight, "The line's thickness; Shift snaps the line to 45°");
            items.Add(weight);
        }
        items.Add(Ui.Row(6, Ui.Label("Fill"), Ui.Swatch(() => s.ForegroundColor, () => OpenColorPicker?.Invoke(false), binder, 36, 18,
            "Shapes fill with the foreground color; click to change it")));
        return Bar(items.ToArray());
    }

    // MARK: Crop (CropControls.swift)

    private FrameworkElement Crop(EditorSession s)
    {
        var ratios = new[] { "Free", "Original", "1:1", "4:3", "16:9" };
        var picker = Ui.Picker(ratios.Select(r => (r, r)).ToList(), () => s.CropRatioChoice, v => { s.CropRatioChoice = v; s.ChangeCropRatio(); }, binder, 150);
        var size = Ui.Label("");
        binder.Add(() => size.Text = s.CropRect is { } r ? $"{(int)r.Width} × {(int)r.Height} px" : "");
        var cancel = Ui.Capsule("Cancel", s.CancelCrop);
        var apply = Ui.Capsule("Apply Crop", () => _ = s.CommitCrop(), accent: true);
        binder.Add(() => cancel.IsEnabled = apply.IsEnabled = s.CropRect != null);
        var grid = new Grid { Padding = new Thickness(18, 0, 18, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.Row(14, Ui.Title("Crop"), picker, size));
        var right = Ui.Row(8, cancel, apply);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }
}
