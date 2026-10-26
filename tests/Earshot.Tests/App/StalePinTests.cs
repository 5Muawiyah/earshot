using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Earshot.Tests.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// A pin that names a pair Windows no longer holds. The paired list and the nodes in the tree both have to say the pair is gone;
// a read that failed says nothing, and a node left behind by a removed pairing is not a pairing.
[TestClass]
public sealed class StalePinTests
{
    private static Task<StalePinResult> Read(FixRig rig, Guid? container = null) =>
        StalePin.ReadAsync(rig.Pairing, rig.Nodes, container ?? RecordedNodes.AirPodsContainer, CancellationToken.None);

    private static PairedDevice OwnersPair() => new("paired-old", "AirPods Pro", RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer);

    private static void RemovePairing(FixRig rig)
    {
        rig.Pairing.Paired.RemoveAll(p => p.Id == "paired-old");
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            rig.Gate.Nodes[id].Present = false;
        }
    }

    // A paired list that does not answer within the budget says nothing, with the timeout's code, instead of holding the tray's
    // look (and the coordinator's start checks behind it) for as long as Windows takes.
    [TestMethod]
    public async Task APairedListThatDoesNotAnswerInTimeIsUnknownWithATimeoutCode()
    {
        using var rig = new FixRig();
        rig.Pairing.HangOnList = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        StalePinResult result = await StalePin.ReadAsync(rig.Pairing, rig.Nodes, RecordedNodes.AirPodsContainer, CancellationToken.None, TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(StalePinState.Unknown, result.State);
        Assert.IsTrue(result.Steps.Any(s => s.Code == 1460 && !s.Ok && s.CodeName == "earshot-timeout"), string.Join(", ", result.Steps.Select(s => s.Step + " " + s.Code + " " + s.CodeName)));
        Assert.IsLessThan(5000L, watch.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task APairStillInTheListIsNotStale()
    {
        using var rig = new FixRig();
        rig.Pairing.Paired.Add(OwnersPair());

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.NotStale, result.State);
    }

    [TestMethod]
    public async Task ARemovedPairWithNothingInTheTreeIsStale()
    {
        using var rig = new FixRig();
        RemovePairing(rig);

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.Stale, result.State);
        Assert.IsEmpty(result.Others);
    }

    [TestMethod]
    public async Task ASetStillInTheTreeIsNotStaleWhateverTheListSays()
    {
        using var rig = new FixRig();

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.NotStale, result.State, "The device node is in the tree even though the list holds nothing.");
    }

    [TestMethod]
    public async Task ANodeLeftBehindByARemovedPairingDoesNotKeepThePinAlive()
    {
        using var rig = new FixRig();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            rig.Gate.Nodes[id].Present = false;
        }

        // The nodes are still listed (phantoms), but none is in the tree and the list holds no such device.
        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.Stale, result.State);
    }

    [TestMethod]
    public async Task AFailedPairedListIsNeverStale()
    {
        using var rig = new FixRig();
        RemovePairing(rig);
        rig.Pairing.ListSteps.Add(StepOutcomes.FromHResult("pair-list", unchecked((int)0x8007001F), "COMException"));

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.Unknown, result.State);
        Assert.IsFalse(result.IsStale);
    }

    [TestMethod]
    public async Task AFailedNodeListIsNeverStale()
    {
        using var rig = new FixRig();
        RemovePairing(rig);
        rig.Gate.Nodes.ListResult = CfgMgr32.CR_FAILURE;

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.Unknown, result.State);
    }

    [TestMethod]
    public async Task OneUnreadableDeviceInTheListDoesNotMakeEveryReadUnknown()
    {
        using var rig = new FixRig();
        RemovePairing(rig);
        rig.Pairing.ListSteps.Add(StepOutcomes.NotAttempted("pair-list-device", "A paired device had no readable address or container, so it is not matched."));
        rig.Pairing.ListSteps.Add(StepOutcomes.FromHResult("pair-list", 0, "1 paired device(s)."));

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.Stale, result.State);
    }

    [TestMethod]
    public async Task NothingPinnedIsNotAStalePin()
    {
        using var rig = new FixRig();
        RemovePairing(rig);

        StalePinResult result = await Read(rig, container: Guid.Empty);

        Assert.AreEqual(StalePinState.NotPinned, result.State);
        Assert.IsEmpty(rig.Pairing.Calls, "Nothing is read when there is nothing to check.");
    }

    // The same AirPods removed in Windows and paired again: the address is the same and the container is new, so the pin names
    // a container that is gone and Connect would go to it. Stale, with the new pairing offered in its place.
    [TestMethod]
    public async Task TheSameAirPodsPairedAgainUnderANewContainerAreStaleAndOffered()
    {
        using var rig = new FixRig();
        RemovePairing(rig);
        var removedContainer = new Guid("11111111-2222-4333-8444-555555555503");
        rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
        rig.Pairing.Paired.Add(new PairedDevice("paired-again", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));

        StalePinResult result = await Read(rig, container: removedContainer);

        Assert.AreEqual(StalePinState.Stale, result.State);
        Assert.AreEqual(FixRig.NewAirPodsContainer, result.Others.Single().ContainerId);
    }

    [TestMethod]
    public async Task AnotherPairOfAirPodsThatIsPairedIsOfferedAndAStrangersDeviceIsNot()
    {
        using var rig = new FixRig();
        RemovePairing(rig);
        rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.SecondAirPodsNodes()));
        rig.Pairing.Paired.Add(new PairedDevice("paired-second", "Second AirPods", FixRig.SecondAirPodsAddress, FixRig.SecondAirPodsContainer));
        rig.Pairing.Paired.Add(new PairedDevice("paired-speaker", "Speaker", "AABBCCDDEEFF", Guid.NewGuid()));

        StalePinResult result = await Read(rig);

        Assert.AreEqual(StalePinState.Stale, result.State);
        OtherAirPods other = result.Others.Single();
        Assert.AreEqual("Second AirPods", other.Name);
        Assert.AreEqual(FixRig.SecondAirPodsAddress, other.Address12);
        Assert.AreEqual(FixRig.SecondAirPodsContainer, other.ContainerId);
    }

    [TestMethod]
    public async Task TheLogLineNamesNoDeviceAndNoAddress()
    {
        using var rig = new FixRig();
        RemovePairing(rig);

        string line = StalePin.Describe(await Read(rig));

        Assert.IsFalse(line.Contains(RecordedNodes.AirPodsAddress, StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(line, "Stale");
    }
}
