using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class GateActionsTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Harness()
        {
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Machine);
            Store = new GateStore(Machine);
        }

        public string Machine { get; }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public FakeFolderSecurity Folders { get; } = new();

        public CapturingLog Log { get; } = new();

        public GateExitCode Run(string verb, string? address = null) =>
            new GateActions(Nodes, Store, Folders, Log, new ManualTime()).Run(new GateRequest(verb, Nonce, address));

        public GateStatusFile Status()
        {
            GateRead<GateStatusFile> read = Store.ReadStatus(Nonce);
            Assert.IsTrue(read.IsOk, read.Step.Detail);
            return read.Value!;
        }

        public void Pin(DeviceIdentity identity) => Assert.IsTrue(Store.WriteDevice(identity).Ok);

        public void Dispose() => _temp.Dispose();
    }

    [TestMethod]
    public void BlockDisablesTheNineNodesPersistentlyAndNothingElse()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());

        GateExitCode exit = h.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, h.Nodes.Calls);
        Assert.IsTrue(h.Nodes.Calls.All(c => c.Kind == "disable" && c.Flags == 0x0C), "CM_DISABLE_PERSIST | CM_DISABLE_UI_NOT_OK on every call.");
        Assert.IsTrue(h.Nodes.Calls.All(c => (c.Flags & CfgMgr32.CM_DISABLE_PERSIST) != 0), "Without CM_DISABLE_PERSIST the block does not survive a reboot.");
        CollectionAssert.AreEquivalent(RecordedNodes.AirPodsTargets, h.Nodes.Calls.Select(c => c.InstanceId).ToArray());
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, h.Nodes.Calls[^1].InstanceId, "The device node goes last.");
        Assert.IsTrue(RecordedNodes.IPhoneNodes.Append(RecordedNodes.RadioNode).All(id => !h.Nodes[id].IsDisabled));

        GateStatusFile status = h.Status();
        Assert.AreEqual("success", status.Result);
        Assert.AreEqual(0, status.ExitCode);
        Assert.AreEqual(nameof(BlockState.Blocked), status.State);
        Assert.HasCount(9, status.Steps.Where(s => s.Step.StartsWith("cm-disable:", StringComparison.Ordinal) && s.Ok));
    }

    [TestMethod]
    public void BlockLeavesPersistentlyDisabledNodesAloneAndRedisablesTemporaryOnes()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        h.Nodes[RecordedNodes.AirPodsTargets[0]].MarkDisabled(persistent: false);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Block));

        Assert.HasCount(1, h.Nodes.Calls);
        Assert.AreEqual(RecordedNodes.AirPodsTargets[0], h.Nodes.Calls[0].InstanceId);
        Assert.HasCount(8, h.Status().Steps.Where(s => s.Detail == "Already disabled."));
    }

    [TestMethod]
    [DataRow(0x28u, "CR_NOT_DISABLEABLE")]
    [DataRow(0x17u, "CR_REMOVE_VETOED")]
    [DataRow(0x33u, "CR_ACCESS_DENIED")]
    public void APerNodeFailureIsReportedAndMakesThePartialResult(uint cr, string name)
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[1]].DisableResult = cr;

        GateExitCode exit = h.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.Partial, exit);
        StepOutcome failed = h.Status().Steps.Single(s => !s.Ok && s.Step.StartsWith("cm-disable:", StringComparison.Ordinal));
        Assert.AreEqual("cm-disable:" + RecordedNodes.AirPodsTargets[1], failed.Step);
        Assert.AreEqual(name, failed.CodeName);
        Assert.AreEqual((int)cr, failed.Code);
        Assert.IsNotNull(failed.Detail);
        Assert.AreEqual(nameof(BlockState.Mixed), h.Status().State);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, name));
    }

    [TestMethod]
    public void EveryNodeFailingIsAFailure()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].DisableResult = CfgMgr32.CR_ACCESS_DENIED;
        }

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.Block));
        Assert.AreEqual("failed", h.Status().Result);
    }

    [TestMethod]
    public void NodesThatAreNotPresentCannotBeBlockedAndSaySo()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].Present = false;
        }

        GateExitCode exit = h.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.NotPresent, exit);
        Assert.IsEmpty(h.Nodes.Calls);
        GateStatusFile status = h.Status();
        Assert.AreEqual("not-present", status.Result);
        Assert.AreEqual(nameof(BlockState.Unknown), status.State);
        Assert.HasCount(9, status.Steps.Where(s => s.CodeName == "CR_NO_SUCH_DEVNODE" && s.Step.StartsWith("cm-disable:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SomeNodesNotPresentIsPartial()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[2]].Present = false;

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.Block));
        Assert.HasCount(8, h.Nodes.Calls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{\"Address\":\"\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"00000000-0000-0000-ffff-ffffffffffff\"}")]
    public void WithoutAValidDeviceFileNothingIsTouched(string? content)
    {
        using var h = new Harness();
        if (content is not null)
        {
            File.WriteAllText(h.Store.DeviceFile, content);
        }

        foreach (string verb in new[] { GateVerbs.Block, GateVerbs.Allow, GateVerbs.Status, GateVerbs.ProtectOn })
        {
            Assert.AreEqual(GateExitCode.NoIdentity, h.Run(verb), verb);
        }

        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual("no-identity", h.Status().Result);
    }

    [TestMethod]
    public void AnIdentityThatMatchesNoNodeIsNotFound()
    {
        using var h = new Harness();
        h.Pin(new DeviceIdentity { Address = "AABBCCDDEEFF", ContainerId = RecordedNodes.AirPodsContainer });

        Assert.AreEqual(GateExitCode.NotFound, h.Run(GateVerbs.Block));
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual(nameof(BlockState.NotFound), h.Status().State);
    }

    [TestMethod]
    public void AFolderThatFailsTheAclCheckIsNotTrusted()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Folders.DefaultMachineSddl = "O:" + TestUsers.Sid + "G:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;CI;0x116;;;BU)";

        GateExitCode exit = h.Run(GateVerbs.Block);

        Assert.AreEqual(GateExitCode.FolderNotSecure, exit);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsFalse(File.Exists(h.Store.StatusFile(Nonce)), "Nothing is written to an untrusted folder.");
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "machine-folder-acl"));
    }

    [TestMethod]
    public void AMissingMachineFolderIsNotTrusted()
    {
        using var h = new Harness();
        Directory.Delete(h.Machine, recursive: true);

        Assert.AreEqual(GateExitCode.FolderNotSecure, h.Run(GateVerbs.Status));
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    [TestMethod]
    public void AllowEnablesOnlyNodesAtProblem22DeviceNodeFirst()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets.Skip(1))
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        h.Nodes[RecordedNodes.IPhoneNodes[1]].MarkDisabled(persistent: true);

        GateExitCode exit = h.Run(GateVerbs.Allow);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(8, h.Nodes.Calls);
        Assert.IsTrue(h.Nodes.Calls.All(c => c.Kind == "enable"));
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, h.Nodes.Calls[0].InstanceId);
        Assert.IsTrue(h.Nodes[RecordedNodes.IPhoneNodes[1]].IsDisabled, "The iPhone is never touched.");
        Assert.AreEqual(nameof(BlockState.Allowed), h.Status().State);
    }

    [TestMethod]
    public void AllowReportsAnEnableFailure()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        h.Nodes[RecordedNodes.AirPodsDeviceNode].EnableResult = CfgMgr32.CR_ACCESS_DENIED;

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.Allow));
        Assert.AreEqual("CR_ACCESS_DENIED", h.Status().Steps.Single(s => !s.Ok && s.Step.StartsWith("cm-enable:", StringComparison.Ordinal)).CodeName);
    }

    [TestMethod]
    public void StatusChangesNothingAndReportsTheState()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[0]].MarkDisabled(persistent: true);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Status));

        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual(nameof(BlockState.Mixed), h.Status().State);
    }

    [TestMethod]
    public void SetBootWritesTheConfig()
    {
        using var h = new Harness();

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetBootOff));
        Assert.IsFalse(h.Store.ReadConfig().Value!.BlockAtBoot);
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetBootOn));
        Assert.IsTrue(h.Store.ReadConfig().Value!.BlockAtBoot);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public void SetDeviceReadsTheContainerFromTheDeviceNode()
    {
        using var h = new Harness();

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.AirPodsAddress));

        DeviceIdentity pinned = h.Store.ReadDevice().Value!;
        Assert.AreEqual(RecordedNodes.AirPodsAddress, pinned.Address);
        Assert.AreEqual(RecordedNodes.AirPodsContainer, pinned.ContainerId);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public void SetDeviceMatchesTheDeviceNodeWhateverItsCase()
    {
        using var h = new Harness();

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.IPhoneAddress));
        Assert.AreEqual(RecordedNodes.IPhoneContainer, h.Store.ReadDevice().Value!.ContainerId);
    }

    [TestMethod]
    public void SetDeviceForAnUnknownAddressIsNotFound()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());

        Assert.AreEqual(GateExitCode.NotFound, h.Run(GateVerbs.SetDevice, "AABBCCDDEEFF"));
        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
    }

    [TestMethod]
    public void SetDeviceRefusesAnAddressThatIsNotValid()
    {
        using var h = new Harness();

        Assert.AreEqual(GateExitCode.Rejected, h.Run(GateVerbs.SetDevice, "5A6b7C8d9Eaf"));
        Assert.IsFalse(File.Exists(h.Store.DeviceFile));
    }

    [TestMethod]
    public void SetDeviceRefusesToLeaveTheCurrentDeviceBlocked()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[3]].MarkDisabled(persistent: true);

        Assert.AreEqual(GateExitCode.OtherDeviceBlocked, h.Run(GateVerbs.SetDevice, RecordedNodes.IPhoneAddress));
        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.AirPodsAddress), "Re-pinning the same device is fine.");
    }

    [TestMethod]
    public void SetDeviceRefusesANodeInThePcContainer()
    {
        var nodes = RecordedNodes.Table().Nodes.ToList();
        nodes.Add(new FakeNode(@"BTHENUM\DEV_AABBCCDDEEFF\b&1a2b3c4d&0&BLUETOOTHDEVICE_AABBCCDDEEFF", NodeMatch.PcContainer));
        using var temp = new TempFolder();
        string machine = Path.Combine(temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(machine);
        var store = new GateStore(machine);

        GateExitCode exit = new GateActions(new FakeNodeApi(nodes), store, new FakeFolderSecurity(), new CapturingLog(), new ManualTime())
            .Run(new GateRequest(GateVerbs.SetDevice, Nonce, "AABBCCDDEEFF"));

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsFalse(File.Exists(store.DeviceFile));
    }

    [TestMethod]
    public void BootBlocksOnlyWhenTheConfigSaysSo()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.Boot), "No config is not permission.");
        Assert.IsEmpty(h.Nodes.Calls);

        File.WriteAllText(h.Store.ConfigFile, "{\"SchemaVersion\":1,\"BlockAtBoot\":\"yes\"}");
        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.Boot));
        Assert.IsEmpty(h.Nodes.Calls);

        Assert.IsTrue(h.Store.WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Boot));
        Assert.IsEmpty(h.Nodes.Calls);

        Assert.IsTrue(h.Store.WriteConfig(new GateConfig { BlockAtBoot = true }).Ok);
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Boot));
        Assert.HasCount(9, h.Nodes.Calls);
        Assert.IsTrue(h.Nodes.Calls.All(c => c.Flags == GateActions.BlockDisableFlags));
    }

    [TestMethod]
    public void ProtectVerbsAreNotAvailableWithoutTheProtectionFeature()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());

        Assert.AreEqual(GateExitCode.NotAvailable, h.Run(GateVerbs.ProtectOn));
        Assert.AreEqual("not-available", h.Status().Result);
        Assert.AreEqual(GateExitCode.NotAvailable, h.Run(GateVerbs.ProtectOff));
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public void AStatusFileThatCannotBeWrittenTurnsSuccessIntoStatusNotWritten()
    {
        using var h = new Harness();
        Directory.CreateDirectory(h.Store.StatusFile(Nonce));

        Assert.AreEqual(GateExitCode.StatusNotWritten, h.Run(GateVerbs.SetBootOn));
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "write-status"));
    }

    [TestMethod]
    public void EveryRunPrunesOldStatusFiles()
    {
        using var h = new Harness();
        for (int i = 0; i < 40; i++)
        {
            File.WriteAllText(h.Store.StatusFile(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), "{}");
        }

        h.Run(GateVerbs.SetBootOn);

        Assert.HasCount(GateStore.MaxStatusFilesKept, Directory.GetFiles(h.Machine, "status-*.json"));
    }

    [TestMethod]
    public void ExitCodesAreDistinctAndNamed()
    {
        GateExitCode[] all = Enum.GetValues<GateExitCode>();

        Assert.HasCount(all.Length, all.Select(c => (int)c).Distinct());
        Assert.HasCount(all.Length, all.Select(GateExitCodes.ResultName).Distinct());
        Assert.IsTrue(all.All(c => (int)c is >= 0 and < 64), "Clear of the sysexits codes in ExitCodes.");
        Assert.AreEqual("not-present", GateExitCodes.NameOf(4));
        Assert.IsNull(GateExitCodes.NameOf(1));
    }

    [TestMethod]
    public void NodeChangeSummaryOutcomes()
    {
        Assert.AreEqual(GateExitCode.Success, new NodeChangeSummary(9, 4, 5, 0, 0).Outcome);
        Assert.AreEqual(GateExitCode.Partial, new NodeChangeSummary(9, 1, 0, 8, 0).Outcome);
        Assert.AreEqual(GateExitCode.NotPresent, new NodeChangeSummary(9, 0, 0, 9, 0).Outcome);
        Assert.AreEqual(GateExitCode.Failed, new NodeChangeSummary(9, 0, 0, 8, 1).Outcome);
        Assert.AreEqual(GateExitCode.NotFound, new NodeChangeSummary(0, 0, 0, 0, 0).Outcome);
    }
}
