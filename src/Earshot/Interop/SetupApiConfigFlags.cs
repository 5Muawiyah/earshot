using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Earshot.Contracts;

namespace Earshot.Interop;

// The configuration flags of a device instance, read and written through SetupAPI by instance id. Used for one thing:
// CONFIGFLAG_DISABLED on a node that is not in the tree. CM_Disable_DevNode and CM_Enable_DevNode need a node that is
// present, so a node that was persistently disabled and then left the tree (the AirPods' audio nodes while the device
// node is disabled) keeps the flag, and can only be cleared or set through its registry property.
// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdiopendeviceinfow
// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw
// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdisetdeviceregistrypropertyw
//
// Every result carries the raw Win32 error and the stage that produced it. Nothing here catches anything: a failure is
// a code, and the caller records it.

// Where in the sequence a read or write stopped; None when it ran to the end.
internal enum ConfigFlagsStage
{
    None,
    CreateList,
    Open,
    Read,
    Write,
    ReadBack,
}

// The flags read from a device instance. Present is false when the instance has no ConfigFlags property, which is the
// same as no flag set.
internal readonly record struct ConfigFlagsRead(uint Win32Error, uint Flags, bool Present, ConfigFlagsStage Stage)
{
    public bool Ok => Win32Error == 0;
}

// A change of CONFIGFLAG_DISABLED. Before and After are the flags read before the write and read back after it; a
// change that was already in effect writes nothing (Wrote is false).
internal readonly record struct ConfigFlagsChange(uint Win32Error, uint Before, uint After, bool Wrote, ConfigFlagsStage Stage)
{
    public bool Ok => Win32Error == 0;

    public bool DisabledAfter => (After & CfgMgr32.CONFIGFLAG_DISABLED) != 0;
}

// The read side. The real implementation cannot write.
internal interface IConfigFlagsReader
{
    ConfigFlagsRead Read(string instanceId);
}

// The write side, for the elevated gate and uninstall only.
internal interface IConfigFlagsWriter
{
    // Clears CONFIGFLAG_DISABLED. A node without the flag is left alone.
    ConfigFlagsChange ClearDisabledFlag(string instanceId);

    // Sets CONFIGFLAG_DISABLED. A node that has it is left alone.
    ConfigFlagsChange SetDisabledFlag(string instanceId);
}

internal sealed class SetupApiConfigFlagsReader : IConfigFlagsReader
{
    public ConfigFlagsRead Read(string instanceId) => SetupApi.ReadConfigFlags(instanceId);
}

internal sealed class SetupApiConfigFlagsWriter : IConfigFlagsWriter
{
    public ConfigFlagsChange ClearDisabledFlag(string instanceId) => SetupApi.ChangeDisabledFlag(instanceId, disabled: false);

    public ConfigFlagsChange SetDisabledFlag(string instanceId) => SetupApi.ChangeDisabledFlag(instanceId, disabled: true);
}

// SP_DEVINFO_DATA: cbSize, the class GUID, the devinst and a reserved pointer, 32 bytes on x64.
// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/ns-setupapi-sp_devinfo_data
[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVINFO_DATA
{
    public uint cbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public nuint Reserved;
}

// A device information set; destroyed when the handle is released.
// https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdidestroydeviceinfolist
internal sealed class DeviceInfoListHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public DeviceInfoListHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => SetupApi.DestroyDeviceInfoList(handle);
}

internal static unsafe partial class SetupApi
{
    private const string Dll = "setupapi.dll";

    // SPDRP_CONFIGFLAGS (setupapi.h), a REG_DWORD.
    // https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw
    internal const uint SPDRP_CONFIGFLAGS = 0x0000000A;

    private const uint ErrorInvalidData = 13;

    [LibraryImport(Dll, SetLastError = true)]
    private static partial DeviceInfoListHandle SetupDiCreateDeviceInfoList(Guid* classGuid, nint hwndParent);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(DeviceInfoListHandle deviceInfoSet, string deviceInstanceId, nint hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryPropertyW(
        DeviceInfoListHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, out uint propertyRegDataType, byte* propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceRegistryPropertyW(
        DeviceInfoListHandle deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, byte* propertyBuffer, uint propertyBufferSize);

    [LibraryImport(Dll, EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyDeviceInfoList(nint deviceInfoSet);

    // Opens the instance (present or not: the page does not limit it to present devices) and reads ConfigFlags.
    internal static ConfigFlagsRead ReadConfigFlags(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using DeviceInfoListHandle set = SetupDiCreateDeviceInfoList(null, 0);
        if (set.IsInvalid)
        {
            return new ConfigFlagsRead((uint)Marshal.GetLastPInvokeError(), 0, false, ConfigFlagsStage.CreateList);
        }

        return ReadOpen(set, instanceId, out _);
    }

    // Reads the flag, writes the wanted state when it differs, and reads it back.
    internal static ConfigFlagsChange ChangeDisabledFlag(string instanceId, bool disabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using DeviceInfoListHandle set = SetupDiCreateDeviceInfoList(null, 0);
        if (set.IsInvalid)
        {
            return new ConfigFlagsChange((uint)Marshal.GetLastPInvokeError(), 0, 0, false, ConfigFlagsStage.CreateList);
        }

        return ChangeOpen(set, instanceId, disabled);
    }

    private static ConfigFlagsRead ReadOpen(DeviceInfoListHandle set, string instanceId, out SP_DEVINFO_DATA data)
    {
        data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
        if (!SetupDiOpenDeviceInfoW(set, instanceId, 0, 0, ref data))
        {
            return new ConfigFlagsRead((uint)Marshal.GetLastPInvokeError(), 0, false, ConfigFlagsStage.Open);
        }

        return ReadFlags(set, ref data, ConfigFlagsStage.Read);
    }

    private static ConfigFlagsRead ReadFlags(DeviceInfoListHandle set, ref SP_DEVINFO_DATA data, ConfigFlagsStage stage)
    {
        uint flags = 0;
        bool ok = SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_CONFIGFLAGS, out _, (byte*)&flags, sizeof(uint), out _);
        if (ok)
        {
            return new ConfigFlagsRead(0, flags, true, ConfigFlagsStage.None);
        }

        uint error = (uint)Marshal.GetLastPInvokeError();

        // A device with no ConfigFlags value reports ERROR_INVALID_DATA: no flag is set.
        return error == ErrorInvalidData
            ? new ConfigFlagsRead(0, 0, false, ConfigFlagsStage.None)
            : new ConfigFlagsRead(error, 0, false, stage);
    }

    private static ConfigFlagsChange ChangeOpen(DeviceInfoListHandle set, string instanceId, bool disabled)
    {
        ConfigFlagsRead before = ReadOpen(set, instanceId, out SP_DEVINFO_DATA data);
        if (!before.Ok)
        {
            return new ConfigFlagsChange(before.Win32Error, 0, 0, false, before.Stage);
        }

        uint wanted = disabled ? before.Flags | CfgMgr32.CONFIGFLAG_DISABLED : before.Flags & ~CfgMgr32.CONFIGFLAG_DISABLED;
        if (wanted == before.Flags)
        {
            return new ConfigFlagsChange(0, before.Flags, before.Flags, false, ConfigFlagsStage.None);
        }

        if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_CONFIGFLAGS, (byte*)&wanted, sizeof(uint)))
        {
            return new ConfigFlagsChange((uint)Marshal.GetLastPInvokeError(), before.Flags, before.Flags, false, ConfigFlagsStage.Write);
        }

        ConfigFlagsRead after = ReadFlags(set, ref data, ConfigFlagsStage.ReadBack);
        return new ConfigFlagsChange(after.Win32Error, before.Flags, after.Ok ? after.Flags : 0, true, after.Ok ? ConfigFlagsStage.None : after.Stage);
    }
}
