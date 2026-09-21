using System.Text.Json.Nodes;
using Compositor.Format;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;
using Xunit;
using static Compositor.Tests.Fixtures;

namespace Compositor.Tests;

public class ProjectCompatibilityTests
{
    private static void Near((byte R, byte G, byte B, byte A) actual, int r, int g, int b, int a = 255, int tolerance = 2)
    {
        Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance
            && Math.Abs(actual.A - a) <= tolerance, $"expected ({r},{g},{b},{a}) got ({actual.R},{actual.G},{actual.B},{actual.A})");
    }

    /// <summary>Blend modes, a layer mask, a folder mask and opacity, from a manifest written the way the Mac app writes it.</summary>
    private static string BlendAndMaskProject(string folder, out Guid multiplyId, out Guid greenId)
    {
        Guid doc = Guid.NewGuid(), background = Guid.NewGuid(), multiply = Guid.NewGuid(), group = Guid.NewGuid(), green = Guid.NewGuid();
        multiplyId = multiply; greenId = green;
        var layers = new[]
        {
            Layer(background, "Background", Transform(0, 0, 8, 8), $"      \"imageFile\" : \"{Id(background)}.png\""),
            Layer(multiply, "Multiply", Transform(0, 0, 8, 8),
                $"      \"blendMode\" : \"Multiply\",\n      \"imageFile\" : \"{Id(multiply)}.png\",\n      \"maskEnabled\" : true,\n      \"maskFile\" : \"{Id(multiply)}.mask.png\",\n      \"opacity\" : 1"),
            Layer(group, "Folder 1", Transform(0, 0, 8, 8),
                $"      \"blendMode\" : \"Normal\",\n      \"isGroup\" : true,\n      \"maskEnabled\" : true,\n      \"maskFile\" : \"{Id(group)}.mask.png\",\n      \"opacity\" : 1"),
            Layer(green, "Green", Transform(0, 0, 8, 8),
                $"      \"blendMode\" : \"Normal\",\n      \"imageFile\" : \"{Id(green)}.png\",\n      \"isGroup\" : false,\n      \"opacity\" : 0.5,\n      \"parentID\" : \"{Id(group)}\""),
        };
        return WritePackage(folder, "Blend.comp", Manifest(doc, 8, 8, 7, green, layers), new()
        {
            [$"{Id(background)}.png"] = SolidPng(8, 8, 255, 0, 0),
            [$"{Id(multiply)}.png"] = SolidPng(8, 8, 128, 128, 128),
            [$"{Id(multiply)}.mask.png"] = GrayPng(8, 8, (x, _) => x < 4 ? (byte)255 : (byte)0),
            [$"{Id(group)}.mask.png"] = GrayPng(8, 8, (_, y) => y < 4 ? (byte)255 : (byte)0),
            [$"{Id(green)}.png"] = SolidPng(8, 8, 0, 255, 0),
        });
    }

    [Fact]
    public void MacProjectRendersBlendModesMasksAndFolderMasks()
    {
        using var temp = new TempFolder();
        var package = BlendAndMaskProject(temp.Path, out _, out _);
        var snapshot = ProjectStore.Load(package);
        Assert.Equal(4, snapshot.Manifest.Layers.Count);
        Assert.Equal(2, snapshot.Masks.Count);
        var image = ImageExporter.Render(snapshot).Image;
        // Multiply by 50% gray where the mask reveals (left), plain red where it hides (right).
        Near(Straight(image, 1, 6), 128, 0, 0);
        Near(Straight(image, 6, 6), 255, 0, 0);
        // The folder mask reveals the top half, where 50% green lies over the result.
        Near(Straight(image, 1, 1), 64, 128, 0);
        Near(Straight(image, 6, 1), 128, 128, 0);
    }

    [Fact]
    public void ClippingStackSharesTheBaseAlpha()
    {
        using var temp = new TempFolder();
        Guid doc = Guid.NewGuid(), b = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid();
        var layers = new[]
        {
            Layer(b, "Base", Transform(2, 2, 4, 4), $"      \"imageFile\" : \"{Id(b)}.png\""),
            Layer(c1, "Blue", Transform(0, 0, 8, 8), $"      \"imageFile\" : \"{Id(c1)}.png\",\n      \"maskSourceID\" : \"{Id(b)}\""),
            Layer(c2, "Red", Transform(0, 0, 8, 8), $"      \"imageFile\" : \"{Id(c2)}.png\",\n      \"maskSourceID\" : \"{Id(b)}\",\n      \"opacity\" : 0.5"),
        };
        var package = WritePackage(temp.Path, "Clip.comp", Manifest(doc, 8, 8, 5, null, layers), new()
        {
            [$"{Id(b)}.png"] = SolidPng(4, 4, 255, 255, 255),
            [$"{Id(c1)}.png"] = SolidPng(8, 8, 0, 0, 255),
            [$"{Id(c2)}.png"] = SolidPng(8, 8, 255, 0, 0),
        });
        var image = ImageExporter.Render(ProjectStore.Load(package)).Image;
        Near(Straight(image, 3, 3), 128, 0, 128);
        Assert.Equal(0, image.PixelAt(0, 0).A);
        Assert.Equal(0, image.PixelAt(7, 7).A);
    }

    private const string MasterDarkenHsv = """
        "hsvSettings" : {
          "adjustments" : [
            "Master",
            {
              "hue" : 0,
              "lightness" : -100,
              "saturation" : 0
            }
          ],
          "bands" : [
            "Reds",
            {
              "falloffEnd" : 45,
              "falloffStart" : 315,
              "rangeEnd" : 15,
              "rangeStart" : 345
            },
            "Master",
            {
              "falloffEnd" : 360,
              "falloffStart" : 0,
              "rangeEnd" : 360,
              "rangeStart" : 0
            }
          ],
          "colorize" : false,
          "invertRange" : false,
          "range" : "Master"
        }
        """;

    private static string LevelsJson => """
        "levels" : {
          "channel" : "RGB",
          "ranges" : [
            { "black" : 0, "gamma" : 1, "outputBlack" : 0, "outputWhite" : 255, "white" : 255 },
            { "black" : 0, "gamma" : 1, "outputBlack" : 0, "outputWhite" : 255, "white" : 255 },
            { "black" : 0, "gamma" : 1, "outputBlack" : 0, "outputWhite" : 255, "white" : 255 },
            { "black" : 0, "gamma" : 1, "outputBlack" : 0, "outputWhite" : 255, "white" : 255 }
          ]
        }
        """;

    private static string CurvesJson => """
        "curves" : {
          "channel" : "RGB",
          "channels" : [
            [ { "x" : 0, "y" : 0 }, { "x" : 255, "y" : 255 } ],
            [ { "x" : 0, "y" : 0 }, { "x" : 255, "y" : 255 } ],
            [ { "x" : 0, "y" : 0 }, { "x" : 255, "y" : 255 } ],
            [ { "x" : 0, "y" : 0 }, { "x" : 255, "y" : 255 } ]
          ]
        }
        """;

    private static string AdjustmentProject(string folder)
    {
        Guid doc = Guid.NewGuid(), background = Guid.NewGuid(), adjustment = Guid.NewGuid();
        string adjustmentJson = "      \"adjustment\" : {\n        \"colorize\" : false,\n" + CurvesJson + ",\n" + MasterDarkenHsv
            + ",\n        \"hue\" : 0,\n        \"kind\" : \"Hue/Saturation\",\n" + LevelsJson + ",\n        \"lightness\" : 0,\n        \"saturation\" : 0\n      },\n"
            + $"      \"maskEnabled\" : true,\n      \"maskFile\" : \"{Id(adjustment)}.mask.png\"";
        var layers = new[]
        {
            Layer(background, "Background", Transform(0, 0, 4, 4), $"      \"imageFile\" : \"{Id(background)}.png\""),
            Layer(adjustment, "Hue/Saturation", Transform(0, 0, 4, 4), adjustmentJson),
        };
        return WritePackage(folder, "Adjust.comp", Manifest(doc, 4, 4, 7, adjustment, layers), new()
        {
            [$"{Id(background)}.png"] = SolidPng(4, 4, 255, 0, 0),
            [$"{Id(adjustment)}.mask.png"] = GrayPng(4, 4, (x, _) => x < 2 ? (byte)255 : (byte)0),
        });
    }

    [Fact]
    public void Version7AdjustmentLayerDecodesSwiftDictionaryArraysAndRenders()
    {
        using var temp = new TempFolder();
        var snapshot = ProjectStore.Load(AdjustmentProject(temp.Path));
        var adjustment = snapshot.Manifest.Layers[1].Adjustment!;
        Assert.Equal(AdjustmentKind.HueSaturation, adjustment.Kind);
        Assert.Equal(-100, adjustment.ResolvedHsv.Adjustments[ColorRange.Master].Lightness);
        Assert.Equal(new HueBand(315, 345, 15, 45), adjustment.ResolvedHsv.Bands[ColorRange.Reds]);
        var image = ImageExporter.Render(snapshot).Image;
        Near(Straight(image, 0, 0), 0, 0, 0);   // Masked in: lightness −100 is black.
        Near(Straight(image, 3, 3), 255, 0, 0); // Masked out: untouched.
    }

    [Fact]
    public void SaveWritesSwiftCompatibleJsonAndRoundTripsEveryField()
    {
        using var temp = new TempFolder();
        var original = ProjectStore.Load(AdjustmentProject(temp.Path));
        var copy = Path.Combine(temp.Path, "Copy.comp");
        ProjectStore.Save(original, copy);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, "manifest.json")))!.AsObject();
        Assert.Equal(7, (int)json["version"]!);
        var layer = json["layers"]![1]!.AsObject();
        var transform = layer["transform"]!.AsObject();
        Assert.IsType<JsonArray>(transform["origin"]);
        Assert.Equal("High quality", (string)transform["sampling"]!);
        var hsv = layer["adjustment"]!["hsvSettings"]!.AsObject();
        // Swift writes [ColorRange: …] dictionaries as flat key/value arrays.
        var adjustments = hsv["adjustments"]!.AsArray();
        Assert.Equal("Master", (string)adjustments[0]!);
        Assert.Equal(-100, (double)adjustments[1]!["lightness"]!);
        // Every non-optional LayerAdjustment property is present, as Swift's decoder requires.
        foreach (var key in new[] { "kind", "hue", "saturation", "lightness", "colorize", "levels", "curves" })
            Assert.True(layer["adjustment"]!.AsObject().ContainsKey(key), key);
        Assert.Equal(json["layers"]![1]!["id"]!.ToString(), json["layers"]![1]!["id"]!.ToString().ToUpperInvariant());
        var reopened = ProjectStore.Load(copy);
        Assert.Equal(original.Manifest.DocumentId, reopened.Manifest.DocumentId);
        Assert.Equal(original.Manifest.ActiveLayerId, reopened.Manifest.ActiveLayerId);
        Assert.Equal(original.Manifest.Layers.Count, reopened.Manifest.Layers.Count);
        for (int i = 0; i < original.Manifest.Layers.Count; i++)
        {
            var a = original.Manifest.Layers[i];
            var b = reopened.Manifest.Layers[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Transform, b.Transform);
            Assert.Equal(a.MaskFile, b.MaskFile);
            Assert.Equal(a.Adjustment, b.Adjustment);
        }
        // The mask was written as 8-bit gray without alpha.
        var maskFile = Directory.GetFiles(Path.Combine(copy, "images"), "*.mask.png").Single();
        var header = Codecs.ReadPngHeader(File.ReadAllBytes(maskFile))!.Value;
        Assert.True(header.IsGray && header.BitDepth == 8);
        var before = ImageExporter.Render(original).Image;
        var after = ImageExporter.Render(reopened).Image;
        Assert.True(before.Pixels.SequenceEqual(after.Pixels));
    }

    [Fact]
    public void EditSaveReopenAndExportProducesTheExpectedImage()
    {
        using var temp = new TempFolder();
        var package = BlendAndMaskProject(temp.Path, out var multiply, out var green);
        var snapshot = ProjectStore.Load(package);
        // Edit: the multiply layer becomes Screen at 100%, the green layer 100% opaque.
        var layers = snapshot.Manifest.Layers.Select(l =>
            l.Id == multiply ? l with { BlendMode = LayerBlendMode.Screen } :
            l.Id == green ? l with { Opacity = 1.0, Name = "Green (edited)" } : l).ToList();
        var edited = snapshot with { Manifest = snapshot.Manifest with { Layers = layers } };
        ProjectStore.Save(edited, package); // Overwrite in place.
        var reopened = ProjectStore.Load(package);
        Assert.Equal(LayerBlendMode.Screen, reopened.Manifest.Layers.Single(l => l.Id == multiply).BlendMode);
        Assert.Equal("Green (edited)", reopened.Manifest.Layers.Single(l => l.Id == green).Name);
        var png = Path.Combine(temp.Path, "Export.png");
        ImageExporter.ExportPng(reopened, png);
        var bytes = File.ReadAllBytes(png);
        Assert.Equal(72, Codecs.ReadPngResolution(bytes)!.Value, 1);
        var exported = Codecs.DecodeRgba(bytes);
        // Screen of red with 50% gray: 255, 128, 128 on the left; the folder reveals opaque green on top.
        Near(Straight(exported, 1, 6), 255, 128, 128);
        Near(Straight(exported, 6, 6), 255, 0, 0);
        Near(Straight(exported, 1, 1), 0, 255, 0);
        // No staging directories are left behind.
        Assert.Single(Directory.GetDirectories(temp.Path));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    public void UnsupportedVersionsAreRejected(int version)
    {
        using var temp = new TempFolder();
        var id = Guid.NewGuid();
        var package = WritePackage(temp.Path, "V.comp", Manifest(Guid.NewGuid(), 4, 4, version, null,
            new[] { Layer(id, "Layer 1", Transform(0, 0, 4, 4)) }), new());
        var error = Assert.Throws<ProjectException>(() => ProjectStore.Load(package));
        Assert.Equal(ProjectErrorKind.Version, error.Kind);
    }

    [Fact]
    public void DamagedMetadataIsRejected()
    {
        using var temp = new TempFolder();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        // A folder cycle.
        var cycle = WritePackage(temp.Path, "Cycle.comp", Manifest(Guid.NewGuid(), 4, 4, 7, null, new[]
        {
            Layer(a, "A", Transform(0, 0, 4, 4), $"      \"isGroup\" : true,\n      \"parentID\" : \"{Id(b)}\""),
            Layer(b, "B", Transform(0, 0, 4, 4), $"      \"isGroup\" : true,\n      \"parentID\" : \"{Id(a)}\""),
        }), new());
        Assert.Equal(ProjectErrorKind.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(cycle)).Kind);
        // A mask file name that does not match the layer, and a path escaping the package.
        var unsafeName = WritePackage(temp.Path, "Unsafe.comp", Manifest(Guid.NewGuid(), 4, 4, 7, null, new[]
        {
            Layer(a, "A", Transform(0, 0, 4, 4), "      \"imageFile\" : \"../../evil.png\""),
        }), new());
        Assert.Throws<ProjectException>(() => ProjectStore.Load(unsafeName));
        // Masks before version 4.
        var early = WritePackage(temp.Path, "Early.comp", Manifest(Guid.NewGuid(), 4, 4, 3, null, new[]
        {
            Layer(a, "A", Transform(0, 0, 4, 4), $"      \"maskFile\" : \"{Id(a)}.mask.png\""),
        }), new() { [$"{Id(a)}.mask.png"] = GrayPng(4, 4, (_, _) => 255) });
        Assert.Throws<ProjectException>(() => ProjectStore.Load(early));
        // A mask saved with alpha is not a valid mask.
        var alphaMask = WritePackage(temp.Path, "AlphaMask.comp", Manifest(Guid.NewGuid(), 4, 4, 7, null, new[]
        {
            Layer(a, "A", Transform(0, 0, 4, 4), $"      \"maskFile\" : \"{Id(a)}.mask.png\""),
        }), new() { [$"{Id(a)}.mask.png"] = SolidPng(4, 4, 255, 255, 255) });
        Assert.Throws<ProjectException>(() => ProjectStore.Load(alphaMask));
        // A clipping mask onto a missing layer.
        var dangling = WritePackage(temp.Path, "Dangling.comp", Manifest(Guid.NewGuid(), 4, 4, 7, null, new[]
        {
            Layer(a, "A", Transform(0, 0, 4, 4), $"      \"maskSourceID\" : \"{Id(Guid.NewGuid())}\""),
        }), new());
        Assert.Throws<ProjectException>(() => ProjectStore.Load(dangling));
    }

    [Fact]
    public void FailedSaveLeavesThePreviousPackageIntact()
    {
        using var temp = new TempFolder();
        var package = BlendAndMaskProject(temp.Path, out _, out _);
        var before = File.ReadAllBytes(Path.Combine(package, "manifest.json"));
        var snapshot = ProjectStore.Load(package);
        var broken = snapshot with { Manifest = snapshot.Manifest with { Version = 99 } };
        Assert.ThrowsAny<Exception>(() => ProjectStore.Save(broken, package));
        var missing = snapshot with { Images = new Dictionary<Guid, ImageAsset>() };
        Assert.ThrowsAny<Exception>(() => ProjectStore.Save(missing, package));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(package, "manifest.json")));
        Assert.Equal(4, ProjectStore.Load(package).Manifest.Layers.Count);
    }

    [Fact]
    public void TransformsPlaceFlippedRotatedAndScaledLayers()
    {
        using var temp = new TempFolder();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        // A 2×1 image: red on the left, blue on the right.
        var pixels = Png(2, 1, (x, _) => x == 0 ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255));
        var package = WritePackage(temp.Path, "T.comp", Manifest(Guid.NewGuid(), 8, 8, 7, null, new[]
        {
            Layer(a, "Flipped", Transform(0, 0, 4, 2, flipX: true, sampling: "Nearest"), $"      \"imageFile\" : \"{Id(a)}.png\""),
            Layer(b, "Rotated", Transform(0, 4, 4, 2, rotation: 180, sampling: "Nearest"), $"      \"imageFile\" : \"{Id(b)}.png\""),
        }), new() { [$"{Id(a)}.png"] = pixels, [$"{Id(b)}.png"] = pixels });
        var image = ImageExporter.Render(ProjectStore.Load(package)).Image;
        Near(Straight(image, 0, 0), 0, 0, 255);
        Near(Straight(image, 3, 1), 255, 0, 0);
        Near(Straight(image, 0, 5), 0, 0, 255);
        Near(Straight(image, 3, 4), 255, 0, 0);
    }
}
