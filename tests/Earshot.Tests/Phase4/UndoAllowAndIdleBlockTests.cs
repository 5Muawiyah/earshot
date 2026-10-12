using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase6;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The idle block that will not cut a live link, the undo that puts back only what one allow changed, and the adoption of a
// replacement pair. The link bit is DN_DEVICE_DISCONNECTED on the device node: clear in the fake table's default (the AirPods
// are connected), set by Disconnected().
[TestClass]
public sealed class UndoAllowAndIdleBlockTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string AllowNonce = "fedcba9876543210fedcba9876543210";

    private static void Disconnected(GateRig rig) => rig.Nodes[RecordedNodes.AirPodsDeviceNode].Status |= CfgMgr32.DN_DEVICE_DISCONNECTED;

    private static GateRig AirPodsAtRest()
    {
        var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());
        rig.BlockAirPodsAtRest();
        return rig;
    }

    // ---- command lines ----

    [TestMethod]
    public void TheNewVerbsParseWithTheirOwnArguments()
    {
        Assert.IsTrue(Program.TryParseGateArgs(["gate", "block-idle", Nonce], out GateRequest? idle, out _));
        Assert.AreEqual(new GateRequest("block-idle", Nonce, null), idle);

        Assert.IsTrue(Program.TryParseGateArgs(["gate", "undo-allow", Nonce, AllowNonce], out GateRequest? undo, out string? problem), problem);
        Assert.AreEqual(AllowNonce, undo.AllowNonce);
        Assert.IsNull(undo.Address);

        Assert.IsTrue(Program.TryParseGateArgs(["gate", "adopt-device", Nonce, "AA11BB22CC33"], out GateRequest? adopt, out _));
        Assert.AreEqual("AA11BB22CC33", adopt.Address);
    }

    [TestMethod]
    [DataRow("gate", "undo-allow", Nonce)]
    [DataRow("gate", "undo-allow", Nonce, "$(Arg2)")]
    [DataRow("gate", "undo-allow", Nonce, "short")]
    [DataRow("gate", "undo-allow", Nonce, "FEDCBA9876543210FEDCBA9876543210")]
    [DataRow("gate", "undo-allow", Nonce, "0A1B2C3D4E8C")]
    [DataRow("gate", "adopt-device", Nonce)]
    [DataRow("gate", "adopt-device", Nonce, "0a1b2c3d4e8c")]
    [DataRow("gate", "adopt-device", Nonce, AllowNonce)]
    [DataRow("gate", "block-idle", Nonce, "0A1B2C3D4E8C")]
    [DataRow("gate", "block-idle", Nonce, AllowNonce)]
    [DataRow("gate", "allow", Nonce, AllowNonce)]
    [DataRow("gate-protect", "undo-allow", Nonce)]
    [DataRow("gate-protect", "block-idle", Nonce)]
    [DataRow("gate-protect", "adopt-device", Nonce, "AA11BB22CC33")]
    public void MalformedUseOfTheNewVerbsIsRefused(params string[] args)
    {
        Assert.IsFalse(Program.TryParseGateArgs(args, out GateRequest? request, out string? problem));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    // ---- the idle block ----

    [TestMethod]
    public void TheIdleBlockRefusesWithInUseAndChangesNothingWhileTheDeviceNodeShowsALiveLink()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());

        GateExitCode exit = rig.Run(GateVerbs.BlockIdle);

        Assert.AreEqual(GateExitCode.InUse, exit);
        Assert.AreEqual(28, (int)exit);
        Assert.IsEmpty(rig.Nodes.Calls);
        GateStatusFile status = rig.Status();
        Assert.AreEqual("in-use", status.Result);
        Assert.AreEqual(28, status.ExitCode);
        Assert.IsTrue(status.Steps.Any(s => s.Step == GateActions.InUseStep && !s.Ok));
    }

    [TestMethod]
    public void TheIdleBlockBlocksOnceTheLinkBitSaysDisconnected()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());
        Disconnected(rig);

        GateExitCode exit = rig.Run(GateVerbs.BlockIdle);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, rig.Calls("disable"));
    }

    [TestMethod]
    public void ThePlainBlockStillCutsALiveLinkBecauseTheHandBackAndTheBootBlockMeanTo()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());

        GateExitCode exit = rig.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, rig.Calls("disable"));
    }

    [TestMethod]
    public void TheLinkBitOfADisabledNodeIsIgnored()
    {
        using GateRig rig = AirPodsAtRest();

        GateExitCode exit = rig.Run(GateVerbs.BlockIdle);

        Assert.AreEqual(GateExitCode.Success, exit, "A disabled device node has no link; its bit says nothing.");
        Assert.IsEmpty(rig.Nodes.Calls);
    }

    [TestMethod]
    public void AStatusThatCannotBeReadIsNotTakenAsConnected()
    {
        using var rig = new GateRig();
        rig.Pin(RecordedNodes.AirPods());
        rig.Nodes[RecordedNodes.AirPodsDeviceNode].StatusReadResult = CfgMgr32.CR_FAILURE;

        GateExitCode exit = rig.Run(GateVerbs.BlockIdle);

        Assert.AreNotEqual(GateExitCode.InUse, exit);
        StepOutcome step = rig.Status().Steps.First(s => s.Step == GateActions.InUseStep);
        Assert.AreEqual((int)CfgMgr32.CR_FAILURE, step.Code, "The raw code is surfaced.");
    }

    // ---- undo-allow ----

    [TestMethod]
    public void UndoAllowDisablesExactlyTheNodesTheAllowChanged()
    {
        using GateRig rig = AirPodsAtRest();
        string allow = rig.NextNonce();
        Assert.AreEqual(GateExitCode.Success, rig.Run(GateVerbs.Allow, nonce: allow));
        Disconnected(rig);
        rig.Nodes.Calls.Clear();

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, rig.Calls("disable"));
        Assert.IsTrue(rig.Calls("disable").All(c => c.Flags == 0x0C), "Persistent, as the block does it.");
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, rig.Calls("disable").Last().InstanceId);
        Assert.AreEqual(nameof(BlockState.Blocked), rig.Status().State);
        Assert.IsFalse(rig.Status().Steps.Any(s => s.Step == GateActions.UndoStep && s.Detail!.Contains("rest are blocked", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UndoAllowLeavesAloneWhatTheAllowDidNotChangeThenBlocksTheRestBecauseTheyAreNotAtRest()
    {
        // The Hands-Free node was enabled before the allow, so the allow did not list it. After the undo it is the one node
        // still enabled, and the state read after the undo sends the full block over it.
        using GateRig rig = AirPodsAtRest();
        FakeNode early = rig.Nodes[RecordedNodes.AirPodsTargets[7]];
        early.Status = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE;
        early.Problem = 0;
        early.ConfigFlags = 0;
        string allow = rig.NextNonce();
        Assert.AreEqual(GateExitCode.Success, rig.Run(GateVerbs.Allow, nonce: allow));
        Assert.HasCount(8, rig.Status(allow).Changed!);
        Disconnected(rig);
        rig.Nodes.Calls.Clear();

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        string[] order = rig.Calls("disable").Select(c => c.InstanceId).ToArray();
        Assert.AreEqual(RecordedNodes.AirPodsTargets[7], order[^1], "The one the allow did not change is blocked last, by the escalation.");
        Assert.AreEqual(nameof(BlockState.Blocked), rig.Status().State);
        Assert.IsTrue(rig.Status().Steps.Any(s => s.Step == GateActions.UndoStep && s.Detail!.Contains("rest are blocked", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UndoAllowWithNoRecordOfTheAllowBlocksEverything()
    {
        using GateRig rig = AirPodsAtRest();
        Assert.AreEqual(GateExitCode.Success, rig.Run(GateVerbs.Allow));
        Disconnected(rig);
        rig.Nodes.Calls.Clear();

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: AllowNonce);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, rig.Calls("disable"));
        Assert.IsTrue(rig.Status().Steps.Any(s => s.Step == GateActions.UndoStep && s.Detail!.Contains("No usable allow record", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UndoAllowWithAStaleRecordBlocksEverything()
    {
        using GateRig rig = AirPodsAtRest();
        string allow = rig.NextNonce();
        Assert.AreEqual(GateExitCode.Success, rig.Run(GateVerbs.Allow, nonce: allow));
        Disconnected(rig);
        rig.Time.Advance(GateActions.UndoAllowMaxAge + TimeSpan.FromSeconds(1));
        rig.Nodes.Calls.Clear();

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, rig.Calls("disable"));
        Assert.IsTrue(rig.Status().Steps.Any(s => s.Detail is not null && s.Detail.Contains("older than 10 minutes", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UndoAllowNamingAStatusFileThatIsNotAnAllowBlocksEverything()
    {
        using GateRig rig = AirPodsAtRest();
        string other = rig.NextNonce();
        Assert.AreEqual(GateExitCode.Success, rig.Run(GateVerbs.Status, nonce: other));
        Disconnected(rig);

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: other);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsTrue(rig.Status().Steps.Any(s => s.Detail is not null && s.Detail.Contains("not an allow's", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UndoAllowIgnoresAListedNodeThatIsNotTheDevicesAndNeverTouchesThePhone()
    {
        using GateRig rig = AirPodsAtRest();
        Disconnected(rig);
        string allow = rig.NextNonce();
        DateTimeOffset now = rig.Time.GetUtcNow();
        var forged = new GateStatusFile(
            GateStore.StatusSchemaVersion, allow, GateVerbs.Allow, now, now, "success", 0, null, [], false,
            [new GateChange(RecordedNodes.IPhoneNodes[1], GateChangeFrom.DisabledPresent), new GateChange(RecordedNodes.AirPodsDeviceNode, GateChangeFrom.DisabledPresent)]);
        Assert.IsTrue(rig.Store.WriteStatus(forged).Ok);
        rig.Nodes[RecordedNodes.AirPodsDeviceNode].Status = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE | CfgMgr32.DN_DEVICE_DISCONNECTED;
        rig.Nodes[RecordedNodes.AirPodsDeviceNode].Problem = 0;
        rig.Nodes[RecordedNodes.AirPodsDeviceNode].ConfigFlags = 0;

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsFalse(rig.Nodes.Calls.Any(c => RecordedNodes.IPhoneNodes.Contains(c.InstanceId)), "The phone is never touched.");
        Assert.IsTrue(rig.Status().Steps.Any(s => s.Detail is not null && s.Detail.Contains("not one of this device's nodes", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UndoAllowRefusesWithInUseWhileTheLinkIsUp()
    {
        using GateRig rig = AirPodsAtRest();
        string allow = rig.NextNonce();
        Assert.AreEqual(GateExitCode.Success, rig.Run(GateVerbs.Allow, nonce: allow));
        rig.Nodes.Calls.Clear();

        GateExitCode exit = rig.Run(GateVerbs.UndoAllow, allowNonce: allow);

        Assert.AreEqual(GateExitCode.InUse, exit);
        Assert.IsEmpty(rig.Nodes.Calls);
    }

    [TestMethod]
    public void AGateThatDiesPartWayThroughAnAllowStillLeavesTheListUndoNeeds()
    {
        using GateRig rig = AirPodsAtRest();
        string allow = rig.NextNonce();
        int enables = 0;
        rig.Nodes.BeforeEnable = _ =>
        {
            if (++enables == 4)
            {
                throw new InvalidOperationException("The gate was stopped here.");
            }
        };

        GateExitCode exit = rig.Run(GateVerbs.Allow, nonce: allow);

        Assert.AreEqual(GateExitCode.Failed, exit);
        GateStatusFile died = rig.Status(allow);
        Assert.AreEqual("failed", died.Result);
        Assert.HasCount(4, died.Changed!, "The fourth node was listed before the run stopped on it.");

        Disconnected(rig);
        rig.Nodes.BeforeEnable = null;
        rig.Nodes.Calls.Clear();
        GateExitCode undo = rig.Run(GateVerbs.UndoAllow, allowNonce: allow);

        Assert.AreEqual(GateExitCode.Success, undo);
        Assert.AreEqual(nameof(BlockState.Blocked), rig.Status().State);
        Assert.HasCount(3, rig.Calls("disable"), "The three that were enabled are put back; the fourth never was.");
    }

    [TestMethod]
    public void TheChangeListSurvivesAWriteAndAReadAndAnOldStatusFileStillReads()
    {
        using var rig = new GateRig();
        DateTimeOffset now = rig.Time.GetUtcNow();
        var status = new GateStatusFile(
            GateStore.StatusSchemaVersion, Nonce, GateVerbs.Allow, now, now, "partial", 2, nameof(BlockState.Mixed), [], false,
            [new GateChange("BTHENUM\\A", GateChangeFrom.DisabledPresent), new GateChange("BTHENUM\\B", GateChangeFrom.FlaggedAbsent)]);
        Assert.IsTrue(rig.Store.WriteStatus(status).Ok);

        GateRead<GateStatusFile> read = rig.Store.ReadStatus(Nonce);

        Assert.IsTrue(read.IsOk, read.Step.Detail);
        Assert.AreEqual(GateStore.StatusSchemaVersion, read.Value!.SchemaVersion);
        Assert.AreEqual(GateChangeFrom.FlaggedAbsent, read.Value.Changed![1].From);

        // A status file as the previous build wrote it: version 1, no Changed member.
        string path = rig.Store.StatusFile(AllowNonce);
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"Nonce\":\"" + AllowNonce + "\",\"Verb\":\"block\",\"StartedUtc\":\"2026-10-12T01:00:00.0000000Z\"," +
            "\"FinishedUtc\":\"2026-10-12T01:00:01.0000000Z\",\"Result\":\"success\",\"ExitCode\":0,\"State\":\"Blocked\",\"Steps\":[],\"StepsTruncated\":false}");
        GateRead<GateStatusFile> old = rig.Store.ReadStatus(AllowNonce);
        Assert.IsTrue(old.IsOk, old.Step.Detail);
        Assert.IsNull(old.Value!.Changed);
    }

    [TestMethod]
    public void AVersionOneFileThatCarriesAChangeListIsRefused()
    {
        using var rig = new GateRig();
        File.WriteAllText(rig.Store.StatusFile(AllowNonce), "{\"SchemaVersion\":1,\"Nonce\":\"" + AllowNonce + "\",\"Verb\":\"allow\",\"StartedUtc\":\"2026-10-12T01:00:00.0000000Z\"," +
            "\"FinishedUtc\":\"2026-10-12T01:00:01.0000000Z\",\"Result\":\"success\",\"ExitCode\":0,\"State\":\"Blocked\",\"Steps\":[],\"StepsTruncated\":false,\"Changed\":[]}");

        Assert.AreEqual(GateReadStatus.Invalid, rig.Store.ReadStatus(AllowNonce).Status);
    }

    // ---- adopt-device ----

    private static FakeNodeApi TableWithReplacementPair(out string newAddress, out Guid newContainer)
    {
        newAddress = "AA11BB22CC33";
        newContainer = new Guid("7e2a9c40-1b3d-4f5e-8a6b-0c1d2e3f4a5b");
        FakeNodeApi table = RecordedNodes.Table();
        var nodes = table.Nodes.ToList();
        nodes.Add(new FakeNode(@"BTHENUM\DEV_AA11BB22CC33\b&1a2b3c4d&0&BLUETOOTHDEVICE_AA11BB22CC33", newContainer, "AirPods Pro"));
        nodes.Add(new FakeNode(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&AA11BB22CC33_C00000000", newContainer));
        nodes.Add(new FakeNode(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&AA11BB22CC33_C00000000", newContainer));
        return new FakeNodeApi(nodes);
    }

    [TestMethod]
    public void AdoptPinsTheReplacementPairAndBlocksItInOneRun()
    {
        // The old pair is allowed (Fix connection puts it back before it adopts anything), the new pair arrives enabled.
        using var rig = new GateRig(TableWithReplacementPair(out string address, out Guid container));
        rig.Pin(RecordedNodes.AirPods());

        GateExitCode exit = rig.Run(GateVerbs.AdoptDevice, address);

        Assert.AreEqual(GateExitCode.Success, exit);
        DeviceIdentity pinned = rig.Store.ReadDevice().Value!;
        Assert.AreEqual(address, pinned.Address);
        Assert.AreEqual(container, pinned.ContainerId);
        Assert.HasCount(3, rig.Calls("disable"));
        Assert.IsTrue(rig.Calls("disable").All(c => c.InstanceId.Contains(address, StringComparison.Ordinal)));
        Assert.AreEqual(nameof(BlockState.Blocked), rig.Status().State);
    }

    [TestMethod]
    public void AdoptRefusesAnAddressThatIsNotAirPodsByTheIdentityRuleAndPinsNothing()
    {
        using var rig = new GateRig(RecordedNodes.TableWithHeadphones());
        rig.Pin(RecordedNodes.AirPods());
        rig.BlockAirPodsAtRest();

        GateExitCode phone = rig.Run(GateVerbs.AdoptDevice, RecordedNodes.IPhoneAddress);
        GateExitCode headphones = rig.Run(GateVerbs.AdoptDevice, RecordedNodes.HeadphonesAddress);
        GateExitCode unknown = rig.Run(GateVerbs.AdoptDevice, "FFFFFFFFFFFF");

        Assert.AreEqual(GateExitCode.NotAirPods, phone);
        Assert.AreEqual(GateExitCode.NotAirPods, headphones);
        Assert.AreEqual(GateExitCode.NotAirPods, unknown);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, rig.Store.ReadDevice().Value!.Address);
        Assert.IsEmpty(rig.Nodes.Calls);
    }

    [TestMethod]
    public void AdoptRefusesToMoveThePinAwayFromAnOldPairThatIsStillBlocked()
    {
        using var rig = new GateRig(TableWithReplacementPair(out string address, out _));
        rig.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets.Take(2))
        {
            rig.Nodes[id].MarkDisabled(persistent: true);
        }

        GateExitCode exit = rig.Run(GateVerbs.AdoptDevice, address);

        Assert.AreEqual(GateExitCode.OtherDeviceBlocked, exit, "Moving the pin would leave the old pair blocked with nothing able to allow it.");
        Assert.AreEqual(RecordedNodes.AirPodsAddress, rig.Store.ReadDevice().Value!.Address);
    }

    [TestMethod]
    public void AdoptReportsPartialWhenThePinMovedAndABlockDidNotTake()
    {
        using var rig = new GateRig(TableWithReplacementPair(out string address, out _));
        rig.Pin(RecordedNodes.AirPods());
        rig.Nodes.Nodes.First(n => n.InstanceId.Contains(address, StringComparison.Ordinal) && n.InstanceId.Contains("111E", StringComparison.OrdinalIgnoreCase)).DisableResult = CfgMgr32.CR_ACCESS_DENIED;

        GateExitCode exit = rig.Run(GateVerbs.AdoptDevice, address);

        Assert.AreEqual(GateExitCode.Partial, exit);
        Assert.AreEqual(address, rig.Store.ReadDevice().Value!.Address, "The pin moved; the failed node is reported, never hidden.");
        Assert.IsTrue(rig.Status().Steps.Any(s => !s.Ok && s.CodeName == "CR_ACCESS_DENIED"));
    }

    [TestMethod]
    public void NewVerbsNeedAnInstalledConfig()
    {
        using var rig = new GateRig();
        File.Delete(rig.Store.ConfigFile);

        Assert.AreEqual(GateExitCode.NoConfig, rig.Run(GateVerbs.BlockIdle));
        Assert.AreEqual(GateExitCode.NoConfig, rig.Run(GateVerbs.UndoAllow, allowNonce: AllowNonce));
        Assert.AreEqual(GateExitCode.NoConfig, rig.Run(GateVerbs.AdoptDevice, "AA11BB22CC33"));
        Assert.IsEmpty(rig.Nodes.Calls);
    }
}
