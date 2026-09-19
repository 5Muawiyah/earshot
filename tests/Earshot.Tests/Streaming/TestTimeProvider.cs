namespace Earshot.Tests.Streaming;

// A clock that moves only when the test moves it, with timers that fire when it passes their due time. Written by
// hand: the ready-made one lives in a package, and this project takes no package it does not already have.
//
// Both overrides matter. CancellationTokenSource(TimeSpan, TimeProvider) arms its cancellation through CreateTimer,
// so a provider that left CreateTimer alone would hand it a real timer on the real clock, and a test of a time
// limit would have to sleep. Here advancing the clock is what fires the cancellation.
internal sealed class TestTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = Start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    // Timers created and not yet disposed, so a test can see a time limit was armed on this clock and let go again.
    public int LiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public int TimersCreated { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return (_now - Start).Ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            TimersCreated++;
            _timers.Add(timer);
            timer.ArmLocked(_now, dueTime, period);
        }

        return timer;
    }

    // Moves the clock and fires, outside the lock, every timer whose due time it passed.
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.TakeIfDueLocked(_now)).ToList();
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Forget(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public void ArmLocked(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            _period = period;
        }

        public bool TakeIfDueLocked(DateTimeOffset now)
        {
            if (_dueAt is not { } dueAt || dueAt > now)
            {
                return false;
            }

            _dueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;
            return true;
        }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                ArmLocked(owner._now, dueTime, period);
            }

            return true;
        }

        public void Dispose() => owner.Forget(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
