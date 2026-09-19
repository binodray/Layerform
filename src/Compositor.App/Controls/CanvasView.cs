using System.Numerics;
using Compositor.Editing;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using WinColor = Windows.UI.Color;

using SelectionMode = Compositor.Editing.SelectionMode;

namespace Compositor.App.Controls;

/// <summary>
/// The editor canvas (EditorCanvas.swift): the composite rendered by the Skia compositor and presented with Win2D, the
/// overlays drawn on top, and every tool's pointer and keyboard behaviour.
/// </summary>
public sealed class CanvasView : UserControl
{
    private readonly CanvasControl canvas;
    private EditorSession? session;
    public EditorSession? Session
    {
        get => session;
        set
        {
            if (session == value) return;
            if (session != null) session.CanvasChanged -= OnCanvasChanged;
            session = value;
            if (session != null) session.CanvasChanged += OnCanvasChanged;
            composite = null;
            SyncGeometry();
            canvas.Invalidate();
        }
    }

    public event Action? RequestFocusRestore;
    public event Action<Windows.ApplicationModel.DataTransfer.DataPackageView, PointD?>? DropReceived;
    public Func<Task>? BeforePaste;
    /// <summary>A right-click (or Shift+F10) on the canvas with a tool that has no right-drag of its own.</summary>
    public event Action<Point>? ContextMenuRequested;

    // Rendered composite cache.
    private CanvasBitmap? composite;
    private SKBitmap? compositePixels;
    private (int Revision, CanvasViewport Viewport, double Width, double Height, Guid? Document, RectD Bounds)? compositeKey;
    private Rect compositeDest;
    private bool compositeCrisp;
    private Affine compositeMapping;
    private RectD compositeRegion;
    private int lastStrokeRevision = -1;

    private readonly DispatcherTimer antsTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer autoscrollTimer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private float antsPhase;

    // Interaction state (the Mac CanvasView's private properties).
    private bool spaceHeld;
    private Point? brushPointer;
    private Point? lastDragPoint;
    private PointD? brushAxisAnchor;
    private bool? brushAxisHorizontal;
    private PointD? brushLastPixel;
    private TransformDrag? transformDrag;
    private CropDrag? cropDrag;
    private CropSnap? cropSnap;
    private bool duplicatesTransformOnDrag;
    private PointD? selectionDragStart;
    private PointD? pixelDragStart;
    private bool marqueeConstrainArmed = true;
    private PointD? marqueeDragPixel;
    private Point? autoscrollPoint;
    private (Point Start, double Zoom, bool Moved)? zoomDrag;
    private (Point Start, double Diameter, double Hardness, bool HardnessShown)? brushTipDrag;
    private enum GradientHandle { Start, End }
    private GradientHandle? gradientDrag;
    private Point? hueTargetStart;
    private bool samplingColor;
    private PaletteColor samplingOriginal = PaletteColor.Black;
    private Point? sampleRingPoint;
    private uint? capturedPointer;
    private DateTime lastClick = DateTime.MinValue;
    private Point lastClickPoint;

    public CanvasView()
    {
        canvas = new CanvasControl { ClearColor = WinColor.FromArgb(255, 0x1B, 0x1B, 0x1B) };
        canvas.Draw += OnDraw;
        Content = canvas;
        IsTabStop = true;
        AllowFocusOnInteraction = true;
        UseSystemFocusVisuals = false;
        AllowDrop = true;
        SizeChanged += (_, _) => { SyncGeometry(); canvas.Invalidate(); };
        Loaded += (_, _) =>
        {
            SyncGeometry();
            if (XamlRoot != null) XamlRoot.Changed += (_, _) => { SyncGeometry(); canvas.Invalidate(); };
        };
        Unloaded += (_, _) => { antsTimer.Stop(); autoscrollTimer.Stop(); };
        antsTimer.Tick += (_, _) => { antsPhase = (antsPhase + 1) % 8; canvas.Invalidate(); };
        autoscrollTimer.Tick += (_, _) => StepAutoscroll();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, e) => EndCapture(e);
        PointerCaptureLost += (_, _) => capturedPointer = null;
        PointerExited += (_, _) => { brushPointer = null; canvas.Invalidate(); };
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
        ContextRequested += (_, e) =>
        {
            if (session?.Document == null || session.Tool.IsBrushTool() || session.BrushStroke != null) return;
            var at = e.TryGetPosition(this, out var p) ? p : new Point(ActualWidth / 2, ActualHeight / 2);
            e.Handled = true;
            ContextMenuRequested?.Invoke(at);
        };
        KeyUp += OnKeyUp;
        LostFocus += (_, _) => OnFocusLost();
        DragOver += (_, e) => { e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; e.DragUIOverride.Caption = "Add to canvas"; };
        Drop += (_, e) =>
        {
            PointD? point = null;
            if (session?.Document is { } doc) point = session.Viewport.DocumentPoint(ToPoint(e.GetPosition(this)), doc.Size);
            DropReceived?.Invoke(e.DataView, point);
        };
    }

    private static PointD ToPoint(Point p) => new(p.X, p.Y);
    private double Scale => XamlRoot?.RasterizationScale ?? 1;

    private void SyncGeometry()
    {
        if (session == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var size = new SizeD(ActualWidth, ActualHeight);
        if (session.Viewport.ViewSize != size || session.Viewport.BackingScale != Scale)
            session.Viewport.Resize(size, Scale, session.Document?.Size);
    }

    private void OnCanvasChanged()
    {
        UpdateAntsTimer();
        canvas.Invalidate();
    }

    public void Refresh()
    {
        UpdateAntsTimer();
        if (brushPointer is { } pointer) UpdateCursor(pointer);
        canvas.Invalidate();
    }

    private void UpdateAntsTimer()
    {
        bool active = session?.DisplayedSelection is { IsEmpty: false };
        if (active && !antsTimer.IsEnabled) antsTimer.Start();
        else if (!active && antsTimer.IsEnabled) antsTimer.Stop();
    }

    // MARK: Drawing

    /// <summary>Up to 400% the canvas is smoothed, so anti-aliased edges look smooth; from there it shows hard-edged document
    /// pixels, and the pixel grid appears from 800%.</summary>
    public const double CrispZoom = 4, PixelGridZoom = 8;

    private RectD? RenderBounds
    {
        get
        {
            if (session?.Document is not { } doc) return null;
            var original = new RectD(0, 0, doc.Width, doc.Height);
            return session.Tool == NavigationTool.Crop ? original.Union(session.CropRect ?? original) : original;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (session?.Document is not { } doc) return;
        var viewport = session.Viewport;
        var pixels = RenderBounds ?? new RectD(0, 0, doc.Width, doc.Height);
        var origin = viewport.ViewPoint(pixels.Origin, doc.Size);
        var rect = new Rect(origin.X, origin.Y, pixels.Width * viewport.PointsPerPixel, pixels.Height * viewport.PointsPerPixel);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var visible = Intersect(rect, bounds);
        // Shadow, then the canvas plate and the transparency checkerboard.
        for (int i = 6; i >= 1; i--)
            ds.FillRectangle(new Rect(rect.X - i, rect.Y - i + 3, rect.Width + i * 2, rect.Height + i * 2), WinColor.FromArgb((byte)(10 - i), 0, 0, 0));
        ds.FillRectangle(rect, WinColor.FromArgb(255, 0x4D, 0x4D, 0x4D));
        if (visible is { } v)
        {
            using (ds.CreateLayer(1f, v))
            {
                const double tile = 10;
                int minX = (int)Math.Floor((v.X - rect.X) / tile), maxX = (int)Math.Ceiling((v.Right - rect.X) / tile);
                int minY = (int)Math.Floor((v.Y - rect.Y) / tile), maxY = (int)Math.Ceiling((v.Bottom - rect.Y) / tile);
                var light = WinColor.FromArgb(255, 0x59, 0x59, 0x59);
                for (int row = minY; row < maxY; row++)
                    for (int column = minX; column < maxX; column++)
                        if ((row + column) % 2 == 0) ds.FillRectangle(new Rect(rect.X + column * tile, rect.Y + row * tile, tile, tile), light);
            }
            EnsureComposite(sender, doc, rect, v);
            if (composite != null)
            {
                using (ds.CreateLayer(1f, v))
                    ds.DrawImage(composite, compositeDest, composite.Bounds, 1f,
                        compositeCrisp ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear);
            }
            if (session.ShowsPixelGrid && viewport.Zoom >= PixelGridZoom) DrawPixelGrid(ds, doc, v);
        }
        ds.DrawRectangle(rect, WinColor.FromArgb(33, 255, 255, 255), (float)(1 / Scale));
        DrawOverlays(ds, doc);
    }

    private static Rect? Intersect(Rect a, Rect b)
    {
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary>Renders what is on screen: at 1:1 and enlarged without smoothing from 200%, else at display resolution.</summary>
    private void EnsureComposite(ICanvasResourceCreator creator, CanvasDocument doc, Rect rect, Rect visible)
    {
        var s = session!;
        (int Revision, CanvasViewport Viewport, double Width, double Height, Guid? Document, RectD Bounds) key = (s.BrushRevision, s.Viewport, ActualWidth, ActualHeight, doc.Id, RenderBounds ?? new RectD(0, 0, doc.Width, doc.Height));
        if (composite != null && compositeKey == key) return;
        // A brush stroke only changes what it touched: re-render that part into the cached composite.
        bool sameGeometry = compositeKey is { } old && old.Viewport == key.Viewport && old.Width == key.Width && old.Height == key.Height
            && old.Document == key.Document && old.Bounds == key.Bounds;
        if (sameGeometry && composite != null && compositePixels != null && s.BrushStroke is { DirtyDocumentRect: { } dirty } stroke
            && stroke.Revision != lastStrokeRevision && TryPatch(dirty))
        {
            lastStrokeRevision = stroke.Revision;
            compositeKey = key;
            return;
        }
        compositeKey = key;
        var viewport = s.Viewport;
        double scale = Scale;
        try
        {
            if (viewport.Zoom >= CrispZoom)
            {
                var topLeft = viewport.DocumentPoint(new PointD(visible.X, visible.Y), doc.Size);
                var bottomRight = viewport.DocumentPoint(new PointD(visible.Right, visible.Bottom), doc.Size);
                var region = new RectD(Math.Floor(topLeft.X), Math.Floor(topLeft.Y), Math.Ceiling(bottomRight.X) - Math.Floor(topLeft.X),
                    Math.Ceiling(bottomRight.Y) - Math.Floor(topLeft.Y)).Intersect((RenderBounds ?? new RectD(0, 0, doc.Width, doc.Height)).Integral);
                if (region.IsNull || region.Width < 1 || region.Height < 1) { composite = null; return; }
                compositeMapping = Affine.Translation(-region.X, -region.Y);
                compositeRegion = region;
                Render(creator, (int)region.Width, (int)region.Height, 96);
                var o = viewport.ViewPoint(region.Origin, doc.Size);
                compositeDest = new Rect(o.X, o.Y, region.Width * viewport.PointsPerPixel, region.Height * viewport.PointsPerPixel);
                compositeCrisp = true;
            }
            else
            {
                int dx = (int)Math.Floor(visible.X * scale), dy = (int)Math.Floor(visible.Y * scale);
                int dw = (int)Math.Ceiling(visible.Right * scale) - dx, dh = (int)Math.Ceiling(visible.Bottom * scale) - dy;
                if (dw < 1 || dh < 1) { composite = null; return; }
                var documentRect = viewport.DocumentRect(doc.Size);
                double pp = viewport.PointsPerPixel * scale;
                compositeMapping = Affine.Scale(pp, pp).Concat(Affine.Translation(documentRect.X * scale - dx, documentRect.Y * scale - dy));
                compositeRegion = new RectD(dx, dy, dw, dh);
                Render(creator, dw, dh, (float)(96 * scale));
                compositeDest = new Rect(dx / scale, dy / scale, dw / scale, dh / scale);
                compositeCrisp = false;
            }
        }
        catch (Exception e) { Diagnostics.Log("Render failed: " + e); composite = null; }
        lastStrokeRevision = s.BrushStroke?.Revision ?? -1;
    }

    private void Render(ICanvasResourceCreator creator, int width, int height, float dpi)
    {
        compositePixels?.Dispose();
        compositePixels = PixelOps.NewRgba(width, height);
        using (var surface = new RenderSurface(compositePixels, compositeMapping))
            session!.RenderCanvas(surface);
        composite?.Dispose();
        composite = CanvasBitmap.CreateFromBytes(creator, compositePixels.GetPixelSpan().ToArray(), width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized, dpi, CanvasAlphaMode.Premultiplied);
    }

    /// <summary>Re-renders only the part of the composite a stroke touched.</summary>
    private bool TryPatch(RectD dirtyDocument)
    {
        if (compositePixels == null || composite == null) return false;
        var device = dirtyDocument.Inset(-2, -2).Apply(compositeMapping).Integral.Intersect(new RectD(0, 0, compositePixels.Width, compositePixels.Height));
        if (device.IsNull || device.IsEmpty) return true;
        int x = (int)device.X, y = (int)device.Y, w = (int)device.Width, h = (int)device.Height;
        if ((long)w * h > (long)compositePixels.Width * compositePixels.Height / 2) return false;
        using var part = PixelOps.NewRgba(w, h);
        using (var surface = new RenderSurface(part, compositeMapping.Concat(Affine.Translation(-x, -y))))
            session!.RenderCanvas(surface);
        var source = part.GetPixelSpan();
        var target = compositePixels.GetPixelSpan();
        var bytes = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            source.Slice(row * part.RowBytes, w * 4).CopyTo(target.Slice((y + row) * compositePixels.RowBytes + x * 4));
            source.Slice(row * part.RowBytes, w * 4).CopyTo(bytes.AsSpan(row * w * 4));
        }
        composite.SetPixelBytes(bytes, x, y, w, h);
        return true;
    }

    private void DrawPixelGrid(CanvasDrawingSession ds, CanvasDocument doc, Rect visible)
    {
        var viewport = session!.Viewport;
        var first = viewport.DocumentPoint(new PointD(visible.X, visible.Y), doc.Size);
        var last = viewport.DocumentPoint(new PointD(visible.Right, visible.Bottom), doc.Size);
        var color = WinColor.FromArgb(115, 140, 140, 140);
        float hairline = (float)(1 / Scale);
        for (int column = (int)Math.Ceiling(first.X); column <= (int)Math.Floor(last.X); column++)
        {
            float x = (float)viewport.ViewPoint(new PointD(column, 0), doc.Size).X;
            ds.FillRectangle(x - hairline / 2, (float)visible.Y, hairline, (float)visible.Height, color);
        }
        for (int row = (int)Math.Ceiling(first.Y); row <= (int)Math.Floor(last.Y); row++)
        {
            float y = (float)viewport.ViewPoint(new PointD(0, row), doc.Size).Y;
            ds.FillRectangle((float)visible.X, y - hairline / 2, (float)visible.Width, hairline, color);
        }
    }

    // MARK: Overlays (TransformOverlay.swift, BrushCursorOverlay.swift, SampleRingOverlay.swift)

    private static WinColor Accent => (WinColor)Application.Current.Resources["SystemAccentColorLight2"];
    private Vector2 View(CanvasDocument doc, PointD p)
    {
        var v = session!.Viewport.ViewPoint(p, doc.Size);
        return new Vector2((float)v.X, (float)v.Y);
    }

    private Matrix3x2 DocumentToView(CanvasDocument doc)
    {
        var origin = session!.Viewport.DocumentRect(doc.Size).Origin;
        float scale = (float)session.Viewport.PointsPerPixel;
        return Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation((float)origin.X, (float)origin.Y);
    }

    private CanvasGeometry? selectionGeometry;
    private object? selectionGeometrySource;

    private CanvasGeometry? SelectionGeometry(ICanvasResourceCreator creator, DocumentSelection selection)
    {
        if (ReferenceEquals(selectionGeometrySource, selection) && selectionGeometry != null) return selectionGeometry;
        selectionGeometry?.Dispose();
        selectionGeometrySource = selection;
        selectionGeometry = PathGeometry(creator, selection.Path);
        return selectionGeometry;
    }

    private static CanvasGeometry PathGeometry(ICanvasResourceCreator creator, SKPath path)
    {
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        bool open = false;
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (open) builder.EndFigure(CanvasFigureLoop.Open);
                    builder.BeginFigure(points[0].X, points[0].Y);
                    open = true;
                    break;
                case SKPathVerb.Line: builder.AddLine(points[1].X, points[1].Y); break;
                case SKPathVerb.Quad: builder.AddQuadraticBezier(new Vector2(points[1].X, points[1].Y), new Vector2(points[2].X, points[2].Y)); break;
                case SKPathVerb.Conic:
                {
                    // Approximate the conic with a quadratic (its control point); conics only come from ovals here.
                    float w = iterator.ConicWeight();
                    var c = new Vector2(points[1].X, points[1].Y);
                    var p0 = new Vector2(points[0].X, points[0].Y);
                    var p2 = new Vector2(points[2].X, points[2].Y);
                    var c1 = p0 + (c - p0) * (2 * w / (1 + w)) * 0.75f;
                    var c2 = p2 + (c - p2) * (2 * w / (1 + w)) * 0.75f;
                    builder.AddCubicBezier(c1, c2, p2);
                    break;
                }
                case SKPathVerb.Cubic:
                    builder.AddCubicBezier(new Vector2(points[1].X, points[1].Y), new Vector2(points[2].X, points[2].Y), new Vector2(points[3].X, points[3].Y));
                    break;
                case SKPathVerb.Close:
                    if (open) builder.EndFigure(CanvasFigureLoop.Closed);
                    open = false;
                    break;
            }
        }
        if (open) builder.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }

    private void DrawOverlays(CanvasDrawingSession ds, CanvasDocument doc)
    {
        var s = session!;
        if (s.Tool == NavigationTool.Crop) DrawCrop(ds, doc);
        else if (GradientLine(doc) is { } line) DrawGradientLine(ds, line);
        else DrawTransformHandles(ds, doc);
        DrawSelection(ds, doc);
        DrawLassoDraft(ds, doc);
        DrawShapeDraft(ds, doc);
        DrawSnapGuides(ds, doc);
        DrawBrushCursor(ds, doc);
        DrawTransformRotationCursor(ds);
        DrawZoomCursor(ds);
        DrawSampleRing(ds);
    }

    private void DrawSnapGuides(CanvasDrawingSession ds, CanvasDocument doc)
    {
        var guides = session!.SnapGuides;
        foreach (var x in guides.Xs) ds.DrawLine(View(doc, new PointD(x, 0)), View(doc, new PointD(x, doc.Height)), Accent, 1);
        foreach (var y in guides.Ys) ds.DrawLine(View(doc, new PointD(0, y)), View(doc, new PointD(doc.Width, y)), Accent, 1);
    }

    private void DrawSelection(CanvasDrawingSession ds, CanvasDocument doc)
    {
        if (session!.DisplayedSelection is not { IsEmpty: false } selection) return;
        var geometry = SelectionGeometry(ds, selection);
        if (geometry == null) return;
        var old = ds.Transform;
        var transformed = geometry.Transform(DocumentToView(doc));
        ds.DrawGeometry(transformed, Colors.White, 1);
        using var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 4f, 4f }, DashOffset = antsPhase };
        ds.DrawGeometry(transformed, Colors.Black, 1, dash);
        transformed.Dispose();
        ds.Transform = old;
    }

    private void DrawShapeDraft(CanvasDrawingSession ds, CanvasDocument doc)
    {
        if (session!.ShapeDraft is not { } draft || draft.Rect.IsEmpty) return;
        var a = View(doc, draft.Rect.Origin);
        var b = View(doc, new PointD(draft.Rect.MaxX, draft.Rect.MaxY));
        var fg = session.ForegroundColor;
        var color = WinColor.FromArgb(255, fg.R8, fg.G8, fg.B8);
        double scale = session.Viewport.PointsPerPixel;
        // The same outline the layer will get, laid out in view points.
        var style = draft.Style(fg).Scaled(scale);
        using var path = EditorSession.ShapePath(style, new RectD(a.X, a.Y, b.X - a.X, b.Y - a.Y));
        using var geometry = PathGeometry(ds, path);
        ds.FillGeometry(geometry, color);
        ds.DrawGeometry(geometry, WinColor.FromArgb(153, 0, 0, 0), 1);
    }

    private void DrawLassoDraft(CanvasDrawingSession ds, CanvasDocument doc)
    {
        if (session!.LassoDraft is not { } draft) return;
        var points = draft.Points.Select(p => View(doc, p)).ToList();
        if (draft.Kind == LassoKind.Polygonal && draft.Cursor is { } cursor) points.Add(View(doc, cursor));
        if (points.Count == 0) return;
        using var builder = new CanvasPathBuilder(ds);
        if (draft.Kind == LassoKind.Ellipse && points.Count == 4)
        {
            float x0 = points.Min(p => p.X), y0 = points.Min(p => p.Y), x1 = points.Max(p => p.X), y1 = points.Max(p => p.Y);
            using var ellipse = CanvasGeometry.CreateEllipse(ds, (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2, (y1 - y0) / 2);
            ds.DrawGeometry(ellipse, WinColor.FromArgb(204, 0, 0, 0), 2);
            ds.DrawGeometry(ellipse, Colors.White, 1);
            return;
        }
        builder.BeginFigure(points[0]);
        foreach (var p in points.Skip(1)) builder.AddLine(p);
        builder.EndFigure(draft.Kind == LassoKind.Rectangle ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, WinColor.FromArgb(204, 0, 0, 0), 2);
        ds.DrawGeometry(geometry, Colors.White, 1);
        if (draft.Kind == LassoKind.Polygonal)
        {
            var first = points[0];
            ds.FillRectangle(first.X - 4, first.Y - 4, 8, 8, Colors.White);
            ds.DrawRectangle(first.X - 4, first.Y - 4, 8, 8, Colors.Black, 1);
        }
    }

    // Transform handles

    public sealed record OverlayGeometry(Vector2[] Handles, Vector2 RotationHandle, bool ShowsRotation)
    {
        public DragMode? Hit(Vector2 point)
        {
            bool Near(Vector2 other) => Vector2.Distance(point, other) <= 10;
            if (ShowsRotation && Near(RotationHandle)) return DragMode.RotateMode;
            if (ShowsRotation)
            {
                // Rotate from just outside any corner, while keeping the inner
                // ten pixels available to the resize handle.
                var center = (Handles[0] + Handles[2] + Handles[4] + Handles[6]) / 4;
                foreach (int i in new[] { 0, 2, 4, 6 })
                {
                    var corner = Handles[i];
                    var offset = point - corner;
                    float distance = offset.Length();
                    var outward = corner - center;
                    if (distance > 10 && distance <= 28 && outward.LengthSquared() > 0
                        && Vector2.Dot(offset, Vector2.Normalize(outward)) > distance * 0.15f)
                        return DragMode.RotateMode;
                }
            }
            for (int i = 0; i < Handles.Length; i++) if (Near(Handles[i])) return DragMode.Resize(i);
            foreach (var (start, end, handle) in new[] { (0, 2, 1), (2, 4, 3), (4, 6, 5), (6, 0, 7) })
            {
                var a = Handles[start]; var b = Handles[end];
                var d = b - a;
                float lengthSquared = d.LengthSquared();
                if (lengthSquared <= 0) continue;
                float t = Vector2.Dot(point - a, d) / lengthSquared;
                if (t >= 0 && t <= 1 && Vector2.Distance(point, a + d * t) <= 10) return DragMode.Resize(handle);
            }
            return null;
        }
    }

    public OverlayGeometry? TransformGeometry
    {
        get
        {
            var s = session;
            if (s?.Document is not { } doc || s.Tool != NavigationTool.Move || !(s.ShowsTransformControls || s.TransformEditState?.Persistent == true)) return null;
            if (s.TransformEditState?.Group != null || (s.TransformEditState == null && s.TransformsAsGroup))
            {
                if (s.TransformEditState?.Corners is { } groupCorners) return FromCorners(doc, groupCorners);
                if ((s.TransformEditState?.Draft ?? s.GroupTransformBox) is not { } box) return null;
                return FromTransform(doc, box);
            }
            if (s.ActiveLayer is not { Asset: not null, IsGroup: false } layer || !doc.EffectiveVisibleIds().Contains(layer.Id)) return null;
            if (s.TransformEditState is { } edit && edit.LayerId == layer.Id && edit.Corners is { } corners) return FromCorners(doc, corners);
            return FromTransform(doc, s.EditedTransform(layer));
        }
    }

    private OverlayGeometry FromTransform(CanvasDocument doc, LayerTransform transform)
    {
        var handles = LayerTransform.Handles.Select(h => View(doc, transform.Point(h))).ToArray();
        var rotation = new Vector2(handles[1].X + (float)Math.Sin(transform.Radians) * 28, handles[1].Y - (float)Math.Cos(transform.Radians) * 28);
        return new OverlayGeometry(handles, rotation, true);
    }

    private OverlayGeometry FromCorners(CanvasDocument doc, PointD[] corners)
    {
        var v = corners.Select(c => View(doc, c)).ToArray();
        Vector2 Mid(Vector2 a, Vector2 b) => (a + b) / 2;
        var handles = new[] { v[0], Mid(v[0], v[1]), v[1], Mid(v[1], v[2]), v[2], Mid(v[2], v[3]), v[3], Mid(v[3], v[0]) };
        return new OverlayGeometry(handles, handles[1], false);
    }

    private void DrawTransformHandles(CanvasDrawingSession ds, CanvasDocument doc)
    {
        if (TransformGeometry is not { } g) return;
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(g.Handles[0]);
        foreach (var i in new[] { 2, 4, 6 }) builder.AddLine(g.Handles[i]);
        builder.EndFigure(CanvasFigureLoop.Closed);
        if (g.ShowsRotation)
        {
            builder.BeginFigure(g.Handles[1]);
            builder.AddLine(g.RotationHandle);
            builder.EndFigure(CanvasFigureLoop.Open);
        }
        using var geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, WinColor.FromArgb(178, 0, 0, 0), 3);
        ds.DrawGeometry(geometry, Accent, 1);
        foreach (var p in g.Handles)
        {
            ds.FillRectangle(p.X - 3.5f, p.Y - 3.5f, 7, 7, Colors.White);
            ds.DrawRectangle(p.X - 3.5f, p.Y - 3.5f, 7, 7, Accent, 1);
        }
        if (!g.ShowsRotation) return;
        ds.FillCircle(g.RotationHandle, 4, Colors.White);
        ds.DrawCircle(g.RotationHandle, 4, Accent, 1);
    }

    // Crop

    private Rect? CropViewRect
    {
        get
        {
            if (session?.VisibleCropRect is not { } rect || session.Document is not { } doc) return null;
            var o = session.Viewport.ViewPoint(rect.Origin, doc.Size);
            return new Rect(o.X, o.Y, rect.Width * session.Viewport.PointsPerPixel, rect.Height * session.Viewport.PointsPerPixel);
        }
    }

    private Point[] CropHandles => CropViewRect is { } r
        ? LayerTransform.Handles.Select(h => new Point(r.X + h.X * r.Width, r.Y + h.Y * r.Height)).ToArray() : Array.Empty<Point>();

    private List<(int Index, Rect Rect)> CropResizeRegions
    {
        get
        {
            var result = new List<(int, Rect)>();
            if (CropViewRect is not { } rect) return result;
            var h = CropHandles;
            const double radius = 10;
            foreach (var i in new[] { 0, 2, 4, 6 }) result.Add((i, new Rect(h[i].X - radius, h[i].Y - radius, radius * 2, radius * 2)));
            foreach (var i in new[] { 1, 5 }) result.Add((i, new Rect(rect.X + radius, h[i].Y - radius, Math.Max(0, rect.Width - radius * 2), radius * 2)));
            foreach (var i in new[] { 3, 7 }) result.Add((i, new Rect(h[i].X - radius, rect.Y + radius, radius * 2, Math.Max(0, rect.Height - radius * 2))));
            return result;
        }
    }

    private void DrawCrop(CanvasDrawingSession ds, CanvasDocument doc)
    {
        if (CropViewRect is not { } rect) return;
        using (var outside = CanvasGeometry.CreateRectangle(ds, new Rect(0, 0, ActualWidth, ActualHeight)))
        using (var inside = CanvasGeometry.CreateRectangle(ds, rect))
        using (var shade = outside.CombineWith(inside, Matrix3x2.Identity, CanvasGeometryCombine.Exclude))
            ds.FillGeometry(shade, WinColor.FromArgb(153, 0, 0, 0));
        ds.DrawRectangle(rect, Colors.White, 1);
        var third = WinColor.FromArgb(102, 255, 255, 255);
        for (int i = 1; i <= 2; i++)
        {
            float f = i / 3f;
            ds.DrawLine((float)(rect.X + rect.Width * f), (float)rect.Y, (float)(rect.X + rect.Width * f), (float)rect.Bottom, third, 1);
            ds.DrawLine((float)rect.X, (float)(rect.Y + rect.Height * f), (float)rect.Right, (float)(rect.Y + rect.Height * f), third, 1);
        }
        foreach (var p in CropHandles)
        {
            ds.FillRectangle((float)p.X - 4, (float)p.Y - 4, 8, 8, Colors.White);
            ds.DrawRectangle((float)p.X - 4, (float)p.Y - 4, 8, 8, Colors.Black, 1);
        }
    }

    // Gradient

    private (Vector2 Start, Vector2 End)? GradientLine(CanvasDocument doc) =>
        session?.GradientEdit is { HasLine: true } edit ? (View(doc, edit.Start), View(doc, edit.End)) : null;

    private void DrawGradientLine(CanvasDrawingSession ds, (Vector2 Start, Vector2 End) line)
    {
        var s = session!;
        if (s.GradientSettings.Shape == GradientShape.Radial)
        {
            float radius = Vector2.Distance(line.Start, line.End);
            using var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 4f, 4f } };
            ds.DrawCircle(line.Start, radius, WinColor.FromArgb(128, 0, 0, 0), 2, dash);
            ds.DrawCircle(line.Start, radius, WinColor.FromArgb(204, 255, 255, 255), 1, dash);
        }
        ds.DrawLine(line.Start, line.End, WinColor.FromArgb(178, 0, 0, 0), 3);
        ds.DrawLine(line.Start, line.End, Colors.White, 1);
        var (first, last) = s.GradientColors(false);
        foreach (var (point, stop) in new[] { (line.Start, first), (line.End, last) })
        {
            ds.FillCircle(point, 6, Colors.White);
            ds.DrawCircle(point, 6, Colors.Black, 1);
            ds.FillCircle(point, 3.5f, WinColor.FromArgb(255, 191, 191, 191));
            ds.FillCircle(point, 3.5f, WinColor.FromArgb((byte)(stop.A * 255), (byte)(stop.R * 255), (byte)(stop.G * 255), (byte)(stop.B * 255)));
        }
    }

    // Brush cursor

    private void DrawBrushCursor(CanvasDrawingSession ds, CanvasDocument doc)
    {
        var s = session!;
        if (!s.Tool.IsBrushTool() || spaceHeld || Picking || brushPointer is not { } pointer) return;
        double diameter = s.BrushStroke?.Settings.Diameter ?? s.BrushSettings.Diameter;
        float radius = (float)Math.Max(0.5, diameter * s.Viewport.PointsPerPixel / 2);
        var center = new Vector2((float)pointer.X, (float)pointer.Y);
        if (brushTipDrag is { } tipDrag) center = new Vector2((float)tipDrag.Start.X, (float)tipDrag.Start.Y);
        if (s.Tool == NavigationTool.CloneStamp)
        {
            var point = s.Viewport.DocumentPoint(ToPoint(pointer), doc.Size);
            if (s.CloneSamplePoint(point) is { } source)
            {
                var v = View(doc, source);
                ds.DrawLine(v.X - 7, v.Y, v.X + 7, v.Y, Colors.White, 3);
                ds.DrawLine(v.X, v.Y - 7, v.X, v.Y + 7, Colors.White, 3);
                ds.DrawLine(v.X - 7, v.Y, v.X + 7, v.Y, Colors.Black, 1);
                ds.DrawLine(v.X, v.Y - 7, v.X, v.Y + 7, Colors.Black, 1);
            }
        }
        ds.DrawCircle(center, radius, WinColor.FromArgb(230, 255, 255, 255), 2.5f);
        ds.DrawCircle(center, radius, WinColor.FromArgb(230, 0, 0, 0), 1);
        if (brushTipDrag is { HardnessShown: true } && s.BrushSettings.Hardness > 0)
        {
            ds.DrawCircle(center, radius * (float)s.BrushSettings.Hardness, WinColor.FromArgb(200, 255, 255, 255), 1.5f);
            ds.DrawCircle(center, radius * (float)s.BrushSettings.Hardness, WinColor.FromArgb(200, 0, 0, 0), 0.75f);
        }
    }

    /// <summary>A clear zoom-tool badge that follows the pointer; Alt changes + to âˆ’.</summary>
    private void DrawZoomCursor(CanvasDrawingSession ds)
    {
        if (session?.Tool != NavigationTool.Zoom || spaceHeld || brushPointer is not { } pointer) return;
        var center = new Vector2((float)pointer.X + 11, (float)pointer.Y + 11);
        var handleStart = center + new Vector2(5, 5);
        var handleEnd = center + new Vector2(10, 10);
        ds.DrawCircle(center, 7, Colors.Black, 4);
        ds.DrawLine(handleStart, handleEnd, Colors.Black, 5);
        ds.DrawCircle(center, 7, Colors.White, 2);
        ds.DrawLine(handleStart, handleEnd, Colors.White, 2);
        ds.DrawLine(center.X - 3, center.Y, center.X + 3, center.Y, Colors.Black, 3);
        ds.DrawLine(center.X - 3, center.Y, center.X + 3, center.Y, Colors.White, 1);
        if (!AltHeld)
        {
            ds.DrawLine(center.X, center.Y - 3, center.X, center.Y + 3, Colors.Black, 3);
            ds.DrawLine(center.X, center.Y - 3, center.X, center.Y + 3, Colors.White, 1);
        }
    }

    /// <summary>A rotation badge shown in the transform box's outside-corner rotation zones.</summary>
    private void DrawTransformRotationCursor(CanvasDrawingSession ds)
    {
        if (session?.Tool != NavigationTool.Move || brushPointer is not { } pointer) return;
        bool rotating = transformDrag?.Mode.Kind == DragModeKind.Rotate
            || TransformGeometry?.Hit(new Vector2((float)pointer.X, (float)pointer.Y))?.Kind == DragModeKind.Rotate;
        if (!rotating) return;
        var center = new Vector2((float)pointer.X + 12, (float)pointer.Y + 12);
        ds.DrawCircle(center, 7, Colors.Black, 4);
        ds.DrawCircle(center, 7, Colors.White, 2);
        var tip = center + new Vector2(7, -1);
        ds.DrawLine(tip, tip + new Vector2(-1, -5), Colors.Black, 4);
        ds.DrawLine(tip, tip + new Vector2(-5, 1), Colors.Black, 4);
        ds.DrawLine(tip, tip + new Vector2(-1, -5), Colors.White, 2);
        ds.DrawLine(tip, tip + new Vector2(-5, 1), Colors.White, 2);
    }

    private void DrawSampleRing(CanvasDrawingSession ds)
    {
        if (sampleRingPoint is not { } p || session is not { ShowsSampleRing: true } s) return;
        var center = new Vector2((float)p.X, (float)p.Y);
        var sampled = s.ColorPicker?.Color ?? s.ForegroundColor;
        WinColor C(PaletteColor c) => WinColor.FromArgb(255, c.R8, c.G8, c.B8);
        // Sampled colour on the top half, the original on the bottom, as a thick ring.
        ds.DrawCircle(center, 48, WinColor.FromArgb(120, 0, 0, 0), 22);
        using (ds.CreateLayer(1f, new Rect(p.X - 60, p.Y - 60, 120, 60))) ds.DrawCircle(center, 46, C(sampled), 18);
        using (ds.CreateLayer(1f, new Rect(p.X - 60, p.Y, 120, 60))) ds.DrawCircle(center, 46, C(samplingOriginal), 18);
        ds.DrawCircle(center, 56, WinColor.FromArgb(160, 255, 255, 255), 1);
        ds.DrawCircle(center, 37, WinColor.FromArgb(160, 255, 255, 255), 1);
    }

    // MARK: Picking and cursors

    private bool AltHeld => Down(VirtualKey.Menu);
    private bool ShiftHeld => Down(VirtualKey.Shift);
    private bool CtrlHeld => Down(VirtualKey.Control);
    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private bool PalettePicking => session != null && (session.Tool == NavigationTool.Eyedropper
        || (AltHeld && (session.Tool is NavigationTool.Brush or NavigationTool.SpotHealing or NavigationTool.Gradient)
            && session.BrushStroke == null && gradientDrag == null));
    private bool Picking => session != null && (PalettePicking || session.ColorPicker != null || session.HueSampleMode != null || session.Levels?.SampleMode != null);

    private void UpdateCursor(Point point)
    {
        if (session == null) return;
        InputSystemCursorShape shape;
        if (transformDrag != null || cropDrag != null) return;
        if (spaceHeld || session.Tool == NavigationTool.Hand) shape = lastDragPoint != null ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Hand;
        else if (Picking) shape = InputSystemCursorShape.Cross;
        else if (session.HueTargeting) shape = InputSystemCursorShape.SizeWestEast;
        else if (session.Tool == NavigationTool.Move) shape = TransformCursor(point);
        else if (session.Tool == NavigationTool.Crop)
        {
            shape = InputSystemCursorShape.Cross;
            foreach (var (index, rect) in CropResizeRegions)
                if (rect.Contains(point)) { shape = index is 1 or 5 ? InputSystemCursorShape.SizeNorthSouth : index is 3 or 7 ? InputSystemCursorShape.SizeWestEast : index is 0 or 4 ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.SizeNortheastSouthwest; break; }
        }
        else if (session.Tool.IsSelectionTool() && session.Document is { } doc
            && (CtrlHeld || session.LassoCursorMode(ShiftHeld, AltHeld) == SelectionMode.Replace)
            && session.CanMoveSelection(session.Viewport.DocumentPoint(ToPoint(point), doc.Size))) shape = InputSystemCursorShape.SizeAll;
        else if (session.Tool is NavigationTool.Idle or NavigationTool.Zoom) shape = InputSystemCursorShape.Arrow;
        else shape = InputSystemCursorShape.Cross;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private InputSystemCursorShape TransformCursor(Point point)
    {
        if (session is not { } s || s.IsProjectBusy || s.IsImporting) return InputSystemCursorShape.Arrow;
        if (TransformGeometry is { } g && g.Hit(new Vector2((float)point.X, (float)point.Y)) is { } hit)
        {
            return hit.Kind switch
            {
                DragModeKind.Rotate => InputSystemCursorShape.Arrow,
                DragModeKind.Resize when s.TransformEditState?.Corners != null || CtrlHeld => InputSystemCursorShape.Arrow,
                DragModeKind.Resize => ResizeCursor(g, hit.Index),
                _ => InputSystemCursorShape.SizeAll,
            };
        }
        return PressMovesLayer(point) ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow;
    }

    private static InputSystemCursorShape ResizeCursor(OverlayGeometry g, int index)
    {
        double angle = Math.Atan2(g.Handles[2].Y - g.Handles[0].Y, g.Handles[2].X - g.Handles[0].X);
        double[] offsets = { Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, 0, Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, 0 };
        int direction = (((int)Math.Round((angle + offsets[index]) / (Math.PI / 4))) % 4 + 4) % 4;
        return direction switch
        {
            0 => InputSystemCursorShape.SizeWestEast,
            1 => InputSystemCursorShape.SizeNorthwestSoutheast,
            2 => InputSystemCursorShape.SizeNorthSouth,
            _ => InputSystemCursorShape.SizeNortheastSouthwest,
        };
    }

    private bool PressMovesLayer(Point point)
    {
        if (session?.Document is not { } doc) return false;
        return TransformPressLayer(session.Viewport.DocumentPoint(ToPoint(point), doc.Size), CtrlHeld) != null;
    }

    /// <summary>The layer a press that misses the handles drags: Ctrl picks the one under the pointer; otherwise the
    /// active layer, unless auto-select finds another under a press outside it.</summary>
    private (Guid Id, bool Picked)? TransformPressLayer(PointD pixel, bool ctrl)
    {
        var s = session!;
        if (!(s.CanEditLayers || s.TransformEditState != null) || s.Document is not { } doc) return null;
        var under = doc.RenderLayers().AsEnumerable().Reverse().FirstOrDefault(l => l.Asset != null && l.Transform.Contains(pixel))?.Id;
        var active = s.ActiveLayer is { Asset: not null, IsGroup: false } a && doc.EffectiveVisibleIds().Contains(a.Id) ? a : null;
        bool picks = s.TransformEditState == null;
        if (ctrl && picks && under is { } u) return (u, true);
        if (s.TransformsAsGroup && s.ActiveLayerId is { } id)
        {
            var box = s.TransformEditState?.Draft ?? s.GroupTransformBox;
            if (box?.Contains(pixel) == true || !(picks && s.TransformAutoSelect) || under == null) return (id, false);
        }
        if (active != null && s.EditedTransform(active).Contains(pixel)) return (active.Id, false);
        if (picks && (s.TransformAutoSelect || ctrl) && under is { } u2) return (u2, true);
        return active == null ? null : (active.Id, false);
    }

    // MARK: Pointer input

    private void Capture(PointerRoutedEventArgs e)
    {
        if (CapturePointer(e.Pointer)) capturedPointer = e.Pointer.PointerId;
    }

    private void EndCapture(PointerRoutedEventArgs e)
    {
        if (capturedPointer == e.Pointer.PointerId) ReleasePointerCapture(e.Pointer);
        capturedPointer = null;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var s = session;
        if (s?.Document is not { } doc || s.IsProjectBusy || s.IsImporting) return;
        var properties = e.GetCurrentPoint(this).Properties;
        var point = e.GetCurrentPoint(this).Position;
        var modifiers = e.KeyModifiers;
        bool shift = modifiers.HasFlag(VirtualKeyModifiers.Shift), alt = modifiers.HasFlag(VirtualKeyModifiers.Menu), ctrl = modifiers.HasFlag(VirtualKeyModifiers.Control);
        var pixel = s.Viewport.DocumentPoint(ToPoint(point), doc.Size);
        Capture(e);
        if (properties.IsMiddleButtonPressed) { lastDragPoint = point; UpdateCursor(point); return; }
        if (properties.IsRightButtonPressed)
        {
            // Right-drag with a brush tool: left and right resize the brush, or with Shift change its hardness.
            if (s.Tool.IsBrushTool() && s.BrushStroke == null && s.WarpStroke == null && !spaceHeld)
            {
                brushTipDrag = (point, s.BrushSettings.Diameter, s.BrushSettings.Hardness, shift);
                brushPointer = point;
                canvas.Invalidate();
            }
            return;
        }
        bool doubleClick = (DateTime.Now - lastClick).TotalMilliseconds < 400 && Math.Abs(point.X - lastClickPoint.X) < 4 && Math.Abs(point.Y - lastClickPoint.Y) < 4;
        lastClick = DateTime.Now;
        lastClickPoint = point;
        if (s.Levels?.SampleMode != null && !spaceHeld) { s.SampleLevels(pixel); return; }
        if (s.Levels != null && !spaceHeld && s.Tool != NavigationTool.Hand && s.Tool != NavigationTool.Zoom) return;
        if (Picking && !spaceHeld)
        {
            if (s.ColorPicker != null || (PalettePicking && s.HueSampleMode == null))
            {
                samplingOriginal = s.ColorPicker?.Color ?? s.ForegroundColor;
                samplingColor = true;
                SampleColor(point);
            }
            else s.SampleHueRange(pixel);
            return;
        }
        if (s.HueTargeting && !spaceHeld)
        {
            if (s.BeginHueTargeting(pixel)) hueTargetStart = point;
            return;
        }
        if (spaceHeld || s.Tool == NavigationTool.Hand)
        {
            lastDragPoint = point;
            UpdateCursor(point);
        }
        else if (s.Tool.IsBrushTool())
        {
            if (s.Tool == NavigationTool.CloneStamp && alt) { s.SetCloneSource(pixel); canvas.Invalidate(); return; }
            brushPointer = point;
            if (shift && s.ShiftLineStart() is { } from) { s.BeginBrush(from); s.ContinueBrush(pixel); }
            else s.BeginBrush(pixel);
            brushAxisAnchor = shift ? pixel : null;
            brushAxisHorizontal = null;
            brushLastPixel = pixel;
        }
        else if (s.Tool.IsSelectionTool()) LassoPressed(pixel, point, shift, alt, ctrl, doubleClick);
        else if (s.Tool == NavigationTool.Gradient) BeginGradientDrag(point, doc);
        else if (s.Tool == NavigationTool.Shape) s.BeginShape(pixel);
        else if (s.Tool == NavigationTool.Crop) BeginCropDrag(point, doc);
        else if (s.Tool == NavigationTool.Move) BeginTransformDrag(point, pixel, alt, ctrl);
        else if (s.Tool == NavigationTool.Zoom) zoomDrag = (point, s.Viewport.Zoom, false);
        canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var s = session;
        var point = e.GetCurrentPoint(this).Position;
        var modifiers = e.KeyModifiers;
        bool shift = modifiers.HasFlag(VirtualKeyModifiers.Shift), alt = modifiers.HasFlag(VirtualKeyModifiers.Menu), ctrl = modifiers.HasFlag(VirtualKeyModifiers.Control);
        if (s?.Document is not { } doc) return;
        s.UpdateHeldSelectionKeys(shift, alt);
        bool pressed = capturedPointer != null && e.GetCurrentPoint(this).Properties is { } p && (p.IsLeftButtonPressed || p.IsMiddleButtonPressed || p.IsRightButtonPressed);
        if (!pressed)
        {
            brushPointer = point;
            if (s.LassoDraft?.Kind == LassoKind.Polygonal) s.MoveLassoCursor(s.Viewport.DocumentPoint(ToPoint(point), doc.Size));
            UpdateCursor(point);
            canvas.Invalidate();
            return;
        }
        var pixel = s.Viewport.DocumentPoint(ToPoint(point), doc.Size);
        if (brushTipDrag is { } tip)
        {
            brushTipDrag = tip with { HardnessShown = shift };
            double dx = point.X - tip.Start.X;
            if (shift) s.BrushSettings = s.BrushSettings with { Hardness = Math.Clamp(tip.Hardness + dx / 200, 0, 1), Diameter = tip.Diameter };
            else
            {
                double perPixel = Math.Max(0.0001, s.Viewport.PointsPerPixel);
                s.BrushSettings = s.BrushSettings with { Diameter = Math.Clamp(Math.Round(tip.Diameter + 2 * dx / perPixel), 1, 2000), Hardness = tip.Hardness };
            }
            canvas.Invalidate();
            return;
        }
        if (zoomDrag is { } drag)
        {
            double dx = point.X - drag.Start.X;
            if (Math.Abs(dx) >= 3) zoomDrag = drag = drag with { Moved = true };
            if (drag.Moved) s.Zoom(drag.Zoom * Math.Pow(2, dx / 100), ToPoint(drag.Start));
            return;
        }
        if (samplingColor) { SampleColor(point); return; }
        if (hueTargetStart is { } start) { s.DragHueTargeting(point.X - start.X, ctrl); return; }
        if (pixelDragStart is { } pixelStart) { s.MovePixels(new SizeD(pixel.X - pixelStart.X, pixel.Y - pixelStart.Y)); return; }
        if (selectionDragStart != null)
        {
            DragSelection(point, shift);
            UpdateAutoscroll(point);
            return;
        }
        if (s.Tool.IsSelectionTool() && lastDragPoint == null && s.LassoDraft is { } draft)
        {
            switch (draft.Kind)
            {
                case LassoKind.Freehand: s.ExtendLasso(pixel); break;
                case LassoKind.Polygonal: s.MoveLassoCursor(pixel); break;
                default: DragMarqueeDraft(pixel, shift); UpdateAutoscroll(point); break;
            }
            return;
        }
        if (s.ShapeDraft != null && lastDragPoint == null) { s.DragShape(pixel, shift, alt); return; }
        if (gradientDrag is { } handle && s.GradientEdit is { } edit)
        {
            if (shift) pixel = Snapped45(pixel, handle == GradientHandle.Start ? edit.End : edit.Start);
            s.MoveGradient(handle == GradientHandle.Start ? pixel : null, handle == GradientHandle.End ? pixel : null);
            return;
        }
        brushPointer = point;
        if ((s.BrushStroke != null || s.WarpStroke != null) && !s.IsProjectBusy)
        {
            if (shift)
            {
                var anchor = brushAxisAnchor ?? brushLastPixel ?? pixel;
                if (brushAxisAnchor == null) { brushAxisAnchor = anchor; brushAxisHorizontal = null; }
                if (brushAxisHorizontal == null && pixel.DistanceTo(anchor) >= 3) brushAxisHorizontal = Math.Abs(pixel.X - anchor.X) >= Math.Abs(pixel.Y - anchor.Y);
                pixel = brushAxisHorizontal is { } horizontal ? (horizontal ? new PointD(pixel.X, anchor.Y) : new PointD(anchor.X, pixel.Y)) : anchor;
            }
            else { brushAxisAnchor = null; brushAxisHorizontal = null; }
            brushLastPixel = pixel;
            // Every coalesced pointer sample between frames, so fast strokes keep their curve.
            foreach (var sample in e.GetIntermediatePoints(this).Reverse().Skip(0))
            {
                if (shift) break;
                var samplePixel = s.Viewport.DocumentPoint(ToPoint(sample.Position), doc.Size);
                if (samplePixel != pixel) s.ContinueBrush(samplePixel);
            }
            s.ContinueBrush(pixel);
            return;
        }
        if (cropDrag is { } crop && s.Tool == NavigationTool.Crop && !s.IsProjectBusy) { DragCrop(crop, point, alt, ctrl); return; }
        if (transformDrag is { } transform)
        {
            if (duplicatesTransformOnDrag) { duplicatesTransformOnDrag = false; s.BeginDuplicateTransform(); }
            if (transform.CornersTo(pixel, shift) is { } corners) s.PreviewCorners(corners);
            else
            {
                var draftTransform = transform.Updated(pixel, s.LocksTransformRatio, shift, alt).Rounded();
                if (transform.Mode.Kind == DragModeKind.Move && !ctrl)
                {
                    var moving = s.TransformEditState?.Group?.Originals.Keys.ToHashSet()
                        ?? (s.TransformEditState?.LayerId is { } id ? new HashSet<Guid> { id } : new HashSet<Guid>());
                    draftTransform = s.SnappedMove(draftTransform, moving, TransformSnap.Distance / Math.Max(s.Viewport.PointsPerPixel, 0.0001));
                }
                else s.SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
                s.PreviewTransform(draftTransform);
            }
            return;
        }
        if (lastDragPoint is { } last)
        {
            s.Pan(new SizeD(point.X - last.X, point.Y - last.Y));
            lastDragPoint = point;
        }
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var s = session;
        var point = e.GetCurrentPoint(this).Position;
        bool alt = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
        EndCapture(e);
        StopAutoscroll();
        if (s?.Document is not { } doc) return;
        if (brushTipDrag != null) { brushTipDrag = null; brushPointer = point; canvas.Invalidate(); return; }
        if (zoomDrag is { } drag)
        {
            zoomDrag = null;
            if (!drag.Moved) s.Zoom(s.Viewport.Zoom * (alt ? 0.5 : 2), ToPoint(drag.Start));
            return;
        }
        s.SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
        if (samplingColor) { samplingColor = false; sampleRingPoint = null; canvas.Invalidate(); return; }
        if ((s.BrushStroke != null || s.WarpStroke != null) && !s.IsProjectBusy)
        {
            s.ContinueBrush(s.Viewport.DocumentPoint(ToPoint(point), doc.Size));
            s.FinishBrush();
        }
        if (gradientDrag != null) { gradientDrag = null; s.EndGradientDrag(); }
        if (s.ShapeDraft != null) s.FinishShape();
        if (hueTargetStart != null) { hueTargetStart = null; s.EndHueTargeting(); }
        if (pixelDragStart != null) { pixelDragStart = null; await s.FinishPixelMove(); }
        if (selectionDragStart is { } start)
        {
            selectionDragStart = null;
            bool moved = !Equals(s.SelectionMoveOrigin, s.Selection);
            s.EndSelectionMove();
            if (!moved && s.Tool == NavigationTool.Wand) await s.MagicWand(start, SelectionMode.Replace);
            else if (!moved) s.Deselect();
        }
        if (s.Tool.IsSelectionTool() && s.LassoDraft is { } draft && draft.Kind != LassoKind.Polygonal) s.FinishLasso();
        cropDrag = null;
        if (transformDrag != null)
        {
            duplicatesTransformOnDrag = false;
            transformDrag = null;
            if (s.TransformEditState?.Persistent == false) s.CommitTransform();
        }
        lastDragPoint = null;
        UpdateCursor(point);
        canvas.Invalidate();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var s = session;
        if (s?.Document == null || transformDrag != null || cropDrag != null || s.BrushStroke != null || s.WarpStroke != null) return;
        var p = e.GetCurrentPoint(this);
        int delta = p.Properties.MouseWheelDelta;
        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control) || modifiers.HasFlag(VirtualKeyModifiers.Menu))
            s.Zoom(s.Viewport.Zoom * Math.Exp(delta / 120.0 * 0.18), ToPoint(p.Position));
        else if (p.Properties.IsHorizontalMouseWheel) s.Pan(new SizeD(-delta / 2.0, 0));
        else if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) s.Pan(new SizeD(delta / 2.0, 0));
        else s.Pan(new SizeD(0, delta / 2.0));
        e.Handled = true;
    }

    // MARK: Tool helpers

    private void SampleColor(Point point)
    {
        var s = session!;
        if (s.Document is not { } doc) return;
        var documentPoint = s.Viewport.DocumentPoint(ToPoint(point), doc.Size);
        if (s.ColorPicker != null) s.SampleIntoColorPicker(documentPoint);
        else if (s.CanEditPalette && s.SampleCompositeColor(documentPoint) is { } color) s.ForegroundColor = color;
        sampleRingPoint = point;
        canvas.Invalidate();
    }

    private static PointD Snapped45(PointD point, PointD anchor)
    {
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new PointD(anchor.X + Math.Cos(angle) * length, anchor.Y + Math.Sin(angle) * length);
    }

    private void BeginGradientDrag(Point point, CanvasDocument doc)
    {
        var s = session!;
        if (GradientLine(doc) is { } line)
        {
            var p = new Vector2((float)point.X, (float)point.Y);
            if (Vector2.Distance(p, line.End) <= 10) { gradientDrag = GradientHandle.End; return; }
            if (Vector2.Distance(p, line.Start) <= 10) { gradientDrag = GradientHandle.Start; return; }
        }
        s.BeginGradient(s.Viewport.DocumentPoint(ToPoint(point), doc.Size));
        gradientDrag = s.GradientEdit == null ? null : GradientHandle.End;
    }

    private void LassoPressed(PointD pixel, Point point, bool shift, bool alt, bool ctrl, bool doubleClick)
    {
        var s = session!;
        marqueeConstrainArmed = !shift;
        marqueeDragPixel = null;
        if (s.LassoDraft is not { Kind: LassoKind.Polygonal } draft)
        {
            // Ctrl-drag inside the selection cuts and moves its pixels; Ctrl+Alt copies them.
            if (ctrl && s.CanMoveSelection(pixel))
            {
                if (s.BeginPixelMove(alt)) pixelDragStart = pixel;
                else s.Host.Beep();
                return;
            }
            var mode = s.SelectionModeFor(shift, alt);
            if (mode == SelectionMode.Replace && s.CanMoveSelection(pixel) && s.BeginSelectionMove()) { selectionDragStart = pixel; return; }
            if (s.Tool == NavigationTool.Wand) { _ = s.MagicWand(pixel, mode); return; }
            s.BeginLasso(pixel, mode);
            return;
        }
        var first = s.Viewport.ViewPoint(draft.Points[0], s.Document!.Size);
        if (doubleClick || (draft.Points.Count >= 3 && Math.Sqrt(Math.Pow(point.X - first.X, 2) + Math.Pow(point.Y - first.Y, 2)) <= 8)) s.FinishLasso();
        else s.ExtendLasso(pixel);
    }

    private void DragMarqueeDraft(PointD pixel, bool shift)
    {
        if (!shift) marqueeConstrainArmed = true;
        marqueeDragPixel = pixel;
        session!.DragMarquee(pixel, marqueeConstrainArmed && shift, false);
    }

    private void DragSelection(Point point, bool shift)
    {
        if (selectionDragStart is not { } start || session?.Document is not { } doc) return;
        var pixel = session.Viewport.DocumentPoint(ToPoint(point), doc.Size);
        var offset = new SizeD(pixel.X - start.X, pixel.Y - start.Y);
        if (shift) offset = Math.Abs(offset.Width) >= Math.Abs(offset.Height) ? offset with { Height = 0 } : offset with { Width = 0 };
        session.MoveSelection(offset);
    }

    private SizeD AutoscrollDelta(Point point)
    {
        const double margin = 12;
        static double Speed(double past) => past <= 0 ? 0 : Math.Min(40, 2 + past * 0.4);
        double left = Speed(margin - point.X), right = Speed(point.X - (ActualWidth - margin));
        double top = Speed(margin - point.Y), bottom = Speed(point.Y - (ActualHeight - margin));
        return new SizeD(left - right, top - bottom);
    }

    private void UpdateAutoscroll(Point point)
    {
        autoscrollPoint = point;
        if (AutoscrollDelta(point) == SizeD.Zero) { StopAutoscroll(); return; }
        if (!autoscrollTimer.IsEnabled) autoscrollTimer.Start();
    }

    private void StepAutoscroll()
    {
        var s = session;
        bool marquee = s?.LassoDraft is { Kind: LassoKind.Rectangle or LassoKind.Ellipse };
        if (autoscrollPoint is not { } point || s?.Document is not { } doc || !(marquee || selectionDragStart != null)) { StopAutoscroll(); return; }
        var delta = AutoscrollDelta(point);
        if (delta == SizeD.Zero) { StopAutoscroll(); return; }
        s.Pan(delta);
        if (selectionDragStart != null) DragSelection(point, ShiftHeld);
        else DragMarqueeDraft(s.Viewport.DocumentPoint(ToPoint(point), doc.Size), ShiftHeld);
    }

    private void StopAutoscroll()
    {
        autoscrollTimer.Stop();
        autoscrollPoint = null;
    }

    public const double CropSnapDistance = 8;

    private void BeginCropDrag(Point point, CanvasDocument doc)
    {
        var s = session!;
        var pixel = s.Viewport.DocumentPoint(ToPoint(point), doc.Size);
        var rect = s.VisibleCropRect ?? new RectD(pixel, SizeD.Zero);
        DragMode mode;
        var region = CropResizeRegions.FirstOrDefault(r => r.Rect.Contains(point));
        if (region.Rect.Width > 0 || region.Rect.Height > 0) mode = DragMode.Resize(region.Index);
        else if (s.CropRect is { } crop && crop.Contains(pixel) && rect != new RectD(0, 0, doc.Width, doc.Height)) mode = DragMode.MoveMode;
        else { mode = DragMode.CreateMode; s.CropRect = null; }
        cropDrag = new CropDrag(pixel, rect, mode);
        var targets = s.CropSnapTargets();
        cropSnap = new CropSnap(targets.Xs, targets.Ys, CropSnapDistance / Math.Max(s.Viewport.PointsPerPixel, 0.0001));
    }

    private void DragCrop(CropDrag drag, Point point, bool symmetric, bool noSnap)
    {
        var s = session!;
        var pixel = s.Viewport.DocumentPoint(ToPoint(point), s.Document!.Size);
        var next = drag.Updated(pixel, s.CropRatio, symmetric);
        if (cropSnap != null && !noSnap) next = cropSnap.Apply(next, drag, pixel, s.CropRatio, symmetric);
        if (CropGeometry.Valid(next)) { s.CropRect = next; s.Notify(); canvas.Invalidate(); }
    }

    private void BeginTransformDrag(Point point, PointD pixel, bool alt, bool ctrl)
    {
        var s = session!;
        if (!(s.CanEditLayers || s.TransformEditState != null)) return;
        DragMode? mode = TransformGeometry?.Hit(new Vector2((float)point.X, (float)point.Y));
        if (mode == null && TransformPressLayer(pixel, ctrl) is { } target)
        {
            if (target.Picked) s.SelectLayer(target.Id);
            mode = DragMode.MoveMode;
        }
        if (mode is not { } m) return;
        duplicatesTransformOnDrag = m.Kind == DragModeKind.Move && alt;
        if (s.TransformEditState == null) s.BeginTransform(persistent: false);
        // Ctrl-dragging a handle distorts; once distorted, handles keep distorting.
        if (m.Kind == DragModeKind.Resize && (ctrl || s.TransformEditState?.Corners != null))
        {
            s.BeginDistort();
            if (s.TransformEditState?.Corners != null) m = DragMode.Distort(m.Index);
        }
        if (s.TransformEditState?.Draft is not { } draft) return;
        transformDrag = new TransformDrag(draft, pixel, m, s.TransformEditState.Corners);
    }

    // MARK: Keyboard

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var s = session;
        if (s == null) return;
        var key = e.Key;
        if (s.Tool == NavigationTool.Zoom && key == VirtualKey.Menu) canvas.Invalidate();
        bool ctrl = CtrlHeld, alt = AltHeld, shift = ShiftHeld;
        bool plain = !ctrl && !alt;
        bool isDelete = key is VirtualKey.Back or VirtualKey.Delete;
        bool isEnter = key == VirtualKey.Enter;
        bool isEscape = key == VirtualKey.Escape;
        e.Handled = true;
        if (isDelete && shift && plain) { if (s.CanContentAwareFill) _ = s.BeginFilter(FilterKind.ContentAwareFill); return; }
        if (s.Levels is { } levels)
        {
            if (isEscape) { s.CancelLevels(); return; }
            if (isEnter) { _ = s.CommitLevels(); return; }
            if (key == VirtualKey.P && alt) { s.UpdateLevels(levels.Settings, !levels.Preview); return; }
            if (key != VirtualKey.Space) { e.Handled = false; return; }
        }
        if (s.BrushStroke != null || s.WarpStroke != null)
        {
            if (isEscape && !s.IsProjectBusy) s.CancelBrush();
            return;
        }
        if (s.LassoDraft != null && (isEscape || isEnter || isDelete))
        {
            if (isEscape) s.CancelLasso();
            else if (isEnter) s.FinishLasso();
            else s.RemoveLastLassoPoint();
        }
        else if (s.ShapeDraft != null && isEscape) s.CancelShape();
        else if (s.GradientEdit != null && isEscape) { gradientDrag = null; s.CancelGradient(); }
        else if (s.GradientEdit != null && isEnter) { gradientDrag = null; _ = s.CommitGradient(); }
        else if (s.Tool == NavigationTool.Crop && isEscape) { cropDrag = null; s.CancelCrop(); }
        else if (s.Tool == NavigationTool.Crop && isEnter) { cropDrag = null; _ = s.CommitCrop(); }
        else if (isEscape && s.TransformEditState != null) { transformDrag = null; s.CancelTransform(); }
        else if (isEnter && s.TransformEditState != null) { transformDrag = null; s.CommitTransform(); }
        else if (IsArrow(key, out var dx, out var dy))
        {
            double step = shift ? 10 : 1;
            if (s.Selection is { IsEmpty: false } && s.LassoDraft == null && ctrl && !alt) _ = s.NudgePixels(dx * step, dy * step);
            else if (s.Tool.IsSelectionTool() && s.LassoDraft == null && s.Selection is { IsEmpty: false } && plain) s.NudgeSelection(dx * step, dy * step);
            else if (s.Tool == NavigationTool.Move && plain) s.NudgeLayer(dx * step, dy * step);
            else e.Handled = false;
        }
        else if (isDelete && plain) _ = s.DeleteKeyPressed();
        else if (key == VirtualKey.Space)
        {
            if (!spaceHeld) { spaceHeld = true; canvas.Invalidate(); }
        }
        else if (plain && !HandleToolKey(s, key, shift)) e.Handled = false;
        else if (!plain) e.Handled = false;
    }

    /// <summary>Tool and palette keys shared by the canvas and the Layers panel (EditorCanvas keyDown).</summary>
    public static bool HandleToolKey(EditorSession s, VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.X: s.SwapPaletteColors(); return true;
            case VirtualKey.D: s.ResetPaletteColors(); return true;
            case VirtualKey.B: s.SelectTool(NavigationTool.Brush); s.BrushMode = BrushToolMode.Paint; s.Notify(); return true;
            case VirtualKey.E: s.SelectTool(NavigationTool.Brush); s.BrushMode = BrushToolMode.Erase; s.Notify(); return true;
            case VirtualKey.J: s.SelectTool(NavigationTool.SpotHealing); return true;
            case VirtualKey.S: s.SelectTool(NavigationTool.CloneStamp); return true;
            case VirtualKey.G: s.SelectTool(NavigationTool.Gradient); return true;
            case VirtualKey.U:
                if (shift && s.Tool == NavigationTool.Shape) s.ToggleShapeKind(); else s.SelectTool(NavigationTool.Shape);
                return true;
            case VirtualKey.I: s.SelectTool(NavigationTool.Eyedropper); return true;
            case VirtualKey.M: s.PressMarqueeKey(); return true;
            case VirtualKey.W: s.SelectTool(NavigationTool.Wand); return true;
            case VirtualKey.L: s.PressLassoKey(); return true;
            case VirtualKey.A: s.SelectTool(NavigationTool.Idle); return true;
            case VirtualKey.R: s.SelectTool(NavigationTool.Blur); return true;
            case VirtualKey.C: s.SelectTool(NavigationTool.Crop); return true;
            case VirtualKey.V: s.SelectTool(NavigationTool.Move); return true;
            case VirtualKey.H: s.SelectTool(NavigationTool.Hand); return true;
            case VirtualKey.Z: s.SelectTool(NavigationTool.Zoom); return true;
            case (VirtualKey)219 when s.Tool.IsBrushTool(): // [ and {
                if (shift) s.ChangeBrushHardness(false); else s.ChangeBrushSize(false);
                return true;
            case (VirtualKey)221 when s.Tool.IsBrushTool(): // ] and }
                if (shift) s.ChangeBrushHardness(true); else s.ChangeBrushSize(true);
                return true;
            case (VirtualKey)187 when shift: s.CycleBlendMode(true); return true;  // Shift + '+'
            case (VirtualKey)189 when shift: s.CycleBlendMode(false); return true; // Shift + '−'
        }
        int digit = key >= VirtualKey.Number0 && key <= VirtualKey.Number9 ? key - VirtualKey.Number0
            : key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9 ? key - VirtualKey.NumberPad0 : -1;
        if (digit >= 0 && s.UsesOpacityKeys && !shift)
        {
            s.TypeOpacityDigit(digit, Environment.TickCount64 / 1000.0);
            return true;
        }
        return false;
    }

    private static bool IsArrow(VirtualKey key, out double dx, out double dy)
    {
        dx = key == VirtualKey.Left ? -1 : key == VirtualKey.Right ? 1 : 0;
        dy = key == VirtualKey.Up ? -1 : key == VirtualKey.Down ? 1 : 0;
        return dx != 0 || dy != 0;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space) { spaceHeld = false; canvas.Invalidate(); e.Handled = true; }
        var s = session;
        if (s?.Tool == NavigationTool.Zoom && e.Key == VirtualKey.Menu) canvas.Invalidate();
        if (s != null) s.UpdateHeldSelectionKeys(ShiftHeld, AltHeld);
    }

    private void OnFocusLost()
    {
        var s = session;
        if (s == null) return;
        if (!s.IsProjectBusy && (s.BrushStroke != null || s.WarpStroke != null)) s.CancelBrush();
        cropDrag = null;
        gradientDrag = null;
        s.CancelShape();
        if (s.LassoDraft is { Kind: not LassoKind.Polygonal }) s.CancelLasso();
        if (selectionDragStart != null) { selectionDragStart = null; s.EndSelectionMove(); }
        if (pixelDragStart != null) { pixelDragStart = null; s.CancelPixelMove(); }
        if (transformDrag is { } drag)
        {
            duplicatesTransformOnDrag = false;
            s.PreviewTransform(drag.Original);
            if (s.TransformEditState?.Persistent == false) s.CancelTransform();
            transformDrag = null;
        }
        spaceHeld = false;
        lastDragPoint = null;
    }

    public void FocusCanvas() => Focus(FocusState.Programmatic);
}
