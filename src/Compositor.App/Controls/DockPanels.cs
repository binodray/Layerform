using System.Text.Json;
using Compositor.Editing;
using Compositor.Imaging;
using Compositor.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;

namespace Compositor.App.Controls;

/// <summary>Swatches and actions the user keeps between sessions (LocalAppData\LayerForm\library.json).</summary>
public sealed class UserLibrary
{
    public List<string> Swatches { get; set; } = new();
    public List<RecordedAction> Actions { get; set; } = new();
    public List<SavedLayer> CustomLayers { get; set; } = new();
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    public sealed class SavedLayer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Custom Layer";
        public string FileName { get; set; } = "";
    }

    public sealed class RecordedAction
    {
        public string Name { get; set; } = "Action";
        public List<string> Steps { get; set; } = new();
    }

    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LayerForm", "library.json");
    private static readonly string LayerDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LayerForm", "custom-layers");
    private static UserLibrary? shared;

    public static UserLibrary Shared
    {
        get
        {
            if (shared != null) return shared;
            if (File.Exists(FilePath))
                try { shared = JsonSerializer.Deserialize<UserLibrary>(File.ReadAllText(FilePath)); } catch { }
            shared ??= new UserLibrary();
            shared.Swatches ??= new();
            shared.Actions ??= new();
            shared.CustomLayers ??= new();
            shared.Shortcuts ??= new();
            if (shared.Actions.Count == 0) shared.Actions.AddRange(DefaultActions());
            return shared;
        }
    }

    /// <summary>Starter actions, built from menu commands like any recorded one.</summary>
    private static IEnumerable<RecordedAction> DefaultActions() => new[]
    {
        new RecordedAction { Name = "Center layer on canvas", Steps = { "Align Horizontal Centers", "Align Vertical Centers" } },
        new RecordedAction { Name = "Mirror canvas", Steps = { "Flip Canvas Horizontal" } },
        new RecordedAction { Name = "Duplicate and mirror layer", Steps = { "Duplicate Layer", "Flip Layer Horizontal" } },
        new RecordedAction { Name = "Invert colors", Steps = { "Invert" } },
        new RecordedAction { Name = "New layer in a folder", Steps = { "New Blank Layer", "Group Selected Layers" } },
    };

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Diagnostics.Log("Library save: " + e.Message); }
    }

    public bool SaveLayer(ImageLayer layer)
    {
        if (layer.Asset is not { } asset || layer.IsGroup) return false;
        try
        {
            Directory.CreateDirectory(LayerDirectory);
            var saved = new SavedLayer { Name = layer.Name };
            saved.FileName = saved.Id + ".png";
            File.WriteAllBytes(Path.Combine(LayerDirectory, saved.FileName), Codecs.EncodePng(asset.Image));
            CustomLayers.Add(saved);
            Save();
            return true;
        }
        catch (Exception e) { Diagnostics.Log("Custom layer save: " + e.Message); return false; }
    }

    public ImageAsset? LoadLayer(SavedLayer saved)
    {
        try
        {
            var image = Codecs.DecodeRgba(File.ReadAllBytes(Path.Combine(LayerDirectory, saved.FileName)));
            return PixelOps.Asset(image, saved.Name);
        }
        catch (Exception e) { Diagnostics.Log("Custom layer load: " + e.Message); return null; }
    }

    public void DeleteLayer(SavedLayer saved)
    {
        CustomLayers.Remove(saved);
        try { File.Delete(Path.Combine(LayerDirectory, saved.FileName)); } catch { }
        Save();
    }
}

/// <summary>A panel docked above Layers, as Photoshop's panels stack on the right: a header with a collapse chevron and a
/// close button, then its content.</summary>
public abstract class DockPanel : UserControl
{
    private readonly ContentPresenter body = new() { Padding = new Thickness(12, 4, 12, 12) };
    private readonly FontIcon chevron = Icons.Glyph(Icons.ChevronDown, 9);
    private bool collapsed;
    public string Key { get; }
    public string Title { get; }
    public event Action? CloseRequested;
    protected readonly Func<EditorSession?> Session;

    protected DockPanel(string key, string title, Func<EditorSession?> session)
    {
        Key = key;
        Title = title;
        Session = session;
        var header = new Grid { Height = 34, Padding = new Thickness(8, 0, 6, 0), Background = new SolidColorBrush(Colors.Transparent) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chevron.Margin = new Thickness(4, 0, 8, 0);
        chevron.Foreground = Ui.Secondary;
        header.Children.Add(chevron);
        var label = Ui.Title(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        var close = Ui.IconButton(Icons.Glyph(Icons.Close, 8), () => CloseRequested?.Invoke(), $"Close {title}", 22, 22);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        header.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, close)) return;
            collapsed = !collapsed;
            body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            chevron.Glyph = collapsed ? Icons.ChevronRight : Icons.ChevronDown;
        };
        Content = new StackPanel { Children = { header, body, new Border { Height = 1, Background = Ui.Divider } } };
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current != null; current = VisualTreeHelper.GetParent(current))
            if (current == ancestor) return true;
        return false;
    }

    protected void SetBody(UIElement content) => body.Content = content;
    public abstract void Refresh();
}

// MARK: Character

/// <summary>Photoshop-style Character panel. It edits the active text layer directly on the canvas, without a modal dialog.</summary>
public sealed class TextDock : DockPanel
{
    private readonly TextBox content = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, PlaceholderText = "Click the canvas with the Type tool" };
    private readonly ComboBox font = new() { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox size = Number(1, 2000), tracking = Number(-100, 1000), leading = Number(0, 5000);
    private readonly NumberBox boxWidth = Number(16, 30000), boxHeight = Number(16, 30000);
    private readonly TextBox color = new() { PlaceholderText = "RRGGBB", MaxLength = 7 };
    private readonly ComboBox alignment = new();
    private readonly CheckBox fixedBox = new() { Content = "Paragraph box" };
    private bool syncing;
    private Guid? shownLayer;

    private static NumberBox Number(double min, double max) => new() { Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    public TextDock(Func<EditorSession?> session) : base("character", "Character", session)
    {
        foreach (var family in SKFontManager.Default.FontFamilies.OrderBy(x => x)) font.Items.Add(family);
        foreach (var value in Enum.GetValues<LayerTextAlignment>()) alignment.Items.Add(value);
        content.TextChanged += (_, _) => Change(s => s with { Content = content.Text.Replace("\r\n", "\n").Replace('\r', '\n') });
        font.LostFocus += (_, _) => Change(s => s with { FontName = string.IsNullOrWhiteSpace(font.Text) ? "Arial" : font.Text.Trim() });
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is string family) Change(s => s with { FontName = family }); };
        size.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) Change(s => s with { FontSize = e.NewValue }); };
        tracking.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) Change(s => s with { Tracking = e.NewValue }); };
        leading.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) Change(s => s with { Leading = e.NewValue }); };
        color.LostFocus += (_, _) => { if (PaletteColor.FromHex(color.Text) is { } c) Change(s => s with { Red = c.Red, Green = c.Green, Blue = c.Blue }); };
        alignment.SelectionChanged += (_, _) => { if (alignment.SelectedItem is LayerTextAlignment a) Change(s => s with { Alignment = a }); };
        fixedBox.Checked += (_, _) => Change(s => s with { BoxSize = new Compositor.Geometry.SizeD(Math.Max(16, boxWidth.Value), Math.Max(16, boxHeight.Value)) });
        fixedBox.Unchecked += (_, _) => Change(s => s with { BoxSize = null });
        boxWidth.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) Change(s => s with { BoxSize = new Compositor.Geometry.SizeD(e.NewValue, Math.Max(16, boxHeight.Value)) }); };
        boxHeight.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) Change(s => s with { BoxSize = new Compositor.Geometry.SizeD(Math.Max(16, boxWidth.Value), e.NewValue) }); };

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Put(FrameworkElement e, int row, int column, int span = 1) { while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(e, row); Grid.SetColumn(e, column); Grid.SetColumnSpan(e, span); grid.Children.Add(e); }
        Put(font, 0, 0, 2); Put(Labeled("Size", size), 1, 0); Put(Labeled("Leading", leading), 1, 1);
        Put(Labeled("Tracking", tracking), 2, 0); Put(Labeled("Color #", color), 2, 1); Put(Labeled("Align", alignment), 3, 0); Put(fixedBox, 3, 1);
        Put(Labeled("Box W", boxWidth), 4, 0); Put(Labeled("Box H", boxHeight), 4, 1);
        SetBody(grid);
    }

    private static FrameworkElement Labeled(string label, Control field) => new StackPanel { Spacing = 3, Children = { Ui.Label(label, 10, brush: Ui.Secondary), field } };

    private LayerTextStyle Current(EditorSession s) => s.ActiveLayer?.LiveText?.Style ?? s.TextDefaults;
    private void Change(Func<LayerTextStyle, LayerTextStyle> update)
    {
        if (syncing || Session() is not { } s) return;
        var next = update(Current(s));
        if (!next.IsValid) return;
        if (s.ActiveLayer is { LiveText: not null } layer) s.EditTextLayer(layer.Id, next); else s.TextDefaults = next;
    }

    public void FocusContent()
    {
        content.Focus(FocusState.Programmatic);
        content.SelectAll();
    }

    public override void Refresh()
    {
        if (Session() is not { } s) return;
        var layer = s.ActiveLayer is { LiveText: not null } text ? text : null;
        var style = Current(s);
        syncing = true;
        if (content.FocusState == FocusState.Unfocused || shownLayer != layer?.Id) content.Text = style.Content;
        font.Text = style.FontName; size.Value = style.FontSize; tracking.Value = style.Tracking; leading.Value = style.Leading;
        color.Text = new PaletteColor(style.Red, style.Green, style.Blue).Hex; alignment.SelectedItem = style.Alignment;
        fixedBox.IsChecked = style.BoxSize != null; boxWidth.Value = style.BoxSize?.Width ?? 600; boxHeight.Value = style.BoxSize?.Height ?? 300;
        boxWidth.IsEnabled = boxHeight.IsEnabled = style.BoxSize != null; content.IsEnabled = layer != null;
        shownLayer = layer?.Id;
        syncing = false;
    }
}

// MARK: Color

/// <summary>Photoshop's Color panel: the foreground (or background) as RGB sliders and a hex field.</summary>
public sealed class ColorDock : DockPanel
{
    private readonly Binder binder = new();
    private bool editingBackground;

    public ColorDock(Func<EditorSession?> session) : base("color", "Color", session)
    {
        PaletteColor Current() => Session()?.PaletteColorFor(editingBackground) ?? PaletteColor.Black;
        void Set(PaletteColor c) { if (Session() is { } s && s.CanEditPalette) s.SetPaletteColor(c, editingBackground); }
        var which = Ui.Segmented(new[] { (false, "Foreground"), (true, "Background") }, () => editingBackground, v => { editingBackground = v; binder.Update(); }, binder);
        var swatch = new Border { Width = 34, Height = 22, CornerRadius = new CornerRadius(5), BorderBrush = Ui.Solid(60, 255, 255, 255), BorderThickness = new Thickness(1) };
        binder.Add(() => swatch.Background = new SolidColorBrush(Ui.Color(Current())));
        FrameworkElement Channel(string name, Func<PaletteColor, double> get, Func<PaletteColor, double, PaletteColor> with)
        {
            var slider = Ui.Slider(0, 255, () => Math.Round(get(Current()) * 255), v => Set(with(Current(), Math.Round(v) / 255)), binder, double.NaN);
            slider.HorizontalAlignment = HorizontalAlignment.Stretch;
            var field = Ui.Number(() => Math.Round(get(Current()) * 255), v => Set(with(Current(), Math.Clamp(Math.Round(v), 0, 255) / 255)), binder, 44);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Ui.Label(name, 11, brush: Ui.Secondary);
            label.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(label);
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);
            Grid.SetColumn(field, 2);
            grid.Children.Add(field);
            return grid;
        }
        var hex = new TextBox { Width = 90, FontSize = 12 };
        binder.Add(() => { if (hex.FocusState == FocusState.Unfocused) hex.Text = Current().Hex; });
        void ApplyHex() { if (PaletteColor.FromHex(hex.Text) is { } c) Set(c); hex.Text = Current().Hex; }
        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { ApplyHex(); e.Handled = true; } };
        SetBody(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Row(10, swatch, which),
                Channel("R", c => c.Red, (c, v) => c with { Red = v }),
                Channel("G", c => c.Green, (c, v) => c with { Green = v }),
                Channel("B", c => c.Blue, (c, v) => c with { Blue = v }),
                Ui.Row(8, Ui.Label("#", 12, brush: Ui.Secondary), hex),
            },
        });
    }

    public override void Refresh() => binder.Update();
}

// MARK: Swatches

/// <summary>Photoshop's Swatches panel: click for the foreground, Alt-click for the background; + keeps the foreground,
/// right-click removes a saved swatch.</summary>
public sealed class SwatchesDock : DockPanel
{
    private static readonly string[] Presets =
    {
        "000000", "404040", "808080", "BFBFBF", "FFFFFF", "FF0000", "FF8000", "FFFF00", "80FF00", "00FF00",
        "00FF80", "00FFFF", "0080FF", "0000FF", "8000FF", "FF00FF", "FF0080", "800000", "804000", "808000",
        "008000", "008080", "000080", "400080", "F2D7C9", "E0AC8A", "C68642", "8D5524", "5C3A21", "3B2219",
    };
    private readonly VariableSizedWrapGrid saved = new() { Orientation = Orientation.Horizontal, ItemWidth = 22, ItemHeight = 22 };

    public SwatchesDock(Func<EditorSession?> session) : base("swatches", "Swatches", session)
    {
        var presets = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 22, ItemHeight = 22 };
        foreach (var hex in Presets) presets.Children.Add(Chip(hex, removable: false));
        var add = Ui.IconButton(Icons.Lucide(LucideIcons.Plus, 12), () =>
        {
            if (Session() is not { } s) return;
            var hex = s.ForegroundColor.Hex;
            if (!UserLibrary.Shared.Swatches.Contains(hex)) { UserLibrary.Shared.Swatches.Add(hex); UserLibrary.Shared.Save(); Rebuild(); }
        }, "Save the foreground color as a swatch", 22, 22);
        SetBody(new StackPanel
        {
            Spacing = 8,
            Children = { presets, Ui.Row(6, Ui.Label("Saved", 11, brush: Ui.Secondary), add), saved },
        });
        Rebuild();
    }

    private void Rebuild()
    {
        saved.Children.Clear();
        foreach (var hex in UserLibrary.Shared.Swatches) saved.Children.Add(Chip(hex, removable: true));
    }

    private FrameworkElement Chip(string hex, bool removable)
    {
        var color = PaletteColor.FromHex(hex) ?? PaletteColor.Black;
        var chip = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Ui.Color(color)),
            BorderBrush = Ui.Solid(50, 255, 255, 255), BorderThickness = new Thickness(1),
        };
        ToolTipService.SetToolTip(chip, $"#{hex} · click: recolor selected layer · Alt-click: background" + (removable ? " · right-click: remove" : ""));
        chip.PointerPressed += (_, e) =>
        {
            if (Session() is not { } s || !s.CanEditPalette) return;
            var point = e.GetCurrentPoint(chip);
            if (point.Properties.IsRightButtonPressed)
            {
                if (removable) { UserLibrary.Shared.Swatches.Remove(hex); UserLibrary.Shared.Save(); Rebuild(); }
                return;
            }
            bool alt = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu);
            s.SetPaletteColor(color, alt);
            if (!alt && !s.IsMaskSelected && s.CanApplyLayerEffect) s.RecolorLayer(color, RecolorMode.Replace);
            e.Handled = true;
        };
        return chip;
    }

    public override void Refresh() { }
}

// MARK: Custom layers

/// <summary>A device-local shelf of reusable raster layers.</summary>
public sealed class CustomLayersDock : DockPanel
{
    private readonly StackPanel list = new() { Spacing = 3 };
    private readonly Button save;
    private string signature = "";

    public CustomLayersDock(Func<EditorSession?> session) : base("customLayers", "Custom Layers", session)
    {
        save = Ui.IconButton(Icons.Glyph(Icons.Add, 12), SaveSelected, "Save selected layer for reuse", 24, 24);
        SetBody(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Row(6, Ui.Label("Reusable layers", 11, brush: Ui.Secondary), save),
                new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            },
        });
        Rebuild();
    }

    private void SaveSelected()
    {
        if (Session()?.ActiveLayer is not { } layer || !UserLibrary.Shared.SaveLayer(layer)) return;
        signature = "";
        Rebuild();
    }

    private void Rebuild()
    {
        list.Children.Clear();
        foreach (var saved in UserLibrary.Shared.CustomLayers.ToList())
        {
            var use = new Button
            {
                Content = Ui.Label(saved.Name, 12), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0),
            };
            use.Click += (_, _) =>
            {
                if (Session() is not { } s || UserLibrary.Shared.LoadLayer(saved) is not { } asset) return;
                s.Insert(asset, fitToCanvas: true);
            };
            var remove = Ui.IconButton(Icons.Glyph(Icons.Trash, 11), () => { UserLibrary.Shared.DeleteLayer(saved); signature = ""; Rebuild(); }, $"Delete {saved.Name}", 24, 24);
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(use);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            list.Children.Add(row);
        }
        if (list.Children.Count == 0) list.Children.Add(Ui.Label("Select a layer, then press + to save it here.", 11, brush: Ui.Secondary));
    }

    public override void Refresh()
    {
        save.IsEnabled = Session()?.ActiveLayer is { Asset: not null, IsGroup: false };
        string next = string.Join("|", UserLibrary.Shared.CustomLayers.Select(x => x.Id + x.Name));
        if (next != signature) { signature = next; Rebuild(); }
    }
}

// MARK: History

/// <summary>Photoshop's History panel: every step of this project; click one to go back (or forward) to it.</summary>
public sealed class HistoryDock : DockPanel
{
    private readonly StackPanel list = new() { Spacing = 1 };
    private readonly ScrollViewer scroller;
    private string signature = "";

    public HistoryDock(Func<EditorSession?> session) : base("history", "History", session)
    {
        scroller = new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetBody(scroller);
    }

    public override void Refresh()
    {
        var s = Session();
        var past = s?.History.PastNames ?? Array.Empty<string>();
        var future = s?.History.FutureNames ?? Array.Empty<string>();
        string next = $"{s?.GetHashCode()}|{string.Join("/", past)}|{string.Join("/", future)}|{s?.Document != null}";
        if (next == signature) return;
        signature = next;
        list.Children.Clear();
        if (s?.Document == null) { list.Children.Add(Ui.Label("No project open", 11, brush: Ui.Secondary)); return; }
        list.Children.Add(Row("Open", 0, past.Count == 0, false));
        for (int i = 0; i < past.Count; i++) list.Children.Add(Row(past[i], i + 1, i == past.Count - 1, false));
        for (int i = 0; i < future.Count; i++) list.Children.Add(Row(future[i], past.Count + i + 1, false, true));
        scroller.ChangeView(null, scroller.ScrollableHeight + 1000, null, true);
    }

    private FrameworkElement Row(string name, int count, bool current, bool undone)
    {
        var text = Ui.Label(name, 12, current);
        text.Opacity = undone ? 0.45 : 1;
        var row = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3, 8, 3), MinHeight = 24, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0),
            Background = current ? new SolidColorBrush(Ui.Color(new PaletteColor(0.04, 0.33, 0.67))) : new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(row, undone ? $"Redo to “{name}”" : $"Go back to “{name}”");
        row.Click += (_, _) => Session()?.JumpToHistory(count);
        return row;
    }
}

// MARK: Actions

/// <summary>Photoshop's Actions panel: record a run of menu commands, then play it back on any project.</summary>
public sealed class ActionsDock : DockPanel
{
    private readonly Func<string, string?> run;
    private readonly StackPanel list = new() { Spacing = 1 };
    private readonly TextBlock status = Ui.Label("", 11);
    private readonly Button record, stop, play, delete;
    private int selected = -1;
    private readonly List<(Button Row, TextBlock Name)> rows = new();
    public UserLibrary.RecordedAction? Recording { get; private set; }

    /// <param name="run">Runs a menu command by name; returns an error message, or null when it ran.</param>
    public ActionsDock(Func<EditorSession?> session, Func<string, string?> run) : base("actions", "Actions", session)
    {
        this.run = run;
        status.Foreground = Ui.Secondary;
        status.TextWrapping = TextWrapping.Wrap;
        record = Ui.IconButton(Icons.Fluent(Icons.LucideData(LucideIcons.CircleDot), 16), StartRecording, "Record a new action from the menu commands you use", 28, 26);
        stop = Ui.IconButton(Icons.Fluent(Icons.LucideData(LucideIcons.Square), 16), StopRecording, "Stop recording", 28, 26);
        play = Ui.IconButton(Icons.Fluent(Icons.LucideData(LucideIcons.Play), 16), () => _ = Play(), "Play the selected action", 28, 26);
        delete = Ui.IconButton(Icons.Fluent(Icons.LucideData(LucideIcons.Trash2), 16), Delete, "Delete the selected action", 28, 26);
        SetBody(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Ui.Row(4, record, stop, play, delete), status,
            },
        });
        Rebuild();
    }

    private void Rebuild()
    {
        list.Children.Clear();
        rows.Clear();
        var actions = UserLibrary.Shared.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            int index = i;
            var action = actions[i];
            var name = Ui.Label(action.Name, 12, index == selected);
            var steps = Ui.Label(string.Join(" › ", action.Steps), 10, brush: Ui.Secondary);
            steps.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new Button
            {
                Content = new StackPanel { Spacing = 1, Children = { name, steps } }, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(0),
                Background = index == selected ? new SolidColorBrush(Ui.Color(new PaletteColor(0.04, 0.33, 0.67))) : new SolidColorBrush(Colors.Transparent),
            };
            ToolTipService.SetToolTip(row, "Double-click to play");
            row.Click += (_, _) => { selected = index; Highlight(); };
            row.DoubleTapped += (_, _) => { selected = index; Highlight(); _ = Play(); };
            rows.Add((row, name));
            list.Children.Add(row);
        }
        UpdateButtons();
    }

    /// <summary>Marks the selected action without rebuilding the rows, so a double-click reaches the same row.</summary>
    private void Highlight()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Row.Background = i == selected ? new SolidColorBrush(Ui.Color(new PaletteColor(0.04, 0.33, 0.67))) : new SolidColorBrush(Colors.Transparent);
            rows[i].Name.FontWeight = i == selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool recording = Recording != null;
        record.IsEnabled = !recording;
        stop.IsEnabled = recording;
        play.IsEnabled = !recording && selected >= 0 && selected < UserLibrary.Shared.Actions.Count;
        delete.IsEnabled = play.IsEnabled;
        if (recording) status.Text = $"Recording “{Recording!.Name}”: {Recording.Steps.Count} step(s). Use the menus, then press Stop.";
    }

    private void StartRecording()
    {
        int number = UserLibrary.Shared.Actions.Count + 1;
        Recording = new UserLibrary.RecordedAction { Name = $"Action {number}" };
        UpdateButtons();
    }

    private void StopRecording()
    {
        if (Recording is { } action && action.Steps.Count > 0)
        {
            UserLibrary.Shared.Actions.Add(action);
            UserLibrary.Shared.Save();
            selected = UserLibrary.Shared.Actions.Count - 1;
            status.Text = $"Saved “{action.Name}” with {action.Steps.Count} step(s).";
        }
        else status.Text = "Nothing was recorded.";
        Recording = null;
        Rebuild();
    }

    /// <summary>Called for each menu command the user runs; kept only while recording.</summary>
    public void Record(string command)
    {
        if (Recording == null) return;
        Recording.Steps.Add(command);
        UpdateButtons();
    }

    private async Task Play()
    {
        if (selected < 0 || selected >= UserLibrary.Shared.Actions.Count) return;
        var action = UserLibrary.Shared.Actions[selected];
        foreach (var step in action.Steps)
        {
            if (run(step) is { } error) { status.Text = $"Stopped at “{step}”: {error}"; return; }
            await Task.Delay(30);
        }
        status.Text = $"Played “{action.Name}”.";
    }

    private void Delete()
    {
        if (selected < 0 || selected >= UserLibrary.Shared.Actions.Count) return;
        UserLibrary.Shared.Actions.RemoveAt(selected);
        UserLibrary.Shared.Save();
        selected = -1;
        Rebuild();
    }

    public override void Refresh() { }
}

// MARK: Align

/// <summary>Align and Distribute as a panel (Window › Align): one layer aligns to the canvas or selection, several to each other.</summary>
public sealed class AlignDock : DockPanel
{
    private readonly Binder binder = new();
    private readonly TextBlock reference = Ui.Label("", 11);

    public AlignDock(Func<EditorSession?> session) : base("align", "Align", session)
    {
        reference.Foreground = Ui.Secondary;
        FrameworkElement Button(string icon, string tip, Action<EditorSession> act, Func<EditorSession, bool> can)
        {
            var button = Ui.IconButton(Icons.Fluent(icon, 18), () => { if (Session() is { } s) act(s); }, tip, 34, 30);
            binder.Add(() => button.IsEnabled = Session() is { } s && can(s));
            return button;
        }
        var align = Ui.Row(2,
            Button(Icons.LucideData(LucideIcons.AlignStartVertical), "Align left edges", s => s.Align(AlignEdge.Left), s => s.CanAlign),
            Button(Icons.LucideData(LucideIcons.AlignCenterVertical), "Align horizontal centers", s => s.Align(AlignEdge.HorizontalCenter), s => s.CanAlign),
            Button(Icons.LucideData(LucideIcons.AlignEndVertical), "Align right edges", s => s.Align(AlignEdge.Right), s => s.CanAlign),
            Button(Icons.LucideData(LucideIcons.AlignStartHorizontal), "Align top edges", s => s.Align(AlignEdge.Top), s => s.CanAlign),
            Button(Icons.LucideData(LucideIcons.AlignCenterHorizontal), "Align vertical centers", s => s.Align(AlignEdge.VerticalCenter), s => s.CanAlign),
            Button(Icons.LucideData(LucideIcons.AlignEndHorizontal), "Align bottom edges", s => s.Align(AlignEdge.Bottom), s => s.CanAlign));
        var distribute = Ui.Row(2,
            Button(Icons.LucideData(LucideIcons.AlignHorizontalDistributeCenter), "Distribute horizontal centers", s => s.Distribute(DistributeAxis.Horizontal), s => s.CanDistribute),
            Button(Icons.LucideData(LucideIcons.AlignVerticalDistributeCenter), "Distribute vertical centers", s => s.Distribute(DistributeAxis.Vertical), s => s.CanDistribute));
        binder.Add(() => reference.Text = Session() is { Document: not null } s && s.CanAlign ? $"Aligns to {s.AlignReference}." : "Select a layer (Ctrl-click for several).");
        SetBody(new StackPanel
        {
            Spacing = 8,
            Children = { align, Ui.Row(8, Ui.Label("Distribute", 11, brush: Ui.Secondary), distribute), reference },
        });
    }

    public override void Refresh() => binder.Update();
}
