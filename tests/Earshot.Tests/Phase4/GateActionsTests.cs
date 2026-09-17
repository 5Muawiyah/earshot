using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase6;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class GateActionsTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Harness(FakeNodeApi? nodes = null, IBluetoothServiceApi? bluetooth = null)
        {
            Bluetooth = bluetooth;
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Machine);
            Store = new GateStore(Machine);
            Nodes = nodes ?? RecordedNodes.Table();

            // Install writes config.json; without it every verb that changes a device refuses (NoConfig).
            Assert.IsTrue(Store.WriteConfig(new GateConfig()).Ok);
        }

        public string Machine { get; }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public CapturingLog Log { get; } = new();

        public IBluetoothServiceApi? Bluetooth { get; }

        // Each verb in the mode its task runs it in.
        public GateExitCode Run(string verb, string? address = null) =>
            new GateActions(Nodes, Store, Folders, Log, new ManualTime(), bluetooth: Bluetooth)
                .Run(new GateRequest(verb, Nonce, address, GateModes.IsProtectVerb(verb) ? GateMode.Protect : GateMode.Gate));

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
    public void ANodeWhoseContainerCannotBeReadIsLeftAloneAndMakesTheBlockPartial()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[4]].ContainerReadResult = 0x1Du; // CR_REGISTRY_ERROR

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.Block));

        Assert.HasCount(8, h.Nodes.Calls);
        Assert.IsFalse(h.Nodes.Calls.Any(c => c.InstanceId == RecordedNodes.AirPodsTargets[4]));
        Assert.AreEqual(nameof(BlockState.Unknown), h.Status().State);
    }

    [TestMethod]
    public void AnAllowWhoseOnlyCandidateCannotBeReadIsAFailureNotNotFound()
    {
        var table = new FakeNodeApi([new FakeNode(RecordedNodes.AirPodsDeviceNode, RecordedNodes.AirPodsContainer) { ContainerReadResult = CfgMgr32.CR_FAILURE }]);
        using var h = new Harness(table);
        h.Pin(RecordedNodes.AirPods());

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.Allow));

        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual(nameof(BlockState.Unknown), h.Status().State);
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
    [DataRow("{\"Address\":\"\",\"ContainerId\":\"5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13\"}")]
    [DataRow("{\"Address\":\"0A1B2C3D4E8C\",\"ContainerId\":\"00000000-0000-0000-ffff-ffffffffffff\"}")]
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

    // An uninstall that could not restore everything keeps device.json and protection.json but deletes config.json.
    // A run that was queued behind it then changes nothing: no task is left to undo what it would do.
    [TestMethod]
    public void WithoutConfigTheVerbsThatChangeADeviceChangeNothing()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones(), FakeBluetoothServices.AirPods());
        h.Pin(RecordedNodes.AirPods());
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        File.Delete(h.Store.ConfigFile);

        foreach (string verb in new[] { GateVerbs.Block, GateVerbs.Allow, GateVerbs.ProtectOn, GateVerbs.ProtectOff })
        {
            Assert.AreEqual(GateExitCode.NoConfig, h.Run(verb), verb);
            Assert.AreEqual("no-config", h.Status().Result);
        }

        Assert.AreEqual(GateExitCode.NoConfig, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
        Assert.Contains("config.json is missing", h.Status().Steps.Single(s => s.Step == "config-check").Detail!);

        // Reading the state and writing the setting do not need it.
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Status));
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetBootOn));
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Allow));
        Assert.HasCount(9, h.Nodes.Calls);
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

    // CM_DISABLE_PERSIST works by setting CONFIGFLAG_DISABLED, so a node that is enabled now but still carries
    // the flag would come back disabled at the next restart. Allow clears it rather than call it already enabled.
    [TestMethod]
    public void AllowAlsoEnablesANodeThatIsEnabledButStillMarkedDisabled()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsDeviceNode].ConfigFlags |= CfgMgr32.CONFIGFLAG_DISABLED;

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Allow));

        Assert.HasCount(1, h.Nodes.Calls);
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, h.Nodes.Calls[0].InstanceId);
        Assert.AreEqual("enable", h.Nodes.Calls[0].Kind);
        Assert.AreEqual(0u, h.Nodes[RecordedNodes.AirPodsDeviceNode].ConfigFlags);
        Assert.AreEqual(nameof(BlockState.Allowed), h.Status().State);
    }

    [TestMethod]
    public void AnEnableThatLeavesTheNodeMarkedDisabledIsAFailure()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        FakeNode node = h.Nodes[RecordedNodes.AirPodsDeviceNode];
        node.MarkDisabled(persistent: true);
        node.KeepConfigFlagsOnEnable = true;

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.Allow));

        StepOutcome kept = h.Status().Steps.Last(s => s.Step == "cm-enable:" + RecordedNodes.AirPodsDeviceNode);
        Assert.IsFalse(kept.Ok);
        Assert.Contains("still marked disabled", kept.Detail!);
    }

    [TestMethod]
    public void AllowCountsANonPresentNodeThatIsStillMarkedDisabledAsAFailure()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        FakeNode node = h.Nodes[RecordedNodes.AirPodsTargets[2]];
        node.MarkDisabled(persistent: true);
        node.Present = false;

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.Allow));

        Assert.Contains("may come back disabled", h.Status().Steps.Last(s => s.Step == "cm-enable:" + node.InstanceId).Detail!);
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
        using var h = new Harness(RecordedNodes.TableWithHeadphones());

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));
        Assert.AreEqual(RecordedNodes.HeadphonesContainer, h.Store.ReadDevice().Value!.ContainerId, "The lower-case node ids match.");
    }

    // A phone has no A2DP sink node, so the gate never pins one even though it has BTHENUM nodes with its
    // address: blocking it would take it away from the PC it is paired with.
    [TestMethod]
    public void SetDeviceRefusesADeviceThatCannotReceiveAudio()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());

        Assert.AreEqual(GateExitCode.NotAudioSink, h.Run(GateVerbs.SetDevice, RecordedNodes.IPhoneAddress));

        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address, "The pin is unchanged.");
        Assert.Contains("not headphones or speakers", h.Status().Steps.Last(s => s.Step == "set-device").Detail!);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    // A sink node that cannot be read says nothing about the device, so the refusal is a failure, not "cannot play
    // audio".
    [TestMethod]
    public void SetDeviceWithAnUnreadableSinkNodeFailsRatherThanCallingItNotAnAudioDevice()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.Pin(RecordedNodes.AirPods());
        string sink = RecordedNodes.HeadphonesNodes.Single(id => id.StartsWith(GateActions.AudioSinkNodePrefix, StringComparison.OrdinalIgnoreCase));
        h.Nodes[sink].ContainerReadResult = 0x1Du; // CR_REGISTRY_ERROR

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address, "The pin is unchanged.");
        GateStatusFile status = h.Status();
        Assert.IsTrue(status.Steps.Any(s => s.Step == "cm-container:" + sink && s.CodeName == "CR_REGISTRY_ERROR"));
        Assert.Contains("could not be read", status.Steps.Last(s => s.Step == "set-device").Detail!);
    }

    [TestMethod]
    public void SetDeviceWithAnUnreadableNodeOfTheCurrentDeviceDoesNotMoveThePin()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[3]].ConfigFlagsReadResult = CfgMgr32.CR_FAILURE;

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
        Assert.Contains("could not be read", h.Status().Steps.Last(s => s.Step == "set-device").Detail!);
    }

    [TestMethod]
    public void SetDeviceRefusesToMoveThePinWhileProtectionListsServices()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.Pin(RecordedNodes.AirPods());
        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord { DisabledServices = { new Guid("0000111E-0000-1000-8000-00805F9B34FB") } }).Ok);

        Assert.AreEqual(GateExitCode.OtherDeviceProtected, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
        Assert.Contains("Turn them back on (protect-off) first", h.Status().Steps.Last(s => s.Step == "set-device").Detail!);
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.AirPodsAddress), "Re-pinning the same device is fine.");
    }

    // The AirPods were removed from Windows (their nodes and their pairing are gone) while protection.json still
    // listed Handsfree for them. Nothing can turn it back on and nothing needs to, so the entries are emptied and
    // the new device is pinned, where before every way out refused.
    [TestMethod]
    public void SetDeviceEmptiesTheRecordOfADeviceRemovedFromWindows()
    {
        FakeBluetoothServices paired = FakeBluetoothServices.AirPods();
        paired.Remove(RecordedNodes.AirPodsAddress);
        using var h = new Harness(WithoutTheAirPods(), paired);
        h.Pin(RecordedNodes.AirPods());
        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord { DisabledServices = { ProtectedServices.Handsfree } }).Ok);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

        Assert.AreEqual(RecordedNodes.HeadphonesContainer, h.Store.ReadDevice().Value!.ContainerId);
        Assert.IsEmpty(h.Store.ReadProtection().Value!.DisabledServices);
        StepOutcome voided = h.Status().Steps.Single(s => s.Step == ProtectionGateRunner.RecordVoidStep);
        Assert.IsTrue(voided.Ok);
        Assert.Contains(RecordedNodes.AirPodsAddress, voided.Detail!);
        Assert.IsEmpty(paired.SetCalls);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    // With no node left but the device still paired, the paired list unreadable, or no Bluetooth API to ask,
    // nothing shows the service state is gone, so the refusal stands.
    [TestMethod]
    public void SetDeviceKeepsTheRecordWhileTheOldDeviceMayStillBePaired()
    {
        FakeBluetoothServices stillPaired = FakeBluetoothServices.AirPods();
        FakeBluetoothServices listFails = FakeBluetoothServices.AirPods();
        listFails.Remove(RecordedNodes.AirPodsAddress);
        listFails.FindResult = BluetoothApis.ERROR_REVISION_MISMATCH;

        foreach (FakeBluetoothServices? bluetooth in new[] { stillPaired, listFails, null })
        {
            using var h = new Harness(WithoutTheAirPods(), bluetooth);
            h.Pin(RecordedNodes.AirPods());
            Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord { DisabledServices = { ProtectedServices.Handsfree } }).Ok);

            Assert.AreEqual(GateExitCode.OtherDeviceProtected, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

            Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
            CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Store.ReadProtection().Value!.DisabledServices);
        }
    }

    // A node of the old device still listed, even when not present (a radio that is off), keeps the record: the
    // device may still be paired with its Handsfree still off.
    [TestMethod]
    public void SetDeviceKeepsTheRecordWhileANodeOfTheOldDeviceIsStillListed()
    {
        FakeBluetoothServices unpaired = FakeBluetoothServices.AirPods();
        unpaired.Remove(RecordedNodes.AirPodsAddress);
        FakeNodeApi table = RecordedNodes.TableWithHeadphones();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            table[id].Present = false;
        }

        using var h = new Harness(table, unpaired);
        h.Pin(RecordedNodes.AirPods());
        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord { DisabledServices = { ProtectedServices.Handsfree } }).Ok);

        Assert.AreEqual(GateExitCode.OtherDeviceProtected, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Store.ReadProtection().Value!.DisabledServices);
    }

    // The node table with the headphones and without any node of the AirPods, as after removing them in Windows.
    private static FakeNodeApi WithoutTheAirPods() =>
        new(RecordedNodes.TableWithHeadphones().Nodes.Where(n => n.Container != RecordedNodes.AirPodsContainer));

    [TestMethod]
    public void SetDeviceRefusesToMoveThePinWhileProtectionCannotBeRead()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.Pin(RecordedNodes.AirPods());
        File.WriteAllText(h.Store.ProtectionFile, "{ not json");

        Assert.AreEqual(GateExitCode.OtherDeviceProtected, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
    }

    [TestMethod]
    public void SetDeviceMovesThePinWhenNothingIsRecordedOrBlocked()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.Pin(RecordedNodes.AirPods());
        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord()).Ok);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));

        Assert.AreEqual(RecordedNodes.HeadphonesContainer, h.Store.ReadDevice().Value!.ContainerId);
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

        Assert.AreEqual(GateExitCode.Rejected, h.Run(GateVerbs.SetDevice, "0a1b2c3d4e8c"));
        Assert.IsFalse(File.Exists(h.Store.DeviceFile));
    }

    [TestMethod]
    public void SetDeviceRefusesToLeaveTheCurrentDeviceBlocked()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.Pin(RecordedNodes.AirPods());
        h.Nodes[RecordedNodes.AirPodsTargets[3]].MarkDisabled(persistent: true);

        Assert.AreEqual(GateExitCode.OtherDeviceBlocked, h.Run(GateVerbs.SetDevice, RecordedNodes.HeadphonesAddress));
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
        Assert.IsTrue(store.WriteConfig(new GateConfig()).Ok);

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
        File.Delete(h.Store.ConfigFile);

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
    [DataRow(GateVerbs.ProtectOn, false)]
    [DataRow(GateVerbs.ProtectOff, false)]
    [DataRow(GateVerbs.Block, true)]
    [DataRow(GateVerbs.Allow, true)]
    [DataRow(GateVerbs.SetBootOn, true)]
    [DataRow(GateVerbs.Boot, true)]
    public void AVerbInTheWrongModeIsRejectedBeforeAnythingIsRead(string verb, bool protectMode)
    {
        GateMode mode = protectMode ? GateMode.Protect : GateMode.Gate;
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());

        GateExitCode exit = new GateActions(h.Nodes, h.Store, h.Folders, h.Log, new ManualTime()).Run(new GateRequest(verb, Nonce, null, mode));

        Assert.AreEqual(GateExitCode.Rejected, exit);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsFalse(File.Exists(h.Store.StatusFile(Nonce)), "Nothing is read or written for a request in the wrong mode.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "does not run in this mode"));
    }

    [TestMethod]
    public void ProtectVerbsAreNotAvailableWithoutABluetoothServiceApi()
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

    // Nothing may end the gate without a status file: an unexpected failure is a step with its own code.
    [TestMethod]
    public void AVerbThatStopsWithAnExceptionStillWritesItsStatusFile()
    {
        using var h = new Harness();
        h.Pin(RecordedNodes.AirPods());
        var actions = new GateActions(new ThrowingNodes(), h.Store, h.Folders, h.Log, new ManualTime());

        GateExitCode exit = actions.Run(new GateRequest(GateVerbs.Block, Nonce, null));

        Assert.AreEqual(GateExitCode.Failed, exit);
        GateStatusFile status = h.Status();
        Assert.AreEqual("failed", status.Result);
        StepOutcome stopped = status.Steps.Single(s => s.Step == GateVerbs.Block + ElevatedFailure.StepSuffix);
        Assert.IsFalse(stopped.Ok);
        Assert.Contains("InvalidOperationException", stopped.Detail!);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "stopped with InvalidOperationException"));
    }

    // Every read throws, so a verb that does not guard would take the process down with it.
    private sealed class ThrowingNodes : INodeApi
    {
        public uint ListDeviceIds(out string[] ids) => throw new InvalidOperationException("The device list is unavailable in this test.");

        public uint Locate(string instanceId, bool includeNonPresent, out uint devInst) => throw new InvalidOperationException("no locate");

        public uint GetStatus(uint devInst, out uint status, out uint problem) => throw new InvalidOperationException("no status");

        public uint GetContainerId(uint devInst, out Guid containerId) => throw new InvalidOperationException("no container");

        public uint GetConfigFlags(uint devInst, out uint configFlags) => throw new InvalidOperationException("no flags");

        public uint GetName(uint devInst, out string? name) => throw new InvalidOperationException("no name");

        public uint Disable(uint devInst, uint flags) => throw new InvalidOperationException("no disable");

        public uint Enable(uint devInst) => throw new InvalidOperationException("no enable");
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
