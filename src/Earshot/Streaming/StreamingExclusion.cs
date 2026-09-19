using Earshot.Contracts;

namespace Earshot.Streaming;

// Which device Play from a phone must never offer, enable or open: the one Earshot manages. Decided from what
// Windows reported about the device, never by reading anything out of its id.
//
// With a device pinned, the pinned container is the identity the rest of Earshot trusts, so that is the rule.
// With nothing pinned yet, or for a device Windows gave no container for, the only identity there is is the
// name rule the tray itself resolves its target with (NodeMatch.NameMatches), so that is used instead.
//
// This is a second fence, not the first. On the owner's PC the selector Windows supplies asks for enabled
// interfaces of devices that offer the A2DP source service. The owner's AirPods were recorded offering the A2DP
// sink service and not the source one, and at rest their device nodes are disabled, so they are not expected in
// the list to begin with. Expected is not proved, which is why the fence is here.
internal static class StreamingExclusion
{
    public static bool IsManagedDevice(StreamingDevice device, Guid pinnedContainer, string deviceMatch)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(deviceMatch);

        if (pinnedContainer != Guid.Empty && device.ContainerId != Guid.Empty)
        {
            return device.ContainerId == pinnedContainer;
        }

        return deviceMatch.Length > 0 && NodeMatch.NameMatches(device.DisplayName, deviceMatch);
    }
}
