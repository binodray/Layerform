namespace Compositor.Model;

public enum LayerBlendMode
{
    Normal, Multiply, Screen, Overlay, SoftLight, Darken, Lighten, Difference, ColorDodge, ColorBurn, Hue, Saturation, Color, Luminosity,
}

public static class BlendModes
{
    public static readonly LayerBlendMode[] All = Enum.GetValues<LayerBlendMode>();
    public static string ToName(this LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.ColorDodge => "Color Dodge",
        LayerBlendMode.ColorBurn => "Color Burn",
        LayerBlendMode.SoftLight => "Soft Light",
        _ => mode.ToString(),
    };
    public static bool TryParse(string? name, out LayerBlendMode mode)
    {
        foreach (var candidate in All)
            if (candidate.ToName() == name) { mode = candidate; return true; }
        mode = LayerBlendMode.Normal;
        return false;
    }
    public static SkiaSharp.SKBlendMode ToSkia(this LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Normal => SkiaSharp.SKBlendMode.SrcOver,
        LayerBlendMode.Multiply => SkiaSharp.SKBlendMode.Multiply,
        LayerBlendMode.Screen => SkiaSharp.SKBlendMode.Screen,
        LayerBlendMode.Overlay => SkiaSharp.SKBlendMode.Overlay,
        LayerBlendMode.SoftLight => SkiaSharp.SKBlendMode.SoftLight,
        LayerBlendMode.Darken => SkiaSharp.SKBlendMode.Darken,
        LayerBlendMode.Lighten => SkiaSharp.SKBlendMode.Lighten,
        LayerBlendMode.Difference => SkiaSharp.SKBlendMode.Difference,
        LayerBlendMode.ColorDodge => SkiaSharp.SKBlendMode.ColorDodge,
        LayerBlendMode.ColorBurn => SkiaSharp.SKBlendMode.ColorBurn,
        LayerBlendMode.Hue => SkiaSharp.SKBlendMode.Hue,
        LayerBlendMode.Saturation => SkiaSharp.SKBlendMode.Saturation,
        LayerBlendMode.Color => SkiaSharp.SKBlendMode.Color,
        _ => SkiaSharp.SKBlendMode.Luminosity,
    };
}

public enum AdjustmentKind { HueSaturation, Levels, Curves, Exposure, GradientMap, Grain }

public static class AdjustmentKinds
{
    public static readonly AdjustmentKind[] All = Enum.GetValues<AdjustmentKind>();
    public static string ToName(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.HueSaturation => "Hue/Saturation",
        AdjustmentKind.GradientMap => "Gradient Map",
        _ => kind.ToString(),
    };
    public static bool TryParse(string? name, out AdjustmentKind kind)
    {
        foreach (var candidate in All)
            if (candidate.ToName() == name) { kind = candidate; return true; }
        kind = AdjustmentKind.HueSaturation;
        return false;
    }
}

public enum LevelsChannel { Rgb, Red, Green, Blue }

public static class LevelsChannels
{
    public static readonly LevelsChannel[] All = Enum.GetValues<LevelsChannel>();
    public static string ToName(this LevelsChannel c) => c switch
    {
        LevelsChannel.Rgb => "RGB", LevelsChannel.Red => "Red", LevelsChannel.Green => "Green", _ => "Blue",
    };
    public static bool TryParse(string? name, out LevelsChannel channel)
    {
        foreach (var candidate in All)
            if (candidate.ToName() == name) { channel = candidate; return true; }
        channel = LevelsChannel.Rgb;
        return false;
    }
    public static int Index(this LevelsChannel c) => (int)c;
}

public sealed record LevelRange(double Black = 0, double Gamma = 1, double White = 255, double OutputBlack = 0, double OutputWhite = 255)
{
    private static double Clamp(double n, double lo, double hi, double fallback) => double.IsFinite(n) ? Math.Min(hi, Math.Max(lo, n)) : fallback;
    public LevelRange Normalized
    {
        get
        {
            var black = Clamp(Black, 0, 254, 0);
            return new LevelRange(black, Clamp(Gamma, 0.1, 9.99, 1), Clamp(White, black + 1, 255, 255),
                Clamp(OutputBlack, 0, 255, 0), Clamp(OutputWhite, 0, 255, 255));
        }
    }
    public double Apply(double value)
    {
        var s = Normalized;
        double input = Math.Min(1, Math.Max(0, (value * 255 - s.Black) / (s.White - s.Black)));
        return (s.OutputBlack + Math.Pow(input, 1 / s.Gamma) * (s.OutputWhite - s.OutputBlack)) / 255;
    }
    public static readonly LevelRange Identity = new();
}

public sealed record LevelsSettings
{
    public LevelsChannel Channel { get; init; } = LevelsChannel.Rgb;
    public IReadOnlyList<LevelRange> Ranges { get; init; } = new[] { new LevelRange(), new LevelRange(), new LevelRange(), new LevelRange() };
    public LevelRange Current => Ranges[Channel.Index()];
    public LevelsSettings WithCurrent(LevelRange value) => WithRange(Channel.Index(), value.Normalized);
    public LevelsSettings WithRange(int index, LevelRange value)
    {
        var list = Ranges.ToArray();
        list[index] = value;
        return this with { Ranges = list };
    }
    public bool IsIdentity => Ranges.All(r => r.Normalized == LevelRange.Identity);
    public double Apply(double value, LevelsChannel channel) => Ranges[0].Apply(Ranges[channel.Index()].Apply(value));
    public bool Equals(LevelsSettings? other) => other is not null && Channel == other.Channel && Ranges.SequenceEqual(other.Ranges);
    public override int GetHashCode() => HashCode.Combine(Channel, Ranges.Count);
}

public sealed record CurvePoint(double X, double Y);

public sealed record CurvesSettings
{
    public LevelsChannel Channel { get; init; } = LevelsChannel.Rgb;
    public IReadOnlyList<IReadOnlyList<CurvePoint>> Channels { get; init; } = Enumerable.Range(0, 4)
        .Select(_ => (IReadOnlyList<CurvePoint>)new[] { new CurvePoint(0, 0), new CurvePoint(255, 255) }).ToArray();

    public bool IsValid => Channels.Count == 4 && Channels.All(points =>
        points.Count >= 2 && points.Count <= 32 && points[0].X == 0 && points[^1].X == 255
        && points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= 255 && p.Y >= 0 && p.Y <= 255)
        && points.Zip(points.Skip(1)).All(pair => pair.First.X < pair.Second.X));

    public CurvesSettings WithChannelPoints(int channel, IReadOnlyList<CurvePoint> points)
    {
        var list = Channels.ToArray();
        list[channel] = points;
        return this with { Channels = list };
    }

    /// <summary>Shape-preserving cubic Hermite interpolation (no overshoot between handles).</summary>
    public double Value(double x, int channel)
    {
        var p = Channels[channel];
        int last = -1;
        for (int j = 0; j < p.Count; j++) if (p[j].X <= x) last = j;
        int i = Math.Min(p.Count - 2, Math.Max(0, last < 0 ? 0 : last));
        var d = new double[p.Count - 1];
        for (int j = 0; j < d.Length; j++) d[j] = (p[j + 1].Y - p[j].Y) / (p[j + 1].X - p[j].X);
        double Slope(int j)
        {
            if (j == 0) return d[0];
            if (j == p.Count - 1) return d[^1];
            if (d[j - 1] * d[j] <= 0) return 0;
            return 2 / (1 / d[j - 1] + 1 / d[j]);
        }
        double h = p[i + 1].X - p[i].X, t = Math.Min(1, Math.Max(0, (x - p[i].X) / h));
        double y = (2 * t * t * t - 3 * t * t + 1) * p[i].Y + (t * t * t - 2 * t * t + t) * h * Slope(i)
                 + (-2 * t * t * t + 3 * t * t) * p[i + 1].Y + (t * t * t - t * t) * h * Slope(i + 1);
        return Math.Min(255, Math.Max(0, y));
    }

    public bool Equals(CurvesSettings? other) => other is not null && Channel == other.Channel
        && Channels.Count == other.Channels.Count && Channels.Zip(other.Channels).All(pair => pair.First.SequenceEqual(pair.Second));
    public override int GetHashCode() => HashCode.Combine(Channel, Channels.Count);
}

public sealed record AdjustmentColor(double Red, double Green, double Blue)
{
    public AdjustmentColor(PaletteColor color) : this(color.Red, color.Green, color.Blue) { }
    public bool IsValid => new[] { Red, Green, Blue }.All(v => double.IsFinite(v) && v >= 0 && v <= 1);
    private static double Clamp(double v) => double.IsFinite(v) ? Math.Min(1, Math.Max(0, v)) : 0;
    public AdjustmentColor Clamped => new(Clamp(Red), Clamp(Green), Clamp(Blue));
}

public sealed record ExposureSettings(double Exposure = 0, double Offset = 0, double Gamma = 1)
{
    public const double ExposureMin = -20, ExposureMax = 20, OffsetMin = -0.5, OffsetMax = 0.5, GammaMin = 0.01, GammaMax = 9.99;
    public bool IsValid => Exposure >= ExposureMin && Exposure <= ExposureMax && Offset >= OffsetMin && Offset <= OffsetMax
        && Gamma >= GammaMin && Gamma <= GammaMax;
    private static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Min(hi, Math.Max(lo, v)) : fallback;
    public ExposureSettings Normalized => new(Clamp(Exposure, ExposureMin, ExposureMax, 0), Clamp(Offset, OffsetMin, OffsetMax, 0), Clamp(Gamma, GammaMin, GammaMax, 1));

    /// <summary>Each channel's output (0–1) for each input byte, decoded to linear light and encoded back.</summary>
    public float[] Table()
    {
        double scale = Math.Pow(2, Exposure);
        var result = new float[256];
        for (int index = 0; index < 256; index++)
        {
            double encoded = index / 255.0;
            double linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * scale + Offset), 1 / Gamma);
            double output = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            result[index] = (float)Math.Min(1, Math.Max(0, output));
        }
        return result;
    }
}

public sealed record GradientMapSettings
{
    public AdjustmentColor Shadows { get; init; } = new(0, 0, 0);
    public AdjustmentColor Highlights { get; init; } = new(1, 1, 1);
    public bool Reversed { get; init; }
    public bool IsValid => Shadows.IsValid && Highlights.IsValid;
    public GradientMapSettings Normalized => this with { Shadows = Shadows.Clamped, Highlights = Highlights.Clamped };
    public (AdjustmentColor Dark, AdjustmentColor Light) Ends => Reversed ? (Highlights, Shadows) : (Shadows, Highlights);
}

public sealed record GrainSettings(double Amount = 25, double Size = 1.5, double Roughness = 50, uint Seed = 0)
{
    public const double AmountMin = 0, AmountMax = 100, SizeMin = 0.5, SizeMax = 20, RoughnessMin = 0, RoughnessMax = 100;
    public bool IsValid => Amount >= AmountMin && Amount <= AmountMax && Size >= SizeMin && Size <= SizeMax
        && Roughness >= RoughnessMin && Roughness <= RoughnessMax;
    private static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Min(hi, Math.Max(lo, v)) : fallback;
    public GrainSettings Normalized => this with { Amount = Clamp(Amount, AmountMin, AmountMax, 25), Size = Clamp(Size, SizeMin, SizeMax, 1.5), Roughness = Clamp(Roughness, RoughnessMin, RoughnessMax, 50) };
}

/// <summary>An adjustment layer's settings, stored in project version 7 manifests.</summary>
public sealed record LayerAdjustment
{
    public AdjustmentKind Kind { get; init; }
    public double Hue { get; init; }
    public double Saturation { get; init; }
    public double Lightness { get; init; }
    public bool Colorize { get; init; }
    /// <summary>Optional so projects saved before range-aware HSV adjustments still decode.</summary>
    public HueSaturationSettings? HsvSettings { get; init; }
    public LevelsSettings Levels { get; init; } = new();
    public CurvesSettings Curves { get; init; } = new();
    public ExposureSettings? ExposureSettings { get; init; }
    public GradientMapSettings? GradientMapSettings { get; init; }
    public GrainSettings? GrainSettings { get; init; }

    public LayerAdjustment(AdjustmentKind kind) { Kind = kind; }

    public HueSaturationSettings ResolvedHsv => HsvSettings ?? new HueSaturationSettings(Hue, Saturation, Lightness, Colorize);
    public ExposureSettings Exposure => ExposureSettings ?? new ExposureSettings();
    public GradientMapSettings GradientMap => GradientMapSettings ?? new GradientMapSettings();
    public GrainSettings Grain => GrainSettings ?? new GrainSettings();

    public bool IsValid
    {
        get
        {
            bool Finite(double v, double limit) => double.IsFinite(v) && Math.Abs(v) <= limit;
            var hsv = ResolvedHsv;
            return Finite(Hue, 360) && Finite(Saturation, 100) && Finite(Lightness, 100)
                && hsv.Adjustments.Values.All(a => Finite(a.Hue, 360) && Finite(a.Saturation, 100) && Finite(a.Lightness, 100))
                && hsv.Bands.Values.All(b => b.Handles.All(double.IsFinite))
                && Levels.Ranges.Count == 4 && Levels.Ranges.All(r => r == r.Normalized) && Curves.IsValid
                && Exposure.IsValid && GradientMap.IsValid && Grain.IsValid;
        }
    }
}
