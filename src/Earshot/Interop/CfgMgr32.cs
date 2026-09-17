using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Configuration Manager (CfgMgr32) declarations for device-node reads and the gate's disable/enable.
// Every function returns a CONFIGRET, which is a raw code (not an HRESULT): record it with
// NativeCodes.ConfigRet (StepOutcomes.FromConfigRet), never NativeCodes.Name, whose small values are
// shared with Win32 codes (0x5 is CR_INVALID_DEVNODE here but ERROR_ACCESS_DENIED there).
// DEVINST is a DWORD. Strings are UTF-16.
//
// CM_Disable_DevNode and CM_Enable_DevNode are declared here for the SYSTEM gate only. Nothing in this
// file calls them.
internal static unsafe partial class CfgMgr32
{
    private const string Dll = "cfgmgr32.dll";

    // MAX_DEVICE_ID_LEN (cfgmgr32.h), in characters without the terminating NUL.
    internal const int MAX_DEVICE_ID_LEN = 200;

    // CONFIGRET values (cfgmgr32.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
    internal const uint CR_SUCCESS = 0x00000000;
    internal const uint CR_INVALID_POINTER = 0x00000003;
    internal const uint CR_INVALID_FLAG = 0x00000004;
    internal const uint CR_INVALID_DEVNODE = 0x00000005;
    internal const uint CR_NO_SUCH_DEVNODE = 0x0000000D;
    internal const uint CR_NO_SUCH_DEVINST = CR_NO_SUCH_DEVNODE;
    internal const uint CR_FAILURE = 0x00000013;
    internal const uint CR_REMOVE_VETOED = 0x00000017;
    internal const uint CR_BUFFER_SMALL = 0x0000001A;
    internal const uint CR_INVALID_DEVICE_ID = 0x0000001E;
    internal const uint CR_NEED_RESTART = 0x00000022;
    internal const uint CR_DEVICE_NOT_THERE = 0x00000024;
    internal const uint CR_NO_SUCH_VALUE = 0x00000025;
    internal const uint CR_NOT_DISABLEABLE = 0x00000028;
    internal const uint CR_ACCESS_DENIED = 0x00000033;
    internal const uint CR_INVALID_PROPERTY = 0x00000035;

    // CM_Get_Device_ID_List(_Size)W filter flags. FILTER_NONE lists every devnode, present or not, and
    // ignores the filter string. FILTER_ENUMERATOR takes an enumerator name such as "BTHENUM".
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw
    internal const uint CM_GETIDLIST_FILTER_NONE = 0x00000000;
    internal const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    internal const uint CM_GETIDLIST_FILTER_SERVICE = 0x00000002;
    internal const uint CM_GETIDLIST_FILTER_EJECTRELATIONS = 0x00000004;
    internal const uint CM_GETIDLIST_FILTER_REMOVALRELATIONS = 0x00000008;
    internal const uint CM_GETIDLIST_FILTER_POWERRELATIONS = 0x00000010;
    internal const uint CM_GETIDLIST_FILTER_BUSRELATIONS = 0x00000020;
    internal const uint CM_GETIDLIST_FILTER_TRANSPORTRELATIONS = 0x00000080;
    internal const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    internal const uint CM_GETIDLIST_FILTER_CLASS = 0x00000200;

    // CM_Locate_DevNodeW flags. NORMAL succeeds only for a devnode currently in the tree (a non-present
    // node gives CR_NO_SUCH_DEVNODE); PHANTOM also finds non-present nodes.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew
    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    internal const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    internal const uint CM_LOCATE_DEVNODE_CANCELREMOVE = 0x00000002;

    // CM_Disable_DevNode flags. Without CM_DISABLE_PERSIST the device is enabled again after a reboot,
    // which would silently defeat the boot block. The gate passes CM_DISABLE_PERSIST | CM_DISABLE_UI_NOT_OK.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
    internal const uint CM_DISABLE_POLITE = 0x00000000;
    internal const uint CM_DISABLE_ABSOLUTE = 0x00000001;
    internal const uint CM_DISABLE_HARDWARE = 0x00000002;
    internal const uint CM_DISABLE_UI_NOT_OK = 0x00000004;
    internal const uint CM_DISABLE_PERSIST = 0x00000008;

    // DN_* status bits (shared\cfg.h). pulProblemNumber is meaningful only when DN_HAS_PROBLEM is set.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/retrieving-the-status-and-problem-code-for-a-device-instance
    internal const uint DN_DRIVER_LOADED = 0x00000002;
    internal const uint DN_STARTED = 0x00000008;
    internal const uint DN_HAS_PROBLEM = 0x00000400;
    internal const uint DN_DISABLEABLE = 0x00002000;
    internal const uint DN_REMOVABLE = 0x00004000;
    internal const uint DN_PRIVATE_PROBLEM = 0x00008000;
    internal const uint DN_WILL_BE_REMOVED = 0x00040000;
    internal const uint DN_NT_ENUMERATOR = 0x00800000;
    internal const uint DN_NT_DRIVER = 0x01000000;

    // "The function driver for a device reported that the device is not connected" (cfg.h comment).
    // Not a documented contract; Core Audio stays the primary connection signal.
    internal const uint DN_DEVICE_DISCONNECTED = 0x02000000;
    internal const uint DN_NO_SHOW_IN_DM = 0x40000000;

    // CM_PROB_* problem codes (shared\cfg.h). Blocked = DN_HAS_PROBLEM with CM_PROB_DISABLED (22).
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-disabled
    internal const uint CM_PROB_DISABLED = 0x00000016;
    internal const uint CM_PROB_DEVICE_NOT_THERE = 0x00000018;
    internal const uint CM_PROB_DISABLED_SERVICE = 0x00000020;
    internal const uint CM_PROB_PHANTOM = 0x0000002D;

    // CONFIGFLAG_DISABLED (RegStr.h), bit 0x1 of DEVPKEY_Device_ConfigFlags. That property is documented
    // as internal use only; it is a cross-check, never the only signal.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
    internal const uint CONFIGFLAG_DISABLED = 0x00000001;

    // DEVPROPTYPE values (devpropdef.h).
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/property-data-type-identifiers
    internal const uint DEVPROP_TYPE_EMPTY = 0x00000000;
    internal const uint DEVPROP_TYPE_NULL = 0x00000001;
    internal const uint DEVPROP_TYPE_INT32 = 0x00000006;
    internal const uint DEVPROP_TYPE_UINT32 = 0x00000007;
    internal const uint DEVPROP_TYPE_GUID = 0x0000000D;
    internal const uint DEVPROP_TYPE_BOOLEAN = 0x00000011;
    internal const uint DEVPROP_TYPE_STRING = 0x00000012;
    internal const uint DEVPROP_TYPEMOD_LIST = 0x00002000;
    internal const uint DEVPROP_TYPE_STRING_LIST = DEVPROP_TYPE_STRING | DEVPROP_TYPEMOD_LIST;

    // DEVPROP_BOOLEAN is one byte: DEVPROP_TRUE is 0xFF, DEVPROP_FALSE is 0x00.
    internal const byte DEVPROP_TRUE = 0xFF;
    internal const byte DEVPROP_FALSE = 0x00;

    // DEVPROPKEYs (devpkey.h). Keys without their own reference page are listed with their SDK values;
    // the property model is described at
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-properties
    // DEVPKEY_NAME, STRING. Present on every AirPods node; FriendlyName is missing on four of them.
    internal static readonly DEVPROPKEY DEVPKEY_NAME = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-friendlyname
    internal static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
    internal static readonly DEVPROPKEY DEVPKEY_Device_ConfigFlags = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 12);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid
    internal static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    // DEVPKEY_Device_InLocalMachineContainer, BOOLEAN.
    internal static readonly DEVPROPKEY DEVPKEY_Device_InLocalMachineContainer = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 4);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-devnodestatus
    internal static readonly DEVPROPKEY DEVPKEY_Device_DevNodeStatus = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 2);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-problemcode
    internal static readonly DEVPROPKEY DEVPKEY_Device_ProblemCode = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 3);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-parent
    internal static readonly DEVPROPKEY DEVPKEY_Device_Parent = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 8);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-children
    internal static readonly DEVPROPKEY DEVPKEY_Device_Children = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 9);

    // DEVPKEY_Device_IsPresent, BOOLEAN (one byte, 0xFF when present). Readable for non-present nodes.
    internal static readonly DEVPROPKEY DEVPKEY_Device_IsPresent = new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 5);

    // DEVPKEY_Device_HasProblem, BOOLEAN.
    internal static readonly DEVPROPKEY DEVPKEY_Device_HasProblem = new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 6);

    // Retry cap for the size-then-fetch pairs, whose result can grow between the two calls.
    private const int BufferSmallRetries = 8;

    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_list_sizew
    // pulLen receives the buffer size in characters, including the final NUL.
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    // Buffer receives a double-NUL-terminated list; BufferLen is in characters. Returns CR_BUFFER_SMALL
    // when the list grew since the size call.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Get_Device_ID_ListW(string? pszFilter, char* buffer, uint bufferLen, uint ulFlags);

    // pDeviceID is a DEVINSTID_W (a non-const WCHAR*), so it is passed as a private NUL-terminated copy,
    // never a pinned managed string. Use LocateDevNode.
    [LibraryImport(Dll)]
    internal static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, char* pDeviceID, uint ulFlags);

    // A non-present devnode returns CR_NO_SUCH_DEVNODE here.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
    [LibraryImport(Dll)]
    internal static partial uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    // With a null buffer, *PropertyBufferSize must be 0; CR_BUFFER_SMALL then reports the size in bytes.
    // A missing property returns CR_NO_SUCH_VALUE.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw
    [LibraryImport(Dll)]
    internal static partial uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, in DEVPROPKEY propertyKey, out uint propertyType, byte* propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    // The keys of every property set on a devnode. With a null array and a count of 0 it returns CR_BUFFER_SMALL
    // and the count needed. Use GetDevNodePropertyKeys.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_property_keys
    [LibraryImport(Dll)]
    internal static partial uint CM_Get_DevNode_Property_Keys(uint dnDevInst, DEVPROPKEY* propertyKeyArray, ref uint propertyKeyCount, uint ulFlags);

    // Lists the property keys set on a devnode. Retries on CR_BUFFER_SMALL. Returns the CONFIGRET; keys is empty
    // on failure.
    internal static uint GetDevNodePropertyKeys(uint devInst, out DEVPROPKEY[] keys)
    {
        keys = [];
        uint count = 0;
        uint cr = CM_Get_DevNode_Property_Keys(devInst, null, ref count, 0);
        if (cr == CR_SUCCESS)
        {
            return cr;
        }

        for (int attempt = 0; attempt < BufferSmallRetries && cr == CR_BUFFER_SMALL; attempt++)
        {
            var buffer = new DEVPROPKEY[Math.Max(count, 1u)];
            count = (uint)buffer.Length;
            fixed (DEVPROPKEY* pointer = buffer)
            {
                cr = CM_Get_DevNode_Property_Keys(devInst, pointer, ref count, 0);
            }

            if (cr == CR_SUCCESS)
            {
                keys = buffer.AsSpan(0, (int)Math.Min(count, (uint)buffer.Length)).ToArray();
            }
        }

        return cr;
    }

    // SYSTEM gate only. Declared, never called outside the gate's block action.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
    [LibraryImport(Dll)]
    internal static partial uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    // SYSTEM gate only. ulFlags must be 0.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
    [LibraryImport(Dll)]
    internal static partial uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    // Lists device instance ids. Retries while the list grows between the size and list calls
    // (CR_BUFFER_SMALL). Returns the CONFIGRET; ids is empty on failure.
    internal static uint GetDeviceIdList(string? filter, uint flags, out string[] ids)
    {
        ids = [];
        uint cr = CR_BUFFER_SMALL;
        for (int attempt = 0; attempt < BufferSmallRetries && cr == CR_BUFFER_SMALL; attempt++)
        {
            cr = CM_Get_Device_ID_List_SizeW(out uint length, filter, flags);
            if (cr != CR_SUCCESS)
            {
                return cr;
            }

            // Room for at least the double NUL of an empty list.
            var buffer = new char[Math.Max(length, 2u)];
            fixed (char* pointer = buffer)
            {
                cr = CM_Get_Device_ID_ListW(filter, pointer, (uint)buffer.Length, flags);
            }

            if (cr == CR_SUCCESS)
            {
                ids = ParseMultiSz(buffer);
            }
        }

        return cr;
    }

    // Finds the devnode for an instance id. Use CM_LOCATE_DEVNODE_PHANTOM to include non-present nodes.
    internal static uint LocateDevNode(string instanceId, uint flags, out uint devInst)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var copy = new char[instanceId.Length + 1];
        instanceId.CopyTo(copy);
        fixed (char* pointer = copy)
        {
            return CM_Locate_DevNodeW(out devInst, pointer, flags);
        }
    }

    // Reads one devnode property as raw bytes plus its DEVPROPTYPE. Retries on CR_BUFFER_SMALL.
    // Returns the CONFIGRET; data is empty on failure.
    internal static uint GetDevNodeProperty(uint devInst, DEVPROPKEY key, out uint propertyType, out byte[] data)
    {
        data = [];
        uint size = 0;
        uint cr = CM_Get_DevNode_PropertyW(devInst, in key, out propertyType, null, ref size, 0);
        for (int attempt = 0; attempt < BufferSmallRetries && cr == CR_BUFFER_SMALL; attempt++)
        {
            var buffer = new byte[Math.Max(size, 1u)];
            size = (uint)buffer.Length;
            fixed (byte* pointer = buffer)
            {
                cr = CM_Get_DevNode_PropertyW(devInst, in key, out propertyType, pointer, ref size, 0);
            }

            if (cr == CR_SUCCESS)
            {
                data = buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length)).ToArray();
            }
        }

        return cr;
    }

    // Splits a double-NUL-terminated UTF-16 list into its strings. Stops at the first empty entry.
    internal static string[] ParseMultiSz(ReadOnlySpan<char> buffer)
    {
        var items = new List<string>();
        while (!buffer.IsEmpty)
        {
            int end = buffer.IndexOf('\0');
            if (end == 0)
            {
                break;
            }

            if (end < 0)
            {
                items.Add(buffer.ToString());
                break;
            }

            items.Add(buffer[..end].ToString());
            buffer = buffer[(end + 1)..];
        }

        return items.ToArray();
    }

    // DEVPROP_TYPE_STRING: a NUL-terminated UTF-16 string.
    internal static bool TryDecodeString(uint propertyType, ReadOnlySpan<byte> data, out string? value)
    {
        value = null;
        if (propertyType != DEVPROP_TYPE_STRING)
        {
            return false;
        }

        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(data[..(data.Length & ~1)]);
        int end = chars.IndexOf('\0');
        value = (end < 0 ? chars : chars[..end]).ToString();
        return true;
    }

    // DEVPROP_TYPE_STRING_LIST: a double-NUL-terminated UTF-16 list.
    internal static bool TryDecodeStringList(uint propertyType, ReadOnlySpan<byte> data, out string[] value)
    {
        value = [];
        if (propertyType != DEVPROP_TYPE_STRING_LIST)
        {
            return false;
        }

        value = ParseMultiSz(MemoryMarshal.Cast<byte, char>(data[..(data.Length & ~1)]));
        return true;
    }

    // DEVPROP_TYPE_GUID: 16 bytes.
    internal static bool TryDecodeGuid(uint propertyType, ReadOnlySpan<byte> data, out Guid value)
    {
        value = Guid.Empty;
        if (propertyType != DEVPROP_TYPE_GUID || data.Length < 16)
        {
            return false;
        }

        value = new Guid(data[..16]);
        return true;
    }

    // DEVPROP_TYPE_UINT32 or DEVPROP_TYPE_INT32 (ConfigFlags is documented as INT32, devpkey.h says UINT32).
    internal static bool TryDecodeUInt32(uint propertyType, ReadOnlySpan<byte> data, out uint value)
    {
        value = 0;
        if ((propertyType != DEVPROP_TYPE_UINT32 && propertyType != DEVPROP_TYPE_INT32) || data.Length < 4)
        {
            return false;
        }

        value = MemoryMarshal.Read<uint>(data);
        return true;
    }

    // DEVPROP_TYPE_BOOLEAN: one byte, DEVPROP_TRUE (0xFF) or DEVPROP_FALSE (0x00). Any non-zero is true.
    internal static bool TryDecodeBoolean(uint propertyType, ReadOnlySpan<byte> data, out bool value)
    {
        value = false;
        if (propertyType != DEVPROP_TYPE_BOOLEAN || data.Length < 1)
        {
            return false;
        }

        value = data[0] != DEVPROP_FALSE;
        return true;
    }
}

// DEVPROPKEY { DEVPROPGUID fmtid; DEVPROPID pid; }, 20 bytes.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpropkey
[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;

    public DEVPROPKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}
