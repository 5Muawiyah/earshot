using System.Runtime.InteropServices;

namespace Earshot.Interop;

// PROPVARIANT as Earshot reads it: the VARTYPE tag at offset 0, three reserved words, and the value
// union at offset 8. Only the union members Earshot reads are declared. The BLOB member { ULONG; pointer }
// makes the union 16 bytes, which gives the native size of 24 bytes on x64 (asserted at startup).
// https://learn.microsoft.com/en-us/windows/win32/api/propidlbase/ns-propidlbase-propvariant
[StructLayout(LayoutKind.Explicit)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(2)] public ushort wReserved1;
    [FieldOffset(4)] public ushort wReserved2;
    [FieldOffset(6)] public ushort wReserved3;

    // pwszVal for VT_LPWSTR, puuid for VT_CLSID.
    [FieldOffset(8)] public nint pointerValue;

    // ulVal for VT_UI4.
    [FieldOffset(8)] public uint ulVal;

    // boolVal for VT_BOOL (VARIANT_BOOL: 0 false, -1 true).
    [FieldOffset(8)] public short boolVal;

    // blob for VT_BLOB.
    [FieldOffset(8)] public BLOB blob;
}

// https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-blob
[StructLayout(LayoutKind.Sequential)]
internal struct BLOB
{
    public uint cbSize;
    public nint pBlobData;
}

// The outcome of reading one property through IPropertyStore::GetValue.
//   Hr        the GetValue HRESULT (0xE000020B is seen on some NOTPRESENT endpoints).
//   VarType   the VARTYPE the store returned; VT_EMPTY when the property is absent.
//   HasValue  true only when GetValue succeeded and the value had the expected type.
//   ClearHr   the PropVariantClear HRESULT; S_OK in normal use.
internal readonly record struct PropertyRead<T>(int Hr, ushort VarType, bool HasValue, T? Value, int ClearHr);

internal static partial class PropVariantInterop
{
    private const string Ole32 = "ole32.dll";

    // VARTYPE values (wtypes.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/wtypes/ne-wtypes-varenum
    internal const ushort VT_EMPTY = 0;
    internal const ushort VT_NULL = 1;
    internal const ushort VT_BOOL = 11;
    internal const ushort VT_UI4 = 19;
    internal const ushort VT_LPWSTR = 31;
    internal const ushort VT_BLOB = 65;
    internal const ushort VT_CLSID = 72;

    // Frees whatever the PROPVARIANT points to and zeroes it (vt becomes VT_EMPTY). Every
    // IPropertyStore::GetValue result must pass through here. Do not use it to initialise.
    // https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-propvariantclear
    [LibraryImport(Ole32)]
    internal static partial int PropVariantClear(ref PROPVARIANT pvar);

    // Reads a VT_LPWSTR property and always clears the PROPVARIANT, whatever happens.
    internal static PropertyRead<string> ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        ArgumentNullException.ThrowIfNull(store);
        PROPVARIANT pv = default;
        int hr = 0;
        ushort varType = VT_EMPTY;
        string? value = null;
        int clearHr;
        try
        {
            hr = store.GetValue(ref key, out pv);
            if (hr >= 0)
            {
                varType = pv.vt;
                if (varType == VT_LPWSTR && pv.pointerValue != 0)
                {
                    value = Marshal.PtrToStringUni(pv.pointerValue);
                }
            }
        }
        finally
        {
            clearHr = PropVariantClear(ref pv);
        }

        return new PropertyRead<string>(hr, varType, value is not null, value, clearHr);
    }

    // Reads a VT_CLSID property (puuid points to the GUID) and always clears the PROPVARIANT.
    // PKEY_Device_ContainerId arrives this way through an endpoint property store (seen on the owner's PC).
    internal static PropertyRead<Guid> ReadGuid(IPropertyStore store, PROPERTYKEY key)
    {
        ArgumentNullException.ThrowIfNull(store);
        PROPVARIANT pv = default;
        int hr = 0;
        ushort varType = VT_EMPTY;
        bool hasValue = false;
        Guid value = Guid.Empty;
        int clearHr;
        try
        {
            hr = store.GetValue(ref key, out pv);
            if (hr >= 0)
            {
                varType = pv.vt;
                if (varType == VT_CLSID && pv.pointerValue != 0)
                {
                    value = Marshal.PtrToStructure<Guid>(pv.pointerValue);
                    hasValue = true;
                }
            }
        }
        finally
        {
            clearHr = PropVariantClear(ref pv);
        }

        return new PropertyRead<Guid>(hr, varType, hasValue, value, clearHr);
    }

    // Reads a VT_UI4 property and always clears the PROPVARIANT.
    internal static PropertyRead<uint> ReadUInt32(IPropertyStore store, PROPERTYKEY key)
    {
        ArgumentNullException.ThrowIfNull(store);
        PROPVARIANT pv = default;
        int hr = 0;
        ushort varType = VT_EMPTY;
        bool hasValue = false;
        uint value = 0;
        int clearHr;
        try
        {
            hr = store.GetValue(ref key, out pv);
            if (hr >= 0)
            {
                varType = pv.vt;
                if (varType == VT_UI4)
                {
                    value = pv.ulVal;
                    hasValue = true;
                }
            }
        }
        finally
        {
            clearHr = PropVariantClear(ref pv);
        }

        return new PropertyRead<uint>(hr, varType, hasValue, value, clearHr);
    }
}
