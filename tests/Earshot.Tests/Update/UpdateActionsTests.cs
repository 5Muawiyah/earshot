using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The elevated half of an update, run for real against temporary folders with only the boundaries faked: the folder
// security (there is no elevated token here), the wait for the tray and the start of the install program. Every
// helper that can run without elevation also has an execution of its own below (the real wait, the real start, the
// real folder security in a temporary tree, a real junction), because a fake at a boundary proves everything except
// the boundary. Nothing here touches Program Files, ProgramData, Task Scheduler, a service or a device.
[TestClass]
public sealed class UpdateActionsTests
{
    private static readonly string CmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static readonly string[] WaitThenStart = ["wait", "start"];

    private sealed class FakeWaiter : IProcessWaiter
    {
        public List<(int ProcessId, string Image, TimeSpan Timeout)> Waits { get; } = new();

        public Func<StepOutcome> Answer { get; set; } = () => new StepOutcome(ProcessExitWaiter.Step, true, 0, "S_OK", "ended");

        public Action? During { get; set; }

        public StepOutcome WaitForExit(int processId, string image, TimeSpan timeout)
        {
            Waits.Add((processId, image, timeout));
            During?.Invoke();
            return Answer();
        }
    }

    private sealed class FakeStarter : IInstallStarter
    {
        public List<(string Executable, string[] Arguments, string WorkingDirectory)> Starts { get; } = new();

        public Func<string, IReadOnlyList<string>, string, StepOutcome>? OnStart { get; set; }

        public StepOutcome Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Starts.Add((executable, arguments.ToArray(), workingDirectory));
            return OnStart?.Invoke(executable, arguments, workingDirectory) ?? new StepOutcome(ChildInstallStarter.Step, true, 0, "S_OK", "started");
        }
    }

    private sealed class World : IDisposable
    {
        private readonly TempFolder _temp = new();

        public World()
        {
            Data = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? _temp.File("data") : null);
            Install = _temp.File(Path.Combine("ProgramFiles", "Earshot"));
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Data.MachineFolder)!);
            Staging = _temp.File(Path.Combine("user", "update", "u1"));
            Directory.CreateDirectory(Staging);
            ReleaseZipBuilder release = ReleaseFiles("1.1.0");
            Assert.AreEqual(GateExitCode.Success, RunInstall(UnzipTo("v1", release)).Outcome, "The starting install.");
        }

        public Paths Data { get; }

        public string Install { get; }

        public string Staging { get; }

        public string Root => _temp.Path;

        public FakeFolderSecurity Folders { get; } = new();

        public FakeTaskRegistrar Tasks { get; } = new();

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public CapturingLog Log { get; } = new();

        public FakeWaiter Waiter { get; } = new();

        public FakeStarter Starter { get; } = new();

        public string ZipPath => Path.Combine(Staging, UpdateService.ZipFileName);

        // A release's files, each carrying its version so a test can tell which release is installed.
        public static ReleaseZipBuilder ReleaseFiles(string version, params string[] extra)
        {
            var release = new ReleaseZipBuilder();
            release.Files["Earshot.exe"] = System.Text.Encoding.ASCII.GetBytes("exe " + version);
            release.Files["Earshot.dll"] = System.Text.Encoding.ASCII.GetBytes("dll " + version);
            release.Files["runtimes/native.txt"] = System.Text.Encoding.ASCII.GetBytes("native " + version);
            foreach (string name in extra)
            {
                release.Files[name] = System.Text.Encoding.ASCII.GetBytes(name + " " + version);
            }

            return release;
        }

        // The release as a folder, the way the very first setup runs from an unzipped one.
        public string UnzipTo(string name, ReleaseZipBuilder release)
        {
            string folder = _temp.File(Path.Combine("unzip", name));
            System.IO.Compression.ZipFile.ExtractToDirectory(new MemoryStream(release.Build()), folder);
            return Path.Combine(folder, "Earshot");
        }

        public InstallResult RunInstall(string source) =>
            new InstallActions(new InstallLayout(source, Install, Data.MachineFolder), Folders, Nodes, Tasks, NoLookup, Log)
                .Run(new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System));

        // Puts the zip where the tray leaves it, and returns the request the tray's hand-over would make for it.
        public UpdateRequest Stage(byte[] zip, string? hash = null)
        {
            File.WriteAllBytes(ZipPath, zip);
            return new UpdateRequest(ZipPath, hash ?? ReleaseZipBuilder.Sha256Hex(zip), 4321,
                new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System));
        }

        public UpdateActions Actions(string? runningFrom = null, IFolderSecurity? folders = null) =>
            new(new InstallLayout(runningFrom ?? Install, Install, Data.MachineFolder), folders ?? Folders, Waiter, Starter, Log)
            {
                TrayWait = TimeSpan.FromSeconds(3),
            };

        // What the started install program does: the install verb, run from the folder it was started in.
        public void StarterRunsTheRealInstall(List<InstallResult> results) =>
            Starter.OnStart = (exe, arguments, cwd) =>
            {
                Assert.AreEqual(cwd, Path.GetDirectoryName(exe), "It runs from its own folder.");
                Assert.IsTrue(File.Exists(exe), "The program it starts exists.");
                Assert.IsTrue(Program.TryParseInstallArgs(arguments, out InstallRequest? request, out string? problem), problem);
                results.Add(RunInstall(cwd));
                return new StepOutcome(ChildInstallStarter.Step, true, 0, "S_OK", "started");
            };

        public string ReadInstalled(string relative) => File.ReadAllText(Path.Combine(Install, relative.Replace('/', '\\')));

        public string[] WorkFolders() =>
            Directory.GetDirectories(Path.GetDirectoryName(Install)!, "Earshot" + UpdateActions.WorkFolderTag + "*");

        public string[] InstallSiblings() =>
            Directory.GetDirectories(Path.GetDirectoryName(Install)!).Where(d => Path.GetFileName(d) != "Earshot").ToArray();

        public SortedDictionary<string, string> InstallSnapshot() => Snapshot(Install);

        public static SortedDictionary<string, string> Snapshot(string folder)
        {
            var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                snapshot[Path.GetRelativePath(folder, file)] = ReleaseZipBuilder.Sha256Hex(File.ReadAllBytes(file));
            }

            return snapshot;
        }

        private static AccountLookup NoLookup(string account) =>
            throw new AssertFailedException("No account name should be looked up: " + account);

        public void Dispose() => _temp.Dispose();
    }

    private static string Steps(InstallResult result) =>
        string.Join(Environment.NewLine, result.Steps.Select(GateActions.Describe));

    // ----- the whole update, with the install run for real -----

    [TestMethod]
    public void AnUpdateInstallsExactlyTheVerifiedZipFromAFolderOnlyAdministratorsCanWrite()
    {
        using var w = new World();
        byte[] zip = World.ReleaseFiles("1.2.0", "new-only.dll").Build();
        UpdateRequest request = w.Stage(zip);
        var installs = new List<InstallResult>();
        w.StarterRunsTheRealInstall(installs);

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        (string exe, string[] arguments, string cwd) = w.Starter.Starts.Single();
        string work = w.WorkFolders().Single();
        Assert.AreEqual(Path.Combine(work, "app", "Earshot.exe"), exe, "The program started is the release's own, in the work folder, never one from the staging folder.");
        Assert.IsFalse(exe.StartsWith(w.Staging, StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(new[] { "install", TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer.ToString("D").ToLowerInvariant() }, arguments);
        Assert.AreEqual(GateExitCode.Success, installs.Single().Outcome, Steps(installs.Single()));
        Assert.AreEqual("exe 1.2.0", w.ReadInstalled("Earshot.exe"));
        Assert.AreEqual("new-only.dll 1.2.0", w.ReadInstalled("new-only.dll"));

        // The work folder was made with the descriptor that keeps everyone but SYSTEM and Administrators out.
        Assert.AreEqual(UpdateActions.WorkFolderSddl, w.Folders.CreatedWith[work]);
        Assert.IsEmpty(UpdateActions.CheckWorkFolder(UpdateActions.WorkFolderSddl));
        Assert.IsTrue(w.Log.Has(LogLevel.Info, "the checked release is installing from"));
    }

    [TestMethod]
    public void AFileOrFileListPlantedInTheUserStagingFolderNeverReachesTheWorkFolderOrTheInstallFolder()
    {
        using var w = new World();
        byte[] zip = World.ReleaseFiles("1.2.0").Build();
        UpdateRequest request = w.Stage(zip);

        // What a program running as the user could put beside the zip, or in an unpacked folder next to it.
        string app = Path.Combine(w.Staging, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(w.Staging, "evil.dll"), "planted dll");
        File.WriteAllText(Path.Combine(w.Staging, "Earshot.exe"), "planted exe");
        File.WriteAllText(Path.Combine(app, "Earshot.exe"), "planted exe");
        File.WriteAllText(Path.Combine(app, "evil.dll"), "planted dll");
        File.WriteAllText(Path.Combine(app, "Earshot.files.json"),
            "{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"Earshot.exe\", \"Sha256\": \"" + ReleaseZipBuilder.Sha256Hex("planted exe"u8.ToArray()) + "\" }, " +
            "{ \"Path\": \"evil.dll\", \"Sha256\": \"" + ReleaseZipBuilder.Sha256Hex("planted dll"u8.ToArray()) + "\" } ] }");
        var installs = new List<InstallResult>();
        w.StarterRunsTheRealInstall(installs);

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.AreEqual("exe 1.2.0", w.ReadInstalled("Earshot.exe"), "The zip's bytes are installed, not the planted ones.");
        Assert.AreEqual("dll 1.2.0", w.ReadInstalled("Earshot.dll"));
        Assert.IsFalse(File.Exists(Path.Combine(w.Install, "evil.dll")), "A planted DLL never reaches Program Files.");
        string work = w.WorkFolders().Single();
        Assert.IsEmpty(Directory.GetFiles(work, "evil.dll", SearchOption.AllDirectories), "A planted DLL never reaches the work folder.");
        Assert.IsFalse(Directory.GetFiles(w.Install, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("planted", StringComparison.Ordinal)));
    }

    // Running the program from the staging folder would let any change made after the check through. A zip changed
    // after the tray hashed it is refused whatever the change is: another valid release, or bytes that are no archive.
    [TestMethod]
    public void AZipChangedAfterTheTrayHashedItIsRefusedAndNothingIsInstalled()
    {
        using var w = new World();
        byte[] verified = World.ReleaseFiles("1.2.0").Build();
        UpdateRequest request = w.Stage(verified);
        byte[] another = World.ReleaseFiles("9.9.9", "backdoor.dll").Build();
        File.WriteAllBytes(w.ZipPath, another);
        SortedDictionary<string, string> before = w.InstallSnapshot();

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "update-verify-zip" && !s.Ok), Steps(result));
        StringAssert.Contains(result.Steps.First(s => s.Step == "update-verify-zip").Detail, ReleaseZipBuilder.Sha256Hex(another));
        Assert.IsEmpty(w.Starter.Starts, "Nothing is started.");
        Assert.IsEmpty(w.Waiter.Waits, "The tray is not even waited for.");
        CollectionAssert.AreEqual(before.ToArray(), w.InstallSnapshot().ToArray(), "The install folder is untouched.");
        Assert.IsEmpty(w.WorkFolders(), "The work folder is removed after a refusal.");
    }

    [TestMethod]
    public void AHashThatIsNotTheCopysRefusesEvenForAnUnchangedZip()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build(), hash: new string('A', 64));

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "update-verify-zip" && !s.Ok), Steps(result));
        Assert.IsEmpty(w.Starter.Starts);
    }

    [TestMethod]
    public void AZipThatIsMissingALinkOrNoArchiveIsRefusedWithItsCause()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        File.Delete(w.ZipPath);
        InstallResult missing = w.Actions().Run(request);
        Assert.AreEqual(GateExitCode.Failed, missing.Outcome);
        StringAssert.Contains(missing.Steps.Last(s => !s.Ok).Detail, "Missing");

        byte[] notAnArchive = new byte[5000];
        new Random(1).NextBytes(notAnArchive);
        UpdateRequest garbage = w.Stage(notAnArchive);
        InstallResult bad = w.Actions().Run(garbage);
        Assert.AreEqual(GateExitCode.Failed, bad.Outcome);
        Assert.IsTrue(bad.Steps.Any(s => s.Step == "update-unpack" && !s.Ok), Steps(bad));
        Assert.IsEmpty(w.Starter.Starts);
        Assert.IsEmpty(w.WorkFolders());
    }

    [TestMethod]
    public void AZipWhoseFilesDoNotMatchItsOwnFileListOrEscapeTheFolderIsRefusedElevatedToo()
    {
        using var w = new World();
        var wrongHash = World.ReleaseFiles("1.2.0");
        wrongHash.WrongHash["Earshot.dll"] = new string('0', 64);
        Assert.AreEqual(GateExitCode.Failed, w.Actions().Run(w.Stage(wrongHash.Build())).Outcome);

        var slip = World.ReleaseFiles("1.2.0");
        slip.RawEntries.Add(("Earshot/../../evil.dll", [1, 2, 3]));
        InstallResult result = w.Actions().Run(w.Stage(slip.Build()));

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "update-unpack" && !s.Ok), Steps(result));
        Assert.IsEmpty(w.Starter.Starts);
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(w.Install)!, "evil.dll")), "Zip slip writes nothing outside the work folder.");
        Assert.IsEmpty(w.WorkFolders());
    }

    // ----- the work folder -----

    private sealed class JunctionFolders(string decoy, FakeFolderSecurity fake) : IFolderSecurity
    {
        public StepOutcome CreateHardened(string path) => fake.CreateHardened(path);

        // A folder that is a junction to somewhere else, where the work folder should be.
        public StepOutcome CreateWithSddl(string path, string sddl)
        {
            var start = new ProcessStartInfo(CmdExe, "/c mklink /J \"" + path + "\" \"" + decoy + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using Process process = Process.Start(start)!;
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, "mklink /J made the junction.");
            return new StepOutcome("folder-create", true, 0, "S_OK", path);
        }

        // The real reader, which refuses a reparse point.
        public StepOutcome ReadSddl(string path, out string? sddl) =>
            path.Contains(".update-", StringComparison.Ordinal) ? new NtfsFolderSecurity().ReadSddl(path, out sddl) : fake.ReadSddl(path, out sddl);
    }

    [TestMethod]
    public void AReparsePointWhereTheWorkFolderShouldBeIsRefusedAndNothingIsWrittenThroughIt()
    {
        using var w = new World();
        string decoy = Path.Combine(w.Root, "decoy");
        Directory.CreateDirectory(decoy);
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());

        InstallResult result = w.Actions(folders: new JunctionFolders(decoy, w.Folders)).Run(request);

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome, Steps(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "folder-acl-read" && !s.Ok && (s.Detail ?? "").Contains("reparse point", StringComparison.Ordinal)), Steps(result));
        Assert.IsEmpty(Directory.GetFileSystemEntries(decoy), "Nothing was written through the junction.");
        Assert.IsEmpty(w.Starter.Starts);
        Assert.IsEmpty(w.Waiter.Waits);
    }

    [TestMethod]
    public void TheRealFolderReaderRefusesARealJunction()
    {
        using var temp = new TempFolder();
        string target = temp.File("target");
        string link = temp.File("link");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo(CmdExe, "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (Process process = Process.Start(start)!)
        {
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode);
        }

        try
        {
            StepOutcome plain = new NtfsFolderSecurity().ReadSddl(target, out string? plainSddl);
            StepOutcome junction = new NtfsFolderSecurity().ReadSddl(link, out string? junctionSddl);

            Assert.IsTrue(plain.Ok, "A plain folder reads.");
            Assert.IsNotNull(plainSddl);
            Assert.IsFalse(junction.Ok);
            Assert.IsNull(junctionSddl);
            StringAssert.Contains(junction.Detail, "reparse point");
        }
        finally
        {
            // Removes the junction itself, never what it points to; the temporary folder's own clean-up cannot.
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void AWorkFolderWhoseSecurityDoesNotReadBackAsAdministratorsOnlyIsRefused()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        w.Folders.SddlFor = path => path.Contains(".update-", StringComparison.Ordinal)
            ? "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + TestUsers.Sid + ")"
            : null;

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome, Steps(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "update-work-acl" && !s.Ok), Steps(result));
        Assert.IsEmpty(w.Starter.Starts);
        Assert.IsEmpty(w.Waiter.Waits);
        Assert.IsEmpty(w.WorkFolders(), "A folder that failed its check is not left behind holding a zip.");
    }

    [TestMethod]
    public void AWorkFolderThatCannotBeCreatedStopsTheUpdateBeforeTheZipIsCopied()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        w.Folders.FailCreate = true;

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome);
        Assert.IsEmpty(w.Starter.Starts);
        Assert.IsEmpty(w.WorkFolders());
    }

    [TestMethod]
    public void TheWorkFolderCheckAcceptsOnlyTheProductionDescriptor()
    {
        Assert.IsEmpty(UpdateActions.CheckWorkFolder(UpdateActions.WorkFolderSddl));
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder(null));
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder("O:BAG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"), "Not protected.");
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder("O:BUG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"), "Owned by Users.");
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FW;;;BU)"), "Users can write.");
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"), "Users can read: only SYSTEM and Administrators may have any access.");
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + TestUsers.Sid + ")"), "The signed-in user can write.");
        Assert.IsNotEmpty(UpdateActions.CheckWorkFolder("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)"), "Everyone.");
    }

    // The folder security helper run for real in a temporary tree. The signed-in user cannot own a folder as
    // Administrators without an elevated token, so the production descriptor is exercised only as far as this token
    // allows (recorded below), and the mechanism (create with a descriptor, read it back, check it) is exercised with a
    // descriptor this user can create.
    [TestMethod]
    public void TheRealFolderSecurityCreatesWithTheGivenDescriptorAndReadsItBack()
    {
        using var temp = new TempFolder();
        string me = WindowsIdentity.GetCurrent().User!.Value;
        string sddl = "O:" + me + "G:" + me + "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + me + ")";
        string path = temp.File("work");
        var folders = new NtfsFolderSecurity();

        StepOutcome created = folders.CreateWithSddl(path, sddl);
        StepOutcome read = folders.ReadSddl(path, out string? back);

        Assert.IsTrue(created.Ok, created.Detail);
        Assert.IsTrue(read.Ok, read.Detail);
        var wanted = new RawSecurityDescriptor(sddl);
        var got = new RawSecurityDescriptor(back!);
        Assert.AreEqual(wanted.Owner, got.Owner);
        Assert.IsTrue((got.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "Protected: nothing is inherited from the parent.");
        string[] Aces(RawSecurityDescriptor d) => d.DiscretionaryAcl!.Cast<QualifiedAce>()
            .Select(a => a.SecurityIdentifier.Value + ":" + a.AccessMask.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":" + a.AceFlags).Order().ToArray();
        CollectionAssert.AreEqual(Aces(wanted), Aces(got), "Exactly the requested entries, in the folder, read back.");

        // The check refuses this very folder, because the signed-in user owns it and can write in it.
        IReadOnlyList<string> problems = UpdateActions.CheckWorkFolder(back);
        Assert.IsTrue(problems.Any(p => p.Contains("owner", StringComparison.OrdinalIgnoreCase)), string.Join("; ", problems));
        Assert.IsTrue(problems.Any(p => p.Contains(me, StringComparison.Ordinal)), string.Join("; ", problems));

        StepOutcome again = folders.CreateWithSddl(path, sddl);
        Assert.IsFalse(again.Ok, "A folder that already exists is never taken as the work folder.");
    }

    [TestMethod]
    public void TheProductionDescriptorCreatesAndReadsBackWhereTheTokenAllowsAndFailsCleanlyWhereItDoesNot()
    {
        using var temp = new TempFolder();
        string path = temp.File("work");
        var folders = new NtfsFolderSecurity();
        bool elevated = WindowsProcessToken.Current().IsElevatedAdministrator;

        StepOutcome created = folders.CreateWithSddl(path, UpdateActions.WorkFolderSddl);

        if (elevated)
        {
            Assert.IsTrue(created.Ok, created.Detail);
            Assert.IsTrue(folders.ReadSddl(path, out string? back).Ok);
            Assert.IsEmpty(UpdateActions.CheckWorkFolder(back), "The folder read back passes the check.");
        }
        else
        {
            // Not elevated: the failure comes back as a step with its raw code, not as an exception, and it leaves
            // no folder for the update to trust.
            Assert.IsFalse(created.Ok);
            Assert.AreNotEqual(0, created.Code);
            TestContext.WriteLine("Creating the production work folder unelevated: " + created.CodeName + " (0x" + created.Code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + created.Detail);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    // ----- earlier work folders -----

    [TestMethod]
    public void EarlierWorkFoldersAreRemovedOnlyWhenTheirSecurityChecksOut()
    {
        using var w = new World();
        string parent = Path.GetDirectoryName(w.Install)!;
        string good = Path.Combine(parent, "Earshot.update-aaaa");
        string planted = Path.Combine(parent, "Earshot.update-bbbb");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "update.zip"), "old");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "keep.txt"), "someone else's");
        w.Folders.SddlFor = path => string.Equals(path, good, StringComparison.OrdinalIgnoreCase) ? UpdateActions.WorkFolderSddl : null;
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        w.StarterRunsTheRealInstall([]);

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        Assert.IsFalse(Directory.Exists(good), "An earlier update's own work folder is removed.");
        Assert.IsTrue(File.Exists(Path.Combine(planted, "keep.txt")), "A folder that did not pass its check is never deleted from the elevated process.");
        Assert.IsTrue(result.Steps.Any(s => s.Step == "remove-earlier-update-work" && !s.Ok), "It says to remove that one by hand.");
    }

    // ----- where it runs and what it waits for -----

    [TestMethod]
    public void TheUpdateRunsOnlyFromTheInstalledCopy()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());

        InstallResult result = w.Actions(runningFrom: Path.Combine(w.Root, "Downloads", "Earshot")).Run(request);

        Assert.AreEqual(GateExitCode.NotFromInstallFolder, result.Outcome);
        Assert.IsEmpty(w.WorkFolders(), "Nothing was created.");
        Assert.IsEmpty(w.Starter.Starts);
    }

    [TestMethod]
    public void AnInstallFolderOthersCanWriteStopsTheUpdate()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        w.Folders.DefaultInstallSddl = "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;BU)";

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome);
        Assert.IsEmpty(w.WorkFolders());
        Assert.IsEmpty(w.Starter.Starts);
    }

    [TestMethod]
    public void TheInstallStartsOnlyAfterTheZipIsCheckedAndTheTrayHasEnded()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        var order = new List<string>();
        w.Waiter.During = () =>
        {
            string work = w.WorkFolders().Single();
            Assert.IsTrue(File.Exists(Path.Combine(work, UpdateService.ZipFileName)) && Directory.Exists(Path.Combine(work, "app")), "The zip is copied and unpacked before the wait.");
            order.Add("wait");
        };
        w.Starter.OnStart = (_, _, _) =>
        {
            order.Add("start");
            return new StepOutcome(ChildInstallStarter.Step, true, 0, "S_OK", "started");
        };

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Steps(result));
        CollectionAssert.AreEqual(WaitThenStart, order);
        (int pid, string image, TimeSpan timeout) = w.Waiter.Waits.Single();
        Assert.AreEqual(4321, pid, "The tray's own id is what is waited on.");
        Assert.AreEqual(Path.Combine(w.Install, "Earshot.exe"), image, "An id that now belongs to another program is not waited on.");
        Assert.AreEqual(TimeSpan.FromSeconds(3), timeout);
    }

    [TestMethod]
    public void AnUpdateWaitsLongEnoughForTheTraysExitAndItsClosingNotice()
    {
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(10), UpdateActions.TrayExitWait, "The hand-back and its failure notice need at least 10 s.");
        Assert.IsGreaterThanOrEqualTo(Earshot.App.TrayContext.DefaultExitWaitLimit + Earshot.App.TrayContext.DefaultExitNoticeTime, UpdateActions.TrayExitWait,
            "Exit waits up to DefaultExitWaitLimit for actions in flight and then keeps its notice up.");
    }

    [TestMethod]
    public void ATrayThatDoesNotEndRefusesTheUpdateAndLeavesTheInstallFolderAlone()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        w.Waiter.Answer = () => StepOutcomes.NotAttempted(ProcessExitWaiter.Step, "Process 4321 was still running after 3 s.");
        SortedDictionary<string, string> before = w.InstallSnapshot();

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "update-tray-still-running" && !s.Ok), Steps(result));
        Assert.IsEmpty(w.Starter.Starts, "The install is not started while the tray is running.");
        CollectionAssert.AreEqual(before.ToArray(), w.InstallSnapshot().ToArray());
        Assert.IsEmpty(w.WorkFolders());
    }

    [TestMethod]
    public void AnInstallProgramThatDoesNotStartIsReportedAndTheWorkFolderRemoved()
    {
        using var w = new World();
        UpdateRequest request = w.Stage(World.ReleaseFiles("1.2.0").Build());
        w.Starter.OnStart = (_, _, _) => StepOutcomes.FromWin32(ChildInstallStarter.Step, 5, "denied", ok: false);

        InstallResult result = w.Actions().Run(request);

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsEmpty(w.WorkFolders());
    }

    // ----- the helpers that can run without elevation, run for real -----

    [TestMethod]
    public void TheRealWaitReturnsWhenTheProcessEndsAndReportsOneThatDoesNot()
    {
        var waiter = new ProcessExitWaiter();
        var quick = new ProcessStartInfo(CmdExe, "/c ping -n 2 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using Process ends = Process.Start(quick)!;
        StepOutcome ended = waiter.WaitForExit(ends.Id, CmdExe, TimeSpan.FromSeconds(30));
        Assert.IsTrue(ended.Ok, ended.Detail);
        Assert.IsTrue(ends.HasExited, "It really waited for the process: " + ended.Detail);

        var slow = new ProcessStartInfo(CmdExe, "/c ping -n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using Process stays = Process.Start(slow)!;
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            StepOutcome timedOut = waiter.WaitForExit(stays.Id, CmdExe, TimeSpan.FromMilliseconds(400));
            Assert.IsFalse(timedOut.Ok);
            Assert.IsGreaterThanOrEqualTo(300, clock.ElapsedMilliseconds);
            Assert.IsFalse(stays.HasExited);
            StringAssert.Contains(timedOut.Detail, "still running");

            StepOutcome otherProgram = waiter.WaitForExit(stays.Id, Path.Combine(Path.GetTempPath(), "Earshot.exe"), TimeSpan.FromSeconds(30));
            Assert.IsTrue(otherProgram.Ok, "An id that belongs to another program is not the tray, so it is not waited on.");
            Assert.IsFalse(stays.HasExited);
        }
        finally
        {
            stays.Kill(entireProcessTree: true);
            stays.WaitForExit();
        }

        StepOutcome gone = waiter.WaitForExit(stays.Id, CmdExe, TimeSpan.FromSeconds(30));
        Assert.IsTrue(gone.Ok, "A process that is not running counts as ended: " + gone.Detail);
    }

    [TestMethod]
    public void TheRealStarterStartsAProgramWithItsArgumentsAndReportsOneItCannotStart()
    {
        using var temp = new TempFolder();
        string marker = temp.File("started.txt");
        Assert.IsFalse(marker.Contains(' ', StringComparison.Ordinal), "The temp path holds a space; this check needs one that does not.");
        var starter = new ChildInstallStarter();

        StepOutcome started = starter.Start(CmdExe, ["/c", "cd>" + marker], temp.Path);

        Assert.IsTrue(started.Ok, started.Detail);
        Assert.IsTrue(SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(30)), "The program ran.");
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            try
            {
                return string.Equals(File.ReadAllText(marker).Trim(), temp.Path, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(30)), "It ran in the folder it was given.");

        StepOutcome missing = starter.Start(Path.Combine(temp.Path, "missing.exe"), ["install"], temp.Path);
        Assert.IsFalse(missing.Ok);
        Assert.AreEqual(2, missing.Code, "ERROR_FILE_NOT_FOUND, with its raw code.");
    }

    // ----- the command line -----

    private static readonly HandoverIdentity Identity = new(TestUsers.Sid, RecordedNodes.AirPodsAddress, new Guid("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13"));

    private const string ZipInUserFolder = @"C:\Users\A Person\AppData\Local\Earshot\update\u1a2b3c4d\update.zip";

    [TestMethod]
    public void TheUpdateCommandLineIsBuiltExactly()
    {
        string hash = new string('a', 64);

        IReadOnlyList<string> arguments = UpdateHandover.UpdateArguments(ZipInUserFolder, hash, 4321, Identity);

        CollectionAssert.AreEqual(new[]
        {
            "update", ZipInUserFolder, new string('A', 64), "4321", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13",
        }, arguments.ToArray());
        Assert.AreEqual(
            "update \"" + ZipInUserFolder + "\" " + new string('A', 64) + " 4321 " + TestUsers.Sid + " 0A1B2C3D4E8C 5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13",
            UpdateHandover.JoinArguments(arguments), "A path with spaces is quoted, the rest is not.");
        Assert.IsTrue(Program.TryParseUpdateArgs(arguments, out UpdateRequest? request, out string? problem), problem);
        Assert.AreEqual(ZipInUserFolder, request!.ZipPath);
        Assert.AreEqual(new string('A', 64), request.ZipSha256);
        Assert.AreEqual(4321, request.TrayProcessId);
        Assert.AreEqual(new InstallRequest(TestUsers.Sid, "0A1B2C3D4E8C", Identity.ContainerId, TaskPrincipalMode.System), request.Install,
            "The identity is the one install takes, and never --principal user.");
    }

    [TestMethod]
    public void TheUpdateVerbTakesTheIdentityInTheSameFormTheInstallVerbDoes()
    {
        string[] update = [.. UpdateHandover.UpdateArguments(ZipInUserFolder, new string('a', 64), 1, Identity)];
        string[] install = [.. UpdateHandover.InstallArguments(Identity)];

        CollectionAssert.AreEqual(install[1..], update[^3..]);
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("install", @"C:\x\update.zip", "AAAA", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"\\server\share\update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"\\?\C:\x\update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\..\y\update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\other.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "not-a-hash", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "H", "0", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "H", "-4", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "H", "1e3", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "H", "1", "S-1-5-18", "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "H", "1", TestUsers.Sid, "0a1b2c3d4e8c", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow("update", @"C:\x\update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "00000000-0000-0000-0000-000000000000")]
    [DataRow("update", @"C:\x\update.zip", "H", "1", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13", "--principal", "user")]
    public void TheUpdateVerbRejectsAnythingElse(params string[] args)
    {
        // "H" stands for a well-formed hash, so each row fails for the one thing it names.
        string[] arguments = args.Select(a => a == "H" ? new string('C', 64) : a).ToArray();

        Assert.IsFalse(Program.TryParseUpdateArgs(arguments, out UpdateRequest? request, out string? problem));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void TheUpdateVerbRefusesSystemUnelevatedAndUnsafeEnvironmentsAndRunsNothingThen()
    {
        string[] arguments = [.. UpdateHandover.UpdateArguments(ZipInUserFolder, new string('a', 64), 4321, Identity)];
        var log = new CapturingLog();
        Func<UpdateRequest, InstallResult> never = _ => throw new AssertFailedException("A refused update must not run.");

        Assert.AreEqual(GateExitCode.RunningAsSystem, Program.RunUpdate(arguments, FakeToken.System, log, never, []));
        Assert.AreEqual(GateExitCode.NotElevated, Program.RunUpdate(arguments, FakeToken.PlainUser, log, never, []));
        Assert.AreEqual(GateExitCode.Rejected, Program.RunUpdate(["update"], FakeToken.ElevatedUser, log, never, []));
        Assert.AreEqual(GateExitCode.UnsafeEnvironment, Program.RunUpdate(arguments, FakeToken.ElevatedUser, log, never, ["DOTNET_STARTUP_HOOKS"]));

        UpdateRequest? seen = null;
        GateExitCode ran = Program.RunUpdate(arguments, FakeToken.ElevatedUser, log, request =>
        {
            seen = request;
            return new InstallResult(GateExitCode.Success, [new StepOutcome("update-step", true, 0, "S_OK", "done")]);
        }, []);

        Assert.AreEqual(GateExitCode.Success, ran);
        Assert.AreEqual(4321, seen!.TrayProcessId);
        Assert.IsTrue(log.Has(LogLevel.Info, "update: success (0)."), "The result is logged with the steps.");
    }

    [TestMethod]
    public void TheUpdateVerbIsAPrivilegedModeRefusedInSafeModeAndOnATestDataRoot()
    {
        Assert.IsTrue(Program.PrivilegedModes.Contains("update"));
        using var temp = new TempFolder();
        var log = new CapturingLog();
        string[] arguments = [.. UpdateHandover.UpdateArguments(ZipInUserFolder, new string('a', 64), 4321, Identity)];

        int safe = Program.Dispatch(arguments, Paths.FromEnvironment(name => name switch { Paths.DataRootVariable => temp.Path, Paths.SafeModeVariable => "1", _ => null }), log);
        int redirected = Program.Dispatch(arguments, Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null), log);

        Assert.AreEqual(ExitCodes.Refused, safe);
        Assert.AreEqual(ExitCodes.Refused, redirected);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Refused: update"));
    }
}
