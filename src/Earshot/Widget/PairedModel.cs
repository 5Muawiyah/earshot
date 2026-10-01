using System.Globalization;
using System.Text.RegularExpressions;
using Earshot.Contracts;

namespace Earshot.Widget;

// What reading the paired AirPods' model found: the model, or why there is none. Steps carries every failing
// read's raw code, so a missing model is never silent.
internal sealed record PairedModelRead(ushort? Model, IReadOnlyList<StepOutcome> Steps);

// The model of the AirPods Windows has paired, as the product id Windows holds for them. A source never throws
// for anything Windows did. The real one reads device nodes with CfgMgr32 and so lives outside this namespace:
// nothing under Earshot.Widget may reach a device path, and listing nodes is the nearest thing to one.
internal interface IPairedModelSource
{
    // container and address12 are the pinned device's. An invalid pair reads as no model.
    PairedModelRead Read(Guid container, string address12);
}

// The product id inside a device node's instance id.
internal static partial class PairedModelParser
{
    // The bus-assigned id of a Bluetooth service node carries the vendor id source and vendor id, then the
    // product id: ..._VID&0001004C_PID&2027. Source 0001 is the Bluetooth SIG's list and 004C the vendor id Apple
    // holds there; any other source or vendor is not the AirPods' own model and is ignored.
    [GeneratedRegex(@"_VID&0001004C_PID&(?<pid>[0-9A-F]{4})(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProductId();

    // The product id in an instance id, or null. A product id of 0000 is "not known" and is no model.
    public static ushort? ParseModel(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        Match match = ProductId().Match(instanceId);
        if (!match.Success)
        {
            return null;
        }

        ushort pid = ushort.Parse(match.Groups["pid"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return pid == 0 ? null : pid;
    }
}
