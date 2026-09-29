using Earshot.Contracts;

namespace Earshot.App;

// What started a connect or disconnect. Only ever written to the log line of the switch, never acted on.
internal enum SwitchTrigger
{
    Click,
    Menu,
    ShortcutToggle,
    ShortcutToPc,
    ShortcutToPhone,
}

// Which way a connect reached the AirPods. None when it did not get far enough to say.
internal enum SwitchPath
{
    None,
    Already,           // render was ACTIVE when the request began: nothing was sent
    Direct,            // the render endpoints were present: the one-shot reconnect was sent at once
    AllowFirst,        // every render endpoint was NOTPRESENT: the nodes were allowed, then the reconnect was sent
    HandsFreeAssisted, // the A2DP filter refused, so protection came off for one more try
}

// What a connect that did not reach ACTIVE left of what it changed.
internal enum SwitchBlockedAgain
{
    NotNeeded, // nothing had been allowed or turned off
    Yes,       // the nodes are blocked again, or read blocked
    No,        // Block at boot is off, or the block did not take or was held back
}

// The phases a switch is measured in. Each is a duration from one moment to another, in the order the connect or
// disconnect takes them, and is empty when the path taken did not run it.
internal enum SwitchPhase
{
    Queued,     // accepted to the operation beginning: not zero when it waited behind another
    FirstPass,  // the first connect attempt
    Status,     // the read of the nodes before an allow
    Allow,      // the allow through the gate
    Endpoints,  // the allow ending to a render endpoint being present
    Connect,    // the connect attempt after an allow
    Protection, // the protection check after ACTIVE
    Block,      // the block that follows a disconnect
}

// The measurement of one connect or disconnect the tray started, taken from one monotonic clock so a step of the
// wall clock cannot bend a figure. It is created when the request is accepted, filled in by the coordinator as the
// operation runs, and formatted by SwitchTimelineText. It reads a clock and writes to itself and to nothing else: a
// clock that throws costs the figures and one log line, never the operation, and every stamp is taken outside
// anything that guards device work.
//
// Never a field of the coordinator: a second request can be accepted while the first runs, and each has its own.
// https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.gettimestamp
internal sealed class SwitchTimeline
{
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly long _accepted;
    private readonly Dictionary<SwitchPhase, TimeSpan> _phases = new();
    private bool _clockFailed;
    private TimeSpan? _active;
    private TimeSpan? _released;
    private TimeSpan? _atRest;

    public SwitchTimeline(TimeProvider time, ILog log, bool connect, SwitchTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _time = time;
        _log = log;
        Connect = connect;
        Trigger = trigger;
        _accepted = Stamp();
        AcceptedUtc = ReadUtc();
    }

    public bool Connect { get; }

    public SwitchTrigger Trigger { get; }

    // The wall-clock moment the request was accepted, for placing the line in time. Never used to measure.
    public DateTimeOffset AcceptedUtc { get; }

    public SwitchPath Path { get; set; } = SwitchPath.None;

    public SwitchBlockedAgain BlockedAgain { get; set; } = SwitchBlockedAgain.NotNeeded;

    // How the operation ended, once Complete has run.
    public OpStatus? Outcome { get; private set; }

    public bool Cancelled { get; private set; }

    public TimeSpan? Total { get; private set; }

    // Connect: render ACTIVE was reached, and how long after acceptance it was first known (null when the clock
    // failed, which is not the same as not reached).
    public bool ActiveReached { get; private set; }

    public TimeSpan? ActiveAfter => _active;

    // Disconnect: the release was seen, and the nodes read Blocked, each with the time since acceptance.
    public bool ReleasedSeen { get; private set; }

    public TimeSpan? ReleasedAfter => _released;

    public bool AtRest { get; private set; }

    public TimeSpan? AtRestAfter => _atRest;

    // Fixed words for why a released switch is not at rest, or null.
    public string? NotAtRestReason { get; private set; }

    // True when the clock or the formatter threw while this timeline was being filled in, so a missing figure is a
    // clock that failed and not a phase that did not run.
    public bool ClockFailed => _clockFailed;

    public TimeSpan? Phase(SwitchPhase phase) => _phases.TryGetValue(phase, out TimeSpan span) ? span : null;

    // A moment to measure a phase from. -1 when the clock failed.
    public long Mark() => Stamp();

    // Records the time from mark to now as the phase. A phase that runs twice adds up. Nothing on a failed mark.
    public void Record(SwitchPhase phase, long mark)
    {
        if (mark < 0)
        {
            return;
        }

        long now = Stamp();
        if (now < 0 || !TryElapsed(mark, now, out TimeSpan span))
        {
            return;
        }

        _phases[phase] = _phases.TryGetValue(phase, out TimeSpan before) ? before + span : span;
    }

    // The operation began running (it was not waiting behind another any more).
    public void CoreBegan()
    {
        long now = Stamp();
        if (now >= 0 && TryElapsed(_accepted, now, out TimeSpan span))
        {
            _phases[SwitchPhase.Queued] = span;
        }
    }

    // Render is ACTIVE, first known now. The first call wins: a later, slower confirmation does not move it.
    public void MarkActive()
    {
        ActiveReached = true;
        _active ??= SinceAccepted();
    }

    public void MarkReleased()
    {
        ReleasedSeen = true;
        _released ??= SinceAccepted();
    }

    // The nodes read Blocked, after the block had run or because they already were.
    public void MarkAtRest()
    {
        AtRest = true;
        _atRest ??= SinceAccepted();
        NotAtRestReason = null;
    }

    public void MarkNotAtRest(string reason)
    {
        AtRest = false;
        _atRest = null;
        NotAtRestReason = reason;
    }

    // The operation ended with this report.
    public void Complete(OpStatus status, bool cancelled)
    {
        Outcome = status;
        Cancelled = cancelled;
        Total = SinceAccepted();
    }

    private TimeSpan? SinceAccepted()
    {
        long now = Stamp();
        return now >= 0 && TryElapsed(_accepted, now, out TimeSpan span) ? span : null;
    }

    private long Stamp()
    {
        try
        {
            return _time.GetTimestamp();
        }
        catch (Exception ex)
        {
            NoteClockFailure(ex);
            return -1;
        }
    }

    private DateTimeOffset ReadUtc()
    {
        try
        {
            return _time.GetUtcNow();
        }
        catch (Exception ex)
        {
            NoteClockFailure(ex);
            return DateTimeOffset.MinValue;
        }
    }

    private bool TryElapsed(long from, long to, out TimeSpan span)
    {
        if (from < 0)
        {
            span = default;
            return false;
        }

        try
        {
            span = _time.GetElapsedTime(from, to);
            return true;
        }
        catch (Exception ex)
        {
            NoteClockFailure(ex);
            span = default;
            return false;
        }
    }

    // Logged once, with the exception type. This guards a clock, not COM, CfgMgr32, Bluetooth or Task Scheduler.
    private void NoteClockFailure(Exception ex)
    {
        if (_clockFailed)
        {
            return;
        }

        _clockFailed = true;
        _log.Warn("Switch timing: the clock failed (" + ex.GetType().Name + "), so this switch has no figures. The switch itself is not affected.");
    }
}
