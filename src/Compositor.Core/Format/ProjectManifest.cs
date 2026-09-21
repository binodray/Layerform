using Compositor.Model;

namespace Compositor.Format;

public sealed class ProjectException : Exception
{
    public ProjectErrorKind Kind { get; }
    public int? Version { get; }
    public ProjectException(ProjectErrorKind kind, int? version = null, Exception? inner = null) : base(Describe(kind, version), inner)
    {
        Kind = kind;
        Version = version;
    }
    private static string Describe(ProjectErrorKind kind, int? version) => kind switch
    {
        ProjectErrorKind.Invalid => "This is not a valid Compositor project, or its metadata is damaged.",
        ProjectErrorKind.Version => $"This project uses format version {version}. This app supports versions 1–8.",
        ProjectErrorKind.MissingImage => "An image inside the project is missing or damaged. The current document has not been replaced.",
        ProjectErrorKind.TooLarge => "This project exceeds the supported canvas, layer, file-size, or 100-megapixel image limit.",
        _ => "An image could not be saved. The previous project has not been replaced.",
    };
    public static ProjectException Invalid(Exception? inner = null) => new(ProjectErrorKind.Invalid, null, inner);
    public static ProjectException TooLarge() => new(ProjectErrorKind.TooLarge);
    public static ProjectException MissingImage(Exception? inner = null) => new(ProjectErrorKind.MissingImage, null, inner);
}

public enum ProjectErrorKind { Invalid, Version, MissingImage, TooLarge, Encode }

public sealed record ProjectLayerRecord
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public bool IsVisible { get; init; } = true;
    public bool? IsLocked { get; init; }
    public LayerTransform Transform { get; init; }
    public string? ImageFile { get; init; }
    public Guid? ParentId { get; init; }
    public bool? IsGroup { get; init; }
    public double? Opacity { get; init; }
    public LayerBlendMode? BlendMode { get; init; }
    public string? MaskFile { get; init; }
    public bool? MaskEnabled { get; init; }
    public Guid? MaskSourceId { get; init; }
    public LayerAdjustment? Adjustment { get; init; }
    /// <summary>A mask moved apart from its layer: where it sits on the document.</summary>
    public LayerTransform? MaskPlacement { get; init; }
    /// <summary>Null (older projects) is linked.</summary>
    public bool? MaskLinked { get; init; }
    public LayerShapeStyle? Shape { get; init; }
    public LayerTextStyle? Text { get; init; }

    public static string ImageFileName(Guid id) => $"{ProjectJson.FormatGuid(id)}.png";
    public static string MaskFileName(Guid id) => $"{ProjectJson.FormatGuid(id)}.mask.png";
}

public sealed record ProjectManifest
{
    public const string FormatIdentifier = "com.compositor.project";
    public const int CurrentVersion = 8;
    public string Format { get; init; } = FormatIdentifier;
    public int Version { get; init; } = CurrentVersion;
    public string ColorSpace { get; init; } = "sRGB";
    /// <summary>Older version-1 projects default to 72 pixels/inch.</summary>
    public double? Resolution { get; init; }
    public Guid DocumentId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public Guid? ActiveLayerId { get; init; }
    public IReadOnlyList<ProjectLayerRecord> Layers { get; init; } = Array.Empty<ProjectLayerRecord>();
}

/// <summary>Layer hierarchy traversal and validation shared by the renderer, the Layers panel and the store.</summary>
public static class LayerHierarchy
{
    public readonly record struct Entry(ProjectLayerRecord Layer, int Depth, bool Visible);

    public static List<Entry> Entries(IReadOnlyList<ProjectLayerRecord> layers, bool topFirst = false, ISet<Guid>? collapsed = null)
    {
        var children = new Dictionary<Guid, List<ProjectLayerRecord>>();
        var roots = new List<ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (layer.ParentId is { } parent)
            {
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<ProjectLayerRecord>();
                list.Add(layer);
            }
            else roots.Add(layer);
        }
        var result = new List<Entry>();
        void Visit(List<ProjectLayerRecord> siblings, int depth, bool visible)
        {
            if (depth > 64) return;
            IEnumerable<ProjectLayerRecord> ordered = topFirst ? Enumerable.Reverse(siblings) : siblings;
            foreach (var layer in ordered)
            {
                bool effective = visible && layer.IsVisible;
                result.Add(new Entry(layer, depth, effective));
                if (layer.IsGroup == true && (collapsed == null || !collapsed.Contains(layer.Id))
                    && children.TryGetValue(layer.Id, out var inside))
                    Visit(inside, depth + 1, effective);
            }
        }
        Visit(roots, 0, true);
        return result;
    }

    public static List<ProjectLayerRecord> VisibleLayers(IReadOnlyList<ProjectLayerRecord> layers) =>
        Entries(layers).Where(e => e.Visible && e.Layer.IsGroup != true).Select(e => e.Layer).ToList();

    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var byId = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer) || (layer.IsGroup == true && layer.ImageFile != null)) throw ProjectException.Invalid();
        }
        foreach (var layer in layers)
        {
            var seen = new HashSet<Guid> { layer.Id };
            var parent = layer.ParentId;
            while (parent is { } id)
            {
                if (seen.Count > 64 || !seen.Add(id) || !byId.TryGetValue(id, out var node) || node.IsGroup != true) throw ProjectException.Invalid();
                parent = node.ParentId;
            }
            if (layer.IsGroup == true && seen.Count > 64) throw ProjectException.Invalid();
        }
    }
}

public static class LiveMaskGraph
{
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var records = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers) if (!records.TryAdd(layer.Id, layer)) throw ProjectException.Invalid();
        foreach (var layer in layers)
        {
            var path = new HashSet<Guid>();
            Guid? current = layer.Id;
            while (current is { } id)
            {
                if (path.Count >= 256 || !path.Add(id) || !records.TryGetValue(id, out var record)) throw ProjectException.Invalid();
                if (record.MaskSourceId is { } source)
                {
                    if (record.IsGroup == true || !records.TryGetValue(source, out var s) || s.IsGroup == true || s.Adjustment != null)
                        throw ProjectException.Invalid();
                }
                current = record.MaskSourceId;
            }
        }
    }
}
