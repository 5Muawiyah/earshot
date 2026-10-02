using Earshot.Widget;

namespace Earshot.Tests;

// A display that refreshes at a chosen rate, in fake time: the k-th vertical blank is at k / rate seconds (rounded to the
// tick, so 15 blanks at 60 Hz and 75 at 300 Hz both land on exactly 250 ms). Nothing happens until a test asks for a blank;
// each blank delivers one frame, stamped with its time, to every subscriber. It counts subscriptions so a test can hold
// that nothing is subscribed at rest.
internal sealed class FakeVBlankClock(double hz) : IFrameClock
{
    private readonly List<Action<TimeSpan>> _subscribers = [];
    private long _blank;

    public double Hz { get; } = hz;

    public TimeSpan Now { get; private set; }

    // Live subscriptions now, and how many were ever made.
    public int Subscriptions => _subscribers.Count;

    public int SubscribeCalls { get; private set; }

    // Blanks that delivered a frame to at least one subscriber.
    public int FramesDelivered { get; private set; }

    public IDisposable Subscribe(Action<TimeSpan> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        SubscribeCalls++;
        _subscribers.Add(onFrame);
        return new Subscription(this, onFrame);
    }

    public TimeSpan TimeOfBlank(long k) => TimeSpan.FromTicks((long)Math.Round(k * TimeSpan.TicksPerSecond / Hz));

    // The next blank: time moves to it and every subscriber gets its frame. True when anyone did.
    public bool Blank()
    {
        _blank++;
        Now = TimeOfBlank(_blank);
        if (_subscribers.Count == 0)
        {
            return false;
        }

        FramesDelivered++;
        foreach (Action<TimeSpan> subscriber in _subscribers.ToArray())
        {
            if (_subscribers.Contains(subscriber))
            {
                subscriber(Now);
            }
        }

        return true;
    }

    // Blanks until no one is subscribed; the number of frames delivered. Stops at max to keep a broken test finite.
    public int RunUntilIdle(int max = 100_000)
    {
        int frames = 0;
        while (_subscribers.Count > 0 && frames < max)
        {
            if (Blank())
            {
                frames++;
            }
        }

        return frames;
    }

    // Blanks until the clock reads at least until.
    public void RunUntil(TimeSpan until)
    {
        while (Now < until)
        {
            Blank();
        }
    }

    private sealed class Subscription(FakeVBlankClock clock, Action<TimeSpan> onFrame) : IDisposable
    {
        public void Dispose() => clock._subscribers.Remove(onFrame);
    }
}
