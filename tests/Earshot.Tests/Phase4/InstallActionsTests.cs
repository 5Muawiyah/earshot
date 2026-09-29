using System.Security.Cryptography;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Service;
using Earshot.Tests.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// Install and uninstall against temp folders, a fake folder security and an in-memory Task Scheduler.
// Nothing here touches %ProgramFiles%, %ProgramData%, the real task namespace or a device node.
[TestClass]
public sealed class InstallActionsTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Harness(FakeNodeApi? nodes = null)
        {
            Nodes = nodes ?? RecordedNodes.Table();
            Source = Path.Combine(_temp.Path, "unzip", "Earshot");
            Install = Path.Combine(_temp.Path, "ProgramFiles", "Earshot");
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Path.Combine(Source, "runtimes"));
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Machine)!);
            File.WriteAllBytes(Path.Combine(Source, "Earshot.exe"), RandomNumberGenerator.GetBytes(4096));
            File.WriteAllBytes(Path.Combine(Source, "Earshot.dll"), RandomNumberGenerator.GetBytes(100_000));
            File.WriteAllText(Path.Combine(Source, "runtimes", "native.txt"), "native");
            WriteManifest("Earshot.exe", "Earshot.dll", "runtimes/native.txt");
        }

        // The publish manifest as the publish step writes it: the relative path and hash of every published
        // file, with "/" separators.
        public void WriteManifest(params string[] relativePaths)
        {
            IEnumerable<string> entries = relativePaths.Select(relative =>
            {
                string path = Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                return "    { \"Path\": \"" + relative + "\", \"Sha256\": \"" + hash + "\" }";
            });
            File.WriteAllText(
                Path.Combine(Source, FileManifest.FileName),
                "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n");
        }

        public string Root => _temp.Path;

        public string Source { get; set; }

        public string Install { get; }

        public string Machine { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public FakeTaskRegistrar Tasks { get; } = new();

        public FakeNodeApi Nodes { get; }

        public RebootDeleteRecorder Reboot { get; } = new();

        public CapturingLog Log { get; } = new();

        public InstallLayout Layout => new(Source, Install, Machine);

        // The service control manager of the run. Null: no service is managed, as in every test that is not about it.
        public FakeServiceControl? Service { get; set; }

        // The wait between two tries at moving the install folder. Null: the real one, half a second.
        public Func<TimeSpan, bool>? MoveWait { get; set; }

        public InstallResult RunInstall(TaskPrincipalMode mode = TaskPrincipalMode.System) =>
            RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, mode);

        public InstallResult RunInstall(string address, Guid container, TaskPrincipalMode mode = TaskPrincipalMode.System) =>
            new InstallActions(Layout, Folders, Nodes, Tasks, NoLookup, Log, service: Service)
                {
                    ServicePoll = _ => true,
                    FolderMoveWait = MoveWait ?? DeviceChangeLock.SleepAndContinue,
                }
                .Run(new InstallRequest(TestUsers.Sid, address, container, mode));

        public InstallResult RunUninstall() => new UninstallActions(Layout, Folders, Nodes, Tasks, Reboot, Log, service: Service).Run();

        // The fake registrar reads principals back as SIDs, so no name is ever looked up.
        private static AccountLookup NoLookup(string account) =>
            throw new AssertFailedException("No account name should be looked up: " + account);

        public void Dispose() => _temp.Dispose();
    }

    private static readonly string[] FreshInstallCalls =
    [
        @"read-folder \Earshot", "create-folder Earshot", @"read-folder \Earshot",
        "register Gate S-1-5-18 5", "register Protect S-1-5-18 5", "register BootBlock S-1-5-18 5",
        @"read-task \Earshot\Gate", @"read-task \Earshot\Protect", @"read-task \Earshot\BootBlock",
    ];

    private static readonly string[] ReplacedFolderCalls = ["delete-task Gate", "delete-task Squat", "delete-folder Earshot", "create-folder Earshot"];

    // Staging and old copies next to the install folder. ("Earshot.*" as a search pattern would also match
    // the install folder itself.)
    private static string[] LeftOvers(string install) =>
        Directory.GetDirectories(Path.GetDirectoryName(install)!)
            .Where(d => Path.GetFileName(d).StartsWith("Earshot.", StringComparison.Ordinal))
            .ToArray();

    private const string SampleHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly Guid Handsfree = new("0000111E-0000-1000-8000-00805F9B34FB");

    // What a Task Scheduler call throws when its service is stopped, with the HRESULT of ERROR_SERVICE_DISABLED.
    private sealed class SchedulerUnavailable : InvalidOperationException
    {
        public SchedulerUnavailable()
            : base("The Task Scheduler service is not available.") =>
            HResult = unchecked((int)0x80070422);
    }

    private static readonly string[] KeptRecordFiles = ["device.json", "protection.json"];

    private static readonly string[] ProtectionRecordOnly = ["protection.json"];

    private static readonly string[] DeviceRecordOnly = ["device.json"];

    private static string Fail(InstallResult result) =>
        string.Join(Environment.NewLine, result.Steps.Where(s => !s.Ok).Select(GateActions.Describe));

    [TestMethod]
    public void AFullInstallCopiesHardensWritesAndRegisters()
    {
        using var h = new Harness();

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        foreach (string file in new[] { "Earshot.exe", "Earshot.dll", Path.Combine("runtimes", "native.txt") })
        {
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(h.Source, file)), File.ReadAllBytes(Path.Combine(h.Install, file)), file);
        }

        Assert.IsEmpty(LeftOvers(h.Install), "No staging or old folder is left.");
        CollectionAssert.AreEqual(new[] { h.Machine }, h.Folders.Created);

        var store = new GateStore(h.Machine);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, store.ReadDevice().Value!.Address);
        Assert.IsTrue(store.ReadConfig().Value!.BlockAtBoot, "Block at boot defaults to on.");

        Assert.AreEqual(Sddl.TaskFolder(TestUsers.Sid), h.Tasks.FolderSddl);
        CollectionAssert.AreEquivalent(TaskPlan.TaskNames.Select(TaskPlan.TaskPath).ToArray(), h.Tasks.Tasks.Keys.ToArray());
        Assert.AreEqual(Sddl.RunnableTask(TestUsers.Sid), h.Tasks.Tasks[@"\Earshot\Gate"].Sddl);
        Assert.AreEqual(Sddl.ReadableTask(TestUsers.Sid), h.Tasks.Tasks[@"\Earshot\BootBlock"].Sddl);
        CollectionAssert.AreEqual(FreshInstallCalls, h.Tasks.Calls);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "copy-app" && s.Ok && s.Detail!.Contains("4 published files", StringComparison.Ordinal)), "The three listed files and the manifest.");
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(h.Source, FileManifest.FileName)), File.ReadAllBytes(Path.Combine(h.Install, FileManifest.FileName)),
            "The manifest is installed with the files, for a repair run.");
    }

    [TestMethod]
    public void InstallRefusesAPhoneAndChangesNothing()
    {
        using var h = new Harness();

        // The paired iPhone: a device node and service nodes in its own container, but no A2DP sink node.
        InstallResult result = h.RunInstall(RecordedNodes.IPhoneAddress, RecordedNodes.IPhoneContainer);

        Assert.AreEqual(GateExitCode.NotAudioSink, result.Outcome, Fail(result));
        AssertNothingChanged(h);
    }

    [TestMethod]
    public void InstallRefusesAContainerThatIsNotTheContainerOfTheAddresssDevice()
    {
        using var h = new Harness();

        InstallResult result = h.RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.IPhoneContainer);

        Assert.AreEqual(GateExitCode.DeviceMismatch, result.Outcome, Fail(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "install-device" && !s.Ok && s.Detail!.Contains(RecordedNodes.AirPodsContainer.ToString("D"), StringComparison.Ordinal)));
        AssertNothingChanged(h);
    }

    [TestMethod]
    public void InstallForAnAddressWithNoDeviceNodeIsNotFound()
    {
        using var h = new Harness();

        InstallResult result = h.RunInstall(RecordedNodes.HeadphonesAddress, RecordedNodes.HeadphonesContainer);

        Assert.AreEqual(GateExitCode.NotFound, result.Outcome, Fail(result));
        AssertNothingChanged(h);
    }

    [TestMethod]
    public void InstallWithAnUnreadableDeviceListFailsAndChangesNothing()
    {
        using var h = new Harness();
        h.Nodes.ListResult = CfgMgr32.CR_FAILURE;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome, Fail(result));
        AssertNothingChanged(h);
    }

    private static void AssertNothingChanged(Harness h)
    {
        Assert.IsFalse(Directory.Exists(h.Install), "Files were copied for a refused device.");
        Assert.IsFalse(Directory.Exists(h.Machine), "The machine folder was created for a refused device.");
        Assert.IsEmpty(h.Folders.Created);
        Assert.IsEmpty(h.Tasks.Calls, "The task namespace was touched for a refused device.");
    }

    [TestMethod]
    public void ThePrincipalFlagRegistersGateAndProtectForTheUserOnly()
    {
        using var h = new Harness();

        InstallResult result = h.RunInstall(TaskPrincipalMode.InteractiveUser);

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "register Gate " + TestUsers.Sid + " 3",
                "register Protect " + TestUsers.Sid + " 3",
                "register BootBlock S-1-5-18 5",
            },
            h.Tasks.Calls);
    }

    [TestMethod]
    public void AnEarlierInstallIsReplacedWhole()
    {
        using var h = new Harness();
        Directory.CreateDirectory(h.Install);
        File.WriteAllText(Path.Combine(h.Install, "stale.dll"), "old");

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsFalse(File.Exists(Path.Combine(h.Install, "stale.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(h.Install, "Earshot.exe")));
        Assert.IsEmpty(LeftOvers(h.Install));
    }

    [TestMethod]
    public void RunningFromTheInstallFolderCopiesNothing()
    {
        using var h = new Harness();
        Directory.Move(h.Source, h.Install);
        h.Source = h.Install + Path.DirectorySeparatorChar;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.AreEqual(NativeCodes.NotAttempted, result.Steps.Single(s => s.Step == "copy-app").Code);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "verify-installed" && s.Ok && s.Detail!.Contains("3 installed files", StringComparison.Ordinal)));
    }

    // A repair run from the installed copy follows the manifest rule too: no manifest there, no repair.
    [TestMethod]
    public void RunningFromTheInstallFolderWithoutAManifestIsRefused()
    {
        using var h = new Harness();
        Directory.Move(h.Source, h.Install);
        h.Source = h.Install;
        File.Delete(Path.Combine(h.Install, FileManifest.FileName));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.NoManifest, result.Outcome);
        StringAssert.Contains(result.Steps.Single(s => s.Step == FileManifest.ReadStep).Detail, FileManifest.MissingMessage);
        Assert.IsEmpty(h.Tasks.Calls);
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    [TestMethod]
    public void RunningFromTheInstallFolderWithAChangedFileIsRefused()
    {
        using var h = new Harness();
        Directory.Move(h.Source, h.Install);
        h.Source = h.Install;
        File.WriteAllText(Path.Combine(h.Install, "runtimes", "native.txt"), "changed after install");

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        StringAssert.Contains(result.Steps.Single(s => s.Step == "verify-installed:" + Path.Combine("runtimes", "native.txt")).Detail, "does not match the hash");
        Assert.IsEmpty(h.Tasks.Calls);
    }

    [TestMethod]
    public void AnApplicationFolderWithoutTheExeIsNotInstalled()
    {
        using var h = new Harness();
        h.WriteManifest("Earshot.dll", "runtimes/native.txt");
        File.Delete(Path.Combine(h.Source, "Earshot.exe"));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "copy-app" && s.Detail!.Contains("not in the published file list", StringComparison.Ordinal)));
        Assert.IsFalse(Directory.Exists(h.Install));
        Assert.IsEmpty(Directory.GetDirectories(Path.GetDirectoryName(h.Install)!));
        Assert.IsEmpty(h.Tasks.Calls);
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    [TestMethod]
    public void AnInstallFolderOthersCanWriteStopsInstall()
    {
        using var h = new Harness();
        h.Folders.DefaultInstallSddl = "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;OICIID;0x1301bf;;;BU)";

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "install-folder-acl"));
        Assert.IsFalse(Directory.Exists(h.Machine));
        Assert.IsEmpty(h.Tasks.Calls);
    }

    // Any user can create %ProgramData%\Earshot first. Install never trusts it and never deletes it: a
    // recursive delete from the elevated process could be sent outside the folder by a junction.
    [TestMethod]
    public void AMachineFolderSomeoneElseCreatedStopsInstallAndIsLeftAlone()
    {
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Machine, "sub"));
        File.WriteAllText(Path.Combine(h.Machine, "device.json"), "{\"planted\":true}");
        File.WriteAllText(Path.Combine(h.Machine, "sub", "keep.txt"), "user file");
        // Read once to see whether it holds records to check, once more before it would be used.
        h.Folders.Queue(h.Machine,
            "O:" + TestUsers.Sid + "G:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;CIID;0x116;;;BU)",
            "O:" + TestUsers.Sid + "G:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;CIID;0x116;;;BU)",
            Sddl.MachineFolder);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "machine-folder-acl" && s.Detail!.Contains("owner", StringComparison.Ordinal)));
        StringAssert.Contains(result.Steps.Single(s => s.Step == "machine-folder-existing").Detail, "by hand");
        Assert.IsEmpty(h.Folders.Created);
        Assert.AreEqual("{\"planted\":true}", File.ReadAllText(Path.Combine(h.Machine, "device.json")), "Nothing in it is written.");
        Assert.IsTrue(File.Exists(Path.Combine(h.Machine, "sub", "keep.txt")), "Nothing in it is deleted.");
        Assert.IsEmpty(h.Tasks.Calls);
    }

    [TestMethod]
    public void AJunctionWhereTheMachineFolderGoesIsRemovedWithoutTouchingItsTarget()
    {
        using var h = new Harness();
        string elsewhere = Path.Combine(h.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "important.txt"), "keep");
        using IDisposable junction = TestLinks.CreateJunction(h.Machine, elsewhere);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "remove-machine-folder-link" && s.Ok));
        Assert.IsFalse(new DirectoryInfo(h.Machine).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(elsewhere, "important.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(elsewhere, "device.json")), "Nothing is written through the link.");
    }

    [TestMethod]
    public void AnEarlierInstallOthersCouldChangeIsMovedAsideButNotDeleted()
    {
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Install, "sub"));
        File.WriteAllText(Path.Combine(h.Install, "sub", "stale.dll"), "old");
        h.Folders.SddlFor = path => path.Contains(".old-", StringComparison.Ordinal)
            ? "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;OICIID;0x1301bf;;;BU)"
            : null;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsTrue(File.Exists(Path.Combine(h.Install, "Earshot.exe")));
        string old = LeftOvers(h.Install).Single();
        StringAssert.Contains(old, ".old-");
        Assert.IsTrue(File.Exists(Path.Combine(old, "sub", "stale.dll")));
        StringAssert.Contains(result.Steps.Single(s => s.Step == "remove-old-install").Detail, "by hand");
    }

    [TestMethod]
    public void AMachineFolderThatStaysUnsafeFailsClosed()
    {
        using var h = new Harness();
        h.Folders.DefaultMachineSddl = "O:BAG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "machine-folder-acl" && s.Detail!.Contains("not protected", StringComparison.Ordinal)));
        Assert.IsFalse(File.Exists(Path.Combine(h.Machine, "device.json")), "Nothing is written to an unsafe folder.");
        Assert.IsEmpty(h.Tasks.Calls);
    }

    [TestMethod]
    public void AMachineFolderThatCannotBeCreatedFailsClosed()
    {
        using var h = new Harness();
        h.Folders.FailCreate = true;

        Assert.AreEqual(GateExitCode.FolderNotSecure, h.RunInstall().Outcome);
        Assert.IsEmpty(h.Tasks.Calls);
    }

    [TestMethod]
    public void AFileWhereTheMachineFolderGoesIsReplaced()
    {
        using var h = new Harness();
        File.WriteAllText(h.Machine, "squat");

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsTrue(Directory.Exists(h.Machine));
    }

    [TestMethod]
    public void AValidEarlierBlockAtBootChoiceIsKept()
    {
        using var h = new Harness();
        Directory.CreateDirectory(h.Machine);
        Assert.IsTrue(new GateStore(h.Machine).WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);

        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        Assert.IsFalse(new GateStore(h.Machine).ReadConfig().Value!.BlockAtBoot);
    }

    [TestMethod]
    public void AnExistingTaskFolderIsEmptiedRemovedAndCreatedAgain()
    {
        using var h = new Harness();
        h.Tasks.FolderSddl = "O:" + TestUsers.Sid + "G:SYD:(A;;FA;;;" + TestUsers.Sid + ")";
        h.Tasks.Tasks[@"\Earshot\Gate"] = ("O:" + TestUsers.Sid + "D:(A;;FA;;;WD)", "<Task/>");
        h.Tasks.Tasks[@"\Earshot\Squat"] = ("O:" + TestUsers.Sid + "D:(A;;FA;;;WD)", "<Task/>");

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        CollectionAssert.IsSubsetOf(ReplacedFolderCalls, h.Tasks.Calls);
        Assert.IsFalse(h.Tasks.Tasks.ContainsKey(@"\Earshot\Squat"));
        Assert.AreEqual(Sddl.RunnableTask(TestUsers.Sid), h.Tasks.Tasks[@"\Earshot\Gate"].Sddl);
    }

    [TestMethod]
    public void ATaskFolderThatCannotBeRemovedStopsInstall()
    {
        using var h = new Harness();
        h.Tasks.FolderSddl = Sddl.TaskFolder(TestUsers.Sid);
        h.Tasks.DeleteFolderResult = unchecked((int)0x80070091);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsFalse(h.Tasks.Calls.Any(c => c.StartsWith("register", StringComparison.Ordinal)));
        StringAssert.Contains(result.Steps.Single(s => s.Step == "task-folder-delete").Detail, "by hand");
    }

    [TestMethod]
    public void ATaskFolderWhoseSecurityReadsBackWrongIsRemoved()
    {
        using var h = new Harness();
        h.Tasks.FolderSddlReadBack = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;CI;FW;;;AU)";

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "task-folder-acl"));
        Assert.IsFalse(h.Tasks.FolderExists);
        Assert.IsEmpty(h.Tasks.Tasks);
    }

    [TestMethod]
    public void AFailedRegistrationRemovesWhatWasRegistered()
    {
        using var h = new Harness();
        h.Tasks.RegisterFailsFor = 1;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.AreEqual("E_ACCESSDENIED", result.Steps.Single(s => s.Step == "task-register:Protect").CodeName);
        Assert.IsEmpty(h.Tasks.Tasks);
        Assert.IsFalse(h.Tasks.FolderExists);
    }

    [TestMethod]
    public void ATaskWhoseSecurityReadsBackWrongFailsClosed()
    {
        using var h = new Harness();
        h.Tasks.SddlOverride = spec => spec.Sddl + "(A;;FW;;;AU)";

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "task-verify:Gate" && s.Detail!.Contains("S-1-5-11", StringComparison.Ordinal)));
        Assert.IsEmpty(h.Tasks.Tasks);
    }

    [TestMethod]
    public void ATaskWhoseDefinitionReadsBackWrongFailsClosed()
    {
        using var h = new Harness();
        h.Tasks.XmlOverride = spec => TaskXml.For(spec, command: @"C:\Users\Public\Earshot.exe");

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "task-verify:Gate" && s.Detail!.Contains("Public", StringComparison.Ordinal)));
        Assert.IsEmpty(h.Tasks.Tasks);
    }

    [TestMethod]
    public void UninstallReversesEverything()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.HasCount(9, h.Nodes.Calls.Where(c => c.Kind == "enable"));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => !h.Nodes[id].IsDisabled));
        Assert.IsEmpty(h.Tasks.Tasks);
        Assert.IsFalse(h.Tasks.FolderExists);
        Assert.IsFalse(Directory.Exists(h.Machine));
        Assert.IsFalse(Directory.Exists(h.Install));
        Assert.IsEmpty(h.Reboot.Scheduled);
    }

    [TestMethod]
    public void UninstallFromTheInstalledCopySchedulesItsDeletion()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Source = h.Install;

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsTrue(Directory.Exists(h.Install), "In use, so left for the restart.");
        Assert.HasCount(6, h.Reboot.Scheduled, "Four files, including the manifest, then two folders.");
        Assert.AreEqual(h.Install, h.Reboot.Scheduled[^1], "The folder itself goes last.");
        Assert.AreEqual(Path.Combine(h.Install, "runtimes"), h.Reboot.Scheduled[^2], "Folders after every file.");
    }

    [TestMethod]
    public void UninstallWithoutADeviceFileStillRemovesEverythingElse()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        File.Delete(new GateStore(h.Machine).DeviceFile);

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    [TestMethod]
    public void UninstallWithAnInvalidDeviceFileIsPartialAndTouchesNoNode()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        File.WriteAllText(new GateStore(h.Machine).DeviceFile, "{}");

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsFalse(h.Tasks.FolderExists, "The rest still runs.");
    }

    // Without a Bluetooth service API in the context the restore hook reports not available, whatever the node
    // table is; the record is then kept.
    [TestMethod]
    public void UninstallCannotRestoreRecordedServicesWithoutABluetoothApi()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        Assert.IsTrue(new GateStore(h.Machine).WriteProtection(new ProtectionRecord { DisabledServices = { Handsfree } }).Ok);

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual(NativeCodes.NotAvailable, result.Steps.Single(s => s.Step == "protection-restore").Code);
        CollectionAssert.AreEquivalent(KeptRecordFiles, Directory.GetFiles(h.Machine).Select(Path.GetFileName).ToArray(),
            "Only the records stay; the status files and config go.");
    }

    // The only record of what to allow and what to turn back on is in the machine folder, so a reversal that
    // did not finish keeps it (and nothing else) for another try.
    [TestMethod]
    public void UninstallReportsANodeThatWouldNotEnableAndKeepsTheRecords()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        var store = new GateStore(h.Machine);
        Assert.IsTrue(store.WriteProtection(new ProtectionRecord { DisabledServices = { Handsfree } }).Ok);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].MarkDisabled(persistent: true);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].EnableResult = CfgMgr32.CR_ACCESS_DENIED;

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual("CR_ACCESS_DENIED", result.Steps.Single(s => s.Step == "cm-enable:" + RecordedNodes.AirPodsDeviceNode).CodeName);
        Assert.IsTrue(Directory.Exists(h.Machine));
        CollectionAssert.AreEquivalent(KeptRecordFiles, Directory.GetFiles(h.Machine).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(RecordedNodes.AirPodsAddress, store.ReadDevice().Value!.Address);
        CollectionAssert.AreEqual(new[] { Handsfree }, store.ReadProtection().Value!.DisabledServices);
        StringAssert.Contains(result.Steps.Last(s => s.Step == "remove-machine-folder").Detail, "is kept with device.json and protection.json");
        Assert.IsFalse(h.Tasks.FolderExists, "The tasks are still removed.");
        Assert.IsFalse(Directory.Exists(h.Install), "The install folder is still removed.");
    }

    // A partial uninstall kept the records of the AirPods (a node it could not enable). Setting up for another device
    // over them would lose the only record of what to allow, so install refuses, as set-device does, and changes
    // nothing. Setting up for the AirPods again works.
    [TestMethod]
    public void InstallForAnotherDeviceRefusesWhileTheKeptRecordsSayTheOldOneIsStillBlocked()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].MarkDisabled(persistent: true);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].EnableResult = CfgMgr32.CR_ACCESS_DENIED;
        Assert.AreEqual(GateExitCode.Partial, h.RunUninstall().Outcome);
        int taskCalls = h.Tasks.Calls.Count;

        InstallResult other = h.RunInstall(RecordedNodes.HeadphonesAddress, RecordedNodes.HeadphonesContainer);

        Assert.AreEqual(GateExitCode.OtherDeviceBlocked, other.Outcome, Fail(other));
        Assert.AreEqual(RecordedNodes.AirPodsAddress, new GateStore(h.Machine).ReadDevice().Value!.Address);
        CollectionAssert.AreEquivalent(DeviceRecordOnly, Directory.GetFiles(h.Machine).Select(Path.GetFileName).ToArray());
        Assert.IsFalse(Directory.Exists(h.Install), "Files were copied for a refused install.");
        Assert.HasCount(taskCalls, h.Tasks.Calls, "The task namespace was touched for a refused install.");
        StringAssert.Contains(other.Steps.Last(s => s.Step == "install-device").Detail, "run uninstall again");

        h.Nodes[RecordedNodes.AirPodsDeviceNode].EnableResult = CfgMgr32.CR_SUCCESS;
        InstallResult same = h.RunInstall();
        Assert.AreEqual(GateExitCode.Success, same.Outcome, Fail(same));
    }

    [TestMethod]
    public void InstallForAnotherDeviceRefusesWhileTheKeptRecordsListServicesTurnedOff()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        Assert.IsTrue(new GateStore(h.Machine).WriteProtection(new ProtectionRecord { DisabledServices = { Handsfree } }).Ok);
        Assert.AreEqual(GateExitCode.Partial, h.RunUninstall().Outcome, "Without a Bluetooth API nothing is turned back on.");

        InstallResult other = h.RunInstall(RecordedNodes.HeadphonesAddress, RecordedNodes.HeadphonesContainer);

        Assert.AreEqual(GateExitCode.OtherDeviceProtected, other.Outcome, Fail(other));
        var store = new GateStore(h.Machine);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, store.ReadDevice().Value!.Address);
        CollectionAssert.AreEqual(new[] { Handsfree }, store.ReadProtection().Value!.DisabledServices);
        Assert.IsFalse(Directory.Exists(h.Install));
    }

    // A machine folder that fails its check is never read, so a device.json a standard user planted there cannot
    // stop install; the folder check refuses it later, as before.
    [TestMethod]
    public void InstallNeverReadsTheRecordsOfAMachineFolderThatFailsItsCheck()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        Directory.CreateDirectory(h.Machine);
        Assert.IsTrue(new GateStore(h.Machine).WriteDevice(RecordedNodes.AirPods()).Ok);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].MarkDisabled(persistent: true);
        h.Folders.SddlFor = path => string.Equals(path, h.Machine, StringComparison.OrdinalIgnoreCase)
            ? "O:" + TestUsers.Sid + "G:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)"
            : null;

        InstallResult result = h.RunInstall(RecordedNodes.HeadphonesAddress, RecordedNodes.HeadphonesContainer);

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome, Fail(result));
    }

    [TestMethod]
    public void UninstallKeepsTheProtectionRecordEvenWithoutADeviceFile()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        Assert.IsTrue(new GateStore(h.Machine).WriteProtection(new ProtectionRecord { DisabledServices = { Handsfree } }).Ok);
        File.Delete(new GateStore(h.Machine).DeviceFile);

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        CollectionAssert.AreEquivalent(ProtectionRecordOnly, Directory.GetFiles(h.Machine).Select(Path.GetFileName).ToArray());
        StringAssert.Contains(result.Steps.Single(s => s.Step == "protection-restore").Detail, "device.json is missing");
    }

    [TestMethod]
    public void UninstallDoesNotTrustOrDeleteAMachineFolderOthersCanChange()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        File.WriteAllText(Path.Combine(h.Machine, "planted.txt"), "user file");
        h.Folders.Queue(h.Machine, "O:" + TestUsers.Sid + "G:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)");

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls, "No node is changed from a file in an untrusted folder.");
        Assert.IsTrue(result.Steps.Any(s => s.Step == "machine-folder-acl"));
        StringAssert.Contains(result.Steps.Single(s => s.Step == "allow-nodes").Detail, "not safe");
        StringAssert.Contains(result.Steps.Single(s => s.Step == "remove-machine-folder").Detail, "by hand");
        Assert.IsTrue(File.Exists(Path.Combine(h.Machine, "planted.txt")), "The folder is not deleted.");
        Assert.IsFalse(h.Tasks.FolderExists, "The tasks are still removed.");
        Assert.IsFalse(Directory.Exists(h.Install), "The install folder is still removed.");
    }

    [TestMethod]
    public void UninstallLeavesAnInstallFolderOthersCanChange()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Folders.DefaultInstallSddl = "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;OICIID;0x1301bf;;;BU)";
        h.Source = h.Install;

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsTrue(Directory.Exists(h.Install));
        Assert.IsEmpty(h.Reboot.Scheduled, "Nothing is scheduled for deletion at restart either.");
        StringAssert.Contains(result.Steps.Single(s => s.Step == "remove-install-folder").Detail, "by hand");
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    // A crash part way through install or uninstall must still leave the steps taken so far and a log line.
    // Install only ever runs from a published folder: without the manifest it copies nothing and says so.
    [TestMethod]
    public void WithoutThePublishManifestNothingIsCopied()
    {
        using var h = new Harness();
        File.Delete(Path.Combine(h.Source, FileManifest.FileName));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.NoManifest, result.Outcome);
        Assert.AreEqual("no-manifest", GateExitCodes.ResultName(result.Outcome));
        StringAssert.Contains(result.Steps.Single(s => s.Step == FileManifest.ReadStep).Detail, "Install from a release build.");
        Assert.IsFalse(Directory.Exists(h.Install));
        Assert.IsEmpty(h.Tasks.Calls);
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("{ \"SchemaVersion\": 2, \"Files\": [] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [] }")]
    [DataRow("{ \"SchemaVersion\": 1 }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"..\\\\Earshot.exe\", \"Sha256\": \"" + SampleHash + "\" } ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"C:/Earshot.exe\", \"Sha256\": \"" + SampleHash + "\" } ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"Earshot.exe\", \"Sha256\": \"short\" } ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"Earshot.exe\" } ] }")]
    public void AnUnusableManifestIsRefused(string content)
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Source, FileManifest.FileName), content);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.NoManifest, result.Outcome);
        Assert.IsFalse(Directory.Exists(h.Install));
    }

    [TestMethod]
    public void AFileChangedAfterItWasPublishedIsRefused()
    {
        using var h = new Harness();
        File.WriteAllBytes(Path.Combine(h.Source, "Earshot.dll"), RandomNumberGenerator.GetBytes(100_000));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        StringAssert.Contains(result.Steps.Single(s => s.Step == "verify-manifest:Earshot.dll").Detail, "does not match the hash recorded when it was published");
        Assert.IsFalse(Directory.Exists(h.Install), "Nothing is left behind.");
        Assert.IsEmpty(LeftOvers(h.Install));
        Assert.IsEmpty(h.Tasks.Calls);
    }

    [TestMethod]
    public void OnlyThePublishedFilesAreCopiedOutOfTheFolderTheReleaseWasUnzippedInto()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Source, "someone-elses.dll"), "not part of the release");
        Directory.CreateDirectory(Path.Combine(h.Source, "Downloads"));
        File.WriteAllText(Path.Combine(h.Source, "Downloads", "invoice.pdf"), "not part of the release");

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        CollectionAssert.AreEquivalent(
            new[] { "Earshot.exe", "Earshot.dll", Path.Combine("runtimes", "native.txt"), FileManifest.FileName },
            Directory.GetFiles(h.Install, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(h.Install, f)).ToArray());
        Assert.IsTrue(result.Steps.Any(s => s.Step == "verify-manifest" && s.Ok));
    }

    [TestMethod]
    public void AnInstallThatStopsWithAnExceptionKeepsItsStepsAndIsLogged()
    {
        using var h = new Harness();
        h.Tasks.Throw = new SchedulerUnavailable();

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "copy-app" && s.Ok), "The steps before it are kept.");
        StepOutcome stopped = result.Steps.Single(s => s.Step == "install" + ElevatedFailure.StepSuffix);
        Assert.IsFalse(stopped.Ok);
        Assert.AreEqual(unchecked((int)0x80070422), stopped.Code);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "install stopped with SchedulerUnavailable"));
    }

    [TestMethod]
    public void AnUninstallThatStopsWithAnExceptionKeepsItsStepsAndIsLogged()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Tasks.Throw = new SchedulerUnavailable();

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        StepOutcome stopped = result.Steps.Single(s => s.Step == "uninstall" + ElevatedFailure.StepSuffix);
        Assert.AreEqual(unchecked((int)0x80070422), stopped.Code);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "uninstall stopped with SchedulerUnavailable"));
    }

    [TestMethod]
    public void UninstallWithNothingInstalledSucceeds()
    {
        using var h = new Harness();

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsEmpty(h.Nodes.Calls);
    }

    // ---- the hand-back service ----

    private static int IndexOf(InstallResult result, string step) => result.Steps.ToList().FindIndex(s => s.Step == step);

    private static int IndexOfPrefix(InstallResult result, string prefix) =>
        result.Steps.ToList().FindIndex(s => s.Step.StartsWith(prefix, StringComparison.Ordinal));

    private static readonly string[] FreshServiceCalls =
    [
        "query", "query", "create", "description", "preshutdown:10000", "query", "dacl:" + ServicePlan.Sddl, "query", "start", "wait:running",
    ];

    // The earlier service's process is waited for by the id read before the stop.
    private static readonly string[] ReplacedServiceCalls =
    [
        "query", "stop", "wait:stopped", "wait-exit:4321", "query", "reconfigure", "description", "preshutdown:10000", "query",
        "dacl:" + ServicePlan.Sddl, "query", "start", "wait:running",
    ];

    private static readonly string[] RestartedServiceCalls = ["query", "stop", "wait:stopped", "wait-exit:4321", "start", "wait:running"];

    private static readonly string[] RemovedServiceCalls = ["query", "stop", "wait:stopped", "delete"];

    private static readonly string[] AbsentServiceCalls = ["query"];

    [TestMethod]
    public void AFreshInstallRegistersTheServiceWithEveryValueOfThePlanAndStartsItLast()
    {
        using var h = new Harness { Service = new FakeServiceControl() };

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        FakeServiceControl service = h.Service!;
        CollectionAssert.AreEqual(FreshServiceCalls, service.Calls);
        ServiceSpec spec = ServicePlan.Spec(h.Install);
        Assert.AreEqual(spec, service.Created, "Every value is the plan's.");
        Assert.AreEqual("\"" + Path.Combine(h.Install, "Earshot.exe") + "\" service", service.Created!.ImagePath);
        Assert.AreEqual(ServicePlan.Description, service.Description);
        Assert.AreEqual(10_000u, service.Preshutdown);
        Assert.AreEqual(ServicePlan.Sddl, service.Sddl);
        Assert.AreEqual(AdvApi32.SERVICE_RUNNING, service.State);
        Assert.IsTrue(IndexOf(result, ServiceSteps.Existing) < IndexOf(result, "copy-app"), "The earlier service is looked for before anything is copied.");
        Assert.IsTrue(IndexOf(result, "copy-app") < IndexOf(result, ServiceSteps.Create));
        Assert.IsTrue(IndexOfPrefix(result, "task-verify:") < IndexOf(result, ServiceSteps.Create), "After the tasks.");
        Assert.AreEqual(ServiceSteps.Wait, result.Steps[^1].Step, "The service is started last.");
    }

    [TestMethod]
    public void SettingUpOverARunningServiceStopsItBeforeTheCopyReconfiguresItAndStartsItLast()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(@"C:\Old\Earshot"));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        CollectionAssert.AreEqual(ReplacedServiceCalls, h.Service.Calls);
        Assert.IsTrue(IndexOf(result, ServiceSteps.Stop) < IndexOf(result, "copy-app"), "Stopped before the install folder is moved aside.");
        Assert.AreEqual(ServicePlan.Spec(h.Install), h.Service.Reconfigured.Single());
        Assert.AreEqual(AdvApi32.SERVICE_RUNNING, h.Service.State);
    }

    [TestMethod]
    public void AStoppedEarlierServiceIsNotStoppedAgain()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_STOPPED, ServicePlan.Spec(@"C:\Old\Earshot"));

        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);

        Assert.DoesNotContain("stop", h.Service.Calls);
        Assert.Contains("reconfigure", h.Service.Calls);
    }

    [TestMethod]
    public void AnEarlierServiceThatWillNotStopStopsInstallBeforeAnythingIsReplaced()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(@"C:\Old\Earshot"));
        h.Service.StateAfterStop = AdvApi32.SERVICE_STOP_PENDING;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        StepOutcome refusal = result.Steps.Single(s => s.Step == ServiceSteps.Stop && s.Code == NativeCodes.NotAttempted);
        StringAssert.Contains(refusal.Detail, "could not be stopped, so nothing was replaced");
        Assert.AreEqual(-1, IndexOf(result, "copy-app"), "No copy step was reached.");
        Assert.IsFalse(Directory.Exists(h.Install));
        Assert.IsFalse(Directory.Exists(h.Machine));
        Assert.IsEmpty(h.Tasks.Calls, "No task was touched.");
        Assert.DoesNotContain("create", h.Service.Calls);
        Assert.DoesNotContain("reconfigure", h.Service.Calls);
        Assert.DoesNotContain("delete", h.Service.Calls);
    }

    [TestMethod]
    public void AServiceMarkedForDeletionFromAnEarlierInstallStopsInstallWithTheInstruction()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Fail("create", AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.Create && s.Code == (int)AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE));
        Assert.IsTrue(result.Steps.Any(s => s.Detail is not null && s.Detail.Contains("still marked for deletion. Restart, then set up again.", StringComparison.Ordinal)));
        AssertTasksKeptAndBackstopReported(h, result, "ERROR_SERVICE_MARKED_FOR_DELETE (1072)");
    }

    // The service is the backstop; the tasks are what block at every boot. Nothing that fails while the service is set up
    // may take the tasks away, and the result has to say plainly that the backstop is not there, with the raw codes.
    private static void AssertTasksKeptAndBackstopReported(Harness h, InstallResult result, string codeText)
    {
        CollectionAssert.AreEquivalent(TaskPlan.TaskNames.Select(TaskPlan.TaskPath).ToArray(), h.Tasks.Tasks.Keys.ToArray(), "The three tasks are still registered.");
        Assert.IsTrue(h.Tasks.FolderExists);
        Assert.DoesNotContain("delete-task Gate", h.Tasks.Calls, "No task was deleted.");
        Assert.IsTrue(new GateStore(h.Machine).ReadConfig().IsOk, "The records the boot block reads are still there.");
        StepOutcome report = result.Steps.Single(s => s.Step == "service-backstop");
        Assert.IsFalse(report.Ok);
        StringAssert.Contains(report.Detail, "could not be set up");
        StringAssert.Contains(report.Detail, "still blocked at every start");
        StringAssert.Contains(report.Detail, codeText);
    }

    // Any difference in the registration read back is a failure that removes the service and the tasks.
    [TestMethod]
    [DataRow("image")]
    [DataRow("startType")]
    [DataRow("preshutdown")]
    [DataRow("sddl")]
    [DataRow("sddlUnreadable")]
    public void ARegistrationThatDoesNotReadBackAsThePlanRemovesTheServiceAndKeepsTheTasks(string what)
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        FakeServiceControl service = h.Service!;
        switch (what)
        {
            case "image": service.ReadImagePath = "\"C:\\Windows\\Temp\\Earshot.exe\" service"; break;
            case "startType": service.ReadStartType = AdvApi32.SERVICE_DEMAND_START; break;
            case "preshutdown": service.ReadPreshutdown = 180_000; break;
            case "sddl": service.ReadSddl = "D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x2001D;;;AU)"; break;
            default: service.SddlUnreadable = true; break;
        }

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome, what);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.Verify && !s.Ok), what);
        Assert.Contains("delete", service.Calls, what + ": the half-registered service is deleted again.");
        Assert.DoesNotContain("start", service.Calls, what + ": never started.");
        AssertTasksKeptAndBackstopReported(h, result, "service-verify");
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("never running")]
    [DataRow("description")]
    [DataRow("preshutdown")]
    [DataRow("dacl")]
    public void AStepThatFailsAfterTheServiceIsCreatedRemovesTheServiceAndKeepsTheTasks(string failure)
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        FakeServiceControl service = h.Service!;
        if (failure == "never running")
        {
            service.StateAfterStart = AdvApi32.SERVICE_START_PENDING;
        }
        else
        {
            service.Fail(failure, AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT);
        }

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome, failure);
        Assert.Contains("delete", service.Calls, failure);
        Assert.IsFalse(service.Exists, failure);
        Assert.IsTrue(Directory.Exists(h.Install), "The install folder stays: it is under Program Files and safe.");
        AssertTasksKeptAndBackstopReported(h, result, failure == "never running" ? "service-wait" : "service-" + failure);
    }

    // A control manager that cannot say whether the service is there changes nothing, and is still no reason to undo the tasks.
    [TestMethod]
    public void AServiceThatCannotBeReadKeepsTheTasksAndIsReported()
    {
        using var h = new Harness { Service = new FakeServiceControl { UnreadableAfterQueries = 1 } };

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.DoesNotContain("create", h.Service!.Calls);
        AssertTasksKeptAndBackstopReported(h, result, "service-query");
    }

    // The tasks set up first and verified stay, and the service that could not be set up is the only thing removed.
    [TestMethod]
    public void AFailedServiceStageLeavesTheBootBlockWorking()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Fail("dacl", 5);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsFalse(h.Service.Exists);
        Assert.HasCount(3, h.Tasks.Tasks);
        Assert.IsTrue(new GateStore(h.Machine).ReadDevice().IsOk);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.Dacl && s.Code == 5));
        StringAssert.Contains(result.Steps.Single(s => s.Step == "service-backstop").Detail, "service-dacl ERROR_ACCESS_DENIED (5)");
    }

    [TestMethod]
    public void SettingUpKeepsBothSettingsOfAnExistingConfig()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        Directory.CreateDirectory(h.Machine);
        Assert.IsTrue(new GateStore(h.Machine).WriteConfig(new GateConfig { BlockAtBoot = false, HandBackAtShutdown = false }).Ok);

        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);

        GateConfig kept = new GateStore(h.Machine).ReadConfig().Value!;
        Assert.IsFalse(kept.BlockAtBoot);
        Assert.IsFalse(kept.HandBackAtShutdown);
    }

    [TestMethod]
    public void SettingUpWithNoConfigWritesBlockOnAndHandBackOff()
    {
        using var h = new Harness();

        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);

        GateConfig written = new GateStore(h.Machine).ReadConfig().Value!;
        Assert.IsTrue(written.BlockAtBoot);
        Assert.IsFalse(written.HandBackAtShutdown);
    }

    [TestMethod]
    public void TheSetupTheTrayStartsRunsWithTheRealServiceControlManager()
    {
        var log = new CapturingLog();
        var layout = new InstallLayout(@"C:\x", @"C:\y", @"C:\z");

        Assert.IsInstanceOfType<WindowsServiceControl>(Program.CreateInstallActions(layout, log).Service);
        Assert.IsInstanceOfType<WindowsServiceControl>(Program.CreateUninstallActions(layout, log).Service);
    }

    // The service goes first: stopped and deleted before a node is enabled, so a shut down that starts after the nodes were
    // enabled cannot block them again, and before the install folder is removed, so its program is not in use.
    [TestMethod]
    public void UninstallStopsAndDeletesTheServiceBeforeItEnablesTheNodesAndBeforeTheInstallFolder()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Nodes.Calls.Clear();
        h.Service!.Calls.Clear();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        CollectionAssert.AreEqual(RemovedServiceCalls, h.Service.Calls);
        Assert.IsFalse(h.Service.Exists);
        int stop = IndexOf(result, ServiceSteps.Stop);
        int delete = IndexOf(result, ServiceSteps.Delete);
        int firstEnable = IndexOfPrefix(result, "cm-enable:");
        int firstTask = IndexOfPrefix(result, "task-");
        int folder = IndexOf(result, "remove-install-folder");
        Assert.IsTrue(firstEnable > 0, "The nodes were enabled.");
        Assert.IsTrue(stop < delete && delete < firstEnable && delete < firstTask && firstTask < folder,
            $"stop {stop}, delete {delete}, first enable {firstEnable}, first task {firstTask}, install folder {folder}");
        Assert.IsFalse(Directory.Exists(h.Install), "Deleted now, not at the restart.");
        Assert.IsEmpty(h.Reboot.Scheduled);
    }

    [TestMethod]
    public void UninstallWithNoServiceRecordsThatAndCarriesOn()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Service!.Exists = false;
        h.Service.Calls.Clear();

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        CollectionAssert.AreEqual(AbsentServiceCalls, h.Service.Calls);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.Existing && s.Ok && s.Detail == "No service."));
    }

    [TestMethod]
    public void AServiceThatWillNotStopIsStillDeletedAndUninstallIsPartial()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Service!.StateAfterStop = AdvApi32.SERVICE_RUNNING;
        h.Service.Calls.Clear();

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        CollectionAssert.AreEqual(RemovedServiceCalls, h.Service.Calls, "Delete is still called: the entry goes at the restart.");
        Assert.IsTrue(result.Steps.Any(s => s.Detail is not null && s.Detail.Contains("deleted when the computer restarts", StringComparison.Ordinal)));
        Assert.IsEmpty(h.Tasks.Tasks, "Everything else was still undone.");
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    [TestMethod]
    public void AServiceAlreadyMarkedForDeletionCountsAsGone()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Service!.State = AdvApi32.SERVICE_STOPPED;
        h.Service.Fail("delete", AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE);

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
    }

    [TestMethod]
    public void UninstallStopsTheServiceEvenWhenTheMachineFolderCannotBeTrusted()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Folders.DefaultMachineSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)";
        h.Service!.Calls.Clear();

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        CollectionAssert.AreEqual(RemovedServiceCalls, h.Service.Calls);
        Assert.IsFalse(Directory.Exists(h.Install));
    }

    // ---- what the service stage waits for and leaves behind ----

    [TestMethod]
    public void TheEarlierServiceCountsAsStoppedOnlyOnceItsProcessHasExited()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(@"C:\Old\Earshot"));
        h.Service.ProcessExitsAfterStop = false;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.Contains("wait-exit:4321", h.Service.Calls);
        Assert.AreEqual(-1, IndexOf(result, "copy-app"), "Nothing was moved while a program was still running from the folder.");
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.ProcessExit && !s.Ok && s.Code == (int)AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT));
        StringAssert.Contains(result.Steps.Single(s => s.Step == ServiceSteps.Stop && s.Code == NativeCodes.NotAttempted).Detail, "could not be stopped, so nothing was replaced");
        Assert.DoesNotContain("reconfigure", h.Service.Calls);
        Assert.IsFalse(Directory.Exists(h.Install));
    }

    // Install stopped the earlier service, so a failure before the service is registered again puts it back.
    [TestMethod]
    public void AnInstallThatFailsAfterStoppingTheEarlierServiceStartsItAgain()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(@"C:\Old\Earshot"));
        File.Delete(Path.Combine(h.Source, FileManifest.FileName));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.NoManifest, result.Outcome);
        CollectionAssert.AreEqual(RestartedServiceCalls, h.Service.Calls);
        Assert.AreEqual(AdvApi32.SERVICE_RUNNING, h.Service.State, "It is running as it was.");
        Assert.IsTrue(IndexOf(result, ServiceSteps.Start) > IndexOf(result, ServiceSteps.Stop), "Started after it was stopped.");
    }

    [TestMethod]
    public void AnEarlierServiceThatCannotBeStartedAgainIsSaidToStayStoppedUntilTheRestart()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(@"C:\Old\Earshot"));
        h.Service.Fail("start", 5);
        File.Delete(Path.Combine(h.Source, FileManifest.FileName));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.NoManifest, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.Start && s.Code == 5));
        Assert.IsTrue(result.Steps.Any(s => s.Detail is not null && s.Detail.Contains("stays stopped until the computer restarts", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnEarlierServiceThatWasStoppedAlreadyIsNotStartedByAFailedInstall()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_STOPPED, ServicePlan.Spec(@"C:\Old\Earshot"));
        File.Delete(Path.Combine(h.Source, FileManifest.FileName));

        h.RunInstall();

        Assert.DoesNotContain("start", h.Service.Calls);
    }

    // A registration from an earlier install may hold values the plan leaves unset. Install brings each to the plan, and
    // writes nothing for the ones that already match.
    [TestMethod]
    public void AnEarlierRegistrationWithValuesTheSpecLeavesUnsetIsBroughtToThePlan()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        FakeServiceControl service = h.Service!;
        service.Install(AdvApi32.SERVICE_STOPPED, ServicePlan.Spec(@"C:\Old\Earshot"));
        service.FailureActionCount = 3;
        service.FailureCommand = @"C:\Temp\run.exe";
        service.DelayedAutoStart = true;
        service.ServiceSidType = 1;
        service.TriggerCount = 2;

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.Contains("clear-failure-actions", service.Calls);
        Assert.Contains("delayed:False", service.Calls);
        Assert.Contains("sid:0", service.Calls);
        Assert.Contains("clear-triggers", service.Calls);
        Assert.AreEqual(0u, service.FailureActionCount);
        Assert.IsNull(service.FailureCommand);
        Assert.IsFalse(service.DelayedAutoStart);
        Assert.AreEqual(0u, service.TriggerCount);
    }

    [TestMethod]
    public void AFreshRegistrationIsGivenNoUnsetValueWrites()
    {
        using var h = new Harness { Service = new FakeServiceControl() };

        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);

        Assert.DoesNotContain("clear-failure-actions", h.Service!.Calls);
        Assert.DoesNotContain("clear-triggers", h.Service.Calls);
        Assert.IsFalse(h.Service.Calls.Any(c => c.StartsWith("delayed:", StringComparison.Ordinal) || c.StartsWith("sid:", StringComparison.Ordinal)));
    }

    // What an empty required-privileges list would mean to the control manager is not documented, and a list that cut the
    // token down could stop the device call, so install does not write one: the read-back refuses the registration.
    [TestMethod]
    public void ARegistrationLimitedToSomePrivilegesIsRefusedAndNotRewritten()
    {
        using var h = new Harness { Service = new FakeServiceControl() };
        h.Service!.Install(AdvApi32.SERVICE_STOPPED, ServicePlan.Spec(@"C:\Old\Earshot"));
        h.Service.RequiredPrivileges = ["SeChangeNotifyPrivilege"];

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.IsTrue(result.Steps.Any(s => s.Step == ServiceSteps.Verify && s.Detail is not null && s.Detail.Contains("SeChangeNotifyPrivilege", StringComparison.Ordinal)));
        AssertTasksKeptAndBackstopReported(h, result, "service-verify");
    }

    // ---- moving the install folder while a program is still on its way out ----

    // The in-app update starts setup while the tray that asked for it is still exiting. A folder with a file open in it cannot
    // be moved until the file is let go, so the move is tried again for a few seconds instead of failing at the first try.
    [TestMethod]
    public void TheInstallFolderMoveIsTriedAgainWhileAProgramStillHoldsAFileInIt()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        FileStream holder = new(Path.Combine(h.Install, "Earshot.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var release = new Timer(_ => holder.Dispose(), null, TimeSpan.FromMilliseconds(900), Timeout.InfiniteTimeSpan);

            InstallResult result = h.RunInstall();

            Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
            StepOutcome[] moves = result.Steps.Where(s => s.Step == "move-old-install").ToArray();
            Assert.IsTrue(moves.Length >= 2, "It was tried more than once: " + moves.Length);
            Assert.IsFalse(moves[0].Ok, "The first try failed while the file was open.");
            Assert.IsTrue(moves[0].Code != 0, "The raw code of the failure is kept: " + moves[0].CodeName);
            StringAssert.StartsWith(moves[0].Detail, "Attempt 1 of " + InstallActions.FolderMoveAttempts);
            Assert.IsTrue(moves[^1].Ok, "The last try worked.");
        }
        finally
        {
            holder.Dispose();
        }
    }

    [TestMethod]
    public void TheInstallFolderMoveGivesUpAfterItsAttemptsAndInstallStopsWithNothingReplaced()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        byte[] before = File.ReadAllBytes(Path.Combine(h.Install, "Earshot.dll"));
        int waits = 0;
        h.MoveWait = _ =>
        {
            waits++;
            return true;
        };
        using var holder = new FileStream(Path.Combine(h.Install, "Earshot.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.AreEqual(InstallActions.FolderMoveAttempts - 1, waits, "One wait between each two tries.");
        Assert.HasCount(InstallActions.FolderMoveAttempts, result.Steps.Where(s => s.Step == "move-old-install" && !s.Ok));
        Assert.IsEmpty(LeftOvers(h.Install), "The staging copy is removed.");
        holder.Dispose();
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(h.Install, "Earshot.dll")), "The installed copy is as it was.");
    }
}
