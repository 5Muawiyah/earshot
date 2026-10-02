namespace Earshot.Widget;

// Turns a burst of notifications into one call: the first notification opens a window of the given length, every one inside
// it is part of the same burst, and the action runs once when the window ends. The next notification after that opens the
// next window, so the action runs at most once per window however many notifications arrive, and the last one of a burst is
// never lost (the action runs after it). Holds no timer while nothing is notified. Safe from any thread. The action runs on
// the timer's thread, outside the lock.
internal sealed class BurstCoalescer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly Action _action;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private bool _closed;

    public BurstCoalescer(TimeProvider time, TimeSpan window, Action action)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(action);
        _time = time;
        _window = window;
        _action = action;
    }

    public void Notify()
    {
        lock (_gate)
        {
            if (_closed || _timer is not null)
            {
                return;
            }

            _timer = _time.CreateTimer(OnWindowEnded, null, _window, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWindowEnded(object? state)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_closed)
            {
                return;
            }
        }

        _action();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
