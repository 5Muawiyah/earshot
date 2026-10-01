using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Widget;

namespace Earshot.Battery;

// Windows' own Hands-Free battery figure for the paired AirPods: the property {104EA319-6EE2-4701-BD47-8DDBF425BBE5}
// id 2 (BatterySweep.HandsFreeBatteryKey), where a figure is expected while the Hands-Free profile is up. That is
// not established: the property has been empty on every node read so far, with Hands-Free off, so what makes
// Windows fill it in has never been seen. Read-only, property reads with CfgMgr32 and DevQuery and nothing else: nothing is enabled,
// disabled, connected or written, so it keeps the at-rest invariant whatever it finds. It lives here and not under
// Earshot.Widget because the widget's own types may not reach a device path.
//
// Where it looks, in this order:
//   1. every device node of the pinned device (the same nodes NodeMatch.IsDisableTarget selects: in the pinned
//      container, a Bluetooth bus node, carrying the device's address), listed with the phantom nodes included.
//      Listing the nodes and reading their properties takes a millisecond or two;
//   2. only when readPairedObjects is set (nothing that ships sets it): the paired association endpoint's container
//      object, then the association endpoints themselves, found by the pinned container id. Windows' own device query
//      for those blocks for about a minute each on the machine this was written on (measured), so neither the
//      minute-by-minute reads nor the probe use it; diag battery-sweep reads the paired objects as evidence.
// A figure is taken only from a byte, an unsigned 16 bit or an unsigned 32 bit property of 0 to 100. Anything else
// (another type, out of range, empty) is no figure and is said so in the note. Two different figures are taken as
// none, whichever is right being unknowable: both kinds are named.
//
// The figure is empty with Hands-Free off, which is how Earshot runs by default, so this normally finds nothing: the
// broadcast is the usual source and this fills in only when Windows really has a number.
// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw
// https://learn.microsoft.com/en-us/windows/win32/api/devquery/nf-devquery-devgetobjects
internal sealed class HandsFreeBatteryProvider(IBatterySweepReader reader, Func<string> pinnedAddress, bool readPairedObjects = false) : IBatteryProvider, IHandsFreeBatterySource
{
    private const string DeviceNode = "device node";
    private const string PairedObject = "paired object";

    public bool HasSource => true;

    // The percent alone, for the contract every battery provider shares. The address is the pinned device's.
    public BatteryReading Read(Guid containerId) => new(Read(containerId, pinnedAddress()).Percent);

    public HandsFreeBatteryRead Read(Guid container, string address12)
    {
        var steps = new List<StepOutcome>();
        var found = new List<(string Origin, int Percent)>();
        var ignored = new List<string>();
        int nodesRead = 0;
        int objectsRead = 0;

        if (!NodeMatch.IsValidTargetContainer(container))
        {
            steps.Add(StepOutcomes.NotAttempted("hands-free-battery", "There is no pinned device, so there is nothing to read."));
            return new HandsFreeBatteryRead(null, null, "No pinned device.", steps);
        }

        if (BoundaryValidation.IsAddress12(address12))
        {
            nodesRead = ReadNodes(container, address12, steps, found, ignored);
        }

        if (readPairedObjects)
        {
            objectsRead = ReadObjects(container, steps, found, ignored);
        }

        int[] distinct = found.Select(f => f.Percent).Distinct().ToArray();
        if (distinct.Length == 1)
        {
            return new HandsFreeBatteryRead(distinct[0], found[0].Origin, null, steps);
        }

        if (distinct.Length > 1)
        {
            string kinds = string.Join(" and ", found.Select(f => f.Origin).Distinct());
            return new HandsFreeBatteryRead(null, null, "Two different figures were found (" + kinds + "), so none is taken.", steps);
        }

        string note = "No figure: the property was empty or absent on " + nodesRead.ToString(CultureInfo.InvariantCulture) +
            " device nodes" + (readPairedObjects ? " and " + objectsRead.ToString(CultureInfo.InvariantCulture) + " paired objects" : string.Empty) +
            (ignored.Count > 0 ? "; unusable values: " + string.Join("; ", ignored.Distinct().Take(4)) : string.Empty) + ".";
        return new HandsFreeBatteryRead(null, null, note, steps);
    }

    // The pinned device's nodes, then the property on each. Returns how many nodes were read.
    private int ReadNodes(Guid container, string address12, List<StepOutcome> steps, List<(string Origin, int Percent)> found, List<string> ignored)
    {
        uint list = reader.ListDeviceIds(out string[] ids);
        if (list != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("hands-free-battery:cm-list", list, "The device list could not be read."));
            return 0;
        }

        int read = 0;
        foreach (string id in ids)
        {
            // NodeMatch needs the address in the id, so only those nodes need a container read.
            if (!id.Contains(address12, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            uint located = reader.Locate(id, out uint devInst);
            if (located != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("hands-free-battery:cm-locate-phantom", located, "A node of the pinned device could not be located."));
                continue;
            }

            uint containerCr = reader.GetProperty(devInst, CfgMgr32.DEVPKEY_Device_ContainerId, out uint containerType, out byte[] containerData);
            if (containerCr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("hands-free-battery:cm-container", containerCr, "A node's container could not be read."));
                continue;
            }

            if (!CfgMgr32.TryDecodeGuid(containerType, containerData, out Guid nodeContainer) ||
                !NodeMatch.IsDisableTarget(id, nodeContainer, container, address12))
            {
                continue;
            }

            uint property = reader.GetProperty(devInst, BatterySweep.HandsFreeBatteryKey, out uint type, out byte[] data);
            read++;
            if (property == CfgMgr32.CR_NO_SUCH_VALUE)
            {
                continue; // not set: the normal case with Hands-Free off
            }

            if (property != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("hands-free-battery:cm-property", property, "The battery property of a node could not be read."));
                continue;
            }

            Take(DeviceNode, type, data, found, ignored);
        }

        return read;
    }

    // The association endpoint objects of the pinned device: the container object (its id is the container) and the
    // endpoints that name that container. Returns how many objects were read.
    private int ReadObjects(Guid container, List<StepOutcome> steps, List<(string Origin, int Percent)> found, List<string> ignored)
    {
        int read = 0;
        foreach (int objectType in new[] { DevQuery.DevObjectTypeAEPContainer, DevQuery.DevObjectTypeAEP })
        {
            int hr = reader.GetPairedObjects(objectType, out IReadOnlyList<DevObjectRecord> objects);
            if (hr < 0)
            {
                steps.Add(StepOutcomes.FromHResult("hands-free-battery:dev-get-objects", hr, "The paired objects could not be read."));
                continue;
            }

            foreach (DevObjectRecord item in objects)
            {
                if (!IsOfContainer(item, objectType, container))
                {
                    continue;
                }

                read++;
                foreach (DevPropertyRecord property in item.Properties)
                {
                    if (property.Key.fmtid == BatterySweep.HandsFreeBatteryKey.fmtid && property.Key.pid == BatterySweep.HandsFreeBatteryKey.pid)
                    {
                        Take(PairedObject, property.Type & DevQuery.DEVPROP_MASK_TYPE, property.Data, found, ignored);
                    }
                }
            }
        }

        return read;
    }

    private static bool IsOfContainer(DevObjectRecord item, int objectType, Guid container)
    {
        if (objectType == DevQuery.DevObjectTypeAEPContainer)
        {
            return Guid.TryParse(item.Id, out Guid id) && id == container;
        }

        foreach (DevPropertyRecord property in item.Properties)
        {
            if (property.Key.fmtid == BatterySweep.AepContainerId.fmtid && property.Key.pid == BatterySweep.AepContainerId.pid &&
                CfgMgr32.TryDecodeGuid(property.Type, property.Data, out Guid id))
            {
                return id == container;
            }
        }

        return false;
    }

    // A figure is a byte, an unsigned 16 bit or an unsigned 32 bit value of 0 to 100 and nothing else.
    internal static int? Accept(uint type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        long? value = type switch
        {
            DevQuery.DEVPROP_TYPE_BYTE when data.Length >= 1 => data[0],
            DevQuery.DEVPROP_TYPE_UINT16 when data.Length >= 2 => BitConverter.ToUInt16(data),
            CfgMgr32.DEVPROP_TYPE_UINT32 when data.Length >= 4 => BitConverter.ToUInt32(data),
            _ => null,
        };
        return value is >= 0 and <= 100 ? (int)value : null;
    }

    private static void Take(string origin, uint type, byte[] data, List<(string Origin, int Percent)> found, List<string> ignored)
    {
        if (Accept(type, data) is int percent)
        {
            found.Add((origin, percent));
            return;
        }

        // An empty value is the normal case, not worth a note; anything else is recorded with its type and size, never
        // the bytes.
        if (data.Length > 0)
        {
            ignored.Add("type 0x" + type.ToString("X", CultureInfo.InvariantCulture) + ", " + data.Length.ToString(CultureInfo.InvariantCulture) + " bytes, on a " + origin);
        }
    }
}
