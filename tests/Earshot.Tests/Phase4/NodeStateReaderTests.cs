using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class NodeStateReaderTests
{
    [TestMethod]
    public void TheScanSelectsExactlyTheNineAirPodsNodes()
    {
        FakeNodeApi table = RecordedNodes.Table();

        NodeScanResult scan = NodeScan.FindTargets(table, RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        Assert.IsTrue(scan.Listed);
        Assert.IsEmpty(scan.Steps);
        CollectionAssert.AreEquivalent(RecordedNodes.AirPodsTargets, scan.Targets.Select(t => t.InstanceId).ToArray());
        Assert.IsTrue(scan.Targets.All(t => t.ContainerId == RecordedNodes.AirPodsContainer));
        Assert.HasCount(1, scan.Targets.Where(t => t.IsDeviceNode));
    }

    [TestMethod]
    public void TheScanNeverSelectsTheIPhoneOrTheRadioForTheAirPods()
    {
        FakeNodeApi table = RecordedNodes.Table();

        NodeScanResult scan = NodeScan.FindTargets(table, RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        string[] selected = scan.Targets.Select(t => t.InstanceId).ToArray();
        foreach (string id in RecordedNodes.IPhoneNodes.Concat(RecordedNodes.AirPodsNonTargets).Append(RecordedNodes.RadioNode))
        {
            CollectionAssert.DoesNotContain(selected, id);
        }
    }

    [TestMethod]
    public void ANodeWithTheAddressInAnotherContainerIsNotSelected()
    {
        var nodes = RecordedNodes.Table().Nodes.ToList();
        nodes.Add(new FakeNode(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000001", RecordedNodes.IPhoneContainer));
        var table = new FakeNodeApi(nodes);

        NodeScanResult scan = NodeScan.FindTargets(table, RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        Assert.HasCount(9, scan.Targets);
    }

    [TestMethod]
    public void AnUnreadableContainerIsNeverSelectedAndIsReported()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table[RecordedNodes.AirPodsDeviceNode].ContainerReadResult = CfgMgr32.CR_NO_SUCH_VALUE;

        NodeScanResult scan = NodeScan.FindTargets(table, RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        Assert.HasCount(8, scan.Targets);
        Assert.HasCount(1, scan.Steps);
        Assert.AreEqual("CR_NO_SUCH_VALUE", scan.Steps[0].CodeName);
        Assert.IsFalse(scan.Steps[0].Ok);
    }

    [TestMethod]
    public void IPhoneIdentitySelectsOnlyItsBthenumNodes()
    {
        NodeScanResult scan = NodeScan.FindTargets(RecordedNodes.Table(), RecordedNodes.IPhoneContainer, RecordedNodes.IPhoneAddress);

        Assert.HasCount(13, scan.Targets);
        Assert.IsTrue(scan.Targets.All(t => t.InstanceId.StartsWith(@"BTHENUM\", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("00000000-0000-0000-0000-000000000000", RecordedNodes.AirPodsAddress)]
    [DataRow("00000000-0000-0000-FFFF-FFFFFFFFFFFF", RecordedNodes.AirPodsAddress)]
    [DataRow("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D", "")]
    [DataRow("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D", "5A6b7C8d9Eaf")]
    [DataRow("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D", "300E431D04")]
    public void AnInvalidIdentitySelectsNothingAndListsNothing(string container, string address)
    {
        FakeNodeApi table = RecordedNodes.Table();
        table.ListResult = CfgMgr32.CR_FAILURE;

        NodeScanResult scan = NodeScan.FindTargets(table, new Guid(container), address);

        Assert.IsFalse(scan.Listed);
        Assert.IsEmpty(scan.Targets);
        Assert.AreEqual(NativeCodes.NotAttempted, scan.Steps.Single().Code);
    }

    [TestMethod]
    public void AListFailureIsReportedWithItsConfigRet()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table.ListResult = CfgMgr32.CR_BUFFER_SMALL;

        NodeScanResult scan = NodeScan.FindTargets(table, RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        Assert.IsFalse(scan.Listed);
        Assert.AreEqual("CR_BUFFER_SMALL", scan.Steps.Single().CodeName);
    }

    [TestMethod]
    public void ContainerNodesIncludeEveryEnumerator()
    {
        NodeScanResult scan = NodeScan.FindContainerNodes(RecordedNodes.Table(), RecordedNodes.AirPodsContainer);

        Assert.HasCount(13, scan.Targets);
        Assert.IsEmpty(scan.Steps);
    }

    [TestMethod]
    public void AContainerDumpReportsUnexpectedReadFailuresButNotMissingContainers()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table[RecordedNodes.RadioNode].ContainerReadResult = CfgMgr32.CR_NO_SUCH_VALUE;
        table[RecordedNodes.IPhoneDeviceNode].ContainerReadResult = 0x1Du;

        NodeScanResult scan = NodeScan.FindContainerNodes(table, RecordedNodes.AirPodsContainer);

        Assert.HasCount(13, scan.Targets);
        Assert.AreEqual("cm-container:" + RecordedNodes.IPhoneDeviceNode, scan.Steps.Single().Step);
        Assert.AreEqual("CR_REGISTRY_ERROR", scan.Steps.Single().CodeName);
    }

    [TestMethod]
    public void ReadsEnabledDisabledAndNonPresentNodes()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table[RecordedNodes.AirPodsTargets[0]].MarkDisabled(persistent: true);
        table[RecordedNodes.AirPodsTargets[1]].MarkDisabled(persistent: false);
        table[RecordedNodes.AirPodsTargets[2]].Present = false;
        table[RecordedNodes.AirPodsTargets[3]].StatusReadResult = CfgMgr32.CR_FAILURE;
        table[RecordedNodes.AirPodsTargets[4]].Status |= CfgMgr32.DN_HAS_PROBLEM;
        table[RecordedNodes.AirPodsTargets[4]].Problem = 10;

        NodeReadResult read = new NodeStateReader(table).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);
        Dictionary<string, BluetoothNode> byId = read.Nodes.ToDictionary(n => n.InstanceId);

        Assert.HasCount(9, read.Nodes);
        Assert.AreEqual(NodeBlockStatus.Disabled, byId[RecordedNodes.AirPodsTargets[0]].Status);
        Assert.IsTrue(byId[RecordedNodes.AirPodsTargets[0]].ConfigFlagsDisabledBit);
        Assert.AreEqual(22u, byId[RecordedNodes.AirPodsTargets[0]].ProblemCode);
        Assert.AreEqual(NodeBlockStatus.Disabled, byId[RecordedNodes.AirPodsTargets[1]].Status);
        Assert.IsFalse(byId[RecordedNodes.AirPodsTargets[1]].ConfigFlagsDisabledBit);
        Assert.IsFalse(byId[RecordedNodes.AirPodsTargets[2]].IsPresent);
        Assert.AreEqual(NodeBlockStatus.Unknown, byId[RecordedNodes.AirPodsTargets[2]].Status);
        Assert.AreEqual(NodeBlockStatus.Unknown, byId[RecordedNodes.AirPodsTargets[3]].Status);
        Assert.AreEqual(NodeBlockStatus.Enabled, byId[RecordedNodes.AirPodsTargets[4]].Status, "A problem other than 22 is not a disable.");
        Assert.AreEqual(10u, byId[RecordedNodes.AirPodsTargets[4]].ProblemCode);
        Assert.AreEqual("BTHENUM", byId[RecordedNodes.AirPodsDeviceNode].EnumeratorPrefix);
        Assert.AreEqual("Owner\u2019s AirPods Pro", byId[RecordedNodes.AirPodsDeviceNode].Name);
        Assert.IsTrue(read.Steps.Any(s => s.Step.StartsWith("cm-locate:", StringComparison.Ordinal) && s.CodeName == "CR_NO_SUCH_DEVNODE"));
        Assert.IsTrue(read.Steps.Any(s => s.Step.StartsWith("cm-status:", StringComparison.Ordinal) && s.CodeName == "CR_FAILURE"));
    }

    // A flag or a presence that could not be read leaves defaults on the node (flag clear, not present); those are
    // not observations, so the read names the node and neither "already allowed" nor "already blocked" holds.
    [TestMethod]
    public void ANodeWhoseFlagOrPresenceCannotBeReadIsNeverTakenAsAllowedOrBlocked()
    {
        FakeNodeApi flags = RecordedNodes.Table();
        flags[RecordedNodes.AirPodsTargets[0]].ConfigFlagsReadResult = CfgMgr32.CR_FAILURE;
        FakeNodeApi presence = RecordedNodes.Table();
        presence[RecordedNodes.AirPodsTargets[1]].PresentLocateResult = CfgMgr32.CR_FAILURE;
        FakeNodeApi status = RecordedNodes.Table();
        status[RecordedNodes.AirPodsTargets[2]].StatusReadResult = CfgMgr32.CR_FAILURE;
        FakeNodeApi clean = RecordedNodes.Table();

        foreach ((FakeNodeApi table, string id) in new[] { (flags, RecordedNodes.AirPodsTargets[0]), (presence, RecordedNodes.AirPodsTargets[1]), (status, RecordedNodes.AirPodsTargets[2]) })
        {
            NodeReadResult read = new NodeStateReader(table).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

            CollectionAssert.AreEqual(new[] { id }, read.Unread.ToArray());
            Assert.IsTrue(read.Steps.Any(s => !s.Ok && s.Step.EndsWith(":" + id, StringComparison.Ordinal)), "The failed read is a step.");
            Assert.IsFalse(BlockStateClassifier.IsFullyAllowed(read), id);
        }

        NodeReadResult readable = new NodeStateReader(clean).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);
        Assert.IsEmpty(readable.Unread);
        Assert.IsTrue(BlockStateClassifier.IsFullyAllowed(readable));

        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            flags[id].MarkDisabled(persistent: true);
        }

        flags[RecordedNodes.AirPodsTargets[0]].ConfigFlags = CfgMgr32.CONFIGFLAG_DISABLED;
        Assert.IsFalse(BlockStateClassifier.IsFullyBlocked(new NodeStateReader(flags).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress)),
            "An unread flag is not a persistent block either.");
    }

    private static BluetoothNode Node(bool present, NodeBlockStatus status, bool persist = false) =>
        new("id", "BTHENUM", null, present, status, status == NodeBlockStatus.Disabled ? 22u : 0u, persist);

    private static NodeReadResult Read(params BluetoothNode[] nodes) => new(true, nodes, Array.Empty<StepOutcome>());

    [TestMethod]
    public void ClassifiesEveryState()
    {
        BluetoothNode on = Node(true, NodeBlockStatus.Enabled);
        BluetoothNode off = Node(true, NodeBlockStatus.Disabled, persist: true);
        BluetoothNode gone = Node(false, NodeBlockStatus.Unknown);
        BluetoothNode unreadable = Node(true, NodeBlockStatus.Unknown);

        Assert.AreEqual(BlockState.NotSetUp, BlockStateClassifier.Classify(false, true, Read(off, off)));
        Assert.AreEqual(BlockState.NotFound, BlockStateClassifier.Classify(true, false, NodeReadResult.NoIdentity));
        Assert.AreEqual(BlockState.NotFound, BlockStateClassifier.Classify(true, true, Read()));
        Assert.AreEqual(BlockState.Unknown, BlockStateClassifier.Classify(true, true, new NodeReadResult(false, [], [])));
        Assert.AreEqual(BlockState.Allowed, BlockStateClassifier.Classify(true, true, Read(on, on, on)));
        Assert.AreEqual(BlockState.Blocked, BlockStateClassifier.Classify(true, true, Read(off, off, off)));
        Assert.AreEqual(BlockState.Mixed, BlockStateClassifier.Classify(true, true, Read(on, off)));
        Assert.AreEqual(BlockState.Mixed, BlockStateClassifier.Classify(true, true, Read(on, off, unreadable)));
        Assert.AreEqual(BlockState.Unknown, BlockStateClassifier.Classify(true, true, Read(gone, gone)));
        Assert.AreEqual(BlockState.Unknown, BlockStateClassifier.Classify(true, true, Read(off, unreadable)));
        Assert.AreEqual(BlockState.Blocked, BlockStateClassifier.Classify(true, true, Read(off, gone)), "Only present nodes decide.");
        Assert.AreEqual(BlockState.Allowed, BlockStateClassifier.Classify(true, true, Read(on, gone)));
    }

    [TestMethod]
    public void FullyBlockedNeedsThePersistentFlagOnEveryNode()
    {
        BluetoothNode persistent = Node(true, NodeBlockStatus.Disabled, persist: true);
        BluetoothNode temporary = Node(true, NodeBlockStatus.Disabled, persist: false);
        BluetoothNode gone = Node(false, NodeBlockStatus.Unknown);
        BluetoothNode goneFlagged = Node(false, NodeBlockStatus.Unknown, persist: true);

        Assert.IsTrue(BlockStateClassifier.IsFullyBlocked(Read(persistent, persistent, goneFlagged)));
        Assert.IsFalse(BlockStateClassifier.IsFullyBlocked(Read(persistent, temporary)));
        Assert.IsFalse(BlockStateClassifier.IsFullyBlocked(Read(persistent, gone)), "A node that is not present and not flagged comes back enabled.");
        Assert.IsFalse(BlockStateClassifier.IsFullyBlocked(Read(gone)));
        Assert.IsFalse(BlockStateClassifier.IsFullyBlocked(new NodeReadResult(false, [persistent], [])));
        Assert.IsTrue(BlockStateClassifier.IsFullyAllowed(Read(Node(true, NodeBlockStatus.Enabled), gone)));
        Assert.IsFalse(BlockStateClassifier.IsFullyAllowed(Read(Node(true, NodeBlockStatus.Enabled), goneFlagged)), "It would come back disabled.");
        Assert.IsFalse(BlockStateClassifier.IsFullyAllowed(Read(Node(true, NodeBlockStatus.Enabled, persist: true))));
    }

    // One rule for the check before a change and the read after it: the same nodes always give the same answer.
    [TestMethod]
    public void AnUnresolvedNodeIsOneThatComesBackInTheWrongState()
    {
        BluetoothNode gone = Node(false, NodeBlockStatus.Unknown);
        BluetoothNode goneFlagged = Node(false, NodeBlockStatus.Unknown, persist: true);
        BluetoothNode present = Node(true, NodeBlockStatus.Enabled);

        Assert.IsTrue(BlockStateClassifier.AnyUnresolvedForBlock(Read(present, gone)));
        Assert.IsFalse(BlockStateClassifier.AnyUnresolvedForBlock(Read(present, goneFlagged)));
        Assert.IsTrue(BlockStateClassifier.AnyUnresolvedForAllow(Read(present, goneFlagged)));
        Assert.IsFalse(BlockStateClassifier.AnyUnresolvedForAllow(Read(present, gone)));
    }
}
