using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The allow from each state the AirPods' nodes can be in, and the block that leaves none of them able to come back
// enabled. The AirPods' audio nodes (A2DP sink, AVRCP, Hands-Free) left the tree while the device node was disabled and kept
// CONFIGFLAG_DISABLED, so the allow that only touched nodes in the tree could never finish.
[TestClass]
public sealed class AllowFromEveryStateTests
{
    private static readonly string[] AudioNodes =
    [
        RecordedNodes.AirPodsTargets[4], // AVRCP 110C
        RecordedNodes.AirPodsTargets[6], // AVRCP 110E
        RecordedNodes.AirPodsTargets[7], // Hands-Free 111E
        RecordedNodes.AirPodsTargets[8], // A2DP sink 110B
    ];

    private static readonly string[] FiveDisablesThenFourFlags =
        ["disable", "disable", "disable", "disable", "disable", "set-flag", "set-flag", "set-flag", "set-flag"];

    private static GateRig RigWithAudioNodesAway(TimeSpan? appearAfter, bool flagged = true)
    {
        var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());
        rig.BlockAirPodsAtRest();
        foreach (string id in AudioNodes)
        {
            FakeNode node = rig.Nodes[id];
            node.Present = false;
            node.AppearsAfter = appearAfter;
            if (!flagged)
            {
                node.ConfigFlags = 0;
                node.Status = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE;
                node.Problem = 0;
            }
        }

        return rig;
    }

    [TestMethod]
    public void NodesInTheTreeAtProblem22AreEnabledDeviceNodeFirstAndEachIsListedBeforeItIsTouched()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());
        rig.BlockAirPodsAtRest();
        string nonce = rig.NextNonce();
        var seenBefore = new List<(string Id, bool ListedAlready, string Result)>();
        rig.Nodes.BeforeEnable = id =>
        {
            GateRead<GateStatusFile> inProgress = rig.Store.ReadStatus(nonce);
            seenBefore.Add((id, inProgress.IsOk && inProgress.Value!.Changed!.Any(c => c.Id == id), inProgress.Value?.Result ?? "none"));
        };

        GateExitCode exit = rig.Run(GateVerbs.Allow, nonce: nonce);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, rig.Nodes.Calls);
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, rig.Nodes.Calls[0].InstanceId, "The device node goes first.");
        Assert.IsTrue(seenBefore.All(s => s.ListedAlready), "The change list was on disk before each node was touched: " + string.Join(", ", seenBefore));
        Assert.IsTrue(seenBefore.All(s => s.Result == "in-progress"));
        GateStatusFile status = rig.Status(nonce);
        Assert.AreEqual("success", status.Result);
        Assert.HasCount(9, status.Changed!);
        Assert.IsTrue(status.Changed!.All(c => c.From == GateChangeFrom.DisabledPresent));
        CollectionAssert.AreEqual(rig.Nodes.Calls.Select(c => c.InstanceId).ToArray(), status.Changed!.Select(c => c.Id).ToArray());
    }

    [TestMethod]
    public void AudioNodesAwayWithTheFlagThatAppearAfterTheDeviceNodeIsEnabledAreEnabledAndTheFlagIsNeverCleared()
    {
        using GateRig rig = RigWithAudioNodesAway(TimeSpan.FromSeconds(3));

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Success, exit, "Old code stopped at 'Not present and still marked disabled' and left them at problem 22.");
        Assert.IsEmpty(rig.Calls("clear-flag"), "The allow never clears a flag on a node that is away.");
        Assert.IsEmpty(rig.Calls("enable-phantom"));
        Assert.HasCount(9, rig.Calls("enable"));
        foreach (string id in AudioNodes)
        {
            Assert.IsFalse(rig.Nodes[id].IsDisabled, id);
            Assert.AreEqual(0u, rig.Nodes[id].ConfigFlags & CfgMgr32.CONFIGFLAG_DISABLED, id);
        }

        GateStatusFile status = rig.Status();
        Assert.HasCount(4, status.Changed!.Where(c => c.From == GateChangeFrom.FlaggedAbsent));
        Assert.HasCount(4, status.Steps.Where(s => s.Step.StartsWith("cm-appear:", StringComparison.Ordinal) && s.Ok));
        Assert.Contains("ms after", status.Steps.First(s => s.Step.StartsWith("cm-appear:", StringComparison.Ordinal)).Detail!, "The measured time to appear is recorded.");
        Assert.AreEqual(nameof(BlockState.Allowed), status.State);
    }

    [TestMethod]
    public void TheWaitEndsAsSoonAsEveryNodeIsBackAndNeverUsesARealSleep()
    {
        using GateRig rig = RigWithAudioNodesAway(TimeSpan.FromSeconds(3));

        rig.Run(GateVerbs.Allow);

        double waited = rig.Nodes.Pauses.Sum(p => p.TotalSeconds);
        Assert.IsGreaterThanOrEqualTo(3.0, waited);
        Assert.IsLessThan(3.5, waited, "It stops polling once the last node is back.");
    }

    [TestMethod]
    public void ANodeThatNeverAppearsAndIsStillFlaggedIsAPartialAllowAndNothingIsCalledOnIt()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null);

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Partial, exit);
        Assert.IsEmpty(rig.Calls("enable-phantom"));
        Assert.IsEmpty(rig.Calls("clear-flag"));
        Assert.AreEqual(10.0, rig.Nodes.Pauses.Sum(p => p.TotalSeconds), 0.001, "It waits the whole budget, polling every 100 ms.");
        GateStatusFile status = rig.Status();
        Assert.HasCount(4, status.Steps.Where(s => !s.Ok && s.Step.StartsWith("cm-enable:", StringComparison.Ordinal) && s.Detail!.Contains("comes back disabled", StringComparison.Ordinal)));
        Assert.HasCount(5, status.Changed!, "Only the five nodes in the tree were changed.");
    }

    [TestMethod]
    public void NodesAwayWithoutTheFlagStartEnabledAndAreNotWaitedFor()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null, flagged: false);

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsEmpty(rig.Nodes.Pauses, "There is nothing to wait for when no node that is away carries the flag.");
        Assert.HasCount(5, rig.Calls("enable"));
    }

    [TestMethod]
    public void WithNoNodeInTheTreeNothingIsWaitedFor()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());
        rig.BlockAirPodsAtRest();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            rig.Nodes[id].Present = false;
        }

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsEmpty(rig.Nodes.Pauses);
        Assert.IsEmpty(rig.Nodes.Calls);
    }

    [TestMethod]
    public void APauseThatEndsTheWaitEndsIt()
    {
        using GateRig rig = RigWithAudioNodesAway(TimeSpan.FromSeconds(3));
        rig.Nodes.StopPausing = true;

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Partial, exit);
        Assert.HasCount(1, rig.Nodes.Pauses);
    }

    [TestMethod]
    public void AnEnableThatFailsOnAReportedNodeIsRecordedWithItsCode()
    {
        using GateRig rig = RigWithAudioNodesAway(TimeSpan.FromSeconds(1));
        rig.Nodes[AudioNodes[3]].EnableResult = CfgMgr32.CR_ACCESS_DENIED;

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Partial, exit);
        StepOutcome failed = rig.Status().Steps.Single(s => !s.Ok && s.Step == "cm-enable:" + AudioNodes[3]);
        Assert.AreEqual("CR_ACCESS_DENIED", failed.CodeName);
    }

    [TestMethod]
    public void AnAudioNodeThatCameBackEnabledButFlaggedIsEnabledToClearTheFlag()
    {
        using GateRig rig = RigWithAudioNodesAway(TimeSpan.FromSeconds(1));
        rig.Nodes[AudioNodes[0]].AppearsEnabled = true;

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.AreEqual(0u, rig.Nodes[AudioNodes[0]].ConfigFlags);
        Assert.IsTrue(rig.Calls("enable").Any(c => c.InstanceId == AudioNodes[0]));
    }

    [TestMethod]
    public void AllowedStateWithEveryNodeInTheTreeIsLeftAlone()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());

        GateExitCode exit = rig.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsEmpty(rig.Nodes.Calls);
        Assert.IsEmpty(rig.Status().Changed!);
    }

    // ---- block ----

    [TestMethod]
    public void BlockMarksNodesThatAreAwayAndNotFlaggedLastAndNeverDisablesAPhantom()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null, flagged: false);
        foreach (string id in RecordedNodes.AirPodsTargets.Except(AudioNodes))
        {
            rig.Nodes[id].ConfigFlags = 0;
            rig.Nodes[id].Status = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE;
            rig.Nodes[id].Problem = 0;
        }

        GateExitCode exit = rig.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsEmpty(rig.Calls("disable-phantom"));
        Assert.HasCount(5, rig.Calls("disable"));
        Assert.HasCount(4, rig.Calls("set-flag"));
        int lastDisable = rig.Nodes.Calls.FindLastIndex(c => c.Kind == "disable");
        int firstFlag = rig.Nodes.Calls.FindIndex(c => c.Kind == "set-flag");
        Assert.IsLessThan(firstFlag, lastDisable, "Every node in the tree is disabled before any SetupAPI write.");
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, rig.Calls("disable").Last().InstanceId, "The device node is the last of the present ones.");
        foreach (string id in AudioNodes)
        {
            Assert.AreEqual(CfgMgr32.CONFIGFLAG_DISABLED, rig.Nodes[id].ConfigFlags & CfgMgr32.CONFIGFLAG_DISABLED, id);
        }

        Assert.AreEqual(nameof(BlockState.Blocked), rig.Status().State);
    }

    [TestMethod]
    public void BlockLeavesAwayNodesThatAreAlreadyFlaggedAlone()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null);

        GateExitCode exit = rig.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsEmpty(rig.Nodes.Calls, "Everything is blocked already; nothing is touched.");
    }

    [TestMethod]
    public void BlockDisablesANodeReportedWhileTheFlagsWereBeingSet()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null, flagged: false);
        foreach (string id in RecordedNodes.AirPodsTargets.Except(AudioNodes))
        {
            rig.Nodes[id].ConfigFlags = 0;
            rig.Nodes[id].Status = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE;
            rig.Nodes[id].Problem = 0;
        }

        FakeNode comesBack = rig.Nodes[AudioNodes[3]];
        comesBack.AppearsOnFlagWrite = true;
        comesBack.AppearsEnabled = true;

        GateExitCode exit = rig.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsEmpty(rig.Calls("disable-phantom"));
        NodeCall second = rig.Calls("disable").Last();
        Assert.AreEqual(AudioNodes[3], second.InstanceId, "A node that is in the tree by then is disabled like any other.");
        Assert.AreEqual(0x0Cu, second.Flags);
        Assert.IsTrue(rig.Nodes[AudioNodes[3]].IsDisabled);
        Assert.AreEqual(nameof(BlockState.Blocked), rig.Status().State);
    }

    [TestMethod]
    public void AFlagThatCannotBeWrittenIsAFailedStepWithItsWin32CodeAndAPartialBlock()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null, flagged: false);
        foreach (string id in RecordedNodes.AirPodsTargets.Except(AudioNodes))
        {
            rig.Nodes[id].MarkDisabled(persistent: true);
        }

        rig.Nodes[AudioNodes[1]].SetFlagError = 5; // ERROR_ACCESS_DENIED

        GateExitCode exit = rig.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Partial, exit);
        StepOutcome failed = rig.Status().Steps.Single(s => !s.Ok && s.Step == "cfg-set:" + AudioNodes[1]);
        Assert.AreEqual(5, failed.Code);
        Assert.AreEqual("ERROR_ACCESS_DENIED", failed.CodeName);
        Assert.Contains("Stopped at Write", failed.Detail!);
    }

    [TestMethod]
    public void AFlagWriteThatDoesNotShowWhenReadBackIsAFailure()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null, flagged: false);
        foreach (string id in RecordedNodes.AirPodsTargets.Except(AudioNodes))
        {
            rig.Nodes[id].MarkDisabled(persistent: true);
        }

        rig.Nodes[AudioNodes[2]].FlagWriteDoesNotStick = true;

        Assert.AreEqual(GateExitCode.Partial, rig.Run(GateVerbs.Block));
        Assert.Contains("did not show when read back", rig.Status().Steps.Single(s => !s.Ok && s.Step == "cfg-set:" + AudioNodes[2]).Detail!);
    }

    [TestMethod]
    public void PreshutdownBlockOrderPutsEveryNodeInTheTreeBeforeTheFlags()
    {
        using GateRig rig = RigWithAudioNodesAway(appearAfter: null, flagged: false);
        foreach (string id in RecordedNodes.AirPodsTargets.Except(AudioNodes))
        {
            rig.Nodes[id].ConfigFlags = 0;
            rig.Nodes[id].Status = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE;
            rig.Nodes[id].Problem = 0;
        }

        var steps = new List<StepOutcome>();
        NodeScanResult scan = NodeScan.FindTargets(rig.Nodes, RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);
        NodeChangeSummary summary = GateActions.ApplyBlock(rig.Nodes, scan.Targets, steps);

        Assert.AreEqual(GateExitCode.Success, summary.Outcome);
        string[] order = rig.Nodes.Calls.Select(c => c.Kind).ToArray();
        CollectionAssert.AreEqual(FiveDisablesThenFourFlags, order);
    }
}
