using Earshot.Contracts;

namespace Earshot.Audio;

// One endpoint as read from Core Audio: the contract record plus PKEY_DeviceInterface_FriendlyName,
// the name of the adapter the endpoint is attached to, which is the best device name available.
// InterfaceName is null when the property was absent or could not be read.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-deviceinterface-friendlyname
internal sealed record EndpointReading(AudioEndpoint Endpoint, string? InterfaceName)
{
    public static EndpointReading Of(AudioEndpoint endpoint) => new(endpoint, InterfaceName: null);
}

// How the target device was chosen.
internal enum TargetResolution
{
    None,          // nothing usable is pinned and no group's name contains DeviceMatch
    Pinned,        // the pinned container is present
    NameMatch,     // nothing usable is pinned; the first group whose name contains DeviceMatch
    PinnedAbsent   // a container is pinned but has no endpoints; no other device is chosen
}

internal sealed record EndpointModel(DeviceSnapshot Snapshot, TargetResolution Resolution);

// Pure functions from endpoint readings to the device model. No Core Audio calls, no clock, no I/O.
//
// Grouping. Endpoints are grouped by PKEY_Device_ContainerId, which groups every devnode of one
// physical device. Every group is listed in AllGroups, including the PC container (internal
// adapters report {00000000-0000-0000-FFFF-FFFFFFFFFFFF}) and Guid.Empty (container unreadable), but
// neither is ever chosen as the target.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/container-ids
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/container-ids-for-bluetooth-devices
//
// Order. Groups that can be a target come first, named before nameless, by display name (ordinal,
// ignoring case) and then container id; the PC container and Guid.Empty groups follow. Endpoints within a group are ordered
// render first, then by endpoint id. The order is deterministic, so two snapshots of the same devices
// compare equal and "the first matching group" is stable across enumerations.
//
// Display name. The first readable PKEY_DeviceInterface_FriendlyName ("Owner’s AirPods Pro - Find
// My"), else the adapter part of the first readable PKEY_Device_FriendlyName, whose documented form is
// "Speakers (XYZ Audio Adapter)", else the whole friendly name, else "". Endpoints are tried render
// first, then ACTIVE, UNPLUGGED, DISABLED, NOTPRESENT. Names are used exactly as read, so the curly
// apostrophe U+2019 is kept.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-device-friendlyname
//
// Connection state, from the render endpoints, or from the capture endpoints when the group has no
// render endpoint with a known state:
//   any ACTIVE                       Connected     (only ACTIVE endpoints can stream)
//   else any UNPLUGGED               Disconnected  (paired, link down)
//   else any DISABLED                Unknown       (turned off in Sound settings; the link cannot be read)
//   else NOTPRESENT                  Disconnected  (adapter devnode removed or disabled, as a boot block does)
//   no endpoint with a known state   Unknown
// Render wins over capture, so the capture endpoint coming and going (Protect audio quality removes
// the Handsfree profile) never changes a state read from the render endpoint.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
//
// Target. When the pinned container is a valid target container, that container and nothing else: if
// none of its endpoints is present there is no target (PinnedAbsent), and the name is not tried. A
// Bluetooth device's container id is seeded from its MAC address, so a different container is always
// a different physical device, and a name match could only pick some other device (a phone whose name
// shares the match string) and report its state as the pinned one's.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/container-ids-for-bluetooth-devices
// When nothing usable is pinned, the first target-capable group whose display name, or the friendly name
// of one of its endpoints, contains DeviceMatch (ordinal, ignoring case, never a wildcard); otherwise
// none. A blank DeviceMatch matches nothing.
internal static class EndpointModelBuilder
{
    public static EndpointModel Build(
        IReadOnlyList<EndpointReading> readings, string? deviceMatch, Guid pinnedContainerId, DateTimeOffset takenUtc)
    {
        ArgumentNullException.ThrowIfNull(readings);

        List<DeviceModel> groups = readings
            .GroupBy(r => r.Endpoint.ContainerId)
            .Select(BuildGroup)
            .OrderBy(g => NodeMatch.IsValidTargetContainer(g.ContainerId) ? 0 : 1)
            .ThenBy(g => g.DisplayName.Length == 0 ? 1 : 0)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.ContainerId)
            .ToList();

        (DeviceModel? target, TargetResolution resolution) = ResolveTarget(groups, deviceMatch, pinnedContainerId);
        return new EndpointModel(new DeviceSnapshot(target, groups, takenUtc), resolution);
    }

    public static EndpointModel Build(
        IReadOnlyList<AudioEndpoint> endpoints, string? deviceMatch, Guid pinnedContainerId, DateTimeOffset takenUtc)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return Build(endpoints.Select(EndpointReading.Of).ToList(), deviceMatch, pinnedContainerId, takenUtc);
    }

    public static (DeviceModel? Target, TargetResolution Resolution) ResolveTarget(
        IReadOnlyList<DeviceModel> groups, string? deviceMatch, Guid pinnedContainerId)
    {
        ArgumentNullException.ThrowIfNull(groups);

        if (NodeMatch.IsValidTargetContainer(pinnedContainerId))
        {
            foreach (DeviceModel group in groups)
            {
                if (group.ContainerId == pinnedContainerId)
                {
                    return (group, TargetResolution.Pinned);
                }
            }

            // Never fall back to the name: see the class comment.
            return (null, TargetResolution.PinnedAbsent);
        }

        if (!string.IsNullOrWhiteSpace(deviceMatch))
        {
            foreach (DeviceModel group in groups)
            {
                if (NodeMatch.IsValidTargetContainer(group.ContainerId) && GroupMatches(group, deviceMatch))
                {
                    return (group, TargetResolution.NameMatch);
                }
            }
        }

        return (null, TargetResolution.None);
    }

    public static bool GroupMatches(DeviceModel group, string deviceMatch)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(deviceMatch))
        {
            return false;
        }

        return NodeMatch.NameMatches(group.DisplayName, deviceMatch) ||
               group.Endpoints.Any(e => NodeMatch.NameMatches(e.FriendlyName, deviceMatch));
    }

    public static ConnectionState DeriveConnection(IEnumerable<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        List<AudioEndpoint> known = endpoints.Where(e => IsKnownState(e.State)).ToList();

        List<AudioEndpoint> render = known.Where(e => e.Flow == EndpointFlow.Render).ToList();
        if (render.Count > 0)
        {
            return FromStates(render);
        }

        List<AudioEndpoint> capture = known.Where(e => e.Flow == EndpointFlow.Capture).ToList();
        return capture.Count > 0 ? FromStates(capture) : ConnectionState.Unknown;
    }

    public static string ChooseDisplayName(IReadOnlyList<EndpointReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        List<EndpointReading> ranked = readings
            .OrderBy(r => r.Endpoint.Flow == EndpointFlow.Render ? 0 : 1)
            .ThenBy(r => StateRank(r.Endpoint.State))
            .ThenBy(r => r.Endpoint.EndpointId, StringComparer.Ordinal)
            .ToList();

        foreach (EndpointReading reading in ranked)
        {
            if (!string.IsNullOrWhiteSpace(reading.InterfaceName))
            {
                return reading.InterfaceName.Trim();
            }
        }

        foreach (EndpointReading reading in ranked)
        {
            if (!string.IsNullOrWhiteSpace(reading.Endpoint.FriendlyName))
            {
                return AdapterPart(reading.Endpoint.FriendlyName.Trim());
            }
        }

        return "";
    }

    // "Headphones (Owner’s AirPods Pro - Find My)" gives "Owner’s AirPods Pro - Find My", and
    // "Speakers (Realtek(R) Audio)" gives "Realtek(R) Audio": the text between the first " (" and the
    // final ")". A name without that shape is returned whole.
    public static string AdapterPart(string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);
        int open = friendlyName.IndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || !friendlyName.EndsWith(')'))
        {
            return friendlyName;
        }

        string inner = friendlyName.Substring(open + 2, friendlyName.Length - open - 3).Trim();
        return inner.Length == 0 ? friendlyName : inner;
    }

    // True when the two snapshots describe the same devices, ignoring TakenUtc. Relies on Build's
    // deterministic order.
    public static bool AreEquivalent(DeviceSnapshot? a, DeviceSnapshot? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (!SameModel(a.Target, b.Target) || a.AllGroups.Count != b.AllGroups.Count)
        {
            return false;
        }

        for (int i = 0; i < a.AllGroups.Count; i++)
        {
            if (!SameModel(a.AllGroups[i], b.AllGroups[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsKnownState(EndpointState state) =>
        state is EndpointState.Active or EndpointState.Unplugged or EndpointState.Disabled or EndpointState.NotPresent;

    private static DeviceModel BuildGroup(IGrouping<Guid, EndpointReading> group)
    {
        List<EndpointReading> readings = group.ToList();
        List<AudioEndpoint> endpoints = readings
            .Select(r => r.Endpoint)
            .OrderBy(e => e.Flow == EndpointFlow.Render ? 0 : 1)
            .ThenBy(e => e.EndpointId, StringComparer.Ordinal)
            .ToList();

        return new DeviceModel(group.Key, ChooseDisplayName(readings), DeriveConnection(endpoints), endpoints);
    }

    private static ConnectionState FromStates(List<AudioEndpoint> endpoints)
    {
        if (endpoints.Any(e => e.State == EndpointState.Active))
        {
            return ConnectionState.Connected;
        }

        if (endpoints.Any(e => e.State == EndpointState.Unplugged))
        {
            return ConnectionState.Disconnected;
        }

        if (endpoints.Any(e => e.State == EndpointState.Disabled))
        {
            return ConnectionState.Unknown;
        }

        return ConnectionState.Disconnected;
    }

    private static int StateRank(EndpointState state) => state switch
    {
        EndpointState.Active => 0,
        EndpointState.Unplugged => 1,
        EndpointState.Disabled => 2,
        EndpointState.NotPresent => 3,
        _ => 4,
    };

    private static bool SameModel(DeviceModel? a, DeviceModel? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.ContainerId == b.ContainerId &&
               string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal) &&
               a.Connection == b.Connection &&
               a.Endpoints.SequenceEqual(b.Endpoints);
    }
}
