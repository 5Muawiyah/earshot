using Earshot.Contracts;

namespace Earshot.Widget;

// Owns the UIA worker thread: waits for a poke or the poll interval, reads once, posts the result to the
// UI thread. Pokes coalesce (an AutoResetEvent, not a counting one): a poke during a read schedules
// exactly one more read. The thread is MTA (UI Automation documents that its calls must come from a
// thread that owns no window, in a multithreaded apartment) and is set up before it starts, since the
// apartment state cannot change afterwards.
internal sealed class TaskbarWatcher : IDisposable
{
    // Design choices: 1 s while attached, 2 s while hidden by the taskbar itself (covered, no free
    // space, no taskbar), doubled once if the mean of the last 20 reads exceeds 20 ms.
    public const int ShownPollIntervalMs = 1000;
    public const int HiddenPollIntervalMs = 2000;
    private const int SlowReadWindowSize = 20;
    private const double SlowReadThresholdMs = 20.0;

    private readonly ITaskbarReader _reader;
    private readonly Func<ShownGauge?> _shownGauge;
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
    private volatile int _pollIntervalMs = ShownPollIntervalMs;

    public TaskbarWatcher(ITaskbarReader reader, Func<ShownGauge?> shownGauge, Action<ITaskbarReader.Result> onResult, Action<Action> uiPost, ILog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(shownGauge);
        ArgumentNullException.ThrowIfNull(onResult);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _reader = reader;
        _shownGauge = shownGauge;
        _onResult = onResult;
        _uiPost = uiPost;
        _log = log;
        _time = time;

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
        if (!_disposed)
        {
            _poke.Set();
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
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        _poke.Set();
        if (_started)
        {
            _thread.Join(TimeSpan.FromMilliseconds(500));
        }
        else
        {
            // The thread never started, so nothing else will ever dispose these.
            _poke.Dispose();
            _stop.Dispose();
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

                ITaskbarReader.Result result;
                long started = _time.GetTimestamp();
                try
                {
                    result = _reader.Read(_shownGauge());
                }
                catch (Exception ex)
                {
                    // A crash here must never reach the UI thread with the process still believing the
                    // gauge is attached: the widget has no reference to anything that can touch a device,
                    // so the worst outcome is a gauge stuck hidden with the tray icon shown.
                    _log.Error("Taskbar watcher: the read threw.", ex);
                    result = ITaskbarReader.Result.Fail(new TaskbarReadFailure(
                        TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromHResult("taskbar-watcher:read", NativeCodes.NotAvailable, ex.Message)));
                }

                RecordDuration(_time.GetElapsedTime(started));
                ITaskbarReader.Result captured = result;
                _uiPost(() => _onResult(captured));
            }
        }
        finally
        {
            _poke.Dispose();
            _stop.Dispose();
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
