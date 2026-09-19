using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Compositor.App.Controls;

/// <summary>
/// A movable, non-modal panel inside the window (FloatingPanel.swift): it never dims the editor, opens centred on the
/// canvas the first time, and reopens wherever it was last left. Closing it reports <see cref="Closed"/> (Cancel).
/// </summary>
public sealed class FloatingPanel : UserControl
{
    private static readonly Dictionary<string, Point> positions = new();
    private readonly string name;
    private readonly TextBlock title = Ui.Label("", 13, true);
    private readonly ContentPresenter body = new();
    private Point? dragOffset;
    public event Action? Closed;
    public Canvas? HostCanvas { get; set; }

    public FloatingPanel(string name)
    {
        this.name = name;
        var close = Ui.IconButton(Icons.Glyph(Icons.Close, 10), () => Closed?.Invoke(), "Close", 26, 26);
        var header = new Grid { Height = 36, Background = Ui.Solid(255, 0x3A, 0x3A, 0x3C), CornerRadius = new CornerRadius(10, 10, 0, 0) };
        title.HorizontalAlignment = HorizontalAlignment.Center;
        header.Children.Add(title);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.Margin = new Thickness(8, 0, 0, 0);
        header.Children.Add(close);
        header.PointerPressed += (_, e) =>
        {
            if (HostCanvas == null) return;
            var p = e.GetCurrentPoint(HostCanvas).Position;
            dragOffset = new Point(p.X - Canvas.GetLeft(this), p.Y - Canvas.GetTop(this));
            header.CapturePointer(e.Pointer);
        };
        header.PointerMoved += (_, e) =>
        {
            if (dragOffset is not { } offset || HostCanvas == null) return;
            var p = e.GetCurrentPoint(HostCanvas).Position;
            Move(p.X - offset.X, p.Y - offset.Y);
        };
        header.PointerReleased += (_, e) => { dragOffset = null; header.ReleasePointerCapture(e.Pointer); };
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.Children.Add(header);
        Grid.SetRow(body, 1);
        stack.Children.Add(body);
        Content = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(10), Background = Ui.Solid(255, 0x2C, 0x2C, 0x2E),
            BorderBrush = Ui.Solid(60, 255, 255, 255), BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 32),
        };
        KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { Closed?.Invoke(); e.Handled = true; } };
    }

    public void Show(Canvas host, string titleText, FrameworkElement content, Rect canvasArea)
    {
        HostCanvas = host;
        title.Text = titleText;
        body.Content = content;
        if (!host.Children.Contains(this)) host.Children.Add(this);
        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (positions.TryGetValue(name, out var saved)) Move(saved.X, saved.Y);
        else Move(canvasArea.X + (canvasArea.Width - DesiredSize.Width) / 2, canvasArea.Y + (canvasArea.Height - DesiredSize.Height) / 2);
    }

    public void SetTitle(string text) => title.Text = text;

    private void Move(double x, double y)
    {
        if (HostCanvas != null)
        {
            x = Math.Clamp(x, -DesiredSize.Width + 80, Math.Max(0, HostCanvas.ActualWidth - 80));
            y = Math.Clamp(y, 0, Math.Max(0, HostCanvas.ActualHeight - 36));
        }
        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
        positions[name] = new Point(x, y);
    }

    public void Hide()
    {
        if (HostCanvas?.Children.Contains(this) == true) HostCanvas.Children.Remove(this);
        body.Content = null;
    }

    public bool IsShown => HostCanvas?.Children.Contains(this) == true;
}
