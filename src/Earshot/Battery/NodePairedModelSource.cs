using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Widget;

namespace Earshot.Battery;

// The paired AirPods' product id from the device nodes of the pinned device, read with CfgMgr32 and nothing
// else. Read-only: the nodes are listed (phantom included) and their container read, and nothing is enabled,
// disabled or connected. It lives here and not under Earshot.Widget because the widget's own types may not reach a
// device path, and listing device nodes is one.
// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw
internal sealed class NodePairedModelSource(INodeReader nodes) : IPairedModelSource
{
    public NodePairedModelSource()
        : this(new CfgMgr32NodeReader())
    {
    }

    public PairedModelRead Read(Guid container, string address12)
    {
        NodeScanResult scan = NodeScan.FindTargets(nodes, container, address12);
        var failures = scan.Steps.Where(s => !s.Ok).ToList();
        if (!scan.Listed)
        {
            return new PairedModelRead(null, failures);
        }

        foreach (TargetNode target in scan.Targets)
        {
            if (PairedModelParser.ParseModel(target.InstanceId) is ushort found)
            {
                return new PairedModelRead(found, failures);
            }
        }

        return new PairedModelRead(null, failures);
    }
}
