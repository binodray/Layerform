using Compositor.Format;
using Compositor.Model;
using Compositor.Rendering;

// Command-line checks for `.comp` projects: inspect metadata, export the flattened image, and verify a round trip.
static int Usage()
{
    Console.Error.WriteLine("""
        compositor-cli inspect <project.comp>
        compositor-cli export <project.comp> <output.png|output.jpg> [--quality 0-100]
        compositor-cli roundtrip <project.comp> <copy.comp>
        """);
    return 2;
}

if (args.Length < 2) return Usage();
try
{
    switch (args[0])
    {
        case "inspect":
        {
            var snapshot = ProjectStore.Load(args[1]);
            var m = snapshot.Manifest;
            Console.WriteLine($"Format {m.Format} v{m.Version}, {m.Width} x {m.Height} px, {m.Resolution ?? 72} ppi, {m.Layers.Count} layers");
            foreach (var entry in LayerHierarchy.Entries(m.Layers, topFirst: true))
            {
                var l = entry.Layer;
                var kind = l.IsGroup == true ? "folder" : l.Adjustment is { } a ? $"adjustment {a.Kind.ToName()}" : l.ImageFile != null ? "pixels" : "empty";
                var extras = new List<string>();
                if ((l.Opacity ?? 1) != 1) extras.Add($"opacity {l.Opacity:0.##}");
                if ((l.BlendMode ?? LayerBlendMode.Normal) != LayerBlendMode.Normal) extras.Add(l.BlendMode!.Value.ToName());
                if (l.MaskFile != null) extras.Add(l.MaskEnabled == false ? "mask (off)" : "mask");
                if (l.MaskSourceId != null) extras.Add("clipped");
                if (!l.IsVisible) extras.Add("hidden");
                if (l.Shape != null) extras.Add($"shape {l.Shape.Kind}");
                Console.WriteLine($"{new string(' ', entry.Depth * 2)}- {l.Name} [{kind}] {l.Transform.Size.Width:0.##}x{l.Transform.Size.Height:0.##} at ({l.Transform.Origin.X:0.##}, {l.Transform.Origin.Y:0.##}) {string.Join(", ", extras)}");
            }
            return 0;
        }
        case "export" when args.Length >= 3:
        {
            var snapshot = ProjectStore.Load(args[1]);
            var output = args[2];
            if (output.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || output.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                int quality = 85;
                int index = Array.IndexOf(args, "--quality");
                if (index > 0 && index + 1 < args.Length) quality = int.Parse(args[index + 1]);
                var raster = ImageExporter.Render(snapshot);
                ImageExporter.WriteAtomically(ImageExporter.Jpeg(raster, quality / 100.0, PaletteColor.White), output);
            }
            else ImageExporter.ExportPng(snapshot, output);
            Console.WriteLine($"Exported {output}");
            return 0;
        }
        case "roundtrip" when args.Length >= 3:
        {
            var original = ProjectStore.Load(args[1]);
            ProjectStore.Save(original, args[2]);
            var copy = ProjectStore.Load(args[2]);
            var a = ImageExporter.Render(original).Image;
            var b = ImageExporter.Render(copy).Image;
            bool same = a.Pixels.SequenceEqual(b.Pixels);
            Console.WriteLine(same ? "Round trip identical: metadata reloaded and rendered pixels match." : "Round trip DIFFERS in rendered pixels.");
            return same ? 0 : 1;
        }
        default:
            return Usage();
    }
}
catch (ProjectException e)
{
    Console.Error.WriteLine($"Project error ({e.Kind}): {e.Message}");
    return 1;
}
