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
// At rest the thread waits on an event and wakes for nothing: the event is set only while someone is subscribed. A frame is
// not posted while the last one has not reached the UI thread yet, so a busy UI thread is never handed a queue of them.
internal sealed class VBlankFrameClock : IFrameClock, IDisposable
{
    private static readonly TimeSpan Unpaced = TimeSpan.FromHours(1);

    private readonly Func<nint> _window;
    private readonly Action<Action> _uiPost;
    private readonly ILog _log;
    private readonly List<Action<TimeSpan>> _subscribers = [];
    private readonly ManualResetEventSlim _wanted = new(false);
    private readonly AutoResetEvent _delivered = new(false);
    private readonly IVBlankOutputs _outputs;
    private Thread? _thread;
    private int _posted;
    private volatile bool _disposed;
    private bool _failing;

    // window: the window whose display paces the frames, read on the clock's thread before every wait (0 while it has no
    // handle: the primary display's nearest output is used).
    // outputs: the display system (DXGI's, composed at the tray; a fake in tests).
    public VBlankFrameClock(Func<nint> window, Action<Action> uiPost, ILog log, IVBlankOutputs outputs)
    {
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

            bool paced = WaitForBlank();
            TimeSpan at = paced ? Now : Now + Unpaced;
            bool posted = false;
            if (_wanted.IsSet && !_disposed && Interlocked.CompareExchange(ref _posted, 1, 0) == 0)
            {
                posted = true;
                _uiPost(() => Deliver(at));
            }

            // With nothing to wait on, the thread waits for the frame it posted to arrive instead, which ends every motion.
            // Nothing posted (a frame still in flight, or nobody wanting one) means nothing will arrive to wait for.
            if (!paced && posted)
            {
                _delivered.WaitOne();
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

    // Waits for the next blank of the window's display. False when there was nothing to wait on.
    private bool WaitForBlank()
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
