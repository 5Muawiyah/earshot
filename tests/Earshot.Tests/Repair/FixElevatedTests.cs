using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// Fix connection started from a process that is already an elevated administrator: both questions are still asked, the
// plan is written where only administrators can reach it, and no second administrator prompt is raised.
[TestClass]
public sealed class FixElevatedTests
{
    private const string NoMachine = @"C:\Earshot-test-machine-folder-that-is-never-made";

    private sealed class Applied
    {
        public List<(string Path, string Sha)> Calls { get; } = new();
    }

    private static string TempMachine() => Path.Combine(Path.GetTempPath(), "earshot-ProgramData-fixelev-" + Guid.NewGuid().ToString("N"));

    private static (FixElevation Elevation, FakeFolderSecurity Folders, Applied Applied) Choose(
        FixRig rig, string machine, IProcessToken? token = null, FakeFolderSecurity? folders = null)
    {
        folders ??= new FakeFolderSecurity();
        var applied = new Applied();
        FixElevation elevation = FixElevationChoice.Choose(
            token ?? FakeToken.ElevatedUser, folders, machine, Guid.NewGuid(),
            (path, sha) =>
            {
                applied.Calls.Add((path, sha));
                return (int)GateExitCode.Success;
            },
            rig.PlanFiles, rig.Elevator);
        return (elevation, folders, applied);
    }

    private static FixHost HostWith(FixRig rig, FixElevation elevation)
    {
        FixHost baseHost = rig.Host();
        return new FixHost
        {
            Nodes = baseHost.Nodes,
            Records = baseHost.Records,
            Leftovers = baseHost.Leftovers,
            Desktop = baseHost.Desktop,
            Prompter = baseHost.Prompter,
            PlanFiles = elevation.PlanFiles,
            Elevator = elevation.Elevator,
            Pairing = baseHost.Pairing,
            RePairUi = baseHost.RePairUi,
            Gate = baseHost.Gate,
            Recovery = baseHost.Recovery,
            Output = baseHost.Output,
            Log = baseHost.Log,
            Options = baseHost.Options,
            MachineFolder = baseHost.MachineFolder,
        };
    }

    [TestMethod]
    public async Task AnElevatedRunAppliesInThisProcessWithoutASecondPromptAndStillAsksBothQuestions()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        string machine = TempMachine();
        try
        {
            (FixElevation elevation, _, Applied applied) = Choose(rig, machine);
            rig.Prompter.SayOk();
            rig.Prompter.SayCancel();

            FixResult result = await FixConnection.RunAsync([], HostWith(rig, elevation), CancellationToken.None);

            Assert.IsTrue(elevation.InProcess);
            Assert.HasCount(1, applied.Calls, "The apply step ran once, in this process.");
            Assert.IsEmpty(rig.Elevator.Calls, "No administrator prompt was raised.");
            Assert.HasCount(2, rig.Prompter.Asked, "Both questions were still asked on the screen.");
            Assert.AreEqual(FixOutcome.Declined, result.Outcome);
            Assert.StartsWith(elevation.PlanRoot!, applied.Calls[0].Path, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(FixPlanFileReader.IsPlanPath(applied.Calls[0].Path, out _));
            Assert.IsFalse(File.Exists(applied.Calls[0].Path), "The plan file is deleted after the run.");
        }
        finally
        {
            DeleteQuietly(machine);
        }
    }

    [TestMethod]
    public void ThePlanFolderOfAnElevatedRunIsAdminOnlyReadBackFromTheFolder()
    {
        using var rig = new FixRig();
        string machine = TempMachine();
        try
        {
            (FixElevation elevation, FakeFolderSecurity folders, _) = Choose(rig, machine);

            Assert.IsNull(elevation.Refusal);
            Assert.StartsWith(machine, elevation.PlanRoot!, StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual(UpdateActions.WorkFolderSddl, folders.CreatedWith[elevation.PlanRoot!]);
            StepOutcome read = folders.ReadSddl(elevation.PlanRoot!, out string? sddl);
            Assert.IsTrue(read.Ok);
            Assert.IsEmpty(UpdateActions.CheckWorkFolder(sddl), "Nobody but SYSTEM and Administrators has any right.");
        }
        finally
        {
            DeleteQuietly(machine);
        }
    }

    [TestMethod]
    public void TheAdminOnlyDescriptorIsReadBackByTheFrameworkWithNoRightForAnyoneElse()
    {
        // The real parser and writer of descriptors, on an object that never reaches the disk.
        const System.Security.AccessControl.AccessControlSections Sections =
            System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Group | System.Security.AccessControl.AccessControlSections.Access;
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(UpdateActions.WorkFolderSddl, Sections);

        string back = security.GetSecurityDescriptorSddlForm(Sections);

        Assert.IsEmpty(UpdateActions.CheckWorkFolder(back), back);
    }

    [TestMethod]
    public void AnOrdinaryRunKeepsThePromptElevatorAndTheUsersPlanFolder()
    {
        using var rig = new FixRig();
        (FixElevation elevation, FakeFolderSecurity folders, Applied applied) = Choose(rig, NoMachine, FakeToken.PlainUser);

        Assert.IsFalse(elevation.InProcess);
        Assert.AreSame(rig.Elevator, elevation.Elevator);
        Assert.AreSame(rig.PlanFiles, elevation.PlanFiles);
        Assert.IsNull(elevation.PlanRoot);
        Assert.IsEmpty(folders.Created, "No folder is made for an ordinary run.");
        Assert.IsEmpty(applied.Calls);
    }

    [TestMethod]
    public void SystemIsNotTreatedAsAnElevatedAdministratorHere()
    {
        using var rig = new FixRig();

        (FixElevation elevation, FakeFolderSecurity folders, _) = Choose(rig, NoMachine, FakeToken.System);

        Assert.IsFalse(elevation.InProcess);
        Assert.IsEmpty(folders.Created);
    }

    [TestMethod]
    public async Task AFolderThatCannotBeMadeRefusesTheRunAndNothingFallsBackToTheUsersFolder()
    {
        using var rig = new FixRig();

        (FixElevation elevation, _, Applied applied) = Choose(rig, NoMachine, folders: new FakeFolderSecurity { FailCreate = true });

        Assert.IsNotNull(elevation.Refusal);
        Assert.IsFalse(elevation.Refusal.Ok);
        Assert.IsTrue(elevation.InProcess);
        ElevatedRun run = await elevation.Elevator.ApplyAsync(Consent(), @"C:\x\fix-plan.json", new string('A', 64), CancellationToken.None);
        Assert.IsNull(run.ExitCode, "The refusing elevator applies nothing.");
        Assert.IsEmpty(applied.Calls);
        Assert.IsEmpty(rig.Elevator.Calls);
    }

    [TestMethod]
    public async Task AMachineFolderSomeoneElseCanWriteRefusesTheRun()
    {
        using var rig = new FixRig();
        string machine = TempMachine();
        try
        {
            const string UsersCanWrite = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)";
            var folders = new FakeFolderSecurity { SddlFor = _ => UsersCanWrite };

            (FixElevation elevation, _, Applied applied) = Choose(rig, machine, folders: folders);

            Assert.IsNotNull(elevation.Refusal);
            Assert.IsEmpty(folders.CreatedWith, "No plan folder was made in a machine folder that others can change.");
            ElevatedRun run = await elevation.Elevator.ApplyAsync(Consent(), @"C:\x\fix-plan.json", new string('A', 64), CancellationToken.None);
            Assert.IsNull(run.ExitCode);
            Assert.IsEmpty(applied.Calls);
        }
        finally
        {
            DeleteQuietly(machine);
        }
    }

    [TestMethod]
    public async Task ANonInteractiveOrPrivateDesktopStillPrintsThePlanAndStopsWhenElevated()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        rig.Desktop = FakeDesktop.NoOneThere();
        string machine = TempMachine();
        try
        {
            (FixElevation elevation, _, Applied applied) = Choose(rig, machine);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();

            FixResult result = await FixConnection.RunAsync([], HostWith(rig, elevation), CancellationToken.None);

            Assert.AreEqual(FixOutcome.PlanOnly, result.Outcome);
            Assert.AreEqual(ExitCodes.PlanOnly, result.ExitCode);
            Assert.IsEmpty(rig.Prompter.Asked);
            Assert.IsEmpty(applied.Calls);
            Assert.IsEmpty(rig.Elevator.Calls);
            Assert.IsEmpty(rig.Pairing.Calls);
            Assert.IsEmpty(rig.Gate.Nodes.Calls);
        }
        finally
        {
            DeleteQuietly(machine);
        }
    }

    [TestMethod]
    public async Task EveryYesStyleFlagStillChangesNothingWhenElevated()
    {
        foreach (string flag in FixConsentTests.YesFlags)
        {
            using var rig = new FixRig();
            rig.LeaveAirPodsBlocked();
            string machine = TempMachine();
            try
            {
                (FixElevation elevation, _, Applied applied) = Choose(rig, machine);

                FixResult result = await FixConnection.RunAsync([flag], HostWith(rig, elevation), CancellationToken.None);

                Assert.AreEqual(ExitCodes.Usage, result.ExitCode, flag);
                Assert.IsEmpty(applied.Calls, flag);
                Assert.IsEmpty(rig.Prompter.Asked, flag);
                Assert.IsEmpty(rig.Gate.Nodes.Calls, flag);
            }
            finally
            {
                DeleteQuietly(machine);
            }
        }
    }

    [TestMethod]
    public async Task YesVariablesAndStandardInputChangeNothingWhenElevatedAndNobodyIsThere()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        rig.Desktop = FakeDesktop.NoOneThere();
        string machine = TempMachine();
        List<EnvironmentVariableScope> scopes = FixConsentTests.YesVariables.Select(v => new EnvironmentVariableScope(v, "1")).ToList();
        TextReader originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("y\nyes\ny\n"));
            (FixElevation elevation, _, Applied applied) = Choose(rig, machine);

            FixResult result = await FixConnection.RunAsync([], HostWith(rig, elevation), CancellationToken.None);

            Assert.AreEqual(FixOutcome.PlanOnly, result.Outcome);
            Assert.IsEmpty(applied.Calls);
            Assert.IsEmpty(rig.Prompter.Asked);
            Assert.IsEmpty(rig.Gate.Nodes.Calls);
        }
        finally
        {
            Console.SetIn(originalIn);
            scopes.ForEach(s => s.Dispose());
            DeleteQuietly(machine);
        }
    }

    [TestMethod]
    public void TheInProcessApplyMakesTheChecksOfTheFixApplyMode()
    {
        int ran = 0;
        var log = new CapturingLog();
        Func<FixApplyRequest, FixApplyResult> run = _ =>
        {
            ran++;
            return new FixApplyResult(GateExitCode.Success, [], []);
        };
        string plan = @"C:\ProgramData\Earshot\fix-1\0123\fix-plan.json";
        string sha = new('A', 64);

        int notElevated = Program.ApplyFixInProcess(plan, sha, PathsFor(safe: false), FakeToken.PlainUser, log, run, []);
        int safeMode = Program.ApplyFixInProcess(plan, sha, PathsFor(safe: true), FakeToken.ElevatedUser, log, run, []);
        int badHash = Program.ApplyFixInProcess(plan, "nothex", PathsFor(safe: false), FakeToken.ElevatedUser, log, run, []);
        int asSystem = Program.ApplyFixInProcess(plan, sha, PathsFor(safe: false), FakeToken.System, log, run, []);

        Assert.AreEqual(0, ran, "None of the refused calls reached the apply step.");
        Assert.AreEqual((int)GateExitCode.NotElevated, notElevated);
        Assert.AreEqual(ExitCodes.Refused, safeMode);
        Assert.AreEqual((int)GateExitCode.Rejected, badHash);
        Assert.AreEqual((int)GateExitCode.RunningAsSystem, asSystem);

        int ok = Program.ApplyFixInProcess(plan, sha, PathsFor(safe: false), FakeToken.ElevatedUser, log, run, []);
        int loader = Program.ApplyFixInProcess(plan, sha, PathsFor(safe: false), FakeToken.ElevatedUser, log, run, ["DOTNET_STARTUP_HOOKS"]);
        Assert.AreEqual(1, ran);
        Assert.AreEqual((int)GateExitCode.Success, ok);
        Assert.AreEqual((int)GateExitCode.UnsafeEnvironment, loader, "A loader variable in the environment refuses the apply step, as it does for fix-apply.");
    }

    [TestMethod]
    public void TheRunsPlanFolderIsRemovedWhenItEndsAndAMissingOneIsNotAnError()
    {
        string root = TempMachine();
        Directory.CreateDirectory(Path.Combine(root, "abc"));
        File.WriteAllText(Path.Combine(root, "abc", FixPlanFiles.FileName), "{}");
        var log = new CapturingLog();

        FixPlanRoot.Remove(root, log);
        FixPlanRoot.Remove(root, log);

        Assert.IsFalse(Directory.Exists(root));
        Assert.IsFalse(log.Has(LogLevel.Warn, "could not be removed"));
    }

    private static Paths PathsFor(bool safe) =>
        Paths.FromEnvironment(name => name == Paths.SafeModeVariable && safe ? "1" : null);

    private static FixConsent Consent()
    {
        var prompter = new FakePrompter();
        prompter.SayOk();
        var plan = new FixPlan([], [], [], [], false, false);
        return new FixConsentDialog(prompter, FakeDesktop.Interactive()).AskChanges(plan).Consent!;
    }

    private static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test folder left in the temp folder is not a failure of the test.
        }
    }
}
