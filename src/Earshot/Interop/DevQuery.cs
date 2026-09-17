using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Device query (devquery.h), the synchronous object query the Windows device information APIs are built on.
// Earshot uses it read-only, for diag battery-sweep only: the paired Bluetooth association endpoints (AEPs) and
// their containers, with every property, to look for a battery value next to the device nodes. Values from the
// Windows SDK headers devquerydef.h, devfiltertypes.h, devpropdef.h and propkey.h.
// https://learn.microsoft.com/en-us/windows/win32/api/devquery/nf-devquery-devgetobjects
// https://learn.microsoft.com/en-us/windows/win32/api/devquery/nf-devquery-devfreeobjects
// https://learn.microsoft.com/en-us/windows/win32/api/devquerydef/ne-devquerydef-dev_object_type
internal static unsafe partial class DevQuery
{
    private const string Dll = "cfgmgr32.dll";

    // DEV_OBJECT_TYPE (devquerydef.h), numbered from DevObjectTypeUnknown = 0.
    internal const int DevObjectTypeAEP = 5;
    internal const int DevObjectTypeAEPContainer = 6;

    // DEV_QUERY_FLAGS (devquerydef.h).
    internal const uint DevQueryFlagNone = 0x0;
    internal const uint DevQueryFlagAllProperties = 0x2;

    // DEVPROP_OPERATOR (devfiltertypes.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/devfiltertypes/ns-devfiltertypes-devprop_filter_expression
    internal const uint DEVPROP_OPERATOR_EQUALS = 0x00000002;

    // DEVPROPSTORE (devpropdef.h).
    internal const uint DEVPROP_STORE_SYSTEM = 0;

    // DEVPROP_TYPE_BYTE and the DEVPROP_MASK_TYPE range for the base type (devpropdef.h), next to the ones in
    // CfgMgr32.
    internal const uint DEVPROP_TYPE_SBYTE = 0x00000002;
    internal const uint DEVPROP_TYPE_BYTE = 0x00000003;
    internal const uint DEVPROP_TYPE_INT16 = 0x00000004;
    internal const uint DEVPROP_TYPE_UINT16 = 0x00000005;
    internal const uint DEVPROP_TYPE_INT64 = 0x00000008;
    internal const uint DEVPROP_TYPE_UINT64 = 0x00000009;
    internal const uint DEVPROP_MASK_TYPE = 0x00000FFF;

    // System.Devices.Aep.IsPaired, System.Devices.Aep.IsConnected and System.Devices.AepContainer.IsPaired
    // (propkey.h). System.ItemNameDisplay is DEVPKEY_NAME in CfgMgr32.
    // https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/device-information-properties
    internal static readonly DEVPROPKEY PKEY_Devices_Aep_IsPaired = new(new Guid("A35996AB-11CF-4935-8B61-A6761081ECDF"), 16);
    internal static readonly DEVPROPKEY PKEY_Devices_Aep_IsConnected = new(new Guid("A35996AB-11CF-4935-8B61-A6761081ECDF"), 7);
    internal static readonly DEVPROPKEY PKEY_Devices_AepContainer_IsPaired = new(new Guid("0BBA1EDE-7566-4F47-90EC-25FC567CED2A"), 4);

    // Returns the HRESULT. On success ppObjects holds pcObjectCount objects to free with DevFreeObjects.
    [LibraryImport(Dll)]
    internal static partial int DevGetObjects(
        int objectType, uint queryFlags, uint cRequestedProperties, DEVPROPCOMPKEY* pRequestedProperties,
        uint cFilterExpressionCount, DEVPROP_FILTER_EXPRESSION* pFilter, uint* pcObjectCount, DEV_OBJECT** ppObjects);

    [LibraryImport(Dll)]
    internal static partial void DevFreeObjects(uint cObjectCount, DEV_OBJECT* pObjects);

    // The paired objects of one type, with every property, copied into managed records; the native result is
    // freed before this returns. The filter is the object type's IsPaired property equal to DEVPROP_TRUE.
    // Returns the HRESULT; objects is empty on failure. Property data longer than maxPropertyBytes is cut to
    // that length (DevPropertyRecord.Truncated).
    internal static int GetPairedObjects(int objectType, out IReadOnlyList<DevObjectRecord> objects, int maxObjects = 4096, int maxPropertyBytes = 65536)
    {
        objects = [];
        DEVPROPKEY pairedKey = objectType == DevObjectTypeAEPContainer ? PKEY_Devices_AepContainer_IsPaired : PKEY_Devices_Aep_IsPaired;
        byte paired = CfgMgr32.DEVPROP_TRUE;
        var filter = new DEVPROP_FILTER_EXPRESSION
        {
            Operator = DEVPROP_OPERATOR_EQUALS,
            Property = new DEVPROPERTY
            {
                CompKey = new DEVPROPCOMPKEY { Key = pairedKey, Store = DEVPROP_STORE_SYSTEM, LocaleName = 0 },
                Type = CfgMgr32.DEVPROP_TYPE_BOOLEAN,
                BufferSize = 1,
                Buffer = (nint)(&paired),
            },
        };

        uint count = 0;
        DEV_OBJECT* found = null;
        int hr = DevGetObjects(objectType, DevQueryFlagAllProperties, 0, null, 1, &filter, &count, &found);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            var copied = new List<DevObjectRecord>((int)Math.Min(count, (uint)maxObjects));
            for (uint i = 0; i < count && i < maxObjects; i++)
            {
                copied.Add(Copy(&found[i], maxPropertyBytes));
            }

            objects = copied;
            return hr;
        }
        finally
        {
            if (found is not null)
            {
                DevFreeObjects(count, found);
            }
        }
    }

    private static DevObjectRecord Copy(DEV_OBJECT* item, int maxPropertyBytes)
    {
        string id = item->pszObjectId == 0 ? "" : Marshal.PtrToStringUni(item->pszObjectId) ?? "";
        var properties = new List<DevPropertyRecord>((int)Math.Min(item->cPropertyCount, 10000u));
        for (uint p = 0; p < item->cPropertyCount && p < 10000; p++)
        {
            DEVPROPERTY* property = &item->pProperties[p];
            int size = (int)Math.Min(property->BufferSize, (uint)maxPropertyBytes);
            byte[] data = property->Buffer == 0 || size == 0 ? [] : new ReadOnlySpan<byte>((void*)property->Buffer, size).ToArray();
            properties.Add(new DevPropertyRecord(property->CompKey.Key, property->CompKey.Store, property->Type, data,
                Truncated: property->BufferSize > (uint)maxPropertyBytes));
        }

        return new DevObjectRecord(item->ObjectType, id, properties);
    }
}

// One object from a device query, copied out of native memory.
internal sealed record DevObjectRecord(int ObjectType, string Id, IReadOnlyList<DevPropertyRecord> Properties);

// One property of such an object: its key and store, DEVPROPTYPE and raw bytes.
internal sealed record DevPropertyRecord(DEVPROPKEY Key, uint Store, uint Type, byte[] Data, bool Truncated);

// DEVPROPCOMPKEY { DEVPROPKEY Key; DEVPROPSTORE Store; PCWSTR LocaleName; }, 32 bytes on x64.
// https://learn.microsoft.com/en-us/windows/win32/api/devpropdef/ns-devpropdef-devpropcompkey
[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPCOMPKEY
{
    public DEVPROPKEY Key;
    public uint Store;
    public nint LocaleName;
}

// DEVPROPERTY { DEVPROPCOMPKEY CompKey; DEVPROPTYPE Type; ULONG BufferSize; PVOID Buffer; }, 48 bytes on x64.
// https://learn.microsoft.com/en-us/windows/win32/api/devpropdef/ns-devpropdef-devproperty
[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPERTY
{
    public DEVPROPCOMPKEY CompKey;
    public uint Type;
    public uint BufferSize;
    public nint Buffer;
}

// DEVPROP_FILTER_EXPRESSION { DEVPROP_OPERATOR Operator; DEVPROPERTY Property; }, 56 bytes on x64.
// https://learn.microsoft.com/en-us/windows/win32/api/devfiltertypes/ns-devfiltertypes-devprop_filter_expression
[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROP_FILTER_EXPRESSION
{
    public uint Operator;
    public DEVPROPERTY Property;
}

// DEV_OBJECT { DEV_OBJECT_TYPE ObjectType; PCWSTR pszObjectId; ULONG cPropertyCount; const DEVPROPERTY *pProperties; },
// 32 bytes on x64.
// https://learn.microsoft.com/en-us/windows/win32/api/devquerydef/ns-devquerydef-dev_object
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DEV_OBJECT
{
    public int ObjectType;
    public nint pszObjectId;
    public uint cPropertyCount;
    public DEVPROPERTY* pProperties;
}
