using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class TaskSchedulerGateTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string InstallFolder = @"C:\Program Files\Earshot";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Harness(string? userSid = TestUsers.Sid)
        {
            Store = new GateStore(_temp.Path);
            Tasks.InstallAll(InstallFolder);
            Gate = new TaskSchedulerGate(Tasks, Store, InstallFolder, userSid, Lookups.None, Time, (delay, ct) =>
            {
                Polls++;
                Time.Advance(delay);
                OnPoll?.Invoke(Polls);
                return !ct.IsCancellationRequested;
            });
        }

        public GateStore Store { get; }

        public FakeScheduledTasks Tasks { get; } = new();

        public ManualTime Time { get; } = new();

        public TaskSchedulerGate Gate { get; }

        public int Polls { get; private set; }

        public Action<int>? OnPoll { get; set; }

        public void WriteStatus(string nonce, GateExitCode code = GateExitCode.Success) =>
            Assert.IsTrue(Store.WriteStatus(new GateStatusFile(1, nonce, GateVerbs.Block, Time.GetUtcNow(), Time.GetUtcNow(),
                GateExitCodes.ResultName(code), (int)code, nameof(BlockState.Blocked), [], false)).Ok);

        public GateRunResult Run(string verb = GateVerbs.Block, string? address = null, CancellationToken ct = default) =>
            Gate.Run(TaskPlan.GateTaskName, verb, Nonce, address, TaskSchedulerGate.GateTimeout, ct);

        public void Dispose() => _temp.Dispose();
    }

    [TestMethod]
    public void VerifyReportsEachHealth()
    {
        using var h = new Harness();

        Assert.AreEqual(TaskHealth.Ready, h.Gate.Verify(TaskPlan.GateTaskName).Health);
        Assert.AreEqual(TaskHealth.Ready, h.Gate.Verify(TaskPlan.BootTaskName).Health);

        h.Tasks.Tasks[@"\Earshot\Gate"] = FakeScheduledTasks.Healthy(TaskPlan.GateTaskName, InstallFolder, TaskPrincipalMode.InteractiveUser);
        Assert.AreEqual(TaskHealth.Ready, h.Gate.Verify(TaskPlan.GateTaskName).Health, "Either principal is accepted.");

        h.Tasks.Tasks.Remove(@"\Earshot\Protect");
        TaskVerification missing = h.Gate.Verify(TaskPlan.ProtectTaskName);
        Assert.AreEqual(TaskHealth.Missing, missing.Health);
        Assert.AreEqual("ERROR_FILE_NOT_FOUND", missing.Steps.Single().CodeName);

        TaskReadback good = h.Tasks.Tasks[@"\Earshot\BootBlock"];
        h.Tasks.Tasks[@"\Earshot\BootBlock"] = good with { Sddl = good.Sddl + "(A;;FA;;;WD)" };
        Assert.AreEqual(TaskHealth.NeedsRepair, h.Gate.Verify(TaskPlan.BootTaskName).Health);

        TaskSpec spec = TaskPlan.Spec(TaskPlan.GateTaskName, InstallFolder, TestUsers.Sid, TaskPrincipalMode.System);
        h.Tasks.Tasks[@"\Earshot\Gate"] = good with { Sddl = spec.Sddl, Xml = TaskXml.For(spec, command: @"C:\Users\Public\Earshot.exe") };
        Assert.AreEqual(TaskHealth.NeedsRepair, h.Gate.Verify(TaskPlan.GateTaskName).Health);

        h.Tasks.ReadResult = unchecked((int)0x80070005);
        Assert.AreEqual(TaskHealth.Unreadable, h.Gate.Verify(TaskPlan.GateTaskName).Health);
    }

    [TestMethod]
    public void WithoutAUserSidNothingVerifies()
    {
        using var h = new Harness(userSid: null);

        Assert.AreEqual(TaskHealth.NeedsRepair, h.Gate.Verify(TaskPlan.GateTaskName).Health);
    }

    [TestMethod]
    public void AMissingOrUnsafeTaskIsNeverRun()
    {
        using var h = new Harness();
        h.Tasks.Tasks.Remove(@"\Earshot\Gate");
        Assert.AreEqual(GateRunOutcome.NotSetUp, h.Run().Outcome);

        h.Tasks.InstallAll(InstallFolder);
        TaskReadback good = h.Tasks.Tasks[@"\Earshot\Gate"];
        h.Tasks.Tasks[@"\Earshot\Gate"] = good with { Sddl = "O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;" + TestUsers.Sid + ")" };
        GateRunResult repair = h.Run();
        Assert.AreEqual(GateRunOutcome.NeedsRepair, repair.Outcome);
        Assert.IsTrue(repair.Steps.Any(s => s.Step == "task-check:Gate"));

        h.Tasks.ReadResult = unchecked((int)0x80070005);
        Assert.AreEqual(GateRunOutcome.RunFailed, h.Run().Outcome);

        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public void ADisabledTaskIsReportedNotRun()
    {
        using var h = new Harness();
        h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_DISABLED, 0, 1000);

        Assert.AreEqual(GateRunOutcome.TaskDisabled, h.Run().Outcome);
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public void ARunCompletesWhenTheTaskHasStoppedAndItsStatusFileExists()
    {
        using var h = new Harness();
        h.Tasks.OnRun = p =>
        {
            h.Tasks.States.Enqueue(new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, 1000));
            h.Tasks.States.Enqueue(new TaskRunState(TaskSchedulerCom.TASK_STATE_RUNNING, 0x41301, 1001));
            h.Tasks.States.Enqueue(new TaskRunState(TaskSchedulerCom.TASK_STATE_RUNNING, 0x41301, 1001));
        };
        h.OnPoll = poll =>
        {
            if (poll == 2)
            {
                h.WriteStatus(Nonce);
                h.Tasks.States.Enqueue(new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, 1001));
            }
        };

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.Completed, result.Outcome);
        Assert.HasCount(1, h.Tasks.Runs);
        CollectionAssert.AreEqual(new[] { GateVerbs.Block, Nonce }, h.Tasks.Runs[0]);
        Assert.IsNotNull(result.Status);
        Assert.AreEqual(Nonce, result.Status.Nonce);
        Assert.AreEqual(0, result.LastTaskResult);
        Assert.AreEqual("success", result.Steps.Single(s => s.Step == "task-last-result").CodeName);
        Assert.IsGreaterThanOrEqualTo(3, h.Polls);
    }

    [TestMethod]
    public void AStoppedTaskIsNotTakenAsFinishedBeforeItRan()
    {
        using var h = new Harness();
        h.OnPoll = poll =>
        {
            if (poll == 5)
            {
                h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 3, 1002);
            }
        };

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.Completed, result.Outcome, "A new LastRunTime shows the run happened.");
        Assert.AreEqual(5, h.Polls);
        Assert.IsNull(result.Status);
        StepOutcome last = result.Steps.Single(s => s.Step == "task-last-result");
        Assert.AreEqual("failed", last.CodeName);
        Assert.IsFalse(last.Ok);
    }

    // A LastRunTime that could not be read after RunEx is not a new run time. Without a status file the wait
    // runs to the timeout instead of taking a run that never started as finished.
    [TestMethod]
    public void AnUnreadableLastRunTimeNeverEndsTheWait()
    {
        using var h = new Harness();
        StepOutcome readFailure = StepOutcomes.FromHResult(@"task-last-run-time-read:\Earshot\Gate", unchecked((int)0x80070005));
        h.Tasks.OnRun = _ => h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, null) { Steps = [readFailure] };

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.TimedOut, result.Outcome);
        Assert.IsGreaterThan(10, h.Polls);
        Assert.AreEqual(readFailure, result.Steps.Single(s => s.Step.StartsWith("task-last-run-time-read:", StringComparison.Ordinal)),
            "Recorded once, not once per poll.");
    }

    [TestMethod]
    public void WithoutALastRunTimeFromBeforeTheRunOnlyTheStatusFileEndsTheWait()
    {
        using var h = new Harness();
        h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, null);
        h.OnPoll = poll =>
        {
            if (poll == 2)
            {
                h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, 1001);
            }

            if (poll == 6)
            {
                h.WriteStatus(Nonce);
            }
        };

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(6, h.Polls, "A run time with nothing to compare against is not taken as a new run.");
        Assert.IsNotNull(result.Status);
    }

    [TestMethod]
    public void AnUnreadableLastTaskResultIsUnavailableNotSuccess()
    {
        using var h = new Harness();
        h.Tasks.OnRun = _ =>
        {
            h.WriteStatus(Nonce);
            h.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, null, 1001)
            {
                Steps = [StepOutcomes.FromHResult(@"task-last-result-read:\Earshot\Gate", unchecked((int)0x80070005))],
            };
        };

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.Completed, result.Outcome);
        Assert.IsNull(result.LastTaskResult);
        StepOutcome last = result.Steps.Single(s => s.Step == "task-last-result");
        Assert.IsFalse(last.Ok);
        Assert.AreEqual(NativeCodes.NotAvailable, last.Code);
        Assert.AreEqual("E_ACCESSDENIED", result.Steps.Single(s => s.Step.StartsWith("task-last-result-read:", StringComparison.Ordinal)).CodeName);
    }

    [TestMethod]
    public void AGateRefusedByItsEnvironmentIsNamedApartFromAFailure()
    {
        StepOutcome refused = TaskSchedulerGate.LastResultStep(ExitCodes.Refused);

        Assert.IsFalse(refused.Ok);
        Assert.AreEqual("refused-by-environment", refused.CodeName);
        Assert.AreEqual(ExitCodes.Refused, refused.Code);
        Assert.AreEqual("success", TaskSchedulerGate.LastResultStep(0).CodeName);
        Assert.AreEqual("folder-not-secure", TaskSchedulerGate.LastResultStep((int)GateExitCode.FolderNotSecure).CodeName);
    }

    [TestMethod]
    public void SetDevicePassesTheAddressAsTheThirdParameter()
    {
        using var h = new Harness();
        h.Tasks.OnRun = _ => h.WriteStatus(Nonce);

        Assert.AreEqual(GateRunOutcome.Completed, h.Run(GateVerbs.SetDevice, "5A6B7C8D9EAF").Outcome);
        CollectionAssert.AreEqual(new[] { GateVerbs.SetDevice, Nonce, "5A6B7C8D9EAF" }, h.Tasks.Runs[0]);
    }

    [TestMethod]
    public void ARunThatNeverHappensTimesOut()
    {
        using var h = new Harness();

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.TimedOut, result.Outcome);
        Assert.IsGreaterThanOrEqualTo(TaskSchedulerGate.GateTimeout.Ticks, TaskSchedulerGate.PollInterval.Ticks * h.Polls);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "task-wait:Gate"));
    }

    [TestMethod]
    public void CancellingStopsTheWaitOnly()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.OnPoll = poll =>
        {
            if (poll == 2)
            {
                cts.Cancel();
            }
        };

        GateRunResult result = h.Run(ct: cts.Token);

        Assert.AreEqual(GateRunOutcome.Cancelled, result.Outcome);
        Assert.HasCount(1, h.Tasks.Runs, "The request was already sent.");
    }

    [TestMethod]
    public void ARunExFailureIsReported()
    {
        using var h = new Harness();
        h.Tasks.RunResult = unchecked((int)0x80070005);

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.RunFailed, result.Outcome);
        Assert.AreEqual("E_ACCESSDENIED", result.Steps.Single(s => s.Step.StartsWith("task-run:", StringComparison.Ordinal)).CodeName);
    }

    [TestMethod]
    public void AnInvalidStatusFileStillCompletesAndIsReported()
    {
        using var h = new Harness();
        h.Tasks.OnRun = _ => File.WriteAllText(h.Store.StatusFile(Nonce), "{\"Nonce\":\"forged\"}");

        GateRunResult result = h.Run();

        Assert.AreEqual(GateRunOutcome.Completed, result.Outcome);
        Assert.IsNull(result.Status);
        Assert.IsTrue(result.Steps.Any(s => s.Step == "read-status" && !s.Ok));
    }

    [TestMethod]
    [DataRow(GateVerbs.Boot, Nonce, null)]
    [DataRow("install", Nonce, null)]
    [DataRow(GateVerbs.Block, "nonce", null)]
    [DataRow(GateVerbs.Block, Nonce, "5A6B7C8D9EAF")]
    [DataRow(GateVerbs.SetDevice, Nonce, null)]
    [DataRow(GateVerbs.SetDevice, Nonce, "5A6b7C8d9Eaf")]
    public void OnlyRequestsTheTraySendsAreAccepted(string verb, string nonce, string? address)
    {
        using var h = new Harness();

        Assert.ThrowsExactly<ArgumentException>(() => h.Gate.Run(TaskPlan.GateTaskName, verb, nonce, address, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.IsEmpty(h.Tasks.Runs);
    }

    [TestMethod]
    public void TheTimeoutsCoverTheTaskLimits()
    {
        Assert.IsGreaterThan(TimeSpan.FromMinutes(2), TaskSchedulerGate.GateTimeout);
        Assert.IsGreaterThan(TimeSpan.FromMinutes(5), TaskSchedulerGate.ProtectTimeout);
    }
}
