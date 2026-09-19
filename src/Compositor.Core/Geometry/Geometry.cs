using SkiaSharp;

namespace Compositor.Geometry;

/// <summary>A point in double precision (document pixels unless noted), top-left origin, y down.</summary>
public readonly record struct PointD(double X, double Y)
{
    public static readonly PointD Zero = new(0, 0);
    public PointD Offset(double dx, double dy) => new(X + dx, Y + dy);
    public PointD Apply(Affine t) => t.Apply(this);
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public double DistanceTo(PointD other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public SKPoint ToSK() => new((float)X, (float)Y);
    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

public readonly record struct SizeD(double Width, double Height)
{
    public static readonly SizeD Zero = new(0, 0);
    public override string ToString() => $"{Width:0.###} x {Height:0.###}";
}

/// <summary>A rectangle with Core Graphics semantics: <c>Null</c> for empty intersections, standardized sizes.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static readonly RectD Zero = new(0, 0, 0, 0);
    /// <summary>Core Graphics' null rectangle, the result of intersecting disjoint rectangles.</summary>
    public static readonly RectD Null = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);
    public RectD(PointD origin, SizeD size) : this(origin.X, origin.Y, size.Width, size.Height) { }
    public bool IsNull => double.IsPositiveInfinity(X) || double.IsPositiveInfinity(Y);
    public bool IsEmpty => IsNull || Width <= 0 || Height <= 0;
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public PointD Origin => new(X, Y);
    public SizeD Size => new(Width, Height);
    public PointD Center => new(MidX, MidY);

    public RectD Standardized
    {
        get
        {
            double x = X, y = Y, w = Width, h = Height;
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            return new RectD(x, y, w, h);
        }
    }

    public RectD Intersect(RectD other)
    {
        if (IsNull || other.IsNull) return Null;
        var a = Standardized; var b = other.Standardized;
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        double x1 = Math.Min(a.MaxX, b.MaxX), y1 = Math.Min(a.MaxY, b.MaxY);
        if (x1 < x0 || y1 < y0) return Null;
        return new RectD(x0, y0, x1 - x0, y1 - y0);
    }

    public bool Intersects(RectD other)
    {
        var r = Intersect(other);
        return !r.IsNull && r.Width > 0 && r.Height > 0;
    }

    public RectD Union(RectD other)
    {
        if (IsNull) return other;
        if (other.IsNull) return this;
        var a = Standardized; var b = other.Standardized;
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        double x1 = Math.Max(a.MaxX, b.MaxX), y1 = Math.Max(a.MaxY, b.MaxY);
        return new RectD(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The smallest whole-pixel rectangle containing this one (CGRectIntegral).</summary>
    public RectD Integral
    {
        get
        {
            if (IsNull) return this;
            var r = Standardized;
            double x0 = Math.Floor(r.X), y0 = Math.Floor(r.Y);
            double x1 = Math.Ceiling(r.MaxX), y1 = Math.Ceiling(r.MaxY);
            return new RectD(x0, y0, x1 - x0, y1 - y0);
        }
    }

    public RectD Inset(double dx, double dy)
    {
        if (IsNull) return this;
        var r = Standardized;
        var result = new RectD(r.X + dx, r.Y + dy, r.Width - dx * 2, r.Height - dy * 2);
        return result.Width < 0 || result.Height < 0 ? Null : result;
    }

    public RectD OffsetBy(double dx, double dy) => IsNull ? this : new RectD(X + dx, Y + dy, Width, Height);

    public bool Contains(PointD p) => !IsNull && p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;

    /// <summary>The upright bounds of this rectangle after an affine transform (CGRectApplyAffineTransform).</summary>
    public RectD Apply(Affine t)
    {
        if (IsNull) return this;
        var r = Standardized;
        var p0 = t.Apply(new PointD(r.MinX, r.MinY));
        var p1 = t.Apply(new PointD(r.MaxX, r.MinY));
        var p2 = t.Apply(new PointD(r.MaxX, r.MaxY));
        var p3 = t.Apply(new PointD(r.MinX, r.MaxY));
        double x0 = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        double y0 = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        double x1 = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        double y1 = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new RectD(x0, y0, x1 - x0, y1 - y0);
    }

    public SKRect ToSK() => new((float)MinX, (float)MinY, (float)MaxX, (float)MaxY);
    public SKRectI ToSKI() => new((int)MinX, (int)MinY, (int)MaxX, (int)MaxY);
    public static RectD FromSK(SKRect r) => new(r.Left, r.Top, r.Width, r.Height);
    public override string ToString() => IsNull ? "null" : $"[{X:0.###}, {Y:0.###}, {Width:0.###} x {Height:0.###}]";
}

/// <summary>
/// An affine transform with Core Graphics semantics: x' = a·x + c·y + tx, y' = b·x + d·y + ty.
/// <c>Translated</c>, <c>Rotated</c> and <c>Scaled</c> prepend (the new step applies to points first), exactly as
/// CGAffineTransformTranslate/Rotate/Scale do; <c>Concat(other)</c> applies this first, then <c>other</c>.
/// </summary>
public readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);
    public static Affine Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    public static Affine Rotation(double radians) => new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);

    public PointD Apply(PointD p) => new(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);

    /// <summary>This transform followed by <paramref name="t"/> (CGAffineTransformConcat(self, t)).</summary>
    public Affine Concat(Affine t) => new(
        A * t.A + B * t.C, A * t.B + B * t.D,
        C * t.A + D * t.C, C * t.B + D * t.D,
        Tx * t.A + Ty * t.C + t.Tx, Tx * t.B + Ty * t.D + t.Ty);

    public Affine Translated(double x, double y) => Translation(x, y).Concat(this);
    public Affine Rotated(double radians) => Rotation(radians).Concat(this);
    public Affine Scaled(double x, double y) => Scale(x, y).Concat(this);

    public double Determinant => A * D - B * C;

    public Affine Inverted()
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-300) return this; // CGAffineTransformInvert returns the input when singular.
        return new Affine(D / det, -B / det, -C / det, A / det, (C * Ty - D * Tx) / det, (B * Tx - A * Ty) / det);
    }

    public SKMatrix ToSK() => new((float)A, (float)C, (float)Tx, (float)B, (float)D, (float)Ty, 0, 0, 1);
    public static Affine FromSK(SKMatrix m) => new(m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY);
}
