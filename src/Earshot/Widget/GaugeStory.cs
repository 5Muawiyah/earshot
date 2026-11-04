namespace Earshot.Widget;

// What the gauge tells of a connect or disconnect that is running.
internal enum GaugeStoryKind
{
    // Nothing to tell: the gauge shows what is true.
    None,

    // The toggle is running: the number leaves and a sweep turns round the ring.
    Spinning,

    // A connect succeeded: a tick in the number's place.
    Tick,

    // A connect or a disconnect failed: a cross in the number's place.
    Cross,
}

// The story as the gauge draws it. Idle is always default(GaugeStoryView), so a push key built from it is stable.
// Connect says which way the toggle was going, for the words of the tooltip.
internal readonly record struct GaugeStoryView(GaugeStoryKind Kind, bool Connect = false);

// Where a toggle was started from. The gauge ignores it; what a card shows of a toggle depends on it.
internal enum ToggleOrigin
{
    // A shortcut, the menu, the gauge, the tray icon or Send to.
    Elsewhere,

    // The button on the card.
    Card,
}

// How a toggle ended, as the gauge cares about it.
internal enum ToggleOutcome
{
    // The link is now what was asked for.
    Succeeded,

    // The attempt did not get there.
    Failed,

    // The attempt was stopped (a newer click, Exit, a session end): nothing is said of it.
    Cancelled,
}

// The gauge's connect story, as a state machine:
//
//   Idle -> Spinning(connect | disconnect) -> Tick (800 ms) | Cross (1500 ms) | Idle
//
// A connect that succeeds ticks; a connect or a disconnect that fails shows a cross; a disconnect that succeeds and a toggle that
// is cancelled go straight to Idle, since what is true is then shown as it is. The hold times are design choices: a tick is
// glanced at, and a failure earns a longer look.
//
// Pure: every call is handed the time, and the story reads no clock and owns no timer. A spin has no end of its own: only Ended
// stops it, and the caller sees to it that Ended always comes. A hold has an end, NextChange, for the caller to set one timer
// at. UI thread only.
internal sealed class GaugeStory
{
    public static readonly TimeSpan TickHold = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan CrossHold = TimeSpan.FromMilliseconds(1500);

    private GaugeStoryKind _kind;
    private bool _connect;
    private DateTimeOffset _holdEnds;

    // Where the toggle that is being told was started from.
    public ToggleOrigin Origin { get; private set; }

    // When the story next changes without being told, or null when it changes only when told: a hold's end.
    public DateTimeOffset? NextChange => _kind is GaugeStoryKind.Tick or GaugeStoryKind.Cross ? _holdEnds : null;

    // What the gauge tells at now.
    public GaugeStoryView View(DateTimeOffset now) => _kind switch
    {
        GaugeStoryKind.Spinning => new GaugeStoryView(GaugeStoryKind.Spinning, _connect),
        GaugeStoryKind.Tick or GaugeStoryKind.Cross when now < _holdEnds => new GaugeStoryView(_kind, _connect),
        _ => default,
    };

    // A toggle began, in the direction it wants. A story already running (a hold, or a spin the caller did not end) is replaced.
    public void Started(bool connect, ToggleOrigin origin, DateTimeOffset now)
    {
        _ = now;
        _kind = GaugeStoryKind.Spinning;
        _connect = connect;
        Origin = origin;
    }

    // The toggle that was started has ended. An end with no spin to end is ignored: a result nobody was shown starting has no
    // mark to show.
    public void Ended(ToggleOutcome outcome, DateTimeOffset now)
    {
        if (_kind != GaugeStoryKind.Spinning)
        {
            return;
        }

        switch (outcome)
        {
            case ToggleOutcome.Succeeded when _connect:
                _kind = GaugeStoryKind.Tick;
                _holdEnds = now + TickHold;
                break;
            case ToggleOutcome.Failed:
                _kind = GaugeStoryKind.Cross;
                _holdEnds = now + CrossHold;
                break;
            default:
                _kind = GaugeStoryKind.None;
                break;
        }
    }
}
