using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compositor.Geometry;
using Compositor.Model;

namespace Compositor.Format;

/// <summary>
/// Reads and writes manifest.json exactly as the Mac app's Swift <c>Codable</c> types do: points and sizes as
/// two-element arrays, UUIDs as uppercase strings, optional fields omitted when nil, and dictionaries keyed by a
/// String-backed enum (Hue/Saturation ranges and bands) as flat [key, value, key, value] arrays, which is how
/// Swift encodes a dictionary whose key type is not String, Int or CodingKeyRepresentable.
/// </summary>
public static class ProjectJson
{
    public static string FormatGuid(Guid id) => id.ToString("D").ToUpperInvariant();

    // MARK: Reading

    public static (string Format, int Version) ReadHeader(byte[] data)
    {
        try
        {
            var root = JsonNode.Parse(data) as JsonObject ?? throw ProjectException.Invalid();
            return (Str(root, "format"), Int(root, "version"));
        }
        catch (ProjectException) { throw; }
        catch (Exception e) { throw ProjectException.Invalid(e); }
    }

    public static ProjectManifest ReadManifest(byte[] data)
    {
        try
        {
            var root = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject
                ?? throw ProjectException.Invalid();
            var layers = Arr(root, "layers").Select(n => ReadLayer(Obj(n))).ToList();
            return new ProjectManifest
            {
                Format = OptStr(root, "format") ?? ProjectManifest.FormatIdentifier,
                Version = OptInt(root, "version") ?? ProjectManifest.CurrentVersion,
                ColorSpace = OptStr(root, "colorSpace") ?? "sRGB",
                Resolution = OptDouble(root, "resolution"),
                DocumentId = Uuid(root, "documentID"),
                Width = Int(root, "width"),
                Height = Int(root, "height"),
                ActiveLayerId = OptUuid(root, "activeLayerID"),
                Layers = layers,
            };
        }
        catch (ProjectException) { throw; }
        catch (Exception e) { throw ProjectException.Invalid(e); }
    }

    private static ProjectLayerRecord ReadLayer(JsonObject o)
    {
        LayerBlendMode? blend = null;
        if (OptStr(o, "blendMode") is { } blendName)
            blend = BlendModes.TryParse(blendName, out var mode) ? mode : throw ProjectException.Invalid();
        return new ProjectLayerRecord
        {
            Id = Uuid(o, "id"),
            Name = Str(o, "name"),
            IsVisible = Bool(o, "isVisible"),
            IsLocked = OptBool(o, "isLocked"),
            Transform = ReadTransform(Obj(o["transform"])),
            ImageFile = OptStr(o, "imageFile"),
            ParentId = OptUuid(o, "parentID"),
            IsGroup = OptBool(o, "isGroup"),
            Opacity = OptDouble(o, "opacity"),
            BlendMode = blend,
            MaskFile = OptStr(o, "maskFile"),
            MaskEnabled = OptBool(o, "maskEnabled"),
            MaskSourceId = OptUuid(o, "maskSourceID"),
            Adjustment = IsPresent(o, "adjustment") ? ReadAdjustment(Obj(o["adjustment"])) : null,
            MaskPlacement = IsPresent(o, "maskPlacement") ? ReadTransform(Obj(o["maskPlacement"])) : null,
            MaskLinked = OptBool(o, "maskLinked"),
            Shape = IsPresent(o, "shape") ? ReadShape(Obj(o["shape"])) : IsPresent(o, LayerFormShapeKey) ? ReadLayerFormShape(Obj(o[LayerFormShapeKey])) : null,
            Text = IsPresent(o, "text") ? ReadText(Obj(o["text"])) : null,
        };
    }

    private static LayerTextStyle ReadText(JsonObject o)
    {
        var alignment = OptStr(o, "alignment") switch
        {
            "Center" => LayerTextAlignment.Center,
            "Right" => LayerTextAlignment.Right,
            _ => LayerTextAlignment.Left,
        };
        SizeD? box = null;
        if (IsPresent(o, "boxSize"))
        {
            var pair = Pair(o, "boxSize");
            box = new SizeD(pair.A, pair.B);
        }
        var style = new LayerTextStyle
        {
            Content = Str(o, "content"), FontName = Str(o, "fontName"), FontSize = Dbl(o, "fontSize"),
            Red = Dbl(o, "red"), Green = Dbl(o, "green"), Blue = Dbl(o, "blue"), Alignment = alignment,
            Tracking = OptDouble(o, "tracking") ?? 0, Leading = OptDouble(o, "leading") ?? 0, BoxSize = box,
        };
        return style.IsValid ? style : throw ProjectException.Invalid();
    }

    public static LayerTransform ReadTransform(JsonObject o)
    {
        var origin = Pair(o, "origin");
        var size = Pair(o, "size");
        var sampling = LayerSampling.High;
        if (OptStr(o, "sampling") is { } name && !LayerSamplingNames.TryParse(name, out sampling)) throw ProjectException.Invalid();
        return new LayerTransform(new PointD(origin.A, origin.B), new SizeD(size.A, size.B),
            OptDouble(o, "rotation") ?? 0, OptBool(o, "flipX") ?? false, OptBool(o, "flipY") ?? false, sampling);
    }

    private static LayerShapeStyle ReadShape(JsonObject o)
    {
        var kind = Str(o, "kind") switch
        {
            "Rectangle" => ShapeKind.Rectangle,
            "Ellipse" => ShapeKind.Ellipse,
            _ => throw ProjectException.Invalid(),
        };
        return new LayerShapeStyle(kind, Dbl(o, "red"), Dbl(o, "green"), Dbl(o, "blue"), OptDouble(o, "cornerRadius") ?? 0);
    }

    /// <summary>Layer Form's extra shapes (lines, triangles, polygons, stars) live under their own key, which the Mac app
    /// ignores: it opens those layers as ordinary pixel layers, and nothing is lost when Layer Form reads them back.</summary>
    public const string LayerFormShapeKey = "layerFormShape";

    private static LayerShapeStyle ReadLayerFormShape(JsonObject o)
    {
        if (!ShapeKinds.TryParse(OptStr(o, "kind"), out var kind)) throw ProjectException.Invalid();
        int sides = (int)Math.Round(OptDouble(o, "sides") ?? 5);
        double weight = OptDouble(o, "weight") ?? 4;
        if (sides < 3 || sides > 100 || !double.IsFinite(weight) || weight <= 0) throw ProjectException.Invalid();
        return new LayerShapeStyle(kind, Dbl(o, "red"), Dbl(o, "green"), Dbl(o, "blue"), OptDouble(o, "cornerRadius") ?? 0,
            sides, weight, OptBool(o, "rising") ?? false, ReadCorners(o));
    }

    private static ShapeCorners? ReadCorners(JsonObject o)
    {
        if (!IsPresent(o, "corners")) return null;
        var values = Arr(o, "corners").Select(n => n?.GetValue<double>() ?? throw ProjectException.Invalid()).ToArray();
        if (values.Length != 4 || values.Any(v => !double.IsFinite(v) || v < 0)) throw ProjectException.Invalid();
        return new ShapeCorners(values[0], values[1], values[2], values[3]);
    }

    public static LayerAdjustment ReadAdjustment(JsonObject o)
    {
        if (!AdjustmentKinds.TryParse(Str(o, "kind"), out var kind)) throw ProjectException.Invalid();
        return new LayerAdjustment(kind)
        {
            Hue = OptDouble(o, "hue") ?? 0,
            Saturation = OptDouble(o, "saturation") ?? 0,
            Lightness = OptDouble(o, "lightness") ?? 0,
            Colorize = OptBool(o, "colorize") ?? false,
            HsvSettings = IsPresent(o, "hsvSettings") ? ReadHsv(Obj(o["hsvSettings"])) : null,
            Levels = IsPresent(o, "levels") ? ReadLevels(Obj(o["levels"])) : new LevelsSettings(),
            Curves = IsPresent(o, "curves") ? ReadCurves(Obj(o["curves"])) : new CurvesSettings(),
            ExposureSettings = IsPresent(o, "exposureSettings") ? ReadExposure(Obj(o["exposureSettings"])) : null,
            GradientMapSettings = IsPresent(o, "gradientMapSettings") ? ReadGradientMap(Obj(o["gradientMapSettings"])) : null,
            GrainSettings = IsPresent(o, "grainSettings") ? ReadGrain(Obj(o["grainSettings"])) : null,
        };
    }

    private static HueSaturationSettings ReadHsv(JsonObject o)
    {
        var settings = new HueSaturationSettings();
        if (OptStr(o, "range") is { } rangeName)
            settings = settings with { Range = ColorRanges.TryParse(rangeName, out var range) ? range : throw ProjectException.Invalid() };
        settings = settings with { Colorize = OptBool(o, "colorize") ?? false, InvertRange = OptBool(o, "invertRange") ?? false };
        if (IsPresent(o, "adjustments"))
        {
            var adjustments = ReadRangeDictionary(o["adjustments"]!, node =>
            {
                var a = Obj(node);
                return new RangeAdjustment(OptDouble(a, "hue") ?? 0, OptDouble(a, "saturation") ?? 0, OptDouble(a, "lightness") ?? 0);
            });
            settings = settings with { Adjustments = adjustments };
        }
        if (IsPresent(o, "bands"))
        {
            var bands = ReadRangeDictionary(o["bands"]!, node =>
            {
                var b = Obj(node);
                return new HueBand(Dbl(b, "falloffStart"), Dbl(b, "rangeStart"), Dbl(b, "rangeEnd"), Dbl(b, "falloffEnd"));
            });
            settings = settings with { Bands = bands };
        }
        return settings;
    }

    /// <summary>Accepts Swift's [key, value, …] array and, defensively, a keyed object.</summary>
    private static Dictionary<ColorRange, T> ReadRangeDictionary<T>(JsonNode node, Func<JsonNode, T> read)
    {
        var result = new Dictionary<ColorRange, T>();
        if (node is JsonArray array)
        {
            if (array.Count % 2 != 0) throw ProjectException.Invalid();
            for (int i = 0; i < array.Count; i += 2)
            {
                var key = array[i]?.GetValue<string>();
                if (!ColorRanges.TryParse(key, out var range)) throw ProjectException.Invalid();
                result[range] = read(array[i + 1] ?? throw ProjectException.Invalid());
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (!ColorRanges.TryParse(pair.Key, out var range)) throw ProjectException.Invalid();
                result[range] = read(pair.Value ?? throw ProjectException.Invalid());
            }
        }
        else throw ProjectException.Invalid();
        return result;
    }

    private static LevelsSettings ReadLevels(JsonObject o)
    {
        var channel = LevelsChannel.Rgb;
        if (OptStr(o, "channel") is { } name && !LevelsChannels.TryParse(name, out channel)) throw ProjectException.Invalid();
        var ranges = Arr(o, "ranges").Select(n =>
        {
            var r = Obj(n);
            return new LevelRange(OptDouble(r, "black") ?? 0, OptDouble(r, "gamma") ?? 1, OptDouble(r, "white") ?? 255,
                OptDouble(r, "outputBlack") ?? 0, OptDouble(r, "outputWhite") ?? 255);
        }).ToArray();
        return new LevelsSettings { Channel = channel, Ranges = ranges };
    }

    private static CurvesSettings ReadCurves(JsonObject o)
    {
        var channel = LevelsChannel.Rgb;
        if (OptStr(o, "channel") is { } name && !LevelsChannels.TryParse(name, out channel)) throw ProjectException.Invalid();
        var channels = Arr(o, "channels").Select(list => (IReadOnlyList<CurvePoint>)(list as JsonArray ?? throw ProjectException.Invalid())
            .Select(p => { var point = Obj(p); return new CurvePoint(Dbl(point, "x"), Dbl(point, "y")); }).ToArray()).ToArray();
        return new CurvesSettings { Channel = channel, Channels = channels };
    }

    private static ExposureSettings ReadExposure(JsonObject o) =>
        new(OptDouble(o, "exposure") ?? 0, OptDouble(o, "offset") ?? 0, OptDouble(o, "gamma") ?? 1);

    private static AdjustmentColor ReadColor(JsonObject o) => new(Dbl(o, "red"), Dbl(o, "green"), Dbl(o, "blue"));

    private static GradientMapSettings ReadGradientMap(JsonObject o) => new()
    {
        Shadows = IsPresent(o, "shadows") ? ReadColor(Obj(o["shadows"])) : new AdjustmentColor(0, 0, 0),
        Highlights = IsPresent(o, "highlights") ? ReadColor(Obj(o["highlights"])) : new AdjustmentColor(1, 1, 1),
        Reversed = OptBool(o, "reversed") ?? false,
    };

    private static GrainSettings ReadGrain(JsonObject o)
    {
        uint seed = 0;
        if (IsPresent(o, "seed"))
        {
            var value = o["seed"]!.GetValue<double>();
            if (value < 0 || value > uint.MaxValue || value != Math.Floor(value)) throw ProjectException.Invalid();
            seed = (uint)value;
        }
        return new GrainSettings(OptDouble(o, "amount") ?? 25, OptDouble(o, "size") ?? 1.5, OptDouble(o, "roughness") ?? 50, seed);
    }

    // MARK: JSON helpers

    private static bool IsPresent(JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) && v is not null;
    private static JsonObject Obj(JsonNode? n) => n as JsonObject ?? throw ProjectException.Invalid();
    private static JsonArray Arr(JsonObject o, string key) => o[key] as JsonArray ?? throw ProjectException.Invalid();
    private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw ProjectException.Invalid();
    private static string? OptStr(JsonObject o, string key) => IsPresent(o, key) ? Str(o, key) : null;
    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw ProjectException.Invalid();
    private static bool? OptBool(JsonObject o, string key) => IsPresent(o, key) ? Bool(o, key) : null;
    private static double Dbl(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : throw ProjectException.Invalid();
    private static double? OptDouble(JsonObject o, string key) => IsPresent(o, key) ? Dbl(o, key) : null;
    private static int Int(JsonObject o, string key)
    {
        var d = Dbl(o, key);
        if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) throw ProjectException.Invalid();
        return (int)d;
    }
    private static int? OptInt(JsonObject o, string key) => IsPresent(o, key) ? Int(o, key) : null;
    private static Guid Uuid(JsonObject o, string key) => Guid.TryParseExact(Str(o, key), "D", out var g) ? g : throw ProjectException.Invalid();
    private static Guid? OptUuid(JsonObject o, string key) => IsPresent(o, key) ? Uuid(o, key) : null;
    private static (double A, double B) Pair(JsonObject o, string key)
    {
        var a = Arr(o, key);
        if (a.Count != 2) throw ProjectException.Invalid();
        return (a[0]!.GetValue<double>(), a[1]!.GetValue<double>());
    }

    // MARK: Writing

    public static byte[] WriteManifest(ProjectManifest m)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            // Keys in sorted order, as the Mac app's .sortedKeys encoder writes them.
            w.WriteStartObject();
            if (m.ActiveLayerId is { } active) w.WriteString("activeLayerID", FormatGuid(active));
            w.WriteString("colorSpace", m.ColorSpace);
            w.WriteString("documentID", FormatGuid(m.DocumentId));
            w.WriteString("format", m.Format);
            w.WriteNumber("height", m.Height);
            w.WritePropertyName("layers");
            w.WriteStartArray();
            foreach (var layer in m.Layers) WriteLayer(w, layer);
            w.WriteEndArray();
            if (m.Resolution is { } resolution) WriteDouble(w, "resolution", resolution);
            w.WriteNumber("version", m.Version);
            w.WriteNumber("width", m.Width);
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteDouble(Utf8JsonWriter w, string key, double value)
    {
        if (!double.IsFinite(value)) throw ProjectException.Invalid();
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15) w.WriteNumber(key, (long)value);
        else w.WriteNumber(key, value);
    }

    private static void WriteDoubleValue(Utf8JsonWriter w, double value)
    {
        if (!double.IsFinite(value)) throw ProjectException.Invalid();
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15) w.WriteNumberValue((long)value);
        else w.WriteNumberValue(value);
    }

    private static void WriteLayer(Utf8JsonWriter w, ProjectLayerRecord l)
    {
        w.WriteStartObject();
        if (l.Adjustment is { } adjustment) { w.WritePropertyName("adjustment"); WriteAdjustment(w, adjustment); }
        if (l.BlendMode is { } blend) w.WriteString("blendMode", blend.ToName());
        w.WriteString("id", FormatGuid(l.Id));
        if (l.ImageFile is { } image) w.WriteString("imageFile", image);
        if (l.IsGroup is { } group) w.WriteBoolean("isGroup", group);
        if (l.IsLocked == true) w.WriteBoolean("isLocked", true);
        w.WriteBoolean("isVisible", l.IsVisible);
        if (l.Shape is { } extra && !extra.IsMacStyle)
        {
            w.WritePropertyName(LayerFormShapeKey);
            w.WriteStartObject();
            WriteDouble(w, "blue", extra.Blue);
            WriteDouble(w, "cornerRadius", extra.CornerRadius);
            WriteDouble(w, "green", extra.Green);
            w.WriteString("kind", extra.Kind.Name());
            WriteDouble(w, "red", extra.Red);
            w.WriteBoolean("rising", extra.Rising);
            WriteDouble(w, "sides", extra.Sides);
            WriteDouble(w, "weight", extra.Weight);
            if (extra.Corners is { } corners)
            {
                w.WritePropertyName("corners");
                w.WriteStartArray();
                for (int i = 0; i < 4; i++) w.WriteNumberValue(corners[i]);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        if (l.MaskEnabled is { } enabled) w.WriteBoolean("maskEnabled", enabled);
        if (l.MaskFile is { } maskFile) w.WriteString("maskFile", maskFile);
        if (l.MaskLinked is { } linked) w.WriteBoolean("maskLinked", linked);
        if (l.MaskPlacement is { } placement) { w.WritePropertyName("maskPlacement"); WriteTransform(w, placement); }
        if (l.MaskSourceId is { } source) w.WriteString("maskSourceID", FormatGuid(source));
        w.WriteString("name", l.Name);
        if (l.Opacity is { } opacity) WriteDouble(w, "opacity", opacity);
        if (l.ParentId is { } parent) w.WriteString("parentID", FormatGuid(parent));
        if (l.Shape is { } shape && shape.IsMacStyle)
        {
            w.WritePropertyName("shape");
            w.WriteStartObject();
            WriteDouble(w, "blue", shape.Blue);
            WriteDouble(w, "cornerRadius", shape.CornerRadius);
            WriteDouble(w, "green", shape.Green);
            w.WriteString("kind", shape.Kind == ShapeKind.Rectangle ? "Rectangle" : "Ellipse");
            WriteDouble(w, "red", shape.Red);
            w.WriteEndObject();
        }
        if (l.Text is { } text)
        {
            w.WritePropertyName("text");
            w.WriteStartObject();
            w.WriteString("alignment", text.Alignment switch { LayerTextAlignment.Center => "Center", LayerTextAlignment.Right => "Right", _ => "Left" });
            if (text.BoxSize is { } box)
            {
                w.WritePropertyName("boxSize");
                w.WriteStartArray(); WriteDoubleValue(w, box.Width); WriteDoubleValue(w, box.Height); w.WriteEndArray();
            }
            WriteDouble(w, "blue", text.Blue);
            w.WriteString("content", text.Content);
            w.WriteString("fontName", text.FontName);
            WriteDouble(w, "fontSize", text.FontSize);
            WriteDouble(w, "green", text.Green);
            WriteDouble(w, "leading", text.Leading);
            WriteDouble(w, "red", text.Red);
            WriteDouble(w, "tracking", text.Tracking);
            w.WriteEndObject();
        }
        w.WritePropertyName("transform");
        WriteTransform(w, l.Transform);
        w.WriteEndObject();
    }

    public static void WriteTransform(Utf8JsonWriter w, LayerTransform t)
    {
        w.WriteStartObject();
        w.WriteBoolean("flipX", t.FlipX);
        w.WriteBoolean("flipY", t.FlipY);
        w.WritePropertyName("origin");
        w.WriteStartArray(); WriteDoubleValue(w, t.Origin.X); WriteDoubleValue(w, t.Origin.Y); w.WriteEndArray();
        WriteDouble(w, "rotation", t.Rotation);
        w.WriteString("sampling", t.Sampling.ToName());
        w.WritePropertyName("size");
        w.WriteStartArray(); WriteDoubleValue(w, t.Size.Width); WriteDoubleValue(w, t.Size.Height); w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteAdjustment(Utf8JsonWriter w, LayerAdjustment a)
    {
        // Every non-optional stored property, as Swift's synthesized encoder writes them all.
        w.WriteStartObject();
        w.WriteBoolean("colorize", a.Colorize);
        w.WritePropertyName("curves"); WriteCurves(w, a.Curves);
        if (a.ExposureSettings is { } exposure)
        {
            w.WritePropertyName("exposureSettings");
            w.WriteStartObject();
            WriteDouble(w, "exposure", exposure.Exposure);
            WriteDouble(w, "gamma", exposure.Gamma);
            WriteDouble(w, "offset", exposure.Offset);
            w.WriteEndObject();
        }
        if (a.GradientMapSettings is { } map)
        {
            w.WritePropertyName("gradientMapSettings");
            w.WriteStartObject();
            w.WritePropertyName("highlights"); WriteColor(w, map.Highlights);
            w.WriteBoolean("reversed", map.Reversed);
            w.WritePropertyName("shadows"); WriteColor(w, map.Shadows);
            w.WriteEndObject();
        }
        if (a.GrainSettings is { } grain)
        {
            w.WritePropertyName("grainSettings");
            w.WriteStartObject();
            WriteDouble(w, "amount", grain.Amount);
            WriteDouble(w, "roughness", grain.Roughness);
            w.WriteNumber("seed", grain.Seed);
            WriteDouble(w, "size", grain.Size);
            w.WriteEndObject();
        }
        if (a.HsvSettings is { } hsv) { w.WritePropertyName("hsvSettings"); WriteHsv(w, hsv); }
        WriteDouble(w, "hue", a.Hue);
        w.WriteString("kind", a.Kind.ToName());
        w.WritePropertyName("levels"); WriteLevels(w, a.Levels);
        WriteDouble(w, "lightness", a.Lightness);
        WriteDouble(w, "saturation", a.Saturation);
        w.WriteEndObject();
    }

    private static void WriteColor(Utf8JsonWriter w, AdjustmentColor c)
    {
        w.WriteStartObject();
        WriteDouble(w, "blue", c.Blue);
        WriteDouble(w, "green", c.Green);
        WriteDouble(w, "red", c.Red);
        w.WriteEndObject();
    }

    private static void WriteHsv(Utf8JsonWriter w, HueSaturationSettings s)
    {
        w.WriteStartObject();
        w.WritePropertyName("adjustments");
        w.WriteStartArray();
        foreach (var range in ColorRanges.All)
        {
            if (!s.Adjustments.TryGetValue(range, out var a)) continue;
            w.WriteStringValue(range.ToName());
            w.WriteStartObject();
            WriteDouble(w, "hue", a.Hue);
            WriteDouble(w, "lightness", a.Lightness);
            WriteDouble(w, "saturation", a.Saturation);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("bands");
        w.WriteStartArray();
        foreach (var range in ColorRanges.All)
        {
            if (!s.Bands.TryGetValue(range, out var b)) continue;
            w.WriteStringValue(range.ToName());
            w.WriteStartObject();
            WriteDouble(w, "falloffEnd", b.FalloffEnd);
            WriteDouble(w, "falloffStart", b.FalloffStart);
            WriteDouble(w, "rangeEnd", b.RangeEnd);
            WriteDouble(w, "rangeStart", b.RangeStart);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("colorize", s.Colorize);
        w.WriteBoolean("invertRange", s.InvertRange);
        w.WriteString("range", s.Range.ToName());
        w.WriteEndObject();
    }

    private static void WriteLevels(Utf8JsonWriter w, LevelsSettings s)
    {
        w.WriteStartObject();
        w.WriteString("channel", s.Channel.ToName());
        w.WritePropertyName("ranges");
        w.WriteStartArray();
        foreach (var r in s.Ranges)
        {
            w.WriteStartObject();
            WriteDouble(w, "black", r.Black);
            WriteDouble(w, "gamma", r.Gamma);
            WriteDouble(w, "outputBlack", r.OutputBlack);
            WriteDouble(w, "outputWhite", r.OutputWhite);
            WriteDouble(w, "white", r.White);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCurves(Utf8JsonWriter w, CurvesSettings s)
    {
        w.WriteStartObject();
        w.WriteString("channel", s.Channel.ToName());
        w.WritePropertyName("channels");
        w.WriteStartArray();
        foreach (var points in s.Channels)
        {
            w.WriteStartArray();
            foreach (var p in points)
            {
                w.WriteStartObject();
                WriteDouble(w, "x", p.X);
                WriteDouble(w, "y", p.Y);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static string Describe(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
