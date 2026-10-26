using System.Globalization;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Repair;

namespace Earshot.App;

// Whether the AirPods Earshot has pinned are still paired with this PC. A pair removed in Windows and paired again is a new
// container, so the pin names something that no longer exists: Connect goes to it ("These AirPods need pairing again"), and
// set-up sends its address and container to the gate, which finds no such device. Read only.
//
//   NotPinned  nothing valid is pinned, so there is nothing to be stale
//   NotStale   the pinned container is in Windows' paired list, or a node of the pinned address is in the tree now
//   Stale      neither: the pairing is gone
//   Unknown    a read failed. Not knowing is never stale: dropping a pin and the readings kept for it on a failed read would be
//              worse than leaving a stale one for the next look.
//
// A node left behind by a removed pairing (not present) is not a pairing, so it does not keep a pin alive; the paired list is
// what says a device is paired. This is where this differs from AirPodsDiscovery's own listing, which counts those nodes.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothdevice.getdeviceselectorfrompairingstate
internal enum StalePinState
{
    NotPinned,
    NotStale,
    Stale,
    Unknown,
}

// Another set of AirPods that is paired now: what the person is offered in place of a stale pin.
internal sealed record OtherAirPods(string Name, string Address12, Guid ContainerId);

internal sealed record StalePinResult(StalePinState State, IReadOnlyList<OtherAirPods> Others, IReadOnlyList<StepOutcome> Steps)
{
    public bool IsStale => State == StalePinState.Stale;
}

internal static class StalePin
{
    // The step the paired list reports its read under; the whole read failed when it is not ok.
    private const string PairListStep = "pair-list";

    // How long the paired list is given. A waiting budget, not a measured figure: the look holds the coordinator's start checks
    // and a failed connect's card, so it must not wait for as long as Windows takes.
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    public static async Task<StalePinResult> ReadAsync(IPairingApi api, INodeReader nodes, Guid pinnedContainer, string pinnedAddress, CancellationToken ct, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(pinnedAddress);
        if (!NodeMatch.IsValidTargetContainer(pinnedContainer))
        {
            return new StalePinResult(StalePinState.NotPinned, [], []);
        }

        var steps = new List<StepOutcome>();
        PairedListResult paired;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(budget ?? DefaultBudget);
            try
            {
                paired = await api.ListPairedAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                steps.Add(StepOutcomes.FromWin32(PairListStep, 1460, "The paired list did not answer within " + (budget ?? DefaultBudget).TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms.", ok: false));
                return new StalePinResult(StalePinState.Unknown, [], steps);
            }
        }
        steps.AddRange(paired.Steps.Where(s => !s.Ok));
        if (paired.Steps.Any(s => s.Step == PairListStep && !s.Ok))
        {
            return new StalePinResult(StalePinState.Unknown, [], steps);
        }

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(nodes);
        steps.AddRange(found.Steps.Where(s => !s.Ok));
        if (!found.Listed)
        {
            return new StalePinResult(StalePinState.Unknown, [], steps);
        }

        bool Pinned(string address, Guid container) =>
            container == pinnedContainer || (pinnedAddress.Length > 0 && string.Equals(address, pinnedAddress, StringComparison.OrdinalIgnoreCase));

        bool stillPaired = paired.Devices.Any(d => Pinned(d.Address12, d.ContainerId));
        bool inTheTree = found.Candidates.Any(c => c.DevicePresent && Pinned(c.Address, c.ContainerId));
        if (stillPaired || inTheTree)
        {
            return new StalePinResult(StalePinState.NotStale, [], steps);
        }

        // The others: sets that pass the identity rule, are in the tree and are in the paired list as well.
        List<OtherAirPods> others = found.Candidates
            .Where(c => c.DevicePresent)
            .Select(c => (Candidate: c, Device: paired.Devices.FirstOrDefault(d => string.Equals(d.Address12, c.Address, StringComparison.OrdinalIgnoreCase) && d.ContainerId == c.ContainerId)))
            .Where(x => x.Device is not null)
            .Select(x => new OtherAirPods(NameOf(x.Candidate, x.Device!), x.Candidate.Address, x.Candidate.ContainerId))
            .ToList();
        return new StalePinResult(StalePinState.Stale, others, steps);
    }

    private static string NameOf(AirPodsCandidate candidate, PairedDevice device) =>
        !string.IsNullOrWhiteSpace(device.Name) ? device.Name : !string.IsNullOrWhiteSpace(candidate.Name) ? candidate.Name : "AirPods";

    // One log line for the result; numbers and states only, never a name or an address.
    public static string Describe(StalePinResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "pinned AirPods: " + result.State + (result.IsStale ? ", " + result.Others.Count.ToString(CultureInfo.InvariantCulture) + " other pair(s) paired" : "") + ".";
    }
}
