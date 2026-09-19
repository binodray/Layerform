using Compositor.App.Controls;
using Compositor.App.Platform;
using Compositor.Editing;
using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

using SelectionMode = Compositor.Editing.SelectionMode;

namespace Compositor.App;

/// <summary>One shortcut-bearing menu command (CompositorApp.swift commands, with ⌘ → Ctrl and ⌥ → Alt).</summary>
internal sealed class MenuCommand
{
    public Func<string> Title = () => "";
    public VirtualKey? Key;
    public VirtualKeyModifiers Modifiers;
    public string? KeyText;
    public Func<bool> Enabled = () => true;
    public Func<bool>? Checked;
    public Action Run = () => { };
    /// <summary>In a text field this shortcut keeps its text-editing meaning.</summary>
    public bool TextKey;
    /// <summary>The name actions record and play back (the menu title when the command was made).</summary>
    public string Id = "";
    public bool Recordable = true;
    public MenuFlyoutItem? Item;
}

public sealed class MainWindow : Window
{
    private readonly WindowsHost host;
    private readonly ProjectWorkspace workspace;
    private EditorSession? attached;
    private EditorSession Session => workspace.Current.Session;

    private readonly Grid root = new();
    private readonly CanvasView canvasView = new();
    private readonly ToolRail rail;
    private readonly ToolOptions options;
    private readonly LayersPanel layers;
    private readonly NewCanvasView welcome;
    private readonly Grid canvasArea = new();
    private readonly Canvas panelHost = new();
    private readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer tabScroller;
    private readonly Grid tabHost = new();
    private readonly Button tabOverflow;
    private readonly Border dragRegion = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly TextBlock zoomText = Ui.Label("", 11, brush: null), sizeText = Ui.Label("", 11), colorText = Ui.Label("sRGB · Transparent", 11), hintText = Ui.Label("", 11);
    private readonly StackPanel busy = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly TextBlock busyText = Ui.Label("Working…", 11);
    private readonly Border dropOutline = new() { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed, Margin = new Thickness(3) };
    private readonly List<MenuCommand> commands = new();
    private readonly MenuBar menuBar = new();
    private readonly ColumnDefinition layersColumn = new() { Width = new GridLength(252) };
    private readonly Dictionary<string, (FloatingPanel Panel, PanelContent? Content, object? Key)> panels = new();
    private bool refreshQueued, closingConfirmed, showingDialog;
    private EditorSession? focusedDocumentSession;
    private CanvasDocument? lastDocument;
    private readonly Queue<(string Title, string Message)> errors = new();
    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
    private readonly List<DockPanel> docks = new();
    private readonly ActionsDock actionsDock;

    public MainWindow(string[] files)
    {
        host = new WindowsHost(() => Content?.XamlRoot);
        workspace = new ProjectWorkspace(host);
        Title = "Layer Form";
        ExtendsContentIntoTitleBar = true;
        rail = new ToolRail(() => attached);
        options = new ToolOptions(() => attached, () => ZoomBy(1.25), () => ZoomBy(1 / 1.25));
        layers = new LayersPanel(() => attached);
        welcome = new NewCanvasView((w, h, ppi) =>
        {
            Session.CreateNewProject(w, h);
            if (ppi != 72 && Session.Document is { } created) Session.Document = created with { Resolution = ppi };
            canvasView.FocusCanvas();
        }, () => _ = Open(), () => _ = OpenImagesFromPicker());
        tabScroller = new ScrollViewer
        {
            Content = tabStrip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
        };
        tabOverflow = Ui.IconButton(new TextBlock { Text = "•••", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            ShowTabOverflow, "More open projects", 32, 30);
        tabOverflow.Visibility = Visibility.Collapsed;
        tabHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabHost.Children.Add(tabScroller);
        Grid.SetColumn(tabOverflow, 1);
        tabHost.Children.Add(tabOverflow);
        tabHost.SizeChanged += (_, _) => QueueTabOverflowUpdate();
        tabStrip.SizeChanged += (_, _) => QueueTabOverflowUpdate();
        BackgroundModels.CleanPartialDownloads();
        BackgroundModels.Restore();
        actionsDock = new ActionsDock(() => attached, RunCommand);
        docks.AddRange(new DockPanel[] { new ColorDock(() => attached), new SwatchesDock(() => attached), new AlignDock(() => attached), new HistoryDock(() => attached), actionsDock });
        foreach (var dock in docks)
        {
            var d = dock;
            d.CloseRequested += () => SetDockVisible(d, false);
            d.Visibility = Settings.Get("panel." + d.Key, d.Key is "color" or "swatches" ? 1 : 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        BuildCommands();
        BuildLayout();
        SetTitleBar(dragRegion);
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Compositor.ico"));
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 800;
                presenter.PreferredMinimumHeight = 520;
                presenter.Maximize();
            }
        }
        catch (Exception e) { Diagnostics.Log("Window setup: " + e.Message); }
        AppWindow.Closing += OnClosing;

        workspace.Changed += () => { Attach(); QueueRefresh(); };
        rail.OpenColorPicker += background => attached?.OpenColorPicker(background);
        rail.ChooseMaskColor += background => _ = ChooseMaskColor(background);
        options.ReleaseFocus = () => canvasView.FocusCanvas();
        options.OpenColorPicker = background => attached?.OpenColorPicker(background);
        layers.ReleaseFocus = () => canvasView.FocusCanvas();
        layers.RunEffect = kind => _ = LayerEffect(kind);
        layers.IsOverTabStrip = point => TabAt(point, out _);
        layers.DroppedOutside = (id, point) =>
        {
            if (TabAt(point, out var tab)) _ = workspace.CopyLayer(id, tab);
        };
        canvasView.RequestFocusRestore += () => canvasView.FocusCanvas();
        canvasView.DropReceived += (data, point) => _ = ReceiveDrop(data, point);
        canvasView.BeforePaste = () => host.RefreshClipboardAsync();
        canvasView.ContextMenuRequested += ShowCanvasMenu;
        Attach();
        root.Loaded += async (_, _) =>
        {
            QueueRefresh();
            await SuggestClipboardSize();
            if (files.Length > 0) await ReceivePaths(files, null, null);
        };
    }

    // MARK: Layout (ContentView.swift)

    private void BuildLayout()
    {
        root.Background = Ui.Brush("WindowBackgroundBrush");
        root.RequestedTheme = ElementTheme.Dark;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        // Title bar: menus, New, the tab strip, then a generous drag area clear of the caption buttons.
        var titleBar = new Grid { Padding = new Thickness(8, 0, 146, 0), ColumnSpacing = 6 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(96) })
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(4, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(icon, "Layer Form");
        try { icon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon-64.png"))); } catch { }
        var left = Ui.Row(4, icon, menuBar);
        menuBar.VerticalAlignment = VerticalAlignment.Center;
        titleBar.Children.Add(left);
        var newButton = Ui.IconButton(Icons.Lucide(LucideIcons.Plus, 16), () => NewCanvas(), "New canvas (Ctrl+N)", 32, 30);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(newButton, "newCanvasToolbar");
        Grid.SetColumn(newButton, 1);
        titleBar.Children.Add(newButton);
        Grid.SetColumn(tabHost, 2);
        titleBar.Children.Add(tabHost);
        Grid.SetColumn(dragRegion, 3);
        titleBar.Children.Add(dragRegion);
        root.Children.Add(titleBar);

        // Tool header.
        var header = new StackPanel { Children = { options, new Border { Height = 1, Background = Ui.Divider } } };
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        // Tools | canvas | resize edge | layers.
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        main.ColumnDefinitions.Add(layersColumn);
        main.Children.Add(new ScrollViewer { Content = rail, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Width = 56 });
        var railDivider = new Border { Background = Ui.Divider };
        Grid.SetColumn(railDivider, 1);
        main.Children.Add(railDivider);
        canvasArea.Background = Ui.Brush("CanvasBackgroundBrush");
        canvasArea.Children.Add(canvasView);
        canvasArea.Children.Add(welcome);
        dropOutline.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        canvasArea.Children.Add(dropOutline);
        Grid.SetColumn(canvasArea, 2);
        main.Children.Add(canvasArea);
        var edge = new Grid { Background = Ui.Divider };
        var edgeHit = new Border { Width = 8, Margin = new Thickness(-4, 0, -4, 0), Background = new SolidColorBrush(Colors.Transparent) };
        edge.Children.Add(edgeHit);
        Grid.SetColumn(edge, 3);
        main.Children.Add(edge);
        MakeResizeEdge(edgeHit);
        // The right column: Window-menu panels stacked above Layers, as Photoshop docks them.
        var dockStack = new StackPanel();
        foreach (var dock in docks) dockStack.Children.Add(dock);
        var rightColumn = new Grid();
        rightColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var dockScroller = new ScrollViewer { Content = dockStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        rightColumn.SizeChanged += (_, e) => dockScroller.MaxHeight = Math.Max(120, e.NewSize.Height * 0.6);
        rightColumn.Children.Add(dockScroller);
        Grid.SetRow(layers, 1);
        rightColumn.Children.Add(layers);
        layers.Visibility = Settings.Get("panel.layers", 1) > 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(rightColumn, 4);
        main.Children.Add(rightColumn);

        // Double-clicking the empty workspace around the canvas offers to open files, as Photoshop does.
        canvasArea.DoubleTapped += (_, e) =>
        {
            if (attached is not { } s || showingDialog) return;
            if (s.Document is { } doc)
            {
                var p = e.GetPosition(canvasView);
                if (s.Viewport.DocumentRect(doc.Size).Contains(new PointD(p.X, p.Y))) return;
            }
            else if (welcome.IsOverCard(e.GetPosition(welcome))) return;
            ShowOpenMenu(e.GetPosition(canvasArea));
            e.Handled = true;
        };
        layersColumn.Width = new GridLength(Settings.Get("layersPanelWidth", 252.0));
        Grid.SetRow(main, 2);
        root.Children.Add(main);

        // Floating panels sit over the editor, above the tool header too, as the Mac's child windows can.
        Grid.SetRow(panelHost, 1);
        Grid.SetRowSpan(panelHost, 3);
        root.Children.Add(panelHost);

        var divider = new Border { Background = Ui.Divider };
        Grid.SetRow(divider, 3);
        root.Children.Add(divider);
        var status = new Grid { Padding = new Thickness(18, 0, 18, 0) };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        zoomText.Width = 62;
        var leftStatus = Ui.Row(16, zoomText, sizeText, colorText);
        leftStatus.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(leftStatus);
        busy.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12 });
        busy.Children.Add(busyText);
        var rightStatus = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { hintText, busy } };
        hintText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(rightStatus, 1);
        status.Children.Add(rightStatus);
        foreach (var text in new[] { zoomText, sizeText, colorText, hintText, busyText }) text.Foreground = Ui.Secondary;
        Grid.SetRow(status, 4);
        root.Children.Add(status);

        // Files dragged onto the editor (tool header, layers, anywhere) import into the current project.
        root.AllowDrop = true;
        root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) && CanReceiveFiles)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                dropOutline.Visibility = Visibility.Visible;
            }
        };
        root.DragLeave += (_, _) => dropOutline.Visibility = Visibility.Collapsed;
        root.Drop += (_, e) =>
        {
            dropOutline.Visibility = Visibility.Collapsed;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            PointD? point = null;
            if (Session.Document is { } doc)
            {
                var p = e.GetPosition(canvasView);
                if (p.X >= 0 && p.Y >= 0 && p.X < canvasView.ActualWidth && p.Y < canvasView.ActualHeight)
                    point = Session.Viewport.DocumentPoint(new PointD(p.X, p.Y), doc.Size);
            }
            _ = ReceiveDrop(e.DataView, point);
        };

        root.PreviewKeyDown += OnPreviewKeyDown;
        Content = root;
    }

    private void MakeResizeEdge(Border edge)
    {
        double? startWidth = null, startX = null;
        edge.PointerEntered += (_, _) => SetCursor(edge, InputSystemCursorShape.SizeWestEast);
        edge.PointerPressed += (_, e) =>
        {
            startWidth = layersColumn.Width.Value;
            startX = e.GetCurrentPoint(root).Position.X;
            edge.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        edge.PointerMoved += (_, e) =>
        {
            if (startWidth is not { } w || startX is not { } x) return;
            double width = Math.Clamp(w - (e.GetCurrentPoint(root).Position.X - x), LayersPanel.MinWidthValue, LayersPanel.MaxWidthValue);
            layersColumn.Width = new GridLength(width);
        };
        edge.PointerReleased += (_, e) =>
        {
            startWidth = null;
            edge.ReleasePointerCapture(e.Pointer);
            Settings.Set("layersPanelWidth", layersColumn.Width.Value);
        };
    }

    private static void SetCursor(UIElement element, InputSystemCursorShape shape)
    {
        try
        {
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(element, InputSystemCursor.Create(shape));
        }
        catch { }
    }

    // MARK: Sessions and refresh

    private void Attach()
    {
        var session = Session;
        if (attached == session) return;
        if (attached != null)
        {
            attached.Changed -= QueueRefresh;
            HideAllPanels();
        }
        attached = session;
        session.Host = host;
        session.Changed += QueueRefresh;
        canvasView.Session = session;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (refreshQueued) return;
        refreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            refreshQueued = false;
            try { Refresh(); }
            catch (Exception e) { Diagnostics.Log("Refresh: " + e); }
        });
    }

    private void Refresh()
    {
        var s = Session;
        if (attached != s) Attach();
        rail.Refresh();
        options.Refresh();
        layers.Refresh();
        canvasView.Refresh();
        RefreshTabs();
        bool hasDocument = s.Document != null;
        // A document appearing (created, opened, switched to) takes keyboard focus, as the Mac's canvasFocusRequest does.
        if (hasDocument && (lastDocument == null || focusedDocumentSession != s))
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => canvasView.FocusCanvas());
        lastDocument = s.Document;
        focusedDocumentSession = s;
        welcome.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        canvasView.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        if (s.Document is { } doc)
        {
            zoomText.Text = FormatPercent(s.Viewport.Zoom);
            sizeText.Text = $"{doc.Width} × {doc.Height} px";
            colorText.Visibility = Visibility.Visible;
        }
        else
        {
            zoomText.Text = "";
            sizeText.Text = "Ready when you are";
            colorText.Visibility = Visibility.Collapsed;
        }
        zoomText.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        bool working = s.IsProjectBusy || s.IsImporting || workspace.IsManaging;
        busy.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        busyText.Text = s.IsImporting ? "Importing images…" : "Working…";
        hintText.Visibility = working ? Visibility.Collapsed : Visibility.Visible;
        hintText.Text = Hint(s);
        Title = s.Document == null ? "Layer Form" : $"{s.Title} — Layer Form";
        foreach (var command in commands)
        {
            if (command.Item == null) continue;
            command.Item.Text = command.Title();
            command.Item.IsEnabled = SafeEnabled(command);
            if (command.Item is ToggleMenuFlyoutItem toggle && command.Checked != null) toggle.IsChecked = command.Checked();
        }
        SyncPanels(s);
        foreach (var dock in docks) if (dock.Visibility == Visibility.Visible) dock.Refresh();
        ShowErrors(s);
    }

    private static bool SafeEnabled(MenuCommand command)
    {
        try { return command.Enabled(); } catch { return false; }
    }

    private static string FormatPercent(double zoom)
    {
        double percent = zoom * 100;
        return Math.Abs(percent - Math.Round(percent)) < 0.05 ? $"{Math.Round(percent):0}%" : $"{percent:0.#}%";
    }

    private static string Hint(EditorSession s) => s.Tool switch
    {
        NavigationTool.Marquee => s.MarqueeKind == LassoKind.Ellipse
            ? "Drag an ellipse · Shift add · Alt subtract · Shift again mid-drag circle · Drag inside to move · Delete clears · Ctrl+D deselect"
            : "Drag a rectangle · Shift add · Alt subtract · Shift again mid-drag square · Drag inside to move · Ctrl-drag moves pixels · Delete clears · Ctrl+D deselect",
        NavigationTool.Wand => "Click to select similar colors · Shift add · Alt subtract · Drag inside to move · Ctrl-drag moves pixels · Delete clears · Ctrl+D deselect",
        NavigationTool.Lasso => s.LassoKind == LassoKind.Freehand
            ? "Drag to select · Drag inside to move · Shift add · Alt subtract · Delete clears · Alt+⌫/Ctrl+⌫ fill · Ctrl+D deselect"
            : "Click corners · Click start, double-click or Enter to close · Delete removes corner · Escape cancel",
        NavigationTool.Brush => (s.BrushMode == BrushToolMode.Erase ? "Drag to erase" : "Drag to paint") + " · [ ] size · Shift-[ ] hardness · 1–0 opacity · Escape cancel · Space to pan",
        NavigationTool.Blur => (s.BlurMode == BlurToolMode.Blur ? "Drag to soften" : s.BlurMode == BlurToolMode.Smudge ? "Drag to smudge" : "Drag to push pixels") + " · [ ] size · Shift-[ ] hardness · 1–0 strength · Space to pan",
        NavigationTool.CloneStamp => "Alt-click to set the source · Drag to clone · [ ] size · Shift-[ ] hardness · 1–0 opacity · Space to pan",
        NavigationTool.SpotHealing => "Drag over blemishes to heal · [ ] size · Shift-[ ] hardness · Escape cancel · Space to pan",
        NavigationTool.Shape => s.ShapeKind == ShapeKind.Line
            ? "Drag to draw a line on a new layer · Shift snaps to 45° · Alt from center · Shift-U next shape · Right-click the tool for all shapes · Escape cancel"
            : $"Drag to draw a {s.ShapeKind.Name().ToLowerInvariant()} on a new layer · Shift keeps proportions · Alt from center · Shift-U next shape · Right-click the tool for all shapes · Escape cancel",
        NavigationTool.Gradient => "Drag to draw · Drag ends to adjust · Shift 45° · 1–0 opacity · Enter apply · Escape cancel",
        NavigationTool.Crop => "Drag to crop · Enter apply · Escape cancel · Space to pan",
        NavigationTool.Move => "Drag to move · Handles to resize · Circle to rotate · 1–0 layer opacity · Space to pan",
        NavigationTool.Hand => "Drag to pan · Ctrl+wheel to zoom",
        NavigationTool.Idle => "No tool selected · Press a tool's key to pick one · Space to pan",
        NavigationTool.Eyedropper => "Click to pick the foreground color · Alt-click for the background · Space to pan",
        _ => "Click to zoom in · Alt-click to zoom out · Drag right or left to zoom smoothly · Space to pan",
    };

    private void ZoomBy(double factor)
    {
        if (attached is { Document: not null } s) s.Zoom(s.Viewport.Zoom * factor);
    }

    // MARK: Floating panels (Levels, Hue/Saturation, filters, color picker)

    private void SyncPanels(EditorSession s)
    {
        Sync("levelsPanel", s.Levels, "Levels", () => new LevelsPanel(s), () => s.CancelLevels());
        Sync("adjustmentPanel", s.HueSaturation, "Hue/Saturation", () => new HueSaturationPanel(s), () => s.CancelHueSaturation());
        Sync("filterPanel", s.FilterEdit, s.FilterEdit?.Kind.Name() ?? "Filter", () => new FilterPanel(s, highlights => s.OpenGradientMapColorPicker(highlights)), () => s.CancelFilter());
        Sync("colorPicker", s.ColorPicker, s.ColorPicker?.Target switch
        {
            ColorPickerTarget.Palette { Background: true } => "Background Color",
            ColorPickerTarget.Palette => "Foreground Color",
            ColorPickerTarget.GradientMapEnd { Highlights: true } => "Highlights Color",
            _ => "Shadows Color",
        }, () => new ColorPickerPanel(s, commit => s.CloseColorPicker(commit)), () => s.CloseColorPicker(false));
    }

    private void Sync(string name, object? edit, string title, Func<PanelContent> make, Action cancel)
    {
        if (!panels.TryGetValue(name, out var entry))
        {
            var panel = new FloatingPanel(name);
            entry = (panel, null, null);
            panels[name] = entry;
            panel.Closed += () => { if (panels[name].Key != null) cancel(); };
        }
        if (edit == null)
        {
            if (entry.Panel.IsShown) entry.Panel.Hide();
            panels[name] = (entry.Panel, null, null);
            return;
        }
        if (ReferenceEquals(entry.Key, edit) && entry.Content != null && entry.Panel.IsShown)
        {
            entry.Panel.SetTitle(title);
            entry.Content.Refresh();
            return;
        }
        var content = make();
        panels[name] = (entry.Panel, content, edit);
        var area = canvasArea.TransformToVisual(panelHost).TransformBounds(new Rect(0, 0, canvasArea.ActualWidth, canvasArea.ActualHeight));
        entry.Panel.Show(panelHost, title, content, area);
    }

    private void HideAllPanels()
    {
        foreach (var name in panels.Keys.ToList())
        {
            var entry = panels[name];
            entry.Panel.Hide();
            panels[name] = (entry.Panel, null, null);
        }
    }

    // MARK: Errors

    private void ShowErrors(EditorSession s)
    {
        if (s.ImportError is { } import) { s.ImportError = null; ShowError("Import couldn’t finish", import); }
        if (s.BrushError is { } brush) { s.BrushError = null; ShowError("Couldn’t paint", brush); }
        if (s.CropError is { } crop) { s.CropError = null; ShowError("Couldn’t crop", crop); }
    }

    public void ShowError(string title, string message)
    {
        errors.Enqueue((title, message));
        _ = DrainErrors();
    }

    private async Task DrainErrors()
    {
        if (showingDialog || Content?.XamlRoot == null) return;
        while (errors.Count > 0)
        {
            var (title, message) = errors.Dequeue();
            showingDialog = true;
            try { await Dialogs.Error(Content.XamlRoot, title, message); }
            catch (Exception e) { Diagnostics.Log("Error dialog: " + e.Message); }
            finally { showingDialog = false; }
        }
    }

    private async Task<T?> WithDialog<T>(Func<XamlRoot, Task<T?>> show)
    {
        if (showingDialog || Content?.XamlRoot is not { } xamlRoot) return default;
        showingDialog = true;
        try { return await show(xamlRoot); }
        finally { showingDialog = false; _ = DrainErrors(); }
    }

    // MARK: Tabs (ProjectTabStrip.swift)

    private List<(Guid Id, FrameworkElement Element)> tabElements = new();
    private string tabSignature = "";
    private bool tabOverflowUpdateQueued;

    private void RefreshTabs()
    {
        string signature = string.Join("|", workspace.Tabs.Select(t => $"{t.Id}:{t.Title}:{t.Session.IsModified}:{t.Id == workspace.SelectedId}:{t.Session.Document != null}"));
        if (signature == tabSignature) return;
        tabSignature = signature;
        tabStrip.Children.Clear();
        tabElements = new();
        foreach (var tab in workspace.Tabs)
        {
            bool selected = tab.Id == workspace.SelectedId;
            var id = tab.Id;
            var title = Ui.Label(tab.Title + (tab.Session.IsModified && tab.Session.Document != null ? " •" : ""), 12, selected);
            title.MaxWidth = 180;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            var close = Ui.IconButton(Icons.Glyph(Icons.Close, 8), () => _ = CloseTab(id), "Close project (Ctrl+W)", 20, 20);
            close.Opacity = selected ? 1 : 0.6;
            var body = Ui.Row(6, title, close);
            var pill = new Border
            {
                Child = body, Padding = new Thickness(6, 4, 12, 4), CornerRadius = new CornerRadius(15), Height = 30, MinWidth = 96,
                Background = selected ? Ui.Solid(46, 255, 255, 255) : Ui.Solid(14, 255, 255, 255),
                BorderBrush = selected ? Ui.Solid(36, 255, 255, 255) : null, BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(pill, tab.Session.ProjectPath ?? tab.Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pill, tab.Title);
            pill.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(pill).Properties.IsMiddleButtonPressed) { _ = CloseTab(id); return; }
                workspace.Select(id);
                canvasView.FocusCanvas();
            };
            tabStrip.Children.Add(pill);
            tabElements.Add((id, pill));
        }
        QueueTabOverflowUpdate();
        if (tabElements.FirstOrDefault(x => x.Id == workspace.SelectedId).Element is { } selectedTab)
            DispatcherQueue.TryEnqueue(() => selectedTab.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));
    }

    private void QueueTabOverflowUpdate()
    {
        if (tabOverflowUpdateQueued) return;
        tabOverflowUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            tabOverflowUpdateQueued = false;
            // When the button is visible its own width is part of the space the
            // strip could reclaim, so include that when deciding it can go away.
            double availableWithoutButton = tabScroller.ViewportWidth
                + (tabOverflow.Visibility == Visibility.Visible ? tabOverflow.ActualWidth : 0);
            bool overflowed = tabScroller.ExtentWidth > availableWithoutButton + 0.5;
            tabOverflow.Visibility = overflowed ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void ShowTabOverflow()
    {
        var menu = new MenuFlyout();
        double left = tabScroller.HorizontalOffset;
        double right = left + tabScroller.ViewportWidth;
        var hidden = tabElements.Where(entry =>
        {
            var bounds = entry.Element.TransformToVisual(tabStrip)
                .TransformBounds(new Rect(0, 0, entry.Element.ActualWidth, entry.Element.ActualHeight));
            return bounds.Left < left - 0.5 || bounds.Right > right + 0.5;
        }).Select(entry => entry.Id).ToHashSet();

        // During the first layout pass bounds can still be zero; listing every
        // project keeps the overflow menu useful in that brief state.
        var tabs = workspace.Tabs.Where(tab => hidden.Count == 0 || hidden.Contains(tab.Id));
        foreach (var tab in tabs)
        {
            var id = tab.Id;
            var item = new ToggleMenuFlyoutItem
            {
                Text = tab.Title + (tab.Session.IsModified && tab.Session.Document != null ? " •" : ""),
                IsChecked = id == workspace.SelectedId,
            };
            item.Click += (_, _) => { workspace.Select(id); canvasView.FocusCanvas(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(tabOverflow);
    }

    /// <summary>Hit-tests the tab strip for layer drags: a tab, or the strip's empty space (a new project).</summary>
    private bool TabAt(Point windowPoint, out Guid? tab)
    {
        tab = null;
        foreach (var (id, element) in tabElements)
        {
            var bounds = element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Contains(windowPoint)) { tab = id; return true; }
        }
        var strip = tabScroller.TransformToVisual(root).TransformBounds(new Rect(0, 0, tabScroller.ActualWidth + dragRegion.ActualWidth, 48));
        return strip.Contains(windowPoint);
    }

    // MARK: Commands and menus

    private MenuCommand Add(string title, Action run, Func<bool>? enabled = null, VirtualKey? key = null, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control,
        string? keyText = null, bool textKey = false) =>
        Add(() => title, run, enabled, key, modifiers, keyText, textKey);

    private MenuCommand Add(Func<string> title, Action run, Func<bool>? enabled = null, VirtualKey? key = null, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control,
        string? keyText = null, bool textKey = false)
    {
        var command = new MenuCommand { Title = title, Run = run, Enabled = enabled ?? (() => true), Key = key, Modifiers = modifiers, KeyText = keyText, TextKey = textKey };
        try { command.Id = title(); } catch { command.Id = ""; }
        commands.Add(command);
        return command;
    }

    private bool HasDocument => attached?.Document != null;
    private bool CanStart => attached is { } s && s.CanStartProjectOperation && !workspace.IsManaging && !showingDialog;
    private bool CanReceiveFiles => attached is { } s && s.Levels == null && !s.IsProjectBusy && s.RenamingLayerId == null && !showingDialog;

    private void BuildCommands()
    {
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control, Shift = VirtualKeyModifiers.Shift, Alt = VirtualKeyModifiers.Menu, None = VirtualKeyModifiers.None;
        var s = () => attached!;
        var backspace = VirtualKey.Back;
        VirtualKey bracketLeft = (VirtualKey)219, bracketRight = (VirtualKey)221, plus = (VirtualKey)187, minus = (VirtualKey)189;

        var file = Menu("File",
            Add("New Canvas…", NewCanvas, () => workspace.CanSwitch, VirtualKey.N),
            Add("Open Image…", () => _ = OpenImagesFromPicker(), () => workspace.CanSwitch, VirtualKey.O),
            Add("Open Project…", () => _ = Open(), () => workspace.CanSwitch, VirtualKey.O, Ctrl | Shift),
            Add("Import Images…", () => _ = ImportFromPicker(), () => attached is { } x && x.Levels == null && !x.IsProjectBusy && !x.IsImporting, VirtualKey.I, Ctrl | Shift | Alt),
            null,
            Add("Save", () => _ = Save(false), () => HasDocument && CanStart, VirtualKey.S),
            Add("Save As…", () => _ = Save(true), () => HasDocument && CanStart, VirtualKey.S, Ctrl | Shift),
            null,
            Add("Export PNG…", () => _ = ExportPng(), () => HasDocument && CanStart, VirtualKey.E, Ctrl | Shift),
            Add("Export JPEG…", () => _ = ExportJpeg(), () => HasDocument && CanStart, VirtualKey.S, Ctrl | Shift | Alt),
            null,
            Add("Close Project", () => _ = CloseTab(workspace.SelectedId), () => workspace.CanSwitch, VirtualKey.W),
            null,
            Add("Exit", () => Close(), null, VirtualKey.F4, Alt));

        var edit = Menu("Edit",
            Add(() => attached?.History.CanUndo == true ? $"Undo {attached.History.UndoName}" : "Undo", () => s().Undo(), () => attached?.CanUndo == true, VirtualKey.Z, textKey: true),
            Add(() => attached?.History.CanRedo == true ? $"Redo {attached.History.RedoName}" : "Redo", () => s().Redo(), () => attached?.CanRedo == true, VirtualKey.Z, Ctrl | Shift, textKey: true),
            null,
            Add("Cut", () => { if (s().Selection != null && s().CanCopyPixels) _ = s().CutSelection(); else host.Beep(); }, () => HasDocument, VirtualKey.X, textKey: true),
            Add("Copy", () => { if (s().CanCopyPixels) s().CopySelection(); else host.Beep(); }, () => HasDocument, VirtualKey.C, textKey: true),
            Add("Copy Merged", () => s().CopyMergedSelection(), () => attached?.CanCopyMerged == true, VirtualKey.C, Ctrl | Shift),
            Add("Paste", () => _ = Paste(), () => attached != null, VirtualKey.V, textKey: true),
            null,
            Add("Fill with Foreground Color", () => _ = s().FillSelection(false), () => attached?.CanEditPixels == true, backspace, Alt, "Alt+Backspace", textKey: true),
            Add("Fill with Background Color", () => _ = s().FillSelection(true), () => attached?.CanEditPixels == true, backspace, Ctrl, "Ctrl+Backspace", textKey: true),
            Add("Clear Selection Pixels", () => _ = s().ClearSelectedPixels(), () => attached is { } x && x.Selection != null && x.CanEditPixels, keyText: "Delete"),
            Add("Content-Aware Fill…", () => _ = s().BeginFilter(FilterKind.ContentAwareFill), () => attached?.CanContentAwareFill == true, backspace, Shift, "Shift+Backspace", textKey: true));

        var select = Menu("Select",
            Add("All", () => s().SelectAll(), () => HasDocument, VirtualKey.A, textKey: true),
            Add("Deselect", () => s().Deselect(), () => attached is { } x && x.Selection != null && x.CanEditSelection, VirtualKey.D),
            Add("Inverse", () => s().InvertSelection(), () => attached is { } x && x.Selection != null && x.CanEditSelection, VirtualKey.I, Ctrl | Shift),
            Add("Layer's Pixels", () => { if (s().ActiveLayerId is { } id) s().LoadLayerSelection(id); }, () => attached is { } x && x.ActiveLayer?.Asset != null && x.CanEditSelection),
            Add("Mask's Black Areas", () => { if (s().ActiveLayerId is { } id) s().LoadMaskSelection(id); }, () => attached is { } x && x.ActiveLayer?.Mask != null && x.CanEditSelection),
            null,
            Add(() => $"Expand by {attached?.SelectionExpandAmount ?? 1} px", () => s().ExpandSelection(s().SelectionExpandAmount), () => attached?.CanModifySelection == true),
            Add(() => $"Contract by {attached?.SelectionContractAmount ?? 1} px", () => s().ContractSelection(s().SelectionContractAmount), () => attached?.CanModifySelection == true));

        bool Colors() => attached is { } x && x.CanAdjustColors && x.HueSaturation == null;
        var image = Menu("Image",
            Add("Curves…", () => _ = s().BeginFilter(FilterKind.Curves), Colors, VirtualKey.M),
            Add("Levels…", () => s().BeginLevels(), Colors, VirtualKey.L),
            Add("Hue/Saturation…", () => s().BeginHueSaturation(), () => attached?.CanAdjustColors == true, VirtualKey.U),
            Add("Exposure…", () => _ = s().BeginFilter(FilterKind.Exposure), Colors),
            Add("Gradient Map…", () => _ = s().BeginFilter(FilterKind.GradientMap), Colors),
            Add("Grain…", () => _ = s().BeginFilter(FilterKind.Grain), Colors),
            Add(() => attached?.IsMaskSelected == true ? "Invert Mask" : "Invert", () => _ = s().InvertPixels(), () => attached?.CanInvert == true, VirtualKey.I),
            null,
            Add("Canvas Size…", () => _ = CanvasSize(), () => HasDocument && CanStart, VirtualKey.C, Ctrl | Alt),
            Add("Image Size…", () => _ = ImageSize(), () => HasDocument && CanStart, VirtualKey.I, Ctrl | Alt),
            null,
            Add("Flip Canvas Horizontal", () => s().FlipCanvas(true), () => attached?.CanEditLayers == true),
            Add("Flip Canvas Vertical", () => s().FlipCanvas(false), () => attached?.CanEditLayers == true));

        var filterItems = Enum.GetValues<FilterKind>().Where(k => k != FilterKind.ContentAwareFill && !k.IsImageAdjustment())
            .Select(k => (MenuCommand?)Add(k.Name() + "…", () => { if (k == FilterKind.RemoveBackground) _ = RemoveBackground(); else _ = s().BeginFilter(k); }, Colors)).ToArray();
        var models = Add("Background Removal Models…", () => _ = WithDialog(root => ModelsDialog.Show(root, firstUse: false)), () => !showingDialog);
        models.Recordable = false;
        var filter = Menu("Filter", filterItems.Append(null).Append(models).ToArray());

        var adjustmentItems = new MenuFlyoutSubItem { Text = "New Adjustment Layer" };
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
        {
            var command = Add(kind.ToName() + "…", () => s().AddAdjustment(kind), () => attached is { Document: not null } x && x.CanEditLayers);
            adjustmentItems.Items.Add(Item(command));
        }
        var layer = Menu("Layer",
            Add("Change Color…", () => _ = LayerEffect("color"), () => attached?.CanApplyLayerEffect == true),
            Add("Gradient Overlay…", () => _ = LayerEffect("gradient"), () => attached?.CanApplyLayerEffect == true),
            Add("Add Shadow…", () => _ = LayerEffect("shadow"), () => attached?.CanApplyLayerEffect == true),
            null,
            Add("Edit Adjustment…", () => { if (s().ActiveLayerId is { } id) s().EditAdjustment(id); }, () => attached is { } x && x.CanEditLayers && x.ActiveLayer?.Adjustment != null),
            null,
            Add(() => attached?.CanTransformSelection == true ? "Transform Selection" : "Transform Layer", () => _ = s().TransformCommand(),
                () => attached is { } x && (x.CanTransform || x.CanTransformSelection), VirtualKey.T),
            Add(() => attached?.Selection == null ? "Duplicate Layer" : "Layer via Copy", () => s().LayerViaCopy(),
                () => attached is { } x && (x.CanCopyPixels || (x.Selection == null && x.CanEditLayers && x.ActiveLayer is { IsGroup: false })), VirtualKey.J),
            null,
            Add(() => attached?.ActiveLayer?.MaskSourceId == null ? "Create Clipping Mask" : "Release Clipping Mask",
                () => { if (s().ActiveLayerId is { } id) s().ToggleClippingMask(id); }, () => attached?.ActiveLayerId is { } id && attached.CanToggleClippingMask(id), VirtualKey.G, Ctrl | Alt),
            null,
            Add("Group Selected Layers", () => s().GroupSelectedLayers(), () => attached?.CanEditLayers == true, VirtualKey.G),
            Add("Move Out of Folder", () => s().MoveActiveLayerOutOfGroup(), () => attached is { } x && x.CanEditLayers && x.ActiveLayer?.ParentId != null),
            Add("New Blank Layer", () => s().AddBlankLayer(), () => attached?.CanEditLayers == true, VirtualKey.N, Ctrl | Shift),
            Add("Rename Layer…", () => { s().RenamingLayerId = s().ActiveLayerId; s().Notify(); }, () => attached is { } x && x.CanEditLayers && x.ActiveLayer != null),
            Add(() => attached?.ActiveLayer?.IsVisible == false ? "Show Layer" : "Hide Layer", () => { if (s().ActiveLayerId is { } id) s().ToggleLayerVisibility(id); },
                () => attached is { } x && x.CanEditLayers && x.ActiveLayer != null),
            null,
            Add("Move Layer Up", () => s().MoveActiveLayer(1), () => attached?.CanMoveActiveLayer(1) == true, bracketRight, Ctrl, "Ctrl+]"),
            Add("Move Layer Down", () => s().MoveActiveLayer(-1), () => attached?.CanMoveActiveLayer(-1) == true, bracketLeft, Ctrl, "Ctrl+["),
            Add(() => attached?.MergeTitle ?? "Merge Down", () => s().MergeLayers(), () => attached?.CanMergeLayers == true, VirtualKey.E),
            null,
            Add("Flip Layer Horizontal", () => s().FlipLayers(true), () => attached?.CanTransform == true),
            Add("Flip Layer Vertical", () => s().FlipLayers(false), () => attached?.CanTransform == true),
            null,
            Add(() => attached is { IsMaskSelected: true, ActiveLayer.Mask: not null } ? "Delete Layer Mask" : attached?.SelectedLayerIds.Count > 1 ? "Delete Layers" : "Delete Layer",
                () => _ = s().DeleteLayerOrMask(), () => attached is { } x && x.CanEditLayers && x.ActiveLayer != null));
        layer.Items.Insert(0, adjustmentItems);

        // Layer › Align and Distribute (Photoshop's Layer › Align / Distribute).
        var align = new MenuFlyoutSubItem { Text = "Align", Icon = Icons.MenuIcon(Icons.LucideData(LucideIcons.AlignStartVertical)) };
        foreach (var (edge, title, icon) in new[]
        {
            (AlignEdge.Left, "Align Left Edges", Icons.LucideData(LucideIcons.AlignStartVertical)), (AlignEdge.HorizontalCenter, "Align Horizontal Centers", Icons.LucideData(LucideIcons.AlignCenterVertical)),
            (AlignEdge.Right, "Align Right Edges", Icons.LucideData(LucideIcons.AlignEndVertical)), (AlignEdge.Top, "Align Top Edges", Icons.LucideData(LucideIcons.AlignStartHorizontal)),
            (AlignEdge.VerticalCenter, "Align Vertical Centers", Icons.LucideData(LucideIcons.AlignCenterHorizontal)), (AlignEdge.Bottom, "Align Bottom Edges", Icons.LucideData(LucideIcons.AlignEndHorizontal)),
        })
        {
            var e = edge;
            var item = Item(Add(title, () => s().Align(e), () => attached?.CanAlign == true));
            item.Icon = Icons.MenuIcon(icon);
            align.Items.Add(item);
        }
        var distribute = new MenuFlyoutSubItem { Text = "Distribute", Icon = Icons.MenuIcon(Icons.LucideData(LucideIcons.AlignHorizontalDistributeCenter)) };
        distribute.Items.Add(Item(Add("Distribute Horizontal Centers", () => s().Distribute(DistributeAxis.Horizontal), () => attached?.CanDistribute == true)));
        distribute.Items.Add(Item(Add("Distribute Vertical Centers", () => s().Distribute(DistributeAxis.Vertical), () => attached?.CanDistribute == true)));
        int alignAt = layer.Items.IndexOf(layer.Items.OfType<MenuFlyoutItem>().First(i => i.Text.StartsWith("Group Selected")));
        layer.Items.Insert(alignAt, new MenuFlyoutSeparator());
        layer.Items.Insert(alignAt, distribute);
        layer.Items.Insert(alignAt, align);

        var pixelGrid = Add("Pixel Grid (800% and above)", () => { s().ShowsPixelGrid = !s().ShowsPixelGrid; s().InvalidateCanvas(); }, () => attached != null);
        pixelGrid.Checked = () => attached?.ShowsPixelGrid == true;
        var transformControls = Add("Show Transform Controls", () => { s().ShowsTransformControls = !s().ShowsTransformControls; s().InvalidateCanvas(); },
            () => attached is { Tool: NavigationTool.Move, Document: not null }, VirtualKey.H);
        transformControls.Checked = () => attached?.ShowsTransformControls == true;
        var view = Menu("View",
            Add("Fit Canvas", () => s().Fit(), () => HasDocument, VirtualKey.Number0),
            Add("Actual Pixels", () => s().Zoom(1), () => HasDocument, VirtualKey.Number1),
            Add("Zoom In", () => ZoomBy(1.25), () => HasDocument, plus, Ctrl, "Ctrl++"),
            Add("Zoom Out", () => ZoomBy(1 / 1.25), () => HasDocument, minus, Ctrl, "Ctrl+−"),
            null, pixelGrid, transformControls);

        var help = Menu("Help",
            Add("Report a Bug…", () => ProjectLinks.Open(ProjectLinks.BugReport)),
            Add("Request a Feature…", () => ProjectLinks.Open(ProjectLinks.FeatureRequest)),
            null,
            Add("About Layer Form", () => _ = WithDialog(AboutDialog.Show), () => !showingDialog));

        // Aliases Windows users expect, not shown in the menus.
        Add("Redo", () => s().Redo(), () => attached?.CanRedo == true, VirtualKey.Y, textKey: true);
        Add("Zoom In", () => ZoomBy(1.25), () => HasDocument, VirtualKey.Add, Ctrl);
        Add("Zoom Out", () => ZoomBy(1 / 1.25), () => HasDocument, VirtualKey.Subtract, Ctrl);
        Add("Fit Canvas", () => s().Fit(), () => HasDocument, VirtualKey.NumberPad0, Ctrl);
        Add("Actual Pixels", () => s().Zoom(1), () => HasDocument, VirtualKey.NumberPad1, Ctrl);
        Add("Content-Aware Fill…", () => _ = s().BeginFilter(FilterKind.ContentAwareFill), () => attached?.CanContentAwareFill == true, VirtualKey.Delete, Shift, textKey: true);
        _ = None;

        // Window: the docked panels, as Photoshop's Window menu shows and hides its panels.
        var window = new MenuBarItem { Title = "Window" };
        foreach (var dock in docks)
        {
            var d = dock;
            var command = Add(d.Title, () => ToggleDock(d), () => true);
            command.Recordable = false;
            command.Checked = () => d.Visibility == Visibility.Visible;
            var item = Item(command);
            item.Icon = Icons.MenuIcon(d.Key switch { "color" => Icons.LucideData(LucideIcons.Palette), "swatches" => Icons.LucideData(LucideIcons.SwatchBook), "align" => Icons.LucideData(LucideIcons.AlignStartVertical), "history" => Icons.LucideData(LucideIcons.History), _ => Icons.LucideData(LucideIcons.Zap) });
            window.Items.Add(item);
        }
        var layersToggle = Add("Layers", () => { layers.Visibility = layers.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; Settings.Set("panel.layers", layers.Visibility == Visibility.Visible ? 1 : 0); }, () => true);
        layersToggle.Recordable = false;
        layersToggle.Checked = () => layers.Visibility == Visibility.Visible;
        var layersItem = Item(layersToggle);
        layersItem.Icon = Icons.MenuIcon(Icons.LucideData(LucideIcons.Layers));
        window.Items.Add(layersItem);
        window.Items.Add(new MenuFlyoutSeparator());
        var reset = Add("Reset Panels", () =>
        {
            foreach (var d in docks) SetDockVisible(d, d.Key is "color" or "swatches");
            layers.Visibility = Visibility.Visible;
            Settings.Set("panel.layers", 1);
        }, () => true);
        reset.Recordable = false;
        window.Items.Add(Item(reset));

        // File, Edit's history and View commands aren't recorded into actions.
        foreach (var command in commands)
            if (command.Id.StartsWith("Undo") || command.Id.StartsWith("Redo") || command.Id is "Fit Canvas" or "Actual Pixels" or "Zoom In" or "Zoom Out"
                || command.Id is "Pixel Grid (800% and above)" or "Show Transform Controls" or "About Layer Form")
                command.Recordable = false;
        foreach (var item in file.Items.Concat(help.Items).OfType<MenuFlyoutItem>())
            if (commands.FirstOrDefault(c => c.Item == item) is { } fileCommand) fileCommand.Recordable = false;

        foreach (var menu in new[] { file, edit, select, image, filter, layer, view, window, help }) menuBar.Items.Add(menu);
    }

    /// <summary>Photoshop's canvas context menu: with a selection, what you can do to those pixels; otherwise layer commands.</summary>
    private void ShowCanvasMenu(Point at)
    {
        if (attached is not { Document: not null } s) return;
        var menu = new MenuFlyout();
        void Entry(string id, string? label = null, string? icon = null)
        {
            if (commands.FirstOrDefault(c => c.Id == id) is not { } command) return;
            var item = new MenuFlyoutItem
            {
                Text = label ?? command.Title(), IsEnabled = SafeEnabled(command),
                KeyboardAcceleratorTextOverride = command.KeyText ?? ShortcutText(command),
            };
            if (icon != null) item.Icon = Icons.MenuIcon(icon);
            item.Click += (_, _) => Execute(command);
            menu.Items.Add(item);
        }
        void Separator() { if (menu.Items.Count > 0 && menu.Items[^1] is not MenuFlyoutSeparator) menu.Items.Add(new MenuFlyoutSeparator()); }
        if (s.Selection is { IsEmpty: false })
        {
            Entry("Cut"); Entry("Copy"); Entry("Copy Merged"); Entry("Paste");
            Separator();
            Entry("Clear Selection Pixels", "Delete Selected Pixels", Icons.LucideData(LucideIcons.Trash2));
            Entry("Fill with Foreground Color", icon: Icons.LucideData(LucideIcons.PaintBucket));
            Entry("Fill with Background Color");
            Entry("Hue/Saturation…", "Change Color (Hue/Saturation)…", Icons.LucideData(LucideIcons.Palette));
            Entry("Invert");
            Separator();
            Entry("Duplicate Layer", "Layer via Copy");
            Entry("Transform Layer", "Transform Selection");
            Separator();
            Entry("Deselect"); Entry("Inverse");
            Entry("Expand by 1 px", $"Expand by {s.SelectionExpandAmount} px");
            Entry("Contract by 1 px", $"Contract by {s.SelectionContractAmount} px");
        }
        else
        {
            Entry("Paste"); Entry("All", "Select All");
            Separator();
            Entry("Duplicate Layer"); Entry("Transform Layer");
            Entry("Align Horizontal Centers", "Center Horizontally on Canvas", Icons.LucideData(LucideIcons.AlignCenterVertical));
            Entry("Align Vertical Centers", "Center Vertically on Canvas", Icons.LucideData(LucideIcons.AlignCenterHorizontal));
            Separator();
            Entry("Change Color…", icon: Icons.LucideData(LucideIcons.Palette)); Entry("Gradient Overlay…", icon: Icons.LucideData(LucideIcons.Blend)); Entry("Add Shadow…");
            Separator();
            Entry("New Blank Layer"); Entry("Delete Layer");
        }
        if (menu.Items.Count > 0 && menu.Items[^1] is MenuFlyoutSeparator last) menu.Items.Remove(last);
        menu.ShowAt(canvasView, new FlyoutShowOptions { Position = at });
    }

    /// <summary>Change Color, Gradient Overlay or Add Shadow on the active layer, each through its preview dialog.</summary>
    private async Task LayerEffect(string kind)
    {
        if (attached is not { CanApplyLayerEffect: true } s || s.ActiveLayer is not { Asset: { } asset } layer)
        {
            ShowError("Choose an image layer", "Select a layer with pixels (not a folder or adjustment layer) in the Layers panel, then try again.");
            return;
        }
        switch (kind)
        {
            case "color":
                var recolor = await WithDialog<(PaletteColor Color, RecolorMode Mode)?>(root => EffectDialogs.ChangeColor(root, asset.Image, s.ForegroundColor));
                if (recolor is { } r) s.RecolorLayer(r.Color, r.Mode);
                break;
            case "gradient":
                var gradient = await WithDialog(root => EffectDialogs.Gradient(root, asset.Image, s.ForegroundColor, s.BackgroundColor));
                if (gradient != null) s.GradientOverlayLayer(gradient);
                break;
            case "shadow":
                double scale = layer.Transform.Size.Width / Math.Max(1, asset.Image.Width);
                var shadow = await WithDialog(root => EffectDialogs.Shadow(root, asset.Image, scale));
                if (shadow != null) s.AddShadow(shadow);
                break;
        }
    }

    /// <summary>Remove Background: the first time, ask which model to download (with sizes and a recommendation).</summary>
    private async Task RemoveBackground()
    {
        if (attached is not { } s) return;
        if (!BackgroundModels.AnyInstalled)
        {
            bool ready = await WithDialog(root => ModelsDialog.Show(root, firstUse: true));
            if (!ready || attached != s) return;
        }
        else if (SubjectRemoval.Segmenter == null && BackgroundModels.State.Active is { } active) BackgroundModels.Activate(active);
        await s.BeginFilter(FilterKind.RemoveBackground);
    }

    private void ShowOpenMenu(Point at)
    {
        var menu = new MenuFlyout();
        void Entry(string text, string icon, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = Icons.MenuIcon(icon) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Entry("Open Image…", Icons.LucideData(LucideIcons.ImagePlus), () => _ = OpenImagesFromPicker());
        Entry("Open Project…", Icons.LucideData(LucideIcons.FolderOpen), () => _ = Open());
        Entry("Import Images…", Icons.LucideData(LucideIcons.ImagePlus), () => _ = ImportFromPicker());
        Entry("New Canvas…", Icons.LucideData(LucideIcons.FilePlus), NewCanvas);
        menu.ShowAt(canvasArea, new FlyoutShowOptions { Position = at });
    }

    // MARK: Docked panels (Window menu)

    private void ToggleDock(DockPanel dock) => SetDockVisible(dock, dock.Visibility != Visibility.Visible);

    private void SetDockVisible(DockPanel dock, bool visible)
    {
        dock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Settings.Set("panel." + dock.Key, visible ? 1 : 0);
        if (visible) dock.Refresh();
        QueueRefresh();
    }

    /// <summary>Runs a menu command by the name an action recorded; returns why it couldn't, or null.</summary>
    private string? RunCommand(string id)
    {
        var command = commands.FirstOrDefault(c => c.Id == id && c.Recordable);
        if (command == null) return "This command isn't available in this version.";
        if (!SafeEnabled(command)) return "It isn't available right now (check the selected layer or selection).";
        try { command.Run(); }
        catch (Exception e) { return e.Message; }
        QueueRefresh();
        return null;
    }

    private MenuBarItem Menu(string title, params MenuCommand?[] items)
    {
        var menu = new MenuBarItem { Title = title };
        foreach (var command in items) menu.Items.Add(command == null ? new MenuFlyoutSeparator() : Item(command));
        return menu;
    }

    private MenuFlyoutItem Item(MenuCommand command)
    {
        MenuFlyoutItem item = command.Checked != null ? new ToggleMenuFlyoutItem() : new MenuFlyoutItem();
        item.Text = command.Title();
        item.KeyboardAcceleratorTextOverride = command.KeyText ?? ShortcutText(command);
        item.Click += (_, _) => Execute(command);
        command.Item = item;
        return item;
    }

    private static string ShortcutText(MenuCommand command)
    {
        if (command.Key is not { } key) return "";
        var parts = new List<string>();
        if (command.Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (command.Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (command.Modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(key - VirtualKey.Number0)).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    private void Execute(MenuCommand command)
    {
        if (!SafeEnabled(command)) { host.Beep(); return; }
        try
        {
            command.Run();
            if (command.Recordable && command.Id.Length > 0) actionsDock.Record(command.Id);
        }
        catch (Exception e) { Diagnostics.Log("Command: " + e); ShowError("Something went wrong", e.Message); }
        QueueRefresh();
    }

    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (showingDialog) return;
        var modifiers = VirtualKeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (Down(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (Down(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        // Delete (or Backspace) with a selection clears the selected pixels wherever the focus is — the canvas, the tool
        // bar or the Layers panel — except while typing.
        if (modifiers == VirtualKeyModifiers.None && e.Key is VirtualKey.Delete or VirtualKey.Back && attached is { Selection: { IsEmpty: false } } sel
            && FocusManager.GetFocusedElement(Content.XamlRoot) is not (TextBox or PasswordBox or RichEditBox or NumberBox)
            && sel.LassoDraft == null && sel.ShapeDraft == null)
        {
            e.Handled = true;
            if (sel.CanEditPixels) { _ = sel.ClearSelectedPixels(); if (commands.FirstOrDefault(c => c.Id == "Clear Selection Pixels") is { } clear) actionsDock.Record(clear.Id); }
            else host.Beep();
            return;
        }
        bool inText = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or PasswordBox or RichEditBox or NumberBox;
        // Tool keys should work from every non-text part of the window, not only
        // while the canvas or Layers list happens to own keyboard focus.
        if (!inText && modifiers is VirtualKeyModifiers.None or VirtualKeyModifiers.Shift
            && attached is { } toolSession && CanvasView.HandleToolKey(toolSession, e.Key, modifiers == VirtualKeyModifiers.Shift))
        {
            e.Handled = true;
            canvasView.FocusCanvas();
            return;
        }
        if (modifiers == VirtualKeyModifiers.None || modifiers == VirtualKeyModifiers.Shift && e.Key != VirtualKey.Back && e.Key != VirtualKey.Delete) return;
        var command = commands.FirstOrDefault(c => c.Key == e.Key && c.Modifiers == modifiers);
        if (command == null) return;
        if (inText && command.TextKey) return;
        e.Handled = true;
        if (inText) canvasView.FocusCanvas();
        Execute(command);
    }

    // MARK: Projects (ProjectController.swift / ProjectWorkspace.swift)

    private bool Begin(EditorSession s)
    {
        if (!s.CanStartProjectOperation || showingDialog) return false;
        s.CancelCrop();
        s.CommitTransform();
        s.IsProjectBusy = true;
        s.Notify();
        return true;
    }

    private void End(EditorSession s)
    {
        s.IsProjectBusy = false;
        s.Notify();
    }

    private void NewCanvas()
    {
        if (!workspace.CanSwitch) return;
        Session.CommitTransform();
        workspace.AddTab(reuseEmpty: false);
        welcome.FocusWidth();
        _ = SuggestClipboardSize();
    }

    /// <summary>The new-canvas view proposes the clipboard image's size, as the Mac's does.</summary>
    private async Task SuggestClipboardSize()
    {
        try
        {
            await host.RefreshClipboardAsync();
            if (host.GetClipboardImage() is { } image && image.Width <= 30_000 && image.Height <= 30_000) welcome.Suggest(image.Width, image.Height);
        }
        catch { }
    }

    private async Task<bool> Open(string? supplied = null)
    {
        if (!workspace.CanSwitch) return false;
        string? path = supplied ?? FileDialogs.PickProjectManifest(Hwnd);
        if (path == null) return false;
        workspace.IsManaging = true;
        QueueRefresh();
        try
        {
            string package = ProjectStore.ResolvePackage(path);
            if (workspace.TabForPath(package) is { } existing) { workspace.SelectForce(existing.Id); return true; }
            var snapshot = await Task.Run(() => ProjectStore.Load(package));
            workspace.AddLoaded(snapshot, package);
            canvasView.FocusCanvas();
            return true;
        }
        catch (Exception e)
        {
            ShowError("Couldn’t open the project", e.Message);
            return false;
        }
        finally
        {
            workspace.IsManaging = false;
            QueueRefresh();
        }
    }

    private async Task<bool> Save(bool asNew)
    {
        var s = Session;
        if (s.Document == null || !Begin(s)) return false;
        try { return await SaveCurrent(s, asNew); }
        finally { End(s); }
    }

    private async Task<bool> SaveCurrent(EditorSession s, bool asNew)
    {
        if (s.ProjectSnapshot() is not { } snapshot) return true;
        string? destination = asNew ? null : s.ProjectPath;
        if (destination == null)
        {
            var name = (s.ProjectPath != null ? Path.GetFileName(s.ProjectPath.TrimEnd('\\', '/')) : s.Title + ".lform");
            // Save As also offers flattened PNG and JPEG copies; the project itself stays as it is.
            string filter = "Layer Form project (*.lform)|*.lform|Compositor project for the Mac app (*.comp)|*.comp"
                + (asNew ? "|PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg" : "");
            destination = FileDialogs.SavePath(Hwnd, asNew ? "Save As" : "Save Project", filter, "lform", name);
            if (destination == null) return false;
            var extension = Path.GetExtension(destination).ToLowerInvariant();
            if (extension is ".png" or ".jpg" or ".jpeg")
            {
                if (File.Exists(destination) && await WithDialog<bool?>(async root => await Dialogs.ConfirmReplace(root, Path.GetFileName(destination))) != true) return false;
                try
                {
                    await Task.Run(() =>
                    {
                        if (extension == ".png") ImageExporter.ExportPng(snapshot, destination);
                        else ImageExporter.WriteAtomically(ImageExporter.Jpeg(ImageExporter.Render(snapshot), 0.9, PaletteColor.White), destination);
                    });
                }
                catch (Exception e) { ShowError(extension == ".png" ? "Couldn’t save the PNG" : "Couldn’t save the JPEG", e.Message); }
                return false;
            }
        }
        try
        {
            await Task.Run(() => ProjectStore.Save(snapshot, destination));
            s.ProjectPath = destination;
            s.History.MarkSaved();
            s.Notify();
            return true;
        }
        catch (Exception e)
        {
            ShowError("Couldn’t save the project", e.Message);
            return false;
        }
    }

    private string ExportName(EditorSession s, string extension) =>
        (s.ProjectPath != null ? Path.GetFileNameWithoutExtension(s.ProjectPath.TrimEnd('\\', '/')) : "Untitled") + extension;

    private async Task ExportPng()
    {
        var s = Session;
        if (s.Document == null || !Begin(s)) return;
        try
        {
            if (s.ProjectSnapshot() is not { } snapshot) return;
            var path = FileDialogs.SavePath(Hwnd, "Export PNG", "PNG image (*.png)|*.png", "png", ExportName(s, ".png"));
            if (path == null) return;
            await Task.Run(() => ImageExporter.ExportPng(snapshot, path));
        }
        catch (Exception e) { ShowError("Couldn’t export PNG", e.Message); }
        finally { End(s); }
    }

    private async Task ExportJpeg()
    {
        var s = Session;
        if (s.Document == null || !Begin(s)) return;
        try
        {
            if (s.ProjectSnapshot() is not { } snapshot) return;
            var raster = await Task.Run(() => ImageExporter.Render(snapshot));
            var data = await WithDialog(root => Dialogs.JpegExport(root, raster));
            if (data == null) return;
            var path = FileDialogs.SavePath(Hwnd, "Export JPEG", "JPEG image (*.jpg)|*.jpg;*.jpeg", "jpg", ExportName(s, ".jpg"));
            if (path == null) return;
            await Task.Run(() => ImageExporter.WriteAtomically(data, path));
        }
        catch (Exception e) { ShowError("Couldn’t export JPEG", e.Message); }
        finally { End(s); }
    }

    private async Task CanvasSize()
    {
        var s = Session;
        if (s.Document is not { } document || !Begin(s)) return;
        CanvasSizeOptions? options;
        try { options = await WithDialog(root => Dialogs.CanvasSize(root, document, s.ForegroundColor, s.BackgroundColor)); }
        finally { End(s); }
        if (options == null) return;
        try { await s.ResizeCanvas(options); }
        catch (Exception e) { ShowError("Couldn’t change canvas size", e.Message); }
    }

    private async Task ImageSize()
    {
        var s = Session;
        if (s.Document is not { } document || !Begin(s)) return;
        ImageSizeOptions? options;
        try { options = await WithDialog(root => Dialogs.ImageSize(root, document)); }
        finally { End(s); }
        if (options == null) return;
        try { await s.ResizeImage(options); }
        catch (Exception e) { ShowError("Couldn’t resize the image", e.Message); }
    }

    /// <summary>Save / Don’t Save / Cancel for an unsaved project. True means go ahead.</summary>
    private async Task<bool> ConfirmReplacement(EditorSession s)
    {
        if (!s.IsModified || s.Document == null) return true;
        var answer = await WithDialog<ContentDialogResult?>(async root => await Dialogs.SaveChanges(root, s.ProjectPath != null ? Path.GetFileName(s.ProjectPath.TrimEnd('\\')) : s.Title));
        if (answer == ContentDialogResult.Primary) return await SaveCurrent(s, false);
        return answer == ContentDialogResult.Secondary;
    }

    private async Task CloseTab(Guid id)
    {
        if (!workspace.CanSwitch || workspace.Tabs.FirstOrDefault(t => t.Id == id) is not { } tab) return;
        workspace.IsManaging = true;
        try
        {
            if (workspace.SelectedId != id) workspace.SelectForce(id);
            var s = tab.Session;
            s.CommitTransform();
            if (!await ConfirmReplacement(s)) return;
            workspace.RemoveTab(id);
        }
        finally
        {
            workspace.IsManaging = false;
            QueueRefresh();
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (closingConfirmed) return;
        e.Cancel = true;
        if (!workspace.CanSwitch || showingDialog) { host.Beep(); return; }
        workspace.IsManaging = true;
        try
        {
            foreach (var tab in workspace.QuitOrder.ToList())
            {
                workspace.SelectForce(tab.Id);
                tab.Session.CommitTransform();
                if (!await ConfirmReplacement(tab.Session)) return;
            }
        }
        finally { workspace.IsManaging = false; QueueRefresh(); }
        closingConfirmed = true;
        Close();
    }

    // MARK: Import, drops, paste

    /// <summary>Opens each chosen image as a standalone document in its own tab.</summary>
    private async Task OpenImagesFromPicker()
    {
        if (!workspace.CanSwitch) return;
        var files = FileDialogs.PickImages(Hwnd, "Open Images");
        if (files.Length == 0) return;
        workspace.IsManaging = true;
        QueueRefresh();
        var failures = new List<string>();
        try
        {
            foreach (var file in files)
            {
                try
                {
                    var asset = await ImageImport.DecodeAsync(file, 100_000_000);
                    var target = workspace.AddTab();
                    target.Session.DefaultName = Path.GetFileNameWithoutExtension(file);
                    target.Session.CreateDocument(asset.Image.Width, asset.Image.Height);
                    target.Session.ImportAssets(new[] { asset });
                    target.Session.Notify();
                }
                catch (Exception e) { failures.Add($"{Path.GetFileName(file)}: {e.Message}"); }
            }
        }
        finally
        {
            workspace.IsManaging = false;
            QueueRefresh();
        }
        if (failures.Count > 0) ShowError("Some images couldn’t be opened", string.Join("\n", failures));
        canvasView.FocusCanvas();
    }

    private async Task ImportFromPicker()
    {
        var s = Session;
        if (s.Levels != null || s.IsProjectBusy || s.IsImporting) return;
        var files = FileDialogs.PickImages(Hwnd);
        if (files.Length == 0) return;
        await ImportImages(s, files, null);
    }

    private async Task ImportImages(EditorSession s, IReadOnlyList<string> files, PointD? point)
    {
        if (files.Count == 0 || s.IsImporting) return;
        s.IsImporting = true;
        s.Notify();
        var assets = new List<ImageAsset>();
        var failures = new List<string>();
        try
        {
            long remaining = 100_000_000 - s.UsedImagePixels;
            foreach (var file in files)
            {
                try
                {
                    var asset = await ImageImport.DecodeAsync(file, remaining);
                    remaining -= (long)asset.Image.Width * asset.Image.Height;
                    assets.Add(asset);
                }
                catch (ImageImportException e) { failures.Add($"{Path.GetFileName(file)}: {e.Message}"); }
                catch (Exception e) { failures.Add($"{Path.GetFileName(file)}: {e.Message}"); }
            }
        }
        finally
        {
            s.IsImporting = false;
        }
        if (assets.Count > 0)
        {
            if (s.Document == null) s.CreateDocument(assets[0].Image.Width, assets[0].Image.Height);
            s.ImportAssets(assets, null, fitToCanvas: true);
            canvasView.FocusCanvas();
        }
        if (failures.Count > 0) s.ImportError = string.Join("\n", failures);
        s.Notify();
    }

    private async Task ReceiveDrop(DataPackageView data, PointD? point)
    {
        if (!CanReceiveFiles) return;
        try
        {
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                await ReceivePaths(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray(), point, workspace.SelectedId);
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                var stream = await data.GetBitmapAsync();
                using var read = await stream.OpenReadAsync();
                var bytes = new byte[read.Size];
                using (var reader = new Windows.Storage.Streams.DataReader(read)) { await reader.LoadAsync((uint)read.Size); reader.ReadBytes(bytes); }
                var image = await ImageImport.DecodeBytesAsync(bytes);
                var s = Session;
                if (s.Document == null) s.CreateDocument(image.Width, image.Height);
                s.ImportAssets(new[] { PixelOps.Asset(image, "Dropped image") }, null, fitToCanvas: true);
            }
        }
        catch (Exception e) { ShowError("Import couldn’t finish", e.Message); }
    }

    /// <summary>Projects open in their own tabs; images import into the destination (or new tabs from the command line).</summary>
    private async Task ReceivePaths(string[] paths, PointD? point, Guid? destination)
    {
        bool IsProject(string p) => Path.GetExtension(p.TrimEnd('\\', '/')).ToLowerInvariant() is ".lform" or ".comp"
            || Directory.Exists(p) && File.Exists(Path.Combine(p, "manifest.json"))
            || Path.GetFileName(p).Equals("manifest.json", StringComparison.OrdinalIgnoreCase);
        var projects = paths.Where(IsProject).ToList();
        var images = paths.Where(p => !IsProject(p) && File.Exists(p)).ToList();
        if (projects.Count > 1 && destination != null) { ShowError("Open one project at a time", "Drop a single .lform or .comp project."); return; }
        foreach (var project in projects) if (!await Open(project)) return;
        if (images.Count == 0) return;
        if (destination is { } id && projects.Count == 0 && workspace.Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
        {
            await ImportImages(tab.Session, images, point);
            return;
        }
        foreach (var file in images)
        {
            var target = workspace.AddTab();
            await ImportImages(target.Session, new[] { file }, null);
        }
    }

    private async Task Paste()
    {
        await host.RefreshClipboardAsync();
        var s = Session;
        if (s.CanPaste) s.Paste();
        else if (s.Document == null && host.GetClipboardImage() is { } image)
        {
            s.CreateDocument(image.Width, image.Height);
            s.Paste();
        }
        else host.Beep();
    }

    private async Task ChooseMaskColor(bool background)
    {
        var white = await WithDialog<bool?>(async root => await Dialogs.ChooseMaskColor(root, background));
        if (white is not { } w || attached is not { } s) return;
        s.MaskPaintWhite = background ? !w : w;
    }
}

/// <summary>Small persisted preferences (the Mac's @AppStorage), stored beside the user's app data.</summary>
internal static class Settings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LayerForm", "settings.json");
    private static Dictionary<string, double>? values;

    private static Dictionary<string, double> Values
    {
        get
        {
            if (values != null) return values;
            if (File.Exists(FilePath))
                try { values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)); } catch { }
            return values ??= new();
        }
    }

    public static double Get(string key, double fallback) => Values.TryGetValue(key, out var v) && double.IsFinite(v) ? v : fallback;

    public static void Set(string key, double value)
    {
        Values[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(Values));
        }
        catch { }
    }
}
