using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The tray-side controller end to end against fakes: the fake scheduler plays the gate by running the real
// GateActions on the fake node table, so a request crosses the same code as it would for real. No task,
// node, UAC prompt or machine folder is touched.
[TestClass]
public sealed class BlockControllerTests
{
    private const string InstallFolder = @"C:\Program Files\Earshot";
    private const string Exe = @"C:\Users\owner\Downloads\Earshot\Earshot.exe";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();
        private readonly SystemWorker _worker;

        public Harness(bool setUp = true, FakeNodeApi? nodes = null)
        {
            Nodes = nodes ?? RecordedNodes.Table();
            string machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(machine);
            Store = new GateStore(machine);
            Settings.Current.PinnedAddress = RecordedNodes.AirPodsAddress;
            Settings.Current.PinnedContainerId = RecordedNodes.AirPodsContainer;
            if (setUp)
            {
                SetUp();
            }

            Tasks.OnRun = PlayTheGate;
            var gate = new TaskSchedulerGate(Tasks, Store, InstallFolder, TestUsers.Sid, Lookups.None, Time, (delay, ct) =>
            {
                Time.Advance(delay);
                return !ct.IsCancellationRequested;
            });
            _worker = new SystemWorker(Log);
            Controller = new BlockController(Log, Settings, Store, Nodes, gate, Launcher, TestUsers.Sid, Exe, _worker);
        }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; }

        public FakeScheduledTasks Tasks { get; } = new();

        public FakeSettings Settings { get; } = new();

        public FakeLauncher Launcher { get; } = new();

        public CapturingLog Log { get; } = new();

        public ManualTime Time { get; } = new();

        public BlockController Controller { get; }

        public bool GateActs { get; set; } = true;

        public void SetUp()
        {
            Tasks.InstallAll(InstallFolder);
            Assert.IsTrue(Store.WriteDevice(RecordedNodes.AirPods()).Ok);
            Assert.IsTrue(Store.WriteConfig(new GateConfig()).Ok);
        }

        // What \Earshot\Gate does when started: the real gate actions, then the scheduler state moves on.
        private void PlayTheGate(string[] parameters)
        {
            int exit = 0;
            if (GateActs)
            {
                exit = (int)new GateActions(Nodes, Store, new FakeFolderSecurity(), Log, Time)
                    .Run(new GateRequest(parameters[0], parameters[1], parameters.Length > 2 ? parameters[2] : null));
            }

            if (GateActs)
            {
                Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, exit, Tasks.Current.LastRunTime + 1);
            }
        }

        public void Dispose()
        {
            Controller.Dispose();
            _worker.Dispose();
            _temp.Dispose();
        }
    }

    [TestMethod]
    public async Task StatusBeforeSetupIsNotSetUpAndShowsThePinnedNodes()
    {
        using var h = new Harness(setUp: false);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.NotSetUp, status.State);
        Assert.IsFalse(status.TasksInstalled);
        Assert.IsTrue(status.BlockAtBoot, "The shipped default before setup.");
        Assert.AreEqual(RecordedNodes.AirPodsContainer, status.TargetContainerId);
        Assert.HasCount(9, status.Nodes);
        Assert.IsFalse(h.Controller.IsSetUp);
    }

    [TestMethod]
    public async Task StatusAfterSetupFollowsTheNodes()
    {
        using var h = new Harness();

        BootBlockStatus allowed = await h.Controller.GetStatusAsync();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        BootBlockStatus blocked = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.Allowed, allowed.State);
        Assert.IsTrue(allowed.TasksInstalled);
        Assert.IsTrue(h.Controller.IsSetUp);
        Assert.AreEqual(BlockState.Blocked, blocked.State);
        Assert.IsTrue(blocked.Nodes.All(n => n.ConfigFlagsDisabledBit));
        Assert.IsEmpty(h.Tasks.Runs, "Status never runs the gate.");
    }

    [TestMethod]
    public async Task ATaskThatNeedsRepairMeansNotSetUp()
    {
        using var h = new Harness();
        TaskReadback good = h.Tasks.Tasks[@"\Earshot\BootBlock"];
        h.Tasks.Tasks[@"\Earshot\BootBlock"] = good with { Sddl = good.Sddl + "(A;;FW;;;BU)" };

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.NotSetUp, status.State);
        Assert.IsFalse(h.Controller.IsSetUp);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "task-check:BootBlock"));
    }

    [TestMethod]
    public async Task StatusWithNothingPinnedIsNotFound()
    {
        using var h = new Harness();
        File.Delete(h.Store.DeviceFile);
        h.Settings.Current = new EarshotSettings();

        Assert.AreEqual(BlockState.NotFound, (await h.Controller.GetStatusAsync()).State);
    }

    [TestMethod]
    public async Task BlockRunsTheGateAndConfirmsWithTheNodes()
    {
        using var h = new Harness();

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(BlockController.BlockedMessage, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
        Assert.AreEqual(GateVerbs.Block, h.Tasks.Runs[0][0]);
        Assert.IsTrue(BoundaryValidation.IsNonce(h.Tasks.Runs[0][1]));
        Assert.HasCount(2, h.Tasks.Runs[0], "No address crosses for block.");
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => h.Nodes[id].IsDisabled && (h.Nodes[id].ConfigFlags & 1) != 0));
        Assert.HasCount(9, result.Steps.Where(s => s.Step.StartsWith("cm-disable:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BlockWhenAlreadyBlockedDoesNotRunTheGate()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.AlreadyInState, result.Status);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task BlockBeforeSetupSaysToSetUp()
    {
        using var h = new Harness(setUp: false);

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.NotSetUpMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task BlockWithAMissingTaskSaysToSetUp()
    {
        using var h = new Harness();
        h.Tasks.Tasks.Remove(@"\Earshot\Gate");

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(BlockController.NotSetUpMessage, result.UserMessage);
        Assert.IsFalse(h.Controller.IsSetUp);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task BlockWithTasksButNoDeviceFileNeedsRepair()
    {
        using var h = new Harness();
        File.Delete(h.Store.DeviceFile);

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(BlockController.NeedsRepairMessage, result.UserMessage);
    }

    [TestMethod]
    public async Task BlockOnNodesThatAreNotPresentSaysConnectOnce()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].Present = false;
        }

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual("Connect the AirPods to this PC once from Windows Bluetooth settings, then try Block again.", result.UserMessage);
        Assert.IsTrue(result.Steps.Any(s => s.CodeName == "CR_NO_SUCH_DEVNODE"));
    }

    [TestMethod]
    public async Task ANodeThatWillNotDisableIsPartial()
    {
        using var h = new Harness();
        h.Nodes[RecordedNodes.AirPodsTargets[4]].DisableResult = CfgMgr32.CR_NOT_DISABLEABLE;

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Partial, result.Status);
        Assert.AreEqual(BlockController.PartialBlockMessage, result.UserMessage);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "CR_NOT_DISABLEABLE"));
    }

    [TestMethod]
    public async Task AGateThatNeverRunsTimesOutAndNothingIsClaimed()
    {
        using var h = new Harness();
        h.GateActs = false;

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.TimedOutMessage, result.UserMessage);
    }

    [TestMethod]
    public async Task TheNodeStateOverridesAStatusFileThatClaimsSuccess()
    {
        using var h = new Harness();
        h.Tasks.OnRun = p =>
        {
            Assert.IsTrue(h.Store.WriteStatus(new GateStatusFile(1, p[1], p[0], h.Time.GetUtcNow(), h.Time.GetUtcNow(), "success", 0,
                nameof(BlockState.Blocked), [], false)).Ok);
            h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, 1001);
        };

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.BlockFailedMessage, result.UserMessage);
    }

    [TestMethod]
    public async Task AllowRunsTheGateAndConfirms()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        ControllerResult result = await h.Controller.AllowAsync();

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(BlockController.AllowedMessage, result.UserMessage);
        Assert.AreEqual(GateVerbs.Allow, h.Tasks.Runs.Single()[0]);
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => !h.Nodes[id].IsDisabled));
        Assert.AreEqual(OpStatus.AlreadyInState, (await h.Controller.AllowAsync()).Status);
    }

    // The check before the gate and the read after it follow the same rule, so a node that is not present and
    // not marked disabled gives the same answer every time rather than alternating with "already blocked".
    [TestMethod]
    public async Task RepeatedBlocksWithANodeThatIsNotPresentSayTheSameThing()
    {
        using var h = new Harness();
        h.Nodes[RecordedNodes.AirPodsTargets[2]].Present = false;

        ControllerResult first = await h.Controller.BlockAsync();
        ControllerResult second = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Partial, first.Status, first.UserMessage);
        Assert.AreEqual(BlockController.NotPresentBlockMessage, first.UserMessage);
        Assert.AreEqual(first.Status, second.Status);
        Assert.AreEqual(first.UserMessage, second.UserMessage);
        Assert.HasCount(2, h.Tasks.Runs, "Neither click short-circuits.");
        Assert.HasCount(8, h.Nodes.Calls.Where(c => c.Kind == "disable" && c.InstanceId != RecordedNodes.AirPodsTargets[2]).Select(c => c.InstanceId).Distinct());
    }

    [TestMethod]
    public async Task ANodeThatIsNotPresentButStillMarkedDisabledCountsAsBlocked()
    {
        using var h = new Harness();
        FakeNode gone = h.Nodes[RecordedNodes.AirPodsTargets[2]];
        gone.MarkDisabled(persistent: true);
        gone.Present = false;

        ControllerResult first = await h.Controller.BlockAsync();
        ControllerResult second = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Success, first.Status, first.UserMessage);
        Assert.AreEqual(BlockController.BlockedMessage, first.UserMessage);
        Assert.AreEqual(OpStatus.AlreadyInState, second.Status);
        Assert.AreEqual(BlockController.AlreadyBlockedMessage, second.UserMessage);
    }

    [TestMethod]
    public async Task AnAllowThatLeavesANodeMarkedDisabledIsPartial()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        h.Nodes[RecordedNodes.AirPodsDeviceNode].KeepConfigFlagsOnEnable = true;

        ControllerResult result = await h.Controller.AllowAsync();

        Assert.AreEqual(OpStatus.Partial, result.Status, result.UserMessage);
        Assert.AreEqual(BlockController.AllowNotPersistentMessage, result.UserMessage);
    }

    [TestMethod]
    public async Task BlockAtBootIsChangedThroughTheGateAndReadBack()
    {
        using var h = new Harness();

        ControllerResult off = await h.Controller.SetBlockAtBootAsync(false);
        ControllerResult offAgain = await h.Controller.SetBlockAtBootAsync(false);
        ControllerResult on = await h.Controller.SetBlockAtBootAsync(blockAtBoot: true);

        Assert.AreEqual(OpStatus.Success, off.Status, off.UserMessage);
        Assert.AreEqual(BlockController.BlockAtBootOffMessage, off.UserMessage);
        Assert.AreEqual(OpStatus.AlreadyInState, offAgain.Status);
        Assert.AreEqual(BlockController.BlockAtBootOnMessage, on.UserMessage);
        CollectionAssert.AreEqual(new[] { GateVerbs.SetBootOff, GateVerbs.SetBootOn }, h.Tasks.Runs.Select(r => r[0]).ToArray());
        Assert.IsTrue((await h.Controller.GetStatusAsync()).BlockAtBoot);
    }

    [TestMethod]
    public async Task SetDeviceValidatesThenRePinsThroughTheGate()
    {
        using var h = new Harness(nodes: RecordedNodes.TableWithHeadphones());

        ControllerResult invalid = await h.Controller.SetDeviceAsync("5A6b7C8d9Eaf");
        ControllerResult blockedFirst;
        h.Nodes[RecordedNodes.AirPodsTargets[0]].MarkDisabled(persistent: true);
        blockedFirst = await h.Controller.SetDeviceAsync(RecordedNodes.HeadphonesAddress);
        h.Nodes[RecordedNodes.AirPodsTargets[0]].Status = CfgMgr32.DN_STARTED;
        h.Nodes[RecordedNodes.AirPodsTargets[0]].ConfigFlags = 0;
        ControllerResult chosen = await h.Controller.SetDeviceAsync(RecordedNodes.HeadphonesAddress);

        Assert.AreEqual(BlockController.InvalidAddressMessage, invalid.UserMessage);
        Assert.AreEqual(BlockController.OtherDeviceBlockedMessage, blockedFirst.UserMessage);
        Assert.AreEqual(OpStatus.Success, chosen.Status, chosen.UserMessage);
        Assert.AreEqual(BlockController.DeviceChosenMessage, chosen.UserMessage);
        Assert.HasCount(2, h.Tasks.Runs, "The invalid address never reaches the task.");
        CollectionAssert.AreEqual(new[] { GateVerbs.SetDevice, h.Tasks.Runs[1][1], RecordedNodes.HeadphonesAddress }, h.Tasks.Runs[1]);
        Assert.AreEqual(RecordedNodes.HeadphonesContainer, h.Store.ReadDevice().Value!.ContainerId);
    }

    [TestMethod]
    public async Task SetDeviceReportsWhyTheGateRefusedADevice()
    {
        using var h = new Harness();

        ControllerResult phone = await h.Controller.SetDeviceAsync(RecordedNodes.IPhoneAddress);

        Assert.AreEqual(OpStatus.Failed, phone.Status);
        Assert.AreEqual(BlockController.NotAudioSinkMessage, phone.UserMessage);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
    }

    [TestMethod]
    public async Task SetDeviceReportsProtectionOnTheDevicePinnedNow()
    {
        using var h = new Harness(nodes: RecordedNodes.TableWithHeadphones());
        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord { DisabledServices = { new Guid("0000111E-0000-1000-8000-00805F9B34FB") } }).Ok);

        ControllerResult refused = await h.Controller.SetDeviceAsync(RecordedNodes.HeadphonesAddress);

        Assert.AreEqual(OpStatus.Failed, refused.Status);
        Assert.AreEqual(BlockController.OtherDeviceProtectedMessage, refused.UserMessage);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address);
    }

    [TestMethod]
    public async Task SetupRefusesWhenNothingIsPinned()
    {
        using var h = new Harness(setUp: false);
        h.Settings.Current = new EarshotSettings();

        ControllerResult result = await h.Controller.RunSetupAsync();

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.NothingPinnedMessage, result.UserMessage);
        Assert.IsEmpty(h.Launcher.Launches);
    }

    [TestMethod]
    public async Task SetupPassesThePinnedIdentityAndConfirmsTheTasks()
    {
        using var h = new Harness(setUp: false);
        h.Launcher.Result = _ =>
        {
            h.SetUp();
            return new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0));
        };

        ControllerResult result = await h.Controller.RunSetupAsync();

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(BlockController.SetupDoneMessage, result.UserMessage);
        Assert.AreEqual((Exe, "install " + TestUsers.Sid + " 5A6B7C8D9EAF 1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d"), h.Launcher.Launches.Single());
        Assert.IsTrue(h.Controller.IsSetUp);
        Assert.IsTrue(Program.TryParseInstallArgs(h.Launcher.Launches[0].Arguments.Split(' '), out _, out string? problem), "Install accepts what setup sends: " + problem);
    }

    [TestMethod]
    public async Task SetupMapsEachEnding()
    {
        using var h = new Harness(setUp: false);

        h.Launcher.Result = _ => new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223));
        Assert.AreEqual(BlockController.SetupCancelledMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.NotElevated, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.SetupNeedsAdminMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.FolderNotSecure, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.SetupUnsafeFolderMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun(77, StepOutcomes.FromWin32("runas", 0));
        ControllerResult refused = await h.Controller.RunSetupAsync();
        Assert.AreEqual(BlockController.SetupFailedMessage, refused.UserMessage);
        Assert.AreEqual("exit 77", refused.Steps.Single(s => s.Step == "install-exit").CodeName);

        h.Launcher.Result = _ => new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.NeedsRepairMessage, (await h.Controller.RunSetupAsync()).UserMessage, "Exit 0 without tasks is not success.");
    }

    [TestMethod]
    public async Task UninstallMapsEachEnding()
    {
        using var h = new Harness();
        await h.Controller.GetStatusAsync();

        h.Launcher.Result = _ => new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223));
        Assert.AreEqual(BlockController.RemoveCancelledMessage, (await h.Controller.UninstallAsync()).UserMessage);
        Assert.IsTrue(h.Controller.IsSetUp);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.Partial, StepOutcomes.FromWin32("runas", 0));
        ControllerResult partial = await h.Controller.UninstallAsync();
        Assert.AreEqual(OpStatus.Partial, partial.Status);
        Assert.IsFalse(h.Controller.IsSetUp);

        h.Launcher.Result = _ => new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.RemovedMessage, (await h.Controller.UninstallAsync()).UserMessage);
        Assert.IsTrue(h.Launcher.Launches.All(l => l == (Exe, "uninstall")));
    }

    private static BluetoothNode Node(bool present, NodeBlockStatus status, bool persist) =>
        new("id", "BTHENUM", null, present, status, 0, persist);

    [TestMethod]
    public void NodeResultsMapToHonestMessages()
    {
        var on = Node(true, NodeBlockStatus.Enabled, false);
        var off = Node(true, NodeBlockStatus.Disabled, true);
        var temporary = Node(true, NodeBlockStatus.Disabled, false);
        var gone = Node(false, NodeBlockStatus.Unknown, false);
        NodeReadResult Read(params BluetoothNode[] nodes) => new(true, nodes, []);

        Assert.AreEqual(BlockController.BlockedMessage, BlockController.MapNodeResult(true, BlockState.Blocked, Read(off, off), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.NotPersistentMessage, BlockController.MapNodeResult(true, BlockState.Blocked, Read(off, temporary), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.NotPresentBlockMessage, BlockController.MapNodeResult(true, BlockState.Blocked, Read(off, gone), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.NotPresentBlockMessage, BlockController.MapNodeResult(true, BlockState.Unknown, Read(gone), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.UnreadableMessage, BlockController.MapNodeResult(true, BlockState.Unknown, Read(Node(true, NodeBlockStatus.Unknown, false)), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.NotFoundMessage, BlockController.MapNodeResult(true, BlockState.NotFound, Read(), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.CancelledMessage, BlockController.MapNodeResult(true, BlockState.Allowed, Read(on), GateRunOutcome.Cancelled, []).UserMessage);
        Assert.AreEqual(BlockController.AllowedMessage, BlockController.MapNodeResult(false, BlockState.Allowed, Read(on), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(BlockController.AllowFailedMessage, BlockController.MapNodeResult(false, BlockState.Blocked, Read(off), GateRunOutcome.Completed, []).UserMessage);
        Assert.AreEqual(OpStatus.Partial, BlockController.MapNodeResult(false, BlockState.Mixed, Read(on, off), GateRunOutcome.Completed, []).Status);
    }

    [TestMethod]
    public void EveryMessageIsPlainBritishCopy()
    {
        string[] messages = typeof(BlockController)
            .GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Message", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.IsGreaterThan(20, messages.Length);
        foreach (string message in messages)
        {
            Assert.IsFalse(message.Contains('\u2014', StringComparison.Ordinal), message);
            Assert.IsFalse(message.Contains('%', StringComparison.Ordinal), message);
            Assert.IsLessThan(128, message.Length, message);
            Assert.IsFalse(message.Contains("color", StringComparison.OrdinalIgnoreCase) || message.Contains("canceled", StringComparison.OrdinalIgnoreCase), message);
        }
    }

    [TestMethod]
    public void TheCompositionWiresTheControllerBehindSafeMode()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();

        ServiceRegistry registry = CompositionRoot.Build(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: true);

        Assert.IsInstanceOfType<SafeBlockController>(registry.Block);
        Assert.IsInstanceOfType<BlockController>(((SafeBlockController)registry.Block).Inner);
        ((BlockController)((SafeBlockController)registry.Block).Inner).Dispose();
    }

    [TestMethod]
    public async Task SafeModeRefusesEveryChangeBeforeTheControllerRuns()
    {
        using var h = new Harness(setUp: false);
        var safe = new SafeBlockController(h.Controller, h.Log);

        ControllerResult[] results =
        [
            await safe.BlockAsync(), await safe.AllowAsync(), await safe.SetBlockAtBootAsync(false),
            await safe.SetDeviceAsync(RecordedNodes.AirPodsAddress), await safe.RunSetupAsync(), await safe.UninstallAsync(),
        ];

        Assert.IsTrue(results.All(r => r.Status == OpStatus.NotAttempted));
        Assert.IsEmpty(h.Tasks.Runs);
        Assert.IsEmpty(h.Launcher.Launches);
        Assert.IsEmpty(h.Nodes.Calls);
    }
}
