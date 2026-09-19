using Compositor.App.Platform;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Compositor.App.Controls;

/// <summary>Help › About Layer Form: what the app is, where it came from, and where to go next.</summary>
public static class AboutDialog
{
    public static async Task<bool?> Show(XamlRoot root)
    {
        var icon = new Image { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top };
        try { icon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon-128.png"))); } catch { }

        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "Layer Form", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Ui.Label($"Version {ProjectLinks.AppVersion} · {ProjectLinks.WindowsDescription}", 12, brush: Ui.Secondary));
        heading.Children.Add(Ui.Label("A native, open-source image editor for Windows", 12, brush: Ui.Secondary));

        var body = new StackPanel { Spacing = 14, Width = 440 };
        body.Children.Add(Ui.Row(16, icon, heading));
        body.Children.Add(Paragraph(
            "Layer Form is a layer-based image editor for everyday compositing and retouching. Stack and mask layers, " +
            "make selections, paint, heal, adjust color and remove backgrounds — then export a finished image. " +
            "It is free, has no account or subscription, and your files never leave your PC."));
        body.Children.Add(Paragraph(
            "It began as a Windows port of Compositor, the macOS editor by Wonder Assembly LLC / Robbie Tilton, and keeps its " +
            "editing model and .comp project format. The application itself was rebuilt for Windows in C# with WinUI 3, Win2D, " +
            "SkiaSharp and DirectML, and has since grown its own features: multi-document tabs, broad image-format support, " +
            "GPU background removal, Photoshop-style shortcuts and more."));
        body.Children.Add(Paragraph(
            "Found a problem or have an idea? Help › Report a Bug and Help › Request a Feature open a pre-filled GitHub issue. " +
            "If Layer Form saves you time, a donation helps keep it maintained."));

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-12, 0, 0, 0) };
        foreach (var (text, url) in new[] { ("GitHub", ProjectLinks.Repository), ("What’s new", ProjectLinks.Changelog), ("License", ProjectLinks.License), ("Original Compositor", ProjectLinks.Upstream) })
            links.Children.Add(new HyperlinkButton { Content = text, NavigateUri = new Uri(url) });
        body.Children.Add(links);

        body.Children.Add(Ui.Label("© 2026 Binod Ray and contributors. Portions © Wonder Assembly LLC. Released under the MIT License.", 11, brush: Ui.Secondary));

        var dialog = new ContentDialog
        {
            XamlRoot = root, Content = body,
            PrimaryButtonText = "Donate", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ProjectLinks.Open(ProjectLinks.Donate);
        return true;
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
}
