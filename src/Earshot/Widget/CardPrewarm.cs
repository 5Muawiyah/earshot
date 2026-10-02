namespace Earshot.Widget;

// Decides when the gauge's card is built ahead of the first click, so that click opens as fast as the later ones: window
// creation, font and layout measuring and the first drawing are paid once, out of sight, while Earshot is otherwise idle.
//
// Armed once, when the first taskbar layout has been applied (the tray is running and the gauge has had its place), and
// the build runs Delay later on the UI thread: a design choice, long enough that start-up work (the first reads, the first
// gauge frame) has finished before it, short enough that an owner clicking soon after start-up still finds it done. It runs
// at most once, and never after Dispose. The warm action itself must show and activate nothing.
internal sealed class CardPrewarm : IDisposable
{
    // Design choice: see above.
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly Action<Action> _uiPost;
    private readonly Action _warm;
    private readonly TimeSpan _delay;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private bool _armed;
    private bool _disposed;

    public CardPrewarm(TimeProvider time, Action<Action> uiPost, Action warm, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(warm);
        _time = time;
        _uiPost = uiPost;
        _warm = warm;
        _delay = delay ?? Delay;
    }

    // How many times the warm action ran, for tests.
    internal int RunCount { get; private set; }

    // Starts the wait. A second call, or one after Dispose, does nothing.
    public void Arm()
    {
        lock (_gate)
        {
            if (_armed || _disposed)
            {
                return;
            }

            _armed = true;
            _timer = _time.CreateTimer(_ => _uiPost(Run), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Run()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed || RunCount > 0)
            {
                return;
            }

            RunCount++;
        }

        _warm();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
