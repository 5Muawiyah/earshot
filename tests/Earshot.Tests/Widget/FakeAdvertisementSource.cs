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

    // Lets a test make Start or Stop report a failure, the way a real watcher's step can, so the
    // service's logging of that step (Warn when not ok) can be exercised without a real watcher.
    public Func<StepOutcome>? StartResult { get; set; }

    public Func<StepOutcome>? StopResult { get; set; }

    public event EventHandler<AdvertisementSample>? Received;

    public event EventHandler<AdvertisementSourceStopped>? Stopped;

    public StepOutcome Start()
    {
        StartCalls++;
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

    public void Dispose() => DisposeCalls++;
}
