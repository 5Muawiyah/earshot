using System.Globalization;
using Earshot.Interop;

namespace Earshot.Widget;

// Counts the unasked openings the presenter has been asked for, so work that was queued for an earlier one (a close timer that
// fired just as the card was shown again, a close posted before a reopen) can tell it is stale and do nothing. The count is
// taken on the calling thread, where the request is made, not on the UI thread when it runs, so a request still waiting in
// the UI queue already makes everything older stale.
internal sealed class OpenGeneration
{
    private int _current;

    public int Current => Volatile.Read(ref _current);

    // A new opening was asked for: everything captured before is stale. Returns the new value.
    public int Next() => Interlocked.Increment(ref _current);

    public bool IsCurrent(int captured) => captured == Current;
}

// What the case-open card is set to, read fresh at each opening: when it closes by itself, which displays show it, and the
// display the gauge is set to, for "where the gauge is". The gauge's position on the taskbar, for a card placed above it,
// comes from the callbacks, as for the asked card.
internal sealed record CaseOpenCardOptions(int CloseSeconds, IReadOnlyList<string> Displays, string GaugeDisplay)
{
    public static CaseOpenCardOptions Default { get; } = new(CaseOpenCardClose.UntilCaseCloses, [], GaugeDisplayChoice.MainDisplay);

    public static CaseOpenCardOptions From(WidgetSettings widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return new CaseOpenCardOptions(widget.CaseOpenCardCloseSeconds, widget.CaseOpenCardDisplays, widget.GaugeDisplay);
    }
}

// The gates one unasked opening must clear before a card shows, checked in this order: the setting, not closing, no asked card
// open, the notification state, then hand-back or a session end, and last the displays one at a time. Every Func is read fresh
// on each request: none of these are cached, since the very point is that a hand-back, a closing tray or a settings change can
// flip one between two openings. A card the owner asked for (the gauge, a shortcut) passes none of them.
internal sealed record UnaskedGate(
    Func<bool> Enabled,               // Settings.Widget.CaseOpenCardOn
    Func<bool> Closing,               // TrayContext._closing
    Func<bool> HandBackInProgress,    // BlockCoordinator.HandBackInProgress
    Func<bool> SessionEndInProgress,  // BlockCoordinator.SessionEndInProgress
    Func<CaseOpenCardOptions>? Options = null) // the close and display choices; the defaults when null
{
    // True for the notification states in which a card nobody asked for may show.
    internal static bool AcceptsUnrequestedCard(int state) =>
        state is Shell.QUNS_ACCEPTS_NOTIFICATIONS or Shell.QUNS_APP;

    internal static string NotificationStateName(int state) => state switch
    {
        Shell.QUNS_NOT_PRESENT => "QUNS_NOT_PRESENT",
        Shell.QUNS_BUSY => "QUNS_BUSY",
        Shell.QUNS_RUNNING_D3D_FULL_SCREEN => "QUNS_RUNNING_D3D_FULL_SCREEN",
        Shell.QUNS_PRESENTATION_MODE => "QUNS_PRESENTATION_MODE",
        Shell.QUNS_ACCEPTS_NOTIFICATIONS => "QUNS_ACCEPTS_NOTIFICATIONS",
        Shell.QUNS_QUIET_TIME => "QUNS_QUIET_TIME",
        Shell.QUNS_APP => "QUNS_APP",
        _ => "QUNS " + state.ToString(CultureInfo.InvariantCulture),
    };

    // Whether the card shown without a display list (Windows could not list the displays) may be shown in this notification
    // state: the full-screen states hide cards per display, and with no display list there is no display to leave out.
    public static bool FallbackMayShow(int state) => AcceptsUnrequestedCard(state);

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

// The desktop as an unasked card needs it: the displays, the gauges shown on them and the foreground window. The real one
// reads Windows; a test hands in fake displays, gauges and a full-screen window.
internal interface IUnaskedScene
{
    // The connected displays; empty only when Windows could not be asked.
    IReadOnlyList<DisplayInfo> Displays();

    // The bounds of every gauge shown now (the main one and those on other displays under All displays).
    IReadOnlyList<Rectangle> Gauges();

    // The foreground window as the full-screen rule reads it, or null.
    ForegroundWindowReading? Foreground(IReadOnlyList<DisplayInfo> displays);
}

// The real scene: the display source the tray works from, the gauges the tray says are shown, and the foreground window.
internal sealed class SystemUnaskedScene(IDisplaySource displays, Func<IReadOnlyList<Rectangle>> gauges) : IUnaskedScene
{
    public IReadOnlyList<DisplayInfo> Displays() => displays.Read().Displays;

    public IReadOnlyList<Rectangle> Gauges() => gauges();

    public ForegroundWindowReading? Foreground(IReadOnlyList<DisplayInfo> list) => SystemDisplaySource.ForegroundWindow(list);
}
