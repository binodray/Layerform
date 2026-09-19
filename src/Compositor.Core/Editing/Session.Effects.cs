using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;

namespace Compositor.Editing;

/// <summary>How Change Color recolours a layer: replace every colour, or tint and keep its light and shade.</summary>
public enum RecolorMode { Replace, Tint }

public sealed record ShadowSettings(PaletteColor Color, double Opacity = 0.6, double OffsetX = 12, double OffsetY = 12, double Blur = 12);

public sealed record GradientOverlaySettings(PaletteColor Start, PaletteColor End, double Angle = 90, bool Radial = false, bool Reverse = false);

/// <summary>Layer effects from the right-click menu: Change Color, Gradient Overlay and Add Shadow. Each keeps the layer's
/// transparency, works on its pixels (one undo step), and leaves the layer's position, mask and blending alone.</summary>
public static class LayerEffects
{
    public static RasterImage Recolor(RasterImage source, PaletteColor color, RecolorMode mode)
    {
        int w = source.Width, h = source.Height;
        var src = source.Pixels;
        var output = new byte[w * h * 4];
        double tr = color.Red, tg = color.Green, tb = color.Blue;
        // The tint's own lightness: tinting maps the layer's lightness around it, so mid-tones land on the chosen colour.
        double target = 0.299 * tr + 0.587 * tg + 0.114 * tb;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = y * source.RowBytes + x * 4, t = (y * w + x) * 4;
                byte a = src[s + 3];
                if (a == 0) continue;
                double r, g, b;
                if (mode == RecolorMode.Replace) { r = tr; g = tg; b = tb; }
                else
                {
                    // Straight colour, its luminance, then the tint scaled to that luminance (Photoshop's Color blend, simplified).
                    double sr = src[s] / (double)a, sg = src[s + 1] / (double)a, sb = src[s + 2] / (double)a;
                    double l = 0.299 * sr + 0.587 * sg + 0.114 * sb;
                    if (target <= 0.0001) { r = g = b = l; }
                    else if (l <= 0.5) { double k = l / 0.5; r = tr * k; g = tg * k; b = tb * k; }
                    else { double k = (l - 0.5) / 0.5; r = tr + (1 - tr) * k; g = tg + (1 - tg) * k; b = tb + (1 - tb) * k; }
                }
                output[t] = Premul(r, a); output[t + 1] = Premul(g, a); output[t + 2] = Premul(b, a); output[t + 3] = a;
            }
        return RasterImage.FromPixels(w, h, output);
    }

    public static RasterImage GradientOverlay(RasterImage source, GradientOverlaySettings settings)
    {
        int w = source.Width, h = source.Height;
        var src = source.Pixels;
        var output = new byte[w * h * 4];
        double angle = settings.Angle * Math.PI / 180, dx = Math.Cos(angle), dy = -Math.Sin(angle);
        // The gradient spans the layer: from one edge to the opposite along the angle, or from the centre outwards.
        double half = Math.Abs(dx) * w / 2 + Math.Abs(dy) * h / 2, radius = Math.Sqrt(w * w + h * h) / 2;
        var a0 = settings.Reverse ? settings.End : settings.Start;
        var a1 = settings.Reverse ? settings.Start : settings.End;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = y * source.RowBytes + x * 4, t = (y * w + x) * 4;
                byte a = src[s + 3];
                if (a == 0) continue;
                double px = x + 0.5 - w / 2.0, py = y + 0.5 - h / 2.0;
                double f = settings.Radial ? Math.Sqrt(px * px + py * py) / Math.Max(1, radius)
                    : (px * dx + py * dy + half) / Math.Max(1, 2 * half);
                f = Math.Clamp(f, 0, 1);
                output[t] = Premul(a0.Red + (a1.Red - a0.Red) * f, a);
                output[t + 1] = Premul(a0.Green + (a1.Green - a0.Green) * f, a);
                output[t + 2] = Premul(a0.Blue + (a1.Blue - a0.Blue) * f, a);
                output[t + 3] = a;
            }
        return RasterImage.FromPixels(w, h, output);
    }

    /// <summary>The layer's silhouette in the shadow colour, blurred, on a canvas grown by <paramref name="padding"/> each side.</summary>
    public static RasterImage Shadow(RasterImage source, ShadowSettings settings, int padding)
    {
        int w = source.Width + 2 * padding, h = source.Height + 2 * padding;
        var src = source.Pixels;
        var output = new byte[w * h * 4];
        var c = settings.Color;
        double opacity = Math.Clamp(settings.Opacity, 0, 1);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                byte a = (byte)Math.Round(src[y * source.RowBytes + x * 4 + 3] * opacity);
                if (a == 0) continue;
                int t = ((y + padding) * w + x + padding) * 4;
                output[t] = Premul(c.Red, a); output[t + 1] = Premul(c.Green, a); output[t + 2] = Premul(c.Blue, a); output[t + 3] = a;
            }
        var flat = RasterImage.FromPixels(w, h, output);
        return settings.Blur > 0.3 ? PixelFilters.GaussianBlur(flat, settings.Blur / 2) : flat;
    }

    private static byte Premul(double channel, byte alpha) => (byte)Math.Clamp(Math.Round(Math.Clamp(channel, 0, 1) * alpha), 0, alpha);
}

public sealed partial class EditorSession
{
    /// <summary>A pixel layer (not a folder or adjustment) whose look can be changed.</summary>
    public bool CanApplyLayerEffect => CanEditLayers && ActiveLayer is { IsGroup: false, Adjustment: null, Asset: not null } && SelectedLayerIds.Count <= 1;

    /// <summary>Replaces the active layer's pixels with <paramref name="effect"/> of them. A shape layer stays a shape when it
    /// is simply recoloured (so it can still be resized cleanly); otherwise it becomes ordinary pixels.</summary>
    public void ApplyLayerEffect(string name, Func<RasterImage, RasterImage> effect, PaletteColor? shapeColor = null)
    {
        if (!CanApplyLayerEffect || ActiveLayerId is not { } id || document?.IndexOf(id) is not (>= 0 and var index)) return;
        var layer = document.Layers[index];
        var result = effect(layer.Asset!.Image);
        BeginEdit(name);
        var shape = shapeColor is { } c && layer.LiveShape is { } live
            ? new LayerShape(live.Style with { Red = c.Red, Green = c.Green, Blue = c.Blue }, result) : null;
        ReplaceLayer(index, layer with { Asset = PixelOps.Asset(result, layer.Asset.Name), Shape = shape });
        EndEdit();
    }

    public void RecolorLayer(PaletteColor color, RecolorMode mode) =>
        ApplyLayerEffect("Change Color", image => LayerEffects.Recolor(image, color, mode), mode == RecolorMode.Replace ? color : null);

    public void GradientOverlayLayer(GradientOverlaySettings settings) =>
        ApplyLayerEffect("Gradient Overlay", image => LayerEffects.GradientOverlay(image, settings));

    /// <summary>Adds a drop shadow as its own layer beneath the active one, so it can be moved, faded or deleted separately.</summary>
    public void AddShadow(ShadowSettings settings)
    {
        if (!CanApplyLayerEffect || ActiveLayerId is not { } id || document?.IndexOf(id) is not (>= 0 and var index)) return;
        var layer = document.Layers[index];
        var image = layer.Asset!.Image;
        int padding = (int)Math.Ceiling(Math.Max(0, settings.Blur) * 1.5) + 2;
        var shadow = LayerEffects.Shadow(image, settings, padding);
        // The padded image keeps the layer's centre, scale, rotation and flips; the offset is in canvas pixels.
        var t = layer.Transform;
        double sx = t.Size.Width / image.Width, sy = t.Size.Height / image.Height;
        var size = new SizeD(shadow.Width * sx, shadow.Height * sy);
        var center = t.Center;
        var transform = t with { Size = size, Origin = new PointD(center.X - size.Width / 2 + settings.OffsetX, center.Y - size.Height / 2 + settings.OffsetY) };
        var shadowLayer = ImageLayer.FromAsset(PixelOps.Asset(shadow, layer.Name + " Shadow"), transform.Origin) with
        {
            Transform = transform, ParentId = layer.ParentId, BlendMode = LayerBlendMode.Multiply,
        };
        BeginEdit("Add Shadow");
        SetLayers(document.Layers.Insert(index, shadowLayer));
        EndEdit();
    }
}
