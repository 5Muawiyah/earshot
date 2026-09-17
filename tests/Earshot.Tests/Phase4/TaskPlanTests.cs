using System.Runtime.InteropServices;
using System.Security.Principal;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class TaskPlanTests
{
    private const string InstallFolder = @"C:\Program Files\Earshot";

    public TestContext TestContext { get; set; } = null!;

    private static TaskSpec Spec(string name, TaskPrincipalMode mode = TaskPrincipalMode.System) =>
        TaskPlan.Spec(name, InstallFolder, TestUsers.Sid, mode);

    [TestMethod]
    public void ThePlanHasTheThreeDesignTasks()
    {
        IReadOnlyList<TaskSpec> plan = TaskPlan.Build(InstallFolder, TestUsers.Sid, TaskPrincipalMode.System);

        Assert.HasCount(3, plan);
        TaskSpec gate = plan[0], protect = plan[1], boot = plan[2];

        Assert.AreEqual(@"\Earshot\Gate", gate.Path);
        Assert.AreEqual(@"C:\Program Files\Earshot\Earshot.exe", gate.ExecutablePath);
        Assert.AreEqual("gate $(Arg0) $(Arg1) $(Arg2)", gate.Arguments);
        Assert.AreEqual("PT2M", gate.ExecutionTimeLimit);
        Assert.IsFalse(gate.BootTrigger);
        Assert.IsTrue(gate.UserMayRun);
        Assert.AreEqual(Sddl.RunnableTask(TestUsers.Sid), gate.Sddl);

        Assert.AreEqual(@"\Earshot\Protect", protect.Path);
        Assert.AreEqual("gate-protect $(Arg0) $(Arg1)", protect.Arguments, "The Protect task runs only the protect verbs.");
        Assert.AreEqual("PT5M", protect.ExecutionTimeLimit);
        Assert.AreEqual(Sddl.RunnableTask(TestUsers.Sid), protect.Sddl);

        Assert.AreEqual(@"\Earshot\BootBlock", boot.Path);
        Assert.AreEqual("gate boot", boot.Arguments);
        Assert.IsTrue(boot.BootTrigger);
        Assert.IsFalse(boot.UserMayRun);
        Assert.AreEqual(Sddl.ReadableTask(TestUsers.Sid), boot.Sddl);

        foreach (TaskSpec spec in plan)
        {
            Assert.AreEqual(new TaskPrincipal("S-1-5-18", TaskSchedulerCom.TASK_LOGON_SERVICE_ACCOUNT, TaskSchedulerCom.TASK_RUNLEVEL_HIGHEST), spec.Principal);
            Assert.IsTrue(spec.Arguments.StartsWith("gate ", StringComparison.Ordinal) || spec.Arguments.StartsWith("gate-protect ", StringComparison.Ordinal),
                "The literal gate or gate-protect token comes first.");
        }
    }

    [TestMethod]
    public void TheUserPrincipalAppliesToGateAndProtectOnly()
    {
        IReadOnlyList<TaskSpec> plan = TaskPlan.Build(InstallFolder, TestUsers.Sid, TaskPrincipalMode.InteractiveUser);
        var user = new TaskPrincipal(TestUsers.Sid, TaskSchedulerCom.TASK_LOGON_INTERACTIVE_TOKEN, TaskSchedulerCom.TASK_RUNLEVEL_HIGHEST);

        Assert.AreEqual(user, plan[0].Principal);
        Assert.AreEqual(user, plan[1].Principal);
        Assert.AreEqual(Sddl.LocalSystemSid, plan[2].Principal.UserId);
        Assert.AreEqual(Sddl.RunnableTask(TestUsers.Sid), plan[0].Sddl, "The same SDDL either way.");
        Assert.ThrowsExactly<ArgumentException>(() => TaskPlan.PrincipalFor(TaskPlan.GateTaskName, TaskPrincipalMode.InteractiveUser, "S-1-1-0"));
    }

    [TestMethod]
    public void TheXmlCheckAcceptsWhatTheServiceReturns()
    {
        foreach (TaskPrincipalMode mode in Enum.GetValues<TaskPrincipalMode>())
        {
            foreach (TaskSpec spec in TaskPlan.Build(InstallFolder, TestUsers.Sid, mode))
            {
                IReadOnlyList<string> problems = TaskXmlCheck.Verify(TaskXml.For(spec), spec, Lookups.None, null);
                Assert.IsEmpty(problems, spec.Name + " " + mode + ": " + string.Join(" | ", problems));
            }
        }
    }

    [TestMethod]
    [DataRow("SYSTEM")]
    [DataRow("NT AUTHORITY\\SYSTEM")]
    [DataRow("s-1-5-18")]
    public void LocalSystemMayReadBackByName(string userId)
    {
        TaskSpec spec = Spec(TaskPlan.GateTaskName);

        Assert.IsEmpty(TaskXmlCheck.Verify(TaskXml.For(spec, userId: userId), spec, Lookups.None, null));
    }

    [TestMethod]
    public void AUserPrincipalMayReadBackAsAnAccountName()
    {
        TaskSpec spec = Spec(TaskPlan.GateTaskName, TaskPrincipalMode.InteractiveUser);

        Assert.IsEmpty(TaskXmlCheck.Verify(TaskXml.For(spec, userId: @"PC\owner"), spec, Lookups.Only(@"PC\owner", TestUsers.Sid), null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(spec, userId: @"PC\other"), spec, Lookups.Only(@"PC\owner", TestUsers.Sid), null));
    }

    // A name that does not resolve keeps its native code: the problem names it and the lookup step is
    // recorded next to it, so a failed lookup is never only "not the user".
    [TestMethod]
    public void AUserPrincipalWhoseNameDoesNotResolveRecordsTheLookupCode()
    {
        TaskSpec spec = Spec(TaskPlan.GateTaskName, TaskPrincipalMode.InteractiveUser);
        StepOutcome failure = StepOutcomes.FromWin32(AccountSids.Step, 1789, "'PC\\gone'", ok: false);
        var steps = new List<StepOutcome>();

        IReadOnlyList<string> problems = TaskXmlCheck.Verify(TaskXml.For(spec, userId: @"PC\gone"), spec, _ => new AccountLookup(null, failure), steps);

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "not the user " + TestUsers.Sid);
        StringAssert.Contains(problems[0], failure.CodeName);
        Assert.AreEqual(failure, steps.Single());
    }

    [TestMethod]
    public void VerifyInstalledKeepsTheLookupStepWhenNeitherPrincipalMatches()
    {
        TaskSpec userGate = Spec(TaskPlan.GateTaskName, TaskPrincipalMode.InteractiveUser);
        StepOutcome failure = StepOutcomes.FromHResult(AccountSids.Step, unchecked((int)0x80131501), "'PC\\gone'", ok: false);
        var steps = new List<StepOutcome>();

        IReadOnlyList<string> problems = TaskXmlCheck.VerifyInstalled(
            TaskXml.For(userGate, userId: @"PC\gone"), TaskPlan.GateTaskName, InstallFolder, TestUsers.Sid, _ => new AccountLookup(null, failure), steps);

        Assert.IsNotEmpty(problems);
        Assert.AreEqual(failure, steps.Single());
    }

    // Read-only: resolves this account's own name and a name no account has, through the local security
    // authority. Nothing is changed.
    [TestMethod]
    [TestCategory("ReadOnlySystem")]
    public void AccountSidsReportsTheCodeOfAFailedLookup()
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        AccountLookup self = AccountSids.Translate(current.Name);
        AccountLookup missing = AccountSids.Translate("Earshot-no-such-account-" + Guid.NewGuid().ToString("N")[..8]);
        AccountLookup empty = AccountSids.Translate(" ");

        Assert.AreEqual(current.User?.Value, self.Sid, GateActions.Describe(self.Step));
        Assert.IsTrue(self.Step.Ok);
        Assert.IsNull(missing.Sid);
        Assert.IsFalse(missing.Step.Ok);
        Assert.AreNotEqual(0, missing.Step.Code, GateActions.Describe(missing.Step));
        Assert.AreEqual(AccountSids.Step, missing.Step.Step);
        Assert.IsNull(empty.Sid);
        Assert.AreEqual(NativeCodes.NotAttempted, empty.Step.Code);
    }

    [TestMethod]
    public void AQuotedCommandIsTheSamePath()
    {
        TaskSpec spec = Spec(TaskPlan.GateTaskName);

        Assert.IsEmpty(TaskXmlCheck.Verify(TaskXml.For(spec, command: "\"C:\\PROGRAM FILES\\Earshot\\Earshot.exe\""), spec, Lookups.None, null));
    }

    [TestMethod]
    public void TheXmlCheckRejectsEveryDifference()
    {
        TaskSpec gate = Spec(TaskPlan.GateTaskName);
        TaskSpec boot = Spec(TaskPlan.BootTaskName);
        TaskSpec userGate = Spec(TaskPlan.GateTaskName, TaskPrincipalMode.InteractiveUser);

        string[] bad =
        [
            TaskXml.For(gate, command: @"C:\Users\Public\Earshot.exe"),
            TaskXml.For(gate, command: @"C:\Program Files\Earshot\Earshot.exe.evil"),
            TaskXml.For(gate, arguments: "install $(Arg0)"),
            TaskXml.For(gate, arguments: "$(Arg0) $(Arg1) $(Arg2)"),
            TaskXml.For(gate, timeLimit: "PT72H"),
            TaskXml.For(gate, multipleInstances: "IgnoreNew"),
            TaskXml.For(gate, userId: TestUsers.Sid),
            TaskXml.For(gate, logonType: "Password"),
            TaskXml.For(gate).Replace("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", "", StringComparison.Ordinal),
            TaskXml.For(gate).Replace("<Settings>", "<Settings><Enabled>false</Enabled>", StringComparison.Ordinal),
            TaskXml.For(gate).Replace("<Settings>", "<Settings><AllowStartOnDemand>false</AllowStartOnDemand>", StringComparison.Ordinal),
            TaskXml.For(gate).Replace("<Principals>", "<Triggers><LogonTrigger/></Triggers><Principals>", StringComparison.Ordinal),
            TaskXml.For(gate).Replace("</Exec>", "</Exec><Exec><Command>cmd.exe</Command></Exec>", StringComparison.Ordinal),
            TaskXml.For(gate).Replace("<UserId>S-1-5-18</UserId>", "<GroupId>S-1-5-32-545</GroupId>", StringComparison.Ordinal),
            "<Task/>",
            "not xml",
            "",
            "<?xml version=\"1.0\"?><!DOCTYPE Task [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">&x;</Task>",
        ];
        foreach (string xml in bad)
        {
            Assert.IsNotEmpty(TaskXmlCheck.Verify(xml, gate, Lookups.None, null), xml);
        }

        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(boot, includeTrigger: false), boot, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(boot).Replace("<Enabled>true</Enabled></BootTrigger>", "<Enabled>false</Enabled></BootTrigger>", StringComparison.Ordinal), boot, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(userGate, logonType: "S4U"), userGate, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(userGate, runLevel: "LeastPrivilege"), userGate, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(userGate, userId: "S-1-5-21-1-2-3-1002"), userGate, Lookups.None, null));
    }

    // A Protect task still registered with the gate token (an install from before gate-protect) would send protect
    // verbs that gate refuses, and a Gate task given gate-protect would run service changes under PT2M. The
    // read-back check calls either one needing repair.
    [TestMethod]
    public void TheReadBackCheckTellsTheTwoTokensApart()
    {
        TaskSpec gate = Spec(TaskPlan.GateTaskName);
        TaskSpec protect = Spec(TaskPlan.ProtectTaskName);

        Assert.IsEmpty(TaskXmlCheck.Verify(TaskXml.For(protect), protect, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(protect, arguments: TaskPlan.GateArguments), protect, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(protect, arguments: "gate-protect $(Arg0) $(Arg1) $(Arg2)"), protect, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.Verify(TaskXml.For(gate, arguments: TaskPlan.ProtectArguments), gate, Lookups.None, null));
    }

    [TestMethod]
    public void VerifyInstalledAcceptsEitherPrincipalForGateButOnlySystemForBootBlock()
    {
        TaskSpec userGate = Spec(TaskPlan.GateTaskName, TaskPrincipalMode.InteractiveUser);
        TaskSpec systemGate = Spec(TaskPlan.GateTaskName);
        TaskSpec boot = Spec(TaskPlan.BootTaskName);
        TaskSpec userBoot = boot with { Principal = userGate.Principal };

        Assert.IsEmpty(TaskXmlCheck.VerifyInstalled(TaskXml.For(systemGate), TaskPlan.GateTaskName, InstallFolder, TestUsers.Sid, Lookups.None, null));
        Assert.IsEmpty(TaskXmlCheck.VerifyInstalled(TaskXml.For(userGate), TaskPlan.GateTaskName, InstallFolder, TestUsers.Sid, Lookups.None, null));
        Assert.IsEmpty(TaskXmlCheck.VerifyInstalled(TaskXml.For(boot), TaskPlan.BootTaskName, InstallFolder, TestUsers.Sid, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.VerifyInstalled(TaskXml.For(userBoot), TaskPlan.BootTaskName, InstallFolder, TestUsers.Sid, Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.VerifyInstalled(TaskXml.For(userGate), TaskPlan.GateTaskName, InstallFolder, "S-1-5-21-1-2-3-1002", Lookups.None, null));
        Assert.IsNotEmpty(TaskXmlCheck.VerifyInstalled(TaskXml.For(systemGate), TaskPlan.GateTaskName, InstallFolder, "", Lookups.None, null));
    }

    // Read-only against the local Task Scheduler: builds each task definition in memory with NewTask and the
    // production writer, reads its XML back and checks it with the production verifier. Nothing is
    // registered, run or deleted.
    [TestMethod]
    [TestCategory("ReadOnlySystem")]
    public void TheServiceXmlForEachUnregisteredDefinitionPassesTheCheck()
    {
        string? currentUser = WindowsIdentity.GetCurrent().User?.Value;
        if (!Sddl.IsUserSid(currentUser))
        {
            Assert.Inconclusive("The test account has no user SID.");
        }

        var report = new List<string>();
        try
        {
            MtaThread.Run(() =>
            {
                int hr = ComTaskScheduler.WithService(service =>
                {
                    foreach (TaskPrincipalMode mode in Enum.GetValues<TaskPrincipalMode>())
                    {
                        foreach (TaskSpec spec in TaskPlan.Build(InstallFolder, currentUser!, mode))
                        {
                            int created = service.NewTask(0, out ITaskDefinition? definition);
                            Assert.AreEqual(0, created, "NewTask " + NativeCodes.Name(created));
                            Assert.IsNotNull(definition);
                            try
                            {
                                var steps = new List<StepOutcome>();
                                int applied = TaskDefinitionWriter.Apply(definition, spec, steps);
                                Assert.AreEqual(0, applied, string.Join(" | ", steps.Select(GateActions.Describe)));
                                Assert.AreEqual(0, definition.get_XmlText(out string? xml));
                                report.Add(spec.Name + " " + mode + ":" + Environment.NewLine + xml);
                                IReadOnlyList<string> problems = TaskXmlCheck.Verify(xml, spec, AccountSids.Translate, steps);
                                Assert.IsEmpty(problems, spec.Name + " " + mode + ": " + string.Join(" | ", problems));
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(definition);
                            }
                        }
                    }

                    return 0;
                });
                Assert.AreEqual(0, hr, "Connect " + NativeCodes.Name(hr));
            }, TimeSpan.FromSeconds(60));
        }
        finally
        {
            foreach (string line in report)
            {
                TestContext.WriteLine(line);
            }
        }
    }
}
