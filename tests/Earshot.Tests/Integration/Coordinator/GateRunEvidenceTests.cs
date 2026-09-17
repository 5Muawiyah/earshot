using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The coordinator tells a gate change that may still run from one that has ended by the steps TaskSchedulerGate
// records. These run the real TaskSchedulerGate against the in-memory Task Scheduler, the way the block and
// protection controllers call it, so a change to those steps fails here rather than on the device.
[TestClass]
public sealed class GateRunEvidenceTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string InstallFolder = @"C:\Program Files\Earshot";

    [TestMethod]
    public void ARunSeenToEndHasEnded()
    {
        using var gate = new GateUnderTest();
        gate.Tasks.OnRun = _ => gate.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_READY, 0, 1001);

        GateRunResult run = gate.Run();

        Assert.AreEqual(GateRunOutcome.Completed, run.Outcome);
        Assert.IsFalse(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Failed)), "A run that ended was taken as still running.");
        Assert.IsFalse(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Success)));
    }

    [TestMethod]
    public void ARunWhoseWaitRanOutMayStillRun()
    {
        using var gate = new GateUnderTest();

        // Queued behind another run: the task never leaves QUEUED while the tray waits.
        gate.Tasks.OnRun = _ => gate.Tasks.Current = new TaskRunState(TaskSchedulerCom.TASK_STATE_QUEUED, 0, 1000);

        GateRunResult run = gate.Run();

        Assert.AreEqual(GateRunOutcome.TimedOut, run.Outcome);
        Assert.IsTrue(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Failed)));
    }

    [TestMethod]
    public void ARunWhoseWaitWasStoppedMayStillRun()
    {
        using var gate = new GateUnderTest();
        using var cancel = new CancellationTokenSource();
        gate.OnPoll = () => cancel.Cancel();

        GateRunResult run = gate.Run(cancel.Token);

        Assert.AreEqual(GateRunOutcome.Cancelled, run.Outcome);
        Assert.IsTrue(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Failed)));
    }

    [TestMethod]
    public void ARunWhoseStateCouldNotBeReadWhileItWasPolledMayStillRun()
    {
        using var gate = new GateUnderTest();
        gate.Tasks.OnRun = _ => gate.Tasks.Tasks.Remove(TaskPlan.TaskPath(TaskPlan.GateTaskName));

        GateRunResult run = gate.Run();

        Assert.AreEqual(GateRunOutcome.RunFailed, run.Outcome);
        Assert.IsTrue(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Failed)));
    }

    [TestMethod]
    public void ARunTheSchedulerRefusedNeverStarted()
    {
        using var gate = new GateUnderTest();
        gate.Tasks.RunResult = unchecked((int)0x80070005);

        GateRunResult run = gate.Run();

        Assert.AreEqual(GateRunOutcome.RunFailed, run.Outcome);
        Assert.IsFalse(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Failed)));
    }

    [TestMethod]
    public void ARunForATaskThatIsNotSetUpNeverStarted()
    {
        using var gate = new GateUnderTest(setUp: false);

        GateRunResult run = gate.Run();

        Assert.AreEqual(GateRunOutcome.NotSetUp, run.Outcome);
        Assert.IsFalse(CoordinatorRules.MayStillRun(AsResult(run, OpStatus.Failed)));
    }

    [TestMethod]
    public void AResultThatSentNothingThroughATaskIsNotRunning()
    {
        ControllerResult safeMode = new(OpStatus.NotAttempted, "Safe mode: no device actions.",
            [StepOutcomes.NotAttempted("safe-mode:allow", "Safe mode: no device actions.")]);

        Assert.IsFalse(CoordinatorRules.MayStillRun(safeMode));
        Assert.IsFalse(CoordinatorRules.MayStillRun(ControllerResult.Ok("Allowed")));
    }

    // What the controllers hand back: the gate's steps, in the result they map the run to.
    private static ControllerResult AsResult(GateRunResult run, OpStatus status) => new(status, "", run.Steps);

    private sealed class GateUnderTest : IDisposable
    {
        private readonly TempFolder _temp = new();

        public GateUnderTest(bool setUp = true)
        {
            var store = new GateStore(_temp.Path);
            if (setUp)
            {
                Tasks.InstallAll(InstallFolder);
            }

            Gate = new TaskSchedulerGate(Tasks, store, InstallFolder, Phase4.TestUsers.Sid, Phase4.Lookups.None, Time, (delay, ct) =>
            {
                Time.Advance(delay);
                OnPoll?.Invoke();
                return !ct.IsCancellationRequested;
            });
        }

        public Phase4.FakeScheduledTasks Tasks { get; } = new();

        public ManualTime Time { get; } = new();

        public TaskSchedulerGate Gate { get; }

        public Action? OnPoll { get; set; }

        public GateRunResult Run(CancellationToken ct = default) =>
            Gate.Run(TaskPlan.GateTaskName, GateVerbs.Allow, Nonce, null, TaskSchedulerGate.GateTimeout, ct);

        public void Dispose() => _temp.Dispose();
    }
}
