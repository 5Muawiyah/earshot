using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Tray;

// One paired Bluetooth Classic device, from its BTHENUM\DEV_<address> device node.
internal sealed record PairedDevice(
    string InstanceId,
    string Name,
    string Address,      // 12 upper-case hex characters
    Guid ContainerId,
    bool? IsPresent);    // null when the property could not be read

internal sealed record PairedDeviceList(IReadOnlyList<PairedDevice> Devices, IReadOnlyList<StepOutcome> Problems);

// Lists paired Bluetooth devices with read-only CfgMgr32 calls. Nothing here changes a device node.
//
// Every BTHENUM node is listed, present or not (CM_GETIDLIST_FILTER_ENUMERATOR without
// CM_GETIDLIST_FILTER_PRESENT), and only the per-device nodes are kept: BTHENUM\DEV_<12 hex>\...
// The per-service nodes (BTHENUM\{uuid}_VID...) carry the same address but are not devices.
// Each kept node is opened with CM_LOCATE_DEVNODE_PHANTOM so a device that is paired but away still
// appears. Every failed call is kept as a StepOutcome and logged; the list never guesses a value.
// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw
// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew
// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw
internal sealed partial class BluetoothDeviceList
{
    public const string Enumerator = "BTHENUM";

    // Name properties in the order they are tried.
    private static readonly (DEVPROPKEY Key, string Label)[] NameProperties =
    [
        (CfgMgr32.DEVPKEY_Device_FriendlyName, "friendly-name"),
        (CfgMgr32.DEVPKEY_NAME, "name"),
    ];

    private readonly ILog _log;

    public BluetoothDeviceList(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    [GeneratedRegex(@"^BTHENUM\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNodeId();

    // The address of a per-device node such as BTHENUM\DEV_5A6B7C8D9EAF\b&1a2b3c4d&0&BLUETOOTHDEVICE_5A6B7C8D9EAF.
    // Windows spells the prefix in either case (Dev_ for some devices), so it is matched case-insensitively
    // and the address is returned upper-case.
    public static bool TryParseAddress(string? instanceId, out string address12)
    {
        address12 = "";
        if (instanceId is null)
        {
            return false;
        }

        Match match = DeviceNodeId().Match(instanceId);
        if (!match.Success)
        {
            return false;
        }

        string address = match.Groups[1].Value.ToUpperInvariant();
        if (!BoundaryValidation.IsAddress12(address))
        {
            return false;
        }

        address12 = address;
        return true;
    }

    // The single address whose device node is in container, or null when there is none or more than one.
    public static string? AddressForContainer(IEnumerable<PairedDevice> devices, Guid container)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (!NodeMatch.IsValidTargetContainer(container))
        {
            return null;
        }

        string[] addresses = devices
            .Where(d => d.ContainerId == container)
            .Select(d => d.Address)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return addresses.Length == 1 ? addresses[0] : null;
    }

    public PairedDeviceList Read()
    {
        var devices = new List<PairedDevice>();
        var problems = new List<StepOutcome>();

        uint cr = CfgMgr32.GetDeviceIdList(Enumerator, CfgMgr32.CM_GETIDLIST_FILTER_ENUMERATOR, out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            Problem(problems, StepOutcomes.FromConfigRet("cm-get-device-id-list:" + Enumerator, cr));
            return new PairedDeviceList(devices, problems);
        }

        foreach (string id in ids)
        {
            if (!TryParseAddress(id, out string address))
            {
                continue;
            }

            cr = CfgMgr32.LocateDevNode(id, CfgMgr32.CM_LOCATE_DEVNODE_PHANTOM, out uint devInst);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                Problem(problems, StepOutcomes.FromConfigRet("cm-locate-devnode:" + id, cr));
                continue;
            }

            cr = CfgMgr32.GetDevNodeProperty(devInst, CfgMgr32.DEVPKEY_Device_ContainerId, out uint type, out byte[] data);
            if (cr != CfgMgr32.CR_SUCCESS || !CfgMgr32.TryDecodeGuid(type, data, out Guid container))
            {
                Problem(problems, PropertyProblem("container-id", id, cr));
                continue;
            }

            string name = ReadName(devInst, id, problems) ?? address;
            bool? present = null;
            cr = CfgMgr32.GetDevNodeProperty(devInst, CfgMgr32.DEVPKEY_Device_IsPresent, out type, out data);
            if (cr == CfgMgr32.CR_SUCCESS && CfgMgr32.TryDecodeBoolean(type, data, out bool isPresent))
            {
                present = isPresent;
            }
            else
            {
                Problem(problems, PropertyProblem("is-present", id, cr));
            }

            devices.Add(new PairedDevice(id, name, address, container, present));
        }

        devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new PairedDeviceList(devices, problems);
    }

    // FriendlyName, then DEVPKEY_NAME. A missing property (CR_NO_SUCH_VALUE) is normal and not a problem.
    private string? ReadName(uint devInst, string id, List<StepOutcome> problems)
    {
        foreach ((DEVPROPKEY key, string label) in NameProperties)
        {
            uint cr = CfgMgr32.GetDevNodeProperty(devInst, key, out uint type, out byte[] data);
            if (cr == CfgMgr32.CR_SUCCESS && CfgMgr32.TryDecodeString(type, data, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (cr != CfgMgr32.CR_SUCCESS && cr != CfgMgr32.CR_NO_SUCH_VALUE)
            {
                Problem(problems, PropertyProblem(label, id, cr));
            }
        }

        return null;
    }

    // A failed read carries its CONFIGRET; a successful read of the wrong type is recorded as not available.
    private static StepOutcome PropertyProblem(string property, string id, uint cr) =>
        cr != CfgMgr32.CR_SUCCESS
            ? StepOutcomes.FromConfigRet("cm-get-devnode-property:" + property + ":" + id, cr)
            : StepOutcomes.NotAvailable("cm-get-devnode-property:" + property + ":" + id, "The property has an unexpected type.");

    private void Problem(List<StepOutcome> problems, StepOutcome step)
    {
        problems.Add(step);
        _log.Warn("Bluetooth device list: " + TrayReport.DescribeStep(step));
    }
}
