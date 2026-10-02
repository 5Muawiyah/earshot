using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// When the gauge's card is built ahead of the first click, on a fake clock, and how the taskbar watcher takes change events:
// all of it without a window.
[TestClass]
public sealed class CardPrewarmTests
{
    private static Action<Action> Inline => action => action();

    [TestMethod]
    public void NothingRunsUntilArmedAndNothingRunsBeforeTheDelay()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        using var prewarm = new CardPrewarm(time, Inline, () => runs++);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, runs, "Not armed: no timer, no run.");

        prewarm.Arm();
        time.Advance(CardPrewarm.Delay - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, runs, "Armed but the start-up settling time has not passed.");
    }

    [TestMethod]
    public void RunsOnceAfterTheDelayThroughTheUiPostAndNotAgain()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        int posts = 0;
        using var prewarm = new CardPrewarm(time, action => { posts++; action(); }, () => runs++);

        prewarm.Arm();
        prewarm.Arm();
        time.Advance(CardPrewarm.Delay);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.AreEqual(1, runs);
        Assert.AreEqual(1, posts, "The work goes to the UI thread through the given post.");
        Assert.AreEqual(0, time.LiveTimers, "No timer is left running once it has run.");
    }

    [TestMethod]
    public void DisposeBeforeTheDelayMeansItNeverRuns()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        var prewarm = new CardPrewarm(time, Inline, () => runs++);
        prewarm.Arm();
        prewarm.Dispose();

        time.Advance(TimeSpan.FromMinutes(1));
        prewarm.Arm();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.AreEqual(0, runs);
        Assert.AreEqual(0, time.LiveTimers);
    }

    // ---- BurstCoalescer, the no-storm rule for change events (pure)

    [TestMethod]
    public void ABurstOfNotificationsIsOneActionAfterTheWindow()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        using var coalescer = new BurstCoalescer(time, TimeSpan.FromMilliseconds(250), () => runs++);

        for (int i = 0; i < 500; i++)
        {
            coalescer.Notify();
        }

        Assert.AreEqual(0, runs, "Nothing runs while the window is open.");
        Assert.AreEqual(1, time.TimersCreated, "One burst, one timer, however many notifications.");
        time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.AreEqual(0, runs);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, runs, "The window ended: one action.");
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, runs, "No second action with no further notification.");
        Assert.AreEqual(0, time.LiveTimers, "No timer is held while nothing is notified.");
    }

    [TestMethod]
    public void ANotificationAfterTheWindowStartsTheNextBurstSoTheRateIsBounded()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        using var coalescer = new BurstCoalescer(time, TimeSpan.FromMilliseconds(250), () => runs++);

        // One notification every 10 ms for 10 s of steady movement: at most one action per 250 ms window.
        for (int i = 0; i < 1000; i++)
        {
            coalescer.Notify();
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.IsLessThanOrEqualTo(40, runs, "10 s of steady events cost at most 10 s / 250 ms actions.");
        Assert.IsGreaterThanOrEqualTo(39, runs, "And the burst is not starved: it acts about every window.");

        time.Advance(TimeSpan.FromSeconds(1));
        int settled = runs;
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(settled, runs, "Once the events stop, so do the actions.");
    }

    [TestMethod]
    public void TheLastNotificationOfABurstIsNeverLost()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        using var coalescer = new BurstCoalescer(time, TimeSpan.FromMilliseconds(250), () => runs++);

        coalescer.Notify();
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(1, runs);

        coalescer.Notify();
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(2, runs, "An event right after a window still gets its own action.");
    }

    [TestMethod]
    public void NotificationsAfterDisposeDoNothingAndHoldNoTimer()
    {
        var time = new TestTimeProvider();
        int runs = 0;
        var coalescer = new BurstCoalescer(time, TimeSpan.FromMilliseconds(250), () => runs++);
        coalescer.Notify();
        coalescer.Dispose();

        coalescer.Notify();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, runs);
        Assert.AreEqual(0, time.LiveTimers);
    }

    [TestMethod]
    public void ASourceWithNoLocationEventReportsItNotActiveSoTheFastPollStays()
    {
        IShellWindowChangeSource source = new NoLocationSource();

        Assert.IsFalse(source.TaskbarLocationEventsActive);
        source.TaskbarLocationChanged += (_, _) => { };
    }

    private sealed class NoLocationSource : IShellWindowChangeSource
    {
        public event EventHandler<ShellWindowChangedEventArgs>? ShellWindowChanged
        {
            add { }
            remove { }
        }

        public Earshot.Contracts.StepOutcome Install() => new("x", true, 0, "S_OK", null);

        public Earshot.Contracts.StepOutcome Reinstall() => Install();

        public void Dispose()
        {
        }
    }

    // ---- TaskbarWatcher: change events

    [TestMethod]
    public void AChangeEventTriggersAReadWithTheScheduledPollFarAway()
    {
        var reader = new FakeTaskbarReader();
        var time = new TestTimeProvider();
        using var watcher = new TaskbarWatcher(reader, () => null, _ => { }, Inline, new CapturingLog(), time, baselinePollIntervalMs: 600_000);
        watcher.Start();
        watcher.Poke();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 1, TimeSpan.FromSeconds(5)));

        watcher.NotifyChanged();
        time.Advance(TimeSpan.FromMilliseconds(TaskbarWatcher.EventCoalesceMs));

        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 2, TimeSpan.FromSeconds(5)), "The event read must not wait for the poll.");
    }

    [TestMethod]
    public void ABurstOfChangeEventsIsOneRead()
    {
        var reader = new FakeTaskbarReader();
        var time = new TestTimeProvider();
        using var watcher = new TaskbarWatcher(reader, () => null, _ => { }, Inline, new CapturingLog(), time, baselinePollIntervalMs: 600_000);
        watcher.Start();
        watcher.Poke();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 1, TimeSpan.FromSeconds(5)));

        for (int i = 0; i < 500; i++)
        {
            watcher.NotifyChanged();
        }

        Thread.Sleep(100);
        Assert.AreEqual(1, reader.ReadCount, "Nothing is read while the burst is still being gathered.");
        Assert.AreEqual(1, time.LiveTimers, "One burst, one timer, however many events.");

        time.Advance(TimeSpan.FromMilliseconds(TaskbarWatcher.EventCoalesceMs));
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 2, TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);
        Assert.AreEqual(2, reader.ReadCount, "The whole burst cost exactly one read.");
        Assert.AreEqual(0, time.LiveTimers);

        // A later event starts the next burst.
        watcher.NotifyChanged();
        time.Advance(TimeSpan.FromMilliseconds(TaskbarWatcher.EventCoalesceMs));
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 3, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void TheSlowSafetyPollStillReadsWithNoEvents()
    {
        var reader = new FakeTaskbarReader();
        using var watcher = new TaskbarWatcher(reader, () => null, _ => { }, Inline, new CapturingLog(), TimeProvider.System, baselinePollIntervalMs: 600_000);
        watcher.Start();
        watcher.Poke();
        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 1, TimeSpan.FromSeconds(5)));

        // The tray moves the baseline to the safety poll once the events are installed; here a short stand-in for 10 s.
        watcher.SetBaselinePollInterval(40);

        Assert.IsTrue(SpinWait.SpinUntil(() => reader.ReadCount >= 4, TimeSpan.FromSeconds(5)), "The poll alone keeps reading.");
    }

    [TestMethod]
    public void TheSafetyPollIsSlowerThanTheFastPollAndTheCoalesceWindowShorterThanBoth()
    {
        Assert.IsGreaterThan(TaskbarWatcher.ShownPollIntervalMs, TaskbarWatcher.SafetyPollIntervalMs);
        Assert.IsLessThan(TaskbarWatcher.ShownPollIntervalMs, TaskbarWatcher.EventCoalesceMs);
    }

    [TestMethod]
    public void ChangeEventsAfterDisposeDoNothingAndLeaveNoTimer()
    {
        var reader = new FakeTaskbarReader();
        var time = new TestTimeProvider();
        var watcher = new TaskbarWatcher(reader, () => null, _ => { }, Inline, new CapturingLog(), time, baselinePollIntervalMs: 600_000);
        watcher.Start();
        watcher.NotifyChanged();
        watcher.Dispose();

        watcher.NotifyChanged();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, time.LiveTimers);
    }
}
