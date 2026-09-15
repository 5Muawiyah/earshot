using System.Security.Cryptography;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
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

        public Harness()
        {
            Source = Path.Combine(_temp.Path, "unzip", "Earshot");
            Install = Path.Combine(_temp.Path, "ProgramFiles", "Earshot");
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Path.Combine(Source, "runtimes"));
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Machine)!);
            File.WriteAllBytes(Path.Combine(Source, "Earshot.exe"), RandomNumberGenerator.GetBytes(4096));
            File.WriteAllBytes(Path.Combine(Source, "Earshot.dll"), RandomNumberGenerator.GetBytes(100_000));
            File.WriteAllText(Path.Combine(Source, "runtimes", "native.txt"), "native");
        }

        public string Root => _temp.Path;

        public string Source { get; set; }

        public string Install { get; }

        public string Machine { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public FakeTaskRegistrar Tasks { get; } = new();

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public RebootDeleteRecorder Reboot { get; } = new();

        public CapturingLog Log { get; } = new();

        public InstallLayout Layout => new(Source, Install, Machine);

        public InstallResult RunInstall(TaskPrincipalMode mode = TaskPrincipalMode.System) =>
            new InstallActions(Layout, Folders, Tasks, NoLookup, Log)
                .Run(new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, mode));

        public InstallResult RunUninstall() => new UninstallActions(Layout, Folders, Nodes, Tasks, Reboot, Log).Run();

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
        Assert.IsTrue(result.Steps.Any(s => s.Step == "copy-app" && s.Ok && s.Detail!.Contains("3 files", StringComparison.Ordinal)));
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
    }

    [TestMethod]
    public void AnApplicationFolderWithoutTheExeIsNotInstalled()
    {
        using var h = new Harness();
        File.Delete(Path.Combine(h.Source, "Earshot.exe"));

        InstallResult result = h.RunInstall();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
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
        h.Folders.Queue(h.Machine,
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
        Assert.HasCount(5, h.Reboot.Scheduled);
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

    [TestMethod]
    public void UninstallCannotRestoreRecordedServicesWithoutTheProtectionFeature()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        Assert.IsTrue(new GateStore(h.Machine).WriteProtection(new ProtectionRecord { DisabledServices = { new Guid("0000111E-0000-1000-8000-00805F9B34FB") } }).Ok);

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual(NativeCodes.NotAvailable, result.Steps.Single(s => s.Step == "protection-restore").Code);
    }

    [TestMethod]
    public void UninstallReportsANodeThatWouldNotEnable()
    {
        using var h = new Harness();
        Assert.AreEqual(GateExitCode.Success, h.RunInstall().Outcome);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].MarkDisabled(persistent: true);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].EnableResult = CfgMgr32.CR_ACCESS_DENIED;

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual("CR_ACCESS_DENIED", result.Steps.Single(s => s.Step == "cm-enable:" + RecordedNodes.AirPodsDeviceNode).CodeName);
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

    [TestMethod]
    public void UninstallWithNothingInstalledSucceeds()
    {
        using var h = new Harness();

        InstallResult result = h.RunUninstall();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsEmpty(h.Nodes.Calls);
    }
}
