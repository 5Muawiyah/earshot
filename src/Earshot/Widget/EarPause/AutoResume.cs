namespace Earshot.Widget.EarPause;

// The session Earshot paused when a bud came out, and what it takes for that same session to be resumed when the bud
// goes back. Container is the output the pause was made on. LeftWasIn and RightWasIn are the buds that were in the ear
// just before the pause: the ones that have to be back. EchoEndsAt is when Windows' own report of this pause stops
// being expected.
internal sealed record RememberedPause(
    string SessionId, string AppId, bool LeftWasIn, bool RightWasIn, Guid Container, DateTimeOffset PausedAt, DateTimeOffset EchoEndsAt);

// One bud's last in-ear value and when the reading carrying it was taken.
internal readonly record struct InEarState(bool? Value, DateTimeOffset? At);

internal enum ResumeVerdict
{
    NothingRemembered,
    Wait,
    Resume,
    Cancel,
}

internal sealed record ResumeDecision(ResumeVerdict Verdict, RememberedPause? Pause, string? Reason);

// Stage two of ear detection, with no clock, no media session and no log of its own: it remembers at most one pause and
// answers, from the facts it is given, whether to wait, to resume, or to forget it. Whatever it forgets is never
// resumed later, so a sound can only start while the person is in the moment of putting the bud back.
//
// Resume needs all of these, and any failure forgets the pause for good:
//   - the pause is no more than WidgetTiming.ResumeWindow old: long enough to take a bud out for a short word, short
//     enough that sound does not start by surprise minutes later;
//   - the setting is still on;
//   - the AirPods are still this PC's output, on the same container the pause was made on;
//   - nothing was played or paused by hand since: no change was reported for the paused session after Windows' own
//     report of Earshot's pause, and none for any other session;
//   - every bud that was in the ear before the pause is in the ear again, on in-ear values that are fresh.
// The caller still checks, with a read of the sessions, that the paused session reads Paused and that no other session
// is playing, because the change report is not guaranteed to be raised for every change.
internal sealed class AutoResume
{
    private readonly Lock _gate = new();
    private RememberedPause? _pending;

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    public RememberedPause? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    // A newer pause replaces an older one: it can only exist if the older session was started again, which cancels it.
    public void Remember(RememberedPause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        lock (_gate)
        {
            _pending = pause;
        }
    }

    // True when there was a pause to forget.
    public bool Forget()
    {
        lock (_gate)
        {
            bool had = _pending is not null;
            _pending = null;
            return had;
        }
    }

    // A playback change Windows reported for sessionId. Returns the reason when this forgot the remembered pause.
    public string? NoteSessionChanged(string sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_gate)
        {
            if (_pending is not { } pending)
            {
                return null;
            }

            if (!string.Equals(sessionId, pending.SessionId, StringComparison.Ordinal))
            {
                _pending = null;
                return "another session changed since the pause";
            }

            // Windows reports Earshot's own pause shortly after it is made. Anything later is the person.
            if (now <= pending.EchoEndsAt)
            {
                return null;
            }

            _pending = null;
            return "the paused session changed since the pause";
        }
    }

    // The AirPods stopped being this PC's output (they left, or the default output moved).
    public string? NoteOutputGone()
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return null;
            }

            _pending = null;
            return "the AirPods are no longer this PC's output";
        }
    }

    // Called with a fresh reading of the chosen set. Resume and Cancel both take the pause out of the memory, so a
    // pause is acted on at most once.
    public ResumeDecision Evaluate(
        DateTimeOffset now,
        DateTimeOffset readingAt,
        bool settingOn,
        bool rendersToAirPods,
        Guid watchedContainer,
        InEarState left,
        InEarState right)
    {
        lock (_gate)
        {
            if (_pending is not { } pending)
            {
                return new ResumeDecision(ResumeVerdict.NothingRemembered, null, null);
            }

            if (!settingOn)
            {
                return CancelLocked("the setting was turned off");
            }

            if (now - pending.PausedAt > WidgetTiming.ResumeWindow)
            {
                return CancelLocked("the bud was not back in time");
            }

            if (!rendersToAirPods || watchedContainer != pending.Container)
            {
                return CancelLocked("the AirPods are no longer this PC's output");
            }

            // A reading that is not fresh is not acted on. It is not a cancel either: the next one may be.
            if (now - readingAt > WidgetTiming.EarFreshWindow)
            {
                return new ResumeDecision(ResumeVerdict.Wait, null, null);
            }

            if (!BackIn(pending.LeftWasIn, left, now) || !BackIn(pending.RightWasIn, right, now))
            {
                return new ResumeDecision(ResumeVerdict.Wait, null, null);
            }

            _pending = null;
            return new ResumeDecision(ResumeVerdict.Resume, pending, null);
        }
    }

    // A bud that was not in the ear before the pause does not have to come back.
    private static bool BackIn(bool wasIn, InEarState state, DateTimeOffset now) =>
        !wasIn || (state.Value == true && state.At is DateTimeOffset at && now - at <= WidgetTiming.EarFreshWindow);

    private ResumeDecision CancelLocked(string reason)
    {
        _pending = null;
        return new ResumeDecision(ResumeVerdict.Cancel, null, reason);
    }
}
