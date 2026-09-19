using Compositor.Geometry;

namespace Compositor.Editing;

/// <summary>
/// Document: pixels with a top-left origin. View: device-independent pixels (points). Zoom 1 means one document pixel
/// per physical display pixel (CanvasViewport.swift).
/// </summary>
public record struct CanvasViewport
{
    public SizeD ViewSize { get; set; }
    public double BackingScale { get; set; } = 1;
    public double Zoom { get; private set; } = 1;
    public SizeD Pan { get; set; }
    public bool FollowsFit { get; private set; } = true;
    public const double MinZoom = 0.001, MaxZoom = 32;

    public CanvasViewport() { }

    public double PointsPerPixel => Zoom / BackingScale;
    public PointD Center => new(ViewSize.Width / 2, ViewSize.Height / 2);

    public RectD DocumentRect(SizeD size)
    {
        double w = size.Width * PointsPerPixel, h = size.Height * PointsPerPixel;
        return new RectD(Center.X - w / 2 + Pan.Width, Center.Y - h / 2 + Pan.Height, w, h);
    }

    public PointD DocumentPoint(PointD point, SizeD documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return new PointD((point.X - origin.X) / PointsPerPixel, (point.Y - origin.Y) / PointsPerPixel);
    }

    public PointD ViewPoint(PointD point, SizeD documentSize)
    {
        var origin = DocumentRect(documentSize).Origin;
        return new PointD(origin.X + point.X * PointsPerPixel, origin.Y + point.Y * PointsPerPixel);
    }

    public void Fit(SizeD documentSize)
    {
        if (!(ViewSize.Width > 0) || !(ViewSize.Height > 0)) { FollowsFit = true; return; }
        Zoom = Clamp(Math.Min(Math.Max(1, ViewSize.Width - 96) / documentSize.Width, Math.Max(1, ViewSize.Height - 96) / documentSize.Height) * BackingScale);
        Pan = SizeD.Zero;
        FollowsFit = true;
    }

    public void Resize(SizeD size, double backingScale, SizeD? documentSize)
    {
        double old = PointsPerPixel;
        ViewSize = size;
        BackingScale = Math.Max(0.5, backingScale);
        if (FollowsFit && documentSize is { } doc) Fit(doc);
        else
        {
            double ratio = PointsPerPixel / old;
            Pan = new SizeD(Pan.Width * ratio, Pan.Height * ratio);
        }
    }

    public void SetZoom(double value, PointD anchor, SizeD documentSize)
    {
        if (!double.IsFinite(value)) return;
        var pixel = DocumentPoint(anchor, documentSize);
        Zoom = Clamp(value);
        var moved = ViewPoint(pixel, documentSize);
        Pan = new SizeD(Pan.Width + anchor.X - moved.X, Pan.Height + anchor.Y - moved.Y);
        FollowsFit = false;
    }

    public void Translate(SizeD delta)
    {
        Pan = new SizeD(Pan.Width + delta.Width, Pan.Height + delta.Height);
        FollowsFit = false;
    }

    private static double Clamp(double value) => Math.Min(MaxZoom, Math.Max(MinZoom, value));
}
