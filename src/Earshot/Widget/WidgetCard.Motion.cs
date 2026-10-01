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

        _exiting = true;
        _animator.Exit(_rest, _travel, FinishExit);
    }

    private void FinishExit()
    {
        _exiting = false;
        if (IsDisposed)
        {
            return;
        }

        // Back at rest and opaque for the next show, which sets its own starting frame.
        if (IsHandleCreated)
        {
            RestoreOpacity();
            _ = NativeMethods.SetWindowPos(Handle, 0, _rest.X, _rest.Y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        Hide();
    }

    private void RestoreOpacity()
    {
        if (_layered && IsHandleCreated)
        {
            _ = NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
        }
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
        public void Apply(Rectangle rest, int offsetPx, byte alpha)
        {
            if (card.IsDisposed || card._motionBroken)
            {
                return;
            }

            nint handle = card.Handle;
            if (!NativeMethods.SetWindowPos(handle, 0, rest.X, rest.Y + offsetPx, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
            {
                card.GiveUpMotion("set-window-pos:widget-card-motion", Marshal.GetLastPInvokeError());
                return;
            }

            if (card._layered && !NativeMethods.SetLayeredWindowAttributes(handle, 0, alpha, NativeMethods.LWA_ALPHA))
            {
                card.GiveUpMotion("set-layered-window-attributes:widget-card-motion", Marshal.GetLastPInvokeError());
            }
        }
    }

    private void GiveUpMotion(string step, int win32Error)
    {
        _motionBroken = true;
        string problem = TrayReport.DescribeStep(StepOutcomes.FromWin32(step, (uint)win32Error));
        if (problem != _lastMotionProblem)
        {
            _lastMotionProblem = problem;
            _log.Warn("The widget card cannot slide in and out, so it is shown and hidden without motion: " + problem);
        }
    }
}
