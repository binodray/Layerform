using Compositor.Imaging;
using Compositor.Model;

namespace Compositor.Format;

public sealed record ProjectSnapshot(ProjectManifest Manifest, IReadOnlyDictionary<Guid, ImageAsset> Images,
    IReadOnlyDictionary<Guid, MaskAsset> Masks)
{
    public LayerMask? Mask(ProjectLayerRecord layer) =>
        layer.MaskFile != null && Masks.TryGetValue(layer.Id, out var asset)
            ? new LayerMask(asset, layer.MaskEnabled ?? true, layer.MaskPlacement, layer.MaskLinked ?? true)
            : null;
}

/// <summary>
/// Reads and writes `.comp` project packages: a directory holding manifest.json and images/&lt;UUID&gt;.png plus
/// images/&lt;UUID&gt;.mask.png. Validation, limits and failure behaviour follow the Mac app's ProjectStore.swift.
/// </summary>
public static class ProjectStore
{
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const long MaxAssetBytes = 512L * 1024 * 1024;
    public const int MaxSide = 30_000;
    public const int MaxPixels = 100_000_000;
    public const int MaxLayers = 10_000;

    public static void Validate(ProjectManifest manifest)
    {
        if (manifest.Format != ProjectManifest.FormatIdentifier) throw ProjectException.Invalid();
        if (manifest.Version < 1 || manifest.Version > 8) throw new ProjectException(ProjectErrorKind.Version, manifest.Version);
        if (manifest.ColorSpace != "sRGB") throw ProjectException.Invalid();
        if (manifest.Resolution is { } resolution && !(double.IsFinite(resolution) && resolution >= 1 && resolution <= 9600))
            throw ProjectException.Invalid();
        if (manifest.Width < 1 || manifest.Width > MaxSide || manifest.Height < 1 || manifest.Height > MaxSide || manifest.Layers.Count > MaxLayers)
            throw ProjectException.TooLarge();
        foreach (var layer in manifest.Layers)
        {
            if (layer.Text is { } text && !(manifest.Version >= 8 && text.IsValid && layer.IsGroup != true && layer.ImageFile != null && layer.Adjustment == null))
                throw ProjectException.Invalid();
            if (layer.Adjustment is { } adjustment &&
                !(manifest.Version >= 7 && layer.IsGroup != true && layer.ImageFile == null && adjustment.IsValid))
                throw ProjectException.Invalid();
            // Layer masks arrived in version 4, folder masks in version 6.
            bool maskOk = layer.MaskFile == null || (manifest.Version >= (layer.IsGroup == true ? 6 : 4)
                && layer.MaskFile == ProjectLayerRecord.MaskFileName(layer.Id));
            if (!maskOk || (layer.MaskEnabled != null && layer.MaskFile == null)
                || (layer.MaskPlacement is { } placement && !(placement.IsValid && layer.MaskFile != null)))
                throw ProjectException.Invalid();
            double opacity = layer.Opacity ?? 1;
            var blend = layer.BlendMode ?? LayerBlendMode.Normal;
            if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1
                || !(manifest.Version >= 3 || (opacity == 1 && blend == LayerBlendMode.Normal))
                || !(layer.IsGroup != true || (manifest.Version >= 8 && blend == LayerBlendMode.Normal) || (opacity == 1 && blend == LayerBlendMode.Normal)))
                throw ProjectException.Invalid();
        }
        LayerHierarchy.Validate(manifest.Layers);
        LiveMaskGraph.Validate(manifest.Layers);
        if (manifest.Version < 5 && manifest.Layers.Any(l => l.MaskSourceId != null)) throw ProjectException.Invalid();
        if (manifest.Version == 1 && manifest.Layers.Any(l => l.ParentId != null || l.IsGroup == true)) throw ProjectException.Invalid();
        var ids = new HashSet<Guid>();
        foreach (var layer in manifest.Layers)
        {
            if (!ids.Add(layer.Id) || !layer.Transform.IsValid || string.IsNullOrWhiteSpace(layer.Name)
                || System.Text.Encoding.UTF8.GetByteCount(layer.Name) > 16_384
                || (layer.ImageFile != null && layer.ImageFile != ProjectLayerRecord.ImageFileName(layer.Id)))
                throw ProjectException.Invalid();
        }
        if (manifest.ActiveLayerId is { } active && !ids.Contains(active)) throw ProjectException.Invalid();
    }

    private static void CheckSize(int width, int height, ref long used)
    {
        if (width < 1 || width > MaxSide || height < 1 || height > MaxSide || (long)width * height > MaxPixels - used)
            throw ProjectException.TooLarge();
        used += (long)width * height;
    }

    /// <summary>Accepts the package directory, its manifest.json, or any file inside it.</summary>
    public static string ResolvePackage(string path)
    {
        if (Directory.Exists(path)) return Path.GetFullPath(path);
        if (File.Exists(path) && string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.GetFullPath(path))!;
        throw ProjectException.Invalid();
    }

    public static ProjectSnapshot Load(string path)
    {
        var package = ResolvePackage(path);
        var manifestPath = Path.Combine(package, "manifest.json");
        CheckFile(manifestPath, package, MaxManifestBytes);
        var metadata = File.ReadAllBytes(manifestPath);
        var (format, version) = ProjectJson.ReadHeader(metadata);
        if (format != ProjectManifest.FormatIdentifier) throw ProjectException.Invalid();
        if (version < 1 || version > ProjectManifest.CurrentVersion) throw new ProjectException(ProjectErrorKind.Version, version);
        var manifest = ProjectJson.ReadManifest(metadata);
        Validate(manifest);
        var images = new Dictionary<Guid, ImageAsset>();
        var masks = new Dictionary<Guid, MaskAsset>();
        long pixels = 0, maskPixels = 0;
        foreach (var layer in manifest.Layers)
        {
            foreach (bool isMask in new[] { false, true })
            {
                var filename = isMask ? layer.MaskFile : layer.ImageFile;
                if (filename == null) continue;
                var file = Path.Combine(package, "images", filename);
                CheckFile(file, package, MaxAssetBytes);
                byte[] data;
                try { data = File.ReadAllBytes(file); }
                catch (Exception e) { throw ProjectException.MissingImage(e); }
                var header = Codecs.ReadPngHeader(data) ?? throw ProjectException.MissingImage();
                if (header.BitDepth > 8) throw ProjectException.MissingImage();
                if (isMask) CheckSize(header.Width, header.Height, ref maskPixels);
                else CheckSize(header.Width, header.Height, ref pixels);
                try
                {
                    if (isMask)
                    {
                        // Masks are 8-bit gray without alpha, as the Mac app validates.
                        if (!header.IsGray || header.BitDepth != 8) throw ProjectException.Invalid();
                        var mask = Codecs.DecodeGray(data);
                        masks[layer.Id] = new MaskAsset(mask, PixelOps.Thumbnail(mask));
                    }
                    else
                    {
                        var image = Codecs.DecodeRgba(data);
                        images[layer.Id] = new ImageAsset(image, PixelOps.Thumbnail(image), layer.Name);
                    }
                }
                catch (ProjectException) { throw; }
                catch (Exception e) { throw ProjectException.MissingImage(e); }
            }
        }
        return new ProjectSnapshot(manifest, images, masks);
    }

    /// <summary>
    /// Writes the complete package to a sibling staging directory, then swaps it into place, so a failed save never
    /// damages the previously saved project.
    /// </summary>
    public static void Save(ProjectSnapshot snapshot, string path)
    {
        Validate(snapshot.Manifest);
        var files = new Dictionary<string, byte[]>();
        long pixels = 0, maskPixels = 0;
        foreach (var layer in snapshot.Manifest.Layers)
        {
            foreach (bool isMask in new[] { false, true })
            {
                var filename = isMask ? layer.MaskFile : layer.ImageFile;
                if (filename == null) continue;
                try
                {
                    if (isMask)
                    {
                        if (!snapshot.Masks.TryGetValue(layer.Id, out var mask)) throw ProjectException.MissingImage();
                        CheckSize(mask.Image.Width, mask.Image.Height, ref maskPixels);
                        files[filename] = Codecs.EncodeGrayPng(mask.Image);
                    }
                    else
                    {
                        if (!snapshot.Images.TryGetValue(layer.Id, out var image)) throw ProjectException.MissingImage();
                        CheckSize(image.Image.Width, image.Image.Height, ref pixels);
                        files[filename] = Codecs.EncodePng(image.Image);
                    }
                }
                catch (ProjectException) { throw; }
                catch (Exception e) { throw new ProjectException(ProjectErrorKind.Encode, null, e); }
            }
        }
        var metadata = ProjectJson.WriteManifest(snapshot.Manifest);
        if (metadata.Length > MaxManifestBytes) throw ProjectException.TooLarge();

        var target = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(target) ?? throw ProjectException.Invalid();
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        if (File.Exists(target)) throw new IOException($"A file named “{Path.GetFileName(target)}” is in the way.");
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.saving-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".{Path.GetFileName(target)}.previous-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "images"));
            foreach (var (name, data) in files) WriteDurably(Path.Combine(staging, "images", name), data);
            WriteDurably(Path.Combine(staging, "manifest.json"), metadata);
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                try { Directory.Move(staging, target); }
                catch
                {
                    Directory.Move(backup, target);
                    throw;
                }
                TryDelete(backup);
            }
            else Directory.Move(staging, target);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static void WriteDurably(string file, byte[] data)
    {
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(true);
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { /* Left for the next save to ignore. */ }
    }

    private static void CheckFile(string file, string package, long maximumBytes)
    {
        var root = Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw ProjectException.Invalid();
        var info = new FileInfo(full);
        if (!info.Exists) throw file.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) ? ProjectException.Invalid() : ProjectException.MissingImage();
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null) throw ProjectException.TooLarge();
        // A symbolic link anywhere between the package and the file could lead outside it.
        for (var dir = info.Directory; dir != null && dir.FullName.Length >= root.Length - 1; dir = dir.Parent)
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && !string.Equals(dir.FullName.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase))
                throw ProjectException.Invalid();
        if (info.Length > maximumBytes) throw ProjectException.TooLarge();
    }
}
