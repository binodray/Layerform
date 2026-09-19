namespace Compositor.Model;

/// <summary>The six color ranges plus Master, as in Photoshop's Hue/Saturation.</summary>
public enum ColorRange { Master, Reds, Yellows, Greens, Cyans, Blues, Magentas }

public static class ColorRanges
{
    public static readonly ColorRange[] All = Enum.GetValues<ColorRange>();
    public static readonly ColorRange[] Colors = All.Where(r => r != ColorRange.Master).ToArray();
    public static string ToName(this ColorRange r) => r.ToString();
    public static bool TryParse(string? name, out ColorRange range)
    {
        foreach (var candidate in All)
            if (candidate.ToString() == name) { range = candidate; return true; }
        range = ColorRange.Master;
        return false;
    }
    public static HueBand DefaultBand(this ColorRange r) => r switch
    {
        ColorRange.Master => new HueBand(0, 0, 360, 360),
        ColorRange.Reds => new HueBand(315, 345, 15, 45),
        ColorRange.Yellows => new HueBand(15, 45, 75, 105),
        ColorRange.Greens => new HueBand(75, 105, 135, 165),
        ColorRange.Cyans => new HueBand(135, 165, 195, 225),
        ColorRange.Blues => new HueBand(195, 225, 255, 285),
        _ => new HueBand(255, 285, 315, 345),
    };
}

/// <summary>A hue band in degrees, wrapping at 360: full strength from RangeStart to RangeEnd, fading at the falloffs.</summary>
public sealed record HueBand(double FalloffStart, double RangeStart, double RangeEnd, double FalloffEnd)
{
    /// <summary>Degrees from <paramref name="from"/> forward to <paramref name="to"/>, always 0…360.</summary>
    public static double Forward(double from, double to)
    {
        double delta = (to - from) % 360;
        return delta < 0 ? delta + 360 : delta;
    }

    public double Weight(double hue)
    {
        double span = Forward(FalloffStart, FalloffEnd);
        if (!(span > 0)) return 1;
        double position = Forward(FalloffStart, hue);
        if (position > span) return 0;
        double rampIn = Forward(FalloffStart, RangeStart);
        double plateauEnd = Forward(FalloffStart, RangeEnd);
        if (position < rampIn) return rampIn > 0 ? position / rampIn : 1;
        if (position <= plateauEnd) return 1;
        double rampOut = span - plateauEnd;
        return rampOut > 0 ? (span - position) / rampOut : 1;
    }

    public double[] Handles => new[] { FalloffStart, RangeStart, RangeEnd, FalloffEnd };

    private static double Wrap(double value)
    {
        double r = value % 360;
        return r < 0 ? r + 360 : r;
    }

    public HueBand CenteredOn(double hue)
    {
        double core = Forward(RangeStart, RangeEnd), leading = Forward(FalloffStart, RangeStart), trailing = Forward(RangeEnd, FalloffEnd);
        double start = Wrap(hue - core / 2);
        return new HueBand(Wrap(start - leading), start, Wrap(start + core), Wrap(start + core + trailing));
    }

    private HueBand Normalize()
    {
        var b = new HueBand(Wrap(FalloffStart), Wrap(RangeStart), Wrap(RangeEnd), Wrap(FalloffEnd));
        if (Forward(b.FalloffStart, b.FalloffEnd) > 350) b = b with { FalloffEnd = Wrap(b.FalloffStart + 350) };
        return b;
    }

    public HueBand Including(double hue)
    {
        if (Weight(hue) >= 1) return this;
        double shoulderIn = Forward(FalloffStart, RangeStart), shoulderOut = Forward(RangeEnd, FalloffEnd);
        double beforeStart = Forward(hue, RangeStart), afterEnd = Forward(RangeEnd, hue);
        var b = beforeStart <= afterEnd
            ? this with { RangeStart = hue, FalloffStart = hue - shoulderIn }
            : this with { RangeEnd = hue, FalloffEnd = hue + shoulderOut };
        return b.Normalize();
    }

    public HueBand Excluding(double hue)
    {
        if (Weight(hue) <= 0) return this;
        double shoulderIn = Forward(FalloffStart, RangeStart), shoulderOut = Forward(RangeEnd, FalloffEnd);
        double fromStart = Forward(FalloffStart, hue), toEnd = Forward(hue, FalloffEnd);
        var b = fromStart <= toEnd
            ? this with { FalloffStart = hue + 1, RangeStart = hue + 1 + shoulderIn }
            : this with { FalloffEnd = hue - 1, RangeEnd = hue - 1 - shoulderOut };
        return b.Normalize();
    }

    public HueBand WithHandle(int index, double degrees)
    {
        double value = ((degrees % 360) + 360) % 360;
        var updated = index switch
        {
            0 => this with { FalloffStart = value },
            1 => this with { RangeStart = value },
            2 => this with { RangeEnd = value },
            _ => this with { FalloffEnd = value },
        };
        double span = Forward(updated.FalloffStart, updated.FalloffEnd);
        double toStart = Forward(updated.FalloffStart, updated.RangeStart), toEnd = Forward(updated.FalloffStart, updated.RangeEnd);
        return span > 1 && span <= 350 && toStart <= toEnd && toEnd <= span ? updated : this;
    }
}

public sealed record RangeAdjustment(double Hue = 0, double Saturation = 0, double Lightness = 0)
{
    public static readonly RangeAdjustment Zero = new();
}

/// <summary>Hue −180…180 (0…360 colorizing), Saturation −100…100 (0…100 colorizing), Lightness −100…100.</summary>
public sealed record HueSaturationSettings
{
    public ColorRange Range { get; init; } = ColorRange.Master;
    public bool Colorize { get; init; }
    public bool InvertRange { get; init; }
    public IReadOnlyDictionary<ColorRange, RangeAdjustment> Adjustments { get; init; } = new Dictionary<ColorRange, RangeAdjustment>();
    public IReadOnlyDictionary<ColorRange, HueBand> Bands { get; init; } = ColorRanges.All.ToDictionary(r => r, r => r.DefaultBand());

    public HueSaturationSettings() { }
    public HueSaturationSettings(double hue, double saturation = 0, double lightness = 0, bool colorize = false, ColorRange range = ColorRange.Master)
    {
        Range = range;
        Colorize = colorize;
        Adjustments = new Dictionary<ColorRange, RangeAdjustment> { [range] = new RangeAdjustment(hue, saturation, lightness) };
    }

    public RangeAdjustment CurrentAdjustment => Adjustments.TryGetValue(Range, out var a) ? a : RangeAdjustment.Zero;
    public double Hue => CurrentAdjustment.Hue;
    public double Saturation => CurrentAdjustment.Saturation;
    public double Lightness => CurrentAdjustment.Lightness;
    public HueBand Band => Bands.TryGetValue(Range, out var b) ? b : Range.DefaultBand();

    public HueSaturationSettings WithAdjustment(ColorRange range, RangeAdjustment value)
    {
        var copy = new Dictionary<ColorRange, RangeAdjustment>(Adjustments) { [range] = value };
        return this with { Adjustments = copy };
    }
    public HueSaturationSettings WithCurrent(Func<RangeAdjustment, RangeAdjustment> change) => WithAdjustment(Range, change(CurrentAdjustment));
    public HueSaturationSettings WithBand(HueBand band)
    {
        var copy = new Dictionary<ColorRange, HueBand>(Bands) { [Range] = band };
        return this with { Bands = copy };
    }

    public static readonly HueSaturationSettings ColorizeStart = new(0, 25, 0, true);
    public bool IsIdentity => !Colorize && Adjustments.Values.All(a => a == RangeAdjustment.Zero);

    public double Weight(ColorRange colorRange, double hue)
    {
        if (colorRange == ColorRange.Master) return 1;
        double weight = (Bands.TryGetValue(colorRange, out var b) ? b : colorRange.DefaultBand()).Weight(hue);
        return InvertRange && colorRange == Range ? 1 - weight : weight;
    }

    public bool Equals(HueSaturationSettings? other) => other is not null && Range == other.Range && Colorize == other.Colorize
        && InvertRange == other.InvertRange && DictEquals(Adjustments, other.Adjustments) && DictEquals(Bands, other.Bands);
    private static bool DictEquals<TValue>(IReadOnlyDictionary<ColorRange, TValue> a, IReadOnlyDictionary<ColorRange, TValue> b)
        => a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var v) && Equals(v, pair.Value));
    public override int GetHashCode() => HashCode.Combine(Range, Colorize, InvertRange, Adjustments.Count);
}
