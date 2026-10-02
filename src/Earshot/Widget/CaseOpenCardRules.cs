using Earshot.Interop;
using Earshot.Popup;

namespace Earshot.Widget;

// Counts the opens a case-open card has been asked to show, so work that was queued for an earlier open (a close timer that
// fired just as the card was shown again, a close posted before a reopen) can tell it is stale and do nothing. The count is
// taken on the calling thread, where the request is made, not on the UI thread when it runs, so a request still waiting in
// the UI queue already makes everything older stale.
internal sealed class OpenGeneration
{
    private int _current;

    public int Current => Volatile.Read(ref _current);

    // A new open was asked for: everything captured before is stale. Returns the new value.
    public int Next() => Interlocked.Increment(ref _current);

    public bool IsCurrent(int captured) => captured == Current;
}

// Decisions of the case-open card that need no window, so they can be tested without one.
internal static class CaseOpenCardRules
{
    // Whether a press of Connect or Disconnect closes the card. The gauge's own card closes (its job is done); the case-open
    // notice stays, since it closes only by its setting, its close button or the case closing, and its button turns to
    // Disconnect or Connecting in place as the connection changes.
    public static bool ConnectClosesCard(bool notice) => !notice;

    // Whether the card shown without a display list (Windows could not list the displays) may be shown in this notification
    // state: the full-screen states hide cards per display, and with no display list there is no display to leave out.
    public static bool FallbackMayShow(int state) => CardPresenter.AcceptsUnrequestedCard(state);

    // Whether a card that is open must close at a recheck of the full-screen state. Fails closed as the show does: a failed
    // read closes it. A card with no display (placed without a display list) answers to the global state alone.
    public static bool ClosesOnRecheck(int hResult, int state, int displayCount, ForegroundWindowReading? foreground, DisplayInfo? display)
    {
        if (hResult < 0)
        {
            return true;
        }

        return display is null
            ? !FallbackMayShow(state)
            : CaseOpenCardFullScreen.Covers(state, Math.Max(1, displayCount), foreground, display);
    }
}
