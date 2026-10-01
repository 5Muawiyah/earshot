using System.Diagnostics;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// install-zip: the first install from a checked download. Parsing and refusals run the real functions; the steps run
// for real against temporary folders with only the boundaries faked (folder security, the install it starts and the
// machine-wide lock), and the runner that starts the install has an execution of its own against cmd.exe, because a
// fake at a boundary proves everything except the boundary. Nothing here touches Program Files, ProgramData, Task
// Scheduler, a service or a device, and no test can reach a real install: the verb is refused under safe mode and
// a redirected data root, and the gate sets both.
[TestClass]
public sealed class InstallZipTests
{
    private const string Container = "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13";

    private static readonly string CmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static readonly HandoverIdentity Identity = new(TestUsers.Sid, RecordedNodes.AirPodsAddress, new Guid(Container));

    private const string ZipInUserFolder = @"C:\Users\A Person\AppData\Local\Temp\earshot-1a2b3c4d\update.zip";

    // Records whether it is held, so a test can see the lock was let go before the install ran.
    private sealed class RecordingLock : IGateRunLock
    {
        public bool Held { get; private set; }

        public int Entered { get; private set; }

        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            Entered++;
            Held = true;
            return new Release(this);
        }

        private sealed class Release(RecordingLock owner) : IDisposable
        {
            public void Dispose() => owner.Held = false;
        }
    }

    private sealed class FakeRunner : IInstallRunner
    {
        public List<(string Executable, string[] Arguments, string WorkingDirectory, TimeSpan Timeout)> Runs { get; } = new();

        public Func<StepOutcome>? OnRun { get; set; }

        public int ExitCode { get; set; }

        public Action<string>? During { get; set; }

        // Called as the process starts, before the caller is told it has: what the lock is at that moment.
        public Action? AtStart { get; set; }

        public StepOutcome RunAndWait(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, Action? afterStart, out int exitCode)
        {
            Runs.Add((executable, arguments.ToArray(), workingDirectory, timeout));
            AtStart?.Invoke();
            afterStart?.Invoke();
            During?.Invoke(executable);
            exitCode = ExitCode;
            return OnRun?.Invoke() ?? (ExitCode == 0
                ? new StepOutcome(ChildInstallRunner.Step, true, 0, "S_OK", "ended")
                : StepOutcomes.NotAttempted(ChildInstallRunner.Step, "ended with " + ExitCode));
        }
    }

    private sealed class NeverWaits : IProcessWaiter
    {
        public StepOutcome WaitForExit(int processId, string image, TimeSpan timeout) =>
            throw new AssertFailedException("A first install has no tray to wait for.");
    }

    private sealed class NeverStarts : IInstallStarter
    {
        public StepOutcome Start(string executable, IReadOnlyList<string> arguments, string workingDirectory) =>
            throw new AssertFailedException("A first install runs its install through the runner, which waits.");
    }

    private sealed class World : IDisposable
    {
        private readonly TempFolder _temp = new();

        public World()
        {
            Data = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? _temp.File("data") : null);
            Install = _temp.File(Path.Combine("ProgramFiles", "Earshot"));
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Staging = _temp.File(Path.Combine("user", "earshot-1a2b3c4d"));
            Directory.CreateDirectory(Staging);
        }

        public Paths Data { get; }

        public string Install { get; }

        public string Staging { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public CapturingLog Log { get; } = new();

        public FakeRunner Runner { get; } = new();

        public RecordingLock Lock { get; } = new();

        public string ZipPath => Path.Combine(Staging, UpdateService.ZipFileName);

        public UpdateRequest Stage(byte[] zip, string? hash = null)
        {
            File.WriteAllBytes(ZipPath, zip);
            return new UpdateRequest(ZipPath, hash ?? ReleaseZipBuilder.Sha256Hex(zip), 0,
                new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System));
        }

        // What a person's PC has when something is installed and usable: the program, in a folder the default permissions allow.
        public void MakeUsableInstall()
        {
            Directory.CreateDirectory(Install);
            File.WriteAllText(Path.Combine(Install, "Earshot.exe"), "installed");
        }

        // A folder with no program in it: unusable, and where a first install may set up again.
        public void MakeProgramMissingInstall() => Directory.CreateDirectory(Install);

        public UpdateActions Actions(IGateRunLock? runLock = null, TimeSpan? lockWait = null) =>
            new(new InstallLayout(_temp.File(Path.Combine("download", "app", "Earshot")), Install, Data.MachineFolder), Folders, new NeverWaits(), new NeverStarts(), Log)
            {
                RunLock = runLock ?? Lock,
                LockWait = lockWait ?? TimeSpan.FromMilliseconds(50),
                InstallRunner = Runner,
            };

        public string[] WorkFolders() =>
            Directory.GetDirectories(Path.GetDirectoryName(Install)!, "Earshot" + UpdateActions.WorkFolderTag + "*");

        public string[] InstallSiblings() =>
            Directory.GetDirectories(Path.GetDirectoryName(Install)!).Where(d => Path.GetFileName(d) != "Earshot").ToArray();

        public void Dispose() => _temp.Dispose();
    }

    private static byte[] Release(string version = "1.3.0") => new ReleaseZipBuilder
    {
        Files =
        {
            ["Earshot.exe"] = System.Text.Encoding.ASCII.GetBytes("exe " + version),
            ["Earshot.dll"] = System.Text.Encoding.ASCII.GetBytes("dll " + version),
        },
    }.Build();

    private static string Steps(InstallResult result) =>
        string.Join(Environment.NewLine, result.Steps.Select(GateActions.Describe));

    // ----- the command line -----

    [TestMethod]
    public void TheVerbTakesExactlyTheZipItsHashAndTheIdentityInstallTakes()
    {
        string hash = new('a', 64);
        string[] args = ["install-zip", @"C:\Users\x\Temp\earshot-aabbccdd\update.zip", hash, TestUsers.Sid, "0A1B2C3D4E8C", Container];

        Assert.IsTrue(Program.TryParseInstallZipArgs(args, out UpdateRequest? request, out string? problem), problem);

        Assert.AreEqual(@"C:\Users\x\Temp\earshot-aabbccdd\update.zip", request.ZipPath);
        Assert.AreEqual(hash.ToUpperInvariant(), request.ZipSha256, "The hash is kept in the one form the elevated copy compares.");
        Assert.AreEqual(TestUsers.Sid, request.Install.UserSid);
        Assert.AreEqual("0A1B2C3D4E8C", request.Install.Address);
        Assert.AreEqual(new Guid(Container), request.Install.ContainerId);
        Assert.AreEqual(TaskPrincipalMode.System, request.Install.Principal);
        Assert.AreEqual(0, request.TrayProcessId, "There is no tray to wait for.");
    }

    [TestMethod]
    [DataRow("install-zip")]
    [DataRow("update", @"C:\x\update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"C:\x\update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"\\server\share\update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"\\?\C:\x\update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"C:\x\..\y\update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"C:\x\other.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"C:\x\update.zip", "not-a-hash", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"C:\x\update.zip", "H", "S-1-5-18", "0A1B2C3D4E8C", Container)]
    [DataRow("install-zip", @"C:\x\update.zip", "H", TestUsers.Sid, "0a1b2c3d4e8c", Container)]
    [DataRow("install-zip", @"C:\x\update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", "00000000-0000-0000-0000-000000000000")]
    [DataRow("install-zip", @"C:\x\update.zip", "H", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal", "user")]
    public void TheVerbRejectsAnythingElse(params string[] args)
    {
        string[] arguments = args.Select(a => a == "H" ? new string('C', 64) : a).ToArray();

        Assert.IsFalse(Program.TryParseInstallZipArgs(arguments, out UpdateRequest? request, out string? problem));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void TheVerbRefusesSystemUnelevatedAndUnsafeEnvironmentsAndRunsNothingThen()
    {
        string[] arguments = ["install-zip", ZipInUserFolder, new string('a', 64), TestUsers.Sid, "0A1B2C3D4E8C", Container];
        var log = new CapturingLog();
        Func<UpdateRequest, InstallResult> never = _ => throw new AssertFailedException("A refused first install must not run.");

        Assert.AreEqual(GateExitCode.RunningAsSystem, Program.RunInstallZip(arguments, FakeToken.System, log, never, []));
        Assert.AreEqual(GateExitCode.NotElevated, Program.RunInstallZip(arguments, FakeToken.PlainUser, log, never, []));
        Assert.AreEqual(GateExitCode.Rejected, Program.RunInstallZip(["install-zip"], FakeToken.ElevatedUser, log, never, []));
        Assert.AreEqual(GateExitCode.UnsafeEnvironment, Program.RunInstallZip(arguments, FakeToken.ElevatedUser, log, never, ["DOTNET_STARTUP_HOOKS"]));

        UpdateRequest? seen = null;
        GateExitCode ran = Program.RunInstallZip(arguments, FakeToken.ElevatedUser, log, request =>
        {
            seen = request;
            return new InstallResult(GateExitCode.Success, [new StepOutcome("install-zip-step", true, 0, "S_OK", "done")]);
        }, []);

        Assert.AreEqual(GateExitCode.Success, ran);
        Assert.AreEqual(ZipInUserFolder, seen!.ZipPath);
        Assert.IsTrue(log.Has(LogLevel.Info, "install-zip: success (0)."), "The result is logged with the steps.");
    }

    [TestMethod]
    public void TheVerbIsAPrivilegedModeRefusedInSafeModeAndOnATestDataRootSoNoTestReachesARealInstall()
    {
        Assert.IsTrue(Program.PrivilegedModes.Contains("install-zip"));
        using var temp = new TempFolder();
        var log = new CapturingLog();
        string[] arguments = ["install-zip", ZipInUserFolder, new string('a', 64), TestUsers.Sid, "0A1B2C3D4E8C", Container];

        int safe = Program.Dispatch(arguments, Paths.FromEnvironment(name => name switch { Paths.DataRootVariable => temp.Path, Paths.SafeModeVariable => "1", _ => null }), log);
        int redirected = Program.Dispatch(arguments, Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null), log);

        Assert.AreEqual(ExitCodes.Refused, safe);
        Assert.AreEqual(ExitCodes.Refused, redirected);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Refused: install-zip"));
    }

    // ----- the steps -----

    [TestMethod]
    public void WithNothingInstalledTheZipIsCheckedUnpackedAndTheReleasesOwnInstallRunsFromAnAdministratorsOnlyFolder()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release());
        string? workDuringInstall = null;
        bool? lockHeldDuringInstall = null;
        bool? lockHeldAtStart = null;
        w.Runner.AtStart = () => lockHeldAtStart = w.Lock.Held;
        w.Runner.During = exe =>
        {
            workDuringInstall = Path.GetDirectoryName(Path.GetDirectoryName(exe));
            lockHeldDuringInstall = w.Lock.Held;
            Assert.IsTrue(File.Exists(exe), "The program the install runs is the unpacked release's own.");
            Assert.AreEqual("exe 1.3.0", File.ReadAllText(exe));
        };

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.HasCount(1, w.Runner.Runs);
        (string exe, string[] arguments, string cwd, TimeSpan timeout) = w.Runner.Runs[0];
        Assert.AreEqual("Earshot.exe", Path.GetFileName(exe));
        Assert.AreEqual(cwd, Path.GetDirectoryName(exe), "It runs from its own folder.");
        CollectionAssert.AreEqual(UpdateHandover.InstallArguments(Identity).ToArray(), arguments);
        Assert.IsTrue(Program.TryParseInstallArgs(arguments, out InstallRequest? parsed, out string? problem), problem);
        Assert.AreEqual(TaskPrincipalMode.System, parsed.Principal);
        Assert.AreEqual(UpdateActions.WorkFolderSddl, w.Folders.CreatedWith[workDuringInstall!], "The work folder is the administrators-only one.");
        Assert.IsTrue(lockHeldAtStart, "The lock is still held while the install starts, so no other run can remove the work folder before the program in it has started.");
        Assert.IsFalse(lockHeldDuringInstall, "The install takes the machine-wide lock itself, so this run lets it go once the install has started.");
        Assert.AreEqual(1, w.Lock.Entered);
        Assert.AreEqual(TimeSpan.FromMinutes(5), timeout);
        Assert.IsEmpty(w.WorkFolders(), "The work folder is removed once the install has ended.");
        Assert.IsFalse(Directory.Exists(w.Install), "This run installs nothing itself.");
    }

    [TestMethod]
    public void TheExitCodeOfTheInstallIsTheOutcome()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release());
        w.Runner.ExitCode = (int)GateExitCode.Partial;

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Partial, result.Outcome, "Partial is what the install said, and it is not made to look like a pass.");
        Assert.IsEmpty(w.WorkFolders(), "A finished install, however it ended, leaves nothing behind.");
    }

    [TestMethod]
    public void AUsableInstallIsNeverReplacedFromADownload()
    {
        using var w = new World();
        w.MakeUsableInstall();
        UpdateRequest request = w.Stage(Release());

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(UpdateActions.AlreadyInstalled, result.Outcome, Steps(result));
        Assert.AreEqual((GateExitCode)26, result.Outcome, "The code the script reads as already installed.");
        Assert.IsEmpty(w.Runner.Runs);
        Assert.IsEmpty(w.InstallSiblings(), "Nothing was created beside the install folder.");
        Assert.AreEqual("installed", File.ReadAllText(Path.Combine(w.Install, "Earshot.exe")));
        StringAssert.Contains(Steps(result), "Earshot is already installed. Use Update or Repair.");
    }

    [TestMethod]
    public void AnInstallWhoseProgramIsMissingCanBeSetUpAgainFromTheDownload()
    {
        using var w = new World();
        w.MakeProgramMissingInstall();
        UpdateRequest request = w.Stage(Release());

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.HasCount(1, w.Runner.Runs);
    }

    [TestMethod]
    public void AnInstallFolderOthersCanWriteToCanBeSetUpAgainFromTheDownload()
    {
        using var w = new World();
        w.MakeUsableInstall();
        w.Folders.SddlFor = path => string.Equals(path, w.Install, StringComparison.OrdinalIgnoreCase)
            ? "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1301bf;;;BU)"
            : null;
        UpdateRequest request = w.Stage(Release());

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.HasCount(1, w.Runner.Runs, "A folder that is not trusted is not a usable install, and setup replaces it.");
    }

    [TestMethod]
    public void AZipThatDoesNotMatchTheHashOnTheCommandLineInstallsNothing()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release(), hash: new string('A', 64));

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Steps(result));
        StringAssert.Contains(Steps(result), "so nothing was installed");
        Assert.IsEmpty(w.Runner.Runs, "Nothing runs from a copy that did not match.");
        Assert.IsEmpty(w.WorkFolders());
    }

    [TestMethod]
    public void AZipThatSwapsAfterTheCommandLineWasFixedFailsTheHashOfTheCopy()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release("1.3.0"));
        File.WriteAllBytes(w.ZipPath, Release("9.9.9"));

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Steps(result));
        Assert.IsEmpty(w.Runner.Runs);
    }

    [TestMethod]
    public void AReleaseWithAFileItsListDoesNotNameInstallsNothing()
    {
        using var w = new World();
        var builder = new ReleaseZipBuilder { Files = { ["extra.dll"] = [1, 2, 3] } };
        builder.Unlisted.Add("extra.dll");
        UpdateRequest request = w.Stage(builder.Build());

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Steps(result));
        Assert.IsEmpty(w.Runner.Runs);
        Assert.IsEmpty(w.WorkFolders());
    }

    [TestMethod]
    public void AnotherSetupHoldingTheLockStopsItBeforeAnythingIsCreated()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release());
        InstallResult result = w.Actions(runLock: new RefusingLock()).RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Steps(result));
        Assert.IsEmpty(w.Runner.Runs);
        Assert.IsEmpty(w.InstallSiblings());
    }

    private sealed class RefusingLock : IGateRunLock
    {
        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            steps.Add(StepOutcomes.NotAttempted(InstallRunLock.StepName, "held elsewhere"));
            return null;
        }
    }

    [TestMethod]
    public void AnInstallThatNeverReportsAnExitCodeLeavesItsWorkFolderAndFails()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release());
        w.Runner.ExitCode = IInstallRunner.DidNotStart;
        w.Runner.OnRun = () => StepOutcomes.NotAttempted(ChildInstallRunner.Step, "did not start");

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.HasCount(1, w.WorkFolders(), "The work folder is left where it is.");
    }

    [TestMethod]
    public void AnInstallStillRunningWhenTheWaitRunsOutEndsWithItsOwnCodeAndLeavesItsWorkFolder()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(Release());
        w.Runner.ExitCode = IInstallRunner.StillRunning;
        w.Runner.OnRun = () => StepOutcomes.NotAttempted(ChildInstallRunner.Step, "still running");

        InstallResult result = w.Actions().RunFirstInstall(request);

        Assert.AreEqual(UpdateActions.StillRunning, result.Outcome, "Not the code of a failed install: it has not failed.");
        Assert.AreEqual(27, (int)result.Outcome);
        Assert.HasCount(1, w.WorkFolders(), "The install may still be running from it, so it is not removed.");
    }

    [TestMethod]
    public void TheUpdatePathStillRunsOnlyFromTheInstalledCopyAndStillWaitsForTheTray()
    {
        using var w = new World();
        w.MakeUsableInstall();
        UpdateRequest request = w.Stage(Release());

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.NotFromInstallFolder, result.Outcome, "update is unchanged by the first-install mode.");
        Assert.IsEmpty(w.Runner.Runs);
    }

    // ----- the runner, for real -----

    [TestMethod]
    public void TheRealRunnerReturnsTheExitCodeOfAProgramItStarted()
    {
        var runner = new ChildInstallRunner();

        int started = 0;
        StepOutcome ok = runner.RunAndWait(CmdExe, ["/c", "exit 0"], Environment.SystemDirectory, TimeSpan.FromSeconds(30), () => started++, out int okCode);
        StepOutcome bad = runner.RunAndWait(CmdExe, ["/c", "exit 3"], Environment.SystemDirectory, TimeSpan.FromSeconds(30), null, out int badCode);

        Assert.IsTrue(ok.Ok, ok.Detail);
        Assert.AreEqual(1, started, "The caller is told the program has started.");
        Assert.AreEqual(0, okCode);
        Assert.IsFalse(bad.Ok);
        Assert.AreEqual(3, badCode);
        StringAssert.Contains(bad.Detail, "failed");
    }

    [TestMethod]
    public void TheRealRunnerSaysSoWhenTheWaitRunsOutAndWhenTheProgramIsNotThere()
    {
        var runner = new ChildInstallRunner();

        int started = 0;
        StepOutcome slow = runner.RunAndWait(CmdExe, ["/c", "ping -n 4 127.0.0.1 >nul"], Environment.SystemDirectory, TimeSpan.FromMilliseconds(200), () => started++, out int slowCode);
        int startedMissing = 0;
        StepOutcome missing = runner.RunAndWait(Path.Combine(Path.GetTempPath(), "earshot-tests", "no-such-program.exe"), [], Path.GetTempPath(), TimeSpan.FromSeconds(5), () => startedMissing++, out int missingCode);

        Assert.IsFalse(slow.Ok);
        Assert.AreEqual(IInstallRunner.StillRunning, slowCode, "Still running is told apart from not started.");
        Assert.AreEqual(1, started);
        StringAssert.Contains(slow.Detail, "left to finish");
        Assert.IsFalse(missing.Ok);
        Assert.AreEqual(IInstallRunner.DidNotStart, missingCode);
        Assert.AreEqual(0, startedMissing, "Nothing started, so the caller is not told it did.");
    }
}
