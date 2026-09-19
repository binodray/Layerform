using System.Runtime.InteropServices;

namespace Compositor.App.Platform;

/// <summary>The PC's graphics adapters in DXGI order — the order DirectML's device index uses — so Remove Background can run
/// on the dedicated GPU of a laptop that also has integrated graphics.</summary>
public static class GpuAdapters
{
    public sealed record Adapter(int Index, string Name, ulong DedicatedBytes, bool Software);

    public static IReadOnlyList<Adapter> List()
    {
        var adapters = new List<Adapter>();
        try
        {
            if (CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factory) != 0) return adapters;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        adapter.GetDesc1(out var desc);
                        adapters.Add(new Adapter((int)i, desc.Description, (ulong)desc.DedicatedVideoMemory, (desc.Flags & 2) != 0));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception e) { Diagnostics.Log("GPU list: " + e.Message); }
        return adapters;
    }

    /// <summary>Hardware adapters, the one with the most dedicated memory first.</summary>
    public static IReadOnlyList<Adapter> Preferred() => List().Where(a => !a.Software).OrderByDescending(a => a.DedicatedBytes).ToList();

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIFactory
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
