using Earshot.Contracts;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// No Core Bluetooth behind it. A test drives State directly and raises Received/Stopped by hand.
internal sealed class FakeAdvertisementSource : IAdvertisementSource
{
    public AdvertisementSourceState State { get; set; } = AdvertisementSourceState.Created;

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public event EventHandler<AdvertisementSample>? Received;

    public event EventHandler<AdvertisementSourceStopped>? Stopped;

    public StepOutcome Start()
    {
        StartCalls++;
        State = AdvertisementSourceState.Started;
        return StepOutcomes.FromHResult("fake-start", 0);
    }

    public StepOutcome Stop()
    {
        StopCalls++;
        State = AdvertisementSourceState.Stopped;
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
