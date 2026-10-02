using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// What the animator moves: the window, set to a rest position plus an offset and a constant alpha. The real one
// is in WidgetCard.Motion.cs; a test records the calls. False says the frame could not be applied and the window has been
// put at rest and made opaque: the motion is over, and an exit goes on to hide the card.
internal interface ICardWindowMotion
{
    bool Apply(Rectangle rest, int offsetPx, byte alpha);
}

// Drives a CardMotionPlan on a frame driver: each frame of the display asks the plan for the frame at the time of that
// refresh and applies it, and the animator stops asking for frames when the motion is over. An entrance during an exit, and
// the other way round, starts from where the card is. With animation effects off nothing runs and nothing moves: the card is
// shown at rest, or hidden, in one step, and no frame is asked for.
//
// UI thread only (the driver's frames arrive there).
internal sealed class CardAnimator : IDisposable
{
    private readonly FrameDriver _driver;
    private readonly ICardWindowMotion _sink;
    private CardMotionPlan? _plan;
    private Rectangle _rest;
    private Action? _hidden;
    private TimeSpan _startedAt;
    private MotionFrame _last = new(0, 255, Done: true);
    private bool _disposed;

    public CardAnimator(FrameDriver driver, ICardWindowMotion sink)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(sink);
        _driver = driver;
        _sink = sink;
    }

    // True while a motion is running.
    public bool Running => _driver.IsRunning(this);

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
        if (!_driver.AnimationsEnabled)
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
        if (!_driver.AnimationsEnabled)
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
        if (Running && !_sink.Apply(rest, _last.OffsetPx, _last.Alpha))
        {
            GiveUp();
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
        _startedAt = _driver.Now;
        if (!Apply(CardMotion.FrameAt(plan, TimeSpan.Zero)))
        {
            GiveUp();
            return;
        }

        _driver.Run(this, Frame);
    }

    // One refresh of the display: the plan's frame at the refresh's time. False when the motion is over.
    private bool Frame(TimeSpan at)
    {
        if (_disposed || _plan is not { } plan)
        {
            return false;
        }

        MotionFrame frame = CardMotion.FrameAt(plan, at - _startedAt);
        if (!Apply(frame))
        {
            GiveUp();
            return false;
        }

        if (!frame.Done)
        {
            return true;
        }

        Stop();
        Action? hidden = _hidden;
        _hidden = null;
        hidden?.Invoke();
        return false;
    }

    private bool Apply(MotionFrame frame)
    {
        _last = frame;
        return _sink.Apply(_rest, frame.OffsetPx, frame.Alpha);
    }

    // A frame could not be applied. The window has been left at rest and opaque, so the motion simply ends: no further
    // frame, and the card is hidden when an exit was under way, as it would have been at its end.
    private void GiveUp()
    {
        Stop();
        Action? hidden = _hidden;
        _hidden = null;
        hidden?.Invoke();
    }

    private void Stop() => _driver.Stop(this);
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
