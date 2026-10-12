using System.Text.RegularExpressions;

namespace Earshot.Contracts;

// The product ids Earshot counts as AirPods. A Bluetooth service node of Apple's carries the id as
// ..._VID&0001004C_PID&xxxx (vendor id source 0001 is the Bluetooth SIG's list, 004C the vendor id Apple holds
// there). Every Apple device shares that vendor id, so the vendor alone says nothing: an iPhone shows PID&0000 and
// Beats headphones carry product ids of their own. Only a product id read from the owner's AirPods on this PC is
// listed here. The advertisement model table at github.com/furiousMAC/continuity (messages/proximity_pairing.md)
// lists more models, but it is in advertisement byte order, and only 0x2027 has been seen to match the product id
// in a node id, so no other value is taken from it.
public static class AirPodsModels
{
    // 0x2027: the product id in the node ids and the broadcast of the owner's own AirPods.
    public const ushort ObservedOnThisPc = 0x2027;

    public static readonly IReadOnlySet<ushort> Pids = new HashSet<ushort> { ObservedOnThisPc };

    public static bool IsAirPods(ushort pid) => Pids.Contains(pid);
}

// A paired device that passes the AirPods identity rule.
//   Address       the 12 upper-case hex characters of the Bluetooth address, read from the service node's id
//   ContainerId   the container of the service nodes (never the PC container)
//   Pid           the product id the rule matched
//   DeviceNodeId  the BTHENUM\DEV_ node of the address, or null when there is none in the list
//   Name          that node's name, or null when it has none or it could not be read
//   DevicePresent the device node is in the tree now (a removed pairing leaves non-present nodes behind)
public sealed record AirPodsCandidate(
    string Address, Guid ContainerId, ushort Pid, string? DeviceNodeId, string? Name, bool DevicePresent);

// The one rule for "these are the owner's AirPods". A device is AirPods only when its container holds a BTHENUM
// service node whose id carries the Apple vendor and a product id in AirPodsModels.Pids. Names are never used to
// decide, and neither is the vendor id alone.
public static partial class AirPodsIdentity
{
    // BTHENUM\{service uuid}_VID&0001004C_PID&xxxx\<bus id>&<12 hex address>_C<8 hex>
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-instance-ids
    [GeneratedRegex(
        @"^BTHENUM\\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}_VID&0001004C_PID&(?<pid>[0-9A-F]{4})\\[^\\]*?(?<address>[0-9A-F]{12})_C[0-9A-F]{8}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNode();

    // True when the instance id is an Apple BTHENUM service node with an AirPods product id.
    public static bool IsAirPodsServiceNode(string instanceId) => TryParseServiceNode(instanceId, out _, out _);

    // The address and product id of an AirPods service node id, or false for any other id (an iPhone's, a Beats
    // device's, a node of another vendor, a device node, an endpoint).
    public static bool TryParseServiceNode(string instanceId, out string address, out ushort pid)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        address = "";
        pid = 0;
        Match match = ServiceNode().Match(instanceId);
        if (!match.Success)
        {
            return false;
        }

        pid = ushort.Parse(match.Groups["pid"].Value, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
        if (!AirPodsModels.IsAirPods(pid))
        {
            return false;
        }

        address = match.Groups["address"].Value.ToUpperInvariant();
        return true;
    }

    // True when any of the ids is an AirPods service node whose address is this one.
    public static bool ContainerIsAirPods(IEnumerable<string> containerNodeIds, string address12)
    {
        ArgumentNullException.ThrowIfNull(containerNodeIds);
        return containerNodeIds.Any(id => TryParseServiceNode(id, out string found, out _) && string.Equals(found, address12, StringComparison.Ordinal));
    }
}
