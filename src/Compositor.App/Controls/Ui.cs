using System.Globalization;
using Compositor.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using WinColor = Windows.UI.Color;

namespace Compositor.App.Controls;

/// <summary>Keeps controls in step with the session: each registers how to read its value back.</summary>
public sealed class Binder
{
    private readonly List<Action> updaters = new();
    public void Add(Action update) { updaters.Add(update); update(); }
    public void Update() { foreach (var update in updaters) update(); }
}

public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => new(WinColor.FromArgb(a, r, g, b));
    public static Brush Secondary => Brush("SecondaryTextBrush");
    public static Brush Divider => Brush("DividerBrush");

    public static TextBlock Label(string text, double size = 12, bool bold = false, Brush? brush = null) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center, Foreground = brush ?? Brush("TextFillColorPrimaryBrush"),
    };

    public static TextBlock Title(string text) => Label(text, 13, true);

    public static Border VerticalDivider(double height = 18) => new() { Width = 1, Height = height, Background = Divider, Margin = new Thickness(2, 0, 2, 0) };
    public static Border HorizontalDivider() => new() { Height = 1, Background = Divider };

    public static StackPanel Row(double spacing = 12, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static Button Capsule(string text, Action action, bool accent = false)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.Resources[accent ? "AccentCapsuleButton" : "CapsuleButton"] };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button IconButton(UIElement icon, Action action, string tooltip, double width = 32, double height = 32)
    {
        var button = new Button
        {
            Content = icon, Width = width, Height = height, Style = (Style)Application.Current.Resources["PlainIconButton"],
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    public static CheckBox Check(string text, Func<bool> get, Action<bool> set, Binder binder, string? tooltip = null)
    {
        var box = new CheckBox { Content = text, MinWidth = 0, MinHeight = 26, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 0, 1) };
        bool updating = false;
        binder.Add(() => { updating = true; box.IsChecked = get(); updating = false; });
        box.Checked += (_, _) => { if (!updating) set(true); };
        box.Unchecked += (_, _) => { if (!updating) set(false); };
        if (tooltip != null) ToolTipService.SetToolTip(box, tooltip);
        return box;
    }

    /// <summary>The Mac's segmented picker: capsule-shaped buttons, the chosen one highlighted.</summary>
    public static FrameworkElement Segmented<T>(IReadOnlyList<(T Value, string Title)> options, Func<T> get, Action<T> set, Binder binder, string? tooltip = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(7), Background = Solid(20, 255, 255, 255), BorderBrush = Solid(26, 255, 255, 255),
            BorderThickness = new Thickness(1), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        border.Child = panel;
        var buttons = new List<(Button Button, T Value)>();
        foreach (var (value, title) in options)
        {
            var button = new Button
            {
                Content = title, FontSize = 12, Padding = new Thickness(10, 2, 10, 3), MinHeight = 22, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            };
            var captured = value;
            button.Click += (_, _) => set(captured);
            buttons.Add((button, value));
            panel.Children.Add(button);
        }
        binder.Add(() =>
        {
            var current = get();
            foreach (var (button, value) in buttons)
                button.Background = EqualityComparer<T>.Default.Equals(value, current) ? Solid(56, 255, 255, 255) : new SolidColorBrush(Colors.Transparent);
        });
        if (tooltip != null) ToolTipService.SetToolTip(border, tooltip);
        return border;
    }

    /// <summary>A segmented picker of icons, each with its tooltip.</summary>
    public static FrameworkElement IconSegmented<T>(IReadOnlyList<(T Value, Func<FrameworkElement> Icon, string Tooltip)> options, Func<T> get, Action<T> set, Binder binder)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(7), Background = Solid(20, 255, 255, 255), BorderBrush = Solid(26, 255, 255, 255),
            BorderThickness = new Thickness(1), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        border.Child = panel;
        var buttons = new List<(Button Button, T Value)>();
        foreach (var (value, icon, tooltip) in options)
        {
            var button = new Button
            {
                Content = icon(), Padding = new Thickness(7, 3, 7, 3), MinHeight = 22, MinWidth = 0, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(button, tooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
            var captured = value;
            button.Click += (_, _) => set(captured);
            buttons.Add((button, value));
            panel.Children.Add(button);
        }
        binder.Add(() =>
        {
            var current = get();
            foreach (var (button, value) in buttons)
                button.Background = EqualityComparer<T>.Default.Equals(value, current) ? Solid(56, 255, 255, 255) : new SolidColorBrush(Colors.Transparent);
        });
        return border;
    }

    public static ComboBox Picker<T>(IReadOnlyList<(T Value, string Title)> options, Func<T> get, Action<T> set, Binder binder, double width = double.NaN)
    {
        var combo = new ComboBox { FontSize = 12, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, Width = width };
        foreach (var (_, title) in options) combo.Items.Add(title);
        bool updating = false;
        binder.Add(() =>
        {
            updating = true;
            var current = get();
            int index = -1;
            for (int i = 0; i < options.Count; i++) if (EqualityComparer<T>.Default.Equals(options[i].Value, current)) index = i;
            if (combo.SelectedIndex != index) combo.SelectedIndex = index;
            updating = false;
        });
        combo.SelectionChanged += (_, _) => { if (!updating && combo.SelectedIndex >= 0) set(options[combo.SelectedIndex].Value); };
        return combo;
    }

    /// <summary>
    /// A number field: applies on Enter or when it loses focus, Up/Down step by <paramref name="step"/> (×10 with
    /// Shift), Enter/Escape hand focus back to the canvas (the Mac's releasesFocusOnCommit).
    /// </summary>
    public static TextBox Number(Func<double> get, Action<double> set, Binder binder, double width = 52, int decimals = 0, double step = 1,
        Action? release = null, string? accessibleName = null)
    {
        var box = new TextBox { Width = width, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        if (accessibleName != null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, accessibleName);
        string Format(double v) => decimals == 0 ? Math.Round(v).ToString(CultureInfo.CurrentCulture)
            : Math.Abs(v - Math.Round(v)) < Math.Pow(10, -decimals) / 2 ? Math.Round(v).ToString(CultureInfo.CurrentCulture)
            : v.ToString("F" + decimals, CultureInfo.CurrentCulture);
        bool focused = false;
        binder.Add(() => { if (!focused) box.Text = Format(get()); });
        void Apply()
        {
            if (double.TryParse(box.Text.Replace("%", "").Replace("px", "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value))
                set(value);
            box.Text = Format(get());
        }
        box.GotFocus += (_, _) => { focused = true; box.SelectAll(); };
        box.LostFocus += (_, _) => { focused = false; Apply(); };
        box.KeyDown += (_, e) =>
        {
            bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (e.Key is VirtualKey.Up or VirtualKey.Down)
            {
                double amount = step * (shift ? 10 : 1) * (e.Key == VirtualKey.Up ? 1 : -1);
                set(get() + amount);
                box.Text = Format(get());
                box.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter) { Apply(); release?.Invoke(); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { box.Text = Format(get()); release?.Invoke(); e.Handled = true; }
        };
        return box;
    }

    public static Slider Slider(double min, double max, Func<double> get, Action<double> set, Binder binder, double width = 100,
        Action? began = null, Action? ended = null)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Width = width, VerticalAlignment = VerticalAlignment.Center, StepFrequency = (max - min) / 1000,
            IsThumbToolTipEnabled = false, Margin = new Thickness(0, -6, 0, -6),
        };
        bool updating = false;
        binder.Add(() => { updating = true; slider.Value = Math.Clamp(get(), min, max); updating = false; });
        slider.ValueChanged += (_, e) => { if (!updating) set(e.NewValue); };
        if (began != null || ended != null)
        {
            slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => began?.Invoke()), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => ended?.Invoke()), true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => ended?.Invoke()), true);
        }
        return slider;
    }

    public static StackPanel WithUnit(FrameworkElement field, string unit) => Row(2, field, Label(unit));

    /// <summary>A colour swatch like the tool rail's: white inner ring, black outer ring.</summary>
    public static Button Swatch(Func<PaletteColor> get, Action action, Binder binder, double width = 34, double height = 18, string? tooltip = null)
    {
        var fill = new Border { CornerRadius = new CornerRadius(4), BorderBrush = new SolidColorBrush(Colors.Black), BorderThickness = new Thickness(1) };
        var inner = new Border { CornerRadius = new CornerRadius(3), BorderBrush = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(1) };
        fill.Child = inner;
        var button = new Button
        {
            Content = fill, Width = width, Height = height, Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center,
        };
        fill.Width = width; fill.Height = height;
        binder.Add(() => { var c = get(); fill.Background = new SolidColorBrush(WinColor.FromArgb(255, c.R8, c.G8, c.B8)); });
        button.Click += (_, _) => action();
        if (tooltip != null) { ToolTipService.SetToolTip(button, tooltip); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip); }
        return button;
    }

    public static WinColor Color(PaletteColor c) => WinColor.FromArgb(255, c.R8, c.G8, c.B8);
    public static WinColor Accent => WinColor.FromArgb(255, 0x0A, 0x84, 0xFF);
}
