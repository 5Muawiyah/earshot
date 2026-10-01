using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tray;

namespace Earshot.Widget;

// The card's entrance and exit on screen. A card with motion attached is shown by sliding in from one taskbar
// thickness away while fading up, and hidden by sliding back while fading out (CardMotion has the numbers); without
// it, or with animation effects off, it is shown and hidden in one step, as it always was.
//
// The fade is a constant window opacity: the card is a layered window and SetLayeredWindowAttributes sets its alpha.
// Form.Opacity is not used; it does the same thing but also owns the style bit and fights this code over it.
internal sealed partial class WidgetCard
{
    private CardAnimator? _animator;
    private bool _layered;
    private bool _motionBroken;
    private bool _exiting;
    private Rectangle _rest;
    private int _travel;
    private string? _lastMotionProblem;

    // The two window calls motion makes. They are the real calls; a test replaces one to make it fail. A failed call's raw
    // code is read from the last P/Invoke error straight after it returns false.
    internal Func<nint, int, int, bool> MoveWindow = static (handle, x, y) =>
        NativeMethods.SetWindowPos(handle, 0, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    internal Func<nint, byte, bool> SetWindowAlpha = static (handle, alpha) =>
        NativeMethods.SetLayeredWindowAttributes(handle, 0, alpha, NativeMethods.LWA_ALPHA);

    // Gives the card motion. Called before the card has a window handle: the layered style is chosen when the
    // handle is created.
    internal void AttachMotion(TimeProvider time, Action<Action> uiPost, Func<bool> animationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(animationsEnabled);
        _animator?.Dispose();
        _layered = CardMotion.UseAlphaFade;
        _animator = new CardAnimator(time, new WindowMotion(this), animationsEnabled, uiPost);
    }

    // True when the style bit for the fade is wanted at handle creation.
    private bool WantsLayeredStyle => _animator is not null && _layered;

    // True from the moment the card starts to leave until it is hidden.
    internal bool IsExiting => _exiting;

    // Whether the card was given motion, for tests.
    internal bool HasMotion => _animator is not null;

    // Whether a motion is running, for tests.
    internal bool IsMoving => _animator?.Running ?? false;

    // Where the card rests: its bounds, except while it is sliding, when the window is somewhere along the way.
    internal Rectangle RestBounds => IsMoving ? _rest : Bounds;

    // Puts the card at rest at bounds, even while it is sliding.
    internal void PlaceAtRest(Rectangle bounds)
    {
        _rest = bounds;
        Bounds = bounds;
        _animator?.Rebase(bounds);
    }

    // Shows the card at rest at rest, travelling in from travel pixels away (negative for a taskbar at the top).
    // The window starts invisible and out at the far end of the travel, so there is no frame at rest before the slide.
    internal void PresentAnimated(Rectangle rest, int travel)
    {
        _rest = rest;
        _travel = travel;
        _exiting = false;
        if (_animator is null || _motionBroken)
        {
            Bounds = rest;
            RestoreOpacity();
            Show();
            return;
        }

        Bounds = rest;
        _ = Handle;
        _animator.Enter(rest, travel);
        Show();
    }

    // Hides the card, sliding it away and fading it out first when it has motion and animation effects are on.
    internal void HideAnimated()
    {
        if (!Visible)
        {
            return;
        }

        if (_animator is null || _motionBroken || !IsHandleCreated)
        {
            Hide();
            return;
        }

        if (_exiting)
        {
            return;
        }

        // A press that began before the exit is over with it, and nothing pressed from here on is answered (OnKeyDown,
        // OnMouseDown and OnMouseUp ask IsExiting): the card has been closed, so it must not connect or toggle anything.
        _exiting = true;
        ClearPressedFlags();
        _animator.Exit(_rest, _travel, FinishExit);
    }

    private void FinishExit()
    {
        _exiting = false;
        if (IsDisposed)
        {
            return;
        }

        // Hidden first, then back at rest and opaque for the next show, which sets its own starting frame. The other way round
        // the card, faded out and slid away, is made fully opaque and put at rest while it is still on screen: a frame of the
        // whole card at the end of every close.
        Hide();
        if (IsHandleCreated)
        {
            PutAtRestAndOpaque();
        }
    }

    // The card where it rests and fully opaque. A failed call is logged with its raw code.
    private void PutAtRestAndOpaque()
    {
        RestoreOpacity();
        if (!MoveWindow(Handle, _rest.X, _rest.Y))
        {
            RecordMotionProblem("set-window-pos:widget-card-rest", Marshal.GetLastPInvokeError(), "the card may be left off its place");
        }
    }

    // Makes the card opaque. A layered window whose opacity was never set is not drawn at all, so when the call fails the
    // card gives the layered style up (the handle is made again without it) rather than stay on screen and invisible.
    private void RestoreOpacity()
    {
        if (!_layered || !IsHandleCreated)
        {
            return;
        }

        if (SetWindowAlpha(Handle, 255))
        {
            return;
        }

        RecordMotionProblem("set-layered-window-attributes:widget-card-opacity", Marshal.GetLastPInvokeError(), "the card gives up its fade so that it is drawn");
        _layered = false;
        _motionBroken = true;
        RecreateHandle();
    }

    private void StopMotion()
    {
        _animator?.Dispose();
        _animator = null;
        _exiting = false;
    }

    // The window, moved and faded to a frame. A failure is logged with its raw code and the card gives up motion for
    // good, showing and hiding at rest from then on.
    private sealed class WindowMotion(WidgetCard card) : ICardWindowMotion
    {
        public bool Apply(Rectangle rest, int offsetPx, byte alpha)
        {
            if (card.IsDisposed || card._motionBroken)
            {
                return false;
            }

            nint handle = card.Handle;
            if (!card.MoveWindow(handle, rest.X, rest.Y + offsetPx))
            {
                card.GiveUpMotion("set-window-pos:widget-card-motion", Marshal.GetLastPInvokeError());
                return false;
            }

            if (card._layered && !card.SetWindowAlpha(handle, alpha))
            {
                card.GiveUpMotion("set-layered-window-attributes:widget-card-motion", Marshal.GetLastPInvokeError());
                return false;
            }

            return true;
        }
    }

    // A window call of the motion failed: the card gives up motion for good and is left where it rests and opaque, so it
    // is shown, and hidden, like a card with no motion. Without that, a card whose fade had begun would stay on screen,
    // active and drawn at no opacity at all.
    private void GiveUpMotion(string step, int win32Error)
    {
        _motionBroken = true;
        RecordMotionProblem(step, win32Error, "the card cannot slide in and out, so it is shown and hidden without motion");
        if (IsHandleCreated)
        {
            PutAtRestAndOpaque();
        }
    }

    private void RecordMotionProblem(string step, int win32Error, string consequence)
    {
        string problem = TrayReport.DescribeStep(StepOutcomes.FromWin32(step, (uint)win32Error));
        string line = "The widget card: " + consequence + ": " + problem;
        if (line != _lastMotionProblem)
        {
            _lastMotionProblem = line;
            _log.Warn(line);
        }
    }
}
