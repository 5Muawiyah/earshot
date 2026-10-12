using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot;

// Every AirPods device node set Windows knows about, found only by the identity rule (AirPodsIdentity): a container
// holding a BTHENUM service node with an AirPods product id. Reads only. A non-present node counts, because a pairing
// that was removed leaves its nodes behind, and so does a device Earshot blocked while it was absent.
internal sealed record AirPodsDiscoveryResult(bool Listed, IReadOnlyList<AirPodsCandidate> Candidates, IReadOnlyList<StepOutcome> Steps);

internal static class AirPodsDiscovery
{
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/overview-of-container-ids
    public static AirPodsDiscoveryResult Find(INodeReader nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var steps = new List<StepOutcome>();
        var found = new List<AirPodsCandidate>();

        uint cr = nodes.ListDeviceIds(out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-list", cr, "The device list could not be read."));
            return new AirPodsDiscoveryResult(false, found, steps);
        }

        var seen = new HashSet<(string Address, Guid Container)>();
        foreach (string id in ids)
        {
            if (!AirPodsIdentity.TryParseServiceNode(id, out string address, out ushort pid))
            {
                continue;
            }

            cr = nodes.Locate(id, includeNonPresent: true, out uint devInst);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, cr, "Listed but could not be located."));
                continue;
            }

            cr = nodes.GetContainerId(devInst, out Guid container);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-container:" + id, cr));
                continue;
            }

            if (!NodeMatch.IsValidTargetContainer(container) || !seen.Add((address, container)))
            {
                continue;
            }

            found.Add(Describe(nodes, ids, address, container, pid, steps));
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Address, b.Address));
        return new AirPodsDiscoveryResult(true, found, steps);
    }

    // The device node of the address in that container: its name, and whether it is in the tree now.
    private static AirPodsCandidate Describe(INodeReader nodes, string[] ids, string address, Guid container, ushort pid, List<StepOutcome> steps)
    {
        string prefix = @"BTHENUM\DEV_" + address + @"\";
        foreach (string id in ids.Where(i => i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            uint cr = nodes.Locate(id, includeNonPresent: true, out uint devInst);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, cr, "Listed but could not be located."));
                continue;
            }

            cr = nodes.GetContainerId(devInst, out Guid nodeContainer);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-container:" + id, cr));
                continue;
            }

            if (nodeContainer != container)
            {
                continue;
            }

            string? name = null;
            uint nameCr = nodes.GetName(devInst, out name);
            if (nameCr is not (CfgMgr32.CR_SUCCESS or CfgMgr32.CR_NO_SUCH_VALUE))
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-name:" + id, nameCr));
                name = null;
            }

            uint presentCr = nodes.Locate(id, includeNonPresent: false, out _);
            if (presentCr is not (CfgMgr32.CR_SUCCESS or CfgMgr32.CR_NO_SUCH_DEVNODE))
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-locate:" + id, presentCr));
            }

            bool present = presentCr == CfgMgr32.CR_SUCCESS;
            return new AirPodsCandidate(address, container, pid, id, name, present);
        }

        return new AirPodsCandidate(address, container, pid, null, null, DevicePresent: false);
    }
}
