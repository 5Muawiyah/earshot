using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// gate protect-on, protect-off and the uninstall restore, run through the real GateActions over the recorded
// node table and an in-memory Bluetooth service stack. No real Bluetooth call, node change or machine folder
// is involved: the fake node table is given the fake Bluetooth stack, and the gate refuses the real one for it.
[TestClass]
public sealed class ProtectionGateTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    internal sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        private readonly bool _withBluetooth;

        public Harness(bool pair = true, bool pin = true)
        {
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Machine);
            Store = new GateStore(Machine);
            _withBluetooth = pair;
            if (pin)
            {
                Assert.IsTrue(Store.WriteDevice(RecordedNodes.AirPods()).Ok);
            }
        }

        public string Machine { get; }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public FakeBluetoothServices Bluetooth { get; } = FakeBluetoothServices.AirPods();

        public FakeDevice AirPods => Bluetooth[RecordedNodes.AirPodsAddress];

        public CapturingLog Log { get; } = new();

        public ProtectionIntentFile Intent => new(Machine);

        public IBluetoothServiceApi? Api => _withBluetooth ? Bluetooth : null;

        public GateExitCode Run(string verb) =>
            new GateActions(Nodes, Store, new FakeFolderSecurity(), Log, new ManualTime(), bluetooth: Api).Run(new GateRequest(verb, Nonce, null, GateMode.Protect));

        public GateRunContext Context(string verb, List<StepOutcome> steps) =>
            new(verb, Nonce, RecordedNodes.AirPods(), Nodes, Store, Log, steps, Api);

        public GateStatusFile Status()
        {
            GateRead<GateStatusFile> read = Store.ReadStatus(Nonce);
            Assert.IsTrue(read.IsOk, read.Step.Detail);
            return read.Value!;
        }

        public List<Guid> Recorded()
        {
            GateRead<ProtectionRecord> read = Store.ReadProtection();
            if (read.Status == GateReadStatus.Missing)
            {
                return [];
            }

            Assert.IsTrue(read.IsOk, read.Step.Detail);
            return read.Value!.DisabledServices;
        }

        public void Record(params Guid[] services) =>
            Assert.IsTrue(Store.WriteProtection(new ProtectionRecord { DisabledServices = services.ToList() }).Ok);

        public void BlockAll()
        {
            foreach (string id in RecordedNodes.AirPodsTargets)
            {
                Nodes[id].MarkDisabled(persistent: true);
            }
        }

        public void AllowAll()
        {
            foreach (string id in RecordedNodes.AirPodsTargets)
            {
                FakeNode node = Nodes[id];
                node.Status &= ~CfgMgr32.DN_HAS_PROBLEM;
                node.Problem = 0;
                node.ConfigFlags = 0;
            }
        }

        public void Dispose() => _temp.Dispose();
    }

    private static StepOutcome Step(GateStatusFile status, string name) => status.Steps.Single(s => s.Step == name);

    [TestMethod]
    public void ProtectOnTurnsOffHandsfreeOnlyAndRecordsIt()
    {
        using var h = new Harness();

        GateExitCode exit = h.Run(GateVerbs.ProtectOn);

        Assert.AreEqual(GateExitCode.Success, exit);
        CollectionAssert.AreEqual(new[] { new BluetoothCall("set", ProtectedServices.Handsfree, false) }, h.Bluetooth.SetCalls.ToArray());
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
        Assert.Contains(ProtectedServices.AudioSink, h.AirPods.Enabled, "A2DP sink is left on.");
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded());

        GateStatusFile status = h.Status();
        Assert.AreEqual("success", status.Result);
        Assert.AreEqual(nameof(BlockState.Allowed), status.State);
        StepOutcome handsfree = Step(status, "bt-service-disable:Handsfree");
        Assert.IsTrue(handsfree.Ok);
        Assert.AreEqual("ERROR_SUCCESS", handsfree.CodeName);
        Assert.IsFalse(status.Steps.Any(s => s.Step == "bt-service-disable:Headset"), "Headset was not called, so it has no call step.");
        StepOutcome headset = Step(status, "bt-service-disable-skip:Headset");
        Assert.IsTrue(headset.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, headset.Code, "A call that was not made never carries 0.");
        Assert.AreEqual("NOT_ATTEMPTED", headset.CodeName);
        Assert.Contains("was not called", headset.Detail!);
        Assert.StartsWith("Protection Protected.", Step(status, ProtectionGateRunner.ServicesAfterStep).Detail!);
        Assert.IsEmpty(h.Nodes.Calls, "No node is changed by a protect verb.");
    }

    [TestMethod]
    public void TheRecordIsWrittenBeforeTheServiceCall()
    {
        using var h = new Harness();
        List<Guid>? recordedAtCall = null;
        h.Bluetooth.OnSet = (_, _) => recordedAtCall = h.Recorded();

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        Assert.IsNotNull(recordedAtCall);
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, recordedAtCall);
    }

    [TestMethod]
    public void AServiceAlreadyOffIsNotCalledAndNotRecorded()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.IsEmpty(h.Recorded());
    }

    [TestMethod]
    public void AlreadyInStateFromAnIncompleteListIsNotAnErrorAndIsNotRecorded()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);
        h.Bluetooth.ServicesResult = BluetoothApis.ERROR_MORE_DATA;
        h.Bluetooth.IncompleteList = [FakeBluetoothServices.Sdp, ProtectedServices.AudioSink];

        GateExitCode exit = h.Run(GateVerbs.ProtectOn);

        Assert.AreEqual(GateExitCode.Success, exit);
        GateStatusFile status = h.Status();
        StepOutcome handsfree = Step(status, "bt-service-disable:Handsfree");
        Assert.IsTrue(handsfree.Ok);
        Assert.AreEqual("E_INVALIDARG", handsfree.CodeName);
        Assert.AreEqual(unchecked((int)0x80070057), handsfree.Code);
        StepOutcome headset = Step(status, "bt-service-disable:Headset");
        Assert.IsTrue(headset.Ok, "1060 is nothing to do.");
        Assert.AreEqual("ERROR_SERVICE_DOES_NOT_EXIST", headset.CodeName);
        Assert.IsEmpty(h.Recorded(), "Earshot changed nothing, so restore must turn nothing on.");
        Assert.IsTrue(Step(status, ServiceStateReader.ServicesStep).Ok, "ERROR_MORE_DATA is a successful read.");
    }

    [TestMethod]
    [DataRow(0x00000000u, "success", "ERROR_SUCCESS", true, true)]
    [DataRow(0x80070057u, "success", "E_INVALIDARG", true, false)]
    [DataRow(0x00000424u, "success", "ERROR_SERVICE_DOES_NOT_EXIST", true, false)]
    [DataRow(0x00000057u, "failed", "ERROR_INVALID_PARAMETER", false, false)]
    [DataRow(0x00000005u, "failed", "ERROR_ACCESS_DENIED", false, false)]
    [DataRow(0x0000048Fu, "failed", "ERROR_DEVICE_NOT_CONNECTED", false, false)]
    public void EachHandsfreeResultMapsToItsOutcome(uint rc, string result, string codeName, bool stepOk, bool recorded)
    {
        using var h = new Harness();
        h.Bluetooth.SetResult = (service, _) => service == ProtectedServices.Handsfree ? rc : null;

        h.Run(GateVerbs.ProtectOn);

        GateStatusFile status = h.Status();
        Assert.AreEqual(result, status.Result);
        StepOutcome step = Step(status, "bt-service-disable:Handsfree");
        Assert.AreEqual(codeName, step.CodeName);
        Assert.AreEqual(unchecked((int)rc), step.Code);
        Assert.AreEqual(stepOk, step.Ok);
        Assert.AreEqual(recorded, h.Recorded().Contains(ProtectedServices.Handsfree));
        if (!stepOk)
        {
            Assert.IsTrue(h.Log.Has(LogLevel.Warn, codeName), "A failed service call is logged, never swallowed.");
        }
    }

    [TestMethod]
    [DataRow(0x0000048Fu)]
    [DataRow(0x00000005u)]
    [DataRow(0x00000057u)]
    public void AFailedDisableThatStillTookStaysRecordedAndProtectOffTurnsItBackOn(uint rc)
    {
        using var h = new Harness();
        h.Bluetooth.OnSet = (service, enable) =>
        {
            if (service == ProtectedServices.Handsfree && !enable)
            {
                h.AirPods.Apply(service, enable);
            }
        };
        h.Bluetooth.SetResult = (service, enable) => service == ProtectedServices.Handsfree && !enable ? rc : null;

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOn));

        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled, "The driver went although the call failed.");
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded(), "Earshot may have turned it off, so restore must know.");
        GateStatusFile status = h.Status();
        Assert.IsFalse(Step(status, "bt-service-disable:Handsfree").Ok);
        StepOutcome kept = Step(status, ProtectionGateRunner.RecordKeptStepPrefix + "Handsfree");
        Assert.IsTrue(kept.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, kept.Code);
        Assert.Contains("no longer has it", kept.Detail!);

        h.Bluetooth.OnSet = null;
        h.Bluetooth.SetResult = null;
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOff));

        Assert.Contains(ProtectedServices.Handsfree, h.AirPods.Enabled);
        Assert.IsEmpty(h.Recorded());
    }

    [TestMethod]
    public void AFailedDisableIsKeptWhenTheServicesCannotBeReadAgain()
    {
        using var h = new Harness();
        h.Bluetooth.OnSet = (_, _) => h.Bluetooth.ServicesResult = 1167;
        h.Bluetooth.SetResult = (service, _) => service == ProtectedServices.Handsfree ? BluetoothApis.ERROR_ACCESS_DENIED : null;

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOn));

        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded());
        GateStatusFile status = h.Status();
        Assert.IsFalse(Step(status, ProtectionGateRunner.ServicesAfterStep).Ok);
        Assert.Contains("could not be read again", Step(status, ProtectionGateRunner.RecordKeptStepPrefix + "Handsfree").Detail!);
    }

    [TestMethod]
    public void AFailedDisableThatAnIncompleteReadBackStillListsIsTakenOut()
    {
        using var h = new Harness();
        h.Bluetooth.ServicesResult = BluetoothApis.ERROR_MORE_DATA;
        h.Bluetooth.IncompleteList = [ProtectedServices.Handsfree];
        h.Bluetooth.SetResult = (service, _) => service == ProtectedServices.Handsfree ? BluetoothApis.ERROR_ACCESS_DENIED : null;

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.ProtectOn), "Headset was called on the incomplete list and is not on the device.");

        Assert.IsEmpty(h.Recorded(), "The list shows it is still on, so Earshot did not turn it off.");
        Assert.IsFalse(h.Status().Steps.Any(st => st.Step.StartsWith(ProtectionGateRunner.RecordKeptStepPrefix, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void HeadsetIsTurnedOffWhenADeviceHasIt()
    {
        using var h = new Harness();
        h.AirPods.AddSupported(ProtectedServices.Headset);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        CollectionAssert.AreEqual(
            new[] { new BluetoothCall("set", ProtectedServices.Handsfree, false), new BluetoothCall("set", ProtectedServices.Headset, false) },
            h.Bluetooth.SetCalls.ToArray());
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree, ProtectedServices.Headset }, h.Recorded());
    }

    [TestMethod]
    public void OneServiceFailingOfTwoIsPartial()
    {
        using var h = new Harness();
        h.AirPods.AddSupported(ProtectedServices.Headset);
        h.Bluetooth.SetResult = (service, _) => service == ProtectedServices.Headset ? BluetoothApis.ERROR_ACCESS_DENIED : null;

        Assert.AreEqual(GateExitCode.Partial, h.Run(GateVerbs.ProtectOn));
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded());
    }

    [TestMethod]
    public void ARequestWhileTheDeviceNodeIsBlockedChangesNothingAndIsKeptForTheNextAllow()
    {
        using var h = new Harness();
        h.Nodes[RecordedNodes.AirPodsDeviceNode].MarkDisabled(persistent: true);

        GateExitCode exit = h.Run(GateVerbs.ProtectOn);

        Assert.AreEqual(ProtectionGateRunner.BlockedExit, exit);
        Assert.IsEmpty(h.Bluetooth.Calls, "No Bluetooth call at all while the device node is disabled.");
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.Contains(ProtectedServices.Handsfree, h.AirPods.Enabled);
        GateStatusFile status = h.Status();
        StepOutcome refused = Step(status, ProtectionGateRunner.RefusedStep);
        Assert.IsFalse(refused.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, refused.Code);
        Assert.StartsWith("The device node is blocked", refused.Detail!);
        Assert.AreEqual((int)ProtectionGateRunner.BlockedExit, status.ExitCode);
        Assert.AreEqual(nameof(BlockState.Mixed), status.State);

        GateRead<ProtectionIntent> intent = h.Intent.Read();
        Assert.IsTrue(intent.IsOk, intent.Step.Detail);
        Assert.IsTrue(intent.Value!.Protect);
    }

    [TestMethod]
    public void ARequestThatCannotBeKeptWhileBlockedIsAFailureNotSaved()
    {
        using var h = new Harness();
        h.BlockAll();
        Directory.CreateDirectory(h.Intent.FilePath);

        GateExitCode exit = h.Run(GateVerbs.ProtectOn);

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsEmpty(h.Bluetooth.Calls);
        GateStatusFile status = h.Status();
        Assert.AreEqual("failed", status.Result);
        Assert.IsFalse(Step(status, "write-protection-intent").Ok);
        Assert.IsFalse(Step(status, ProtectionGateRunner.RefusedStep).Ok);
    }

    [TestMethod]
    public void ARequestWhileBlockedAtBootKeepsTheOffRequestToo()
    {
        using var h = new Harness();
        h.Record(ProtectedServices.Handsfree);
        h.BlockAll();

        Assert.AreEqual(ProtectionGateRunner.BlockedExit, h.Run(GateVerbs.ProtectOff));

        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.AreEqual(nameof(BlockState.Blocked), h.Status().State);
        Assert.IsFalse(h.Intent.Read().Value!.Protect);
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded(), "The record is untouched.");
    }

    [TestMethod]
    public void ABlockedServiceNodeAlsoStopsTheRequest()
    {
        using var h = new Harness();
        string handsfreeNode = RecordedNodes.AirPodsTargets.Single(id => id.Contains("{0000111E", StringComparison.Ordinal));
        h.Nodes[handsfreeNode].MarkDisabled(persistent: false);

        Assert.AreEqual(ProtectionGateRunner.BlockedExit, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.StartsWith("A service node is blocked", Step(h.Status(), ProtectionGateRunner.RefusedStep).Detail!);
    }

    [TestMethod]
    public void ANonPresentServiceNodeStillFlaggedDisabledDoesNotStopTheRequest()
    {
        using var h = new Harness();
        string handsfreeNode = RecordedNodes.AirPodsTargets.Single(id => id.Contains("{0000111E", StringComparison.Ordinal));
        FakeNode node = h.Nodes[handsfreeNode];
        node.MarkDisabled(persistent: true);
        node.Present = false;

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
        GateStatusFile status = h.Status();
        Assert.AreEqual(nameof(BlockState.Allowed), status.State, "The gate reads the AirPods as allowed, as the tray does.");
        Assert.IsFalse(status.Steps.Any(st => st.Step == ProtectionGateRunner.RefusedStep));
        Assert.IsFalse(File.Exists(h.Intent.FilePath), "Nothing is kept for an allow that is not needed.");
    }

    [TestMethod]
    public void AGoneDeviceNodeStillFlaggedDisabledKeepsTheRequest()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].Present = false;
        }

        h.Nodes[RecordedNodes.AirPodsDeviceNode].ConfigFlags |= CfgMgr32.CONFIGFLAG_DISABLED;

        Assert.AreEqual(ProtectionGateRunner.BlockedExit, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.StartsWith("The device node is blocked", Step(h.Status(), ProtectionGateRunner.RefusedStep).Detail!);
        Assert.IsTrue(h.Intent.Read().Value!.Protect);
    }

    [TestMethod]
    public void AGoneDeviceNodeWithoutTheFlagIsNotPresentRatherThanBlocked()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].Present = false;
        }

        Assert.AreEqual(GateExitCode.NotPresent, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.IsFalse(File.Exists(h.Intent.FilePath));
    }

    [TestMethod]
    public void TheBlockedRuleCountsWhatTheTrayCounts()
    {
        const string service = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000";
        static BluetoothNode Node(string id, bool present, NodeBlockStatus status, bool flag) =>
            new(id, "BTHENUM", null, present, status, status == NodeBlockStatus.Disabled ? CfgMgr32.CM_PROB_DISABLED : 0, flag);

        BluetoothNode presentDisabled = Node(service, present: true, NodeBlockStatus.Disabled, flag: false);
        BluetoothNode presentEnabledFlagged = Node(service, present: true, NodeBlockStatus.Enabled, flag: true);
        BluetoothNode goneServiceFlagged = Node(service, present: false, NodeBlockStatus.Unknown, flag: true);
        BluetoothNode goneDeviceFlagged = Node(RecordedNodes.AirPodsDeviceNode, present: false, NodeBlockStatus.Unknown, flag: true);
        BluetoothNode goneDevice = Node(RecordedNodes.AirPodsDeviceNode, present: false, NodeBlockStatus.Unknown, flag: false);

        CollectionAssert.AreEqual(
            new[] { presentDisabled, goneDeviceFlagged },
            ProtectionGateRunner.BlockedNodes([presentDisabled, presentEnabledFlagged, goneServiceFlagged, goneDeviceFlagged, goneDevice]).ToArray());

        // Wherever the gate refuses a present device, the tray does not read Allowed.
        BluetoothNode device = Node(RecordedNodes.AirPodsDeviceNode, present: true, NodeBlockStatus.Enabled, flag: true);
        foreach (BluetoothNode other in new[] { presentDisabled, presentEnabledFlagged, goneServiceFlagged })
        {
            var read = new NodeReadResult(true, [device, other], []);
            bool refused = ProtectionGateRunner.BlockedNodes(read.Nodes).Count > 0;
            BlockState tray = BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, read);
            Assert.AreEqual(refused, tray != BlockState.Allowed, other + " " + tray);
        }
    }

    [TestMethod]
    public void AfterTheAllowTheKeptRequestIsAppliedAndCleared()
    {
        using var h = new Harness();
        h.BlockAll();
        Assert.AreEqual(ProtectionGateRunner.BlockedExit, h.Run(GateVerbs.ProtectOn));
        Assert.IsTrue(File.Exists(h.Intent.FilePath));

        h.AllowAll();
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        Assert.IsFalse(File.Exists(h.Intent.FilePath), "A completed request replaces the kept one.");
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
    }

    [TestMethod]
    public void ProtectOffTurnsBackOnOnlyWhatEarshotTurnedOff()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));
        h.AirPods.TurnOffOutside(FakeBluetoothServices.Avrcp);
        h.Bluetooth.Calls.Clear();

        GateExitCode exit = h.Run(GateVerbs.ProtectOff);

        Assert.AreEqual(GateExitCode.Success, exit);
        CollectionAssert.AreEqual(new[] { new BluetoothCall("set", ProtectedServices.Handsfree, true) }, h.Bluetooth.SetCalls.ToArray());
        Assert.Contains(ProtectedServices.Handsfree, h.AirPods.Enabled);
        Assert.DoesNotContain(FakeBluetoothServices.Avrcp, h.AirPods.Enabled, "A service turned off outside Earshot stays off.");
        Assert.IsEmpty(h.Recorded());
        Assert.StartsWith("Protection NotProtected.", Step(h.Status(), ProtectionGateRunner.ServicesAfterStep).Detail!);
    }

    [TestMethod]
    public void ProtectOffWithNothingRecordedMakesNoBluetoothCall()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOff));

        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
    }

    [TestMethod]
    public void TurningBackOnAServiceAlreadyOnOnlyUpdatesTheRecord()
    {
        using var h = new Harness();
        h.Record(ProtectedServices.Handsfree);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOff));

        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.IsEmpty(h.Recorded());
        StepOutcome skipped = Step(h.Status(), "bt-service-enable-skip:Handsfree");
        Assert.IsTrue(skipped.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, skipped.Code);
        Assert.IsFalse(h.Status().Steps.Any(s => s.Step == "bt-service-enable:Handsfree"));
    }

    [TestMethod]
    public void AFailedEnableKeepsTheEntryForTheNextTry()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);
        h.Record(ProtectedServices.Handsfree);
        h.Bluetooth.SetResult = (_, _) => BluetoothApis.ERROR_ACCESS_DENIED;

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOff));

        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded());
        Assert.AreEqual("ERROR_ACCESS_DENIED", Step(h.Status(), "bt-service-enable:Handsfree").CodeName);
    }

    [TestMethod]
    public void AnEntryForAnotherServiceIsNeverTurnedOn()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(FakeBluetoothServices.Avrcp);
        h.Record(FakeBluetoothServices.Avrcp);

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOff));

        Assert.IsEmpty(h.Bluetooth.SetCalls);
        CollectionAssert.AreEqual(new[] { FakeBluetoothServices.Avrcp }, h.Recorded());
    }

    [TestMethod]
    public void UninstallRestoresOnlyTheRecordedServices()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));
        h.AirPods.TurnOffOutside(FakeBluetoothServices.Avrcp);
        h.Bluetooth.Calls.Clear();
        string source = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(h.Machine)!)!, "unzip");
        string install = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(h.Machine)!)!, "ProgramFiles", "Earshot");

        InstallResult result = new UninstallActions(new InstallLayout(source, install, h.Machine), new FakeFolderSecurity(), h.Nodes,
            new FakeTaskRegistrar(), new RebootDeleteRecorder(), h.Log, bluetooth: h.Bluetooth).Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, string.Join(Environment.NewLine, result.Steps.Where(s => !s.Ok).Select(GateActions.Describe)));
        CollectionAssert.AreEqual(new[] { new BluetoothCall("set", ProtectedServices.Handsfree, true) }, h.Bluetooth.SetCalls.ToArray());
        Assert.DoesNotContain(FakeBluetoothServices.Avrcp, h.AirPods.Enabled);
        Assert.IsFalse(result.Steps.Any(s => s.Step == "protection-restore"), "The restore hook ran, so nothing is reported as not available.");
    }

    [TestMethod]
    public void RestoreWithNothingRecordedTouchesNothingEvenWhileBlocked()
    {
        using var h = new Harness();
        h.BlockAll();
        var steps = new List<StepOutcome>();
        GateRunContext ctx = h.Context("uninstall", steps);

        Assert.IsTrue(GateActions.TryRestoreProtection(ctx));

        Assert.AreEqual(GateExitCode.Success, ctx.Outcome);
        Assert.IsEmpty(h.Bluetooth.Calls);
    }

    [TestMethod]
    public void RestoreWhileStillBlockedCallsNoService()
    {
        using var h = new Harness();
        h.Record(ProtectedServices.Handsfree);
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);
        h.BlockAll();
        var steps = new List<StepOutcome>();
        GateRunContext ctx = h.Context("uninstall", steps);

        Assert.IsTrue(GateActions.TryRestoreProtection(ctx));

        Assert.AreEqual(ProtectionGateRunner.BlockedExit, ctx.Outcome);
        Assert.IsEmpty(h.Bluetooth.Calls);
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded());
    }

    [TestMethod]
    public void AProtectVerbChangesNothingWhileAnotherDeviceChangeHoldsTheLock()
    {
        using var h = new Harness();
        using FileStream other = HoldLock(h);
        int waits = 0;
        var runner = new ProtectionGateRunner(h.Bluetooth, _ => ++waits > 0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var steps = new List<StepOutcome>();
        GateRunContext ctx = h.Context(GateVerbs.ProtectOn, steps);

        runner.Run(ctx, protect: true);

        Assert.AreEqual(GateExitCode.Failed, ctx.Outcome);
        Assert.AreEqual(4, waits, "One second in 250 ms polls.");
        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.IsFalse(File.Exists(h.Intent.FilePath));
        StepOutcome busy = steps.Single(s => s.Step == DeviceChangeLock.StepName);
        Assert.IsTrue(DeviceChangeLock.IsBusy(busy), busy.Detail);
        Assert.AreEqual("ERROR_SHARING_VIOLATION", busy.CodeName);
        Assert.IsFalse(steps.Any(s => s.Step == ProtectionGateRunner.RefusedStep), "The nodes are not even read without the lock.");
    }

    [TestMethod]
    public void AProtectVerbGoesAheadOnceTheOtherChangeReleasesTheLock()
    {
        using var h = new Harness();
        using FileStream other = HoldLock(h);
        var runner = new ProtectionGateRunner(h.Bluetooth, _ =>
        {
            other.Dispose();
            return true;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var steps = new List<StepOutcome>();
        GateRunContext ctx = h.Context(GateVerbs.ProtectOn, steps);

        runner.Run(ctx, protect: true);

        Assert.AreEqual(GateExitCode.Success, ctx.Outcome);
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
        StepOutcome taken = steps.Single(s => s.Step == DeviceChangeLock.StepName);
        Assert.IsTrue(taken.Ok);
        Assert.Contains("after waiting 0.25 s", taken.Detail!);
        using FileStream again = HoldLock(h);
    }

    [TestMethod]
    public void TheRestoreAlsoWaitsForTheLock()
    {
        using var h = new Harness();
        h.Record(ProtectedServices.Handsfree);
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);
        using FileStream other = HoldLock(h);
        var runner = new ProtectionGateRunner(h.Bluetooth, _ => true, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var steps = new List<StepOutcome>();
        GateRunContext ctx = h.Context("uninstall", steps);

        runner.Restore(ctx);

        Assert.AreEqual(GateExitCode.Failed, ctx.Outcome);
        Assert.IsEmpty(h.Bluetooth.Calls);
        CollectionAssert.AreEqual(new[] { ProtectedServices.Handsfree }, h.Recorded());
        Assert.IsTrue(steps.Any(DeviceChangeLock.IsBusy));
    }

    [TestMethod]
    public void TheVerbTakesTheLockThroughTheGateAndReleasesIt()
    {
        using var h = new Harness();

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        Assert.IsTrue(Step(h.Status(), DeviceChangeLock.StepName).Ok);
        using FileStream after = HoldLock(h);
    }

    [TestMethod]
    public void TheLockTimeoutsOutlastTheOtherTasksTimeLimits()
    {
        Assert.IsGreaterThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.GateTimeLimit), ProtectionGateRunner.ProtectLockTimeout);
        Assert.IsLessThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.ProtectTimeLimit), ProtectionGateRunner.ProtectLockTimeout, "Time is left for the service calls.");
        Assert.IsGreaterThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.ProtectTimeLimit), ProtectionGateRunner.RestoreLockTimeout);
    }

    // What a block or allow holding the lock looks like from here: a write open that shares only read.
    private static FileStream HoldLock(Harness h) =>
        new(Path.Combine(h.Machine, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

    [TestMethod]
    public void WithoutABluetoothApiTheVerbsStayNotAvailable()
    {
        using var h = new Harness(pair: false);

        Assert.AreEqual(GateExitCode.NotAvailable, h.Run(GateVerbs.ProtectOn));
        Assert.AreEqual(GateExitCode.NotAvailable, h.Run(GateVerbs.ProtectOff));

        Assert.IsEmpty(h.Bluetooth.Calls);
    }

    [TestMethod]
    public void OnlyTheRealNodeApiGoesWithTheRealBluetoothApi()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        var log = new CapturingLog();

        // Constructing either API makes no native call.
        Assert.IsInstanceOfType<BluetoothServiceApi>(GateActions.ForMachine(temp.Path, log).Bluetooth);
        Assert.ThrowsExactly<ArgumentException>(() => new GateActions(RecordedNodes.Table(), store, new FakeFolderSecurity(), log, new ManualTime(), bluetooth: new BluetoothServiceApi()));
        Assert.ThrowsExactly<ArgumentException>(() => new GateActions(new CfgMgr32NodeApi(), store, new FakeFolderSecurity(), log, new ManualTime(), bluetooth: FakeBluetoothServices.AirPods()));
        Assert.ThrowsExactly<ArgumentException>(() => new GateRunContext("uninstall", Nonce, RecordedNodes.AirPods(), RecordedNodes.Table(), store, log, [], new BluetoothServiceApi()));
        Assert.ThrowsExactly<ArgumentException>(() => new UninstallActions(new InstallLayout(temp.Path, temp.Path, temp.Path), new FakeFolderSecurity(),
            RecordedNodes.Table(), new FakeTaskRegistrar(), new RebootDeleteRecorder(), log, bluetooth: new BluetoothServiceApi()));
    }

    [TestMethod]
    public void ADeviceThatIsNotPairedIsNotFound()
    {
        using var h = new Harness();
        h.Bluetooth.Remove(RecordedNodes.AirPodsAddress);

        Assert.AreEqual(GateExitCode.NotFound, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.AreEqual("ERROR_NOT_FOUND", Step(h.Status(), BluetoothDeviceLookup.FindStep).CodeName);
    }

    [TestMethod]
    public void AFailedDeviceListChangesNothing()
    {
        using var h = new Harness();
        h.Bluetooth.FindResult = BluetoothApis.ERROR_REVISION_MISMATCH;

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.AreEqual("ERROR_REVISION_MISMATCH", Step(h.Status(), BluetoothDeviceLookup.FindStep).CodeName);
    }

    [TestMethod]
    public void AFailedServiceReadChangesNothing()
    {
        using var h = new Harness();
        h.Bluetooth.ServicesResult = 1167;

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.SetCalls);
        StepOutcome read = Step(h.Status(), ServiceStateReader.ServicesStep);
        Assert.IsFalse(read.Ok);
        Assert.AreEqual("ERROR_DEVICE_NOT_CONNECTED", read.CodeName);
    }

    [TestMethod]
    public void AnInvalidRecordIsNeverOverwritten()
    {
        using var h = new Harness();
        File.WriteAllText(h.Store.ProtectionFile, "{}");

        Assert.AreEqual(GateExitCode.Failed, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.Calls);
        Assert.AreEqual("{}", File.ReadAllText(h.Store.ProtectionFile));
    }

    [TestMethod]
    public void ADeviceNodeThatIsNotPresentChangesNothing()
    {
        using var h = new Harness();
        h.Nodes[RecordedNodes.AirPodsDeviceNode].Present = false;

        Assert.AreEqual(GateExitCode.NotPresent, h.Run(GateVerbs.ProtectOn));

        Assert.IsEmpty(h.Bluetooth.Calls);
    }

    [TestMethod]
    public void TheIPhoneIsNeverTouched()
    {
        using var h = new Harness();
        FakeDevice iPhone = h.Bluetooth[RecordedNodes.IPhoneAddress];
        iPhone.AddSupported(ProtectedServices.Handsfree);

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.ProtectOn));

        Assert.Contains(ProtectedServices.Handsfree, iPhone.Enabled);
    }
}
