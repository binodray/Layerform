using Compositor.Editing;
using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;
using Xunit;
using static Compositor.Tests.Fixtures;

namespace Compositor.Tests;

public class SessionTests
{
    private static RasterImage Solid(int w, int h, SKColor color)
    {
        var bitmap = PixelOps.NewRgba(w, h);
        bitmap.Erase(color);
        return RasterImage.Adopt(bitmap);
    }

    private static EditorSession NewSession(int w = 100, int h = 80)
    {
        var s = new EditorSession();
        s.Viewport.Resize(new SizeD(800, 600), 1, null);
        s.CreateDocument(w, h, emptyLayer: true);
        return s;
    }

    private static (byte R, byte G, byte B, byte A) At(EditorSession s, int x, int y) => Straight(s.RenderComposite(s.Document!), x, y);

    private static bool HasPartialCoverage(MaskImage mask)
    {
        foreach (byte value in mask.Pixels)
            if (value is > 0 and < 255) return true;
        return false;
    }

    [Fact]
    public void NewCanvasHasOneBlankLayerAndCleanHistory()
    {
        var s = new EditorSession();
        s.CreateNewProject(640, 480);
        Assert.Single(s.Document!.Layers);
        Assert.Equal("Layer 1", s.Document.Layers[0].Name);
        Assert.Equal(s.Document.Layers[0].Id, s.ActiveLayerId);
    }

    [Fact]
    public void UndoRedoCoverLayerEditsAndKeepRedoOnNoOps()
    {
        var s = NewSession();
        s.AddBlankLayer();
        Assert.Equal(2, s.Document!.Layers.Count);
        Assert.Equal("New Blank Layer", s.History.UndoName);
        s.RenameLayer(s.ActiveLayerId!.Value, "Sky");
        s.Undo();
        Assert.NotEqual("Sky", s.ActiveLayer!.Name);
        s.SelectLayer(s.Document.Layers[0].Id); // Selecting is not an edit.
        Assert.True(s.CanRedo);
        s.Redo();
        Assert.Equal("Sky", s.Document!.Layers[1].Name);
        s.Undo(); s.Undo();
        Assert.Single(s.Document!.Layers);
    }

    [Fact]
    public void LockedLayerRejectsEditsAndRoundTripsThroughSnapshot()
    {
        var s = NewSession();
        var id = s.ActiveLayerId!.Value;
        s.ToggleLayerLock(id);
        Assert.True(s.ActiveLayer!.IsLocked);
        Assert.False(s.CanPaint);
        Assert.False(s.CanTransform);
        s.RenameLayer(id, "Changed");
        Assert.Equal("Layer 1", s.ActiveLayer.Name);

        var snapshot = s.ProjectSnapshot()!;
        Assert.True(snapshot.Manifest.Layers.Single().IsLocked);
        var reopened = new EditorSession();
        reopened.InstallProject(snapshot, "locked.lform");
        Assert.True(reopened.ActiveLayer!.IsLocked);

        reopened.ToggleLayerLock(id);
        reopened.RenameLayer(id, "Changed");
        Assert.Equal("Changed", reopened.ActiveLayer!.Name);
    }

    [Fact]
    public void EditableTextRendersRecolorsAndRoundTripsMetadata()
    {
        var s = NewSession(500, 300);
        var style = new LayerTextStyle
        {
            Content = "Hello\nLayer Form", FontName = "Arial", FontSize = 48,
            Red = 1, Green = 0.2, Blue = 0.1, Alignment = LayerTextAlignment.Center,
            Tracking = 2, Leading = 60, BoxSize = new SizeD(360, 160),
        };
        Assert.True(s.AddTextLayer(style, new PointD(20, 30)));
        Assert.NotNull(s.ActiveLayer!.LiveText);
        Assert.NotNull(s.ActiveLayer.Asset);
        Assert.True(s.RecolorText(new PaletteColor(0, 0, 1)));
        Assert.Equal(1, s.ActiveLayer!.LiveText!.Style.Blue);

        var manifest = s.ProjectSnapshot()!.Manifest;
        var decoded = ProjectJson.ReadManifest(ProjectJson.WriteManifest(manifest));
        Assert.Equal("Hello\nLayer Form", decoded.Layers.Last().Text!.Content);
        Assert.Equal(new SizeD(360, 160), decoded.Layers.Last().Text!.BoxSize);
    }

    [Fact]
    public void FolderOpacityAndDuplicationIncludeTheWholeBranch()
    {
        var s = NewSession();
        s.GroupSelectedLayers();
        var folder = s.ActiveLayer!;
        Assert.True(folder.IsGroup);
        s.SetLayerOpacity(0.5);
        Assert.Equal(0.5, s.ActiveLayer!.Opacity);
        int before = s.Document!.Layers.Count;
        s.DuplicateActiveLayer();
        Assert.True(s.ActiveLayer!.IsGroup);
        Assert.Equal(before * 2, s.Document!.Layers.Count);
        Assert.Equal(0.5, s.ActiveLayer.Opacity);
    }

    [Fact]
    public void BrushStrokePaintsTheForegroundColourAsOneUndoStep()
    {
        var s = NewSession();
        s.SelectTool(NavigationTool.Brush);
        s.ForegroundColor = new PaletteColor(1, 0, 0);
        s.BrushSettings = s.BrushSettings with { Diameter = 10, Hardness = 1 };
        s.BeginBrush(new PointD(20, 40));
        s.ContinueBrush(new PointD(50, 40));
        s.ContinueBrush(new PointD(80, 40));
        s.FinishBrush();
        Assert.Equal("Brush Stroke", s.History.UndoName);
        var layer = s.ActiveLayer!;
        Assert.NotNull(layer.Asset);
        Near(At(s, 50, 40), 255, 0, 0, 255);
        Assert.Equal(0, At(s, 50, 10).A);
        s.Undo();
        Assert.Null(s.ActiveLayer!.Asset);
    }

    [Fact]
    public void SoftBrushFallsOffAndOpacityCapsTheStroke()
    {
        var s = NewSession();
        s.SelectTool(NavigationTool.Brush);
        s.BrushSettings = s.BrushSettings with { Diameter = 40, Hardness = 0, Opacity = 0.5 };
        s.BeginBrush(new PointD(50, 40));
        for (int i = 0; i < 10; i++) s.ContinueBrush(new PointD(50 + i * 0.5, 40));
        s.FinishBrush();
        var centre = At(s, 50, 40);
        Assert.InRange(centre.A, 120, 132);   // Many overlapping dabs never exceed the 50% cap.
        Assert.True(At(s, 66, 40).A < centre.A);
    }

    [Fact]
    public void EraserClearsPixels()
    {
        var s = NewSession();
        s.ImportAssets(new[] { PixelOps.Asset(Solid(100, 80, SKColors.Blue), "Blue") });
        s.SelectTool(NavigationTool.Brush);
        s.BrushMode = BrushToolMode.Erase;
        s.BrushSettings = s.BrushSettings with { Diameter = 20, Hardness = 1, Opacity = 1 };
        s.BeginBrush(new PointD(50, 40));
        s.FinishBrush();
        Assert.Equal("Erase", s.History.UndoName);
        Assert.Equal(0, At(s, 50, 40).A);
        Near(At(s, 5, 5), 0, 0, 255);
    }

    [Fact]
    public async Task FillAndClearRespectTheSelection()
    {
        var s = NewSession();
        s.SelectTool(NavigationTool.Marquee);
        s.BeginLasso(new PointD(10, 10), SelectionMode.Replace);
        s.DragMarquee(new PointD(30, 30), false, false);
        s.FinishLasso();
        Assert.NotNull(s.Selection);
        s.ForegroundColor = new PaletteColor(0, 1, 0);
        await s.FillSelection(background: false);
        Near(At(s, 20, 20), 0, 255, 0);
        Assert.Equal(0, At(s, 40, 40).A);
        s.SelectAll();
        s.BeginLasso(new PointD(15, 15), SelectionMode.Replace);
        s.DragMarquee(new PointD(25, 25), false, false);
        s.FinishLasso();
        await s.ClearSelectedPixels();
        Assert.Equal(0, At(s, 20, 20).A);
        Near(At(s, 12, 12), 0, 255, 0);
    }

    [Fact]
    public void SelectionAlgebraAddSubtractInverseAndExpand()
    {
        var s = NewSession();
        s.SelectTool(NavigationTool.Marquee);
        void Box(double x0, double y0, double x1, double y1, SelectionMode mode)
        {
            s.BeginLasso(new PointD(x0, y0), mode);
            s.DragMarquee(new PointD(x1, y1), false, false);
            s.FinishLasso();
        }
        Box(0, 0, 50, 50, SelectionMode.Replace);
        Box(40, 40, 90, 70, SelectionMode.Add);
        Assert.True(s.Selection!.Contains(new PointD(80, 60)));
        Box(0, 0, 20, 20, SelectionMode.Subtract);
        Assert.False(s.Selection!.Contains(new PointD(10, 10)));
        Assert.True(s.Selection!.Contains(new PointD(30, 30)));
        s.InvertSelection();
        Assert.True(s.Selection!.Contains(new PointD(10, 10)));
        s.Deselect();
        Assert.Null(s.Selection);
        Box(40, 40, 60, 60, SelectionMode.Replace);
        s.ExpandSelection(5);
        Assert.True(s.Selection!.Contains(new PointD(37, 50)));
        s.ContractSelection(8);
        Assert.False(s.Selection!.Contains(new PointD(41, 50)));
    }

    [Fact]
    public async Task MagicWandSelectsSimilarContiguousPixels()
    {
        var s = NewSession(40, 40);
        var image = Solid(40, 40, SKColors.White);
        var bitmap = image.CopyBitmap();
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawRect(new SKRect(10, 10, 20, 20), new SKPaint { Color = SKColors.Red });
        s.ImportAssets(new[] { PixelOps.Asset(RasterImage.Adopt(bitmap), "Pic") });
        s.SelectTool(NavigationTool.Wand);
        await s.MagicWand(new PointD(15, 15), SelectionMode.Replace);
        Assert.NotNull(s.Selection);
        var bounds = s.Selection!.Bounds;
        Assert.Equal(new RectD(10, 10, 10, 10), bounds);
        Assert.True(HasPartialCoverage(s.Selection.Coverage(40, 40)));
    }

    [Fact]
    public void MasksHideAndInvertLoadsSelection()
    {
        var s = NewSession();
        s.ImportAssets(new[] { PixelOps.Asset(Solid(100, 80, SKColors.Red), "Red") });
        s.AddLayerMask(revealing: false);
        Assert.True(s.IsMaskSelected);
        Assert.Equal(0, At(s, 50, 40).A);
        s.ToggleLayerMask();
        Assert.Equal(255, At(s, 50, 40).A);
        s.ToggleLayerMask();
        s.DeleteLayerMask();
        Assert.Null(s.ActiveLayer!.Mask);
        Assert.Equal(255, At(s, 50, 40).A);
    }

    [Fact]
    public void PaintingAMaskHidesUnderTheBrush()
    {
        var s = NewSession();
        s.ImportAssets(new[] { PixelOps.Asset(Solid(100, 80, SKColors.Red), "Red") });
        s.AddLayerMask(revealing: true);
        s.SelectTool(NavigationTool.Brush);
        s.MaskPaintWhite = false;
        s.BrushSettings = s.BrushSettings with { Diameter = 20, Hardness = 1, Opacity = 1 };
        s.BeginBrush(new PointD(50, 40));
        s.FinishBrush();
        Assert.Equal("Paint Mask", s.History.UndoName);
        Assert.Equal(0, At(s, 50, 40).A);
        Assert.Equal(255, At(s, 5, 5).A);
        var snapshot = s.ProjectSnapshot()!;
        Assert.Equal(100, snapshot.Masks.Values.Single().Image.Width);
    }

    [Fact]
    public void ClippingMaskToggleAndGroupAndMerge()
    {
        var s = NewSession(20, 20);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(10, 10, SKColors.White), "Base") });
        var baseId = s.ActiveLayerId!.Value;
        s.ImportAssets(new[] { PixelOps.Asset(Solid(20, 20, SKColors.Blue), "Blue") });
        var blue = s.ActiveLayerId!.Value;
        Assert.True(s.CanToggleClippingMask(blue));
        s.ToggleClippingMask(blue);
        Assert.Equal(baseId, s.ActiveLayer!.MaskSourceId);
        Assert.Equal(0, At(s, 1, 1).A);
        Near(At(s, 10, 10), 0, 0, 255);
        s.SelectLayers(new HashSet<Guid> { baseId, blue }, blue);
        s.GroupSelectedLayers();
        var group = s.ActiveLayer!;
        Assert.True(group.IsGroup);
        Assert.All(s.Document!.Layers.Where(l => !l.IsGroup && l.Asset != null && l.Name != "Layer 1"), l => Assert.Equal(group.Id, l.ParentId));
        Assert.True(s.CanMergeLayers);
        Assert.Equal("Merge Group", s.MergeTitle);
        s.MergeLayers();
        var merged = s.ActiveLayer!;
        Assert.False(merged.IsGroup);
        Assert.Equal(10, merged.Asset!.Image.Width);
        Near(At(s, 10, 10), 0, 0, 255);
        Assert.Equal(0, At(s, 1, 1).A);
    }

    [Fact]
    public void TransformCommitsMoveScaleRotateAndSnaps()
    {
        var s = NewSession(200, 200);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(20, 20, SKColors.Red), "Red") });
        s.SelectTool(NavigationTool.Move);
        s.BeginTransform(persistent: false);
        var drag = new TransformDrag(s.TransformEditState!.Draft, new PointD(100, 100), DragMode.MoveMode);
        var draft = drag.Updated(new PointD(3, 3), true, false).Rounded();
        draft = s.SnappedMove(draft, new HashSet<Guid> { s.ActiveLayerId!.Value }, 10);
        s.PreviewTransform(draft);
        Assert.Equal(new double[] { 0 }, s.SnapGuides.Xs);
        s.CommitTransform();
        // Dropped at (-7, -7): the smallest snap puts the layer's centre on the canvas corner, as on the Mac.
        Assert.Equal(new PointD(-10, -10), s.ActiveLayer!.Transform.Origin);
        s.BeginTransform();
        var resize = new TransformDrag(s.TransformEditState!.Draft, new PointD(20, 20), DragMode.Resize(4));
        s.PreviewTransform(resize.Updated(new PointD(40, 40), true, false).Rounded());
        s.CommitTransform();
        Assert.Equal(new SizeD(40, 40), s.ActiveLayer!.Transform.Size);
        s.BeginTransform();
        var original = s.TransformEditState!.Draft;
        var center = original.Center;
        var rotate = new TransformDrag(original, center.Offset(20, 0), DragMode.RotateMode);
        s.PreviewTransform(rotate.Updated(center.Offset(0, 20), true, false).Rounded());
        Assert.Equal(90, s.TransformEditState!.Draft.Rotation);
        s.CommitTransform();
        Assert.Equal(90, s.ActiveLayer!.Transform.Rotation);
        s.FlipLayers(horizontally: true);
        Assert.True(s.ActiveLayer!.Transform.FlipX);
        s.Undo();
        Assert.False(s.ActiveLayer!.Transform.FlipX);
    }

    [Fact]
    public void DistortResamplesIntoTheNewShape()
    {
        var s = NewSession(100, 100);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(20, 20, SKColors.Red), "Red") }, new PointD(50, 50));
        s.BeginTransform();
        s.BeginDistort();
        var corners = s.TransformEditState!.Corners!;
        corners[2] = corners[2].Offset(20, 20);
        s.PreviewCorners(corners);
        s.CommitTransform();
        Assert.Equal("Distort", s.History.UndoName);
        Assert.True(s.ActiveLayer!.Transform.Size.Width > 30);
        Near(At(s, 45, 45), 255, 0, 0);
    }

    [Fact]
    public async Task CropCanvasSizeAndImageSize()
    {
        var s = NewSession(100, 80);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(100, 80, SKColors.Red), "Red") });
        s.SelectTool(NavigationTool.Crop);
        s.CropRect = new RectD(10, 10, 50, 40);
        await s.CommitCrop();
        Assert.Equal(50, s.Document!.Width);
        Assert.Equal(new PointD(-10, -10), s.ActiveLayer!.Transform.Origin);
        await s.ResizeCanvas(new CanvasSizeOptions(70, 60, 4, PaletteColor.White));
        Assert.Equal(70, s.Document!.Width);
        Assert.Equal("Canvas Extension", s.Document.Layers[0].Name);
        // The extension is white where the canvas grew and transparent over the old canvas.
        var extension = s.Document.Layers[0].Asset!.Image;
        Assert.Equal((255, 255, 255, 255), extension.PixelAt(1, 1));
        Assert.Equal(0, extension.PixelAt(35, 30).A);
        // Crop kept the red layer's pixels beyond the frame, so they still cover the grown canvas.
        Near(At(s, 1, 1), 255, 0, 0);
        await s.ResizeImage(new ImageSizeOptions(35, 30, 144));
        Assert.Equal(35, s.Document!.Width);
        Assert.Equal(144, s.Document.Resolution);
        s.Undo();
        Assert.Equal(70, s.Document!.Width);
    }

    [Fact]
    public async Task AdjustmentLayerAppliesToWhatIsBeneath()
    {
        var s = NewSession(10, 10);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(10, 10, new SKColor(100, 100, 100)), "Gray") });
        s.AddAdjustment(AdjustmentKind.Levels);
        await Task.Delay(50);
        for (int i = 0; i < 100 && s.Levels == null; i++) await Task.Delay(20);
        Assert.NotNull(s.Levels);
        s.UpdateLevels(s.Levels!.Settings.WithCurrent(new LevelRange(0, 1, 200)), true);
        await s.CommitLevels();
        Assert.Null(s.Levels);
        var adjustment = s.ActiveLayer!.Adjustment!;
        Assert.Equal(200, adjustment.Levels.Ranges[0].White);
        Near(At(s, 5, 5), 128, 128, 128);
        var snapshot = s.ProjectSnapshot()!;
        Near(Straight(ImageExporter.Render(snapshot).Image, 5, 5), 128, 128, 128);
    }

    [Fact]
    public async Task GaussianBlurFilterSpreadsPastTheLayerEdge()
    {
        var s = NewSession(60, 60);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(20, 20, SKColors.Black), "Dot") });
        await s.BeginFilter(FilterKind.GaussianBlur);
        s.UpdateFilter(s.FilterEdit!.Settings with { Radius = 3 }, true);
        await s.CommitFilter();
        Assert.Equal("Gaussian Blur", s.History.UndoName);
        Assert.True(s.ActiveLayer!.Asset!.Image.Width > 20);
        Assert.InRange(At(s, 19, 30).A, 1, 254);
    }

    [Fact]
    public void GradientFillsAcrossTheCanvas()
    {
        var s = NewSession(100, 10);
        s.SelectTool(NavigationTool.Gradient);
        s.GradientSettings = s.GradientSettings with { Style = GradientStyle.ForegroundToBackground };
        s.BeginGradient(new PointD(0, 5));
        s.MoveGradient(end: new PointD(100, 5));
        s.CommitGradient().GetAwaiter().GetResult();
        Assert.Equal("Gradient", s.History.UndoName);
        Assert.True(At(s, 5, 5).R < 30);
        Assert.True(At(s, 95, 5).R > 220);
    }

    [Fact]
    public void ShapeToolMakesAShapeLayerThatRoundTrips()
    {
        var s = NewSession(100, 100);
        s.SelectTool(NavigationTool.Shape);
        s.ShapeCornerRadius = 8;
        s.BeginShape(new PointD(10, 10));
        s.DragShape(new PointD(60, 40), false, false);
        s.FinishShape();
        var layer = s.ActiveLayer!;
        Assert.Equal("Rectangle 1", layer.Name);
        Assert.NotNull(layer.LiveShape);
        using var temp = new TempFolder();
        var path = temp.File("Shape.comp");
        ProjectStore.Save(s.ProjectSnapshot()!, path);
        var reopened = new EditorSession();
        reopened.InstallProject(ProjectStore.Load(path), path);
        Assert.Equal(8, reopened.Document!.Layers.Single(l => l.Id == layer.Id).LiveShape!.Style.CornerRadius);
    }

    [Theory]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Triangle)]
    [InlineData(ShapeKind.Polygon)]
    [InlineData(ShapeKind.Star)]
    public void EveryShapeKindDrawsAndRoundTrips(ShapeKind kind)
    {
        var s = NewSession(100, 100);
        s.SelectTool(NavigationTool.Shape);
        s.ShapeKind = kind;
        s.ShapeSides = 6;
        s.ShapeLineWeight = 6;
        s.BeginShape(new PointD(10, 10));
        s.DragShape(new PointD(70, 60), false, false);
        s.FinishShape();
        var layer = s.ActiveLayer!;
        Assert.Equal($"{kind} 1", layer.Name);
        Assert.Equal(kind, layer.LiveShape!.Style.Kind);
        Assert.True(At(s, 40, 35).A > 200, "the shape covers its centre");
        using var temp = new TempFolder();
        var path = temp.File("Shapes.comp");
        ProjectStore.Save(s.ProjectSnapshot()!, path);
        var json = File.ReadAllText(Path.Combine(path, "manifest.json"));
        // Rectangles and ellipses stay in the Mac's "shape" key; the other kinds use a key the Mac app ignores.
        Assert.Equal(kind == ShapeKind.Ellipse, json.Contains("\"shape\""));
        var reopened = new EditorSession();
        reopened.InstallProject(ProjectStore.Load(path), path);
        var style = reopened.Document!.Layers.Single(l => l.Id == layer.Id).LiveShape!.Style;
        Assert.Equal(layer.LiveShape.Style, style);
    }

    [Fact]
    public void AlignMovesOneLayerAgainstTheCanvasAndSeveralAgainstEachOther()
    {
        var s = NewSession(100, 100);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(20, 10, SKColors.Red), "A") });
        var a = s.ActiveLayerId!.Value;
        s.Align(AlignEdge.Left);
        Assert.Equal(0, s.Document!.Layer(a)!.Transform.Origin.X);
        s.Align(AlignEdge.Bottom);
        Assert.Equal(90, s.Document!.Layer(a)!.Transform.Origin.Y);
        Assert.Equal("Align Bottom Edges", s.History.UndoName);

        s.ImportAssets(new[] { PixelOps.Asset(Solid(10, 10, SKColors.Blue), "B") });
        var b = s.ActiveLayerId!.Value;
        s.SelectLayers(new HashSet<Guid> { a, b }, b);
        s.Align(AlignEdge.Top);
        Assert.Equal(45, s.Document!.Layer(a)!.Transform.Origin.Y);
        Assert.Equal(45, s.Document!.Layer(b)!.Transform.Origin.Y);
        s.Undo();
        Assert.Equal(90, s.Document!.Layer(a)!.Transform.Origin.Y);
    }

    [Fact]
    public void DistributeSpacesCentresEvenly()
    {
        var s = NewSession(200, 100);
        var ids = new List<Guid>();
        foreach (var x in new[] { 0.0, 30, 150 })
        {
            s.ImportAssets(new[] { PixelOps.Asset(Solid(10, 10, SKColors.Red), "L") });
            var id = s.ActiveLayerId!.Value;
            ids.Add(id);
            s.UpdateLayer(id, l => l with { Transform = l.Transform with { Origin = new PointD(x, 0) } });
        }
        s.SelectLayers(ids.ToHashSet(), ids[0]);
        s.Distribute(DistributeAxis.Horizontal);
        Assert.Equal(75, s.Document!.Layer(ids[1])!.Transform.Origin.X);
    }

    [Fact]
    public void PlacedImagesCentreAndShrinkToFitTheCanvas()
    {
        var s = NewSession(100, 50);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(400, 100, SKColors.Red), "Wide") }, fitToCanvas: true);
        var t = s.ActiveLayer!.Transform;
        Assert.Equal(new SizeD(100, 25), t.Size);
        Assert.Equal(new PointD(0, 12), t.Origin);
        Assert.Equal(400, s.ActiveLayer!.Asset!.Image.Width);
    }

    [Fact]
    public void PerCornerRadiiRoundOnlyThoseCornersAndRoundTrip()
    {
        var s = NewSession(100, 100);
        s.SelectTool(NavigationTool.Shape);
        s.ShapeCornerRadii = new ShapeCorners(30, 0, 0, 0);
        s.BeginShape(new PointD(10, 10));
        s.DragShape(new PointD(90, 90), false, false);
        s.FinishShape();
        Assert.True(At(s, 12, 12).A < 10, "the rounded top-left corner is cut away");
        Assert.True(At(s, 88, 11).A > 240, "the square top-right corner stays filled");
        using var temp = new TempFolder();
        var path = temp.File("Corners.lform");
        ProjectStore.Save(s.ProjectSnapshot()!, path);
        Assert.Contains("\"corners\"", File.ReadAllText(Path.Combine(path, "manifest.json")));
        var reopened = new EditorSession();
        reopened.InstallProject(ProjectStore.Load(path), path);
        Assert.Equal(new ShapeCorners(30, 0, 0, 0), reopened.ActiveLayer!.LiveShape!.Style.Corners);
    }

    [Fact]
    public void LayerEffectsRecolorGradientAndShadow()
    {
        var s = NewSession(100, 100);
        var bitmap = PixelOps.NewRgba(40, 40);
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawCircle(20, 20, 15, new SKPaint { Color = SKColors.Blue, IsAntialias = true });
        s.ImportAssets(new[] { PixelOps.Asset(RasterImage.Adopt(bitmap), "Dot") });
        var id = s.ActiveLayerId!.Value;
        s.RecolorLayer(new PaletteColor(1, 0, 0), RecolorMode.Replace);
        Near(At(s, 50, 50), 255, 0, 0, 255);
        Assert.Equal(0, At(s, 31, 31).A);   // outside the circle stays clear
        Assert.Equal("Change Color", s.History.UndoName);

        s.GradientOverlayLayer(new GradientOverlaySettings(new PaletteColor(0, 0, 0), new PaletteColor(1, 1, 1), Angle: 0));
        Assert.True(At(s, 37, 50).R < At(s, 63, 50).R, "left is darker than right");

        s.AddShadow(new ShadowSettings(PaletteColor.Black, 1, 10, 10, 0));
        var layers = s.Document!.Layers;
        Assert.Equal("Dot Shadow", layers[layers.FindIndex(l => l.Id == id) - 1].Name);
        Assert.Equal(id, s.ActiveLayerId);
        Assert.True(At(s, 70, 62).A > 200, "the shadow shows below and to the right");
    }

    [Fact]
    public void SessionSaveReopenPreservesEverything()
    {
        var s = NewSession(50, 50);
        s.ImportAssets(new[] { PixelOps.Asset(Solid(30, 30, SKColors.Red), "Red") });
        s.AddLayerMask(revealing: true);
        s.SelectLayerTarget(s.ActiveLayerId!.Value, false);
        s.SetLayerBlendMode(LayerBlendMode.Screen);
        s.SetLayerOpacity(0.4);
        s.AddGroup();
        s.AddAdjustment(AdjustmentKind.Exposure);
        s.CancelFilter();
        using var temp = new TempFolder();
        var path = temp.File("Session.comp");
        ProjectStore.Save(s.ProjectSnapshot()!, path);
        var reopened = new EditorSession();
        reopened.InstallProject(ProjectStore.Load(path), path);
        Assert.Equal(s.Document!.Layers.Select(l => (l.Id, l.Name, l.IsGroup, l.Opacity, l.BlendMode, l.Transform)),
            reopened.Document!.Layers.Select(l => (l.Id, l.Name, l.IsGroup, l.Opacity, l.BlendMode, l.Transform)));
        Assert.False(reopened.IsModified);
        Assert.True(ImageExporter.Render(s.ProjectSnapshot()!).Image.Pixels.SequenceEqual(ImageExporter.Render(reopened.ProjectSnapshot()!).Image.Pixels));
    }

    private static void Near((byte R, byte G, byte B, byte A) actual, int r, int g, int b, int a = 255, int tolerance = 3)
    {
        Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance
            && Math.Abs(actual.A - a) <= tolerance, $"expected ({r},{g},{b},{a}) got ({actual.R},{actual.G},{actual.B},{actual.A})");
    }
}
