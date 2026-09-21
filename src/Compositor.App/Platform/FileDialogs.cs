using System.Runtime.InteropServices;

namespace Compositor.App.Platform;

/// <summary>The system Common Item Dialog (IFileOpenDialog / IFileSaveDialog). It returns paths without creating files,
/// and Open Project picks a project package folder (`.lform`, or the Mac app's `.comp`) directly.</summary>
public static class FileDialogs
{
    private static string? lastFolder;
    private static uint lastTypeIndex = 1;

    public static string? PickProjectFolder(IntPtr owner) =>
        Show(owner, open: true, "Open Project", Array.Empty<(string, string)>(), null, null, FOS.PICKFOLDERS | FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST).FirstOrDefault();

    public static string? PickFolder(IntPtr owner, string title) =>
        Show(owner, open: true, title, Array.Empty<(string, string)>(), null, null, FOS.PICKFOLDERS | FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST).FirstOrDefault();

    public static string[] PickImages(IntPtr owner, string title = "Import Images") =>
        Show(owner, open: true, title,
            new[] { ("Images", "*.jpg;*.jpeg;*.png;*.svg;*.heic;*.heif;*.tif;*.tiff;*.bmp;*.gif;*.ico;*.webp;*.avif;*.jxr;*.wdp;*.hdp"), ("All files", "*.*") }, null, null,
            FOS.ALLOWMULTISELECT | FOS.FORCEFILESYSTEM | FOS.FILEMUSTEXIST);

    /// <summary>Open Project: choose the `.lform` or `.comp` folder.</summary>
    public static string? PickProjectManifest(IntPtr owner) => PickProjectFolder(owner);

    public static string? SavePath(IntPtr owner, string title, string filter, string defaultExtension, string fileName)
    {
        var parts = filter.Split('|');
        var types = new List<(string, string)>();
        for (int i = 0; i + 1 < parts.Length; i += 2) types.Add((parts[i], parts[i + 1]));
        // A project is a folder; saving over an existing project replaces it atomically, as Save does on the Mac.
        bool project = defaultExtension is "lform" or "comp";
        var options = FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST | (project ? FOS.NOVALIDATE : FOS.OVERWRITEPROMPT);
        var path = Show(owner, open: false, title, types, fileName, defaultExtension, options).FirstOrDefault();
        if (path == null) return null;
        // Keep an extension any of the offered types allows; otherwise add the chosen type's.
        var extensions = types.Select(t => t.Item2.Split(';').Select(s => s.Trim().TrimStart('*', '.')).ToArray()).ToList();
        if (extensions.SelectMany(e => e).Any(e => e.Length > 0 && e != "*" && path.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))) return path;
        int chosen = Math.Clamp((int)lastTypeIndex - 1, 0, Math.Max(0, extensions.Count - 1));
        string extension = extensions.Count > 0 && extensions[chosen].FirstOrDefault(e => e.Length > 0 && e != "*") is { } picked ? picked : defaultExtension;
        return path + "." + extension;
    }

    private static string[] Show(IntPtr owner, bool open, string title, IReadOnlyList<(string Name, string Spec)> types, string? fileName,
        string? defaultExtension, FOS options)
    {
        IFileDialog dialog = open ? (IFileDialog)new FileOpenDialogRCW() : (IFileDialog)new FileSaveDialogRCW();
        try
        {
            dialog.GetOptions(out var existing);
            dialog.SetOptions(existing | options);
            dialog.SetTitle(title);
            if (types.Count > 0)
            {
                var specs = types.Select(t => new COMDLG_FILTERSPEC { pszName = t.Name, pszSpec = t.Spec }).ToArray();
                dialog.SetFileTypes((uint)specs.Length, specs);
                dialog.SetFileTypeIndex(1);
            }
            if (defaultExtension != null) dialog.SetDefaultExtension(defaultExtension);
            if (fileName != null) dialog.SetFileName(fileName);
            string folder = lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (Directory.Exists(folder) && SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out var folderItem) == 0)
                dialog.SetFolder(folderItem);
            if (dialog.Show(owner) != 0) return Array.Empty<string>();
            if (types.Count > 0) { dialog.GetFileTypeIndex(out var index); lastTypeIndex = index; }
            var paths = new List<string>();
            if (open && (options & FOS.ALLOWMULTISELECT) != 0)
            {
                ((IFileOpenDialog)dialog).GetResults(out var items);
                items.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    items.GetItemAt(i, out var item);
                    if (ItemPath(item) is { } p) paths.Add(p);
                }
            }
            else
            {
                dialog.GetResult(out var item);
                if (ItemPath(item) is { } p) paths.Add(p);
            }
            if (paths.Count > 0) lastFolder = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
            return paths.ToArray();
        }
        catch (COMException e)
        {
            Diagnostics.Log("File dialog: " + e.Message);
            return Array.Empty<string>();
        }
        finally { Marshal.FinalReleaseComObject(dialog); }
    }

    private static string? ItemPath(IShellItem item)
    {
        item.GetDisplayName(SIGDN.FILESYSPATH, out var pointer);
        try { return Marshal.PtrToStringUni(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    // MARK: Interop

    [Flags]
    private enum FOS : uint
    {
        OVERWRITEPROMPT = 0x2, PICKFOLDERS = 0x20, FORCEFILESYSTEM = 0x40, NOVALIDATE = 0x100, ALLOWMULTISELECT = 0x200,
        PATHMUSTEXIST = 0x800, FILEMUSTEXIST = 0x1000,
    }

    private enum SIGDN : uint { FILESYSPATH = 0x80058000 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogRCW { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FOS options);
        void GetOptions(out FOS options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] specs);
        new void SetFileTypeIndex(uint index);
        new void GetFileTypeIndex(out uint index);
        new void Advise(IntPtr events, out uint cookie);
        new void Unadvise(uint cookie);
        new void SetOptions(FOS options);
        new void GetOptions(out FOS options);
        new void SetDefaultFolder(IShellItem item);
        new void SetFolder(IShellItem item);
        new void GetFolder(out IShellItem item);
        new void GetCurrentSelection(out IShellItem item);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        new void GetResult(out IShellItem item);
        new void AddPlace(IShellItem item, int placement);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr filter);
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(SIGDN name, out IntPtr display);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr store);
        void GetPropertyDescriptionList(IntPtr key, ref Guid riid, out IntPtr list);
        void GetAttributes(int flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr items);
    }
}
