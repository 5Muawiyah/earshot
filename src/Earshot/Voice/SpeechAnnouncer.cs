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
// The mailbox, the stop flag, the last accepted line, the last accepted time, the failure counter and
// the outcome list are guarded by one private lock object, and that lock is never held across a call
// into the engine.
public sealed class SpeechAnnouncer : IAnnouncer
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
    private DateTimeOffset _lastAcceptedAt;
    private int _consecutiveSpeakFailures;
    private bool _disposed;

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
        }

        // IsBackground: a phrase in flight at exit must never keep the process alive on its own; Stop is
        // still what every shutdown path calls, but a crash or a kill leaves nothing here to wait for.
        _worker = new Thread(RunWorker) { IsBackground = true, Name = "voiceover" };
        _worker.Start();
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

            DateTimeOffset now = _time.GetUtcNow();
            if (_lastAccepted == line && now - _lastAcceptedAt < TimeSpan.FromMilliseconds(_settings.RepeatGapMilliseconds))
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
            _lastAcceptedAt = now;
            result = StepOutcomes.FromHResult("announce", 0, "accepted");
            Record(result);
            _signal.Set();
        }

        return result;
    }

    public StepOutcome StopSpeaking()
    {
        lock (_lock)
        {
            _stopRequested = true;
            _available = false;
            _pending = null;
        }

        // Wakes the worker so it notices the stop flag once it is not blocked inside Speak. A phrase
        // already in flight still finishes: this never calls SpeakAsyncCancelAll or anything that would
        // reach the engine from this thread.
        _signal.Set();

        Thread? worker = _worker;
        if (worker is null)
        {
            StepOutcome nothingToStop = StepOutcomes.FromHResult("stop", 0, "not running");
            lock (_lock) { Record(nothingToStop); }
            return nothingToStop;
        }

        bool joined = worker.Join(_settings.ShutdownWaitMilliseconds);
        _worker = null;
        if (!joined)
        {
            StepOutcome timedOut = StepOutcomes.NotAttempted("stop", "worker still speaking");
            lock (_lock) { Record(timedOut); }
            return timedOut;
        }

        _engine.Dispose();
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
