using System.Collections.Immutable;
using Compositor.Geometry;
using Compositor.Imaging;
using SkiaSharp;

namespace Compositor.Model;

public readonly record struct PaletteColor(double Red, double Green, double Blue)
{
    public static readonly PaletteColor Black = new(0, 0, 0);
    public static readonly PaletteColor White = new(1, 1, 1);
    public PaletteColor Quantized() => new(Math.Round(Red * 255) / 255, Math.Round(Green * 255) / 255, Math.Round(Blue * 255) / 255);
    public override string ToString() => $"#{Hex}";
    public byte R8 => (byte)Math.Round(Math.Clamp(Red, 0, 1) * 255, MidpointRounding.AwayFromZero);
    public byte G8 => (byte)Math.Round(Math.Clamp(Green, 0, 1) * 255, MidpointRounding.AwayFromZero);
    public byte B8 => (byte)Math.Round(Math.Clamp(Blue, 0, 1) * 255, MidpointRounding.AwayFromZero);
    public SKColor ToSK(byte alpha = 255) => new(R8, G8, B8, alpha);
    public string Hex => $"{R8:X2}{G8:X2}{B8:X2}";
    public static PaletteColor? FromHex(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return new PaletteColor(((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }
}

public enum ShapeKind { Rectangle, Ellipse, Line, Triangle, Polygon, Star }

public static class ShapeKinds
{
    /// <summary>Rectangles and ellipses are the Mac format's shapes; the others are Layer Form's own additions.</summary>
    public static bool IsMacShape(this ShapeKind k) => k is ShapeKind.Rectangle or ShapeKind.Ellipse;
    public static string Name(this ShapeKind k) => k.ToString();
    public static bool TryParse(string? value, out ShapeKind kind) => Enum.TryParse(value, false, out kind) && Enum.IsDefined(kind);
}

/// <summary>What a shape layer draws, kept so the shape can be drawn again at a new size (project v7 "shape").
/// <see cref="Sides"/> is the polygon's sides or the star's points; <see cref="Weight"/> and <see cref="Rising"/> describe a line.</summary>
public sealed record LayerShapeStyle(ShapeKind Kind, double Red, double Green, double Blue, double CornerRadius, int Sides = 5, double Weight = 4, bool Rising = false,
    ShapeCorners? Corners = null)
{
    public PaletteColor Color => new(Red, Green, Blue);

    /// <summary>The rounding of one corner: its own radius when set, otherwise the shared one.</summary>
    public double Radius(int corner) => Corners is { } c ? c[corner] : CornerRadius;

    /// <summary>Only the Mac format's shapes with one shared radius go in its "shape" key.</summary>
    public bool IsMacStyle => Kind.IsMacShape() && Corners == null;

    /// <summary>Radii and line weight scaled (to draw at another size).</summary>
    public LayerShapeStyle Scaled(double factor) => this with
    {
        CornerRadius = CornerRadius * factor, Weight = Weight * factor, Corners = Corners?.Scaled(factor),
    };
}

/// <summary>Separate corner radii, clockwise from the top-left (a rectangle) or from the top point (a triangle, which uses
/// the first three).</summary>
public readonly record struct ShapeCorners(double A, double B, double C, double D)
{
    public double this[int index] => index switch { 0 => A, 1 => B, 2 => C, _ => D };
    public ShapeCorners With(int index, double value) => index switch
    {
        0 => this with { A = value }, 1 => this with { B = value }, 2 => this with { C = value }, _ => this with { D = value },
    };
    public ShapeCorners Scaled(double k) => new(A * k, B * k, C * k, D * k);
    public static ShapeCorners All(double r) => new(r, r, r, r);
}

/// <summary>A layer made with the Shape tool; live while the layer's pixels are still <see cref="Image"/>.</summary>
public sealed record LayerShape(LayerShapeStyle Style, RasterImage Image)
{
    public static LayerShape? Loaded(LayerShapeStyle? style, RasterImage? image) => style is null || image is null ? null : new LayerShape(style, image);
    public bool Equals(LayerShape? other) => other is not null && Style == other.Style && ReferenceEquals(Image, other.Image);
    public override int GetHashCode() => Style.GetHashCode();
}

/// <summary>Immutable, normalized layer-local coverage (white reveals, black hides).</summary>
public sealed record LayerMask(MaskAsset Asset, bool IsEnabled = true, LayerTransform? Placement = null, bool IsLinked = true)
{
    public MaskImage? EnabledImage => IsEnabled ? Asset.Image : null;
    public LayerMask Replacing(MaskAsset asset) => this with { Asset = asset };
    public bool Equals(LayerMask? other) => other is not null && ReferenceEquals(Asset.Image, other.Asset.Image)
        && IsEnabled == other.IsEnabled && Placement == other.Placement && IsLinked == other.IsLinked;
    public override int GetHashCode() => HashCode.Combine(IsEnabled, IsLinked);

    public static LayerMask Solid(bool revealing)
    {
        var image = MaskImage.Solid(revealing ? (byte)255 : (byte)0);
        return new LayerMask(new MaskAsset(image, image));
    }

    /// <summary>Where the mask sits once its layer moves from <paramref name="old"/> to <paramref name="next"/>.</summary>
    public LayerTransform? PlacementMovingLayer(LayerTransform old, LayerTransform next)
    {
        if (Asset.Image.Width <= 1 && Asset.Image.Height <= 1) return null;
        LayerTransform? moved = IsLinked ? Placement?.Following(old, next) : (Placement ?? old);
        return moved is { } m && !m.SamePlacement(next) ? m : null;
    }
}

public sealed record ImageLayer
{
    public Guid Id { get; init; }
    public ImageAsset? Asset { get; init; }
    public LayerTransform Transform { get; init; }
    public string Name { get; init; } = "";
    public bool IsVisible { get; init; } = true;
    public Guid? ParentId { get; init; }
    public bool IsGroup { get; init; }
    public double Opacity { get; init; } = 1;
    public LayerBlendMode BlendMode { get; init; } = LayerBlendMode.Normal;
    public Guid? MaskSourceId { get; init; }
    public LayerMask? Mask { get; init; }
    public LayerAdjustment? Adjustment { get; init; }
    public LayerShape? Shape { get; init; }

    public SizeD Size => Transform.Size;
    public PointD Origin => Transform.Origin;
    /// <summary>Where the mask's pixels sit on the document: its own placement, else the layer's.</summary>
    public LayerTransform MaskTransform => Mask?.Placement ?? Transform;
    /// <summary>The shape this layer still is: null once its pixels were edited some other way.</summary>
    public LayerShape? LiveShape => Shape is { } s && Asset is { } a && ReferenceEquals(a.Image, s.Image) ? s : null;

    public static ImageLayer FromAsset(ImageAsset asset, PointD origin) => new()
    {
        Id = Guid.NewGuid(), Asset = asset, Name = asset.Name,
        Transform = new LayerTransform(origin, new SizeD(asset.Image.Width, asset.Image.Height)),
    };

    public static ImageLayer Blank(string name, SizeD size) => new()
    {
        Id = Guid.NewGuid(), Name = name, Transform = new LayerTransform(PointD.Zero, size),
    };

    public bool Equals(ImageLayer? other) => other is not null && Id == other.Id && Name == other.Name && IsVisible == other.IsVisible
        && Transform == other.Transform && ReferenceEquals(Asset?.Image, other.Asset?.Image) && ParentId == other.ParentId
        && IsGroup == other.IsGroup && Opacity == other.Opacity && BlendMode == other.BlendMode && Equals(Mask, other.Mask)
        && MaskSourceId == other.MaskSourceId && Equals(Adjustment, other.Adjustment) && Equals(Shape, other.Shape);
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>A document-space selection outline. Null on the document means no selection; an empty path is an explicit
/// empty selection, which edits treat as "touch nothing".</summary>
public sealed class DocumentSelection
{
    private readonly SKPath path;
    public bool Antialiased { get; }
    public DocumentSelection(SKPath path, bool antialiased = true)
    {
        this.path = new SKPath(path) { FillType = SKPathFillType.Winding };
        Antialiased = antialiased;
    }
    /// <summary>A copy of the outline (callers may change it).</summary>
    public SKPath Path => new(path);
    internal SKPath SharedPath => path;
    public RectD Bounds => path.IsEmpty ? RectD.Null : RectD.FromSK(path.Bounds);
    public bool IsEmpty => path.IsEmpty || Bounds.IsNull || Bounds.IsEmpty;
    public bool Contains(PointD p) => path.Contains((float)p.X, (float)p.Y);
    public DocumentSelection Transformed(Affine t)
    {
        var copy = new SKPath(path);
        copy.Transform(t.ToSK());
        return new DocumentSelection(copy, Antialiased);
    }
    public override bool Equals(object? obj) => obj is DocumentSelection other && Antialiased == other.Antialiased
        && (ReferenceEquals(path, other.path) || path.ToSvgPathData() == other.path.ToSvgPathData());
    public override int GetHashCode() => Antialiased.GetHashCode();
}

public sealed record CanvasDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Width { get; init; }
    public int Height { get; init; }
    public double Resolution { get; init; } = 72;
    /// <summary>Bottom to top.</summary>
    public ImmutableList<ImageLayer> Layers { get; init; } = ImmutableList<ImageLayer>.Empty;
    /// <summary>Part of the document so undo covers selection changes. Not saved to disk.</summary>
    public DocumentSelection? Selection { get; init; }
    public SizeD Size => new(Width, Height);

    public static int? ValidDimension(string value) => int.TryParse(value.Trim(), out var n) && n >= 1 && n <= 30_000 ? n : null;

    public int IndexOf(Guid id)
    {
        for (int i = 0; i < Layers.Count; i++) if (Layers[i].Id == id) return i;
        return -1;
    }
    public ImageLayer? Layer(Guid? id) => id is { } value ? Layers.FirstOrDefault(l => l.Id == value) : null;

    public bool Equals(CanvasDocument? other) => other is not null && Id == other.Id && Width == other.Width && Height == other.Height
        && Resolution == other.Resolution && Layers.SequenceEqual(other.Layers) && Equals(Selection, other.Selection);
    public override int GetHashCode() => HashCode.Combine(Id, Width, Height, Layers.Count);
}
