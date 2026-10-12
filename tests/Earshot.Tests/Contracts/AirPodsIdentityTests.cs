using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

// The one rule for "these are the owner's AirPods": a BTHENUM service node with the Apple vendor and an AirPods product id.
// Every Apple device shares the vendor id, so the tests below are about what must NOT match.
[TestClass]
public sealed class AirPodsIdentityTests
{
    private static readonly string[] BothAddresses = ["0A1B2C3D4E8C", "AA11BB22CC33"];

    private const string AirPodsServiceNode =
        @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000";

    [TestMethod]
    public void TheSetHoldsOnlyTheProductIdSeenOnThisPc()
    {
        CollectionAssert.AreEquivalent(new ushort[] { 0x2027 }, AirPodsModels.Pids.ToArray());
    }

    [TestMethod]
    public void NoBeatsOrOtherApplePidIsInTheSet()
    {
        // Product ids other Apple audio devices could carry. None may be taken for AirPods: Beats and AirPods share the
        // vendor id 004C, and only 0x2027 has been read from the owner's AirPods.
        ushort[] others = [0x0000, 0x2006, 0x2011, 0x2012, 0x2014, 0x2016, 0x2024, 0x2028, 0x200E, 0x200F, 0x2013];

        Assert.IsFalse(others.Any(AirPodsModels.IsAirPods));
        Assert.IsFalse(AirPodsModels.Pids.Any(pid => others.Contains(pid)));
    }

    [TestMethod]
    public void AnAirPodsServiceNodeMatchesAndGivesItsAddress()
    {
        Assert.IsTrue(AirPodsIdentity.IsAirPodsServiceNode(AirPodsServiceNode));
        Assert.IsTrue(AirPodsIdentity.TryParseServiceNode(AirPodsServiceNode, out string address, out ushort pid));
        Assert.AreEqual("0A1B2C3D4E8C", address);
        Assert.AreEqual((ushort)0x2027, pid);
    }

    [TestMethod]
    public void TheMatchIgnoresCaseOfTheIdAndGivesAnUpperCaseAddress()
    {
        Assert.IsTrue(AirPodsIdentity.TryParseServiceNode(AirPodsServiceNode.ToLowerInvariant(), out string address, out _));
        Assert.AreEqual("0A1B2C3D4E8C", address);
    }

    [TestMethod]
    public void NoNodeOfThePairedIPhoneMatches()
    {
        foreach (string id in RecordedNodes.IPhoneNodes)
        {
            Assert.IsFalse(AirPodsIdentity.IsAirPodsServiceNode(id), id);
        }
    }

    [TestMethod]
    public void AnAppleHeadsetWithAnotherProductIdDoesNotMatch()
    {
        // The second pair of headphones in the recorded table carries the Apple vendor id and product id 2028.
        foreach (string id in RecordedNodes.HeadphonesNodes)
        {
            Assert.IsFalse(AirPodsIdentity.IsAirPodsServiceNode(id), id);
        }

        const string beats = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2006\b&1a2b3c4d&0&AABBCCDDEE01_C00000000";
        Assert.IsFalse(AirPodsIdentity.IsAirPodsServiceNode(beats));
    }

    [TestMethod]
    [DataRow(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001005D_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000", "another vendor")]
    [DataRow(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0002004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000", "another id source")]
    [DataRow(@"BTHENUM\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C", "the device node")]
    [DataRow(@"SWD\MMDEVAPI\{0.0.0.00000000}.{55555555-6666-4777-8888-999999999901}", "an audio endpoint")]
    [DataRow(@"BTH\MS_BTHBRB\a&3c4d5e6&0&1", "the radio")]
    [DataRow(@"USB\VID_004C&PID_2027\5&12345&0&1", "a USB device")]
    [DataRow(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&20270\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000", "a longer product id")]
    [DataRow("", "empty")]
    public void NothingElseMatches(string id, string why)
    {
        Assert.IsFalse(AirPodsIdentity.IsAirPodsServiceNode(id), why);
    }

    [TestMethod]
    public void AContainerIsAirPodsOnlyForItsOwnAddress()
    {
        string[] ids = [AirPodsServiceNode, RecordedNodes.AirPodsDeviceNode];

        Assert.IsTrue(AirPodsIdentity.ContainerIsAirPods(ids, RecordedNodes.AirPodsAddress));
        Assert.IsFalse(AirPodsIdentity.ContainerIsAirPods(ids, RecordedNodes.IPhoneAddress));
        Assert.IsFalse(AirPodsIdentity.ContainerIsAirPods(RecordedNodes.IPhoneNodes, RecordedNodes.IPhoneAddress));
    }

    [TestMethod]
    public void DiscoveryFindsTheAirPodsAndNeverThePhoneOrTheHeadphones()
    {
        FakeNodeApi table = RecordedNodes.TableWithHeadphones();

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(table);

        Assert.IsTrue(found.Listed);
        AirPodsCandidate only = found.Candidates.Single();
        Assert.AreEqual(RecordedNodes.AirPodsAddress, only.Address);
        Assert.AreEqual(RecordedNodes.AirPodsContainer, only.ContainerId);
        Assert.AreEqual((ushort)0x2027, only.Pid);
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, only.DeviceNodeId);
        Assert.IsTrue(only.DevicePresent);
        Assert.IsEmpty(table.Calls, "Discovery reads and changes nothing.");
    }

    [TestMethod]
    public void DiscoveryCountsNodesThatAreNotPresentAndNamesTheDevice()
    {
        FakeNodeApi table = RecordedNodes.Table();
        foreach (FakeNode node in table.Nodes.Where(n => n.Container == RecordedNodes.AirPodsContainer))
        {
            node.Present = false;
        }

        AirPodsCandidate only = AirPodsDiscovery.Find(table).Candidates.Single();

        Assert.IsFalse(only.DevicePresent);
        Assert.IsNotNull(only.Name);
    }

    [TestMethod]
    public void DiscoveryOfAReplacementPairGivesTwoCandidates()
    {
        // A new pairing with a new address and container, beside the old one that was never removed.
        var nodes = RecordedNodes.Table().Nodes.ToList();
        var newContainer = new Guid("7e2a9c40-1b3d-4f5e-8a6b-0c1d2e3f4a5b");
        nodes.Add(new FakeNode(@"BTHENUM\DEV_AA11BB22CC33\b&1a2b3c4d&0&BLUETOOTHDEVICE_AA11BB22CC33", newContainer, "AirPods Pro"));
        nodes.Add(new FakeNode(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&AA11BB22CC33_C00000000", newContainer));

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(new FakeNodeApi(nodes));

        CollectionAssert.AreEqual(BothAddresses, found.Candidates.Select(c => c.Address).ToArray());
    }

    [TestMethod]
    public void AContainerThatCannotBeReadIsAStepNotACandidate()
    {
        FakeNodeApi table = RecordedNodes.Table();
        foreach (FakeNode node in table.Nodes.Where(n => AirPodsIdentity.IsAirPodsServiceNode(n.InstanceId)))
        {
            node.ContainerReadResult = Earshot.Interop.CfgMgr32.CR_FAILURE;
        }

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(table);

        Assert.IsTrue(found.Listed);
        Assert.IsEmpty(found.Candidates);
        Assert.IsTrue(found.Steps.Any(s => !s.Ok && s.Step.StartsWith("cm-container:", StringComparison.Ordinal)), "The raw code is surfaced.");
    }

    [TestMethod]
    public void AListThatCannotBeReadIsNotListed()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table.ListResult = Earshot.Interop.CfgMgr32.CR_FAILURE;

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(table);

        Assert.IsFalse(found.Listed);
        Assert.IsEmpty(found.Candidates);
    }
}
