using System.Collections.Immutable;
using Compositor.Format;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;
using Compositor.Pixels;
using Compositor.Rendering;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    // MARK: Multi-selection and folders (LayerGroups.swift)

    public void SelectLayers(ISet<Guid> ids, Guid? primary)
    {
        if (brushStroke != null) return;
        var valid = ids.Intersect(document?.Layers.Select(l => l.Id) ?? Enumerable.Empty<Guid>()).ToImmutableHashSet();
        if (!valid.SetEquals(SelectedLayerIds)) { CommitTransform(); ResolveGradient(); }
        ActiveLayerId = primary is { } p && valid.Contains(p) ? p : valid.FirstOrDefault() is var first && valid.Count > 0 ? first : null;
        SelectedLayerIds = valid;
        InvalidateCanvas();
    }

    public HashSet<Guid> DescendantIds(Guid id)
    {
        var children = (document?.Layers ?? ImmutableList<ImageLayer>.Empty).Where(l => l.ParentId != null)
            .GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(id);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            if (!children.TryGetValue(parent, out var list)) continue;
            foreach (var child in list) if (result.Add(child.Id)) pending.Push(child.Id);
        }
        return result;
    }

    private string NextFolderName()
    {
        var names = new HashSet<string>(document!.Layers.Select(l => l.Name));
        int number = 1;
        while (names.Contains($"Folder {number}")) number++;
        return $"Folder {number}";
    }

    public void GroupSelectedLayers()
    {
        if (!CanEditLayers || SelectedLayersLocked || document == null || document.Layers.Count >= 10_000) return;
        var byId = document.Layers.ToDictionary(l => l.Id);
        var selected = SelectedLayerIds.Where(byId.ContainsKey).ToHashSet();
        List<Guid?> Ancestors(Guid id)
        {
            var result = new List<Guid?>();
            var parent = byId.GetValueOrDefault(id)?.ParentId;
            while (parent is { } p) { result.Add(p); parent = byId.GetValueOrDefault(p)?.ParentId; }
            result.Add(null);
            return result;
        }
        var rootIds = selected.Where(id => !Ancestors(id).Any(a => a is { } v && selected.Contains(v))).ToHashSet();
        var ordered = document.HierarchyEntries().Select(e => e.Layer.Id).Where(rootIds.Contains).ToList();
        Guid? parent = null;
        if (ordered.Count > 0)
            parent = Ancestors(ordered[0]).FirstOrDefault(candidate => ordered.All(o => Ancestors(o).Contains(candidate)));
        var group = ImageLayer.Blank(NextFolderName(), document.Size) with { IsGroup = true, ParentId = parent };
        var branches = ordered.Select(id =>
        {
            var branch = id;
            while (byId.GetValueOrDefault(branch)?.ParentId is { } next && next != parent) branch = next;
            return branch;
        }).ToHashSet();
        int highest = document.Layers.FindLastIndex(l => branches.Contains(l.Id));
        int insertion = highest >= 0 ? document.Layers.Take(highest + 1).Count(l => !rootIds.Contains(l.Id)) : document.Layers.Count;
        var layers = document.Layers.Where(l => !rootIds.Contains(l.Id)).ToList();
        layers.Insert(Math.Min(insertion, layers.Count), group);
        foreach (var id in ordered)
            if (byId.TryGetValue(id, out var child)) layers.Add(child with { ParentId = group.Id });
        try { LayerHierarchy.Validate(layers.Select(l => l.HierarchyRecord()).ToList()); } catch { return; }
        BeginEdit("Group Layers");
        SetLayers(layers);
        ActiveLayerId = group.Id;
        if (parent is { } p2) CollapsedGroupIds.Remove(p2);
        EndEdit();
    }

    public void AddGroup()
    {
        if (!CanEditLayers || document == null || document.Layers.Count >= 10_000) return;
        var group = ImageLayer.Blank(NextFolderName(), document.Size) with
        {
            IsGroup = true, ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId,
        };
        int insertion = ActiveLayerId is { } a && document.IndexOf(a) is >= 0 and var i ? i + 1 : document.Layers.Count;
        var layers = document.Layers.Insert(insertion, group);
        try { LayerHierarchy.Validate(layers.Select(l => l.HierarchyRecord()).ToList()); } catch { return; }
        BeginEdit("New Folder");
        SetLayers(layers);
        ActiveLayerId = group.Id;
        if (group.ParentId is { } parent) CollapsedGroupIds.Remove(parent);
        EndEdit();
    }

    public void ToggleGroupExpansion(Guid id)
    {
        if (IsProjectBusy || document?.Layer(id)?.IsGroup != true) return;
        if (CollapsedGroupIds.Contains(id)) CollapsedGroupIds.Remove(id);
        else
        {
            if (ActiveLayerId is { } active && DescendantIds(id).Contains(active)) SelectLayer(id);
            CollapsedGroupIds.Add(id);
        }
        Notify();
    }

    public bool CanPlaceLayer(Guid id, Guid? parent)
    {
        if (!CanEditLayers || document?.Layer(id) is not { IsLocked: false }) return false;
        if (parent is { } destination && document.Layer(destination)?.IsLocked == true) return false;
        if (parent is not { } p) return true;
        return p != id && !DescendantIds(id).Contains(p) && document!.Layer(p)?.IsGroup == true;
    }

    public bool PlaceLayer(Guid id, Guid? parent, Guid? above = null, bool atBottom = false)
    {
        if (!CanPlaceLayer(id, parent) || document == null || above == id) return false;
        var layers = document.Layers.ToList();
        int index = layers.FindIndex(l => l.Id == id);
        var layer = layers[index] with { ParentId = parent };
        layers.RemoveAt(index);
        int insertion = atBottom ? 0 : layers.Count;
        if (above is { } target)
        {
            int targetIndex = layers.FindIndex(l => l.Id == target && l.ParentId == parent);
            if (targetIndex < 0) return false;
            insertion = targetIndex + 1;
        }
        layers.Insert(insertion, layer);
        AdoptClipping(id, layers);
        ReleaseDetachedClipping(layers);
        try { LayerHierarchy.Validate(layers.Select(l => l.HierarchyRecord()).ToList()); } catch { return false; }
        BeginEdit("Move Layer");
        SetLayers(layers);
        ActiveLayerId = id;
        if (parent is { } p) CollapsedGroupIds.Remove(p);
        EndEdit();
        return true;
    }

    public void MoveActiveLayerOutOfGroup()
    {
        if (ActiveLayer is not { ParentId: { } parent } layer || document?.Layer(parent) is not { } group) return;
        PlaceLayer(layer.Id, group.ParentId, group.Id);
    }

    // MARK: Clipping masks (LiveLayerMask.swift)

    public bool CanLinkMask(Guid source, Guid target)
    {
        if (!CanEditLayers || source == target || document == null) return false;
        if (!document.Layers.Any(l => l.Id == source && !l.IsGroup && l.Adjustment == null)) return false;
        if (!document.Layers.Any(l => l.Id == target && !l.IsGroup)) return false;
        var records = document.Layers.Select(l => l.Id == target ? l.HierarchyRecord() with { MaskSourceId = source } : l.HierarchyRecord()).ToList();
        try { LiveMaskGraph.Validate(records); return true; } catch { return false; }
    }

    public bool LinkMask(Guid source, Guid target)
    {
        if (!CanLinkMask(source, target)) return false;
        int index = document!.IndexOf(target);
        if (document.Layers[index].MaskSourceId == source) return true;
        BeginEdit("Create Clipping Mask");
        ReplaceLayer(index, document.Layers[index] with { MaskSourceId = source });
        EndEdit();
        return true;
    }

    /// <summary>A layer dropped into the middle of a clipping group joins it.</summary>
    internal static void AdoptClipping(Guid id, List<ImageLayer> layers)
    {
        var layer = layers.FirstOrDefault(l => l.Id == id);
        if (layer == null || layer.IsGroup) return;
        var siblings = layers.Where(l => l.ParentId == layer.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == id);
        if (index <= 0 || index + 1 >= siblings.Count || siblings[index + 1].MaskSourceId is not { } source || source == id) return;
        var below = siblings[index - 1];
        if (below.Id != source && below.MaskSourceId != source) return;
        int position = layers.FindIndex(l => l.Id == id);
        layers[position] = layers[position] with { MaskSourceId = source };
    }

    /// <summary>A moved layer stops clipping when it no longer belongs to the contiguous stack above its base.</summary>
    internal static void ReleaseDetachedClipping(List<ImageLayer> layers)
    {
        var release = new HashSet<Guid>();
        foreach (var stack in layers.GroupBy(l => l.ParentId))
        {
            Guid? baseId = null;
            foreach (var layer in stack)
            {
                if (layer.MaskSourceId is { } source)
                {
                    if (source != baseId) { release.Add(layer.Id); baseId = layer.Id; }
                }
                else baseId = layer.IsGroup ? null : layer.Id;
            }
        }
        for (int i = 0; i < layers.Count; i++)
            if (release.Contains(layers[i].Id)) layers[i] = layers[i] with { MaskSourceId = null };
    }

    public void RemoveLiveMask(Guid target)
    {
        if (!CanEditLayers || document?.Layer(target) is not { MaskSourceId: { } source } targetLayer) return;
        var siblings = document.Layers.Where(l => l.ParentId == targetLayer.ParentId).ToList();
        int targetIndex = siblings.FindIndex(l => l.Id == target);
        if (targetIndex < 0) return;
        var releases = siblings.Skip(targetIndex).TakeWhile(l => l.Id == target || l.MaskSourceId == source).Select(l => l.Id).ToHashSet();
        BeginEdit("Release Clipping Mask");
        SetLayers(document.Layers.Select(l => releases.Contains(l.Id) ? l with { MaskSourceId = null } : l));
        EndEdit();
    }

    public bool CanToggleClippingMask(Guid id)
    {
        if (!CanEditLayers || document?.Layer(id) is not { IsGroup: false } layer) return false;
        if (layer.MaskSourceId != null) return true;
        var siblings = document.Layers.Where(l => l.ParentId == layer.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == id);
        if (index <= 0 || siblings[index - 1].IsGroup) return false;
        return CanLinkMask(siblings[index - 1].MaskSourceId ?? siblings[index - 1].Id, id);
    }

    /// <summary>Alt-click clips to the next lower sibling, sharing its base when it is already clipped.</summary>
    public void ToggleClippingMask(Guid id)
    {
        if (!CanEditLayers || document?.Layer(id) is not { IsGroup: false } layer) return;
        if (layer.MaskSourceId != null) { RemoveLiveMask(id); return; }
        var siblings = document.Layers.Where(l => l.ParentId == layer.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == id);
        if (index <= 0) return;
        var below = siblings[index - 1];
        if (below.IsGroup) return;
        LinkMask(below.MaskSourceId ?? below.Id, id);
    }

    // MARK: Deleting layers that supply live masks

    public async Task<bool> DeleteWithLiveMaskChoice(IReadOnlyList<Guid> ids)
    {
        var removed = new HashSet<Guid>();
        foreach (var id in ids) { removed.Add(id); removed.UnionWith(DescendantIds(id)); }
        var targets = (document?.Layers ?? ImmutableList<ImageLayer>.Empty)
            .Where(l => !removed.Contains(l.Id) && l.MaskSourceId is { } s && removed.Contains(s)).Select(l => l.Id).ToList();
        if (targets.Count == 0) return false;
        var choice = await Host.AskBakeLiveMasks(ids.Count != 1);
        if (choice == false) { FinishDeletingLayers(ids.ToList(), new()); return true; }
        if (choice != true || ProjectSnapshot() is not { } snapshot) return true;
        IsProjectBusy = true;
        try
        {
            var baked = await Task.Run(() =>
            {
                var result = new Dictionary<Guid, ImageAsset>();
                foreach (var target in targets) if (LiveMaskBaker.Bake(snapshot, target) is { } asset) result[target] = asset;
                return result;
            });
            IsProjectBusy = false;
            FinishDeletingLayers(ids.ToList(), baked);
        }
        catch (Exception e) { BrushError = e.Message; }
        finally { IsProjectBusy = false; Notify(); }
        return true;
    }

    public void FinishDeletingLayer(Guid id, Dictionary<Guid, ImageAsset> baked)
    {
        int index = document?.IndexOf(id) ?? -1;
        if (index < 0) return;
        var removed = DescendantIds(id);
        removed.Add(id);
        BeginEdit("Delete Layer");
        var layers = document!.Layers.Where(l => !removed.Contains(l.Id)).Select(l =>
        {
            if (l.MaskSourceId is { } source && removed.Contains(source))
                return baked.TryGetValue(l.Id, out var asset) ? l with { MaskSourceId = null, Asset = asset } : l with { MaskSourceId = null };
            return l;
        }).ToImmutableList();
        SetLayers(layers);
        if (ActiveLayerId is { } active && removed.Contains(active))
            ActiveLayerId = layers.IsEmpty ? null : layers[Math.Min(index, layers.Count - 1)].Id;
        EndEdit();
    }

    public void FinishDeletingLayers(List<Guid> ids, Dictionary<Guid, ImageAsset> baked)
    {
        if (ids.Count <= 1) { if (ids.Count == 1) FinishDeletingLayer(ids[0], baked); return; }
        BeginEdit("Delete Layers");
        foreach (var id in ids) FinishDeletingLayer(id, baked);
        EndEdit();
    }

    // MARK: Appearance (LayerAppearance.swift)

    public LayerBlendMode DisplayedBlendMode(ImageLayer layer) =>
        BlendPreview is { } preview && preview.LayerId == layer.Id && ActiveLayerId == layer.Id ? preview.Mode : layer.BlendMode;

    public void PreviewBlendMode(LayerBlendMode? mode, Guid? id)
    {
        BlendPreview = mode is { } m && id is { } i && i == ActiveLayerId && CanEditBlendAppearance ? (i, m) : null;
        InvalidateCanvas();
    }

    public bool CanEditAppearance => CanEditLayers && SelectedLayerIds.Count == 1 && ActiveLayer is { IsLocked: false };
    public bool CanEditBlendAppearance => CanEditAppearance && ActiveLayer?.IsGroup == false;

    public void BeginOpacityEdit()
    {
        if (!CanEditAppearance || OpacityEditLayerId != null || ActiveLayerId is not { } id) return;
        BeginEdit("Layer Opacity");
        OpacityEditLayerId = id;
    }

    public void FinishOpacityEdit()
    {
        if (OpacityEditLayerId == null) return;
        OpacityEditLayerId = null;
        EndEdit();
    }

    public void SetLayerOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || !CanEditAppearance || (OpacityEditLayerId ?? ActiveLayerId) is not { } id) return;
        int index = document!.IndexOf(id);
        if (index < 0) return;
        bool standalone = OpacityEditLayerId == null;
        if (standalone) BeginEdit("Layer Opacity");
        ReplaceLayer(index, document.Layers[index] with { Opacity = Math.Clamp(opacity, 0, 1) });
        if (standalone) EndEdit();
    }

    public void SetSelectedLayersOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || !CanEditLayers || document == null) return;
        double value = Math.Clamp(opacity, 0, 1);
        var indices = Enumerable.Range(0, document.Layers.Count)
            .Where(i => SelectedLayerIds.Contains(document.Layers[i].Id) && document.Layers[i].Opacity != value).ToList();
        if (indices.Count == 0) return;
        FinishOpacityEdit();
        BeginEdit("Layer Opacity");
        foreach (var i in indices) ReplaceLayer(i, document!.Layers[i] with { Opacity = value });
        EndEdit();
    }

    public void CycleBlendMode(bool forward)
    {
        if (!CanEditBlendAppearance || ActiveLayer is not { } layer) return;
        var modes = BlendModes.All;
        int index = Array.IndexOf(modes, layer.BlendMode);
        SetLayerBlendMode(modes[(index + (forward ? 1 : modes.Length - 1)) % modes.Length]);
    }

    public void SetLayerBlendMode(LayerBlendMode mode)
    {
        BlendPreview = null;
        if (!CanEditBlendAppearance || ActiveLayerId is not { } id || document!.IndexOf(id) is not (>= 0 and var index)) return;
        FinishOpacityEdit();
        BeginEdit("Layer Blend Mode");
        ReplaceLayer(index, document.Layers[index] with { BlendMode = mode });
        EndEdit();
    }

    // MARK: Masks (LayerMask.swift)

    public bool CanEditMask => CanEditLayers && SelectedLayerIds.Count == 1 && ActiveLayer is { IsLocked: false };

    public void SelectLayerTarget(Guid id, bool mask)
    {
        if (IsProjectBusy || IsImporting || brushStroke != null) return;
        ResolveGradient();
        SelectLayer(id);
        IsMaskSelected = mask && ActiveLayer?.Mask != null;
    }

    /// <summary>The footer button: with no selection a plain reveal/hide mask; with one, the selection painted the
    /// opposite colour, used up in the same undo step.</summary>
    public void AddMask(bool revealing = true)
    {
        if (Selection is not { } selection) { AddLayerMask(revealing); return; }
        if (!CanEditMask || ActiveLayer is not { Mask: null } layer || document == null) return;
        int index = document.IndexOf(layer.Id);
        int width = layer.Asset?.Image.Width ?? (int)Math.Round(layer.Size.Width);
        int height = layer.Asset?.Image.Height ?? (int)Math.Round(layer.Size.Height);
        try
        {
            if (width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw ProjectException.TooLarge();
            var toDocument = LayerTransform.PixelToDocument(layer.Transform, width, height);
            using var alpha = PixelOps.NewAlpha(width, height);
            using (var canvas = new SkiaSharp.SKCanvas(alpha))
            {
                canvas.SetMatrix(toDocument.Inverted().ToSK());
                using var paint = SelectionRaster.FillPaint(selection.Antialiased);
                canvas.DrawPath(selection.SharedPath, paint);
            }
            var bytes = PixelOps.AlphaToMaskCopy(alpha).CopyPixels();
            if (revealing) for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(255 - bytes[i]);
            var mask = new LayerMask(PixelOps.MaskAssetFrom(MaskImage.FromPixels(width, height, bytes)));
            FinishOpacityEdit();
            BeginEdit("Add Mask from Selection");
            ReplaceLayer(index, document.Layers[index] with { Mask = mask });
            Document = document with { Selection = null };
            IsMaskSelected = true;
            EndEdit();
        }
        catch (Exception e) { BrushError = e.Message; Notify(); }
    }

    public void AddLayerMask(bool revealing = true)
    {
        if (!CanEditMask || ActiveLayer?.Mask != null || ActiveLayerId is not { } id) return;
        int index = document!.IndexOf(id);
        FinishOpacityEdit();
        BeginEdit(revealing ? "Add Reveal-All Mask" : "Add Hide-All Mask");
        ReplaceLayer(index, document.Layers[index] with { Mask = LayerMask.Solid(revealing) });
        IsMaskSelected = true;
        EndEdit();
    }

    public void ToggleLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: { } mask } layer) return;
        FinishOpacityEdit();
        BeginEdit(mask.IsEnabled ? "Disable Layer Mask" : "Enable Layer Mask");
        UpdateLayer(layer.Id, l => l with { Mask = mask with { IsEnabled = !mask.IsEnabled } });
        EndEdit();
    }

    public void DeleteLayerMask()
    {
        if (!CanEditMask || ActiveLayer is not { Mask: not null } layer) return;
        FinishOpacityEdit();
        BeginEdit("Delete Layer Mask");
        UpdateLayer(layer.Id, l => l with { Mask = null });
        IsMaskSelected = false;
        EndEdit();
    }

    public bool CanCopyMask(Guid source, Guid target) =>
        CanEditLayers && source != target && document?.Layer(source)?.Mask != null && document.Layer(target) is { IsGroup: false };

    /// <summary>Alt-dragging a mask thumbnail onto another layer: a copy of the mask, where it sits on the document.</summary>
    public void CopyMask(Guid source, Guid target)
    {
        if (!CanCopyMask(source, target) || document!.Layer(source) is not { Mask: { } mask } from) return;
        CommitTransform();
        FinishOpacityEdit();
        var copy = mask with { Placement = from.MaskTransform };
        BeginEdit(document.Layer(target)!.Mask == null ? "Copy Layer Mask" : "Replace Layer Mask");
        UpdateLayer(target, l => l with { Mask = copy });
        SelectLayer(target);
        IsMaskSelected = true;
        EndEdit();
    }

    public void ToggleMaskLink(Guid id)
    {
        if (!CanEditLayers || document?.Layer(id) is not { IsLocked: false, Mask: { } mask }) return;
        CommitTransform();
        FinishOpacityEdit();
        BeginEdit(mask.IsLinked ? "Unlink Layer Mask" : "Link Layer Mask");
        UpdateLayer(id, l => l with { Mask = mask with { IsLinked = !mask.IsLinked } });
        EndEdit();
    }

    /// <summary>Where a layer's mask shows right now — null while it covers the layer's displayed pixel grid.</summary>
    public LayerTransform? DisplayedMaskPlacement(ImageLayer layer)
    {
        if (layer.Mask is not { } mask) return null;
        if (FilterEdit is { GrownTransform: not null } filter && filter.PreviewImage(layer.Id) != null) return mask.Placement ?? layer.Transform;
        if (TransformEditState is { } edit && edit.Group is { } group)
        {
            if (!group.Originals.TryGetValue(layer.Id, out var original)) return mask.Placement;
            if (edit.Corners != null) return mask.IsLinked && mask.Placement == null ? null : mask.Placement ?? layer.Transform;
            return mask.PlacementMovingLayer(layer.Transform, original.Following(group.Box, edit.Draft));
        }
        if (TransformEditState is not { } e || e.LayerId != layer.Id || e.Floating != null) return mask.Placement;
        if (e.Mask) return e.Draft.SamePlacement(layer.Transform) ? null : e.Draft;
        if (e.Corners != null) return mask.IsLinked && mask.Placement == null ? null : mask.Placement ?? layer.Transform;
        return mask.PlacementMovingLayer(layer.Transform, e.Draft);
    }

    /// <summary>Apply for an unlinked mask transformed on its own: it takes the new placement.</summary>
    public void CommitMaskTransform(TransformEdit edit)
    {
        if (!edit.Draft.IsValid || document?.Layer(edit.LayerId) is not { Mask: { } mask } layer) return;
        if (edit.Corners is { } corners)
        {
            try
            {
                var moved = DistortWarp.WarpMask(mask.Asset.Image, edit.Draft, corners, MaskPlacement.Background(mask.Asset.Thumbnail));
                var asset = ReferenceEquals(moved.Image, mask.Asset.Image) ? mask.Asset : PixelOps.MaskAssetFrom(moved.Image);
                FinishOpacityEdit();
                BeginEdit("Distort Layer Mask");
                UpdateLayer(layer.Id, l => l with
                {
                    Mask = new LayerMask(asset, mask.IsEnabled, moved.Transform.SamePlacement(layer.Transform) ? null : moved.Transform, mask.IsLinked),
                });
                EndEdit();
            }
            catch (Exception e) { BrushError = e.Message; Notify(); }
            return;
        }
        LayerTransform? placement = edit.Draft.SamePlacement(layer.Transform) ? null : edit.Draft;
        if (placement == mask.Placement) return;
        FinishOpacityEdit();
        BeginEdit("Transform Layer Mask");
        UpdateLayer(layer.Id, l => l with { Mask = mask with { Placement = placement } });
        EndEdit();
    }

    // MARK: Flip (LayerFlip.swift)

    public void FlipLayers(bool horizontally)
    {
        CommitTransform();
        if (!CanTransform || document == null) return;
        List<ImageLayer> members;
        double axis;
        if (TransformsAsGroup)
        {
            if (GroupTransformBox is not { } box) return;
            members = GroupTransformMembers;
            axis = horizontally ? box.Center.X : box.Center.Y;
        }
        else
        {
            if (ActiveLayer is not { } layer) return;
            members = new() { layer };
            axis = horizontally ? layer.Transform.Center.X : layer.Transform.Center.Y;
        }
        var ids = members.Select(m => m.Id).ToHashSet();
        if (ids.Count == 0) return;
        FinishOpacityEdit();
        BeginEdit(horizontally ? "Flip Horizontal" : "Flip Vertical");
        SetLayers(document.Layers.Select(layer =>
        {
            if (!ids.Contains(layer.Id)) return layer;
            var flipped = layer.Transform.Mirrored(horizontally, axis);
            var mask = layer.Mask is { } m ? m with { Placement = m.PlacementMovingLayer(layer.Transform, flipped) } : null;
            return layer with { Transform = flipped, Mask = mask };
        }));
        EndEdit();
    }

    public void FlipCanvas(bool horizontally)
    {
        CommitTransform();
        CancelCrop();
        if (!CanEditLayers || document == null) return;
        double axis = horizontally ? document.Width / 2.0 : document.Height / 2.0;
        FinishOpacityEdit();
        BeginEdit(horizontally ? "Flip Canvas Horizontal" : "Flip Canvas Vertical");
        var doc = document;
        var layers = doc.Layers.Select(layer => layer with
        {
            Transform = layer.Transform.Mirrored(horizontally, axis),
            Mask = layer.Mask is { Placement: { } p } m ? m with { Placement = p.Mirrored(horizontally, axis) } : layer.Mask,
        }).ToImmutableList();
        var selection = doc.Selection?.Transformed(horizontally ? new Affine(-1, 0, 0, 1, doc.Width, 0) : new Affine(1, 0, 0, -1, 0, doc.Height));
        Document = doc with { Layers = layers, Selection = selection };
        EndEdit();
    }

    // MARK: Merge (LayerMerge.swift)

    private (List<Guid> Ids, HashSet<Guid> Removed, string Name, Guid? Parent, Guid Anchor, string Action)? MergePlan()
    {
        if (!CanEditLayers || document == null || ActiveLayer is not { } active) return null;
        var layers = document.Layers;
        if (SelectedLayerIds.Count > 1)
        {
            var picked = new HashSet<Guid>(SelectedLayerIds);
            foreach (var id in SelectedLayerIds) picked.UnionWith(DescendantIds(id));
            var ordered = layers.Where(l => picked.Contains(l.Id)).ToList();
            var top = ordered.LastOrDefault(l => SelectedLayerIds.Contains(l.Id));
            if (!ordered.Any(l => !l.IsGroup) || top == null) return null;
            return (ordered.Select(l => l.Id).ToList(), picked, top.Name, top.ParentId, top.Id, "Merge Layers");
        }
        if (active.IsGroup)
        {
            var inside = DescendantIds(active.Id);
            if (!layers.Any(l => inside.Contains(l.Id) && !l.IsGroup)) return null;
            var ids = layers.Where(l => inside.Contains(l.Id) || l.Id == active.Id).Select(l => l.Id).ToList();
            return (ids, ids.ToHashSet(), active.Name, active.ParentId, active.Id, "Merge Group");
        }
        int index = document.IndexOf(active.Id);
        var below = layers.Take(index).LastOrDefault(l => l.ParentId == active.ParentId);
        if (below == null || below.IsGroup) return null;
        return (new List<Guid> { below.Id, active.Id }, new HashSet<Guid> { below.Id, active.Id }, below.Name, active.ParentId, active.Id, "Merge Down");
    }

    public bool CanMergeLayers => MergePlan() != null;
    public string MergeTitle => MergePlan()?.Action ?? "Merge Down";

    /// <summary>Ctrl+E: the layers composited as the canvas shows them into one pixel layer, trimmed, in their place.</summary>
    public void MergeLayers()
    {
        CommitTransform();
        if (MergePlan() is not { } plan || document == null) return;
        var layers = document.Layers;
        var kept = plan.Ids.ToHashSet();
        var subset = layers.Where(l => kept.Contains(l.Id)).Select(l => l with
        {
            ParentId = l.ParentId is { } p && !kept.Contains(p) ? null : l.ParentId,
            MaskSourceId = l.MaskSourceId is { } s && !kept.Contains(s) ? null : l.MaskSourceId,
        }).ToImmutableList();
        var flat = document with { Layers = subset };
        RasterImage full;
        try { full = RenderComposite(flat); }
        catch { Host.Beep(); return; }
        var canvas = LayerTransform.Canvas(document.Width, document.Height);
        var trimmed = PixelFilter.Trimmed(full, canvas);
        var merged = ImageLayer.FromAsset(PixelOps.Asset(trimmed.Image, plan.Name), trimmed.Transform.Origin) with
        {
            Transform = trimmed.Transform, Name = plan.Name, ParentId = plan.Parent,
        };
        var next = layers.Where(l => !plan.Removed.Contains(l.Id))
            .Select(l => l.MaskSourceId is { } s && plan.Removed.Contains(s) ? l with { MaskSourceId = merged.Id } : l).ToList();
        int slot = document.IndexOf(plan.Anchor);
        if (slot < 0) slot = layers.Count;
        int insertion = slot - layers.Take(slot).Count(l => plan.Removed.Contains(l.Id));
        next.Insert(Math.Clamp(insertion, 0, next.Count), merged);
        try { LayerHierarchy.Validate(next.Select(l => l.HierarchyRecord()).ToList()); } catch { Host.Beep(); return; }
        FinishOpacityEdit();
        BeginEdit(plan.Action);
        SetLayers(next);
        ActiveLayerId = merged.Id;
        EndEdit();
    }
}

/// <summary>Deletion can bake a live mask's coverage into dependent image pixels (LiveMaskBaker).</summary>
public static class LiveMaskBaker
{
    public static ImageAsset? Bake(ProjectSnapshot snapshot, Guid target)
    {
        var record = snapshot.Manifest.Layers.FirstOrDefault(l => l.Id == target);
        if (record == null || !snapshot.Images.TryGetValue(target, out var original)) return null;
        int w = original.Image.Width, h = original.Image.Height;
        var toPixels = LayerTransform.PixelToDocument(record.Transform, w, h).Inverted();
        var source = new BakeSource(snapshot, target, record);
        var bitmap = PixelOps.NewRgba(w, h);
        using (var surface = new RenderSurface(bitmap, toPixels))
        {
            // Only the target and its live-mask chain draw; the target keeps its own raster mask and appearance.
            CompositeRenderer.Render(source, surface);
        }
        var image = RasterImage.Adopt(bitmap);
        return new ImageAsset(image, PixelOps.Thumbnail(image), original.Name);
    }

    private sealed class BakeSource : ICompositeSource
    {
        private readonly ProjectSnapshot snapshot;
        private readonly Guid target;
        private readonly Dictionary<Guid, ProjectLayerRecord> records;
        public BakeSource(ProjectSnapshot snapshot, Guid target, ProjectLayerRecord record)
        {
            this.snapshot = snapshot;
            this.target = target;
            records = snapshot.Manifest.Layers.ToDictionary(l => l.Id);
        }
        public IReadOnlyList<Guid> RenderOrder => new[] { target };
        public Guid? Parent(Guid id) => null;
        public Guid? MaskSource(Guid id) => records.GetValueOrDefault(id)?.MaskSourceId;
        public LayerAdjustment? Adjustment(Guid id) => null;
        public double Opacity(Guid id) => 1;
        public LayerBlendMode Blend(Guid id) => LayerBlendMode.Normal;
        public ClipMask? FolderClip(Guid folderId) => null;
        public ClipMask? AdjustmentClip(Guid id) => null;
        public void DrawOwn(Guid id, RenderSurface surface, IReadOnlyList<ClipMask> clips)
        {
            if (!records.TryGetValue(id, out var layer) || !snapshot.Images.TryGetValue(id, out var asset)) return;
            if (id == target)
            {
                LayerRenderer.Draw(surface, asset.Image, layer.Transform with { Sampling = LayerSampling.Nearest }, 1, LayerBlendMode.Normal, null, clips);
                return;
            }
            var mask = snapshot.Mask(layer) is { } owned ? MaskPlacement.ClipImage(owned, owned.Placement, layer.Transform, asset.Image.Width, asset.Image.Height) : null;
            LayerRenderer.Draw(surface, asset.Image, layer.Transform, layer.Opacity ?? 1, LayerBlendMode.Normal, mask, clips);
        }
    }
}
