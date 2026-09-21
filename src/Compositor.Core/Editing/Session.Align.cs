using Compositor.Geometry;
using Compositor.Model;

namespace Compositor.Editing;

/// <summary>Photoshop's Align and Distribute (Layer › Align, and the Move tool's options bar).</summary>
public enum AlignEdge { Left, HorizontalCenter, Right, Top, VerticalCenter, Bottom }

public enum DistributeAxis { Horizontal, Vertical }

public sealed partial class EditorSession
{
    /// <summary>What moves together: each selected layer, or a selected folder with everything inside it.</summary>
    private sealed record AlignUnit(List<int> Indices, RectD Box);

    private List<AlignUnit> AlignUnits()
    {
        if (document == null) return new();
        var chosen = SelectedLayerIds.Count > 0 ? SelectedLayerIds.ToHashSet() : ActiveLayerId is { } a ? new HashSet<Guid> { a } : new HashSet<Guid>();
        // A layer inside a chosen folder moves with the folder.
        var covered = new HashSet<Guid>();
        foreach (var id in chosen) if (document.Layer(id)?.IsGroup == true) covered.UnionWith(DescendantIds(id));
        var units = new List<AlignUnit>();
        foreach (var id in chosen.Where(id => !covered.Contains(id)))
        {
            var members = document.Layer(id)?.IsGroup == true ? DescendantIds(id) : new HashSet<Guid> { id };
            var indices = members.Select(m => document.IndexOf(m))
                .Where(i => i >= 0 && !document.Layers[i].IsGroup && document.Layers[i].Adjustment == null && document.Layers[i].Asset != null).ToList();
            if (indices.Count == 0) continue;
            var box = RectD.Null;
            foreach (var i in indices) box = box.Union(Bounds(document.Layers[i].Transform));
            units.Add(new AlignUnit(indices, box));
        }
        return units;
    }

    private static RectD Bounds(LayerTransform transform)
    {
        var corners = transform.Corners();
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y), maxX = corners.Max(c => c.X), maxY = corners.Max(c => c.Y);
        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    private bool CanArrange => document != null && !IsProjectBusy && !IsImporting && brushStroke == null && warpStroke == null
        && RenamingLayerId == null && CropRect == null && GradientEdit == null && PixelMove == null && HueSaturation == null && Levels == null
        && FilterEdit == null && AdjustmentEditingId == null && (TransformEditState == null || TransformEditState.Persistent);

    public bool CanAlign => CanArrange && !SelectedLayersLocked && AlignUnits().Count > 0;
    public bool CanDistribute => CanArrange && !SelectedLayersLocked && AlignUnits().Count >= 3;

    /// <summary>What one layer aligns to: the selection if there is one, otherwise the canvas.</summary>
    public string AlignReference => AlignUnits().Count > 1 ? "the selected layers" : Selection is { IsEmpty: false } ? "the selection" : "the canvas";

    public void Align(AlignEdge edge)
    {
        if (!CanAlign) return;
        CommitTransform();
        if (!CanEditLayers || document == null) return;
        var units = AlignUnits();
        RectD reference;
        if (units.Count > 1)
        {
            reference = RectD.Null;
            foreach (var u in units) reference = reference.Union(u.Box);
        }
        else reference = Selection is { IsEmpty: false } selection ? selection.Bounds : new RectD(0, 0, document.Width, document.Height);
        BeginEdit(edge switch
        {
            AlignEdge.Left => "Align Left Edges", AlignEdge.HorizontalCenter => "Align Horizontal Centers", AlignEdge.Right => "Align Right Edges",
            AlignEdge.Top => "Align Top Edges", AlignEdge.VerticalCenter => "Align Vertical Centers", _ => "Align Bottom Edges",
        });
        foreach (var unit in units)
        {
            var b = unit.Box;
            double dx = edge switch
            {
                AlignEdge.Left => reference.X - b.X,
                AlignEdge.HorizontalCenter => reference.X + reference.Width / 2 - (b.X + b.Width / 2),
                AlignEdge.Right => reference.X + reference.Width - (b.X + b.Width),
                _ => 0,
            };
            double dy = edge switch
            {
                AlignEdge.Top => reference.Y - b.Y,
                AlignEdge.VerticalCenter => reference.Y + reference.Height / 2 - (b.Y + b.Height / 2),
                AlignEdge.Bottom => reference.Y + reference.Height - (b.Y + b.Height),
                _ => 0,
            };
            Shift(unit, dx, dy);
        }
        EndEdit();
    }

    /// <summary>Spaces the selected layers' centres evenly between the outermost two.</summary>
    public void Distribute(DistributeAxis axis)
    {
        if (!CanDistribute) return;
        CommitTransform();
        if (!CanEditLayers || document == null) return;
        bool horizontal = axis == DistributeAxis.Horizontal;
        double Centre(AlignUnit u) => horizontal ? u.Box.X + u.Box.Width / 2 : u.Box.Y + u.Box.Height / 2;
        var units = AlignUnits().OrderBy(Centre).ToList();
        double first = Centre(units[0]), last = Centre(units[^1]), step = (last - first) / (units.Count - 1);
        BeginEdit(horizontal ? "Distribute Horizontal Centers" : "Distribute Vertical Centers");
        for (int i = 1; i < units.Count - 1; i++)
        {
            double delta = first + step * i - Centre(units[i]);
            Shift(units[i], horizontal ? delta : 0, horizontal ? 0 : delta);
        }
        EndEdit();
    }

    /// <summary>Moves a unit by whole pixels, so aligned layers stay sharp; unlinked masks stay where they are.</summary>
    private void Shift(AlignUnit unit, double dx, double dy)
    {
        // Round where the box lands (not the offset), so aligning twice never flips a half-pixel centre back and forth.
        dx = Math.Round(unit.Box.X + dx, MidpointRounding.AwayFromZero) - unit.Box.X;
        dy = Math.Round(unit.Box.Y + dy, MidpointRounding.AwayFromZero) - unit.Box.Y;
        if (dx == 0 && dy == 0) return;
        foreach (var index in unit.Indices)
        {
            var layer = document!.Layers[index];
            var moved = layer.Transform with { Origin = layer.Transform.Origin.Offset(dx, dy) };
            var mask = layer.Mask is { } m ? m with { Placement = m.PlacementMovingLayer(layer.Transform, moved) } : null;
            ReplaceLayer(index, layer with { Transform = moved, Mask = mask });
        }
    }
}
