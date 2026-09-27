using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

internal sealed class FakeTaskbarReader : ITaskbarReader
{
    private readonly Lock _gate = new();
    private ITaskbarReader.Result _next = ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, new Earshot.Contracts.StepOutcome("fake", false, 0, "S_OK", null)));

    public int ReadCount { get; private set; }

    public ShownGauge? LastShownGauge { get; private set; }

    public void SetNextResult(ITaskbarReader.Result result)
    {
        lock (_gate)
        {
            _next = result;
        }
    }

    public ITaskbarReader.Result Read(ShownGauge? shownGauge)
    {
        lock (_gate)
        {
            ReadCount++;
            LastShownGauge = shownGauge;
            return _next;
        }
    }
}

// TaskbarWatcher's real thread lifecycle: this is the one execution kept of the real background loop
// (the controller's own tests drive OnLayout synchronously instead). Every test disposes the watcher
// before returning, so nothing outlives the test.
[TestClass]
public sealed class TaskbarWatcherTests
{
    private static Action<Action> ImmediateUiPost => action => action();

    [TestMethod]
    public void StartReadsAtLeastOnceWithinThePollInterval()
    {
        var reader = new FakeTaskbarReader();
        var log = new CapturingLog();
        using var watcher = new TaskbarWatcher(reader, () => null, _ => { }, ImmediateUiPost, log, TimeProvider.System)
        {
            PollIntervalMs = 50,
        };

        watcher.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 1, TimeSpan.FromSeconds(5)), "The watcher must read at least once.");
    }

    [TestMethod]
    public void PokeCausesAnImmediateReadWithoutWaitingTheFullInterval()
    {
        var reader = new FakeTaskbarReader();
        var log = new CapturingLog();
        using var watcher = new TaskbarWatcher(reader, () => null, _ => { }, ImmediateUiPost, log, TimeProvider.System)
        {
            PollIntervalMs = 30000,
        };

        // Start() itself only arms the wait; a caller that wants an immediate first read (as
        // CompositionRoot does right after Start) pokes it once.
        watcher.Start();
        watcher.Poke();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 1, TimeSpan.FromSeconds(5)));
        int before = reader.ReadCount;

        watcher.Poke();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount > before, TimeSpan.FromSeconds(2)), "A poke must not wait for the 30 s interval.");
    }

    [TestMethod]
    public void ResultsAreDeliveredThroughTheGivenUiPostDelegateNeverCalledDirectly()
    {
        var reader = new FakeTaskbarReader();
        var expected = ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.Notification, new Earshot.Contracts.StepOutcome("marker", true, 0, "S_OK", null)));
        reader.SetNextResult(expected);

        var log = new CapturingLog();
        int uiPostCalls = 0;
        ITaskbarReader.Result? delivered = null;

        using var watcher = new TaskbarWatcher(reader, () => null, result => delivered = result,
            action =>
            {
                Interlocked.Increment(ref uiPostCalls);
                action();
            },
            log, TimeProvider.System)
        {
            PollIntervalMs = 20,
        };

        watcher.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => delivered is not null, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(uiPostCalls >= 1, "Every result must go through uiPost, never call the callback directly.");
        Assert.AreEqual(expected.Failure!.Step, delivered!.Value.Failure!.Step);
    }

    [TestMethod]
    public void DisposeStopsTheThreadAndNothingOutlivesIt()
    {
        var reader = new FakeTaskbarReader();
        var log = new CapturingLog();
        var watcher = new TaskbarWatcher(reader, () => null, _ => { }, ImmediateUiPost, log, TimeProvider.System)
        {
            PollIntervalMs = 20,
        };

        watcher.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 1, TimeSpan.FromSeconds(5)));

        watcher.Dispose();
        int countAfterDispose = reader.ReadCount;
        Thread.Sleep(100);
        Assert.AreEqual(countAfterDispose, reader.ReadCount, "No read happens after Dispose.");

        // A second Dispose must not throw or hang.
        watcher.Dispose();
    }
}
