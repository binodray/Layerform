using Compositor.Model;

namespace Compositor.Editing;

/// <summary>
/// Undo history of immutable document snapshots (DocumentHistory.swift). Snapshots share images and never copy pixels;
/// begin/end nest so a whole gesture is one step, and no-op edits keep the redo history.
/// </summary>
public sealed class DocumentHistory
{
    public sealed record Snapshot(CanvasDocument? Document, Guid? ActiveLayerId, Guid Revision);
    private sealed record Entry(string Name, Snapshot Before, Snapshot After);

    private readonly List<Entry> past = new();
    private readonly List<Entry> future = new();
    private Guid revision = Guid.NewGuid();
    private Guid savedRevision;
    private Snapshot? pending;
    private string pendingName = "Edit";
    private int depth;
    public int EntryLimit { get; }
    public long RetainedByteLimit { get; }
    public event Action? Changed;

    public DocumentHistory(int entryLimit = 100, long retainedByteLimit = 256L * 1024 * 1024)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        savedRevision = revision;
    }

    public bool CanUndo => depth == 0 && past.Count > 0;
    public bool CanRedo => depth == 0 && future.Count > 0;
    public string UndoName => past.Count > 0 ? past[^1].Name : "";
    public string RedoName => future.Count > 0 ? future[^1].Name : "";
    public bool IsModified => revision != savedRevision;
    public int UndoCount => past.Count;
    /// <summary>Step names, oldest first: what Undo would take back, then what Redo would bring back (nearest first).</summary>
    public IReadOnlyList<string> PastNames => past.Select(e => e.Name).ToList();
    public IReadOnlyList<string> FutureNames => future.AsEnumerable().Reverse().Select(e => e.Name).ToList();
    public int Depth => depth;

    public void MarkSaved() { savedRevision = revision; Changed?.Invoke(); }

    public void Reset()
    {
        past.Clear();
        future.Clear();
        pending = null;
        depth = 0;
        revision = Guid.NewGuid();
        savedRevision = revision;
        Changed?.Invoke();
    }

    public void Begin(string name, CanvasDocument? document, Guid? selection)
    {
        if (depth == 0)
        {
            pending = new Snapshot(document, selection, revision);
            pendingName = name;
        }
        depth++;
    }

    public void End(CanvasDocument? document, Guid? selection)
    {
        if (depth <= 0) return;
        depth--;
        if (depth != 0 || pending is not { } before) return;
        pending = null;
        // Selecting, navigating and no-op edits preserve redo history.
        if (Equals(before.Document, document)) return;
        revision = Guid.NewGuid();
        past.Add(new Entry(pendingName, before, new Snapshot(document, selection, revision)));
        future.Clear();
        Trim(document);
        Changed?.Invoke();
    }

    public Snapshot? Undo()
    {
        if (!CanUndo) return null;
        var entry = past[^1];
        past.RemoveAt(past.Count - 1);
        future.Add(entry);
        revision = entry.Before.Revision;
        Trim(entry.Before.Document);
        Changed?.Invoke();
        return entry.Before;
    }

    public Snapshot? Redo()
    {
        if (!CanRedo) return null;
        var entry = future[^1];
        future.RemoveAt(future.Count - 1);
        past.Add(entry);
        revision = entry.After.Revision;
        Trim(entry.After.Document);
        Changed?.Invoke();
        return entry.After;
    }

    /// <summary>Bytes retained only by history, excluding images in the live document.</summary>
    public long RetainedBytes(CanvasDocument? current)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Mark(ImageLayer layer)
        {
            if (layer.Asset is { } a) { seen.Add(a.Image); seen.Add(a.Thumbnail); }
            if (layer.Mask?.Asset is { } m) { seen.Add(m.Image); seen.Add(m.Thumbnail); }
        }
        foreach (var layer in current?.Layers ?? System.Collections.Immutable.ImmutableList<ImageLayer>.Empty) Mark(layer);
        long bytes = 0;
        foreach (var entry in past.Concat(future))
            foreach (var snapshot in new[] { entry.Before, entry.After })
                foreach (var layer in snapshot.Document?.Layers ?? System.Collections.Immutable.ImmutableList<ImageLayer>.Empty)
                {
                    if (layer.Asset is { } a)
                    {
                        if (seen.Add(a.Image)) bytes += (long)a.Image.Width * a.Image.Height * 4;
                        if (seen.Add(a.Thumbnail)) bytes += (long)a.Thumbnail.Width * a.Thumbnail.Height * 4;
                    }
                    if (layer.Mask?.Asset is { } m)
                    {
                        if (seen.Add(m.Image)) bytes += (long)m.Image.Width * m.Image.Height;
                        if (seen.Add(m.Thumbnail)) bytes += (long)m.Thumbnail.Width * m.Thumbnail.Height;
                    }
                }
        return bytes;
    }

    private void Trim(CanvasDocument? current)
    {
        while (past.Count + future.Count > EntryLimit || RetainedBytes(current) > RetainedByteLimit)
        {
            if (past.Count > 0) past.RemoveAt(0);
            else if (future.Count > 0) future.RemoveAt(0);
            else break;
        }
    }
}
