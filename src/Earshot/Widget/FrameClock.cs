namespace Earshot.Widget;

// Where animation frames come from: one call for each refresh of the display a window is on, on the UI thread, stamped with
// the time of that refresh, for as long as someone is subscribed. Nothing is called, and nothing wakes, while no one is.
// The real clock waits for the display's vertical blank (VBlankFrameClock); tests use a fake one that ticks at a chosen rate.
internal interface IFrameClock
{
    // The time now on the clock's own timeline, the one frames are stamped on.
    TimeSpan Now { get; }

    // Asks for onFrame at each refresh, on the UI thread, until the returned subscription is disposed.
    IDisposable Subscribe(Action<TimeSpan> onFrame);
}

// The animations of one window, run off one frame clock. Each running animation is a step function, called with the frame's
// time, that does its work and says whether it wants another frame. The clock is subscribed to while at least one animation
// runs and the subscription is disposed the moment the last one ends, so a window at rest gets no frames and makes no calls.
//
// With Windows' animation effects off nothing runs: callers ask AnimationsEnabled first and put their end state in place in
// one step instead (AnimatedValue does this).
//
// UI thread only.
internal sealed class FrameDriver : IDisposable
{
    private readonly IFrameClock _clock;
    private readonly Func<bool> _animationsEnabled;
    private readonly Dictionary<object, Func<TimeSpan, bool>> _running = [];
    private IDisposable? _subscription;
    private bool _disposed;

    public FrameDriver(IFrameClock clock, Func<bool> animationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(animationsEnabled);
        _clock = clock;
        _animationsEnabled = animationsEnabled;
    }

    // Called once after every frame's steps, for a window that repaints once for all of them.
    public event Action? AfterFrame;

    public TimeSpan Now => _clock.Now;

    // Windows' animation effects setting, read now.
    public bool AnimationsEnabled => !_disposed && _animationsEnabled();

    // True while subscribed to the clock.
    public bool Subscribed => _subscription is not null;

    public bool IsRunning(object key) => _running.ContainsKey(key);

    // Runs step at each frame until it returns false or Stop(key) is called. A step already running under key is replaced.
    public void Run(object key, Func<TimeSpan, bool> step)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(step);
        if (_disposed)
        {
            return;
        }

        _running[key] = step;
        _subscription ??= _clock.Subscribe(OnFrame);
    }

    public void Stop(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_running.Remove(key) && _running.Count == 0)
        {
            Unsubscribe();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _running.Clear();
        Unsubscribe();
    }

    private void OnFrame(TimeSpan at)
    {
        if (_disposed)
        {
            return;
        }

        // A step may start or stop others, so the frame walks a copy.
        foreach (KeyValuePair<object, Func<TimeSpan, bool>> entry in _running.ToArray())
        {
            // Skipped when an earlier step stopped or replaced it; removed when it is done, unless it replaced itself.
            if (!_running.TryGetValue(entry.Key, out Func<TimeSpan, bool>? step) || !ReferenceEquals(step, entry.Value))
            {
                continue;
            }

            bool more = step(at);
            if (!more && _running.TryGetValue(entry.Key, out Func<TimeSpan, bool>? now) && ReferenceEquals(now, step))
            {
                _running.Remove(entry.Key);
            }
        }

        if (_running.Count == 0)
        {
            Unsubscribe();
        }

        AfterFrame?.Invoke();
    }

    private void Unsubscribe()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}

// One number that moves from where it is to where it is sent, on a curve, over a duration: a knob's place, a fill's strength,
// a ring's sweep, a height. Sent somewhere new while moving, it sets out from where it is at that moment, so nothing ever
// jumps. Pure: the caller says what time it is.
internal sealed class AnimatedValue(double value)
{
    private double _from = value;
    private double _to = value;
    private TimeSpan _start;
    private TimeSpan _duration;
    private CubicBezier _curve = FluentMotion.Linear;

    public double Target => _to;

    public double ValueAt(TimeSpan now)
    {
        if (_duration <= TimeSpan.Zero || now - _start >= _duration)
        {
            return _to;
        }

        double t = Math.Max(0, (now - _start) / _duration);
        return _from + ((_to - _from) * _curve.Progress(t));
    }

    public bool MovingAt(TimeSpan now) => _duration > TimeSpan.Zero && now - _start < _duration;

    // Puts the value at target at once.
    public void Jump(double target)
    {
        _from = target;
        _to = target;
        _duration = TimeSpan.Zero;
    }

    // Sends the value to target from where it is at now.
    public void MoveTo(double target, TimeSpan now, TimeSpan duration, CubicBezier curve)
    {
        _from = ValueAt(now);
        _to = target;
        _start = now;
        _duration = duration;
        _curve = curve;
    }

    // Sends the value to target on driver, calling apply with each frame's value until it is there. With animation effects
    // off, or with no driver, the value is put there at once and apply is called once. Nothing happens when the value is
    // already at target and still.
    public void AnimateTo(double target, FrameDriver? driver, TimeSpan duration, CubicBezier curve, Action<double> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (target == _to)
        {
            return;
        }

        if (driver is null || !driver.AnimationsEnabled)
        {
            driver?.Stop(this);
            Jump(target);
            apply(target);
            return;
        }

        MoveTo(target, driver.Now, duration, curve);
        driver.Run(this, at =>
        {
            apply(ValueAt(at));
            return MovingAt(at);
        });
    }
}
