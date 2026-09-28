using Earshot.Contracts;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// No Core Bluetooth behind it. A test drives State directly and raises Received/Stopped by hand.
internal sealed class FakeAdvertisementSource : IAdvertisementSource
{
    private AdvertisementSourceState _state = AdvertisementSourceState.Created;

    // Lets a test make a state read throw, the way a real watcher's Status getter reading a COM property
    // could, so a timer callback's own exception boundary can be exercised without a real watcher.
    public Func<AdvertisementSourceState>? StateOverride { get; set; }

    public AdvertisementSourceState State
    {
        get => StateOverride is { } f ? f() : _state;
        set => _state = value;
    }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    // The count of Start() calls so far, whatever each one returns: mirrors WinRtAdvertisementSource's own
    // generation counter, so RaiseStopped(code, name, step) below can tag an ordinary Stopped with "current"
    // without a test having to track the number itself. A test proving the stale-generation path (item 1)
    // constructs an AdvertisementSourceStopped directly with an earlier value instead.
    public int Generation { get; private set; }

    // Lets a test make Start or Stop report a failure, the way a real watcher's step can, so the
    // service's logging of that step (Warn when not ok) can be exercised without a real watcher.
    public Func<StepOutcome>? StartResult { get; set; }

    public Func<StepOutcome>? StopResult { get; set; }

    // Test-only, for the deadlock proof (item 3: Start and Stop must run outside the service's own lock).
    // Armed, Stop() signals _stopEntered and then waits for _releaseStop, so a test can hold it open while it
    // tries something that needs the service's lock from another thread.
    private readonly ManualResetEventSlim _stopEntered = new(initialState: false);
    private readonly ManualResetEventSlim _releaseStop = new(initialState: false);
    private bool _blockNextStop;

    public event EventHandler<AdvertisementSample>? Received;

    public event EventHandler<AdvertisementSourceStopped>? Stopped;

    public StepOutcome Start()
    {
        StartCalls++;
        Generation++;
        if (StartResult is { } result)
        {
            StepOutcome step = result();
            State = step.Ok ? AdvertisementSourceState.Started : AdvertisementSourceState.Aborted;
            return step;
        }

        State = AdvertisementSourceState.Started;
        return StepOutcomes.FromHResult("fake-start", 0);
    }

    public StepOutcome Stop()
    {
        StopCalls++;
        if (_blockNextStop)
        {
            _stopEntered.Set();
            _releaseStop.Wait(TimeSpan.FromSeconds(10));
        }

        State = AdvertisementSourceState.Stopped;
        if (StopResult is { } result)
        {
            return result();
        }

        return StepOutcomes.FromHResult("fake-stop", 0);
    }

    public void Raise(AdvertisementSample sample) => Received?.Invoke(this, sample);

    public void RaiseStopped(AdvertisementSourceStopped stopped)
    {
        State = AdvertisementSourceState.Stopped;
        Stopped?.Invoke(this, stopped);
    }

    // Convenience for the ordinary, non-stale case: tags the event with the current generation, so a test
    // exercising anything other than item 1's stale-Stopped path never has to know the number.
    public void RaiseStopped(int errorCode, string errorName, StepOutcome step) =>
        RaiseStopped(new AdvertisementSourceStopped(errorCode, errorName, step, Generation));

    // Arms the next Stop() call to block until ReleaseStop is called, and resets the two signals so it can
    // be used more than once in the same test.
    public void ArmBlockingStop()
    {
        _blockNextStop = true;
        _stopEntered.Reset();
        _releaseStop.Reset();
    }

    public bool WaitForStopEntered(TimeSpan timeout) => _stopEntered.Wait(timeout);

    public void ReleaseStop() => _releaseStop.Set();

    public void Dispose() => DisposeCalls++;
}
