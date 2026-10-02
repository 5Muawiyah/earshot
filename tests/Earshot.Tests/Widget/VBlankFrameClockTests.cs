using System.Collections.Concurrent;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real frame clock's thread logic, on a fake display system (IVBlankOutputs): the test says when a blank happens and when
// the UI thread runs what was posted to it. The waits are real threads, so each step waits on a condition, never on a sleep.
[TestClass]
public sealed class VBlankFrameClockTests
{
    private const int Failed = unchecked((int)0x80004005);

    // A display system whose blank happens when the test releases it. Outputs are listed only from Available.
    private sealed class FakeOutputs : IVBlankOutputs
    {
        private int _blanks;
        private readonly HashSet<nint> _listed = [];
        private readonly object _lock = new();
        private int _waits;
        private int _lists;
        private int _releases;
        private int _threadConfigs;

        public HashSet<nint> Available { get; } = [1];

        // The monitor the window is on, changed by the test.
        public nint Monitor { get; set; } = 1;

        // What the next blank returns.
        public int NextResult { get; set; }

        public List<nint> Waited { get; } = [];

        public int Waits => Volatile.Read(ref _waits);

        public int Lists => Volatile.Read(ref _lists);

        public int Releases => Volatile.Read(ref _releases);

        public int ThreadConfigs => Volatile.Read(ref _threadConfigs);

        public void Blank()
        {
            lock (_lock)
            {
                _blanks++;
                System.Threading.Monitor.PulseAll(_lock);
            }
        }

        public void ConfigureThread(Thread thread) => Interlocked.Increment(ref _threadConfigs);

        public nint MonitorFor(nint window) => Monitor;

        public bool Has(nint monitor)
        {
            lock (_lock)
            {
                return _listed.Contains(monitor);
            }
        }

        public int ListOutputs()
        {
            Interlocked.Increment(ref _lists);
            lock (_lock)
            {
                _listed.Clear();
                _listed.UnionWith(Available);
            }

            return 0;
        }

        public int WaitForVBlank(nint monitor)
        {
            lock (_lock)
            {
                Waited.Add(monitor);
            }

            Interlocked.Increment(ref _waits);
            lock (_lock)
            {
                // Ten seconds is a test that forgot to blank, not a display.
                DateTime end = DateTime.UtcNow.AddSeconds(10);
                while (_blanks == 0)
                {
                    TimeSpan left = end - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !System.Threading.Monitor.Wait(_lock, left))
                    {
                        return Failed;
                    }
                }

                _blanks--;
                return NextResult;
            }
        }

        public void Release()
        {
            Interlocked.Increment(ref _releases);
            lock (_lock)
            {
                _listed.Clear();
            }
        }
    }

    // The UI thread: what the clock posts is kept until the test runs it.
    private sealed class FakeUi
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public void Post(Action action)
        {
            _queue.Enqueue(action);
            Interlocked.Increment(ref _posted);
        }

        // One turn of the UI thread: what was posted before it began, and no more. Actions posted while it runs (the unpaced clock
        // thread posts its next frame the moment a frame is delivered) wait for the next turn, as a real message loop's would.
        public void Run()
        {
            int waiting = _queue.Count;
            for (int i = 0; i < waiting && _queue.TryDequeue(out Action? action); i++)
            {
                action();
            }
        }
    }

    private readonly FakeOutputs _outputs = new();
    private readonly FakeUi _ui = new();
    private readonly CapturingLog _log = new();
    private readonly List<TimeSpan> _frames = [];

    private VBlankFrameClock NewClock(Func<nint>? window = null) => new(window ?? (() => 7), _ui.Post, _log, _outputs);

    private static void WaitUntil(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < end, "The clock's thread did not reach the state the test waits for.");
            Thread.Sleep(1);
        }
    }

    // One blank: released, and the thread seen to come round to its next wait.
    private void BlankAndWaitForNext()
    {
        int before = _outputs.Waits;
        _outputs.Blank();
        WaitUntil(() => _outputs.Waits > before);
    }

    private int WarnCount() => _log.Entries.Count(e => e.Level == LogLevel.Warn);

    [TestMethod]
    public void TheClockThreadIsConfiguredOnceByTheOutputs()
    {
        using VBlankFrameClock clock = NewClock();
        using IDisposable a = clock.Subscribe(_frames.Add);
        using IDisposable b = clock.Subscribe(_frames.Add);

        WaitUntil(() => _outputs.Waits >= 1);
        Assert.AreEqual(1, _outputs.ThreadConfigs);
    }

    [TestMethod]
    public void OneFrameIsInFlightAtATimeWhateverTheDisplayDoes()
    {
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);

        BlankAndWaitForNext();
        BlankAndWaitForNext();
        BlankAndWaitForNext();
        Assert.AreEqual(1, _ui.Posted, "Three blanks with the UI thread busy are one frame, not a queue of three.");

        _ui.Run();
        Assert.HasCount(1, _frames);

        BlankAndWaitForNext();
        Assert.AreEqual(2, _ui.Posted, "Once the frame has arrived the next blank posts the next.");
    }

    [TestMethod]
    public void AFrameIsStampedWithTheTimeTheWaitReturned()
    {
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);

        TimeSpan before = clock.Now;
        BlankAndWaitForNext();
        _ui.Run();

        Assert.HasCount(1, _frames);
        Assert.IsTrue(_frames[0] >= before && _frames[0] <= clock.Now, "A paced frame is stamped with the blank's own time.");
    }

    [TestMethod]
    public void WithNoOutputTheFramesAreStampedAnHourAheadAndTheFailureIsLoggedWithItsCode()
    {
        _outputs.Available.Clear();
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);

        WaitUntil(() => _ui.Posted == 1);
        TimeSpan now = clock.Now;
        _ui.Run();

        Assert.HasCount(1, _frames);
        Assert.IsTrue(_frames[0] > now + TimeSpan.FromMinutes(59), "An unpaced frame ends every motion in one step.");
        Assert.IsTrue(_log.Has(LogLevel.Warn, "0x887A0002"), "The raw HRESULT is in the log.");
    }

    // The race that made the test above flaky, forced: delivering a frame lets the unpaced clock thread post its next one at once, so a
    // UI thread that kept draining the queue would deliver that one too and a single run would deliver several. One run delivers what
    // was posted before it began, and the next frame waits for the next run.
    [TestMethod]
    public void OneRunOfTheUiThreadDeliversOnlyTheFramesPostedBeforeIt()
    {
        _outputs.Available.Clear();
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(at =>
        {
            _frames.Add(at);

            // Held inside the first delivery until the clock thread has posted the next frame, which is the interleaving.
            if (_frames.Count == 1)
            {
                WaitUntil(() => _ui.Posted == 2);
            }
        });

        WaitUntil(() => _ui.Posted == 1);
        _ui.Run();

        Assert.HasCount(1, _frames, "One run, one frame, though the next was posted while it ran.");
        Assert.AreEqual(2, _ui.Posted, "The next frame is posted and waits.");
        _ui.Run();
        Assert.HasCount(2, _frames, "And the next run delivers it.");
    }

    [TestMethod]
    public void AnUnpacedClockWaitsForTheFrameItPostedThenPostsAgain()
    {
        _outputs.Available.Clear();
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);

        WaitUntil(() => _ui.Posted == 1);
        Thread.Sleep(30);
        Assert.AreEqual(1, _ui.Posted, "The thread waits for its frame to arrive instead of spinning.");

        _ui.Run();
        WaitUntil(() => _ui.Posted == 2);
    }

    // The unpaced failure path when nothing was posted (nobody wants a frame any more): the thread must not wait for a frame
    // that was never sent, or a later Subscribe never gets one.
    [TestMethod]
    public void AnUnpacedFailureWithNothingPostedDoesNotStickTheThread()
    {
        using VBlankFrameClock clock = NewClock();
        IDisposable first = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);

        // The only subscriber leaves while the thread is waiting, and then the wait fails: nothing will be posted.
        first.Dispose();
        _outputs.NextResult = Failed;
        _outputs.Blank();
        WaitUntil(() => _outputs.Releases >= 1);
        Assert.AreEqual(0, _ui.Posted);

        _outputs.Available.Clear();
        using IDisposable second = clock.Subscribe(_frames.Add);

        WaitUntil(() => _ui.Posted == 1);
        _ui.Run();
        Assert.HasCount(1, _frames, "A later subscriber is given frames.");
    }

    [TestMethod]
    public void DisposingWhileWaitingForABlankPostsNothingAndEndsTheThread()
    {
        VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);

        clock.Dispose();
        _outputs.Blank();

        WaitUntil(() => _outputs.Releases >= 1);
        Assert.AreEqual(0, _ui.Posted, "A disposed clock posts no frame, even for the blank it was waiting on.");
    }

    [TestMethod]
    public void DisposingWhileWaitingForAnUnpacedFrameEndsTheThread()
    {
        _outputs.Available.Clear();
        VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _ui.Posted == 1);

        clock.Dispose();

        WaitUntil(() => _outputs.Releases >= 1);
    }

    [TestMethod]
    public void ADisposedClockLetsItsSubscriptionsAndItsLateFramesGoWithoutThrowing()
    {
        VBlankFrameClock clock = NewClock();
        IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);
        BlankAndWaitForNext();

        clock.Dispose();
        clock.Dispose();
        sub.Dispose();
        _outputs.Blank();
        WaitUntil(() => _outputs.Releases >= 1);

        _ui.Run();
        Assert.IsEmpty(_frames, "A frame that reaches the UI thread after disposal is dropped.");
        using IDisposable late = clock.Subscribe(_frames.Add);
        Assert.AreEqual(1, _outputs.ThreadConfigs, "A subscription after disposal starts nothing.");
    }

    [TestMethod]
    public void AClockThatWasNeverSubscribedDisposesWithoutAThread()
    {
        VBlankFrameClock clock = NewClock();

        clock.Dispose();

        Assert.AreEqual(0, _outputs.ThreadConfigs);
    }

    [TestMethod]
    public void OutputsAreListedAgainWhenTheWindowMovesToAnotherDisplay()
    {
        _outputs.Available.Add(2);
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);
        Assert.AreEqual(1, _outputs.Lists, "The first wait lists the outputs.");

        BlankAndWaitForNext();
        Assert.AreEqual(1, _outputs.Lists, "A window that stays put does not list them again.");

        _ui.Run();
        _outputs.Available.Add(3);
        _outputs.Monitor = 3;
        BlankAndWaitForNext();

        Assert.AreEqual(2, _outputs.Lists, "A monitor that is not among the listed outputs lists them again.");
        CollectionAssert.AreEqual(new nint[] { 1, 1, 3 }, _outputs.Waited.ToArray());
    }

    [TestMethod]
    public void TheWindowIsReadOnTheClockThreadBeforeEveryWait()
    {
        int calls = 0;
        int callingThread = -1;
        nint handle = 5;
        using VBlankFrameClock clock = NewClock(() =>
        {
            Interlocked.Increment(ref calls);
            callingThread = Environment.CurrentManagedThreadId;
            return handle;
        });
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);
        int afterSubscribe = Volatile.Read(ref calls);

        BlankAndWaitForNext();
        BlankAndWaitForNext();

        Assert.AreNotEqual(Environment.CurrentManagedThreadId, callingThread, "A handle read at subscription goes stale; the thread reads it afresh.");
        Assert.IsTrue(Volatile.Read(ref calls) >= afterSubscribe + 2, "One read for each wait.");
    }

    [TestMethod]
    public void AFailureRunIsLoggedOnceAndALaterRunIsLoggedAgainAfterASuccess()
    {
        using VBlankFrameClock clock = NewClock();
        using IDisposable sub = clock.Subscribe(_frames.Add);
        WaitUntil(() => _outputs.Waits >= 1);

        // Two failed waits in a row: one log line.
        _outputs.NextResult = Failed;
        _outputs.Blank();
        WaitUntil(() => _ui.Posted == 1);
        _ui.Run();
        WaitUntil(() => _outputs.Waits >= 2);
        _outputs.Blank();
        WaitUntil(() => _ui.Posted == 2);
        _ui.Run();
        WaitUntil(() => _outputs.Waits >= 3);
        Assert.AreEqual(1, WarnCount(), "A run of failures is one line, with the code of the first.");

        // A blank that works ends the run.
        _outputs.NextResult = 0;
        BlankAndWaitForNext();
        _ui.Run();
        Assert.AreEqual(1, WarnCount());

        // The next failure is a new run.
        _outputs.NextResult = Failed;
        _outputs.Blank();
        WaitUntil(() => _ui.Posted >= 4);
        Assert.AreEqual(2, WarnCount(), "A failure after a success is logged again.");
        _ui.Run();
    }
}
