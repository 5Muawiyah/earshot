using System.Diagnostics;
using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// The real frame clock: one frame for each vertical blank of the display the window is on, at that display's own rate (the
// owner's two displays run at 300 Hz), never a fixed timer.
//
// How it is paced, and why. A dedicated background thread calls IDXGIOutput::WaitForVBlank on the DXGI output whose monitor
// is the window's (MonitorFromWindow, asked again before every wait, so a window moved to another display follows that
// display's rate), and after each blank posts one frame to the UI thread, stamped with the time the wait returned
// (Stopwatch, QueryPerformanceCounter). The options were:
//   - DirectComposition: would mean moving the card's drawing into a visual tree; the card and the gauge draw with GDI+ into
//     their own buffers (the v1.3.0 flicker fixes rest on that), so it is the larger change for no gain in pacing here.
//   - DwmFlush on a thread: one call, documented, but it waits for the next desktop composition rather than for a given
//     display's blank, and the page does not say what it does on a desktop with displays at different rates.
//     https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush
//   - IDXGIOutput::WaitForVBlank: documented, waits for the blank of one named output, so each window is paced to the display
//     it is on. Chosen.
//     https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-waitforvblank
// The rate is never read or assumed: the waits are the rate. When no output can be waited on (DXGI refuses, or the window's
// monitor is on no output, as in some remote sessions) the raw HRESULT is logged once and frames are stamped an hour ahead,
// so every motion reaches its end in one frame, as with animation effects off: nothing is paced to a guessed rate.
//
// A display that is off can break WaitForVBlank in two ways. The first was observed on the owner's PC; the second is a case the code
// guards against and has not been observed. Neither may drive the animation:
//   - The wait returns at once with success, tens of thousands of times a second. A wait that returns in under MinRealWait (half a
//     millisecond: the period of a display at 2000 Hz, and a wait chained straight after the last one lasts about one period) three
//     times running is not a blank. On the owner's PC the whole frame cycle of such a run took about 27 microseconds (164,883 ms over
//     6,074,765 frames), an upper bound on the wait itself. The clock logs it once, says the display is probably off, and draws
//     unpaced (frames stamped an hour ahead: each motion ends in one frame, and no more frames are posted than the UI thread
//     delivers). It listens for the display's return by chaining three waits at the start of each run: when all three last a real
//     period it is paced again.
//   - The wait does not return. Never seen: a 30 s timeout could not tell a hang from a flood of instant returns. A wait that has not
//     returned within WatchdogAfter (100 ms: several refresh periods of any display that could be asked to animate) is taken to be
//     stuck. A watchdog timer then delivers unpaced frames itself, logged once, and the stuck thread is left where it is: it holds up
//     nothing, not a subscriber, not Dispose. Paced frames resume when its wait returns.
// At rest the thread waits on an event and wakes for nothing: the event is set only while someone is subscribed. A frame is
// not posted while the last one has not reached the UI thread yet, so a busy UI thread is never handed a queue of them.
internal sealed class VBlankFrameClock : IFrameClock, IDisposable
{
    private static readonly TimeSpan Unpaced = TimeSpan.FromHours(1);

    // A wait shorter than this is not a vertical blank (see the notes above).
    internal static readonly TimeSpan DefaultMinRealWait = TimeSpan.FromMilliseconds(0.5);

    // A wait longer than this is stuck.
    internal static readonly TimeSpan DefaultWatchdogAfter = TimeSpan.FromMilliseconds(100);

    // Instant waits in a row before the display is taken to be off, and real waits in a row before it is taken to be back.
    private const int FastWaitsToGiveUp = 3;
    private const int RealWaitsToRecover = 3;

    private readonly Func<nint> _window;
    private readonly Action<Action> _uiPost;
    private readonly ILog _log;
    private readonly List<Action<TimeSpan>> _subscribers = [];
    private readonly ManualResetEventSlim _wanted = new(false);
    private readonly AutoResetEvent _delivered = new(false);
    private readonly IVBlankOutputs _outputs;
    private readonly TimeSpan _minRealWait;
    private readonly TimeSpan _watchdogAfter;
    private System.Threading.Timer? _watchdog;
    private Thread? _thread;
    private int _posted;
    private volatile bool _disposed;
    private bool _failing;

    // Thread state: instant waits in a row, and whether they have made the display count as off.
    private int _fastWaits;
    private bool _displayOff;

    // Stopwatch ticks at which the wait now under way began, or 0 when the thread is not inside one. Read by the watchdog.
    private long _waitStarted;

    // Whether the watchdog has delivered frames since a paced one last arrived, so it logs once per run of them.
    private int _watchdogFired;

    // window: the window whose display paces the frames, read on the clock's thread before every wait (0 while it has no
    // handle: the primary display's nearest output is used).
    // outputs: the display system (DXGI's, composed at the tray; a fake in tests).
    // minRealWait and watchdogAfter: how short a wait is no blank and how long a wait is stuck (the defaults; a test of the rest of the
    // clock turns the first off with TimeSpan.Zero, since its fake waits end the moment the test says).
    public VBlankFrameClock(
        Func<nint> window, Action<Action> uiPost, ILog log, IVBlankOutputs outputs, TimeSpan? minRealWait = null, TimeSpan? watchdogAfter = null)
    {
        _minRealWait = minRealWait ?? DefaultMinRealWait;
        _watchdogAfter = watchdogAfter ?? DefaultWatchdogAfter;
        ArgumentNullException.ThrowIfNull(outputs);
        _outputs = outputs;
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(log);
        _window = window;
        _uiPost = uiPost;
        _log = log;
    }

    public TimeSpan Now => Stopwatch.GetElapsedTime(0);

    public IDisposable Subscribe(Action<TimeSpan> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        _subscribers.Add(onFrame);
        if (_subscribers.Count == 1 && !_disposed)
        {
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Earshot frame clock" };
                _outputs.ConfigureThread(_thread);
                _thread.Start();
            }

            _wanted.Set();
            StartWatchdog();
        }

        return new Subscription(this, onFrame);
    }

    // UI thread. The thread, once started, ends on seeing the flag and disposes the events itself (it may be waiting on them);
    // with no thread started they are disposed here.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscribers.Clear();
        StopWatchdog();
        _watchdog?.Dispose();
        if (_thread is null)
        {
            _wanted.Dispose();
            _delivered.Dispose();
            return;
        }

        _wanted.Set();
        _delivered.Set();
    }

    private void Unsubscribe(Action<TimeSpan> onFrame)
    {
        if (_subscribers.Remove(onFrame) && _subscribers.Count == 0 && !_disposed)
        {
            _wanted.Reset();
            StopWatchdog();
        }
    }

    private void Run()
    {
        while (true)
        {
            _wanted.Wait();
            if (_disposed)
            {
                _outputs.Release();
                _wanted.Dispose();
                _delivered.Dispose();
                return;
            }

            bool paced = PacedWait();
            if (paced)
            {
                Interlocked.Exchange(ref _watchdogFired, 0);
            }

            TimeSpan at = paced ? Now : Now + Unpaced;
            bool posted = false;
            if (_wanted.IsSet && !_disposed && Interlocked.CompareExchange(ref _posted, 1, 0) == 0)
            {
                posted = true;
                _uiPost(() => Deliver(at));
            }

            // With nothing to wait on, the thread waits for the frame to arrive instead of asking again, which ends every motion: the
            // frame it posted, or one still in flight from before (asking again at once would spin while the UI thread is busy). Nobody
            // wanting a frame means none will arrive, and it does not wait. The wait is bounded, so a frame that goes missing cannot
            // stick the thread.
            if (!paced && (posted || (_wanted.IsSet && Volatile.Read(ref _posted) == 1)))
            {
                _ = _delivered.WaitOne(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    // UI thread.
    private void Deliver(TimeSpan at)
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _posted, 0);
        _delivered.Set();

        foreach (Action<TimeSpan> subscriber in _subscribers.ToArray())
        {
            if (_subscribers.Contains(subscriber))
            {
                subscriber(at);
            }
        }
    }

    // One wait for a blank, judged: true only when a real blank came. A display that is off returns at once (see the notes at the top).
    private bool PacedWait()
    {
        if (_displayOff)
        {
            return DisplayHasReturned();
        }

        TimeSpan took = TimeSpan.Zero;
        if (!TimedWaitForBlank(ref took))
        {
            return false;
        }

        if (_minRealWait <= TimeSpan.Zero || took >= _minRealWait)
        {
            _fastWaits = 0;
            return true;
        }

        // A stray short wait is still a frame; the third in a row is a display that is off.
        if (++_fastWaits < FastWaitsToGiveUp)
        {
            return true;
        }

        _displayOff = true;
        _log.Warn("Motion: IDXGIOutput::WaitForVBlank returned in " + (took.TotalMilliseconds * 1000).ToString("F0", CultureInfo.InvariantCulture)
            + " microseconds, " + FastWaitsToGiveUp + " times running, so the display is probably off; this motion ends in one frame until its waits last a refresh again.");
        return false;
    }

    // The display was taken to be off: three waits chained one after the other. A display that is off returns from the first at once;
    // one that is back takes about a refresh for each.
    private bool DisplayHasReturned()
    {
        for (int i = 0; i < RealWaitsToRecover; i++)
        {
            TimeSpan took = TimeSpan.Zero;
            if (!TimedWaitForBlank(ref took) || took < _minRealWait)
            {
                return false;
            }
        }

        _displayOff = false;
        _fastWaits = 0;
        _log.Write(LogLevel.Debug, "Motion: IDXGIOutput::WaitForVBlank lasts a refresh again, so motion is paced to the display again.");
        return true;
    }

    // WaitForBlank, with how long the wait itself took, and the moment it began on record for the watchdog while it lasts.
    private bool TimedWaitForBlank(ref TimeSpan took)
    {
        long before = Stopwatch.GetTimestamp();
        bool waited = WaitForBlank(() => Volatile.Write(ref _waitStarted, before));
        Volatile.Write(ref _waitStarted, 0);
        took = Stopwatch.GetElapsedTime(before);
        return waited;
    }

    // The watchdog: while someone is subscribed, a timer looks every half bound at whether the thread has been inside one wait for
    // longer than the bound. A wait that long is stuck (the display is off and the call does not return), so the timer delivers an
    // unpaced frame itself, which ends the motion, and logs it once per run of such frames. It never touches the stuck thread.
    private void StartWatchdog()
    {
        if (_watchdogAfter <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan period = _watchdogAfter / 2;
        _watchdog ??= new System.Threading.Timer(_ => OnWatchdog(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watchdog.Change(period, period);
    }

    private void StopWatchdog() => _watchdog?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void OnWatchdog()
    {
        long started = Volatile.Read(ref _waitStarted);
        if (_disposed || !_wanted.IsSet || started == 0 || Stopwatch.GetElapsedTime(started) < _watchdogAfter)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _posted, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _watchdogFired, 1) == 0)
        {
            _log.Warn("Motion: IDXGIOutput::WaitForVBlank has not returned in " + _watchdogAfter.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)
                + " ms, so the display is probably off; this motion ends in one frame, and is paced again when the wait returns.");
        }

        TimeSpan at = Now + Unpaced;
        _uiPost(() => Deliver(at));
    }

    // Waits for the next blank of the window's display. False when there was nothing to wait on. beginWait is called just before the
    // wait itself (not before the listing of outputs), for the watchdog.
    private bool WaitForBlank(Action beginWait)
    {
        nint monitor = _outputs.MonitorFor(_window());
        if (!_outputs.Has(monitor))
        {
            // Displays come and go: the outputs are listed again whenever a monitor is not among them.
            int listed = _outputs.ListOutputs();
            if (listed < 0 || !_outputs.Has(monitor))
            {
                Problem(listed < 0 ? listed : Dxgi.DXGI_ERROR_NOT_FOUND, "no DXGI output shows the window's display");
                return false;
            }
        }

        beginWait();
        int hr = _outputs.WaitForVBlank(monitor);
        if (hr < 0)
        {
            Problem(hr, "IDXGIOutput::WaitForVBlank failed");
            _outputs.Release();
            return false;
        }

        _failing = false;
        return true;
    }

    // A run of failures is logged once, with the raw code of its first; the next success ends the run, so a later run is logged again.
    private void Problem(int hr, string what)
    {
        if (_failing)
        {
            return;
        }

        _failing = true;
        _log.Warn("Motion: " + what + " (HRESULT 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + "), so this motion ends in one frame.");
    }

    private sealed class Subscription(VBlankFrameClock clock, Action<TimeSpan> onFrame) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                clock.Unsubscribe(onFrame);
            }
        }
    }
}
