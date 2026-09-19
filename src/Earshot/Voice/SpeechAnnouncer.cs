using Earshot.Contracts;

namespace Earshot.Voice;

// Speaks Earshot's state changes through ISpeechEngine, one closed phrase at a time. Exactly one thread
// ever touches the engine: the "voiceover" background worker Start creates. Announce is called from the
// UI thread: it takes a lock only for a few field assignments, and it never calls the engine, waits on
// the worker or sleeps. The mailbox holds at most one line: a newer Announce replaces whatever is
// waiting, and the dropped line is recorded as superseded, so a burst of state changes speaks at most
// the first and the last, and the queue can never grow.
//
// Start calls engine.Open inline, on the calling thread, and returns its outcome: IsAvailable and the
// outcome Start returns must be correct the moment it returns (a hotkey or a menu click needs to know
// at once), so Open is never deferred onto the worker. The accepted cost is that Start blocks the UI
// thread for as long as constructing a synthesiser and reading the voice list takes; it runs once, from
// a menu click or a settings load, never on a timer and never in a loop.
//
// The mailbox, the stop flag, the last accepted line, the last accepted time, the worker field, the
// failure counter and the outcome list are guarded by one private lock object, and that lock is never
// held across a call into the engine. The worker field is written only under that lock too (both in
// Start, together with _available, and in StopSpeaking, which claims it by swapping it for null): that
// is what stops two concurrent StopSpeaking calls both disposing the engine, and what stops a
// StopSpeaking that races in before the worker thread is started from finding nothing to join and
// leaking an already-opened engine (see StopSpeaking's own comment).
//
// Internal, not public: the constructor takes ISpeechEngine, which is itself internal (see that
// interface's own header). IAnnouncer, the interface this implements, stays public: nothing on it
// exposes ISpeechEngine.
internal sealed class SpeechAnnouncer : IAnnouncer
{
    private readonly ISpeechEngine _engine;
    private readonly VoiceOverSettings _settings;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<StepOutcome> _outcomes = new();
    private readonly ManualResetEventSlim _signal = new(initialState: false);

    private Thread? _worker;
    private bool _available;
    private bool _stopRequested = true;
    private VoiceLine? _pending;
    private VoiceLine? _lastAccepted;
    private long _lastAcceptedTimestamp;
    private int _consecutiveSpeakFailures;
    private bool _disposed;
    private bool _workerStarted;
    private bool _workerJoinConfirmed;

    public SpeechAnnouncer(ISpeechEngine engine, VoiceOverSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);

        _engine = engine;
        _settings = settings;
        _time = time;
    }

    public bool IsAvailable
    {
        get { lock (_lock) { return _available; } }
    }

    public event EventHandler<StepOutcome>? Stopped;

    public StepOutcome Start()
    {
        StepOutcome outcome = _engine.Open(_settings);
        lock (_lock)
        {
            Record(outcome);
            if (!outcome.Ok)
            {
                _available = false;
                _stopRequested = true;
                return outcome;
            }

            _available = true;
            _stopRequested = false;
            _consecutiveSpeakFailures = 0;

            // IsBackground: a phrase in flight at exit must never keep the process alive on its own;
            // StopSpeaking is still what every shutdown path calls, but a crash or a kill leaves nothing
            // here to wait for. Created and started under the same lock as _available: Thread.Start does
            // not block (it only schedules the OS thread, which begins with _signal.Wait(), needing no
            // lock itself), so holding the lock across it costs nothing measurable, and it closes the
            // window where a concurrent StopSpeaking could see _available true with _worker still null
            // and return "not running" without ever disposing the engine Open just constructed.
            _worker = new Thread(RunWorker) { IsBackground = true, Name = "voiceover" };
            _workerStarted = true;
            _worker.Start();
        }

        return outcome;
    }

    public StepOutcome Announce(VoiceLine line)
    {
        StepOutcome result;
        lock (_lock)
        {
            if (!_available)
            {
                result = StepOutcomes.NotAttempted("announce", _stopRequested ? "stopped" : "not available");
                Record(result);
                return result;
            }

            if (VoicePhrases.IsFailure(line) && !_settings.SpeakFailures)
            {
                result = StepOutcomes.NotAttempted("announce", "failures are off");
                Record(result);
                return result;
            }

            // The repeat gap is measured on TimeProvider's monotonic timestamp (GetTimestamp/
            // GetElapsedTime), never on GetUtcNow: a wall clock can jump, forwards on an NTP correction
            // or backwards on one the other way, and a gap measured on it could refuse to speak again
            // for far longer than RepeatGapMilliseconds, or accept a repeat far too early, depending on
            // which way the wall clock moved (see RepeatGapUsesTheMonotonicClockNotTheWallClock).
            if (_lastAccepted == line && _time.GetElapsedTime(_lastAcceptedTimestamp) < TimeSpan.FromMilliseconds(_settings.RepeatGapMilliseconds))
            {
                result = StepOutcomes.NotAttempted("announce", "repeat inside gap");
                Record(result);
                return result;
            }

            if (_pending is not null)
            {
                Record(StepOutcomes.NotAttempted("announce", "superseded"));
            }

            _pending = line;
            _lastAccepted = line;
            _lastAcceptedTimestamp = _time.GetTimestamp();
            result = StepOutcomes.FromHResult("announce", 0, "accepted");
            Record(result);
            _signal.Set();
        }

        return result;
    }

    public StepOutcome StopSpeaking()
    {
        // Claims _worker by swapping it for null under the lock: whichever of two concurrent
        // StopSpeaking calls wins the lock first is the only one that can see a non-null worker, so only
        // one of them ever joins it or disposes the engine (ConcurrentStopSpeakingCallsDisposeTheEngine
        // ExactlyOnce proves this). The other sees worker already null and returns "not running" at
        // once, exactly as it would if StopSpeaking had never had anything to stop.
        Thread? worker;
        lock (_lock)
        {
            _stopRequested = true;
            _available = false;
            _pending = null;
            worker = _worker;
            _worker = null;
        }

        // Wakes the worker so it notices the stop flag once it is not blocked inside Speak. A phrase
        // already in flight still finishes: this never calls SpeakAsyncCancelAll or anything that would
        // reach the engine from this thread.
        _signal.Set();

        if (worker is null)
        {
            StepOutcome nothingToStop = StepOutcomes.FromHResult("stop", 0, "not running");
            lock (_lock) { Record(nothingToStop); }
            return nothingToStop;
        }

        bool joined = worker.Join(_settings.ShutdownWaitMilliseconds);
        if (!joined)
        {
            StepOutcome timedOut = StepOutcomes.NotAttempted("stop", "worker still speaking");
            lock (_lock) { Record(timedOut); }
            return timedOut;
        }

        _engine.Dispose();
        lock (_lock) { _workerJoinConfirmed = true; }
        StepOutcome ok = StepOutcomes.FromHResult("stop", 0);
        lock (_lock) { Record(ok); }
        return ok;
    }

    public IReadOnlyList<StepOutcome> Drain()
    {
        lock (_lock)
        {
            if (_outcomes.Count == 0)
            {
                return Array.Empty<StepOutcome>();
            }

            var copy = _outcomes.ToArray();
            _outcomes.Clear();
            return copy;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSpeaking();

        // Only disposed once the worker thread is provably done: either it never started at all, or a
        // Join (in this call or an earlier concurrent one) actually returned true, which the OS
        // guarantees means the thread has fully exited and will never touch _signal again. When
        // StopSpeaking timed out ("worker still speaking"), the background thread may still be alive and
        // may still call _signal.Wait/Reset once more; disposing here would risk an
        // ObjectDisposedException on a thread this class no longer controls, so _signal is deliberately
        // left undisposed in that case, the same accepted trade-off already made for the engine itself
        // (see StopSpeaking's timeout branch and the IsBackground comment on the worker thread).
        bool safeToDisposeSignal;
        lock (_lock) { safeToDisposeSignal = !_workerStarted || _workerJoinConfirmed; }
        if (safeToDisposeSignal)
        {
            _signal.Dispose();
        }
    }

    // Runs on the "voiceover" thread only. Waits on the signal, takes the line out of the mailbox under
    // the lock, looks the phrase up and calls engine.Speak. Blocking there is correct and expected: this
    // thread has nothing else to do.
    private void RunWorker()
    {
        while (true)
        {
            _signal.Wait();

            VoiceLine? line;
            lock (_lock)
            {
                if (_stopRequested)
                {
                    _signal.Reset();
                    return;
                }

                line = _pending;
                _pending = null;
                _signal.Reset();
            }

            if (line is not { } toSpeak)
            {
                continue;
            }

            StepOutcome outcome = _engine.Speak(VoicePhrases.For(toSpeak));
            bool giveUp = false;
            lock (_lock)
            {
                Record(outcome);
                if (outcome.Ok)
                {
                    _consecutiveSpeakFailures = 0;
                }
                else
                {
                    _consecutiveSpeakFailures++;
                    if (_consecutiveSpeakFailures >= _settings.FailuresBeforeGivingUp)
                    {
                        giveUp = true;
                        _available = false;
                        _stopRequested = true;
                    }
                }
            }

            if (giveUp)
            {
                Stopped?.Invoke(this, outcome);
                return;
            }
        }
    }

    // Caller must hold _lock.
    private void Record(StepOutcome outcome) => _outcomes.Add(outcome);
}
