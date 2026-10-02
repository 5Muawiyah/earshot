using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The few DXGI calls the frame clock needs to wait for a display's vertical blank: a factory, its adapters, their outputs,
// each output's monitor handle, and IDXGIOutput::WaitForVBlank. Nothing here draws, makes a device or changes a mode.
//
// Classic COM interop does not flatten inherited interfaces, so each interface below repeats its bases' methods in vtable
// order (IUnknown is implied by InterfaceIsIUnknown): IDXGIObject's four, then the interface's own, up to the last one used.
// The order is the SDK's (shared\dxgi.h). Every method keeps its HRESULT ([PreserveSig]); a failure is recorded, not thrown.
// https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nn-dxgi-idxgiobject
internal static partial class Dxgi
{
    private const string Dll = "dxgi.dll";

    // DXGI_ERROR_NOT_FOUND: EnumAdapters or EnumOutputs was asked for an index past the last one.
    // https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-error
    internal const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    // https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-createdxgifactory1
    [LibraryImport(Dll)]
    internal static partial int CreateDXGIFactory1(in Guid riid, out nint ppFactory);

    internal static int CreateFactory(out IDXGIFactory1? factory)
    {
        Guid iid = typeof(IDXGIFactory1).GUID;
        int hr = CreateDXGIFactory1(in iid, out nint pointer);
        return ComActivation.TakeInterface(hr, pointer, out factory);
    }
}

// https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nn-dxgi-idxgifactory1
[ComImport]
[Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIFactory1
{
    // IDXGIObject
    [PreserveSig] int SetPrivateData(in Guid name, uint dataSize, nint data);
    [PreserveSig] int SetPrivateDataInterface(in Guid name, nint unknown);
    [PreserveSig] int GetPrivateData(in Guid name, ref uint dataSize, nint data);
    [PreserveSig] int GetParent(in Guid riid, out nint parent);

    // IDXGIFactory
    // https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgifactory-enumadapters
    [PreserveSig] int EnumAdapters(uint adapter, out nint ppAdapter);
}

// https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nn-dxgi-idxgiadapter
[ComImport]
[Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter
{
    // IDXGIObject
    [PreserveSig] int SetPrivateData(in Guid name, uint dataSize, nint data);
    [PreserveSig] int SetPrivateDataInterface(in Guid name, nint unknown);
    [PreserveSig] int GetPrivateData(in Guid name, ref uint dataSize, nint data);
    [PreserveSig] int GetParent(in Guid riid, out nint parent);

    // https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiadapter-enumoutputs
    [PreserveSig] int EnumOutputs(uint output, out nint ppOutput);
}

// https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nn-dxgi-idxgioutput
[ComImport]
[Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput
{
    // IDXGIObject
    [PreserveSig] int SetPrivateData(in Guid name, uint dataSize, nint data);
    [PreserveSig] int SetPrivateDataInterface(in Guid name, nint unknown);
    [PreserveSig] int GetPrivateData(in Guid name, ref uint dataSize, nint data);
    [PreserveSig] int GetParent(in Guid riid, out nint parent);

    // https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-getdesc
    [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);

    // Not used; declared for the vtable order.
    [PreserveSig] int GetDisplayModeList(uint enumFormat, uint flags, ref uint numModes, nint desc);
    [PreserveSig] int FindClosestMatchingMode(nint modeToMatch, nint closestMatch, nint concernedDevice);

    // Halts the calling thread until the next vertical blank of this output.
    // https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-waitforvblank
    [PreserveSig] int WaitForVBlank();
}

// https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_output_desc
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_OUTPUT_DESC
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int AttachedToDesktop;
    public int Rotation;
    public nint Monitor;
}
