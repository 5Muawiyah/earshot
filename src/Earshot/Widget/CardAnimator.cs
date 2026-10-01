using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// What the animator moves: the window, set to a rest position plus an offset and a constant alpha. The real one
// is in WidgetCard.Motion.cs; a test records the calls.
internal interface ICardWindowMotion
{
    void Apply(Rectangle rest, int offsetPx, byte alpha);
}

// Drives a CardMotionPlan with a timer on the given clock: a tick about every frame asks the plan for the frame at
// the elapsed time and applies it, and stops when the motion is over. An entrance during an exit, and the other
// way round, starts from where the card is. With animation effects off there is no timer and no movement: the card
// is shown at rest, or hidden, in one step.
//
// Not thread safe: the timer's tick is handed to uiPost, and everything else is called on the UI thread.
internal sealed class CardAnimator : IDisposable
{
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly TimeProvider _time;
    private readonly ICardWindowMotion _sink;
    private readonly Func<bool> _animationsEnabled;
    private readonly Action<Action> _uiPost;
    private ITimer? _timer;
    private CardMotionPlan? _plan;
    private Rectangle _rest;
    private Action? _hidden;
    private long _startedAt;
    private MotionFrame _last = new(0, 0, Done: true);
    private int _generation;
    private bool _disposed;

    public CardAnimator(TimeProvider time, ICardWindowMotion sink, Func<bool> animationsEnabled, Action<Action> uiPost)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(animationsEnabled);
        ArgumentNullException.ThrowIfNull(uiPost);
        _time = time;
        _sink = sink;
        _animationsEnabled = animationsEnabled;
        _uiPost = uiPost;
    }

    // True while a motion is running.
    public bool Running => _timer is not null;

    // The last frame applied, for tests.
    internal MotionFrame LastFrame => _last;

    // Brings the card in from travel pixels away. The card is applied at that offset with no opacity at once.
    public void Enter(Rectangle rest, int travel)
    {
        if (_disposed)
        {
            return;
        }

        _rest = rest;
        _hidden = null;
        if (!_animationsEnabled())
        {
            Stop();
            Apply(CardMotion.FrameAt(CardMotion.EnterFromBelow(travel), TimeSpan.Zero, reducedMotion: true));
            return;
        }

        CardMotionPlan plan = _plan is { Kind: MotionKind.Exit } && Running
            ? CardMotion.Enter(travel, _last.OffsetPx, _last.Alpha / 255.0)
            : CardMotion.EnterFromBelow(travel);
        Begin(plan);
    }

    // Takes the card away, towards travel pixels away and fading out, then calls hidden once it is gone. With
    // animation effects off hidden is called at once.
    public void Exit(Rectangle rest, int travel, Action hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        if (_disposed)
        {
            return;
        }

        _rest = rest;
        if (!_animationsEnabled())
        {
            Stop();
            _hidden = null;
            // The card is left at rest and opaque, as the next entrance expects to find it, and then hidden.
            Apply(new MotionFrame(0, 255, Done: true));
            hidden();
            return;
        }

        bool entering = _plan is { Kind: MotionKind.Enter } && Running;
        CardMotionPlan plan = entering
            ? CardMotion.Exit(travel, _last.OffsetPx, _last.Alpha / 255.0)
            : CardMotion.ExitFromRest(travel);
        _hidden = hidden;
        Begin(plan);
    }

    // The card was put somewhere else while it moves (its page changed size): carry on from the same frame there.
    public void Rebase(Rectangle rest)
    {
        _rest = rest;
        if (Running)
        {
            _sink.Apply(rest, _last.OffsetPx, _last.Alpha);
        }
    }

    // Ends any motion without a further frame, for a card that is going away for another reason.
    public void Cancel()
    {
        Stop();
        _hidden = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _hidden = null;
    }

    private void Begin(CardMotionPlan plan)
    {
        Stop();
        _plan = plan;
        _startedAt = _time.GetTimestamp();
        int generation = ++_generation;
        Apply(CardMotion.FrameAt(plan, TimeSpan.Zero));
        _timer = _time.CreateTimer(_ => _uiPost(() => Tick(generation)), null, FrameInterval, FrameInterval);
    }

    private void Tick(int generation)
    {
        if (_disposed || generation != _generation || _plan is not { } plan)
        {
            return;
        }

        MotionFrame frame = CardMotion.FrameAt(plan, _time.GetElapsedTime(_startedAt));
        Apply(frame);
        if (!frame.Done)
        {
            return;
        }

        Stop();
        Action? hidden = _hidden;
        _hidden = null;
        hidden?.Invoke();
    }

    private void Apply(MotionFrame frame)
    {
        _last = frame;
        _sink.Apply(_rest, frame.OffsetPx, frame.Alpha);
    }

    private void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

// Where "animation effects" is read from: SystemParametersInfo with SPI_GETCLIENTAREAANIMATION, asked at each show
// and hide so a change applies on the next one without an event. If the call fails the card does not move, and
// the raw code is logged (once for each distinct code).
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
internal sealed class SystemAnimationSetting(ILog log)
{
    private int _lastError = -1;

    public bool Enabled()
    {
        if (Shell.SystemParametersInfoForBool(Shell.SPI_GETCLIENTAREAANIMATION, 0, out int on, 0))
        {
            return on != 0;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error != _lastError)
        {
            _lastError = error;
            log.Warn("The animation effects setting could not be read (SystemParametersInfoW with SPI_GETCLIENTAREAANIMATION, Win32 error " + error.ToString(System.Globalization.CultureInfo.InvariantCulture) + "), so the card does not move.");
        }

        return false;
    }
}
