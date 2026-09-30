using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Service;
using Earshot.Tests.Phase4;
using Earshot.Tests.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The elevated half of Repair Earshot, run for real against temporary folders with only the boundaries faked: the folder
// security (there is no elevated token here), Task Scheduler and the device nodes, as the install tests fake them. Repair
// is install's own repair run from the installed copy, so the install it repairs is made by the real install first.
// Nothing here touches Program Files, ProgramData, the real task namespace or a device node.
[TestClass]
public sealed class RepairTests
{
    private const string OwnersSettings =
        "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"AirPods\", \"PinnedAddress\": \"0A1B2C3D4E8C\", \"CheckForUpdatesAutomatically\": true, \"HandBackOnShutdownAndSleep\": false }";

    private sealed class World : IDisposable
    {
        private readonly TempFolder _temp = new();

        public World()
        {
            Data = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? _temp.File("data") : null);
            Install = _temp.File(Path.Combine("ProgramFiles", "Earshot"));
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Directory.CreateDirectory(Data.RoamingFolder);
            Directory.CreateDirectory(Data.LocalFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(Data.MachineFolder)!);
            Release = NewRelease("v1", "1.2.0");
            Assert.AreEqual(GateExitCode.Success, RunInstall(Release).Outcome, "The starting install.");
        }

        public Paths Data { get; }

        public string Install { get; }

        public string Release { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public FakeTaskRegistrar Tasks { get; } = new();

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public CapturingLog Log { get; } = new();

        public string NewRelease(string name, string version)
        {
            string folder = _temp.File(Path.Combine("unzip", name, "Earshot"));
            Directory.CreateDirectory(Path.Combine(folder, "runtimes"));
            File.WriteAllText(Path.Combine(folder, "Earshot.exe"), "exe " + version);
            File.WriteAllText(Path.Combine(folder, "Earshot.dll"), "dll " + version);
            File.WriteAllText(Path.Combine(folder, "runtimes", "native.txt"), "native " + version);
            string[] listed = ["Earshot.exe", "Earshot.dll", "runtimes/native.txt"];
            IEnumerable<string> entries = listed.Select(relative =>
                "    { \"Path\": \"" + relative + "\", \"Sha256\": \"" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, relative.Replace('/', '\\'))))) + "\" }");
            File.WriteAllText(Path.Combine(folder, FileManifest.FileName), "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n");
            return folder;
        }

        public InstallResult RunInstall(string source, IGateRunLock? runLock = null, TimeSpan? lockWait = null) =>
            new InstallActions(new InstallLayout(source, Install, Data.MachineFolder), Folders, Nodes, Tasks, NoLookup, Log)
            {
                RunLock = runLock ?? NoGateRunLock.Instance,
                LockWait = lockWait ?? InstallRunLock.WaitBeforeRefusing,
            }.Run(Request);

        // The repair the tray asks for: the installed copy runs it, so the running folder is the install folder.
        public InstallResult RunRepair(
            string? runningFrom = null, IFolderSecurity? folders = null, IGateRunLock? runLock = null, TimeSpan? lockWait = null, IServiceControl? service = null) =>
            new InstallActions(new InstallLayout(runningFrom ?? Install, Install, Data.MachineFolder), folders ?? Folders, Nodes, Tasks, NoLookup, Log, service: service)
            {
                RepairOnly = true,
                RunLock = runLock ?? NoGateRunLock.Instance,
                LockWait = lockWait ?? InstallRunLock.WaitBeforeRefusing,
                ServicePoll = _ => true,
            }.Run(Request);

        private static InstallRequest Request { get; } =
            new(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System);

        // The owner's own data: settings, the battery set-up (the widget's claim and its other records), logs, a live-test record.
        public void WriteOwnersData()
        {
            File.WriteAllText(Data.SettingsFile, OwnersSettings);
            Directory.CreateDirectory(Data.WidgetFolder);
            File.WriteAllText(Data.WidgetClaimFile, "{ \"claim\": \"invented battery set-up values\" }");
            File.WriteAllText(Path.Combine(Data.WidgetFolder, "setup-record.json"), "{ \"record\": \"invented proof\" }");
            Directory.CreateDirectory(Data.LogFolder);
            File.WriteAllText(Data.LogFile, "an earlier session\r\n");
            Directory.CreateDirectory(Data.LiveTestFolder);
            File.WriteAllText(Path.Combine(Data.LiveTestFolder, "evidence.json"), "{}");
        }

        public static SortedDictionary<string, string> Snapshot(params string[] roots)
        {
            var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string root in roots)
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    snapshot[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                }
            }

            return snapshot;
        }

        public SortedDictionary<string, string> OwnersDataSnapshot() => Snapshot(Data.RoamingFolder, Data.LocalFolder);

        public SortedDictionary<string, string> InstallSnapshot() => Snapshot(Install);

        public string InstalledFile(string relative) => Path.Combine(Install, relative.Replace('/', '\\'));

        private static AccountLookup NoLookup(string account) =>
            throw new AssertFailedException("No account name should be looked up: " + account);

        public void Dispose() => _temp.Dispose();
    }

    private static string Steps(InstallResult result) =>
        string.Join(Environment.NewLine, result.Steps.Select(GateActions.Describe));

    // ----- the repair itself -----

    [TestMethod]
    public void ARepairFromTheInstalledCopyChecksEveryFileRegistersEverythingAgainAndKeepsTheOwnersData()
    {
        using var w = new World();
        w.WriteOwnersData();
        Assert.IsTrue(new GateStore(w.Data.MachineFolder).WriteConfig(new GateConfig { BlockAtBoot = false }).Ok, "The owner turned Block at boot off.");
        GateStore store = new(w.Data.MachineFolder);
        string deviceBefore = File.ReadAllText(store.DeviceFile);
        SortedDictionary<string, string> dataBefore = w.OwnersDataSnapshot();
        SortedDictionary<string, string> installBefore = w.InstallSnapshot();
        w.Tasks.Tasks.Clear();
        w.Tasks.Calls.Clear();

        InstallResult result = w.RunRepair();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "verify-installed" && s.Ok), "Every installed file was checked against the published hashes: " + Steps(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "repair-running-from" && s.Ok));
        CollectionAssert.AreEqual(installBefore.ToArray(), w.InstallSnapshot().ToArray(), "Repair copies nothing: the install folder is what it was.");

        // The tasks are registered again, exactly as setup registers them.
        CollectionAssert.AreEquivalent(TaskPlan.TaskNames.Select(TaskPlan.TaskPath).ToArray(), w.Tasks.Tasks.Keys.ToArray());
        Assert.IsTrue(w.Tasks.Calls.Any(c => c.StartsWith("create-folder", StringComparison.Ordinal)), string.Join(", ", w.Tasks.Calls));

        // The settings, the battery set-up records, the logs and the pinned device are as they were.
        CollectionAssert.AreEqual(dataBefore.ToArray(), w.OwnersDataSnapshot().ToArray(), "Nothing under the roaming and local data folders changed.");
        var settings = new JsonSettingsStore(w.Data.SettingsFile, new CapturingLog());
        Assert.AreEqual(SettingsLoadStatus.Loaded, settings.LastLoadStatus);
        Assert.IsTrue(settings.Current.CheckForUpdatesAutomatically);
        Assert.AreEqual("0A1B2C3D4E8C", settings.Current.PinnedAddress);
        Assert.IsTrue(File.Exists(w.Data.WidgetClaimFile));
        Assert.AreEqual(deviceBefore, File.ReadAllText(store.DeviceFile), "The pinned device is the same.");
        Assert.IsFalse(store.ReadConfig().Value!.BlockAtBoot, "Block at boot stays as the owner set it.");
    }

    [TestMethod]
    public void ARepairRunFromAnyFolderButTheInstalledCopyIsRefusedBeforeAnythingIsCheckedOrChanged()
    {
        using var w = new World();
        SortedDictionary<string, string> installBefore = w.InstallSnapshot();
        w.Tasks.Tasks.Clear();
        w.Tasks.Calls.Clear();
        string elsewhere = w.NewRelease("v2", "9.9.9");

        InstallResult result = w.RunRepair(runningFrom: elsewhere);

        Assert.AreEqual(GateExitCode.NotFromInstallFolder, result.Outcome, Steps(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "repair-running-from" && !s.Ok));
        Assert.IsEmpty(w.Tasks.Tasks, "Nothing was registered.");
        Assert.IsEmpty(w.Tasks.Calls);
        CollectionAssert.AreEqual(installBefore.ToArray(), w.InstallSnapshot().ToArray(), "Nothing was copied in from the other folder.");
        Assert.AreEqual("exe 1.2.0", File.ReadAllText(w.InstalledFile("Earshot.exe")));
    }

    [TestMethod]
    public void AMissingOrChangedInstalledFileStopsTheRepairWithNothingChangedAndNamesTheFile()
    {
        foreach (string damage in new[] { "missing", "changed" })
        {
            using var w = new World();
            string dll = w.InstalledFile("Earshot.dll");
            if (damage == "missing")
            {
                File.Delete(dll);
            }
            else
            {
                File.WriteAllText(dll, "dll tampered");
            }

            w.Tasks.Tasks.Clear();
            w.Tasks.Calls.Clear();

            InstallResult result = w.RunRepair();

            Assert.AreEqual(GateExitCode.Failed, result.Outcome, damage + ": " + Steps(result));
            StepOutcome bad = result.Steps.Single(s => !s.Ok && s.Step.StartsWith("verify-installed", StringComparison.Ordinal));
            Assert.AreEqual("verify-installed:Earshot.dll", bad.Step, damage);
            Assert.IsEmpty(w.Tasks.Tasks, damage + ": nothing was registered on the word of files that do not match.");
            Assert.IsEmpty(w.Tasks.Calls);
        }
    }

    [TestMethod]
    public void AnInstallFolderAStandardUserCanWriteIsNotTrustedAndTheRepairRefuses()
    {
        using var w = new World();
        w.Tasks.Tasks.Clear();
        w.Tasks.Calls.Clear();
        w.Folders.SddlFor = path => path == w.Install ? "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;BU)" : null;

        InstallResult result = w.RunRepair();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Steps(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "install-folder-acl" && !s.Ok), Steps(result));
        Assert.IsEmpty(w.Tasks.Tasks);
    }

    [TestMethod]
    public void AnInstallWithNoPublishedFileListCannotBeRepairedInPlace()
    {
        using var w = new World();
        File.Delete(w.InstalledFile(FileManifest.FileName));
        w.Tasks.Tasks.Clear();

        InstallResult result = w.RunRepair();

        Assert.AreEqual(GateExitCode.NoManifest, result.Outcome, Steps(result));
        Assert.IsEmpty(w.Tasks.Tasks);
    }

    // ----- the service is left alone until the files have been checked -----

    // A repair registers the service again, which stops it first. It copies nothing, so nothing needs the service stopped
    // before the files are known to be good: a repair that finds one missing or different leaves the service running.
    [TestMethod]
    public void ARepairThatFindsAFileDamagedStopsBeforeTheServiceIsStopped()
    {
        foreach (string damage in new[] { "changed", "missing", "no-list" })
        {
            using var w = new World();
            var service = new FakeServiceControl();
            service.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(w.Install));
            if (damage == "changed")
            {
                File.WriteAllText(w.InstalledFile("Earshot.dll"), "dll tampered");
            }
            else if (damage == "missing")
            {
                File.Delete(w.InstalledFile("Earshot.dll"));
            }
            else
            {
                File.Delete(w.InstalledFile(FileManifest.FileName));
            }

            w.Tasks.Tasks.Clear();
            w.Tasks.Calls.Clear();

            InstallResult result = w.RunRepair(service: service);

            Assert.AreNotEqual(GateExitCode.Success, result.Outcome, damage);
            Assert.DoesNotContain("stop", service.Calls, damage + ": the service was stopped before the files were checked: " + string.Join(", ", service.Calls));
            Assert.AreEqual(AdvApi32.SERVICE_RUNNING, service.State, damage + ": and it is still running.");
            Assert.IsEmpty(w.Tasks.Calls, damage + ": nothing was changed.");
        }
    }

    [TestMethod]
    public void ARepairOfFilesThatAllMatchStillStopsAndRegistersTheServiceAgain()
    {
        using var w = new World();
        var service = new FakeServiceControl();
        service.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(w.Install));

        InstallResult result = w.RunRepair(service: service);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.Contains("stop", service.Calls);
        Assert.AreEqual(AdvApi32.SERVICE_RUNNING, service.State);
        Assert.IsLessThan(result.Steps.ToList().FindIndex(s => s.Step == ServiceSteps.Stop), result.Steps.ToList().FindIndex(s => s.Step == "verify-installed"), "The files were checked first.");
    }

    // ----- one setup, update or repair at a time on the machine -----

    private static string NewLockName() => @"Local\Earshot.Tests.InstallRunLock." + Guid.NewGuid().ToString("N");

    // The real lock class under a private name and an access list that lets this test's own user in, as the gate's tests do.
    private static MachineGateMutex TestLock(string name, Func<SecurityIdentifier?, bool>? trusted = null) =>
        new(name, UserAndMachineSecurity, trusted ?? (_ => true), InstallRunLock.StepName, "setup, update or repair");

    private static MutexSecurity UserAndMachineSecurity()
    {
        MutexSecurity security = MachineGateMutex.MachineSecurity();
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(me.User!, MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // Holds the lock on another thread, as another elevated process would, until the test lets go.
    private sealed class OtherRun : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public OtherRun(string name)
        {
            using var entered = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                var steps = new List<StepOutcome>();
                using IDisposable? held = TestLock(name).TryEnter(TimeSpan.FromSeconds(5), steps);
                Assert.IsNotNull(held, "The other run could not take the lock.");
                entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            });
            _thread.Start();
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "The other run never took the lock.");
        }

        public void LetGo()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            LetGo();
            _release.Dispose();
        }
    }

    [TestMethod]
    public void ARepairThatFindsTheLockHeldIsRefusedAndLoggedWithNothingChangedAndWorksOnceItIsFree()
    {
        using var w = new World();
        string name = NewLockName();
        using var other = new OtherRun(name);
        w.Tasks.Tasks.Clear();
        w.Tasks.Calls.Clear();

        InstallResult refused = w.RunRepair(runLock: TestLock(name), lockWait: TimeSpan.FromMilliseconds(150));

        Assert.AreEqual(GateExitCode.Busy, refused.Outcome, Steps(refused));
        StepOutcome step = refused.Steps.Single();
        Assert.AreEqual(InstallRunLock.StepName, step.Step);
        Assert.AreEqual((int)MachineGateMutex.WaitTimeout, step.Code, "The raw code is the wait that ran out.");
        Assert.IsTrue(InstallRunLock.IsBusy(step));
        Assert.IsEmpty(w.Tasks.Calls, "Nothing was read or changed.");
        Assert.IsTrue(w.Log.Has(LogLevel.Warn, "the machine-wide lock was not taken, so nothing was changed (another setup, update or repair holds it)"));

        other.LetGo();
        InstallResult allowed = w.RunRepair(runLock: TestLock(name), lockWait: TimeSpan.FromSeconds(5));

        Assert.AreEqual(GateExitCode.Success, allowed.Outcome, Steps(allowed));
    }

    // Two runs in progress at once: the second is refused while the first is part-way through its work, and the first is
    // not disturbed.
    [TestMethod]
    public void ARepairAndASetupStartedTogetherNeverWorkOnTheTasksAtOnce()
    {
        using var w = new World();
        string name = NewLockName();
        using var insideFirst = new ManualResetEventSlim();
        using var letFirstGo = new ManualResetEventSlim();
        w.Tasks.Tasks.Clear();
        w.Tasks.Calls.Clear();
        w.Tasks.OnList = () =>
        {
            insideFirst.Set();
            Assert.IsTrue(letFirstGo.Wait(TimeSpan.FromSeconds(30)));
        };
        InstallResult? first = null;
        var thread = new Thread(() => first = w.RunRepair(runLock: TestLock(name), lockWait: TimeSpan.FromSeconds(5)));
        thread.Start();
        Assert.IsTrue(insideFirst.Wait(TimeSpan.FromSeconds(10)), "The first run never reached the tasks.");
        string[] whileHeld = w.Tasks.Calls.ToArray();

        InstallResult second = w.RunInstall(w.Release, TestLock(name), TimeSpan.FromMilliseconds(150));

        Assert.AreEqual(GateExitCode.Busy, second.Outcome, Steps(second));
        CollectionAssert.AreEqual(whileHeld, w.Tasks.Calls.ToArray(), "The second run did not touch the tasks while the first had them.");
        letFirstGo.Set();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.AreEqual(GateExitCode.Success, first!.Outcome, Steps(first));
    }

    // The install the update starts is a separate run that begins as the update run ends: it waits for the lock rather than
    // refusing.
    [TestMethod]
    public void AnInstallThatWaitsForTheRunBeforeItGoesOnInsteadOfRefusing()
    {
        using var w = new World();
        string name = NewLockName();
        using var other = new OtherRun(name);
        using var timer = new System.Threading.Timer(_ => other.LetGo(), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);

        InstallResult result = w.RunInstall(w.Release, TestLock(name), TimeSpan.FromSeconds(10));

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
    }

    // A mutex someone else made under the name is not a lock that is held: it is a failure, said as one, and nothing is changed.
    [TestMethod]
    public void ALockNameTakenByAnotherOwnerIsAFailureNotABusyAndNothingIsChanged()
    {
        using var w = new World();
        string name = NewLockName();
        using var squatter = new Mutex(false, name);
        w.Tasks.Tasks.Clear();
        w.Tasks.Calls.Clear();

        InstallResult result = w.RunRepair(runLock: TestLock(name, trusted: _ => false), lockWait: TimeSpan.FromMilliseconds(150));

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Steps(result));
        Assert.IsFalse(InstallRunLock.IsBusy(result.Steps.Single()));
        StringAssert.Contains(result.Steps.Single().Detail, "not trusted");
        Assert.IsEmpty(w.Tasks.Calls);
    }

    [TestMethod]
    public void TheRealLockIsTheMachineWideOneUnderItsOwnNameAndTheTrayTellsWhyNothingStarted()
    {
        Assert.IsInstanceOfType<MachineGateMutex>(InstallRunLock.Create());
        Assert.AreEqual("busy", GateExitCodes.ResultName(GateExitCode.Busy));
        StringAssert.Contains(UpdateOutcomes.Describe(new InstallResult(GateExitCode.Busy, [new StepOutcome(InstallRunLock.StepName, false, (int)MachineGateMutex.WaitTimeout, "WAIT_TIMEOUT", "held")])).Reason, "another setup, update or repair");
    }

    // ----- the command line -----

    private static readonly string[] Good = ["repair", TestUsers.Sid, RecordedNodes.AirPodsAddress, "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13"];

    [TestMethod]
    public void TheRepairCommandLineIsMatchedExactly()
    {
        Assert.IsTrue(Program.TryParseRepairArgs(Good, out InstallRequest? request, out string? problem), problem);
        Assert.AreEqual(TestUsers.Sid, request!.UserSid);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, request.Address);
        Assert.AreEqual(TaskPrincipalMode.System, request.Principal, "The tasks are always registered for SYSTEM.");

        Assert.IsFalse(Program.TryParseRepairArgs(["repair"], out _, out _));
        Assert.IsFalse(Program.TryParseRepairArgs([.. Good, "--principal", "user"], out _, out _), "No fall-back principal flag on a repair.");
        Assert.IsFalse(Program.TryParseRepairArgs(["install", .. Good[1..]], out _, out _), "It is the repair verb, not install's.");
        Assert.IsFalse(Program.TryParseRepairArgs(["repair", "not a sid", Good[2], Good[3]], out _, out _));
        Assert.IsFalse(Program.TryParseRepairArgs(["repair", Good[1], "0a1b2c3d4e8c", Good[3]], out _, out _), "The address is upper-case hex.");
        Assert.IsFalse(Program.TryParseRepairArgs(["repair", Good[1], Good[2], "not a guid"], out _, out _));
    }

    [TestMethod]
    public void RepairRefusesUnlessElevatedAndOnABadCommandLineAndOnAnUnsafeEnvironment()
    {
        var log = new CapturingLog();
        bool ran = false;
        Func<InstallRequest, InstallResult> run = _ =>
        {
            ran = true;
            return new InstallResult(GateExitCode.Success, []);
        };

        GateExitCode notElevated = Program.RunRepair(Good, FakeToken.PlainUser, log, run, []);
        GateExitCode rejected = Program.RunRepair(["repair", "x"], FakeToken.ElevatedUser, log, run, []);
        GateExitCode unsafeEnvironment = Program.RunRepair(Good, FakeToken.ElevatedUser, log, run, ["DOTNET_STARTUP_HOOKS"]);

        Assert.AreEqual(GateExitCode.NotElevated, notElevated);
        Assert.AreEqual(GateExitCode.Rejected, rejected);
        Assert.AreEqual(GateExitCode.UnsafeEnvironment, unsafeEnvironment);
        Assert.IsFalse(ran, "Nothing ran for any of the three.");

        GateExitCode success = Program.RunRepair(Good, FakeToken.ElevatedUser, log, run, []);

        Assert.AreEqual(GateExitCode.Success, success);
        Assert.IsTrue(ran);
        Assert.IsTrue(log.Has(LogLevel.Info, "repair: success (0)."), "The result is logged with its steps.");
    }

    [TestMethod]
    public void TheRepairVerbIsAPrivilegedModeRefusedInSafeModeAndOnATestDataRoot()
    {
        Assert.IsTrue(Program.PrivilegedModes.Contains("repair"));
        using var temp = new TempFolder();
        var log = new CapturingLog();

        int safe = Program.Dispatch(Good, Paths.FromEnvironment(name => name switch { Paths.DataRootVariable => temp.Path, Paths.SafeModeVariable => "1", _ => null }), log);
        int redirected = Program.Dispatch(Good, Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null), log);

        Assert.AreEqual(ExitCodes.Refused, safe);
        Assert.AreEqual(ExitCodes.Refused, redirected);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Refused: repair"));
    }

    // The built Earshot.exe, started as a real process with the test switches set, refuses the repair verb before it reads
    // anything or asks for anything: the dispatch is the real one, end to end. (Started without the switch it would be
    // refused for not being elevated, but that run would also try to write the machine log, so it is not run here.)
    [TestMethod]
    public void TheBuiltProgramRefusesTheRepairVerbInSafeModeAsARealProcess()
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Earshot.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in Good)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment.Remove(Paths.DataRootVariable);
        info.Environment[Paths.SafeModeVariable] = "1";

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(30)), "Earshot.exe repair did not end.");

        Assert.AreEqual(ExitCodes.Refused, process.ExitCode, output.Result + error.Result);
    }

    // ----- how it ended is recorded, for the tray to say -----

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [TestMethod]
    public void ARepairThatFinishedRecordsRepairedAndOneThatDidNotRecordsWhyAndTheRawCode()
    {
        using var temp = new TempFolder();
        string folder = temp.File("Earshot");
        Directory.CreateDirectory(folder);
        var folders = new FakeFolderSecurity { SddlFor = _ => Sddl.MachineFolder };
        var log = new CapturingLog();
        var recorder = new UpdateOutcomeRecorder(folder, folders, log, new FixedTime(Now));
        var store = new GateStore(folder);

        recorder.RecordRepairRun(new InstallResult(GateExitCode.Success, []), "1.2.0");
        UpdateOutcome repaired = store.ReadUpdateOutcome().Value!;
        Assert.AreEqual(UpdateOutcomeKind.Repaired, repaired.Kind);
        Assert.AreEqual("1.2.0", repaired.Version);
        Assert.AreEqual("Earshot was repaired.", UpdateOutcomes.NoticeFor(repaired, Now));

        recorder.RecordRepairRun(
            new InstallResult(GateExitCode.Failed,
            [
                StepOutcomes.NotAttempted("copy-app", InstallActions.NothingToCopyDetail),
                new StepOutcome("verify-installed:Earshot.dll", false, -1, "NOT_ATTEMPTED", @"C:\Program Files\Earshot\Earshot.dll"),
            ]), "1.2.0");
        UpdateOutcome failed = store.ReadUpdateOutcome().Value!;
        Assert.AreEqual(UpdateOutcomeKind.RepairFailed, failed.Kind);
        Assert.AreEqual("an installed file is missing or is not what was published", failed.Reason);
        Assert.AreEqual("NOT_ATTEMPTED", failed.Code);
        Assert.DoesNotContain("Program Files", failed.Reason, "A step's own detail holds paths and never reaches the owner.");
        Assert.AreNotEqual(repaired.Id, failed.Id, "A new Id, so the tray shows it after the earlier one.");
        string notice = UpdateOutcomes.NoticeFor(failed, Now)!;
        Assert.AreEqual("The repair did not finish: an installed file is missing or is not what was published. Choose Repair Earshot from its menu to try again.", notice);
    }

    [TestMethod]
    public void ARepairOutcomeIsNotRecordedWhenTheMachineFolderIsNotTrusted()
    {
        using var temp = new TempFolder();
        string folder = temp.File("Earshot");
        Directory.CreateDirectory(folder);
        var folders = new FakeFolderSecurity { SddlFor = _ => "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;BU)" };
        var log = new CapturingLog();

        new UpdateOutcomeRecorder(folder, folders, log, new FixedTime(Now)).RecordRepairRun(new InstallResult(GateExitCode.Success, []), "1.2.0");

        Assert.AreEqual(GateReadStatus.Missing, new GateStore(folder).ReadUpdateOutcome().Status);
        Assert.IsTrue(log.Has(LogLevel.Warn, "update outcome: not recorded"));
    }
}
