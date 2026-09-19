using Compositor.Editing;
using Compositor.Format;
using Compositor.Geometry;
using Compositor.Model;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using WinColor = Windows.UI.Color;

using SelectionMode = Compositor.Editing.SelectionMode;

namespace Compositor.App.Controls;

/// <summary>The Layers panel: header, blend and opacity, the layer list and its footer (LayersPanel.swift,
/// LayerAppearanceControls.swift, NativeLayerList.swift).</summary>
public sealed class LayersPanel : UserControl
{
    private readonly Func<EditorSession?> session;
    private readonly TextBlock count = Ui.Label("0", 11, brush: Ui.Brush("TertiaryTextBrush"));
    private readonly StackPanel rowsPanel = new() { Spacing = 2 };
    private readonly ScrollViewer scroll;
    private readonly Grid listHost = new();
    private readonly FrameworkElement empty;
    private readonly TextBlock emptyDetail = Ui.Label("", 11, brush: Ui.Secondary);
    private readonly ComboBox blend = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Slider opacity = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.001, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox opacityField = new() { Width = 44, FontSize = 12, TextAlignment = TextAlignment.Right, MinWidth = 0 };
    private readonly Grid appearance = new() { Padding = new Thickness(12), RowSpacing = 8, ColumnSpacing = 6 };
    private readonly Border insertionLine = new() { Height = 2, Background = new SolidColorBrush(Colors.DodgerBlue), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Button newLayer, newFolder, addMask, adjustments, trash;
    private readonly List<LayerRow> rows = new();
    private string? rowSignature;
    private bool updatingAppearance;
    private bool opacityFieldFocused;
    public Action? ReleaseFocus;
    /// <summary>Change Color, Gradient Overlay or Add Shadow for the active layer ("color", "gradient", "shadow").</summary>
    public Action<string>? RunEffect;
    public Action<Guid, Point>? DroppedOutside;
    public Func<Point, bool>? IsOverTabStrip;

    public const double MinWidthValue = 202, MaxWidthValue = 352;

    public LayersPanel(Func<EditorSession?> session)
    {
        this.session = session;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Padding = new Thickness(18) };
        header.Children.Add(Ui.Label("Layers", 12, true));
        count.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(count);
        Add(root, header, 0);
        Add(root, Ui.HorizontalDivider(), 1);

        // Blend and opacity.
        appearance.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appearance.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        appearance.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appearance.RowDefinitions.Add(new RowDefinition());
        appearance.RowDefinitions.Add(new RowDefinition());
        var blendLabel = Ui.Label("Blend", 11);
        foreach (var mode in BlendModes.All) blend.Items.Add(new ComboBoxItem { Content = mode.ToName(), Tag = mode });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(blend, "Blend mode");
        blend.SelectionChanged += (_, _) =>
        {
            if (updatingAppearance || session() is not { } s || blend.SelectedItem is not ComboBoxItem { Tag: LayerBlendMode mode }) return;
            s.SetLayerBlendMode(mode);
        };
        blend.DropDownOpened += (_, _) =>
        {
            // Previews each mode on the canvas as it is highlighted, as the Mac pop-up does.
            foreach (ComboBoxItem item in blend.Items)
            {
                item.PointerEntered -= PreviewBlend;
                item.PointerEntered += PreviewBlend;
                item.GotFocus -= PreviewBlendFocus;
                item.GotFocus += PreviewBlendFocus;
            }
        };
        blend.DropDownClosed += (_, _) => session()?.PreviewBlendMode(null, null);
        Grid.SetColumnSpan(blend, 2);
        Place(appearance, blendLabel, 0, 0);
        Place(appearance, blend, 0, 1);
        Grid.SetColumnSpan(blend, 2);
        var opacityLabel = Ui.Label("Opacity", 11);
        Place(appearance, opacityLabel, 1, 0);
        Place(appearance, opacity, 1, 1);
        Place(appearance, Ui.Row(2, opacityField, Ui.Label("%", 11)), 1, 2);
        opacity.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => session()?.BeginOpacityEdit()), true);
        opacity.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => session()?.FinishOpacityEdit()), true);
        opacity.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => session()?.FinishOpacityEdit()), true);
        opacity.ValueChanged += (_, e) => { if (!updatingAppearance) session()?.SetLayerOpacity(e.NewValue); };
        opacityField.GotFocus += (_, _) => { opacityFieldFocused = true; opacityField.SelectAll(); };
        opacityField.LostFocus += (_, _) => { opacityFieldFocused = false; ApplyOpacityField(); };
        opacityField.KeyDown += (_, e) =>
        {
            if (session() is not { } s) return;
            if (e.Key is VirtualKey.Up or VirtualKey.Down)
            {
                bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                double current = Math.Round((s.ActiveLayer?.Opacity ?? 1) * 100);
                s.SetLayerOpacity(Math.Clamp(current + (e.Key == VirtualKey.Up ? 1 : -1) * (shift ? 10 : 1), 0, 100) / 100);
                SyncOpacityField();
                opacityField.SelectAll();
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Enter or VirtualKey.Escape) { if (e.Key == VirtualKey.Enter) ApplyOpacityField(); ReleaseFocus?.Invoke(); e.Handled = true; }
        };
        Add(root, appearance, 2);
        Add(root, Ui.HorizontalDivider(), 3);

        scroll = new ScrollViewer { Content = rowsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 2, 0, 2) };
        listHost.Children.Add(scroll);
        listHost.Children.Add(insertionLine);
        empty = new StackPanel
        {
            Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(16),
            Children = { new Viewbox { Width = 26, Height = 26, Child = Icons.Stroke("M9,2 L16,5.5 L9,9 L2,5.5 Z M2,9 L9,12.5 L16,9 M2,12.5 L9,16 L16,12.5", 26, 1) },
                         Ui.Label("No layers yet", 13, true, Ui.Secondary), emptyDetail },
        };
        emptyDetail.TextAlignment = TextAlignment.Center;
        emptyDetail.TextWrapping = TextWrapping.Wrap;
        listHost.Children.Add(empty);
        listHost.KeyDown += OnListKeyDown;
        listHost.IsTabStop = true;
        Add(root, listHost, 4);
        Add(root, Ui.HorizontalDivider(), 5);

        newLayer = Ui.IconButton(Icons.Lucide(LucideIcons.SquarePlus, 16), () => session()?.AddBlankLayer(), "New blank layer (Ctrl+Shift+N)");
        newFolder = Ui.IconButton(Icons.Lucide(LucideIcons.FolderPlus, 16), () => session()?.GroupSelectedLayers(), "Group selected layers (Ctrl+G)");
        addMask = Ui.IconButton(Icons.Lucide(LucideIcons.RectangleCircle, 16), () => session()?.AddMask(), "Add layer mask");
        adjustments = Ui.IconButton(Icons.Lucide(LucideIcons.Contrast, 16), () => { }, "New adjustment layer");
        var flyout = new MenuFlyout();
        foreach (var kind in AdjustmentKinds.All)
        {
            var item = new MenuFlyoutItem { Text = kind.ToName() };
            var captured = kind;
            item.Click += (_, _) => session()?.AddAdjustment(captured);
            flyout.Items.Add(item);
        }
        adjustments.Flyout = flyout;
        trash = Ui.IconButton(Icons.Lucide(LucideIcons.Trash2, 16), () => { if (session() is { } s) _ = s.DeleteLayerOrMask(); }, "Delete selected layer");
        var footer = new Grid { Padding = new Thickness(8, 4, 8, 4) };
        footer.Children.Add(Ui.Row(0, newLayer, newFolder, addMask, adjustments));
        trash.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(trash);
        Add(root, footer, 6);
        Content = root;
        Width = 252;
    }

    private void PreviewBlend(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ComboBoxItem { Tag: LayerBlendMode mode } && session() is { } s) s.PreviewBlendMode(mode, s.ActiveLayerId);
    }

    private void PreviewBlendFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBoxItem { Tag: LayerBlendMode mode } && session() is { } s) s.PreviewBlendMode(mode, s.ActiveLayerId);
    }

    private static void Add(Grid grid, FrameworkElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private void ApplyOpacityField()
    {
        if (session() is { } s && double.TryParse(opacityField.Text.Replace("%", ""), out var value) && double.IsFinite(value)) s.SetLayerOpacity(value / 100);
        SyncOpacityField();
    }

    private void SyncOpacityField()
    {
        if (!opacityFieldFocused) opacityField.Text = Math.Round((session()?.ActiveLayer?.Opacity ?? 1) * 100).ToString();
    }

    // MARK: Refresh

    public void Refresh()
    {
        var s = session();
        var doc = s?.Document;
        count.Text = (doc?.Layers.Count ?? 0).ToString();
        updatingAppearance = true;
        var active = s?.ActiveLayer;
        blend.SelectedIndex = Array.IndexOf(BlendModes.All, active?.BlendMode ?? LayerBlendMode.Normal);
        if (s != null && s.OpacityEditLayerId == null) opacity.Value = active?.Opacity ?? 1;
        SyncOpacityField();
        updatingAppearance = false;
        bool canAppearance = s?.CanEditAppearance == true;
        appearance.IsHitTestVisible = canAppearance;
        appearance.Opacity = canAppearance ? 1 : 0.45;
        bool canEdit = s?.CanEditLayers == true;
        newLayer.IsEnabled = canEdit;
        newFolder.IsEnabled = canEdit;
        adjustments.IsEnabled = canEdit;
        addMask.IsEnabled = s?.CanEditMask == true && active?.Mask == null;
        ToolTipService.SetToolTip(addMask, s?.Selection == null ? "Add layer mask" : "Add layer mask (the selection becomes black)");
        trash.IsEnabled = canEdit && active != null;
        ToolTipService.SetToolTip(trash, s?.IsMaskSelected == true ? "Delete layer mask" : s?.SelectedLayerIds.Count > 1 ? "Delete selected layers" : "Delete selected layer");
        if (doc == null || doc.Layers.IsEmpty)
        {
            empty.Visibility = Visibility.Visible;
            scroll.Visibility = Visibility.Collapsed;
            emptyDetail.Text = doc == null ? "Create a canvas or import an image." : "Import an image or add a blank layer.";
            rowsPanel.Children.Clear();
            rows.Clear();
            rowSignature = null;
            return;
        }
        empty.Visibility = Visibility.Collapsed;
        scroll.Visibility = Visibility.Visible;
        var entries = s!.LayerRows;
        string signature = string.Join(",", entries.Select(e => e.Layer.Id));
        if (signature != rowSignature)
        {
            rowSignature = signature;
            rowsPanel.Children.Clear();
            rows.Clear();
            foreach (var entry in entries)
            {
                var row = new LayerRow(this, entry.Layer.Id);
                rows.Add(row);
                rowsPanel.Children.Add(row);
            }
        }
        var byId = doc.Layers.ToDictionary(l => l.Id);
        for (int i = 0; i < entries.Count; i++)
            if (byId.TryGetValue(entries[i].Layer.Id, out var layer)) rows[i].Update(s, layer, entries[i].Depth, entries[i].Visible);
        if (s.RenamingLayerId is { } renaming && rows.FirstOrDefault(r => r.LayerId == renaming) is { } target) target.BeginRenaming(s);
    }

    // MARK: List keys

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (session() is not { } s || e.OriginalSource is TextBox) return;
        bool ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu), shift = Down(VirtualKey.Shift);
        bool plain = !ctrl && !alt;
        if (e.Key == VirtualKey.Escape && s.TransformEditState != null) { s.CancelTransform(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter && s.TransformEditState != null) { s.CommitTransform(); e.Handled = true; return; }
        if (plain && (s.TransformEditState != null || s.Tool == NavigationTool.Move) && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            double step = shift ? 10 : 1;
            s.NudgeLayer(e.Key == VirtualKey.Left ? -step : e.Key == VirtualKey.Right ? step : 0, e.Key == VirtualKey.Up ? -step : e.Key == VirtualKey.Down ? step : 0);
            e.Handled = true;
            return;
        }
        if (plain && e.Key is VirtualKey.Back or VirtualKey.Delete) { _ = s.DeleteKeyPressed(); e.Handled = true; return; }
        if (plain && CanvasView.HandleToolKey(s, e.Key, shift)) e.Handled = true;
    }

    internal static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    // MARK: Dragging rows

    private Guid? dragLayer;
    private Guid? dragMask;
    private Point dragStart;
    private bool dragging;
    private (Guid? Parent, Guid? Above, bool AtBottom, Guid? Into)? dropTarget;
    private Guid? maskDropTarget;

    internal void BeginRowPress(Guid id, Point point, bool maskThumbnail)
    {
        dragLayer = maskThumbnail ? null : id;
        dragMask = maskThumbnail ? id : null;
        dragStart = point;
        dragging = false;
    }

    internal void MoveRowPress(Point point, Point windowPoint)
    {
        if (dragLayer == null && dragMask == null) return;
        if (!dragging && Math.Abs(point.Y - dragStart.Y) < 4 && Math.Abs(point.X - dragStart.X) < 4) return;
        if (dragMask != null && !Down(VirtualKey.Menu)) return;
        dragging = true;
        var local = point;
        int index = RowIndexAt(local, out var within);
        if (dragMask is { } source)
        {
            maskDropTarget = index >= 0 && index < rows.Count && session()!.CanCopyMask(source, rows[index].LayerId) ? rows[index].LayerId : null;
            foreach (var row in rows) row.SetDropHighlight(row.LayerId == maskDropTarget);
            return;
        }
        var s = session()!;
        var entries = s.LayerRows;
        insertionLine.Visibility = Visibility.Collapsed;
        foreach (var row in rows) row.SetDropHighlight(false);
        dropTarget = null;
        if (IsOverTabStrip?.Invoke(windowPoint) == true) return;
        if (index >= 0 && index < entries.Count && entries[index].Layer.IsGroup == true && within > 0.25 && within < 0.75 && s.CanPlaceLayer(dragLayer!.Value, entries[index].Layer.Id))
        {
            rows[index].SetDropHighlight(true);
            dropTarget = (entries[index].Layer.Id, null, false, entries[index].Layer.Id);
            return;
        }
        // Between rows: dropping above row `i` in the list (top first) places the layer above it.
        int slot = index < 0 ? entries.Count : within >= 0.5 ? index + 1 : index;
        slot = Math.Clamp(slot, 0, entries.Count);
        (Guid? Parent, Guid? Above, bool AtBottom) placement = slot >= entries.Count ? (null, null, true) : (entries[slot].Layer.ParentId, entries[slot].Layer.Id, false);
        if (!s.CanPlaceLayer(dragLayer!.Value, placement.Parent)) return;
        dropTarget = (placement.Parent, placement.Above, placement.AtBottom, null);
        double y = slot < rows.Count ? rows[slot].TransformToVisual(listHost).TransformPoint(new Point(0, 0)).Y - 1
            : rows.Count > 0 ? rows[^1].TransformToVisual(listHost).TransformPoint(new Point(0, rows[^1].ActualHeight)).Y + 1 : 0;
        insertionLine.Margin = new Thickness(8, Math.Max(0, y), 8, 0);
        insertionLine.Visibility = Visibility.Visible;
    }

    internal void EndRowPress(Point windowPoint)
    {
        var s = session();
        insertionLine.Visibility = Visibility.Collapsed;
        foreach (var row in rows) row.SetDropHighlight(false);
        if (s == null || !dragging) { dragLayer = null; dragMask = null; dragging = false; return; }
        if (dragMask is { } maskSource && maskDropTarget is { } maskTarget) s.CopyMask(maskSource, maskTarget);
        else if (dragLayer is { } id)
        {
            if (IsOverTabStrip?.Invoke(windowPoint) == true) DroppedOutside?.Invoke(id, windowPoint);
            else if (dropTarget is { } target)
            {
                bool copying = Down(VirtualKey.Menu);
                var ids = DraggedLayers(s, id);
                if (copying && ids.Any(i => s.Document!.Layer(i)?.IsGroup != false)) { }
                else
                {
                    var order = target.Into != null ? Enumerable.Reverse(ids).ToList() : ids;
                    s.BeginEdit(copying ? (ids.Count > 1 ? "Duplicate Layers" : "Duplicate Layer") : (ids.Count > 1 ? "Move Layers" : "Move Layer"));
                    bool placed = false;
                    foreach (var layer in order)
                        placed = (copying ? s.DuplicateLayer(layer, target.Parent, target.Above, target.AtBottom)
                                          : s.PlaceLayer(layer, target.Parent, target.Above, target.AtBottom)) || placed;
                    if (placed && !copying) s.SelectLayers(ids.ToHashSet(), ids.FirstOrDefault());
                    s.EndEdit();
                }
            }
        }
        dragLayer = null;
        dragMask = null;
        dragging = false;
        dropTarget = null;
        maskDropTarget = null;
    }

    internal bool IsDragging => dragging;

    /// <summary>Every layer being dragged in list order, leaving out anything inside a dragged folder.</summary>
    private static List<Guid> DraggedLayers(EditorSession s, Guid pressed)
    {
        var dragged = s.SelectedLayerIds.Contains(pressed) ? s.SelectedLayerIds.ToHashSet() : new HashSet<Guid> { pressed };
        var carried = new HashSet<Guid>();
        foreach (var id in dragged) carried.UnionWith(s.DescendantIds(id));
        return s.LayerRows.Select(r => r.Layer.Id).Where(id => dragged.Contains(id) && !carried.Contains(id)).ToList();
    }

    private int RowIndexAt(Point listPoint, out double within)
    {
        within = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var top = rows[i].TransformToVisual(listHost).TransformPoint(new Point(0, 0)).Y;
            if (listPoint.Y >= top && listPoint.Y < top + rows[i].ActualHeight + 2)
            {
                within = (listPoint.Y - top) / Math.Max(1, rows[i].ActualHeight);
                return i;
            }
        }
        return -1;
    }

    internal Point ToList(PointerRoutedEventArgs e) => e.GetCurrentPoint(listHost).Position;
    internal Func<EditorSession?> SessionProvider => session;
    internal void FocusList() => listHost.Focus(FocusState.Programmatic);

    // MARK: Eye swipe

    private bool? swipeVisible;
    internal void BeginSwipe(Guid id)
    {
        if (session() is not { } s) return;
        swipeVisible = s.BeginVisibilitySwipe(id);
    }

    internal void ContinueSwipe(Point listPoint)
    {
        if (swipeVisible is not { } visible || session() is not { } s) return;
        int index = RowIndexAt(listPoint, out _);
        if (index >= 0 && index < rows.Count) s.SetVisibilityInSwipe(rows[index].LayerId, visible);
    }

    internal void EndSwipe()
    {
        if (swipeVisible == null) return;
        swipeVisible = null;
        session()?.EndVisibilitySwipe();
    }
}

/// <summary>One row of the list (LayerCell).</summary>
internal sealed class LayerRow : UserControl
{
    private readonly LayersPanel panel;
    public Guid LayerId { get; }
    private readonly Grid grid = new() { Height = 52 };
    private readonly Border background = new() { CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0) };
    private readonly Button eye = new() { Width = 20, Height = 32, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), MinWidth = 0 };
    private readonly Button disclosure = new() { Width = 16, Height = 24, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), MinWidth = 0 };
    private readonly Border thumbnail = new() { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(0), BorderBrush = new SolidColorBrush(Colors.DodgerBlue) };
    private readonly Border maskThumbnail = new() { CornerRadius = new CornerRadius(3), BorderBrush = new SolidColorBrush(Colors.DodgerBlue) };
    private readonly TextBlock disabledMark = new() { Text = "╱", FontSize = 30, Foreground = new SolidColorBrush(Colors.Red), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Button link = new() { Width = 12, Height = 20, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), MinWidth = 0 };
    private readonly TextBlock name = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox renameBox = new() { FontSize = 13, Visibility = Visibility.Collapsed, MinWidth = 0, Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBlock detail = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ColumnDefinition indent = new() { Width = new GridLength(0) };
    private readonly Grid thumbSlot = new() { Width = 36 };
    private readonly Grid maskSlot = new() { Width = 30 };
    private ImageLayer? layer;
    private bool renaming;
    private object? thumbKey, maskKey;
    private bool pressedOnRow;

    public LayerRow(LayersPanel panel, Guid id)
    {
        this.panel = panel;
        LayerId = id;
        ToolTipService.SetToolTip(thumbnail, "Select layer; Ctrl-click to select its pixels (Ctrl+Shift adds, Ctrl+Alt subtracts)");
        ToolTipService.SetToolTip(maskThumbnail, "Select layer mask; Shift-click to enable/disable; Ctrl-click to select its black areas (Ctrl+Shift adds, Ctrl+Alt subtracts)");
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(indent);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumnSpan(background, 8);
        grid.Children.Add(background);
        Place(eye, 1);
        Place(disclosure, 3);
        thumbSlot.Margin = new Thickness(-2, 0, 0, 0);
        thumbSlot.Children.Add(thumbnail);
        thumbnail.HorizontalAlignment = HorizontalAlignment.Center;
        thumbnail.VerticalAlignment = VerticalAlignment.Center;
        Place(thumbSlot, 4);
        link.Content = Icons.Glyph(Icons.Link, 10);
        link.Foreground = Ui.Secondary;
        Place(link, 5);
        maskSlot.Children.Add(maskThumbnail);
        maskSlot.Children.Add(disabledMark);
        maskThumbnail.HorizontalAlignment = HorizontalAlignment.Center;
        maskThumbnail.VerticalAlignment = VerticalAlignment.Center;
        Place(maskSlot, 6);
        var text = new StackPanel { Margin = new Thickness(5, 9, 8, 0), Spacing = 3 };
        text.Children.Add(name);
        text.Children.Add(renameBox);
        text.Children.Add(detail);
        detail.Foreground = Ui.Secondary;
        Place(text, 7);
        var edge = new Border { Height = 1, Background = Ui.Solid(15, 255, 255, 255), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        Grid.SetColumnSpan(edge, 8);
        grid.Children.Add(edge);
        Content = grid;

        eye.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            panel.BeginSwipe(LayerId);
            eye.CapturePointer(e.Pointer);
            e.Handled = true;
        }), true);
        eye.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) => panel.ContinueSwipe(panel.ToList(e))), true);
        eye.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => { eye.ReleasePointerCapture(e.Pointer); panel.EndSwipe(); }), true);
        eye.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => panel.EndSwipe()), true);
        disclosure.Click += (_, _) => Session?.ToggleGroupExpansion(LayerId);
        link.Click += (_, _) => Session?.ToggleMaskLink(LayerId);
        thumbnail.PointerPressed += (_, e) => ThumbnailPressed(e, mask: false);
        maskThumbnail.PointerPressed += (_, e) => ThumbnailPressed(e, mask: true);
        grid.PointerPressed += RowPressed;
        grid.PointerMoved += (_, e) => { if (pressedOnRow) panel.MoveRowPress(panel.ToList(e), e.GetCurrentPoint(null).Position); };
        grid.PointerReleased += (_, e) =>
        {
            if (!pressedOnRow) return;
            pressedOnRow = false;
            grid.ReleasePointerCapture(e.Pointer);
            panel.EndRowPress(e.GetCurrentPoint(null).Position);
        };
        grid.DoubleTapped += (_, _) =>
        {
            if (Session is not { CanEditLayers: true } s || layer == null) return;
            s.ActiveLayerId = LayerId;
            if (layer.Adjustment != null) { s.EditAdjustment(LayerId); return; }
            s.RenamingLayerId = LayerId;
            BeginRenaming(s);
        };
        renameBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { EndRenaming(true); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { EndRenaming(false); e.Handled = true; }
        };
        renameBox.LostFocus += (_, _) => EndRenaming(true);
        ContextFlyout = BuildMenu();
    }

    private EditorSession? Session => panel.SessionProvider();

    private void Place(FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        element.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        void Item(string text, Action<EditorSession> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => { if (Session is { } s) action(s); };
            menu.Items.Add(item);
        }
        Item("Change Color…", s => { s.SelectLayer(LayerId); panel.RunEffect?.Invoke("color"); });
        Item("Gradient Overlay…", s => { s.SelectLayer(LayerId); panel.RunEffect?.Invoke("gradient"); });
        Item("Add Shadow…", s => { s.SelectLayer(LayerId); panel.RunEffect?.Invoke("shadow"); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Rename…", s => { if (!s.CanEditLayers) return; s.ActiveLayerId = LayerId; s.RenamingLayerId = LayerId; BeginRenaming(s); });
        Item("Hide/Show Layer", s => s.ToggleLayerVisibility(LayerId));
        Item("Add White Mask", s => { s.SelectLayerTarget(LayerId, false); s.AddMask(); });
        Item("Add Black Mask", s => { s.SelectLayerTarget(LayerId, false); s.AddMask(revealing: false); });
        Item("Enable/Disable Mask", s => { s.SelectLayerTarget(LayerId, false); s.ToggleLayerMask(); });
        Item("Delete Mask", s => { s.SelectLayerTarget(LayerId, false); s.DeleteLayerMask(); });
        Item("Release Clipping Mask", s => s.RemoveLiveMask(LayerId));
        Item("Move Out of Folder", s => { s.SelectLayer(LayerId); s.MoveActiveLayerOutOfGroup(); });
        Item("Delete Layer / Folder", s =>
        {
            if (s.SelectedLayerIds.Count > 1 && s.SelectedLayerIds.Contains(LayerId)) _ = s.DeleteSelectedLayers();
            else _ = s.DeleteLayer(LayerId);
        });
        return menu;
    }

    private void ThumbnailPressed(PointerRoutedEventArgs e, bool mask)
    {
        if (Session is not { } s) return;
        var modifiers = e.KeyModifiers;
        bool ctrl = modifiers.HasFlag(VirtualKeyModifiers.Control), alt = modifiers.HasFlag(VirtualKeyModifiers.Menu), shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (alt && !ctrl)
        {
            if (mask)
            {
                // Alt-drag a mask thumbnail carries a copy of the mask to another row.
                pressedOnRow = true;
                grid.CapturePointer(e.Pointer);
                panel.BeginRowPress(LayerId, panel.ToList(e), maskThumbnail: true);
                e.Handled = true;
            }
            return;
        }
        if (ctrl)
        {
            var mode = alt ? SelectionMode.Subtract : shift ? SelectionMode.Add : SelectionMode.Replace;
            if (mask) s.LoadMaskSelection(LayerId, mode); else s.LoadLayerSelection(LayerId, mode);
            e.Handled = true;
            return;
        }
        if (mask && shift)
        {
            s.SelectLayerTarget(LayerId, true);
            s.ToggleLayerMask();
            e.Handled = true;
            return;
        }
        if (!shift) s.SelectLayerTarget(LayerId, mask);
        // The row still starts a drag from here, but must not retarget the layer's pixels.
        thumbnailClicked = true;
    }

    private bool thumbnailClicked;

    private void RowPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Session is not { } s || renaming) return;
        var point = e.GetCurrentPoint(grid);
        if (point.Properties.IsRightButtonPressed) return;
        var modifiers = e.KeyModifiers;
        bool ctrl = modifiers.HasFlag(VirtualKeyModifiers.Control), alt = modifiers.HasFlag(VirtualKeyModifiers.Menu), shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        panel.FocusList();
        // Alt-click on the bottom quarter of a row makes or releases a clipping mask.
        if (alt && !ctrl && point.Position.Y >= grid.ActualHeight * 0.75)
        {
            s.ToggleClippingMask(LayerId);
            e.Handled = true;
            return;
        }
        if (thumbnailClicked) { thumbnailClicked = false; }
        else if (ctrl && !e.Handled)
        {
            var set = s.SelectedLayerIds.ToHashSet();
            if (!set.Add(LayerId)) set.Remove(LayerId);
            s.SelectLayers(set, set.Contains(LayerId) ? LayerId : set.FirstOrDefault());
        }
        else if (shift && !e.Handled && s.ActiveLayerId is { } anchor)
        {
            var order = s.LayerRows.Select(r => r.Layer.Id).ToList();
            int a = order.IndexOf(anchor), b = order.IndexOf(LayerId);
            if (a >= 0 && b >= 0) s.SelectLayers(order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).ToHashSet(), anchor);
        }
        else if (!(s.SelectedLayerIds.Count > 1 && s.SelectedLayerIds.Contains(LayerId)))
        {
            // A click on the name targets the layer itself, even when its mask was selected.
            if (s.IsMaskSelected && s.SelectedLayerIds.SetEquals(new[] { LayerId }) && !e.Handled) { s.CommitTransform(); s.SelectLayerTarget(LayerId, false); }
            else if (!e.Handled) s.SelectLayer(LayerId);
        }
        pressedOnRow = true;
        grid.CapturePointer(e.Pointer);
        panel.BeginRowPress(LayerId, panel.ToList(e), maskThumbnail: false);
    }

    public void SetDropHighlight(bool on) => background.BorderBrush = on ? new SolidColorBrush(Colors.DodgerBlue) : null;

    public void Update(EditorSession s, ImageLayer layer, int depth, bool visible)
    {
        this.layer = layer;
        background.BorderThickness = new Thickness(2);
        bool selected = s.SelectedLayerIds.Contains(layer.Id);
        background.Background = selected ? new SolidColorBrush(WinColor.FromArgb(90, 10, 132, 255)) : null;
        indent.Width = new GridLength(Math.Min(depth, 8) * 24 + (layer.MaskSourceId == null ? 0 : 24));
        disclosure.Visibility = layer.IsGroup ? Visibility.Visible : Visibility.Collapsed;
        disclosure.Content = Icons.Glyph(s.CollapsedGroupIds.Contains(layer.Id) ? Icons.ChevronRight : Icons.ChevronDown, 10);
        disclosure.IsEnabled = s.CanEditLayers || true;
        eye.Content = Icons.Glyph(layer.IsVisible ? Icons.Eye : Icons.EyeOff, 13);
        eye.IsEnabled = s.CanEditLayers;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(eye, $"{(layer.IsVisible ? "Hide" : "Show")} {layer.Name}");
        var canvas = s.Document?.Size ?? new SizeD(1, 1);
        double scale = XamlRoot?.RasterizationScale ?? 2;
        bool framed = layer.Adjustment == null && !layer.IsGroup;
        var layerSize = framed ? Thumbnails.FittedSize(canvas, 36) : new SizeD(36, 36);
        thumbnail.Width = layerSize.Width;
        thumbnail.Height = layerSize.Height;
        object key = (layer.Asset?.Thumbnail, layer.Transform, canvas, layer.Adjustment?.Kind, layer.IsGroup);
        if (!Equals(key, thumbKey))
        {
            thumbKey = key;
            if (layer.Adjustment is { } adjustment) thumbnail.Child = new ContentControl { Content = Icons.Lucide(adjustment.Kind == Model.AdjustmentKind.GradientMap ? LucideIcons.Blend : LucideIcons.SlidersHorizontal, 22), Foreground = Ui.Brush("TextFillColorPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            else if (layer.IsGroup) thumbnail.Child = new FontIcon { Glyph = Icons.Folder, FontFamily = new FontFamily(Icons.FluentFont), FontSize = 26 };
            else thumbnail.Child = new Image { Source = Thumbnails.Layer(layer.Asset?.Thumbnail, layer.Transform, canvas, 36, scale), Stretch = Stretch.Fill };
        }
        var maskSize = Thumbnails.FittedSize(canvas, 30);
        maskThumbnail.Width = maskSize.Width;
        maskThumbnail.Height = maskSize.Height;
        object mKey = (layer.Mask?.Asset.Thumbnail, layer.MaskTransform, canvas);
        if (!Equals(mKey, maskKey))
        {
            maskKey = mKey;
            maskThumbnail.Child = layer.Mask is { } mask ? new Image { Source = Thumbnails.Mask(mask.Asset.Thumbnail, layer.MaskTransform, canvas, 30, scale), Stretch = Stretch.Fill } : null;
        }
        maskSlot.Visibility = layer.Mask == null ? Visibility.Collapsed : Visibility.Visible;
        disabledMark.Visibility = layer.Mask?.IsEnabled == false ? Visibility.Visible : Visibility.Collapsed;
        bool linkable = layer.Mask != null && framed;
        link.Visibility = linkable ? Visibility.Visible : Visibility.Collapsed;
        link.Opacity = layer.Mask?.IsLinked == false ? 0.05 : 1;
        ToolTipService.SetToolTip(link, layer.Mask?.IsLinked == false ? "Link layer and mask so they move together" : "Unlink layer and mask to move or transform them separately");
        maskSlot.Margin = new Thickness(linkable ? 0 : 5, 0, 0, 0);
        bool active = s.ActiveLayerId == layer.Id && s.SelectedLayerIds.Count == 1;
        thumbnail.BorderThickness = new Thickness(active && !s.IsMaskSelected ? 2 : 0);
        maskThumbnail.BorderThickness = new Thickness(active && s.IsMaskSelected ? 2 : 0);
        if (!renaming) name.Text = (layer.MaskSourceId == null ? "" : "↳ ") + layer.Name;
        if (layer.MaskSourceId is { } source)
        {
            var sourceName = s.Document?.Layer(source)?.Name ?? "Missing source";
            detail.Text = $"Clipped to {sourceName}";
            ToolTipService.SetToolTip(detail, $"Clipping mask based on {sourceName}. Alt-click the bottom of its row to release.");
        }
        else detail.Text = layer.Adjustment != null ? "Adjustment · Double-click to edit" : layer.IsGroup ? "Folder"
            : $"{(int)Math.Round(layer.Size.Width)} × {(int)Math.Round(layer.Size.Height)} px";
        Opacity = visible ? 1 : 0.35;
    }

    public void BeginRenaming(EditorSession s)
    {
        if (renaming || layer == null || s.Document == null || s.IsProjectBusy || s.IsImporting) return;
        renaming = true;
        name.Visibility = Visibility.Collapsed;
        renameBox.Visibility = Visibility.Visible;
        renameBox.Text = layer.Name;
        renameBox.Focus(FocusState.Programmatic);
        renameBox.SelectAll();
    }

    private void EndRenaming(bool keep)
    {
        if (!renaming || Session is not { } s) return;
        renaming = false;
        var typed = renameBox.Text;
        renameBox.Visibility = Visibility.Collapsed;
        name.Visibility = Visibility.Visible;
        if (s.RenamingLayerId == LayerId) s.RenamingLayerId = null;
        if (keep) s.RenameLayer(LayerId, typed);
        s.Notify();
        panel.FocusList();
    }
}
