using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// The tray-side controller end to end against fakes: the fake scheduler plays \Earshot\Protect by running the
// real GateActions over the recorded node table, paired with the in-memory Bluetooth stack. No task, node,
// Bluetooth service or machine folder is touched.
[TestClass]
public sealed class AudioProtectionControllerTests
{
    private const string InstallFolder = @"C:\Program Files\Earshot";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();
        private readonly SystemWorker _worker;

        public Harness(bool setUp = true)
        {
            string machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(machine);
            Store = new GateStore(machine);
            Settings.Current.PinnedAddress = RecordedNodes.AirPodsAddress;
            Settings.Current.PinnedContainerId = RecordedNodes.AirPodsContainer;
            GateBluetooth.Pair(Nodes, Bluetooth);
            if (setUp)
            {
                Tasks.InstallAll(InstallFolder);
                Assert.IsTrue(Store.WriteDevice(RecordedNodes.AirPods()).Ok);
            }

            Tasks.OnRun = PlayTheGate;
            var gate = new TaskSchedulerGate(Tasks, Store, InstallFolder, TestUsers.Sid, Lookups.None, Time, (delay, ct) =>
            {
                Time.Advance(delay);
                return !ct.IsCancellationRequested;
            });
            _worker = new SystemWorker(Log);
            Controller = new AudioProtectionController(Log, Settings, Store, new ServiceStateReader(Bluetooth), gate, _worker);
        }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public FakeBluetoothServices Bluetooth { get; } = FakeBluetoothServices.AirPods();

        public FakeDevice AirPods => Bluetooth[RecordedNodes.AirPodsAddress];

        public FakeScheduledTasks Tasks { get; } = new();

        public FakeSettings Settings { get; } = new();

        public CapturingLog Log { get; } = new();

        public ManualTime Time { get; } = new();

        public AudioProtectionController Controller { get; }

        public bool GateActs { get; set; } = true;

        public List<int> GateThreads { get; } = new();

        public void BlockAll()
        {
            foreach (string id in RecordedNodes.AirPodsTargets)
            {
                Nodes[id].MarkDisabled(persistent: true);
            }
        }

        // A gate run that writes the given status instead of running the gate actions.
        public void PlayAGateThatWrites(GateExitCode exit, params StepOutcome[] steps)
        {
            Tasks.OnRun = parameters =>
            {
                DateTimeOffset now = Time.GetUtcNow();
                var status = new GateStatusFile(GateStore.SchemaVersion, parameters[1], parameters[0], now, now,
                    GateExitCodes.ResultName(exit), (int)exit, nameof(BlockState.Allowed), steps, StepsTruncated: false);
                Assert.IsTrue(Store.WriteStatus(status).Ok);
                Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, (int)exit, Tasks.Current.LastRunTime + 1);
            };
        }

        public ProtectionIntentFile Intent => new(Store.Folder);

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

        // What \Earshot\Protect does when started: the real gate actions, then the scheduler state moves on.
        private void PlayTheGate(string[] parameters)
        {
            GateThreads.Add(Environment.CurrentManagedThreadId);
            int exit = 0;
            if (GateActs)
            {
                exit = (int)new GateActions(Nodes, Store, new FakeFolderSecurity(), Log, Time)
                    .Run(new GateRequest(parameters[0], parameters[1], null));
            }

            Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, exit, Tasks.Current.LastRunTime + 1);
        }

        public void Dispose()
        {
            Controller.Dispose();
            _worker.Dispose();
            _temp.Dispose();
        }
    }

    [TestMethod]
    public async Task StatusIsARead()
    {
        using var h = new Harness();

        AudioProtectionSnapshot before = await h.Controller.GetStatusAsync();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);
        AudioProtectionSnapshot after = await h.Controller.GetStatusAsync();

        Assert.AreEqual(AudioProtectionState.NotProtected, before.State);
        Assert.IsTrue(before.HandsfreeInstalled);
        Assert.IsFalse(before.HeadsetInstalled);
        Assert.AreEqual(AudioProtectionState.Protected, after.State);
        Assert.IsEmpty(h.Tasks.Runs, "Status never starts the task.");
        Assert.IsEmpty(h.Bluetooth.SetCalls);
    }

    [TestMethod]
    public async Task StatusBeforeSetupReadsThePinnedDevice()
    {
        using var h = new Harness(setUp: false);

        AudioProtectionSnapshot status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(AudioProtectionState.NotProtected, status.State);
    }

    [TestMethod]
    public async Task StatusWithNothingPinnedIsUnknown()
    {
        using var h = new Harness(setUp: false);
        h.Settings.Current.PinnedAddress = "";

        AudioProtectionSnapshot status = await h.Controller.GetStatusAsync();

        Assert.AreEqual(AudioProtectionState.Unknown, status.State);
        Assert.IsEmpty(h.Bluetooth.Calls);
    }

    [TestMethod]
    public async Task ProtectRunsTheProtectTaskOffTheCallingThreadAndConfirmsByReadBack()
    {
        using var h = new Harness();
        h.Tasks.Tasks.Remove(TaskPlan.TaskPath(TaskPlan.GateTaskName));

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(AudioProtectionController.ProtectedMessage, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
        Assert.AreEqual(GateVerbs.ProtectOn, h.Tasks.Runs[0][0]);
        Assert.HasCount(2, h.Tasks.Runs[0], "Verb and nonce only: no address crosses the boundary.");
        Assert.IsTrue(BoundaryValidation.IsNonce(h.Tasks.Runs[0][1]));
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, h.GateThreads.Single());
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceStateReader.ServicesStep));
    }

    [TestMethod]
    public async Task OffAfterOnTurnsHandsfreeBackOn()
    {
        using var h = new Harness();
        Assert.IsTrue((await h.Controller.ApplyAsync(protect: true)).IsSuccess);

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(AudioProtectionController.OffMessage, result.UserMessage);
        Assert.Contains(ProtectedServices.Handsfree, h.AirPods.Enabled);
        Assert.AreEqual(GateVerbs.ProtectOff, h.Tasks.Runs[^1][0]);
    }

    [TestMethod]
    public async Task AMissingTaskIsNotSetUp()
    {
        using var h = new Harness();
        h.Tasks.Tasks.Clear();

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(AudioProtectionController.NotSetUpMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
        Assert.IsEmpty(h.Bluetooth.SetCalls);
    }

    [TestMethod]
    public async Task NoDeviceFileAndNoTaskIsNotSetUp()
    {
        using var h = new Harness(setUp: false);

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(AudioProtectionController.NotSetUpMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task ATaskWithoutADeviceFileNeedsRepair()
    {
        using var h = new Harness(setUp: false);
        h.Tasks.InstallAll(InstallFolder);

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(AudioProtectionController.NeedsRepairMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task ATaskThatFailsItsCheckIsNeverStarted()
    {
        using var h = new Harness();
        string path = TaskPlan.TaskPath(TaskPlan.ProtectTaskName);
        TaskReadback good = h.Tasks.Tasks[path];
        h.Tasks.Tasks[path] = good with { Sddl = good.Sddl + "(A;;FW;;;BU)" };

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(AudioProtectionController.NeedsRepairMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task AlreadyProtectedStartsNothing()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.AlreadyInState, result.Status);
        Assert.AreEqual(AudioProtectionController.AlreadyProtectedMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task HandsfreeTurnedOffOutsideEarshotIsLeftOff()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(AudioProtectionController.OffOutsideEarshotMessage, result.UserMessage);
        Assert.IsEmpty(h.Tasks.Runs);
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
    }

    [TestMethod]
    public async Task TheTaskResultAloneNeverCountsAsProtected()
    {
        using var h = new Harness { GateActs = false };

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(AudioProtectionController.ProtectFailedMessage, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
    }

    [TestMethod]
    public async Task WhileBlockedTheRequestIsSavedForTheNextAllow()
    {
        using var h = new Harness();
        h.BlockAll();

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(AudioProtectionController.SavedForAllowMessage, result.UserMessage);
        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.IsTrue(new ProtectionIntentFile(h.Store.Folder).Read().Value!.Protect);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ProtectionGateRunner.RefusedStep));
    }

    [TestMethod]
    public async Task WhileBlockedARequestThatCannotBeSavedIsNotReportedAsSaved()
    {
        using var h = new Harness();
        h.BlockAll();
        Directory.CreateDirectory(h.Intent.FilePath);

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(AudioProtectionController.SaveFailedMessage, result.UserMessage);
        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "write-protection-intent" && !s.Ok));
    }

    [TestMethod]
    public async Task AnotherDeviceChangeHoldingTheLockIsReportedAsBusy()
    {
        using var h = new Harness();
        h.PlayAGateThatWrites(GateExitCode.Failed,
            StepOutcomes.FromWin32(DeviceChangeLock.StepName, 32, "another Earshot device change was still running"));

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(AudioProtectionController.BusyMessage, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
    }

    [TestMethod]
    public async Task AKeptRequestThatDiffersIsReplacedEvenWhenTheServicesAlreadyMatch()
    {
        using var h = new Harness();
        h.BlockAll();
        Assert.AreEqual(AudioProtectionController.SavedForAllowMessage, (await h.Controller.ApplyAsync(protect: true)).UserMessage);

        // Handsfree is still on, so off already matches, but the kept on request must not be applied later.
        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(AudioProtectionController.SavedForAllowMessage, result.UserMessage);
        Assert.HasCount(2, h.Tasks.Runs);
        Assert.AreEqual(GateVerbs.ProtectOff, h.Tasks.Runs[1][0]);
        Assert.IsFalse(h.Intent.Read().Value!.Protect);
        Assert.IsEmpty(h.Bluetooth.SetCalls);
    }

    [TestMethod]
    public async Task AKeptRequestThatMatchesStartsNothing()
    {
        using var h = new Harness();
        Assert.IsTrue(h.Intent.Write(false).Ok);

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.AlreadyInState, result.Status);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task AKeptRequestIsClearedWhenAnAllowedRequestCompletes()
    {
        using var h = new Harness();
        Assert.IsTrue(h.Intent.Write(true).Ok);

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(AudioProtectionController.OffMessage, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
        Assert.IsFalse(File.Exists(h.Intent.FilePath));
        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.Contains(ProtectedServices.Handsfree, h.AirPods.Enabled);
    }

    [TestMethod]
    public async Task AnEntryForAServiceWindowsTurnedBackOnIsDroppedWithoutACall()
    {
        using var h = new Harness();
        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord { DisabledServices = [ProtectedServices.Handsfree] }).Ok);

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
        Assert.IsEmpty(h.Bluetooth.SetCalls);
        Assert.IsEmpty(h.Recorded(), "A later off outside Earshot is never undone by a restore.");
    }

    [TestMethod]
    public async Task AnInvalidRecordWithHandsfreeOnIsAlreadyOff()
    {
        using var h = new Harness();
        File.WriteAllText(h.Store.ProtectionFile, "{}");

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.AlreadyInState, result.Status);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public async Task OffOutsideEarshotReplacesAKeptOnRequestAndLeavesHandsfreeOff()
    {
        using var h = new Harness();
        h.AirPods.TurnOffOutside(ProtectedServices.Handsfree);
        Assert.IsTrue(h.Intent.Write(true).Ok);

        ControllerResult result = await h.Controller.ApplyAsync(protect: false);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(AudioProtectionController.OffOutsideEarshotMessage, result.UserMessage);
        Assert.HasCount(1, h.Tasks.Runs);
        Assert.IsFalse(File.Exists(h.Intent.FilePath));
        Assert.DoesNotContain(ProtectedServices.Handsfree, h.AirPods.Enabled);
    }

    [TestMethod]
    public async Task TheKeptRequestCanBeRead()
    {
        using var h = new Harness();
        Assert.AreEqual(GateReadStatus.Missing, (await h.Controller.GetPendingIntentAsync()).Status);
        h.BlockAll();
        await h.Controller.ApplyAsync(protect: true);

        GateRead<ProtectionIntent> pending = await h.Controller.GetPendingIntentAsync();

        Assert.IsTrue(pending.IsOk, pending.Step.Detail);
        Assert.IsTrue(pending.Value!.Protect);
    }

    [TestMethod]
    public async Task ASharedWorkerOutlivesTheController()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        Paths paths = Paths.FromEnvironment(name => name == "EARSHOT_DATA_ROOT" ? temp.Path : null);
        using var worker = new SystemWorker(log);

        AudioProtectionController.Create(log, new FakeSettings(), paths, worker).Dispose();

        Assert.AreEqual(7, await worker.RunAsync(_ => 7));
    }

    [TestMethod]
    public async Task AnUnpairedDeviceIsReportedAsNotFound()
    {
        using var h = new Harness();
        h.Bluetooth.Remove(RecordedNodes.AirPodsAddress);

        ControllerResult result = await h.Controller.ApplyAsync(protect: true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.NotFoundMessage, result.UserMessage);
    }

    [TestMethod]
    public void TheReadBackMapsEveryState()
    {
        var completed = new GateRunResult(GateRunOutcome.Completed, null, 0, []);
        var timedOut = new GateRunResult(GateRunOutcome.TimedOut, null, null, []);
        var cancelled = new GateRunResult(GateRunOutcome.Cancelled, null, null, []);

        Assert.AreEqual(OpStatus.Success, AudioProtectionController.MapReadBack(true, AudioProtectionState.Protected, timedOut, []).Status);
        Assert.AreEqual(OpStatus.Success, AudioProtectionController.MapReadBack(false, AudioProtectionState.NotProtected, completed, []).Status);
        Assert.AreEqual(OpStatus.Partial, AudioProtectionController.MapReadBack(true, AudioProtectionState.Partial, completed, []).Status);
        Assert.AreEqual(AudioProtectionController.OffFailedMessage, AudioProtectionController.MapReadBack(false, AudioProtectionState.Partial, completed, []).UserMessage);
        Assert.AreEqual(AudioProtectionController.TimedOutMessage, AudioProtectionController.MapReadBack(true, AudioProtectionState.NotProtected, timedOut, []).UserMessage);
        Assert.AreEqual(AudioProtectionController.CancelledMessage, AudioProtectionController.MapReadBack(true, AudioProtectionState.NotProtected, cancelled, []).UserMessage);
        Assert.AreEqual(AudioProtectionController.UnreadableMessage, AudioProtectionController.MapReadBack(true, AudioProtectionState.Unknown, completed, []).UserMessage);
        Assert.AreEqual(AudioProtectionController.OffFailedMessage, AudioProtectionController.MapReadBack(false, AudioProtectionState.Protected, completed, []).UserMessage);
    }

    [TestMethod]
    [DataRow(true, AudioProtectionState.Protected, AudioProtectionController.ProtectedWithProblemMessage)]
    [DataRow(false, AudioProtectionState.NotProtected, AudioProtectionController.OffWithProblemMessage)]
    public void AMatchingReadBackAfterAGateThatReportedAFailureIsPartial(bool protect, AudioProtectionState state, string message)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        foreach (GateExitCode exit in new[] { GateExitCode.Partial, GateExitCode.Failed })
        {
            var status = new GateStatusFile(GateStore.SchemaVersion, "0123456789abcdef0123456789abcdef", GateVerbs.ProtectOn, now, now,
                GateExitCodes.ResultName(exit), (int)exit, null, [], StepsTruncated: false);
            var run = new GateRunResult(GateRunOutcome.Completed, status, (int)exit, []);

            ControllerResult result = AudioProtectionController.MapReadBack(protect, state, run, []);

            Assert.AreEqual(OpStatus.Partial, result.Status);
            Assert.AreEqual(message, result.UserMessage);
        }

        var clean = new GateStatusFile(GateStore.SchemaVersion, "0123456789abcdef0123456789abcdef", GateVerbs.ProtectOn, now, now,
            "success", 0, null, [], StepsTruncated: false);
        Assert.AreEqual(OpStatus.Success, AudioProtectionController.MapReadBack(protect, state, new GateRunResult(GateRunOutcome.Completed, clean, 0, []), []).Status);
    }

    [TestMethod]
    public void EveryMessageIsPlainBritishEnglish()
    {
        string[] messages =
        [
            AudioProtectionController.NotSetUpMessage, AudioProtectionController.NeedsRepairMessage, AudioProtectionController.CouldNotStartMessage,
            AudioProtectionController.ProtectedMessage, AudioProtectionController.AlreadyProtectedMessage, AudioProtectionController.OffMessage,
            AudioProtectionController.AlreadyOffMessage, AudioProtectionController.SavedForAllowMessage, AudioProtectionController.OffOutsideEarshotMessage,
            AudioProtectionController.PartialMessage, AudioProtectionController.ProtectFailedMessage, AudioProtectionController.OffFailedMessage,
            AudioProtectionController.UnreadableMessage, AudioProtectionController.TimedOutMessage, AudioProtectionController.CancelledMessage,
            AudioProtectionController.SaveFailedMessage, AudioProtectionController.BusyMessage,
            AudioProtectionController.ProtectedWithProblemMessage, AudioProtectionController.OffWithProblemMessage,
        ];

        foreach (string message in messages)
        {
            Assert.DoesNotContain("\u2014", message);
            Assert.IsLessThanOrEqualTo(80, message.Length, message);
            Assert.IsFalse(message.Contains('%', StringComparison.Ordinal), message);
        }
    }

    [TestMethod]
    public void TheCompositionWiresTheControllerBehindSafeMode()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();

        ServiceRegistry registry = CompositionRoot.Build(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: true);
        try
        {
            Assert.IsInstanceOfType<SafeAudioProtectionController>(registry.Protection);
            Assert.IsInstanceOfType<AudioProtectionController>(((SafeAudioProtectionController)registry.Protection).Inner);
        }
        finally
        {
            ((AudioProtectionController)((SafeAudioProtectionController)registry.Protection).Inner).Dispose();
            ((IDisposable)((SafeBlockController)registry.Block).Inner).Dispose();
            registry.Monitor.Dispose();
            registry.Worker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
