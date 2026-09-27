using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

internal sealed class FakeTaskbarReader : ITaskbarReader
{
    private readonly Lock _gate = new();
    private ITaskbarReader.Result _next = ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, new Earshot.Contracts.StepOutcome("fake", false, 0, "S_OK", null)));

    // Optional: lets a test hold a read open, e.g. to dispose the watcher while one is still in flight.
    // Null (the default) never blocks, matching every existing test's expectations.
    private readonly ManualResetEventSlim? _readStarted;
    private readonly ManualResetEventSlim? _releaseRead;

    public FakeTaskbarReader()
    {
    }

    public FakeTaskbarReader(ManualResetEventSlim readStarted, ManualResetEventSlim releaseRead)
    {
        _readStarted = readStarted;
        _releaseRead = releaseRead;
    }

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
        _readStarted?.Set();
        _releaseRead?.Wait();

        lock (_gate)
        {
            ReadCount++;
            LastShownGauge = shownGauge;
            return _next;
        }
    }
}

// A reader slow enough to trip TaskbarWatcher's own slow-read back-off (mean of the last 20 reads over
// 20 ms) until GoFast() is called, for ResetBackoffReturnsTheDoubledIntervalToTheShownBaseline.
internal sealed class SlowThenFastTaskbarReader : ITaskbarReader
{
    private static readonly ITaskbarReader.Result Failure =
        ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, new Earshot.Contracts.StepOutcome("fake", false, 0, "S_OK", null)));

    private readonly TimeSpan _delay;
    private volatile bool _slow = true;

    public SlowThenFastTaskbarReader(TimeSpan delay) => _delay = delay;

    public void GoFast() => _slow = false;

    public ITaskbarReader.Result Read(ShownGauge? shownGauge)
    {
        if (_slow)
        {
            Thread.Sleep(_delay);
        }

        return Failure;
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

    // Dispose joins the worker thread for at most 500 ms then, on the old code, disposed the poke and stop
    // wait handles regardless of whether the thread had actually exited. A read still in flight past that
    // budget meant the worker thread went on to wait on a handle Dispose had already disposed, throwing
    // ObjectDisposedException on a background thread with nothing to catch it: on the old code this crashes
    // the process it runs in, which is exactly what a security review reported happening to a test host.
    // The fix moves disposal of the handles onto the worker thread itself (a finally block at the end of
    // Loop), so the UI thread's Dispose call never races the worker for them.
    [TestMethod]
    public void DisposeDuringAReadStillInFlightDoesNotThrow()
    {
        using var readStarted = new ManualResetEventSlim(false);
        using var releaseRead = new ManualResetEventSlim(false);
        var reader = new FakeTaskbarReader(readStarted, releaseRead);
        var log = new CapturingLog();
        var watcher = new TaskbarWatcher(reader, () => null, _ => { }, ImmediateUiPost, log, TimeProvider.System)
        {
            PollIntervalMs = 20,
        };

        watcher.Start();
        Assert.IsTrue(readStarted.Wait(TimeSpan.FromSeconds(5)), "The fake read never started.");

        // Dispose runs on its own thread so the read can still be released from here once Dispose's 500 ms
        // join budget has had time to elapse (the read is still blocked at that point).
        Exception? disposeThrew = null;
        var disposeThread = new Thread(() =>
        {
            try
            {
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                disposeThrew = ex;
            }
        });
        disposeThread.Start();
        Assert.IsTrue(disposeThread.Join(TimeSpan.FromSeconds(5)), "Dispose must return even while a read is still in flight.");
        Assert.IsNull(disposeThrew, "Dispose itself must not throw.");

        // Only now does the blocked read return, deep inside the worker thread's loop, exactly where the
        // old code would go on to touch a wait handle Dispose had already disposed.
        releaseRead.Set();

        // If the worker thread throws an unhandled exception here, it takes the whole test process down;
        // reaching this line at all is part of the proof. A short wait lets the worker actually finish its
        // final iteration and dispose its own handles before the test ends.
        Thread.Sleep(200);

        // Safe, and still no throw, whether or not the worker had already finished.
        watcher.Dispose();
    }

    // A read already in flight when Dispose is called is not interrupted (UI Automation gives no way to
    // cancel one, per Dispose's own comment): it goes on to complete, deep inside Loop, possibly well after
    // Dispose has already returned to its caller. The old code still posted that stale result to the UI
    // thread regardless, which OnTaskbarLayout would then apply to state the same Dispose call may already
    // be tearing down. Loop must check the stop signal again right before posting, and skip the post
    // entirely once it is set.
    [TestMethod]
    public void NoResultIsPostedToTheUiThreadOnceDisposeHasBeenCalled()
    {
        using var readStarted = new ManualResetEventSlim(false);
        using var releaseRead = new ManualResetEventSlim(false);
        var reader = new FakeTaskbarReader(readStarted, releaseRead);
        var log = new CapturingLog();
        int postCallsAfterDispose = 0;
        bool disposed = false;
        var watcher = new TaskbarWatcher(reader, () => null, _ => { },
            action =>
            {
                if (disposed)
                {
                    Interlocked.Increment(ref postCallsAfterDispose);
                }

                action();
            },
            log, TimeProvider.System)
        {
            PollIntervalMs = 20,
        };

        watcher.Start();
        Assert.IsTrue(readStarted.Wait(TimeSpan.FromSeconds(5)), "The fake read never started.");

        var disposeThread = new Thread(() => watcher.Dispose());
        disposeThread.Start();
        Assert.IsTrue(disposeThread.Join(TimeSpan.FromSeconds(5)), "Dispose must return even while a read is still in flight.");
        disposed = true;

        // Only now does the blocked read return, deep inside the worker thread's loop, after Dispose has
        // already returned: exactly the window the old code posted a stale result through regardless.
        releaseRead.Set();
        Thread.Sleep(200);

        Assert.AreEqual(0, postCallsAfterDispose, "No result may reach uiPost once Dispose has been called.");
    }

    // PollIntervalMs's own doc comment already says it is "reset after a poke source such as
    // TaskbarCreated, WM_SETTINGCHANGE or WM_DISPLAYCHANGE": TrayContext wires exactly that, but nothing
    // ever exercised the reset itself until now. A poll interval doubled by a real run of slow reads is
    // stale once Explorer (and its taskbar) is new, so ResetBackoff must clear it back to the baseline
    // rather than leaving the gauge polling twice as slowly as production ever intended after that.
    [TestMethod]
    public void ResetBackoffReturnsTheDoubledIntervalToTheShownBaseline()
    {
        var reader = new SlowThenFastTaskbarReader(TimeSpan.FromMilliseconds(25));
        var log = new CapturingLog();
        using var watcher = new TaskbarWatcher(reader, () => null, _ => { }, ImmediateUiPost, log, TimeProvider.System)
        {
            PollIntervalMs = 1,
        };

        // Started at 1 ms (not ShownPollIntervalMs) so the 20 reads doubling needs happen fast: doubling
        // multiplies whatever the interval currently is, so watch for it changing away from 1, not for it
        // exceeding the baseline.
        watcher.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => watcher.PollIntervalMs != 1, TimeSpan.FromSeconds(5)),
            "Sanity: enough slow reads must double the poll interval.");

        // Fast from here on, so nothing but the reset itself can explain the interval going back down.
        reader.GoFast();
        watcher.ResetBackoff();

        Assert.IsTrue(SpinWait.SpinUntil(() => watcher.PollIntervalMs == TaskbarWatcher.ShownPollIntervalMs, TimeSpan.FromSeconds(5)),
            "ResetBackoff must return the poll interval to the shown baseline.");

        Thread.Sleep(200);
        Assert.AreEqual(TaskbarWatcher.ShownPollIntervalMs, watcher.PollIntervalMs, "Fast reads must not double it again.");
    }
}
