using Earshot.Contracts;

namespace Earshot.Widget;

// Owns the UIA worker thread: waits for a poke or the poll interval, reads once, posts the result to the
// UI thread. Pokes coalesce (an AutoResetEvent, not a counting one): a poke during a read schedules
// exactly one more read. The thread is MTA (UI Automation documents that its calls must come from a
// thread that owns no window, in a multithreaded apartment) and is set up before it starts, since the
// apartment state cannot change afterwards.
internal sealed class TaskbarWatcher : IDisposable
{
    // Design choice: 1 s, doubled once if the mean of the last 20 reads exceeds 20 ms. A slower rate while
    // hidden by the taskbar itself (covered, no free space, no taskbar) is not implemented in this build:
    // PollIntervalMs never varies with GaugeState, only with read speed.
    public const int ShownPollIntervalMs = 1000;

    // Design choice: 10 s, the safety net while the taskbar-change events (ShellWindowChangeHook's location hook and
    // the foreground and shell-window hooks) are installed. The events say when to read; this poll only finds what
    // they miss (an icon added with no window shown or hidden, a covering window no hook reported). A tray whose
    // location hook failed to install keeps ShownPollIntervalMs.
    public const int SafetyPollIntervalMs = 10_000;

    // Design choice: the quiet time a burst of change events is gathered over before one read. A taskbar sliding in or
    // out raises many location events in a row; each would otherwise be a read. The read happens this long after the
    // first event of a burst, and a later event starts the next burst, so reads from events are at most 1000 / this
    // per second however many events arrive.
    public const int EventCoalesceMs = 250;
    private const int SlowReadWindowSize = 20;
    private const double SlowReadThresholdMs = 20.0;

    private readonly ITaskbarReader _reader;
    private readonly Func<ShownGauge?> _shownGauge;
    private readonly Func<string> _chosenDisplay;
    private readonly Action<ITaskbarReader.Result> _onResult;
    private readonly Action<Action> _uiPost;
    private readonly ILog _log;
    private readonly TimeProvider _time;

    private readonly Thread _thread;
    private readonly AutoResetEvent _poke = new(initialState: false);
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Queue<double> _recentDurationsMs = new(SlowReadWindowSize);
    private bool _intervalDoubled;
    private bool _started;
    private bool _disposed;
    private volatile int _baselinePollIntervalMs;
    private volatile int _pollIntervalMs;
    private volatile bool _backoffResetRequested;

    // Turns a burst of NotifyChanged calls into one poke. It holds no timer while no burst is open, so an idle watcher has
    // nothing but its wait.
    private readonly BurstCoalescer _changes;

    // Guards _poke and _stop against a call to Set() racing their disposal. Loop's own finally block is the
    // only place either handle is ever disposed (see Dispose's comment), but a caller of Poke() or
    // ResetBackoff() can be checking _disposed and about to call _poke.Set() at the exact moment Loop
    // finishes and disposes it: checking a bool and then calling Set() are two separate steps with a gap
    // between them, and that gap is exactly where a disposed handle used to be reached. _handlesDisposed is
    // read and written only inside this lock, so "is it safe to call Set()" and "call Set()" happen as one
    // step, and Loop's finally cannot run between them.
    private readonly Lock _handleGate = new();
    private bool _handlesDisposed;

    // baselinePollIntervalMs: the value the first wait, and every backoff reset (Loop's own handling of
    // _backoffResetRequested), use. ShownPollIntervalMs by default; a caller that needs a poke's own read
    // told apart from a scheduled one deterministically, without a real-time race, sets this far longer
    // than anything it runs for instead (TrayStartOptions.TaskbarWatcherPollIntervalMs is that seam).
    public TaskbarWatcher(
        ITaskbarReader reader, Func<ShownGauge?> shownGauge, Action<ITaskbarReader.Result> onResult, Action<Action> uiPost, ILog log, TimeProvider time,
        int baselinePollIntervalMs = ShownPollIntervalMs, Func<string>? chosenDisplay = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(shownGauge);
        ArgumentNullException.ThrowIfNull(onResult);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _reader = reader;
        _shownGauge = shownGauge;
        _chosenDisplay = chosenDisplay ?? (static () => GaugeDisplayChoice.MainDisplay);
        _onResult = onResult;
        _uiPost = uiPost;
        _log = log;
        _time = time;
        _baselinePollIntervalMs = Math.Max(1, baselinePollIntervalMs);
        _pollIntervalMs = _baselinePollIntervalMs;
        _changes = new BurstCoalescer(time, TimeSpan.FromMilliseconds(EventCoalesceMs), Poke);

        _thread = new Thread(Loop) { IsBackground = true, Name = "Earshot taskbar watcher" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    // The interval the next wait uses. Set by the caller: shown/hidden band, or reset after a poke source
    // such as TaskbarCreated, WM_SETTINGCHANGE or WM_DISPLAYCHANGE.
    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set => _pollIntervalMs = Math.Max(1, value);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _thread.Start();
    }

    // Wakes the watcher for an immediate read. Safe from any thread. Coalesces: a poke that arrives while
    // a read is already running still causes exactly one more read afterwards, because AutoResetEvent
    // stays signalled until the loop waits on it again.
    public void Poke()
    {
        lock (_handleGate)
        {
            if (!_handlesDisposed)
            {
                _poke.Set();
            }
        }
    }

    // A taskbar-change event arrived (a window of the taskbar moved or resized, a window was shown or hidden, the foreground
    // changed). Safe from any thread. Coalesces: the first event of a burst opens EventCoalesceMs, every event inside it
    // is part of the same burst, and one Poke follows when it ends, so the read sees where the burst left the taskbar.
    public void NotifyChanged() => _changes.Notify();

    // Moves the poll interval every wait uses and every backoff reset returns to, and reads once at the new rate: the tray
    // calls this when the change events turn out to be installed (SafetyPollIntervalMs) or lost (ShownPollIntervalMs).
    public void SetBaselinePollInterval(int milliseconds)
    {
        _baselinePollIntervalMs = Math.Max(1, milliseconds);
        ResetBackoff();
    }

    // Resets the slow-read back-off (the poll interval and the measurement window that doubled it) to the
    // shown baseline. Called after a poke source that makes the old measurement stale: TaskbarCreated means
    // a new Explorer and a new taskbar, so a doubled interval measured against the old one no longer means
    // anything. The actual field writes happen on the worker thread (Loop), the only thread that otherwise
    // touches _recentDurationsMs and _intervalDoubled, via the same volatile-flag-plus-poke pattern Dispose
    // and Poke already use to cross from any caller's thread to the worker thread safely.
    public void ResetBackoff()
    {
        lock (_handleGate)
        {
            if (!_handlesDisposed)
            {
                _backoffResetRequested = true;
                _poke.Set();
            }
        }
    }

    // Signals the worker thread to stop and waits briefly, but never disposes _poke or _stop itself: the
    // worker thread still owns them until its own Loop actually returns, which can be later than this
    // call's 500 ms budget when a read is still in flight (UI Automation gives no way to cancel one). The
    // old code disposed both handles here unconditionally, so a read that outlived the budget went on to
    // have the worker thread wait on a handle this call had already disposed, throwing
    // ObjectDisposedException on a background thread with nothing to catch it and taking the process down.
    // Loop's own finally block disposes them instead, after the thread is certain never to touch them
    // again, so only one thread ever calls Dispose on either handle.
    //
    // Only _stop.Set() is needed to wake a waiting Loop: it is one of the two handles WaitAny waits on, so
    // setting it alone is enough for the loop to notice _stop.IsSet and return. An earlier version also
    // called _poke.Set() here, which raced this very method: the worker thread can wake on _stop, return,
    // and dispose _poke in Loop's finally before this method's own next line ran, throwing
    // ObjectDisposedException out of Dispose() itself. Dropping the redundant call removes that race
    // outright rather than guarding it.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _changes.Dispose();
        _stop.Set();
        if (_started)
        {
            _thread.Join(TimeSpan.FromMilliseconds(500));
        }
        else
        {
            // The thread never started, so nothing else will ever dispose these. Guarded by the same lock
            // as Poke/ResetBackoff even though nothing else can race here (the thread never ran), so the
            // "who disposes these handles" rule has exactly one enforcement point.
            lock (_handleGate)
            {
                if (!_handlesDisposed)
                {
                    _handlesDisposed = true;
                    _poke.Dispose();
                    _stop.Dispose();
                }
            }
        }
    }

    private void Loop()
    {
        try
        {
            var handles = new WaitHandle[] { _poke, _stop.WaitHandle };
            while (true)
            {
                WaitHandle.WaitAny(handles, _pollIntervalMs);
                if (_stop.IsSet)
                {
                    return;
                }

                if (_backoffResetRequested)
                {
                    _backoffResetRequested = false;
                    _pollIntervalMs = _baselinePollIntervalMs;
                    _intervalDoubled = false;
                    _recentDurationsMs.Clear();
                }

                ITaskbarReader.Result result;
                long started = _time.GetTimestamp();
                try
                {
                    result = _reader.Read(_shownGauge(), _chosenDisplay());
                }
                catch (Exception ex)
                {
                    // A crash here must never reach the UI thread with the process still believing the
                    // gauge is attached: the widget has no reference to anything that can touch a device.
                    // It is reported as its own step, not as "no taskbar": a read that threw says nothing
                    // about whether the bar is there, so the controller counts it like any other failed
                    // read instead of hiding the gauge at once.
                    _log.Error("Taskbar watcher: the read threw.", ex);
                    result = ITaskbarReader.Result.Fail(new TaskbarReadFailure(
                        TaskbarReadFailureStep.Exception, StepOutcomes.FromHResult("taskbar-watcher:read", NativeCodes.NotAvailable, ex.Message)));
                }

                RecordDuration(_time.GetElapsedTime(started));

                // A read UI Automation gives no way to cancel can still be in flight when Dispose is called,
                // and can go on to complete after Dispose has already returned to its caller: checked again
                // here, right before posting, so a result from a read that started before Dispose never
                // reaches uiPost once the watcher is considered gone.
                if (_stop.IsSet)
                {
                    return;
                }

                ITaskbarReader.Result captured = result;
                _uiPost(() => _onResult(captured));
            }
        }
        finally
        {
            lock (_handleGate)
            {
                _handlesDisposed = true;
                _poke.Dispose();
                _stop.Dispose();
            }
        }
    }

    private void RecordDuration(TimeSpan elapsed)
    {
        double ms = elapsed.TotalMilliseconds;
        _log.Write(LogLevel.Debug, "Taskbar read took " + ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        _recentDurationsMs.Enqueue(ms);
        while (_recentDurationsMs.Count > SlowReadWindowSize)
        {
            _recentDurationsMs.Dequeue();
        }

        if (!_intervalDoubled && _recentDurationsMs.Count == SlowReadWindowSize && Average(_recentDurationsMs) > SlowReadThresholdMs)
        {
            _intervalDoubled = true;
            int doubled = _pollIntervalMs * 2;
            _pollIntervalMs = doubled;
            _log.Info("Taskbar poll interval doubled to " + doubled.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " ms: the mean of the last " + SlowReadWindowSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + " reads exceeded " +
                SlowReadThresholdMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        }
    }

    private static double Average(Queue<double> values)
    {
        double sum = 0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }
}
