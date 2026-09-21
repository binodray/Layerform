using Compositor.Geometry;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    public void CreateSliceGrid(int rows, int columns)
    {
        if (document == null) return;
        SliceRows = Math.Clamp(rows, 1, 100);
        SliceColumns = Math.Clamp(columns, 1, 100);
        Slices.Clear();
        for (int row = 0; row < SliceRows; row++)
            for (int column = 0; column < SliceColumns; column++)
            {
                double x0 = document.Width * column / (double)SliceColumns;
                double x1 = document.Width * (column + 1) / (double)SliceColumns;
                double y0 = document.Height * row / (double)SliceRows;
                double y1 = document.Height * (row + 1) / (double)SliceRows;
                Slices.Add(new RectD(x0, y0, x1 - x0, y1 - y0));
            }
        InvalidateCanvas();
        Notify();
    }

    public void CommitSlice(RectD rect)
    {
        if (document == null) return;
        double x0 = Math.Clamp(rect.MinX, 0, document.Width);
        double y0 = Math.Clamp(rect.MinY, 0, document.Height);
        double x1 = Math.Clamp(rect.MaxX, 0, document.Width);
        double y1 = Math.Clamp(rect.MaxY, 0, document.Height);
        if (x1 - x0 >= 1 && y1 - y0 >= 1) Slices.Add(new RectD(x0, y0, x1 - x0, y1 - y0));
        SliceDraft = null;
        InvalidateCanvas();
        Notify();
    }

    public void ClearSlices()
    {
        Slices.Clear();
        SliceDraft = null;
        InvalidateCanvas();
        Notify();
    }
}
