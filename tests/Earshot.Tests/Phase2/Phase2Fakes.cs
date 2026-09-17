using Earshot.Audio;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// An endpoint source with no Core Audio behind it. Records every call, the thread it came on, and hands
// back whatever readings the test sets.
internal sealed class FakeEndpointSource : IEndpointSource
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = new();
    private readonly HashSet<int> _threads = new();
    private List<EndpointReading> _readings = new();
    private List<StepOutcome> _steps = new();
    private Action<EndpointNotification>? _sink;
    private Action? _sinkFailed;
    private (int Count, Exception? Last) _callbackFailures;

    public StepOutcome SubscribeResult { get; set; } = StepOutcomes.FromHResult(AudioWorker.Steps.RegisterClient, 0);

    public StepOutcome? EnumerationFailure { get; set; }

    // When set, Enumerate waits for it before reading (the wait runs on the worker thread).
    public ManualResetEventSlim? EnumerateGate { get; set; }

    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) { return _calls.ToArray(); } }
    }

    public IReadOnlyCollection<int> Threads
    {
        get { lock (_gate) { return _threads.ToArray(); } }
    }

    public int EnumerateCalls => Calls.Count(c => c == "enumerate");

    public Action<EndpointNotification>? Sink
    {
        get { lock (_gate) { return _sink; } }
    }

    public Action? SinkFailed
    {
        get { lock (_gate) { return _sinkFailed; } }
    }

    public void SetReadings(IEnumerable<EndpointReading> readings, IEnumerable<StepOutcome>? steps = null)
    {
        lock (_gate)
        {
            _readings = readings.ToList();
            _steps = steps?.ToList() ?? new List<StepOutcome>();
        }
    }

    public void SetCallbackFailures(int count, Exception? last)
    {
        lock (_gate)
        {
            _callbackFailures = (count, last);
        }
    }

    public StepOutcome Subscribe(Action<EndpointNotification> sink, Action sinkFailed)
    {
        Record("subscribe");
        lock (_gate)
        {
            if (SubscribeResult.Ok)
            {
                _sink = sink;
                _sinkFailed = sinkFailed;
            }

            return SubscribeResult;
        }
    }

    public StepOutcome Unsubscribe()
    {
        Record("unsubscribe");
        lock (_gate)
        {
            bool had = _sink is not null;
            _sink = null;
            return had
                ? StepOutcomes.FromHResult(AudioWorker.Steps.UnregisterClient, 0)
                : StepOutcomes.NotAttempted(AudioWorker.Steps.UnregisterClient, "No notification client is registered.");
        }
    }

    public EndpointEnumeration Enumerate()
    {
        Record("enumerate");
        EnumerateGate?.Wait(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            if (EnumerationFailure is StepOutcome failure)
            {
                return EndpointEnumeration.Failed(failure);
            }

            return new EndpointEnumeration(true, _readings.ToList(), _steps.ToList());
        }
    }

    public (int Count, Exception? Last) TakeCallbackFailures()
    {
        lock (_gate)
        {
            (int, Exception?) taken = _callbackFailures;
            _callbackFailures = (0, null);
            return taken;
        }
    }

    // Delivers a notification the way the notification client does.
    public void Notify(EndpointNotification notification)
    {
        Action<EndpointNotification>? sink = Sink;
        Assert.IsNotNull(sink, "No sink is subscribed.");
        sink(notification);
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
            _threads.Add(Environment.CurrentManagedThreadId);
        }
    }
}

// Settings in memory. Update raises Changed on the calling thread, like JsonSettingsStore.
internal sealed class FakeSettingsStore : ISettingsStore
{
    private readonly Lock _gate = new();
    private EarshotSettings _current = new();

    public event EventHandler<EarshotSettings>? Changed;

    public EarshotSettings Current
    {
        get { lock (_gate) { return Copy(_current); } }
    }

    public void Update(Action<EarshotSettings> mutate)
    {
        EarshotSettings next;
        lock (_gate)
        {
            next = Copy(_current);
            mutate(next);
            _current = next;
        }

        Changed?.Invoke(this, Copy(next));
    }

    public void Reload() => Changed?.Invoke(this, Current);

    private static EarshotSettings Copy(EarshotSettings s) => new()
    {
        SchemaVersion = s.SchemaVersion,
        DeviceMatch = s.DeviceMatch,
        ProtectAudioQuality = s.ProtectAudioQuality,
        ProtectAudioNoticeShown = s.ProtectAudioNoticeShown,
        OpenOnStartup = s.OpenOnStartup,
        PinnedContainerId = s.PinnedContainerId,
        PinnedAddress = s.PinnedAddress,
    };
}

// A delay the test releases by hand, so coalescing is deterministic.
internal sealed class ManualDelay
{
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource> _pending = new();
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    public Task Delay(TimeSpan window)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending.Add(tcs);
        }

        Interlocked.Increment(ref _requests);
        return tcs.Task;
    }

    public void ReleaseAll()
    {
        TaskCompletionSource[] pending;
        lock (_gate)
        {
            pending = _pending.ToArray();
            _pending.Clear();
        }

        foreach (TaskCompletionSource tcs in pending)
        {
            tcs.TrySetResult();
        }
    }
}

internal static class Eventually
{
    public static async Task True(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException("Timed out waiting for: " + what);
            }

            await Task.Delay(5);
        }
    }
}
