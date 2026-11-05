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

    // A connect that took, with the audio quality protection after it not applied (connected, not protected). The gauge claims nothing
    // either way: no tick that would say it all worked and no cross that would say they did not connect. The notice the coordinator
    // raises says what did not take, and it may be refused (presentation mode, quiet time), so nothing relies on it for the gauge.
    // A disconnect whose block did not take is never Partial: the nodes stay enabled and the PC can page the AirPods, so it is Failed.
    // Its cross waits for a gauge on screen to draw it (GaugeStory.Ended, mustBeSeen), and the failure is held for the next card he
    // asks for and spoken, so it is said whether or not a notice is shown or a gauge was hidden at the time.
    Partial,

    // The attempt was stopped (a newer click, Exit, a session end): nothing is said of it.
    Cancelled,
}

// The gauge's connect story, as a state machine:
//
//   Idle -> Spinning(connect | disconnect) -> Tick (800 ms) | Cross (1500 ms) | Idle
//
// A connect that succeeds ticks; a connect or a disconnect that fails shows a cross; a disconnect that succeeds and a toggle that
// is cancelled go straight to Idle, since what is true is then shown as it is; so does a connect that ends Partial (connected, not
// protected): no tick that says all worked, no cross that says they did not connect. The hold times are design choices: a tick is
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
    private bool _awaitingSight;

    // Where the toggle that is being told was started from.
    public ToggleOrigin Origin { get; private set; }

    // A cross that has to be seen has not yet been drawn by a gauge on screen: it has no end until one does.
    public bool AwaitingSight => _kind == GaugeStoryKind.Cross && _awaitingSight;

    // When the story next changes without being told, or null when it changes only when told: a hold's end.
    public DateTimeOffset? NextChange => (_kind is GaugeStoryKind.Tick or GaugeStoryKind.Cross) && !_awaitingSight ? _holdEnds : null;

    // What the gauge tells at now.
    public GaugeStoryView View(DateTimeOffset now) => _kind switch
    {
        GaugeStoryKind.Spinning => new GaugeStoryView(GaugeStoryKind.Spinning, _connect),
        GaugeStoryKind.Cross when _awaitingSight => new GaugeStoryView(GaugeStoryKind.Cross, _connect),
        GaugeStoryKind.Tick or GaugeStoryKind.Cross when now < _holdEnds => new GaugeStoryView(_kind, _connect),
        _ => default,
    };

    // A toggle began, in the direction it wants. A story already running (a hold, or a spin the caller did not end) is replaced.
    public void Started(bool connect, ToggleOrigin origin, DateTimeOffset now)
    {
        _ = now;
        _kind = GaugeStoryKind.Spinning;
        _connect = connect;
        _awaitingSight = false;
        Origin = origin;
    }

    // The toggle that was started has ended. An end with no spin to end is ignored: a result nobody was shown starting has no
    // mark to show. mustBeSeen is a failure the person has to be told (a disconnect whose block did not take): its cross is not
    // timed from now but from the first moment a gauge on screen draws it (Seen), since a gauge hidden for a presentation or a
    // full-screen game would otherwise let the whole hold pass unseen.
    public void Ended(ToggleOutcome outcome, DateTimeOffset now, bool mustBeSeen = false)
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
                _awaitingSight = mustBeSeen;
                break;
            default:
                _kind = GaugeStoryKind.None;
                break;
        }
    }

    // A gauge on screen drew the cross that was waiting for one: its hold runs from now. False when none was waiting.
    public bool Seen(DateTimeOffset now)
    {
        if (!AwaitingSight)
        {
            return false;
        }

        _awaitingSight = false;
        _holdEnds = now + CrossHold;
        return true;
    }

    // The cross that was waiting to be seen is no longer owed (it was said another way, or it is over): the story goes idle.
    public bool Dismiss()
    {
        if (!AwaitingSight)
        {
            return false;
        }

        _awaitingSight = false;
        _kind = GaugeStoryKind.None;
        return true;
    }
}
