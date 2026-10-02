using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private readonly Dictionary<nint, IDXGIOutput> _outputs = [];
    private Thread? _thread;
    private nint _hwnd;
    private int _posted;
    private volatile bool _disposed;
    private int _lastProblem;

    // window: the window whose display paces the frames, read on the UI thread at each subscription (0 while it has no
    // handle: the primary display's nearest output is used).
    public VBlankFrameClock(Func<nint> window, Action<Action> uiPost, ILog log)
    {
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
        Volatile.Write(ref _hwnd, _window());
        if (_subscribers.Count == 1 && !_disposed)
        {
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Earshot frame clock" };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }

            _wanted.Set();
        }

        return new Subscription(this, onFrame);
    }

    public void Dispose()
    {
        _disposed = true;
        _subscribers.Clear();
        _wanted.Set();
        _delivered.Set();
    }

    private void Unsubscribe(Action<TimeSpan> onFrame)
    {
        if (_subscribers.Remove(onFrame) && _subscribers.Count == 0)
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
                ReleaseOutputs();
                return;
            }

            bool paced = WaitForBlank();
            TimeSpan at = paced ? Now : Now + Unpaced;
            if (_wanted.IsSet && Interlocked.CompareExchange(ref _posted, 1, 0) == 0)
            {
                _uiPost(() => Deliver(at));
            }

            // With nothing to wait on, the thread waits for the frame it posted to arrive instead, which ends every motion.
            if (!paced)
            {
                _delivered.WaitOne();
            }
        }
    }

    // UI thread.
    private void Deliver(TimeSpan at)
    {
        Volatile.Write(ref _posted, 0);
        _delivered.Set();
        if (_disposed)
        {
            return;
        }

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
        nint monitor = Shell.MonitorFromWindow(Volatile.Read(ref _hwnd), Shell.MONITOR_DEFAULTTONEAREST);
        if (!_outputs.TryGetValue(monitor, out IDXGIOutput? output))
        {
            // Displays come and go: the outputs are listed again whenever a monitor is not among them.
            int listed = ListOutputs();
            if (listed < 0 || !_outputs.TryGetValue(monitor, out output))
            {
                Problem(listed < 0 ? listed : Dxgi.DXGI_ERROR_NOT_FOUND, "no DXGI output shows the window's display");
                return false;
            }
        }

        int hr = output.WaitForVBlank();
        if (hr < 0)
        {
            Problem(hr, "IDXGIOutput::WaitForVBlank failed");
            ReleaseOutputs();
            return false;
        }

        return true;
    }

    // Every output of every adapter, by its monitor. Returns the first failing HRESULT, or 0.
    private int ListOutputs()
    {
        ReleaseOutputs();
        int hr = Dxgi.CreateFactory(out IDXGIFactory1? factory);
        if (hr < 0 || factory is null)
        {
            return hr < 0 ? hr : ComActivation.E_POINTER;
        }

        try
        {
            for (uint a = 0; ; a++)
            {
                hr = factory.EnumAdapters(a, out nint adapterPointer);
                if (hr == Dxgi.DXGI_ERROR_NOT_FOUND)
                {
                    return 0;
                }

                hr = ComActivation.TakeInterface(hr, adapterPointer, out IDXGIAdapter? adapter);
                if (hr < 0 || adapter is null)
                {
                    return hr < 0 ? hr : ComActivation.E_POINTER;
                }

                try
                {
                    for (uint o = 0; ; o++)
                    {
                        hr = adapter.EnumOutputs(o, out nint outputPointer);
                        if (hr == Dxgi.DXGI_ERROR_NOT_FOUND)
                        {
                            break;
                        }

                        hr = ComActivation.TakeInterface(hr, outputPointer, out IDXGIOutput? output);
                        if (hr < 0 || output is null)
                        {
                            return hr < 0 ? hr : ComActivation.E_POINTER;
                        }

                        hr = output.GetDesc(out DXGI_OUTPUT_DESC desc);
                        if (hr < 0 || !_outputs.TryAdd(desc.Monitor, output))
                        {
                            Marshal.ReleaseComObject(output);
                            if (hr < 0)
                            {
                                return hr;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private void ReleaseOutputs()
    {
        foreach (IDXGIOutput output in _outputs.Values)
        {
            Marshal.ReleaseComObject(output);
        }

        _outputs.Clear();
    }

    // Each distinct failure is logged once, with its raw code.
    private void Problem(int hr, string what)
    {
        if (hr == _lastProblem)
        {
            return;
        }

        _lastProblem = hr;
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
