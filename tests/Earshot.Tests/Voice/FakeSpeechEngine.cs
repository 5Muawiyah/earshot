using Earshot.Contracts;
using Earshot.Voice;

namespace Earshot.Tests.Voice;

// A fake ISpeechEngine that never touches System.Speech, a real audio device or a real voice. Every
// SpeechAnnouncer test uses this, never a real SystemSpeechEngine: Announce returns before the worker has
// done anything, so a test that announces and then asserts without waiting on one of these signals is a
// race dressed up as an assertion (see SpeechAnnouncerTests for how each signal is used).
internal sealed class FakeSpeechEngine : ISpeechEngine
{
    private readonly object _lock = new();
    private readonly List<string> _utterances = new();
    private int _speakFailuresRemaining;
    private Exception? _speakException;

    // Signalled as the first statement inside Speak, before it blocks on ReleaseGate. A test waits on
    // this to know the worker has taken the line out of the mailbox.
    public SemaphoreSlim SpeakEntered { get; } = new(0);

    // Waited on inside Speak, after SpeakEntered is signalled. Closed by default (a test that wants the
    // fake to speak straight through calls ReleaseGate.Release() once, or sets OpenGateByDefault).
    public SemaphoreSlim ReleaseGate { get; } = new(0);

    // Signalled as Speak returns, so a test can wait for an utterance to finish rather than guess.
    public SemaphoreSlim SpeakCompleted { get; } = new(0);

    // When true (the default), Speak never waits on ReleaseGate: most tests only care about the
    // utterance order, not about pinning the moment the worker is mid-speak. A test that needs to hold a
    // phrase in flight (coalescing, "still speaking") sets this false and releases the gate itself.
    public bool OpenGateByDefault { get; set; } = true;

    public bool IsOpen { get; private set; }

    private int _disposeCount;

    // Interlocked, not a plain get/private-set counter: a concurrent-dispose test calls Dispose() from
    // two threads at once on purpose (SpeechAnnouncerTests.ConcurrentStopSpeakingCallsDisposeTheEngine
    // ExactlyOnce), and a plain "DisposeCount++" can lose an update under that exact race, which would
    // hide a real double-dispose defect in the code under test rather than reveal it.
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public StepOutcome OpenOutcome { get; set; } = StepOutcomes.FromHResult("open", 0);

    // Every string passed to Speak, in order, read under this fake's own lock.
    public IReadOnlyList<string> Utterances
    {
        get { lock (_lock) { return _utterances.ToArray(); } }
    }

    public StepOutcome Open(VoiceOverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (OpenOutcome.Ok)
        {
            IsOpen = true;
        }

        return OpenOutcome;
    }

    // The next N calls to Speak throw this exception instead of recording an utterance.
    public void ThrowOnNextSpeaks(int count, Exception exception)
    {
        lock (_lock)
        {
            _speakFailuresRemaining = count;
            _speakException = exception;
        }
    }

    public StepOutcome Speak(string text)
    {
        SpeakEntered.Release();
        if (!OpenGateByDefault)
        {
            ReleaseGate.Wait();
        }

        Exception? toThrow = null;
        lock (_lock)
        {
            if (_speakFailuresRemaining > 0)
            {
                _speakFailuresRemaining--;
                toThrow = _speakException;
            }
            else
            {
                _utterances.Add(text);
            }
        }

        SpeakCompleted.Release();
        if (toThrow is not null)
        {
            return new StepOutcome("speak", Ok: false, toThrow.HResult, toThrow.GetType().Name, toThrow.Message);
        }

        return StepOutcomes.FromHResult("speak", 0);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        IsOpen = false;
    }
}
