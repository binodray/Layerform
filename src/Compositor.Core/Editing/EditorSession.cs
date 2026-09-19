using System.Collections.Immutable;
using Compositor.Geometry;
using Compositor.Imaging;
using Compositor.Model;

namespace Compositor.Editing;

public enum NavigationTool { Move, Marquee, Lasso, Wand, Crop, Brush, SpotHealing, CloneStamp, Blur, Gradient, Shape, Eyedropper, Hand, Zoom, Idle }

public static class NavigationTools
{
    public static readonly NavigationTool[] Rail = Enum.GetValues<NavigationTool>().Where(t => t != NavigationTool.Idle).ToArray();
    public static bool IsBrushTool(this NavigationTool t) => t is NavigationTool.Brush or NavigationTool.SpotHealing or NavigationTool.CloneStamp or NavigationTool.Blur;
    public static bool IsSelectionTool(this NavigationTool t) => t is NavigationTool.Marquee or NavigationTool.Lasso or NavigationTool.Wand;
    public static string Label(this NavigationTool t) => t switch
    {
        NavigationTool.Eyedropper => "Eyedropper (I)",
        NavigationTool.Marquee => "Marquee (M)",
        NavigationTool.Lasso => "Lasso (L)",
        NavigationTool.Wand => "Magic Wand (W)",
        NavigationTool.Brush => "Brush (B) · Eraser (E)",
        NavigationTool.SpotHealing => "Spot Healing Brush (J)",
        NavigationTool.CloneStamp => "Clone Stamp (S) · Alt-click sets the source",
        NavigationTool.Blur => "Smear (R)",
        NavigationTool.Gradient => "Gradient (G)",
        NavigationTool.Shape => "Shape (U) · Shift+U switches Rectangle/Ellipse",
        NavigationTool.Crop => "Crop (C)",
        NavigationTool.Move => "Move / Transform (V)",
        NavigationTool.Hand => "Hand (H)",
        NavigationTool.Zoom => "Zoom (Z)",
        _ => "No tool (A)",
    };
}

/// <summary>Services the session needs from the application (dialogs, clipboard, sounds).</summary>
public interface IEditorHost
{
    void Beep();
    /// <summary>Asks whether deleting layers that supply live masks should bake (true), remove links (false) or cancel (null).</summary>
    Task<bool?> AskBakeLiveMasks(bool plural);
    void SetClipboardImage(RasterImage image);
    RasterImage? GetClipboardImage();
    bool ClipboardHasImage { get; }
    /// <summary>A value that changes whenever another application writes the clipboard.</summary>
    long ClipboardSequence { get; }
}

public sealed class NullEditorHost : IEditorHost
{
    public static readonly NullEditorHost Instance = new();
    public RasterImage? Clipboard;
    public long Sequence;
    public void Beep() { }
    public Task<bool?> AskBakeLiveMasks(bool plural) => Task.FromResult<bool?>(false);
    public void SetClipboardImage(RasterImage image) { Clipboard = image; Sequence++; }
    public RasterImage? GetClipboardImage() => Clipboard;
    public bool ClipboardHasImage => Clipboard != null;
    public long ClipboardSequence => Sequence;
}

public sealed record BrushSettings
{
    public double Diameter { get; init; } = 40;
    public double Hardness { get; init; } = 1;
    public double Red { get; init; }
    public double Green { get; init; }
    public double Blue { get; init; }
    /// <summary>Caps the whole stroke: overlapping dabs never exceed it.</summary>
    public double Opacity { get; init; } = 1;
    public bool Erasing { get; init; }
    public bool Healing { get; init; }
    public SpotHealingMode HealingMode { get; init; } = SpotHealingMode.ContentAware;
}

public enum SpotHealingMode { ContentAware, CreateTexture, ProximityMatch }
public enum BrushToolMode { Paint, Erase }
public enum BlurToolMode { Liquify, Blur, Smudge }

public static class ModeNames
{
    public static string Name(this SpotHealingMode m) => m switch
    {
        SpotHealingMode.ContentAware => "Content-Aware", SpotHealingMode.CreateTexture => "Create Texture", _ => "Proximity Match",
    };
}

/// <summary>Several layers transformed together: the box around them when the edit began, and each one's transform.</summary>
public sealed record TransformGroup(LayerTransform Box, IReadOnlyDictionary<Guid, LayerTransform> Originals);

/// <summary>Cmd-T with a selection: the selected pixels float on a temporary layer until merged back.</summary>
public sealed record FloatingTransform(Guid SourceId, CanvasDocument Before, Guid? BeforeActive, LayerTransform Original, SizeD PixelSize);

public sealed record TransformEdit
{
    public Guid LayerId { get; init; }
    public LayerTransform Draft { get; init; }
    public bool Persistent { get; init; }
    public FloatingTransform? Floating { get; init; }
    /// <summary>Set once a handle is Ctrl-dragged: the four corners move freely.</summary>
    public PointD[]? Corners { get; init; }
    /// <summary>An unlinked mask selected: the edit places the mask alone.</summary>
    public bool Mask { get; init; }
    public TransformGroup? Group { get; init; }
}

public sealed partial class EditorSession
{
    public IEditorHost Host { get; set; } = NullEditorHost.Instance;
    public DocumentHistory History { get; } = new();

    /// <summary>Raised (possibly many times per edit) when anything the interface shows may have changed.</summary>
    public event Action? Changed;
    /// <summary>Raised when the canvas image must be redrawn (pixels, previews, overlays).</summary>
    public event Action? CanvasChanged;
    public void Notify() => Changed?.Invoke();
    public void InvalidateCanvas() { BrushRevision++; CanvasChanged?.Invoke(); Changed?.Invoke(); }

    private CanvasDocument? document;
    public CanvasDocument? Document
    {
        get => document;
        set { document = value; InvalidateCanvas(); }
    }

    public int BrushRevision { get; private set; }
    public string? ProjectPath { get; set; }
    public bool IsProjectBusy { get; set; }
    public bool IsImporting { get; set; }
    public string? ImportError { get; set; }
    public string? BrushError { get; set; }
    public string? CropError { get; set; }
    public Guid? RenamingLayerId { get; set; }
    public bool ShowsSampleRing { get; set; } = true;
    public bool ShowsPixelGrid { get; set; } = true;
    public CanvasViewport Viewport;

    private NavigationTool tool = NavigationTool.Move;
    public NavigationTool Tool { get => tool; private set { tool = value; Notify(); } }

    public HashSet<Guid> CollapsedGroupIds { get; set; } = new();
    public RectD? CropRect { get; set; }
    public string CropRatioChoice { get; set; } = "Free";
    public TransformEdit? TransformEditState { get; set; }
    public (double[] Xs, double[] Ys) SnapGuides { get; set; } = (Array.Empty<double>(), Array.Empty<double>());
    public (PointD Point, Guid LayerId, bool Mask)? LastBrushPoint { get; set; }
    public bool LocksTransformRatio { get; set; } = true;
    public bool TransformAutoSelect { get; set; }
    public bool ShowsTransformControls { get; set; } = true;
    private (Guid Copy, Guid Source)? transformDuplicate;

    private BrushSettings brushSettings = new();
    public BrushSettings BrushSettings { get => brushSettings; set { brushSettings = value; RefreshGradient(); Notify(); } }
    public SpotHealingMode SpotHealingMode { get; set; } = SpotHealingMode.ContentAware;
    public BlurToolMode BlurMode { get; set; } = BlurToolMode.Liquify;
    public BrushToolMode BrushMode { get; set; } = BrushToolMode.Paint;
    public PointD? CloneSource { get; set; }
    public CloneSettings CloneSettings { get; set; } = new();
    private readonly Dictionary<int, (double Diameter, double Hardness, double Opacity)> parkedBrushTips = new() { [1] = (40, 0, 1), [2] = (40, 0, 1) };
    private static int TipFamily(NavigationTool t) => t == NavigationTool.CloneStamp ? 1 : t == NavigationTool.Blur ? 2 : 0;
    public SizeD? CloneOffset { get; set; }
    private bool maskPaintWhite;
    public bool MaskPaintWhite { get => maskPaintWhite; set { maskPaintWhite = value; RefreshGradient(); Notify(); } }
    private PaletteColor backgroundColor = PaletteColor.White;
    public PaletteColor BackgroundColor { get => backgroundColor; set { backgroundColor = value; RefreshGradient(); Notify(); } }
    private GradientSettings gradientSettings = new();
    public GradientSettings GradientSettings { get => gradientSettings; set { gradientSettings = value; RefreshGradient(); Notify(); } }

    public double OpacityDigitWindow { get; set; } = 0.6;
    private (int Digit, double Time)? pendingOpacityDigit;
    public Guid? OpacityEditLayerId { get; private set; }
    public (Guid LayerId, LayerBlendMode Mode)? BlendPreview { get; private set; }

    private bool isMaskSelected;
    public bool IsMaskSelected { get => isMaskSelected; set { isMaskSelected = value; InvalidateCanvas(); } }
    public ImmutableHashSet<Guid> SelectedLayerIds { get; private set; } = ImmutableHashSet<Guid>.Empty;
    private Guid? activeLayerId;
    public Guid? ActiveLayerId
    {
        get => activeLayerId;
        set
        {
            if (activeLayerId != value) isMaskSelected = false;
            activeLayerId = value;
            SelectedLayerIds = value is { } id ? ImmutableHashSet.Create(id) : ImmutableHashSet<Guid>.Empty;
            InvalidateCanvas();
        }
    }

    public ImageLayer? ActiveLayer => document?.Layer(activeLayerId);
    public DocumentSelection? Selection => document?.Selection;
    public bool IsModified => History.IsModified;

    // MARK: State gates (the Mac app's computed permissions)

    public bool CanStartProjectOperation => !IsProjectBusy && !IsImporting && brushStroke == null && warpStroke == null && Levels == null
        && RenamingLayerId == null && ImportError == null && AdjustmentEditingId == null;

    public bool CanUseHistory => !IsProjectBusy && !IsImporting && brushStroke == null && warpStroke == null && Levels == null
        && RenamingLayerId == null && ImportError == null && TransformEditState == null;
    public bool CanUndo => CanUseHistory && (History.CanUndo || GradientEdit != null);
    public bool CanRedo => CanUseHistory && History.CanRedo;

    public bool CanEditLayers => document != null && brushStroke == null && warpStroke == null && !IsProjectBusy && !IsImporting
        && RenamingLayerId == null && TransformEditState == null && CropRect == null && GradientEdit == null && PixelMove == null
        && HueSaturation == null && Levels == null && FilterEdit == null && AdjustmentEditingId == null;

    public bool TransformsAsGroup => SelectedLayerIds.Count > 1 || (SelectedLayerIds.Count == 1 && ActiveLayer?.IsGroup == true);

    public bool CanTransform
    {
        get
        {
            if (!CanEditLayers) return false;
            if (TransformsAsGroup) return GroupTransformMembers.Count > 0;
            return ActiveLayer is { } layer && layer.Asset != null && !layer.IsGroup && EffectiveVisibleIds.Contains(layer.Id);
        }
    }

    public List<ImageLayer> GroupTransformMembers
    {
        get
        {
            if (!TransformsAsGroup || document == null) return new();
            var parents = document.Layers.ToDictionary(l => l.Id, l => l.ParentId);
            var visible = EffectiveVisibleIds;
            return document.Layers.Where(layer =>
            {
                if (layer.Asset == null || layer.IsGroup || !visible.Contains(layer.Id)) return false;
                Guid? current = layer.Id;
                for (int i = 0; i < 64; i++)
                {
                    if (current is not { } id) return false;
                    if (SelectedLayerIds.Contains(id)) return true;
                    current = parents.GetValueOrDefault(id);
                }
                return false;
            }).ToList();
        }
    }

    public LayerTransform? GroupTransformBox
    {
        get
        {
            var points = GroupTransformMembers.SelectMany(m => m.Transform.Corners()).ToList();
            if (points.Count == 0) return null;
            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            return new LayerTransform(new PointD(minX, minY), new SizeD(Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)));
        }
    }

    // MARK: Hierarchy helpers

    public IReadOnlyList<Format.ProjectLayerRecord> HierarchyRecords => document?.Layers.Select(l => l.HierarchyRecord()).ToList()
        ?? new List<Format.ProjectLayerRecord>();
    public HashSet<Guid> EffectiveVisibleIds => document == null ? new() : document.EffectiveVisibleIds();
    public List<Format.LayerHierarchy.Entry> LayerRows =>
        Format.LayerHierarchy.Entries(HierarchyRecords, topFirst: true, collapsed: CollapsedGroupIds);

    // MARK: Selection of layers and tools

    public void SelectLayer(Guid? id)
    {
        if (brushStroke != null || warpStroke != null || Levels != null) return;
        if (id != ActiveLayerId) { CommitTransform(); ResolveGradient(); }
        ActiveLayerId = id;
    }

    public void SelectTool(NavigationTool value)
    {
        if (IsProjectBusy || brushStroke != null || warpStroke != null || Levels != null) return;
        if (Tool != value) { CommitTransform(); CancelCrop(); ResolveGradient(); CancelLasso(); CancelShape(); }
        int from = TipFamily(Tool), to = TipFamily(value);
        if (from != to && parkedBrushTips.TryGetValue(to, out var parked))
        {
            parkedBrushTips[from] = (brushSettings.Diameter, brushSettings.Hardness, brushSettings.Opacity);
            BrushSettings = brushSettings with { Diameter = parked.Diameter, Hardness = parked.Hardness, Opacity = parked.Opacity };
        }
        Tool = value;
        if (value == NavigationTool.Crop && CropRect == null && document != null)
        {
            CropRatioChoice = "Free";
            CropRect = new RectD(0, 0, document.Width, document.Height);
        }
        InvalidateCanvas();
    }

    // MARK: Transforms

    public void BeginTransform(bool persistent = true)
    {
        CancelCrop();
        if (TransformEditState != null || !CanTransform || ActiveLayer is not { } layer) return;
        Tool = NavigationTool.Move;
        if (TransformsAsGroup)
        {
            var members = GroupTransformMembers;
            if (GroupTransformBox is not { } box) return;
            TransformEditState = new TransformEdit
            {
                LayerId = layer.Id, Draft = box, Persistent = persistent,
                Group = new TransformGroup(box, members.ToDictionary(m => m.Id, m => m.Transform)),
            };
            InvalidateCanvas();
            return;
        }
        bool maskAlone = IsMaskSelected && layer.Mask?.IsLinked == false;
        TransformEditState = new TransformEdit { LayerId = layer.Id, Draft = maskAlone ? layer.MaskTransform : layer.Transform, Persistent = persistent, Mask = maskAlone };
        InvalidateCanvas();
    }

    public void PreviewTransform(LayerTransform value)
    {
        if (!value.IsValid || TransformEditState == null) return;
        TransformEditState = TransformEditState with { Draft = value };
        InvalidateCanvas();
    }

    public void BeginDuplicateTransform()
    {
        if (transformDuplicate != null || TransformsAsGroup || ActiveLayerId is not { } source) return;
        CommitTransform();
        if (!CanTransform) return;
        BeginEdit("Duplicate Layer");
        DuplicateActiveLayer();
        if (ActiveLayerId is not { } copy || copy == source) { EndEdit(); return; }
        transformDuplicate = (copy, source);
        BeginTransform(persistent: false);
    }

    public void CommitTransform()
    {
        SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
        BlendPreview = null;
        FinishOpacityEdit();
        if (TransformEditState is not { } edit) return;
        try
        {
            TransformEditState = null;
            if (edit.Floating is { } floating)
            {
                if (edit.Draft == floating.Original && edit.Corners == null) CancelFloatingTransform(floating);
                else MergeFloatingTransform(edit, floating);
                return;
            }
            if (edit.Mask) { CommitMaskTransform(edit); return; }
            if (edit.Corners is { } corners) { CommitDistort(edit, corners); return; }
            if (edit.Group is { } group)
            {
                if (!edit.Draft.IsValid) return;
                BeginEdit("Transform Layers");
                foreach (var (id, original) in group.Originals)
                {
                    int index = document!.IndexOf(id);
                    if (index < 0) continue;
                    var moved = original.Following(group.Box, edit.Draft);
                    if (!moved.IsValid) continue;
                    var layer = document.Layers[index];
                    var mask = layer.Mask is { } m ? m with { Placement = m.PlacementMovingLayer(original, moved) } : null;
                    ReplaceLayer(index, layer with { Transform = moved, Mask = mask });
                    RedrawShape(index);
                }
                EndEdit();
                return;
            }
            int layerIndex = document?.IndexOf(edit.LayerId) ?? -1;
            if (!edit.Draft.IsValid || layerIndex < 0) return;
            BeginEdit("Transform Layer");
            var current = document!.Layers[layerIndex];
            var newMask = current.Mask is { } owned ? owned with { Placement = owned.PlacementMovingLayer(current.Transform, edit.Draft) } : null;
            ReplaceLayer(layerIndex, current with { Transform = edit.Draft, Mask = newMask });
            RedrawShape(layerIndex);
            EndEdit();
        }
        finally
        {
            if (transformDuplicate != null) { transformDuplicate = null; EndEdit(); }
            InvalidateCanvas();
        }
    }

    public void CancelTransform()
    {
        SnapGuides = (Array.Empty<double>(), Array.Empty<double>());
        if (TransformEditState is not { } edit) return;
        TransformEditState = null;
        if (transformDuplicate is { } duplicate)
        {
            Document = document! with { Layers = document.Layers.RemoveAll(l => l.Id == duplicate.Copy) };
            ActiveLayerId = duplicate.Source;
            transformDuplicate = null;
            EndEdit();
        }
        if (edit.Floating is { } floating) CancelFloatingTransform(floating);
        InvalidateCanvas();
    }

    /// <summary>Pixels the transform places — what 100% scale draws 1:1. Null for a layer without pixels.</summary>
    public SizeD? TransformPixelSize
    {
        get
        {
            if (TransformEditState?.Group is { } group) return group.Box.Size;
            if (TransformEditState == null && TransformsAsGroup) return GroupTransformBox?.Size;
            if (TransformTargetsMask) return null;
            if (TransformEditState?.Floating is { } floating) return floating.PixelSize;
            return ActiveLayer?.Asset is { } asset ? new SizeD(asset.Image.Width, asset.Image.Height) : null;
        }
    }

    public LayerTransform DisplayedTransform(ImageLayer layer)
    {
        if (PendingTransform(layer) is { } pending) return pending;
        if (FilterEdit is { GrownTransform: { } grown } edit && edit.PreviewImage(layer.Id) != null) return grown;
        return layer.Transform;
    }

    public bool TransformTargetsMask => TransformEditState?.Mask ?? (IsMaskSelected && ActiveLayer?.Mask?.IsLinked == false);

    public LayerTransform EditedTransform(ImageLayer layer)
    {
        if (TransformEditState is { } edit && edit.LayerId == layer.Id) return edit.Draft;
        if (TransformEditState == null && layer.Id == ActiveLayerId && TransformsAsGroup && GroupTransformBox is { } box) return box;
        return layer.Id == ActiveLayerId && TransformTargetsMask ? layer.MaskTransform : layer.Transform;
    }

    public LayerTransform? PendingTransform(ImageLayer layer)
    {
        if (TransformEditState is not { } edit || edit.Mask) return null;
        if (edit.Group is { } group) return group.Originals.TryGetValue(layer.Id, out var original) ? original.Following(group.Box, edit.Draft) : null;
        return edit.LayerId == layer.Id ? edit.Draft : null;
    }

    public void NudgeLayer(double dx, double dy)
    {
        bool alreadyEditing = TransformEditState != null;
        if (!alreadyEditing) BeginTransform(persistent: false);
        if (TransformEditState?.Draft is not { } value) return;
        PreviewTransform(value with { Origin = value.Origin.Offset(dx, dy) });
        if (TransformEditState?.Corners is { } corners) PreviewCorners(corners.Select(c => c.Offset(dx, dy)).ToArray());
        if (!alreadyEditing) CommitTransform();
    }

    // MARK: History

    public void Undo()
    {
        if (GradientEdit != null) { CancelGradient(); return; }
        if (!CanUndo || History.Undo() is not { } snapshot) return;
        Restore(snapshot);
    }

    public void Redo()
    {
        if (!CanRedo || History.Redo() is not { } snapshot) return;
        Restore(snapshot);
    }

    /// <summary>Photoshop's History panel: undo or redo until <paramref name="undoCount"/> steps remain.</summary>
    public void JumpToHistory(int undoCount)
    {
        while (History.UndoCount > undoCount && CanUndo) Undo();
        while (History.UndoCount < undoCount && CanRedo) Redo();
    }

    private void Restore(DocumentHistory.Snapshot snapshot)
    {
        CancelCrop();
        CancelGradient();
        bool changedCanvas = document?.Id != snapshot.Document?.Id;
        bool keepMaskTarget = IsMaskSelected && ActiveLayerId == snapshot.ActiveLayerId;
        Document = snapshot.Document;
        ActiveLayerId = snapshot.ActiveLayerId;
        IsMaskSelected = keepMaskTarget && ActiveLayer?.Mask != null;
        if (changedCanvas && document != null) Viewport.Fit(document.Size);
        InvalidateCanvas();
    }

    /// <summary>Nestable transaction boundary; a gesture groups into one undo step.</summary>
    public void BeginEdit(string name) => History.Begin(name, document, ActiveLayerId);
    public void EndEdit() { History.End(document, ActiveLayerId); InvalidateCanvas(); }

    internal void ReplaceLayer(int index, ImageLayer layer) => Document = document! with { Layers = document.Layers.SetItem(index, layer) };
    internal void UpdateLayer(Guid id, Func<ImageLayer, ImageLayer> change)
    {
        int index = document?.IndexOf(id) ?? -1;
        if (index >= 0) ReplaceLayer(index, change(document!.Layers[index]));
    }
    internal void SetLayers(ImmutableList<ImageLayer> layers) => Document = document! with { Layers = layers };
    internal void SetLayers(IEnumerable<ImageLayer> layers) => Document = document! with { Layers = layers.ToImmutableList() };

    // MARK: Layers

    public string NextLayerName()
    {
        var names = new HashSet<string>(document?.Layers.Select(l => l.Name) ?? Enumerable.Empty<string>());
        int number = 1;
        while (names.Contains($"Layer {number}")) number++;
        return $"Layer {number}";
    }

    public void AddBlankLayer()
    {
        if (!CanEditLayers || document == null) return;
        var layer = ImageLayer.Blank(NextLayerName(), document.Size) with
        {
            ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId,
        };
        if (layer.ParentId is { } parent) CollapsedGroupIds.Remove(parent);
        int insertion = ActiveLayerId is { } active && document.IndexOf(active) is var ai && ai >= 0 ? ai + 1 : document.Layers.Count;
        if (ActiveLayer?.IsGroup == true && ActiveLayerId is { } folder)
        {
            var parents = document.Layers.ToDictionary(l => l.Id, l => l.ParentId);
            bool IsInside(Guid id)
            {
                var parentId = parents.GetValueOrDefault(id);
                for (int steps = 0; parentId is { } current && steps < 64; steps++)
                {
                    if (current == folder) return true;
                    parentId = parents.GetValueOrDefault(current);
                }
                return false;
            }
            int topmost = document.Layers.FindLastIndex(l => IsInside(l.Id));
            if (topmost >= 0) insertion = Math.Max(insertion, topmost + 1);
        }
        BeginEdit("New Blank Layer");
        SetLayers(document.Layers.Insert(insertion, layer));
        ActiveLayerId = layer.Id;
        EndEdit();
    }

    public async Task DeleteLayer(Guid id)
    {
        if (!CanEditLayers || document?.IndexOf(id) < 0) return;
        if (await DeleteWithLiveMaskChoice(new[] { id })) return;
        FinishDeletingLayer(id, new());
    }

    public Task DeleteActiveLayer() => ActiveLayerId is { } id ? DeleteLayer(id) : Task.CompletedTask;

    public async Task DeleteSelectedLayers()
    {
        if (!CanEditLayers || document == null) return;
        var ids = document.Layers.Select(l => l.Id).Where(SelectedLayerIds.Contains).ToList();
        if (ids.Count <= 1) { await DeleteActiveLayer(); return; }
        if (await DeleteWithLiveMaskChoice(ids)) return;
        FinishDeletingLayers(ids, new());
    }

    public void RenameLayer(Guid id, string name)
    {
        name = name.Trim();
        if (IsProjectBusy || IsImporting || name.Length == 0 || document?.IndexOf(id) is not (>= 0 and var index)) return;
        BeginEdit("Rename Layer");
        ReplaceLayer(index, document.Layers[index] with { Name = name });
        EndEdit();
    }

    public void ToggleLayerVisibility(Guid id)
    {
        if (!CanEditLayers || document?.IndexOf(id) is not (>= 0 and var index)) return;
        var layer = document.Layers[index];
        BeginEdit(layer.IsVisible ? "Hide Layer" : "Show Layer");
        ReplaceLayer(index, layer with { IsVisible = !layer.IsVisible });
        EndEdit();
    }

    /// <summary>Photoshop's eye swipe: one undo step from the press until the button comes up.</summary>
    public bool? BeginVisibilitySwipe(Guid id)
    {
        if (!CanEditLayers || document?.Layer(id) is not { } layer) return null;
        bool visible = !layer.IsVisible;
        BeginEdit(visible ? "Show Layer" : "Hide Layer");
        SetVisibilityInSwipe(id, visible);
        return visible;
    }

    public void SetVisibilityInSwipe(Guid id, bool visible)
    {
        if (document?.IndexOf(id) is not (>= 0 and var index) || document.Layers[index].IsVisible == visible) return;
        ReplaceLayer(index, document.Layers[index] with { IsVisible = visible });
    }

    public void EndVisibilitySwipe() => EndEdit();

    public bool CanMoveActiveLayer(int offset)
    {
        if (!CanEditLayers || ActiveLayer is not { } active) return false;
        var siblings = document!.Layers.Where(l => l.ParentId == active.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == active.Id);
        return index >= 0 && index + offset >= 0 && index + offset < siblings.Count;
    }

    public void MoveActiveLayer(int offset)
    {
        if (!CanMoveActiveLayer(offset) || ActiveLayer is not { } active) return;
        var layers = document!.Layers;
        var siblings = layers.Where(l => l.ParentId == active.ParentId).ToList();
        int index = siblings.FindIndex(l => l.Id == active.Id);
        int a = document.IndexOf(active.Id), b = document.IndexOf(siblings[index + offset].Id);
        BeginEdit("Reorder Layers");
        SetLayers(layers.SetItem(a, layers[b]).SetItem(b, layers[a]));
        EndEdit();
    }

    // MARK: Documents

    public void Insert(ImageAsset asset, PointD? center = null, bool fitToCanvas = false)
    {
        BeginEdit("Import Image");
        if (document == null)
        {
            Document = new CanvasDocument { Width = asset.Image.Width, Height = asset.Image.Height };
            Viewport.Fit(document!.Size);
        }
        var doc = document!;
        var c = center ?? new PointD(doc.Width / 2.0, doc.Height / 2.0);
        var layer = ImageLayer.FromAsset(asset, new PointD(Math.Floor(c.X - asset.Image.Width / 2.0), Math.Floor(c.Y - asset.Image.Height / 2.0)));
        // Placed images centre on the canvas and scale down (never up) to fit inside it; the pixels stay full size.
        if (fitToCanvas)
        {
            double scale = Math.Min(1, Math.Min(doc.Width / (double)asset.Image.Width, doc.Height / (double)asset.Image.Height));
            var size = new SizeD(Math.Max(1, Math.Round(asset.Image.Width * scale)), Math.Max(1, Math.Round(asset.Image.Height * scale)));
            layer = layer with { Transform = layer.Transform with { Size = size, Origin = new PointD(Math.Floor((doc.Width - size.Width) / 2), Math.Floor((doc.Height - size.Height) / 2)) } };
        }
        layer = layer with
        {
            ParentId = ActiveLayer?.IsGroup == true ? ActiveLayerId : ActiveLayer?.ParentId,
        };
        if (layer.ParentId is { } parent) CollapsedGroupIds.Remove(parent);
        SetLayers(doc.Layers.Add(layer));
        ActiveLayerId = layer.Id;
        EndEdit();
    }

    /// <summary>Imports decoded images as layers, one undo step per request (the app decodes files).</summary>
    public void ImportAssets(IEnumerable<ImageAsset> assets, PointD? point = null, bool fitToCanvas = false)
    {
        BeginEdit("Import Images");
        var at = document == null ? null : point;
        foreach (var asset in assets) Insert(asset, at, fitToCanvas);
        EndEdit();
    }

    public long UsedImagePixels => document?.Layers.Sum(l => l.Asset is { } a ? (long)a.Image.Width * a.Image.Height : 0) ?? 0;

    public void CreateDocument(int width, int height, bool emptyLayer = false)
    {
        if (IsProjectBusy || IsImporting || width < 1 || width > 30_000 || height < 1 || height > 30_000) return;
        CommitTransform();
        BeginEdit("New Canvas");
        var doc = new CanvasDocument { Width = width, Height = height };
        var layer = emptyLayer ? ImageLayer.Blank("Layer 1", doc.Size) : null;
        if (layer != null) doc = doc with { Layers = ImmutableList.Create(layer) };
        Document = doc;
        ActiveLayerId = layer?.Id;
        RenamingLayerId = null;
        Viewport.Fit(doc.Size);
        EndEdit();
    }

    public void Fit()
    {
        if (document != null) { Viewport.Fit(document.Size); InvalidateCanvas(); }
    }

    public void Zoom(double value, PointD? anchor = null)
    {
        if (document == null) return;
        Viewport.SetZoom(value, anchor ?? Viewport.Center, document.Size);
        InvalidateCanvas();
    }

    public void Pan(SizeD delta)
    {
        Viewport.Translate(delta);
        InvalidateCanvas();
    }
}

public sealed record CloneSettings(bool Aligned = true, bool SampleAllLayers = false);

public static class DocumentExtensions
{
    public static Format.ProjectLayerRecord HierarchyRecord(this ImageLayer layer) => new()
    {
        Id = layer.Id, Name = layer.Name, IsVisible = layer.IsVisible, Transform = layer.Transform,
        ImageFile = layer.Asset == null ? null : Format.ProjectLayerRecord.ImageFileName(layer.Id),
        ParentId = layer.ParentId, IsGroup = layer.IsGroup, Opacity = layer.Opacity, BlendMode = layer.BlendMode,
        MaskFile = layer.Mask == null ? null : Format.ProjectLayerRecord.MaskFileName(layer.Id), MaskEnabled = layer.Mask?.IsEnabled,
        MaskSourceId = layer.MaskSourceId, Adjustment = layer.Adjustment, MaskPlacement = layer.Mask?.Placement, MaskLinked = layer.Mask?.IsLinked,
    };

    public static List<Format.LayerHierarchy.Entry> HierarchyEntries(this CanvasDocument document) =>
        Format.LayerHierarchy.Entries(document.Layers.Select(l => l.HierarchyRecord()).ToList());

    public static HashSet<Guid> EffectiveVisibleIds(this CanvasDocument document) =>
        document.HierarchyEntries().Where(e => e.Visible).Select(e => e.Layer.Id).ToHashSet();

    public static List<ImageLayer> RenderLayers(this CanvasDocument document)
    {
        var byId = document.Layers.ToDictionary(l => l.Id);
        return document.HierarchyEntries().Where(e => e.Visible && e.Layer.IsGroup != true).Select(e => byId[e.Layer.Id]).ToList();
    }
}
