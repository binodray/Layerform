using Compositor.Geometry;

namespace Compositor.Model;

public enum LayerSampling { Nearest, Smooth, High }

public static class LayerSamplingNames
{
    public static string ToName(this LayerSampling s) => s switch
    {
        LayerSampling.Nearest => "Nearest",
        LayerSampling.Smooth => "Smooth",
        _ => "High quality",
    };
    public static bool TryParse(string? value, out LayerSampling sampling)
    {
        switch (value)
        {
            case "Nearest": sampling = LayerSampling.Nearest; return true;
            case "Smooth": sampling = LayerSampling.Smooth; return true;
            case "High quality": sampling = LayerSampling.High; return true;
            default: sampling = LayerSampling.High; return false;
        }
    }
    public static readonly LayerSampling[] All = { LayerSampling.Nearest, LayerSampling.Smooth, LayerSampling.High };
}

/// <summary>Unrotated bounds in document pixels; rotation is clockwise around their center (y down).</summary>
public readonly record struct LayerTransform(PointD Origin, SizeD Size, double Rotation = 0, bool FlipX = false, bool FlipY = false,
    LayerSampling Sampling = LayerSampling.High)
{
    public PointD Center => new(Origin.X + Size.Width / 2, Origin.Y + Size.Height / 2);
    /// <summary>Swift's <c>truncatingRemainder(dividingBy: 360)</c> keeps the sign, as C#'s % does.</summary>
    public double Radians => (Rotation % 360) * Math.PI / 180;

    public bool IsValid =>
        double.IsFinite(Origin.X) && double.IsFinite(Origin.Y) && double.IsFinite(Size.Width) && double.IsFinite(Size.Height) && double.IsFinite(Rotation)
        && Size.Width >= 1 && Size.Width <= 300_000 && Size.Height >= 1 && Size.Height <= 300_000
        && Math.Abs(Origin.X) <= 1_000_000 && Math.Abs(Origin.Y) <= 1_000_000;

    public PointD Point(PointD unit)
    {
        double x = (unit.X - 0.5) * Size.Width, y = (unit.Y - 0.5) * Size.Height;
        double r = Radians, cos = Math.Cos(r), sin = Math.Sin(r);
        var c = Center;
        return new PointD(c.X + x * cos - y * sin, c.Y + x * sin + y * cos);
    }

    public bool Contains(PointD point)
    {
        var c = Center;
        double x = point.X - c.X, y = point.Y - c.Y, r = Radians, cos = Math.Cos(r), sin = Math.Sin(r);
        return Math.Abs(x * cos + y * sin) <= Size.Width / 2 && Math.Abs(-x * sin + y * cos) <= Size.Height / 2;
    }

    /// <summary>Width as a percentage of the pixel size it places (100% draws them 1:1).</summary>
    public double ScalePercent(SizeD pixelSize) => Size.Width / Math.Max(1, pixelSize.Width) * 100;

    public LayerTransform ScaledToPercent(double percent, SizeD pixelSize)
    {
        var c = Center;
        var size = new SizeD(pixelSize.Width * percent / 100, pixelSize.Height * percent / 100);
        return this with { Size = size, Origin = new PointD(c.X - size.Width / 2, c.Y - size.Height / 2) };
    }

    /// <summary>Whole pixels and whole degrees: what dragging, scaling and rotating leave behind.</summary>
    public LayerTransform Rounded() => this with
    {
        Origin = new PointD(RoundSwift(Origin.X), RoundSwift(Origin.Y)),
        Size = new SizeD(Math.Max(1, RoundSwift(Size.Width)), Math.Max(1, RoundSwift(Size.Height))),
        Rotation = RoundSwift(Rotation),
    };

    /// <summary>Swift's default rounding (to nearest, ties away from zero).</summary>
    public static double RoundSwift(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>The unit square (0…1, y down) mapped where this transform places a layer on the document.</summary>
    public Affine UnitToDocument => PixelToDocument(this, 1, 1);

    /// <summary>A transform placing the unit square as <paramref name="map"/> does (shear dropped). Keeps sampling.</summary>
    public LayerTransform Placing(Affine map)
    {
        double sign = FlipX ? -1 : 1;
        double angle = Math.Atan2(map.B * sign, map.A * sign);
        double along = -map.C * Math.Sin(angle) + map.D * Math.Cos(angle);
        var middle = map.Apply(new PointD(0.5, 0.5));
        var size = new SizeD(Math.Sqrt(map.A * map.A + map.B * map.B), Math.Abs(along));
        double degrees = angle * 180 / Math.PI;
        return this with
        {
            Size = size,
            Rotation = degrees + RoundSwift((Rotation - degrees) / 360) * 360,
            FlipY = along < 0,
            Origin = new PointD(middle.X - size.Width / 2, middle.Y - size.Height / 2),
        };
    }

    /// <summary>This placement carried along as a layer moves from <paramref name="old"/> to <paramref name="next"/>.</summary>
    public LayerTransform Following(LayerTransform old, LayerTransform next)
    {
        if (old == next) return this;
        if (old.Size == next.Size && old.Rotation == next.Rotation && old.FlipX == next.FlipX && old.FlipY == next.FlipY)
            return this with { Origin = new PointD(Origin.X + next.Origin.X - old.Origin.X, Origin.Y + next.Origin.Y - old.Origin.Y) };
        return Placing(UnitToDocument.Concat(old.UnitToDocument.Inverted()).Concat(next.UnitToDocument));
    }

    public bool SamePlacement(LayerTransform other) => (this with { Sampling = other.Sampling }) == other;

    public static readonly PointD[] Handles =
    {
        new(0, 0), new(0.5, 0), new(1, 0), new(1, 0.5), new(1, 1), new(0.5, 1), new(0, 1), new(0, 0.5),
    };

    /// <summary>Maps an image's top-left pixel grid (width × height) onto the document (BrushRaster.pixelToDocument).</summary>
    public static Affine PixelToDocument(LayerTransform t, int width, int height) =>
        Affine.Translation(t.Center.X, t.Center.Y)
            .Rotated(t.Radians)
            .Scaled(t.Size.Width / width * (t.FlipX ? -1 : 1), t.Size.Height / height * (t.FlipY ? -1 : 1))
            .Translated(-(double)width / 2, -(double)height / 2);

    public static LayerTransform Canvas(double width, double height) => new(PointD.Zero, new SizeD(width, height));

    /// <summary>The corners in handle order: top-left, top-right, bottom-right, bottom-left.</summary>
    public PointD[] Corners() => new[] { Point(new(0, 0)), Point(new(1, 0)), Point(new(1, 1)), Point(new(0, 1)) };

    /// <summary>This placement mirrored across a vertical (or horizontal) line at <paramref name="axis"/>.</summary>
    public LayerTransform Mirrored(bool horizontally, double axis)
    {
        var c = Center;
        if (horizontally)
            return this with { FlipX = !FlipX, Origin = new PointD(2 * axis - c.X - Size.Width / 2, Origin.Y), Rotation = -Rotation };
        return this with { FlipY = !FlipY, Origin = new PointD(Origin.X, 2 * axis - c.Y - Size.Height / 2), Rotation = -Rotation };
    }
}
