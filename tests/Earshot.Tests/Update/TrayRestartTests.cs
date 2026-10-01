using System.Runtime.InteropServices;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Tests.Interop;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// Earshot is running again after an update. The install that ends an update is the last program to run, and it is elevated, so
// it starts the tray through a one-shot Task Scheduler task for the signed-in user (TrayRestarter) and only for an install that
// completes an update's record. Everything runs against temporary folders with only the boundaries faked: the tray probe, the
// task run and the in-memory task scheduler. The real probe, the real task definition and the real scheduler connection each
// have an execution of their own at the end, because a fake at a boundary proves everything except the boundary.
[TestClass]
public sealed class TrayRestartTests
{
    private const string InstallFolder = @"C:\Program Files\Earshot";

    private const int RunRefused = unchecked((int)0x8004130F);

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeProbe : ITrayInstanceProbe
    {
        public TrayInstance State { get; set; } = TrayInstance.NotRunning;

        public Func<TrayInstance>? Script { get; set; }

        public int Probes { get; private set; }

        public TrayProbe Probe()
        {
            Probes++;
            TrayInstance state = Script?.Invoke() ?? State;
            return new TrayProbe(state, new StepOutcome(MutexTrayInstanceProbe.Step, state != TrayInstance.Unknown, 0, "S_OK", state.ToString()));
        }
    }

    private sealed class FakeRunner(FakeProbe probe) : ITaskRunner
    {
        public List<string> Runs { get; } = new();

        public int Result { get; set; }

        // The tray the run starts: seen by the probe from now on.
        public bool StartsATray { get; set; } = true;

        public int RunNow(string taskPath)
        {
            Runs.Add(taskPath);
            if (Result >= 0 && StartsATray)
            {
                probe.State = TrayInstance.Running;
            }

            return Result;
        }

        public int ReadResult(string taskPath, out int state, out int? lastTaskResult)
        {
            state = TaskSchedulerCom.TASK_STATE_READY;
            lastTaskResult = RunRefused;
            return 0;
        }
    }

    // Passes everything to the in-memory scheduler and keeps what it was asked to register.
    private sealed class CapturingRegistrar(FakeTaskRegistrar inner) : ITaskRegistrar
    {
        public List<TaskSpec> Specs { get; } = new();

        public int ReadFolderSddl(string folderPath, out string? sddl) => inner.ReadFolderSddl(folderPath, out sddl);

        public int ListTasks(string folderPath, out IReadOnlyList<string> names) => inner.ListTasks(folderPath, out names);

        public int DeleteTask(string folderPath, string name) => inner.DeleteTask(folderPath, name);

        public int DeleteFolder(string parentPath, string name) => inner.DeleteFolder(parentPath, name);

        public int CreateFolder(string parentPath, string name, string sddl) => inner.CreateFolder(parentPath, name, sddl);

        public int Register(string folderPath, TaskSpec spec, IList<StepOutcome> steps)
        {
            Specs.Add(spec);
            return inner.Register(folderPath, spec, steps);
        }

        public int ReadTask(string taskPath, out string? sddl, out string? xml) => inner.ReadTask(taskPath, out sddl, out xml);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Tasks = new FakeTaskRegistrar { FolderSddl = Sddl.TaskFolder(TestUsers.Sid) };
            Registrar = new CapturingRegistrar(Tasks);
            Runner = new FakeRunner(Probe);
            Restarter = new TrayRestarter(Probe, Registrar, Runner, Log) { Wait = _ => true };
        }

        public FakeProbe Probe { get; } = new();

        public FakeTaskRegistrar Tasks { get; }

        public CapturingRegistrar Registrar { get; }

        public FakeRunner Runner { get; }

        public CapturingLog Log { get; } = new();

        public TrayRestarter Restarter { get; set; }

        public IReadOnlyList<StepOutcome> Restart() => Restarter.Restart(TestUsers.Sid, InstallFolder);
    }

    private static string Describe(IEnumerable<StepOutcome> steps) =>
        string.Join(Environment.NewLine, steps.Select(GateActions.Describe));

    private static StepOutcome Final(IReadOnlyList<StepOutcome> steps) => steps[^1];

    // ----- the start itself -----

    [TestMethod]
    public void ItStartsExactlyOneTrayThroughAOneShotTaskForTheUserWithTheLeastRunLevel()
    {
        var rig = new Rig();

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        Assert.HasCount(1, rig.Registrar.Specs, "One task was registered.");
        TaskSpec spec = rig.Registrar.Specs[0];
        Assert.AreEqual("StartTray", spec.Name);
        Assert.AreEqual(TestUsers.Sid, spec.Principal.UserId, "For the user the install was given, not the elevated account.");
        Assert.AreEqual(TaskSchedulerCom.TASK_LOGON_INTERACTIVE_TOKEN, spec.Principal.LogonType);
        Assert.AreEqual(TaskSchedulerCom.TASK_RUNLEVEL_LUA, spec.Principal.RunLevel, "Never elevated.");
        Assert.AreEqual(Path.Combine(InstallFolder, "Earshot.exe"), spec.ExecutablePath, "The installed program, in the administrators-only folder.");
        Assert.AreEqual("", spec.Arguments, "A start by hand, not the start at sign-in.");
        Assert.AreEqual(4, spec.Priority);
        Assert.AreEqual("PT0S", spec.ExecutionTimeLimit, "No time limit, or the scheduler ends the tray after three days.");
        Assert.IsFalse(spec.BootTrigger);
        Assert.AreEqual(Sddl.ReadableTask(TestUsers.Sid), spec.Sddl, "The user may read the task and nothing more.");
        Assert.HasCount(1, rig.Runner.Runs, "Run once, at once.");
        Assert.AreEqual(@"\Earshot\StartTray", rig.Runner.Runs[0]);
        Assert.IsEmpty(rig.Tasks.Tasks, "Removed after the run.");
        Assert.IsTrue(Final(steps).Ok, Describe(steps));
        Assert.AreEqual(TrayRestarter.Step, Final(steps).Step);
    }

    [TestMethod]
    public void TheTaskIsRegisteredAfterAnEarlierOneIsRemovedAndRemovedAfterItRuns()
    {
        var rig = new Rig();
        rig.Tasks.Tasks[@"\Earshot\StartTray"] = ("stale", "<stale/>");
        var order = new List<string>();
        rig.Probe.Script = () =>
        {
            order.Add("probe");
            return rig.Probe.State;
        };

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        string[] calls = rig.Tasks.Calls.Where(c => c.StartsWith("delete-task", StringComparison.Ordinal) || c.StartsWith("register", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(
            new[] { "delete-task StartTray", "register StartTray " + TestUsers.Sid + " 3", "delete-task StartTray" }, calls,
            "A stale task is removed first, then the task is registered, then removed.");
        Assert.IsEmpty(rig.Tasks.Tasks);
        Assert.IsTrue(steps.Any(s => s.Step == "install-start-tray-stale" && s.Ok), Describe(steps));
        Assert.IsTrue(Final(steps).Ok, Describe(steps));
    }

    [TestMethod]
    public void NoTrayIsStartedWhenOneIsAlreadyRunning()
    {
        var rig = new Rig { Probe = { State = TrayInstance.Running } };

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        Assert.IsEmpty(rig.Tasks.Calls.Where(c => !c.StartsWith("read-", StringComparison.Ordinal)), "Nothing registered or removed: " + string.Join(", ", rig.Tasks.Calls));
        Assert.IsEmpty(rig.Registrar.Specs);
        Assert.IsEmpty(rig.Runner.Runs);
        Assert.IsTrue(Final(steps).Ok, "Nothing to start is not a failure.");
        StringAssert.Contains(Final(steps).Detail, "already running");
    }

    [TestMethod]
    public void NoTrayIsStartedOnAGuessWhenTheProbeCannotTell()
    {
        var rig = new Rig { Probe = { State = TrayInstance.Unknown } };

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        Assert.IsEmpty(rig.Registrar.Specs);
        Assert.IsEmpty(rig.Runner.Runs);
        Assert.IsFalse(Final(steps).Ok);
        StringAssert.Contains(Final(steps).Detail, "next sign-in");
    }

    [TestMethod]
    public void AUserThatIsNotAUserSidGetsNoTask()
    {
        var rig = new Rig();

        IReadOnlyList<StepOutcome> steps = rig.Restarter.Restart("S-1-5-18", InstallFolder);

        Assert.IsEmpty(rig.Registrar.Specs, "SYSTEM is never a principal for the tray.");
        Assert.IsEmpty(rig.Runner.Runs);
        Assert.IsFalse(Final(steps).Ok);
    }

    [TestMethod]
    public void ATaskFolderSomeoneElseCouldWriteIsNeverUsed()
    {
        var rig = new Rig();
        rig.Tasks.FolderSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + TestUsers.Sid + ")";

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        Assert.IsEmpty(rig.Registrar.Specs);
        Assert.IsEmpty(rig.Runner.Runs);
        Assert.IsTrue(steps.Any(s => s.Step == "install-start-tray-folder-acl" && !s.Ok), Describe(steps));
        Assert.IsFalse(Final(steps).Ok);
    }

    // ----- a start that does not happen never fails the install, and says what happened -----

    [TestMethod]
    public void AFailedRunIsAStepWithItsRawCodeTheTaskIsStillRemovedAndTheSentenceSaysWhenEarshotStarts()
    {
        var rig = new Rig();
        rig.Runner.Result = RunRefused;

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        StepOutcome run = steps.Single(s => s.Step == "install-start-tray-run");
        Assert.IsFalse(run.Ok);
        Assert.AreEqual(RunRefused, run.Code, "The raw code is kept.");
        Assert.IsEmpty(rig.Tasks.Tasks, "The task is removed whether or not it ran.");
        Assert.IsFalse(Final(steps).Ok);
        StringAssert.Contains(Final(steps).Detail, "next sign-in");
        Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "next sign-in"), "Logged, not silent.");
    }

    [TestMethod]
    public void ATaskThatCannotBeRegisteredIsNotRunAndTheRawCodeIsKept()
    {
        var rig = new Rig();
        rig.Tasks.RegisterFailsFor = 0;

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        Assert.IsEmpty(rig.Runner.Runs);
        StepOutcome register = steps.Single(s => s.Step == "install-start-tray-register");
        Assert.IsFalse(register.Ok);
        Assert.AreEqual(unchecked((int)0x80070005), register.Code);
        Assert.IsFalse(Final(steps).Ok);
    }

    [TestMethod]
    public void ATrayThatNeverAppearsIsWaitedForThenReportedWithTheTasksOwnState()
    {
        var rig = new Rig();
        rig.Runner.StartsATray = false;
        int waits = 0;
        rig.Restarter = new TrayRestarter(rig.Probe, rig.Registrar, rig.Runner, rig.Log)
        {
            Wait = _ =>
            {
                waits++;
                return true;
            },
        };

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        Assert.AreEqual(TrayRestarter.SeenPolls, waits, "It looks a fixed number of times.");
        StepOutcome seen = steps.Single(s => s.Step == "install-start-tray-seen");
        Assert.IsFalse(seen.Ok);
        StringAssert.Contains(seen.Detail, "last result 0x" + RunRefused.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsEmpty(rig.Tasks.Tasks, "Removed all the same.");
        Assert.IsFalse(Final(steps).Ok);
    }

    [TestMethod]
    public void ATrayThatEndsWhenItsTaskIsRemovedIsReportedAsNotStarted()
    {
        var rig = new Rig();
        // Seen after the run, gone once the task has been removed.
        int probes = 0;
        rig.Probe.Script = () => ++probes switch
        {
            1 => TrayInstance.NotRunning,
            2 => TrayInstance.Running,
            _ => TrayInstance.NotRunning,
        };

        IReadOnlyList<StepOutcome> steps = rig.Restart();

        StepOutcome after = steps.Single(s => s.Step == "install-start-tray-after-remove");
        Assert.IsFalse(after.Ok, "What was seen after the removal is recorded.");
        Assert.IsFalse(Final(steps).Ok);
        StringAssert.Contains(Final(steps).Detail, "ended when its start task was removed");
    }

    // ----- the install that ends an update -----

    private static InstallResult Done(GateExitCode outcome = GateExitCode.Success) =>
        new(outcome, [new StepOutcome("task-verify:BootBlock", outcome == GateExitCode.Success, 0, "S_OK", "ok")]);

    private sealed class Machine : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Machine()
        {
            Folder = _temp.File("Earshot");
            Directory.CreateDirectory(Folder);
            Folders = new FakeFolderSecurity { SddlFor = _ => Sddl.MachineFolder };
        }

        public string Folder { get; }

        public FakeFolderSecurity Folders { get; }

        public CapturingLog Log { get; } = new();

        public FixedTime Time { get; } = new(Start);

        public GateStore Store => new(Folder);

        public UpdateOutcomeRecorder Recorder() => new(Folder, Folders, Log, Time);

        public void Dispose() => _temp.Dispose();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Func<IReadOnlyList<StepOutcome>> CountingRestart(List<int> calls) => () =>
    {
        calls.Add(calls.Count + 1);
        return [new StepOutcome(TrayRestarter.Step, true, 0, "S_OK", "Earshot was started again.")];
    };

    [TestMethod]
    public void TheInstallThatCompletesAnUpdateStartsTheTrayAfterTheOutcomeIsRecorded()
    {
        using var machine = new Machine();
        UpdateOutcomeRecorder recorder = machine.Recorder();
        recorder.RecordUpdateRun(Done());
        machine.Time.Now = Start.AddSeconds(20);
        var calls = new List<int>();
        UpdateOutcomeKind? kindWhenStarted = null;

        InstallResult result = Program.CompleteInstall(Done(), recorder, "1.3.0", () =>
        {
            kindWhenStarted = machine.Store.ReadUpdateOutcome().Value?.Kind;
            return CountingRestart(calls)();
        });

        Assert.HasCount(1, calls, "Exactly one start.");
        Assert.AreEqual(UpdateOutcomeKind.Installed, kindWhenStarted, "The tray that starts finds the outcome already written, so it says it once.");
        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual(TrayRestarter.Step, result.Steps[^1].Step, "The start's steps are part of the install's.");
    }

    [TestMethod]
    public void AFirstInstallOrASetupByHandStartsNoTray()
    {
        using var machine = new Machine();
        var calls = new List<int>();

        InstallResult result = Program.CompleteInstall(Done(), machine.Recorder(), "1.3.0", CountingRestart(calls));

        Assert.IsEmpty(calls, "earshot.ps1 starts the tray after a first install, so the program does not.");
        Assert.HasCount(1, result.Steps);
        Assert.IsFalse(File.Exists(machine.Store.UpdateOutcomeFile));
    }

    [TestMethod]
    public void AnInstallLongAfterTheUpdateRecordOrAfterItWasCompletedStartsNoTray()
    {
        using var machine = new Machine();
        UpdateOutcomeRecorder recorder = machine.Recorder();
        recorder.RecordUpdateRun(Done());
        var calls = new List<int>();

        machine.Time.Now = Start.AddSeconds(20);
        Program.CompleteInstall(Done(), recorder, "1.3.0", CountingRestart(calls));
        machine.Time.Now = Start.AddSeconds(40);
        Program.CompleteInstall(Done(), recorder, "1.3.0", CountingRestart(calls));
        Assert.HasCount(1, calls, "The record is completed once, so a second install is a setup by hand.");

        recorder.RecordUpdateRun(Done());
        machine.Time.Now = Start.AddSeconds(40) + UpdateOutcomes.InstallWindow + TimeSpan.FromSeconds(1);
        Program.CompleteInstall(Done(), recorder, "1.3.0", CountingRestart(calls));
        Assert.HasCount(1, calls, "An install after the window is not the update's.");
    }

    [TestMethod]
    [DataRow((int)GateExitCode.Failed)]
    [DataRow((int)GateExitCode.Partial)]
    [DataRow((int)GateExitCode.FolderNotSecure)]
    public void AnUpdateWhoseInstallDidNotFinishStartsNoTray(int outcome)
    {
        using var machine = new Machine();
        UpdateOutcomeRecorder recorder = machine.Recorder();
        recorder.RecordUpdateRun(Done());
        machine.Time.Now = Start.AddSeconds(20);
        var calls = new List<int>();

        Program.CompleteInstall(Done((GateExitCode)outcome), recorder, "1.3.0", CountingRestart(calls));

        Assert.IsEmpty(calls, "The tasks and the service are not all in place, so nothing is started.");
        Assert.AreEqual(UpdateOutcomeKind.Failed, machine.Store.ReadUpdateOutcome().Value!.Kind, "How it ended is still recorded.");
    }

    [TestMethod]
    public void AStartThatFailsLeavesTheInstallASuccess()
    {
        using var machine = new Machine();
        UpdateOutcomeRecorder recorder = machine.Recorder();
        recorder.RecordUpdateRun(Done());
        machine.Time.Now = Start.AddSeconds(20);

        InstallResult result = Program.CompleteInstall(Done(), recorder, "1.3.0",
            () => [StepOutcomes.NotAttempted(TrayRestarter.Step, "Earshot was not started now: the tray did not start.")]);

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual(UpdateOutcomeKind.Installed, machine.Store.ReadUpdateOutcome().Value!.Kind);
        Assert.IsFalse(result.Steps[^1].Ok, "Its failure is a step, with its detail.");
    }

    // ----- the whole hand-over: what a 1.2.x tray starts, ending with a tray -----

    private sealed class CapturingStarter : IInstallStarter
    {
        public List<(string Executable, string[] Arguments, string WorkingDirectory)> Starts { get; } = new();

        public StepOutcome Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Starts.Add((executable, arguments.ToArray(), workingDirectory));
            return new StepOutcome(ChildInstallStarter.Step, true, 0, "S_OK", "started");
        }
    }

    private sealed class Waiter : IProcessWaiter
    {
        public StepOutcome WaitForExit(int processId, string image, TimeSpan timeout) =>
            new(ProcessExitWaiter.Step, true, 0, "S_OK", "ended");
    }

    [TestMethod]
    public void TheUpdateVerbThenTheNewReleasesInstallVerbEndsWithOneTrayStartedAfterTheTasksAreInPlace()
    {
        using var temp = new TempFolder();
        Paths data = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.File("data") : null);
        string install = temp.File(Path.Combine("ProgramFiles", "Earshot"));
        Directory.CreateDirectory(Path.GetDirectoryName(install)!);
        Directory.CreateDirectory(Path.GetDirectoryName(data.MachineFolder)!);
        string staging = temp.File(Path.Combine("user", "update", "u1"));
        Directory.CreateDirectory(staging);
        var folders = new FakeFolderSecurity { SddlFor = path => string.Equals(path, data.MachineFolder, StringComparison.OrdinalIgnoreCase) ? Sddl.MachineFolder : null };
        var tasks = new FakeTaskRegistrar();
        var nodes = RecordedNodes.Table();
        var log = new CapturingLog();
        var time = new FixedTime(Start);
        static AccountLookup NoLookup(string account) => throw new AssertFailedException("No account name should be looked up: " + account);

        InstallResult RunInstall(string source, InstallRequest request) =>
            new InstallActions(new InstallLayout(source, install, data.MachineFolder), folders, nodes, tasks, NoLookup, log).Run(request);

        // The release already installed: the 1.2.x tray's own files.
        string UnzipTo(string name, ReleaseZipBuilder release)
        {
            string folder = temp.File(Path.Combine("unzip", name));
            System.IO.Compression.ZipFile.ExtractToDirectory(new MemoryStream(release.Build()), folder);
            return Path.Combine(folder, "Earshot");
        }

        ReleaseZipBuilder Release(string version)
        {
            var release = new ReleaseZipBuilder();
            release.Files["Earshot.exe"] = System.Text.Encoding.ASCII.GetBytes("exe " + version);
            release.Files["Earshot.dll"] = System.Text.Encoding.ASCII.GetBytes("dll " + version);
            release.Files["runtimes/native.txt"] = System.Text.Encoding.ASCII.GetBytes("native " + version);
            return release;
        }

        var identity = new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System);
        Assert.AreEqual(GateExitCode.Success, RunInstall(UnzipTo("v1", Release("1.2.1")), identity).Outcome, "The starting install.");
        byte[] zip = Release("1.3.0").Build();
        string zipPath = Path.Combine(staging, UpdateService.ZipFileName);
        File.WriteAllBytes(zipPath, zip);
        var recorder = new UpdateOutcomeRecorder(data.MachineFolder, folders, log, time);

        // 1. The 1.2.x tray's update verb, exactly as it is started: update <zip> <sha> <trayPid> <sid> <address> <container>.
        var starter = new CapturingStarter();
        string[] updateArgs = [.. UpdateHandover.UpdateArguments(zipPath, ReleaseZipBuilder.Sha256Hex(zip), 4321, new HandoverIdentity(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer))];
        GateExitCode updated = Program.RunUpdate(updateArgs, FakeToken.ElevatedUser, log, request =>
        {
            InstallResult result = new UpdateActions(new InstallLayout(install, install, data.MachineFolder), folders, new Waiter(), starter, log)
            {
                TrayWait = TimeSpan.FromSeconds(3),
            }.Run(request);
            recorder.RecordUpdateRun(result);
            return result;
        }, []);
        Assert.AreEqual(GateExitCode.Success, updated);
        Assert.HasCount(1, starter.Starts, "The update verb started the new release's install.");
        string[] installArgs = starter.Starts[0].Arguments;
        CollectionAssert.AreEqual(
            new[] { "install", TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer.ToString("D") }, installArgs,
            "The install verb takes the three values a 1.2.x update verb already passes and nothing new.");

        // 2. The new release's install verb, a moment later, from the folder the update unpacked it to. No tray is running (the
        // update waited for it to end), and the tray starts at once when the task is run.
        time.Now = Start.AddSeconds(20);
        var probe = new FakeProbe();
        var runner = new FakeRunner(probe);
        var capturing = new CapturingRegistrar(tasks);
        var restarter = new TrayRestarter(probe, capturing, runner, log) { Wait = _ => true };
        string work = starter.Starts[0].WorkingDirectory;
        InstallResult? finished = null;
        GateExitCode installed = Program.RunInstall(installArgs, FakeToken.ElevatedUser, log, request =>
        {
            InstallResult result = RunInstall(work, request);
            finished = Program.CompleteInstall(result, recorder, "1.3.0", () => restarter.Restart(request.UserSid, install));
            return finished;
        }, []);

        Assert.AreEqual(GateExitCode.Success, installed);
        Assert.HasCount(1, runner.Runs, "Exactly one tray was started.");
        Assert.HasCount(1, capturing.Specs);
        Assert.AreEqual(TaskSchedulerCom.TASK_RUNLEVEL_LUA, capturing.Specs[0].Principal.RunLevel);
        Assert.AreEqual(TestUsers.Sid, capturing.Specs[0].Principal.UserId);
        Assert.AreEqual(UpdateOutcomeKind.Installed, new GateStore(data.MachineFolder).ReadUpdateOutcome().Value!.Kind, "The notice is recorded once, for the tray that starts.");
        Assert.HasCount(3, tasks.Tasks, "Gate, Protect and BootBlock stay; the start task is gone.");
        string[] stepNames = finished!.Steps.Select(s => s.Step).ToArray();
        Assert.IsGreaterThan(Array.IndexOf(stepNames, "task-verify:BootBlock"), Array.IndexOf(stepNames, TrayRestarter.Step),
            "The tray is started after the tasks are registered and checked.");
        Assert.IsTrue(finished.Steps[^1].Ok, Describe(finished.Steps));
    }

    // ----- the real parts, each executed once -----

    [TestMethod]
    public void TheRealProbeSeesAHeldSingleInstanceLockAndAReleasedOne()
    {
        string name = "Earshot.Tests.TrayProbe." + Guid.NewGuid().ToString("N");
        var probe = new MutexTrayInstanceProbe(name);

        Assert.AreEqual(TrayInstance.NotRunning, probe.Probe().State, "No lock yet.");
        using (var held = new Mutex(initiallyOwned: true, name, Program.TrayInstanceOptions, out bool createdNew))
        {
            Assert.IsTrue(createdNew);
            TrayProbe seen = probe.Probe();
            Assert.AreEqual(TrayInstance.Running, seen.State, Describe([seen.Step]));
            held.ReleaseMutex();
        }

        Assert.AreEqual(TrayInstance.NotRunning, probe.Probe().State, "Released with the tray.");
    }

    [TestMethod]
    [TestCategory("ReadOnlySystem")]
    public void TheServiceReadsBackTheStartTaskAsTheUsersInteractiveTokenAtTheLeastRunLevelWithNoTimeLimit()
    {
        string xml = "";
        MtaThread.Run(() =>
        {
            int hr = ComTaskScheduler.WithService(service =>
            {
                int created = service.NewTask(0, out ITaskDefinition? definition);
                Assert.AreEqual(0, created, "NewTask " + NativeCodes.Name(created));
                Assert.IsNotNull(definition);
                try
                {
                    var steps = new List<StepOutcome>();
                    int applied = TaskDefinitionWriter.Apply(definition, TaskPlan.TrayStartSpec(InstallFolder, TestUsers.Sid), steps);
                    Assert.AreEqual(0, applied, Describe(steps));
                    Assert.AreEqual(0, definition.get_XmlText(out string? text));
                    xml = text ?? "";
                }
                finally
                {
                    Marshal.ReleaseComObject(definition);
                }

                return 0;
            });
            Assert.AreEqual(0, hr, "Connect " + NativeCodes.Name(hr));
        }, TimeSpan.FromSeconds(60));

        TestContext.WriteLine(xml);
        StringAssert.Contains(xml, "<UserId>" + TestUsers.Sid + "</UserId>");
        StringAssert.Contains(xml, "<LogonType>InteractiveToken</LogonType>");
        StringAssert.Contains(xml, "<RunLevel>LeastPrivilege</RunLevel>");
        StringAssert.Contains(xml, "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        StringAssert.Contains(xml, "<Command>" + Path.Combine(InstallFolder, "Earshot.exe") + "</Command>");
        StringAssert.Contains(xml, "<Triggers />", "Started only on demand.");
        StringAssert.Contains(xml, "<Priority>4</Priority>", "Not the scheduler's default, which runs below normal.");
        Assert.DoesNotContain("HighestAvailable", xml, StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("ReadOnlySystem")]
    public void TheRealRunnerAsksTheServiceForATaskThatIsNotThereAndGetsItsCode()
    {
        int hr = 0;
        MtaThread.Run(() => hr = new ComTaskRunner().RunNow(@"\Earshot\NoSuchTaskForTheTests"), TimeSpan.FromSeconds(60));

        Assert.AreEqual(unchecked((int)0x80070002), hr, NativeCodes.Name(hr));
    }

    public TestContext TestContext { get; set; } = null!;
}
