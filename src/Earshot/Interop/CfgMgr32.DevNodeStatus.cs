using Earshot.Contracts;

namespace Earshot.Interop;

// What the radio check reads from one device node: its status bits and problem number, and the driver's version and date.
// Every read is read-only. A part that could not be read is null here and its raw CONFIGRET is in Steps.
internal sealed record DevNodeStatusInfo(
    bool Located,
    uint Status,
    uint Problem,
    string? DriverVersion,
    DateOnly? DriverDate,
    IReadOnlyList<StepOutcome> Steps)
{
    // The problem number is meaningful only with DN_HAS_PROBLEM set, so 0 stands for "no problem" otherwise.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/retrieving-the-status-and-problem-code-for-a-device-instance
    public bool HasProblem => Located && (Status & CfgMgr32.DN_HAS_PROBLEM) != 0;

    public uint ProblemCode => HasProblem ? Problem : 0;
}

internal static unsafe partial class CfgMgr32
{
    // DEVPROP_TYPE_FILETIME: eight bytes, a FILETIME.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devprop-type-filetime
    internal const uint DEVPROP_TYPE_FILETIME = 0x00000010;

    // The property category (A8B865DD...) and the pids 2 (date) and 3 (version) are the devpkey.h values; the pages below give the
    // names and types but not the numbers. A read of the owner's radio through Get-PnpDeviceProperty on 25 October 2026 gave the
    // same version and date as this class read (by hand, as recorded in verification.md); the test itself does not compare.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-driverdate
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverDate = new(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 2);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-driverversion
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = new(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 3);

    // DEVPROP_TYPE_FILETIME to a date, in UTC. False for another type, a short buffer, or a value outside DateTime's range.
    internal static bool TryDecodeFileTimeDate(uint propertyType, ReadOnlySpan<byte> data, out DateOnly value)
    {
        value = default;
        if (propertyType != DEVPROP_TYPE_FILETIME || data.Length < 8)
        {
            return false;
        }

        long ticks = System.Runtime.InteropServices.MemoryMarshal.Read<long>(data);
        if (ticks <= 0 || ticks > DateTime.MaxValue.ToFileTimeUtc())
        {
            return false;
        }

        value = DateOnly.FromDateTime(DateTime.FromFileTimeUtc(ticks));
        return true;
    }

    // A property read that succeeded with a type or size this build does not decode: a failed step whose code is the property
    // type, so a version or date reported unknown has its reason. Null when the read failed (its CONFIGRET is its own step) or
    // the value decoded.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/property-data-type-identifiers
    internal static StepOutcome? UndecodedProperty(string step, uint cr, uint propertyType, bool decoded) =>
        cr == CR_SUCCESS && !decoded
            ? new StepOutcome(step + "-type", false, unchecked((int)propertyType),
                "DEVPROP_TYPE 0x" + propertyType.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), "The value's type or size is not one this build reads.")
            : null;

    // Reads the status and the driver of the present node with this instance id. Locate, status and each property are separate
    // calls; a failed one is recorded with its CONFIGRET and does not stop the others.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw
    internal static DevNodeStatusInfo ReadDevNodeStatus(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var steps = new List<StepOutcome>();
        uint cr = LocateDevNode(instanceId, CM_LOCATE_DEVNODE_NORMAL, out uint devInst);
        steps.Add(StepOutcomes.FromConfigRet("cm-locate-radio", cr));
        if (cr != CR_SUCCESS)
        {
            return new DevNodeStatusInfo(false, 0, 0, null, null, steps);
        }

        cr = CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
        steps.Add(StepOutcomes.FromConfigRet("cm-status-radio", cr));
        bool located = cr == CR_SUCCESS;

        string? version = null;
        cr = GetDevNodeProperty(devInst, DEVPKEY_Device_DriverVersion, out uint type, out byte[] data);
        steps.Add(StepOutcomes.FromConfigRet("cm-driver-version", cr));
        bool decoded = cr == CR_SUCCESS && TryDecodeString(type, data, out version);
        if (UndecodedProperty("cm-driver-version", cr, type, decoded) is { } versionType)
        {
            version = null;
            steps.Add(versionType);
        }

        DateOnly? date = null;
        cr = GetDevNodeProperty(devInst, DEVPKEY_Device_DriverDate, out type, out data);
        steps.Add(StepOutcomes.FromConfigRet("cm-driver-date", cr));
        if (cr == CR_SUCCESS && TryDecodeFileTimeDate(type, data, out DateOnly day))
        {
            date = day;
        }
        else if (UndecodedProperty("cm-driver-date", cr, type, decoded: false) is { } dateType)
        {
            steps.Add(dateType);
        }

        return new DevNodeStatusInfo(located, status, problem, version, date, steps);
    }
}
