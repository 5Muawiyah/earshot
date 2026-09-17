using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The machine-wide gate run lock and the device change lock around every node change. The gate runs over the
// recorded fake node table in a temp folder; the real mutex is only ever created under a private test name in
// this session's namespace, with an access list that also grants the current user.
[TestClass]
public sealed class GateSerialisationTests
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
            Assert.IsTrue(Store.WriteDevice(RecordedNodes.AirPods()).Ok);
            Assert.IsTrue(Store.WriteConfig(new GateConfig { BlockAtBoot = true }).Ok);
        }

        public string Root => _temp.Path;

        public string Machine { get; }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public CapturingLog Log { get; } = new();

        public GateExitCode Run(string verb, string? address = null, IGateRunLock? runLock = null, Func<TimeSpan, bool>? wait = null) =>
            new GateActions(Nodes, Store, new FakeFolderSecurity(), Log, new ManualTime(), runLock) { LockWait = wait ?? DeviceChangeLock.SleepAndContinue }
                .Run(new GateRequest(verb, Nonce, address));

        public GateStatusFile Status()
        {
            GateRead<GateStatusFile> read = Store.ReadStatus(Nonce);
            Assert.IsTrue(read.IsOk, read.Step.Detail);
            return read.Value!;
        }

        // What another change holding the device change lock looks like from here.
        public FileStream HoldDeviceChangeLock() =>
            new(Path.Combine(Machine, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

        public void Dispose() => _temp.Dispose();
    }

    // A run lock that is always held by someone else.
    private sealed class BusyRunLock : IGateRunLock
    {
        public List<TimeSpan> Waits { get; } = new();

        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            Waits.Add(timeout);
            steps.Add(StepOutcomes.FromWin32(MachineGateMutex.StepName, MachineGateMutex.WaitTimeout, "held elsewhere", ok: false));
            return null;
        }
    }

    // A run lock that records when it is entered and left.
    private sealed class RecordingRunLock : IGateRunLock
    {
        public List<string> Events { get; } = new();

        // Runs as the lock is left.
        public Action? OnExit { get; set; }

        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            Events.Add("enter " + timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new Exit(this);
        }

        private sealed class Exit(RecordingRunLock owner) : IDisposable
        {
            public void Dispose()
            {
                owner.OnExit?.Invoke();
                owner.Events.Add("exit");
            }
        }
    }

    // Says a run has started waiting for the lock it wraps.
    private sealed class SignallingRunLock(IGateRunLock inner, ManualResetEventSlim entering) : IGateRunLock
    {
        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            entering.Set();
            return inner.TryEnter(timeout, steps);
        }
    }

    private static readonly Func<TimeSpan, bool> StopAtOnce = _ => false;

    private static readonly string[] MachineHolders = ["S-1-5-18 FullControl Allow", "S-1-5-32-544 FullControl Allow"];

    [TestMethod]
    [DataRow(GateVerbs.Block, null)]
    [DataRow(GateVerbs.Allow, null)]
    [DataRow(GateVerbs.Boot, null)]
    [DataRow(GateVerbs.SetDevice, RecordedNodes.AirPodsAddress)]
    public void EveryNodeChangeTakesTheDeviceChangeLock(string verb, string? address)
    {
        using var h = new Harness();

        h.Run(verb, address);

        StepOutcome taken = h.Status().Steps.Single(s => s.Step == DeviceChangeLock.StepName);
        Assert.IsTrue(taken.Ok, taken.Detail);
        using FileStream released = h.HoldDeviceChangeLock();
    }

    [TestMethod]
    [DataRow(GateVerbs.Block, null)]
    [DataRow(GateVerbs.Allow, null)]
    [DataRow(GateVerbs.Boot, null)]
    [DataRow(GateVerbs.SetDevice, RecordedNodes.IPhoneAddress)]
    public void ANodeChangeChangesNothingWhileAnotherChangeHoldsTheDeviceChangeLock(string verb, string? address)
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        GateExitCode exit;
        using (h.HoldDeviceChangeLock())
        {
            exit = h.Run(verb, address, wait: StopAtOnce);
        }

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, h.Store.ReadDevice().Value!.Address, "device.json is not rewritten.");
        GateStatusFile status = h.Status();
        Assert.IsTrue(status.Steps.Any(DeviceChangeLock.IsBusy));
        Assert.IsFalse(status.Steps.Any(s => s.Step.StartsWith("cm-", StringComparison.Ordinal)), "The nodes are not even read without the lock.");
    }

    [TestMethod]
    public void ARunThatCannotEnterTheRunLockChangesNothingAndSaysWhy()
    {
        using var h = new Harness();
        var busy = new BusyRunLock();
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(h.Store.StatusFile(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), "{}");
        }

        GateExitCode exit = h.Run(GateVerbs.Block, runLock: busy);

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsEmpty(h.Nodes.Calls);
        GateStatusFile status = h.Status();
        Assert.AreEqual("failed", status.Result);
        Assert.IsTrue(status.Steps.Any(MachineGateMutex.IsBusy));
        Assert.HasCount(21, Directory.GetFiles(h.Machine, "status-*.json"), "Nothing is pruned without the lock.");
        CollectionAssert.AreEqual(new[] { GateActions.GateRunWait }, busy.Waits);
    }

    [TestMethod]
    public void TheRunLockIsHeldForTheWholeRunAndReleasedAfterTheStatusFile()
    {
        using var h = new Harness();
        var recording = new RecordingRunLock();

        Assert.AreEqual(GateExitCode.Success, h.Run(GateVerbs.Block, runLock: recording));

        CollectionAssert.AreEqual(new[] { "enter " + GateActions.GateRunWait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "exit" }, recording.Events);
        Assert.HasCount(9, h.Nodes.Calls);
    }

    [TestMethod]
    public void AProtectRunWaitsLongerThanAGateRunForTheRunLock()
    {
        using var h = new Harness();
        var busy = new BusyRunLock();

        GateExitCode exit = new GateActions(h.Nodes, h.Store, new FakeFolderSecurity(), h.Log, new ManualTime(), busy)
            .Run(new GateRequest(GateVerbs.ProtectOn, Nonce, null, GateMode.Protect));

        Assert.AreEqual(GateExitCode.Failed, exit);
        CollectionAssert.AreEqual(new[] { GateActions.ProtectRunWait }, busy.Waits);
        Assert.IsGreaterThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.GateTimeLimit), GateActions.ProtectRunWait, "A protect run outlasts a whole gate run.");
        Assert.IsLessThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.ProtectTimeLimit), GateActions.ProtectRunWait, "Time is left for the service calls.");
        Assert.IsLessThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.GateTimeLimit), GateActions.GateRunWait, "Time is left for the node change.");
    }

    [TestMethod]
    public void TheRealGateAndUninstallUseTheMachineMutex()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();

        // Constructing either makes no native call and takes no lock.
        Assert.IsInstanceOfType<MachineGateMutex>(GateActions.ForMachine(temp.Path, log).RunLock);
        Assert.IsInstanceOfType<NoGateRunLock>(new GateActions(RecordedNodes.Table(), new GateStore(temp.Path), new FakeFolderSecurity(), log, new ManualTime()).RunLock);
    }

    [TestMethod]
    public void UninstallChangesNoNodeOrServiceWithoutTheLocks()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        string install = Path.Combine(h.Root, "ProgramFiles", "Earshot");
        var layout = new InstallLayout(Path.Combine(h.Root, "unzip"), install, h.Machine);

        InstallResult result;
        using (h.HoldDeviceChangeLock())
        {
            result = new UninstallActions(layout, new FakeFolderSecurity(), h.Nodes, new FakeTaskRegistrar(), new RebootDeleteRecorder(), h.Log,
                runLock: null, StopAtOnce, TimeSpan.FromSeconds(1)).Run();
        }

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsTrue(result.Steps.Any(DeviceChangeLock.IsBusy));
        StringAssert.Contains(result.Steps.Single(s => s.Step == "allow-nodes").Detail, "Run uninstall again");
    }

    [TestMethod]
    public void UninstallChangesNoNodeWithoutTheRunLock()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        var layout = new InstallLayout(Path.Combine(h.Root, "unzip"), Path.Combine(h.Root, "ProgramFiles", "Earshot"), h.Machine);
        var busy = new BusyRunLock();

        InstallResult result = new UninstallActions(layout, new FakeFolderSecurity(), h.Nodes, new FakeTaskRegistrar(), new RebootDeleteRecorder(), h.Log,
            busy, StopAtOnce, TimeSpan.FromSeconds(1)).Run();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsTrue(result.Steps.Any(MachineGateMutex.IsBusy));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, busy.Waits);
        Assert.IsGreaterThan(System.Xml.XmlConvert.ToTimeSpan(TaskPlan.ProtectTimeLimit), UninstallActions.LockWait, "Uninstall outwaits a whole protect run.");
        Assert.IsFalse(result.Steps.Any(s => s.Step == DeviceChangeLock.StepName), "The device change lock is not tried without the run lock.");
    }

    // A gate run that was already waiting for the run lock while uninstall restored the nodes gets in only after
    // uninstall has removed the records and the folder, so it cannot disable the nodes again. The two runs share
    // one real mutex under a private test name, on two threads, as two processes would.
    [TestMethod]
    [DataRow(GateVerbs.Block)]
    [DataRow(GateVerbs.Boot)]
    public void AGateRunQueuedBehindUninstallChangesNothing(string verb)
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        string name = NewName();
        var layout = new InstallLayout(Path.Combine(h.Root, "unzip"), Path.Combine(h.Root, "ProgramFiles", "Earshot"), h.Machine);
        var tasks = new FakeTaskRegistrar();
        var gateLog = new CapturingLog();
        using var waiting = new ManualResetEventSlim();
        GateExitCode? queuedExit = null;
        Thread? queued = null;
        bool queuedBeforeRelease = false;
        tasks.OnList = () =>
        {
            // Uninstall holds the run lock here: the nodes are allowed, the tasks and the folder are not yet removed.
            queued = new Thread(() => queuedExit =
                new GateActions(h.Nodes, h.Store, new FakeFolderSecurity(), gateLog, new ManualTime(), new SignallingRunLock(TestMutex(name), waiting))
                    .Run(new GateRequest(verb, Nonce, null)));
            queued.Start();
            queuedBeforeRelease = waiting.Wait(TimeSpan.FromSeconds(10));
        };

        InstallResult result = new UninstallActions(layout, new FakeFolderSecurity(), h.Nodes, tasks, new RebootDeleteRecorder(), h.Log,
            TestMutex(name), DeviceChangeLock.SleepAndContinue, TimeSpan.FromSeconds(5)).Run();

        Assert.IsNotNull(queued);
        Assert.IsTrue(queued.Join(TimeSpan.FromSeconds(30)), "The queued gate run finished.");
        Assert.IsTrue(queuedBeforeRelease, "The gate run started waiting while uninstall held the lock.");
        Assert.AreEqual(GateExitCode.Success, result.Outcome, string.Join(Environment.NewLine, result.Steps.Where(s => !s.Ok).Select(GateActions.Describe)));
        Assert.AreEqual(GateExitCode.FolderNotSecure, queuedExit, "The folder is gone by the time the gate run gets in.");
        Assert.IsEmpty(h.Nodes.Calls.Where(c => c.Kind == "disable"), "No node is disabled again after uninstall allowed it.");
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => !h.Nodes[id].IsDisabled));
    }

    [TestMethod]
    public void UninstallRetiresTheRecordsBeforeItLetsGoOfTheDeviceChangeLockAndHoldsTheRunLockUntilTheFolderIsGone()
    {
        using var h = new Harness();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord()).Ok);
        Assert.IsTrue(new ProtectionIntentFile(h.Machine).Write(true).Ok);
        var layout = new InstallLayout(Path.Combine(h.Root, "unzip"), Path.Combine(h.Root, "ProgramFiles", "Earshot"), h.Machine);
        var tasks = new FakeTaskRegistrar();
        string?[]? leftWhenTheTasksGo = null;
        tasks.OnList = () => leftWhenTheTasksGo = Directory.GetFiles(h.Machine).Select(Path.GetFileName).ToArray();
        bool? folderWhenTheRunLockGoes = null;
        var running = new RecordingRunLock { OnExit = () => folderWhenTheRunLockGoes = Directory.Exists(h.Machine) };

        InstallResult result = new UninstallActions(layout, new FakeFolderSecurity(), h.Nodes, tasks, new RebootDeleteRecorder(), h.Log,
            running, StopAtOnce, TimeSpan.FromSeconds(1)).Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, string.Join(Environment.NewLine, result.Steps.Where(s => !s.Ok).Select(GateActions.Describe)));
        CollectionAssert.AreEqual(new[] { DeviceChangeLock.FileName }, leftWhenTheTasksGo,
            "Once the nodes are allowed nothing a gate run acts on is left, and only the released lock file remains.");
        Assert.IsFalse(folderWhenTheRunLockGoes, "The run lock is held until the machine folder is removed.");
        Assert.HasCount(2, running.Events);
        CollectionAssert.AreEquivalent(
            new[] { "device.json", "config.json", "protection.json", ProtectionIntentFile.FileName },
            result.Steps.Where(s => s.Step.StartsWith(UninstallActions.RetireStepPrefix, StringComparison.Ordinal) && s.Ok)
                .Select(s => s.Step[UninstallActions.RetireStepPrefix.Length..]).ToArray());
    }

    [TestMethod]
    public void UninstallKeepsTheFolderWhenTheRunLockCannotBeEnteredEvenWithoutADeviceFile()
    {
        using var h = new Harness();
        File.Delete(h.Store.DeviceFile);
        var layout = new InstallLayout(Path.Combine(h.Root, "unzip"), Path.Combine(h.Root, "ProgramFiles", "Earshot"), h.Machine);

        InstallResult result = new UninstallActions(layout, new FakeFolderSecurity(), h.Nodes, new FakeTaskRegistrar(), new RebootDeleteRecorder(), h.Log,
            new BusyRunLock(), StopAtOnce, TimeSpan.FromSeconds(1)).Run();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsTrue(Directory.Exists(h.Machine), "Another elevated run may still be using it.");
        StringAssert.Contains(result.Steps.Last(s => s.Step == "remove-machine-folder").Detail, "held the gate run lock");
        Assert.IsTrue(result.Steps.Any(MachineGateMutex.IsBusy));
    }

    [TestMethod]
    public void BusyGateStepsReadAsBusyToTheTray()
    {
        StepOutcome mutex = StepOutcomes.FromWin32(MachineGateMutex.StepName, MachineGateMutex.WaitTimeout, "held", ok: false);
        StepOutcome file = StepOutcomes.FromWin32(DeviceChangeLock.StepName, 32, "held", ok: false);
        StepOutcome other = StepOutcomes.FromWin32(MachineGateMutex.StepName, 5, "denied", ok: false);

        Assert.IsTrue(BlockController.IsBusy(RunWith(mutex)));
        Assert.IsTrue(BlockController.IsBusy(RunWith(file)));
        Assert.IsFalse(BlockController.IsBusy(RunWith(other)), "Only a lock someone else held is busy.");
        Assert.IsFalse(BlockController.IsBusy(new GateRunResult(GateRunOutcome.TimedOut, null, null, [mutex])), "Only the gate's own status file counts.");
    }

    [TestMethod]
    public void AProtectRequestKeptWhileBlockedIsNamedDeviceBlocked()
    {
        GateExitCode kept = ProtectionGateRunner.BlockedExit;

        Assert.AreEqual("device-blocked", GateExitCodes.ResultName(kept));
        Assert.AreEqual("device-blocked", GateExitCodes.NameOf((int)kept));
        Assert.AreEqual("other-device-blocked", GateExitCodes.NameOf((int)GateExitCode.OtherDeviceBlocked), "set-device keeps its own name.");
    }

    private static GateRunResult RunWith(StepOutcome step)
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var status = new GateStatusFile(GateStore.SchemaVersion, Nonce, GateVerbs.Block, now, now, "failed", (int)GateExitCode.Failed, null, [step], false);
        return new GateRunResult(GateRunOutcome.Completed, status, (int)GateExitCode.Failed, []);
    }

    // ---- the real mutex, under a private name in this session ----

    private static MachineGateMutex TestMutex(string name, Func<SecurityIdentifier?, bool>? trusted = null) =>
        new(name, UserAndMachineSecurity, trusted ?? (_ => true));

    private static string NewName() => @"Local\Earshot.Tests.GateRunLock." + Guid.NewGuid().ToString("N");

    private static MutexSecurity UserAndMachineSecurity()
    {
        MutexSecurity security = MachineGateMutex.MachineSecurity();
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(me.User!, MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [TestMethod]
    public void TheMutexIsEnteredReleasedAndEnteredAgain()
    {
        string name = NewName();
        var steps = new List<StepOutcome>();

        using (IDisposable? first = TestMutex(name).TryEnter(TimeSpan.FromSeconds(5), steps))
        {
            Assert.IsNotNull(first);
            Assert.IsTrue(steps.Single().Ok);

            var other = new List<StepOutcome>();
            IDisposable? second = null;
            var thread = new Thread(() => second = TestMutex(name).TryEnter(TimeSpan.FromMilliseconds(50), other));
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));

            Assert.IsNull(second, "Held by another thread, so the wait runs out.");
            StepOutcome busy = other.Single();
            Assert.IsTrue(MachineGateMutex.IsBusy(busy), busy.Detail);
            Assert.AreEqual((int)MachineGateMutex.WaitTimeout, busy.Code);
        }

        var again = new List<StepOutcome>();
        using IDisposable? third = TestMutex(name).TryEnter(TimeSpan.FromSeconds(5), again);
        Assert.IsNotNull(third, again.FirstOrDefault()?.Detail);
    }

    [TestMethod]
    public void AMutexLeftByARunThatEndedIsEnteredAndNoted()
    {
        string name = NewName();
        using var keepAlive = new Mutex(false, name);
        var thread = new Thread(() =>
        {
            var steps = new List<StepOutcome>();
            Assert.IsNotNull(TestMutex(name).TryEnter(TimeSpan.FromSeconds(5), steps));

            // Ends without releasing, as a process stopped at its time limit would.
        });
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));

        var after = new List<StepOutcome>();
        using IDisposable? entered = TestMutex(name).TryEnter(TimeSpan.FromSeconds(5), after);

        Assert.IsNotNull(entered);
        StepOutcome step = after.Single();
        Assert.IsTrue(step.Ok);
        StringAssert.Contains(step.Detail, "without releasing");
    }

    [TestMethod]
    public void AMutexSomeoneElseCreatedIsNotTrusted()
    {
        string name = NewName();
        using var squatter = new Mutex(false, name);
        var steps = new List<StepOutcome>();

        // Trusting only SYSTEM here, so the test's own mutex is untrusted whether or not the test runs elevated.
        IDisposable? entered = new MachineGateMutex(name, UserAndMachineSecurity, owner => owner?.Value == Sddl.LocalSystemSid).TryEnter(TimeSpan.FromSeconds(1), steps);

        Assert.IsNull(entered);
        StepOutcome refused = steps.Single();
        Assert.IsFalse(refused.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, refused.Code);
        StringAssert.Contains(refused.Detail, "not trusted");
        Assert.IsFalse(MachineGateMutex.IsBusy(refused), "A mutex that is not trusted is not a busy one.");
    }

    [TestMethod]
    public void TheMachineMutexGrantsOnlySystemAndAdministratorsAndTrustsOnlyTheirOwnership()
    {
        MutexSecurity security = MachineGateMutex.MachineSecurity();

        Assert.IsTrue(security.AreAccessRulesProtected);
        List<string> holders = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value + " " + r.MutexRights + " " + r.AccessControlType)
            .ToList();
        CollectionAssert.AreEquivalent(MachineHolders, holders);

        Assert.IsTrue(MachineGateMutex.IsMachineOwner(new SecurityIdentifier(Sddl.LocalSystemSid)));
        Assert.IsTrue(MachineGateMutex.IsMachineOwner(new SecurityIdentifier(Sddl.AdministratorsSid)));
        Assert.IsFalse(MachineGateMutex.IsMachineOwner(new SecurityIdentifier(TestUsers.Sid)));
        Assert.IsFalse(MachineGateMutex.IsMachineOwner(null));
        StringAssert.StartsWith(MachineGateMutex.DefaultName, @"Global\");
    }
}
