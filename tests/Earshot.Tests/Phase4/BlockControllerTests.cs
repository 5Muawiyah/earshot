using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Service;
using Earshot.Tests.Service;
using Earshot.Tray;
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

        public Harness(bool setUp = true, FakeNodeApi? nodes = null, IServiceControl? service = null, IInstalledFiles? files = null)
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
            Controller = new BlockController(Log, Settings, Store, Nodes, gate, Launcher, TestUsers.Sid, Exe, _worker, null, service, InstallFolder, files);
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
            Assert.IsTrue(Store.WriteConfig(new GateConfig { HandBackAtShutdown = true }).Ok);
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

    // The installed and the running Earshot.exe as the status read sees them: a file that is there with a version,
    // one that is missing, or one whose read failed.
    private sealed class FakeInstalledFiles : IInstalledFiles
    {
        private readonly Dictionary<string, InstalledFile> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Reads { get; } = [];

        public FakeInstalledFiles With(string path, string? version) =>
            Put(path, new InstalledFile(true, version is null ? null : new Version(version), StepOutcomes.FromHResult("read-file-version", 0, path)));

        public FakeInstalledFiles Missing(string path) =>
            Put(path, new InstalledFile(false, null, StepOutcomes.FromHResult("read-file-version", unchecked((int)0x80070002), "Missing: " + path, ok: false)));

        public FakeInstalledFiles Unreadable(string path) =>
            Put(path, new InstalledFile(true, null, StepOutcomes.FromHResult("read-file-version", unchecked((int)0x80070020), path + ": in use")));

        public InstalledFile Read(string path)
        {
            Reads.Add(path);
            return _files[path];
        }

        // The install folder is there whenever the test gave the installed program a record, present or not; a test that
        // gave none has no folder, as a machine before setup has none.
        public bool FolderExists(string path) => _files.Keys.Any(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase));

        private FakeInstalledFiles Put(string path, InstalledFile file)
        {
            _files[path] = file;
            return this;
        }
    }

    private static readonly string InstalledExe = Path.Combine(InstallFolder, "Earshot.exe");

    // Tasks that read fine, with the installed Earshot.exe gone: setup puts the file back, and nothing else offers to.
    [TestMethod]
    public async Task AnInstallWhoseEarshotExeIsMissingNeedsRepair()
    {
        FakeInstalledFiles files = new FakeInstalledFiles().Missing(InstalledExe);
        using var h = new Harness(files: files);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.Allowed, status.State, "The tasks read fine, so this is not NotSetUp.");
        Assert.IsTrue(status.TasksInstalled);
        Assert.IsTrue(status.NeedsRepair);
        Assert.IsFalse(status.RunningCopyIsNewer);
        Assert.IsTrue(status.InstallExists);
        Assert.IsTrue(TrayStatus.OffersRepair(status), "An install exists, so the menu offers Repair.");
        Assert.IsFalse(TrayStatus.OffersSetUp(status), "Set up is only for when nothing is installed.");
        Assert.IsFalse(TrayStatus.NeedsSetUp(status));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "read-file-version"), "The failed read is on record with its code.");
    }

    // The same message a block gives when device.json is gone ("Boot block needs repair. Choose Repair Earshot.") must
    // have the item it names on the menu.
    [TestMethod]
    public async Task AnInstallWithNoDeviceFileNeedsRepair()
    {
        FakeInstalledFiles files = new FakeInstalledFiles().With(InstalledExe, "1.2.0.0").With(Exe, "1.2.0.0");
        using var h = new Harness(files: files);
        File.Delete(h.Store.DeviceFile);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.IsTrue(status.TasksInstalled);
        Assert.IsTrue(status.NeedsRepair);
        Assert.IsTrue(status.InstallExists);
        Assert.IsTrue(TrayStatus.OffersRepair(status), "An install exists, so the menu offers Repair.");
        Assert.IsFalse(TrayStatus.OffersSetUp(status), "Set up is only for when nothing is installed.");
        Assert.AreEqual(BlockController.NeedsRepairMessage, (await h.Controller.BlockAsync()).UserMessage);
    }

    // Tasks that run some other file than the installed Earshot.exe are not verified, so the state is NotSetUp, and
    // that already offers setup. Recorded here because the damaged case was reported as unhandled.
    [TestMethod]
    public async Task TasksThatRunAnotherPathThanTheInstalledExeReadAsNotSetUp()
    {
        FakeInstalledFiles files = new FakeInstalledFiles().With(InstalledExe, "1.2.0.0").With(Exe, "1.2.0.0");
        using var h = new Harness(setUp: false, files: files);
        h.Tasks.InstallAll(@"D:\Somewhere\Else");
        Assert.IsTrue(h.Store.WriteDevice(RecordedNodes.AirPods()).Ok);
        Assert.IsTrue(h.Store.WriteConfig(new GateConfig()).Ok);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.NotSetUp, status.State);
        Assert.IsFalse(status.TasksInstalled);
        Assert.IsTrue(status.InstallExists);
        Assert.IsTrue(TrayStatus.OffersRepair(status), "An install exists, so the menu offers Repair.");
        Assert.IsFalse(TrayStatus.OffersSetUp(status), "Set up is only for when nothing is installed.");
        Assert.IsEmpty(files.Reads, "Before the tasks verify there is no installed file to ask about.");
    }

    [TestMethod]
    public async Task ARunningCopyWithANewerFileVersionThanTheInstalledOneIsFlagged()
    {
        FakeInstalledFiles files = new FakeInstalledFiles().With(InstalledExe, "1.1.0.0").With(Exe, "1.2.0.0");
        using var h = new Harness(files: files);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.IsTrue(status.RunningCopyIsNewer);
        Assert.IsFalse(status.NeedsRepair);
        Assert.AreEqual(BlockState.Allowed, status.State);
        Assert.IsTrue(status.InstallExists);
        Assert.IsTrue(TrayStatus.OffersRepair(status), "An install exists, so the menu offers Repair.");
        Assert.IsFalse(TrayStatus.OffersSetUp(status), "Set up is only for when nothing is installed.");
        Assert.IsFalse(TrayStatus.NeedsSetUp(status));
    }

    [TestMethod]
    public async Task ACopyThatIsTheSameAsOrOlderThanTheInstalledOneIsNotFlagged()
    {
        foreach ((string installed, string running) in new[] { ("1.2.0.0", "1.2.0.0"), ("1.2.0.0", "1.1.9.0"), ("2.0.0.0", "1.9.9.9") })
        {
            FakeInstalledFiles files = new FakeInstalledFiles().With(InstalledExe, installed).With(Exe, running);
            using var h = new Harness(files: files);

            BootBlockStatus status = await h.Controller.GetStatusAsync();

            Assert.IsFalse(status.RunningCopyIsNewer, installed + " installed, " + running + " running");
            Assert.IsFalse(status.NeedsRepair, installed + " installed, " + running + " running");
            Assert.IsFalse(TrayStatus.OffersSetUp(status), installed + " installed, " + running + " running");
            Assert.IsTrue(TrayStatus.OffersRepair(status), installed + " installed, " + running + " running");
        }
    }

    // A version that cannot be read is not a newer copy and not a damaged install: nothing is offered on a guess, and
    // the failed step is kept.
    [TestMethod]
    public async Task AVersionThatCouldNotBeReadIsNeitherNewerNorDamaged()
    {
        FakeInstalledFiles files = new FakeInstalledFiles().Unreadable(InstalledExe);
        using var h = new Harness(files: files);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.IsFalse(status.NeedsRepair);
        Assert.IsFalse(status.RunningCopyIsNewer);
        Assert.IsFalse(TrayStatus.OffersSetUp(status));
        Assert.IsTrue(TrayStatus.OffersRepair(status), "Repair is offered for an install in any state.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "read-file-version"));
    }

    [TestMethod]
    public async Task AHealthyInstallOffersNothing()
    {
        FakeInstalledFiles files = new FakeInstalledFiles().With(InstalledExe, "1.2.0.0").With(Exe, "1.2.0.0");
        using var h = new Harness(files: files);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.Allowed, status.State);
        Assert.IsFalse(status.NeedsRepair);
        Assert.IsFalse(status.RunningCopyIsNewer);
        Assert.IsFalse(TrayStatus.OffersSetUp(status));
        Assert.IsTrue(TrayStatus.OffersRepair(status), "Repair is offered for an install in any state.");
    }

    [TestMethod]
    public async Task StatusBeforeSetupIsNotSetUpAndShowsThePinnedNodes()
    {
        using var h = new Harness(setUp: false);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.NotSetUp, status.State);
        Assert.IsFalse(status.TasksInstalled);
        Assert.IsFalse(status.InstallExists);
        Assert.IsTrue(TrayStatus.OffersSetUp(status));
        Assert.IsFalse(TrayStatus.OffersRepair(status));
        Assert.IsTrue(status.BlockAtBoot, "The shipped default before setup.");
        Assert.IsTrue(status.BlockAtBootKnown);
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

    // A Task Scheduler read that fails says nothing about setup. The state is Unknown rather than NotSetUp, so setup
    // (a UAC prompt) is not offered, nothing that blocks is turned off, and the state is read again.
    [TestMethod]
    public async Task ATaskThatCouldNotBeReadIsNotTakenAsNotSetUp()
    {
        using var h = new Harness();
        h.Tasks.ReadResult = unchecked((int)0x800706BA); // RPC_S_SERVER_UNAVAILABLE

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.Unknown, status.State);
        Assert.IsFalse(status.TasksKnown);
        Assert.IsFalse(status.TasksInstalled);
        Assert.IsTrue(status.BlockAtBootKnown, "config.json was read.");
        Assert.IsTrue(status.BlockAtBoot);
        Assert.IsFalse(TrayStatus.NeedsSetUp(status));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, @"task-open:\Earshot\Gate"));

        ControllerResult block = await h.Controller.BlockAsync();
        Assert.AreEqual(BlockController.TaskUnreadableMessage, block.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);

        h.Tasks.ReadResult = 0;
        BootBlockStatus again = await h.Controller.GetStatusAsync();
        Assert.AreEqual(BlockState.Allowed, again.State);
        Assert.IsTrue(again.TasksKnown);
    }

    // The same for a task that opened but whose security descriptor or XML could not be read: that is not a task
    // that needs repair. Without config.json the setting is not known either.
    [TestMethod]
    public async Task ATaskWhoseDefinitionCouldNotBeReadIsNotTakenAsNotSetUp()
    {
        using var h = new Harness();
        TaskReadback good = h.Tasks.Tasks[@"\Earshot\Protect"];
        h.Tasks.Tasks[@"\Earshot\Protect"] = good with { Xml = null, Steps = [StepOutcomes.FromHResult(@"task-xml:\Earshot\Protect", unchecked((int)0x8007000E))] };
        File.Delete(h.Store.ConfigFile);

        BootBlockStatus status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(BlockState.Unknown, status.State);
        Assert.IsFalse(status.TasksKnown);
        Assert.IsFalse(status.BlockAtBootKnown, "A missing config.json does not mean before setup while the tasks are not known.");
        Assert.IsFalse(status.BlockAtBoot);
        Assert.IsFalse(h.Controller.IsSetUp);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, @"task-xml:\Earshot\Protect"));
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
        Assert.AreEqual("Connect the AirPods to this PC once from Windows Bluetooth settings, so Earshot can block them.", result.UserMessage);
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

    // A gate run that is not SYSTEM refuses before it does anything while its environment names code for the .NET
    // runtime to load, so it writes no status file and only LastTaskResult carries the reason. Trying again cannot
    // work, so the card names the reason instead of saying to try again.
    [TestMethod]
    public async Task AGateRunRefusedForItsEnvironmentSaysWhyRatherThanTryAgain()
    {
        using var h = new Harness();
        h.GateActs = false;
        h.Tasks.OnRun = _ => h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, (int)GateExitCode.UnsafeEnvironment, h.Tasks.Current.LastRunTime + 1);

        ControllerResult block = await h.Controller.BlockAsync();
        ControllerResult setting = await h.Controller.SetBlockAtBootAsync(false);

        Assert.AreEqual(OpStatus.Failed, block.Status);
        Assert.AreEqual(BlockController.UnsafeEnvironmentMessage, block.UserMessage);
        Assert.AreEqual(BlockController.UnsafeEnvironmentMessage, setting.UserMessage);
        Assert.IsTrue(block.Steps.Any(s => s.CodeName == "unsafe-environment"));
        Assert.DoesNotContain("Try again", block.UserMessage);
    }

    // The gate refuses a node change without config.json; the card says how to write it again.
    [TestMethod]
    public async Task AGateRunRefusedForAMissingConfigSaysHowToWriteItAgain()
    {
        using var h = new Harness();
        File.Delete(h.Store.ConfigFile);

        ControllerResult block = await h.Controller.BlockAsync();

        Assert.AreEqual(OpStatus.Failed, block.Status);
        Assert.AreEqual(BlockController.NoConfigMessage, block.UserMessage);
        Assert.IsEmpty(h.Nodes.Calls);

        ControllerResult setting = await h.Controller.SetBlockAtBootAsync(true);
        Assert.AreEqual(OpStatus.Success, setting.Status);
        Assert.AreEqual(OpStatus.Success, (await h.Controller.BlockAsync()).Status);
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

    // A flag that cannot be read is not "already allowed": the gate runs, changes nothing it cannot read, and the
    // failed read is in the result rather than dropped.
    [TestMethod]
    public async Task AllowWithAnUnreadableFlagRunsTheGateAndKeepsTheFailedRead()
    {
        using var h = new Harness();
        string unreadable = RecordedNodes.AirPodsTargets[3];
        h.Nodes[unreadable].ConfigFlagsReadResult = CfgMgr32.CR_FAILURE;

        ControllerResult result = await h.Controller.AllowAsync();

        Assert.HasCount(1, h.Tasks.Runs, "Not answered as already allowed.");
        Assert.AreNotEqual(OpStatus.AlreadyInState, result.Status);
        Assert.AreEqual(OpStatus.Partial, result.Status, result.UserMessage);
        Assert.AreEqual(BlockController.UnreadableMessage, result.UserMessage);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "cm-configflags:" + unreadable && s.CodeName == "CR_FAILURE"));
        Assert.IsEmpty(h.Nodes.Calls.Where(c => c.InstanceId == unreadable), "A node whose flag cannot be read is not changed.");
    }

    [TestMethod]
    public async Task BlockWithAnUnreadablePresenceIsNotAlreadyBlocked()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        h.Nodes[RecordedNodes.AirPodsTargets[5]].PresentLocateResult = CfgMgr32.CR_FAILURE;

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.HasCount(1, h.Tasks.Runs);
        Assert.AreNotEqual(OpStatus.Success, result.Status);
        Assert.AreNotEqual(OpStatus.AlreadyInState, result.Status);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "cm-locate:" + RecordedNodes.AirPodsTargets[5] && s.CodeName == "CR_FAILURE"));
    }

    // Every other target is blocked, but one still enabled has a container that cannot be read: the gate runs,
    // changes only the nodes it can verify, and nothing is reported as blocked.
    [TestMethod]
    public async Task BlockWithANodeWhoseContainerCannotBeReadIsNotAlreadyBlocked()
    {
        using var h = new Harness();
        string handsFree = RecordedNodes.AirPodsTargets.Single(id => id.Contains("{0000111E", StringComparison.Ordinal));
        foreach (string id in RecordedNodes.AirPodsTargets.Where(id => id != handsFree))
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        h.Nodes[handsFree].ContainerReadResult = 0x1Du; // CR_REGISTRY_ERROR

        ControllerResult result = await h.Controller.BlockAsync();

        Assert.HasCount(1, h.Tasks.Runs, "The gate must run.");
        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.UnreadableMessage, result.UserMessage);
        Assert.IsFalse(h.Nodes[handsFree].IsDisabled, "A node whose device could not be verified is never changed.");
        Assert.IsFalse(h.Nodes.Calls.Any(c => c.InstanceId == handsFree));
        Assert.AreEqual(BlockState.Unknown, (await h.Controller.GetStatusAsync()).State);
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
    public async Task AfterSetupASettingThatDoesNotReadIsNotKnownAndNeverOn()
    {
        using var h = new Harness();

        File.WriteAllText(h.Store.ConfigFile, "{ \"SchemaVersion\": 1, \"BlockAtBoot\": \"yes\" }");
        BootBlockStatus invalid = await h.Controller.GetStatusAsync();

        BootBlockStatus unreadable;
        using (new FileStream(h.Store.ConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            unreadable = await h.Controller.GetStatusAsync();
        }

        File.Delete(h.Store.ConfigFile);
        BootBlockStatus missing = await h.Controller.GetStatusAsync();

        foreach (BootBlockStatus status in new[] { invalid, unreadable, missing })
        {
            Assert.IsFalse(status.BlockAtBootKnown);
            Assert.IsFalse(status.BlockAtBoot, "A setting that was not read is never taken as on.");
            Assert.AreEqual(BlockState.Allowed, status.State, "The nodes are still read.");
        }

        Assert.IsTrue(h.Store.WriteConfig(new GateConfig()).Ok);
        BootBlockStatus read = await h.Controller.GetStatusAsync();
        Assert.IsTrue(read.BlockAtBootKnown);
        Assert.IsTrue(read.BlockAtBoot);
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

        ControllerResult invalid = await h.Controller.SetDeviceAsync("0a1b2c3d4e8c");
        ControllerResult blockedFirst;
        h.Nodes[RecordedNodes.AirPodsTargets[0]].MarkDisabled(persistent: true);
        blockedFirst = await h.Controller.SetDeviceAsync(RecordedNodes.HeadphonesAddress);
        h.Nodes[RecordedNodes.AirPodsTargets[0]].Status = CfgMgr32.DN_STARTED;
        h.Nodes[RecordedNodes.AirPodsTargets[0]].ConfigFlags = 0;
        ControllerResult chosen = await h.Controller.SetDeviceAsync(RecordedNodes.HeadphonesAddress);

        Assert.AreEqual(BlockController.InvalidAddressMessage, invalid.UserMessage);
        Assert.AreEqual(BlockController.OtherDeviceBlockedMessage, blockedFirst.UserMessage);
        Assert.AreEqual((int)GateExitCode.OtherDeviceBlocked, blockedFirst.Steps.Single(s => s.Step == BlockController.SetDeviceExitStep).Code);
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
        Assert.AreEqual((Exe, "install " + TestUsers.Sid + " 0A1B2C3D4E8C 5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13"), h.Launcher.Launches.Single());
        Assert.IsTrue(h.Controller.IsSetUp);
        Assert.IsTrue(Program.TryParseInstallArgs(h.Launcher.Launches[0].Arguments.Split(' '), out _, out string? problem), "Install accepts what setup sends: " + problem);
    }

    // ----- repair -----

    private static readonly string InstalledProgram = Path.Combine(InstallFolder, "Earshot.exe");

    private const string Identity = " " + TestUsers.Sid + " 0A1B2C3D4E8C 5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13";

    // The elevated program is the installed one, never the copy that happens to be running: the running copy may be in a
    // folder the signed-in user can write.
    [TestMethod]
    public async Task RepairRunsTheInstalledProgramNotTheRunningCopyWithTheRepairVerb()
    {
        using var h = new Harness();

        ControllerResult result = await h.Controller.RunRepairAsync(RepairVerb.Repair);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(BlockController.RepairDoneMessage, result.UserMessage);
        Assert.AreEqual("Earshot was repaired", result.UserMessage);
        (string exe, string arguments) = h.Launcher.Launches.Single();
        Assert.AreEqual(InstalledProgram, exe);
        Assert.AreNotEqual(Exe, exe);
        Assert.AreEqual("repair" + Identity, arguments);
        Assert.IsTrue(Program.TryParseRepairArgs(arguments.Split(' '), out _, out string? problem), "Repair accepts what the tray sends: " + problem);
    }

    // A program from before the repair verb already runs install from its own folder as a repair, and would answer an
    // unknown verb with a usage error after the prompt.
    [TestMethod]
    public async Task AnInstalledProgramFromBeforeTheRepairVerbIsAskedThroughTheInstallVerb()
    {
        using var h = new Harness();

        ControllerResult result = await h.Controller.RunRepairAsync(RepairVerb.Install);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual((InstalledProgram, "install" + Identity), h.Launcher.Launches.Single());
        Assert.IsTrue(Program.TryParseInstallArgs(h.Launcher.Launches[0].Arguments.Split(' '), out _, out string? problem), problem);
    }

    [TestMethod]
    public async Task WithNoInstalledProgramToRunTheRunningCopysOwnSetupDoesTheRepair()
    {
        using var h = new Harness();

        ControllerResult result = await h.Controller.RunRepairAsync(RepairVerb.FromThisCopy);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual("Earshot was repaired", result.UserMessage, "It is a repair in the person's words, whichever program runs.");
        Assert.AreEqual((Exe, "install" + Identity), h.Launcher.Launches.Single());
    }

    [TestMethod]
    public async Task RepairRefusesWhenNothingIsPinnedAndRunsNothing()
    {
        using var h = new Harness();
        h.Settings.Current = new EarshotSettings();

        ControllerResult result = await h.Controller.RunRepairAsync(RepairVerb.Repair);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual("Choose your AirPods first, then repair Earshot.", result.UserMessage);
        Assert.IsEmpty(h.Launcher.Launches);
    }

    [TestMethod]
    public async Task RepairMapsEachEndingInItsOwnWords()
    {
        using var h = new Harness();
        (int? Exit, long RunasCode, string Message)[] endings =
        [
            (null, 1223, BlockController.RepairCancelledMessage),
            ((int)GateExitCode.NotElevated, 0, BlockController.RepairNeedsAdminMessage),
            ((int)GateExitCode.FolderNotSecure, 0, BlockController.RepairUnsafeFolderMessage),
            ((int)GateExitCode.NoManifest, 0, BlockController.RepairNeedsFilesMessage),
            ((int)GateExitCode.NotFromInstallFolder, 0, BlockController.RepairNotFromInstallMessage),
            ((int)GateExitCode.DeviceMismatch, 0, BlockController.RepairDeviceChangedMessage),
            ((int)GateExitCode.UnsafeEnvironment, 0, BlockController.RepairUnsafeEnvironmentMessage),
            ((int)GateExitCode.Failed, 0, BlockController.RepairFailedMessage),
        ];

        foreach ((int? exit, long runas, string message) in endings)
        {
            h.Launcher.Result = _ => new ElevatedRun(exit, StepOutcomes.FromWin32("runas", (uint)runas));
            Assert.AreEqual(message, (await h.Controller.RunRepairAsync(RepairVerb.Repair)).UserMessage);
        }

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.Partial, StepOutcomes.FromWin32("runas", 0));
        ControllerResult partial = await h.Controller.RunRepairAsync(RepairVerb.Repair);
        Assert.AreEqual(OpStatus.Partial, partial.Status);
        Assert.AreEqual("Earshot was repaired, but the shut-down hand-back could not be set up. Try again.", partial.UserMessage);
    }

    [TestMethod]
    public void RepairWordsAreAllPlainAndHaveNoDash()
    {
        foreach (BlockController.ElevatedWords words in new[] { BlockController.RepairWords })
        {
            foreach (string text in new[]
            {
                words.NothingPinned, words.Done, words.Partial, words.Cancelled, words.Failed, words.NeedsAdmin, words.UnsafeFolder, words.NeedsFiles,
                words.DeviceChanged, words.OtherBlocked, words.OtherProtected, words.UnsafeEnvironment, words.NotReadBack, BlockController.RepairNotFromInstallMessage,
            })
            {
                Assert.DoesNotContain("\u2014", text);
                Assert.IsLessThanOrEqualTo(120, text.Length, text);
            }
        }

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

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.Partial, StepOutcomes.FromWin32("runas", 0));
        ControllerResult partial = await h.Controller.RunSetupAsync();
        Assert.AreEqual(BlockController.SetupHandBackNotSetUpMessage, partial.UserMessage, "A Partial exit says what is missing, not that setup failed.");
        Assert.AreEqual("Earshot is set up, but the shut-down hand-back could not be set up. Try again.", partial.UserMessage);
        Assert.AreEqual(OpStatus.Partial, partial.Status);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.NotAudioSink, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.NotAudioSinkMessage, (await h.Controller.RunSetupAsync()).UserMessage, "A phone is never set up.");

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.NotFound, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.NotFoundMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.DeviceMismatch, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.SetupDeviceChangedMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.OtherDeviceBlocked, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.SetupOtherDeviceBlockedMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.OtherDeviceProtected, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.SetupOtherDeviceProtectedMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.UnsafeEnvironment, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.SetupUnsafeEnvironmentMessage, (await h.Controller.RunSetupAsync()).UserMessage);

        h.Launcher.Result = _ => new ElevatedRun(77, StepOutcomes.FromWin32("runas", 0));
        ControllerResult refused = await h.Controller.RunSetupAsync();
        Assert.AreEqual(BlockController.SetupFailedMessage, refused.UserMessage);
        Assert.AreEqual("exit 77", refused.Steps.Single(s => s.Step == "install-exit").CodeName);

        h.Launcher.Result = _ => new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.NeedsRepairMessage, (await h.Controller.RunSetupAsync()).UserMessage, "Exit 0 without tasks is not success.");

        h.Tasks.InstallAll(InstallFolder);
        h.Tasks.ReadResult = unchecked((int)0x800706BA);
        ControllerResult unread = await h.Controller.RunSetupAsync();
        Assert.AreEqual(OpStatus.Partial, unread.Status, "Exit 0 with tasks that could not be read back is neither success nor a repair.");
        Assert.AreEqual(BlockController.SetupNotReadBackMessage, unread.UserMessage);
    }

    [TestMethod]
    public async Task UninstallMapsEachEnding()
    {
        using var h = new Harness();
        await h.Controller.GetStatusAsync();

        h.Launcher.Result = _ => new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223));
        Assert.AreEqual(BlockController.RemoveCancelledMessage, (await h.Controller.UninstallAsync()).UserMessage);
        Assert.IsTrue(h.Controller.IsSetUp);

        h.Launcher.Result = _ => new ElevatedRun((int)GateExitCode.UnsafeEnvironment, StepOutcomes.FromWin32("runas", 0));
        Assert.AreEqual(BlockController.RemoveUnsafeEnvironmentMessage, (await h.Controller.UninstallAsync()).UserMessage);
        Assert.IsTrue(h.Controller.IsSetUp, "Nothing was removed.");

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

    // ---- the hand-back setting and the hand-back service ----

    [TestMethod]
    public async Task TheHandBackSettingIsSentThroughTheGateAndReadBack()
    {
        using var h = new Harness();

        ControllerResult off = await h.Controller.SetHandBackAtShutdownAsync(false);
        ControllerResult offAgain = await h.Controller.SetHandBackAtShutdownAsync(false);
        ControllerResult on = await h.Controller.SetHandBackAtShutdownAsync(true);

        Assert.AreEqual(OpStatus.Success, off.Status, off.UserMessage);
        Assert.AreEqual(BlockController.HandBackMirrorOffMessage, off.UserMessage);
        Assert.AreEqual(OpStatus.AlreadyInState, offAgain.Status, "A file that already says so needs no run.");
        Assert.AreEqual(BlockController.HandBackMirrorOnMessage, on.UserMessage);
        CollectionAssert.AreEqual(new[] { GateVerbs.SetHandBackOff, GateVerbs.SetHandBackOn }, h.Tasks.Runs.Select(r => r[0]).ToArray());
        Assert.IsTrue(h.Tasks.Runs.All(r => BoundaryValidation.IsNonce(r[1]) && r.Length == 2), "A verb and a nonce, no address.");
        GateConfig config = h.Store.ReadConfig().Value!;
        Assert.IsTrue(config.HandBackAtShutdown);
        Assert.IsTrue(config.BlockAtBoot, "The other setting is kept.");
    }

    [TestMethod]
    public async Task TheHandBackSettingIsOkOnlyWhenTheFileNowSaysSo()
    {
        using var h = new Harness();
        h.GateActs = false;
        h.Tasks.OnRun = _ => h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, h.Tasks.Current.LastRunTime + 1);

        ControllerResult result = await h.Controller.SetHandBackAtShutdownAsync(false);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.HandBackMirrorFailedMessage, result.UserMessage);
        Assert.IsTrue(h.Store.ReadConfig().Value!.HandBackAtShutdown, "Nothing changed.");
    }

    [TestMethod]
    public async Task TheHandBackSettingBeforeSetupSaysSoAndSendsNothing()
    {
        using var h = new Harness(setUp: false);

        ControllerResult result = await h.Controller.SetHandBackAtShutdownAsync(false);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.NotSetUpMessage, result.UserMessage);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public async Task TheStatusReadFillsTheHandBackMirrorFromConfigJson()
    {
        using var h = new Harness();

        BootBlockStatus on = await h.Controller.GetStatusAsync();
        Assert.IsTrue(h.Store.WriteConfig(new GateConfig { BlockAtBoot = true, HandBackAtShutdown = false }).Ok);
        BootBlockStatus off = await h.Controller.GetStatusAsync();
        File.Delete(h.Store.ConfigFile);
        BootBlockStatus missing = await h.Controller.GetStatusAsync();
        File.WriteAllText(h.Store.ConfigFile, "not json");
        BootBlockStatus invalid = await h.Controller.GetStatusAsync();

        Assert.AreEqual(true, on.HandBackAtShutdownMirror);
        Assert.AreEqual(false, off.HandBackAtShutdownMirror);
        Assert.IsNull(missing.HandBackAtShutdownMirror, "A file that was not read is no answer.");
        Assert.IsNull(invalid.HandBackAtShutdownMirror);
    }

    [TestMethod]
    public async Task TheHandBackServiceIsLoggedOncePerRunWithHowItReads()
    {
        var service = new FakeServiceControl();
        using var h = new Harness(service: service);
        service.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(InstallFolder));

        await h.Controller.GetStatusAsync();
        await h.Controller.GetStatusAsync();

        Assert.AreEqual(1, h.Log.Entries.Count(e => e.Message.StartsWith("Hand-back service: ", StringComparison.Ordinal)));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: running."));
        Assert.AreEqual(1, service.Calls.Count(c => c == "query"), "The control manager is asked once.");
    }

    [TestMethod]
    [DataRow("stopped", "Hand-back service: stopped.")]
    [DataRow("missing", "Hand-back service: missing.")]
    [DataRow("differs", "Hand-back service: differs: The start type is 3, not 2.")]
    public async Task EachWayTheServiceCanReadHasItsOwnLogLine(string kind, string expected)
    {
        var service = new FakeServiceControl();
        using var h = new Harness(service: service);
        switch (kind)
        {
            case "stopped": service.Install(AdvApi32.SERVICE_STOPPED, ServicePlan.Spec(InstallFolder)); break;
            case "differs":
                service.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(InstallFolder));
                service.ReadStartType = AdvApi32.SERVICE_DEMAND_START;
                break;
        }

        await h.Controller.GetStatusAsync();

        Assert.IsTrue(h.Log.Has(LogLevel.Info, expected), string.Join(" | ", h.Log.Entries.Select(e => e.Message)));
    }

    [TestMethod]
    public void AServiceThatCouldNotBeReadIsLoggedAsUnreadableWithItsCode()
    {
        var read = new ServiceQuery(
            ServicePresence.Unknown, [ServiceSteps.FromWin32(ServiceSteps.Query, 5, "The service control manager could not be opened.")]);

        Assert.AreEqual("Hand-back service: unreadable: ERROR_ACCESS_DENIED.", HandBackServiceText.Registration(read, ServicePlan.Spec(InstallFolder)));
    }

    [TestMethod]
    public async Task SafeModeRefusesTheHandBackSettingToo()
    {
        using var h = new Harness();
        var safe = new SafeBlockController(h.Controller, h.Log);

        ControllerResult result = await safe.SetHandBackAtShutdownAsync(false);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.IsEmpty(h.Tasks.Runs);
    }
}
