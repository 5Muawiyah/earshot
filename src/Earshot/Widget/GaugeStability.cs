namespace Earshot.Widget;

// How long a verdict about the taskbar must hold before the gauge acts on it. One read of the taskbar is a snapshot of a bar
// that Explorer lays out again whenever a window, a display or the notification area changes, so a gauge that hid, moved or
// was removed on one read followed every flicker of the bar. A field log (47 hours, one extra display) showed the gauge
// there hidden 1,373 times and removed 325 times with nothing the owner could see having changed.
//
// A verdict holds when the same value has been read on `reads` consecutive reads and the first of them is `window` old. The
// reads stop a quiet stretch from deciding on one snapshot; the time stops a run of fast event reads (the watcher coalesces
// to 250 ms) from deciding inside one burst. Both must hold.
//
// The windows below are the longest disturbance the log showed, with room to spare, against the shortest change that was
// real. Nothing here is a hide, a move or a removal for a full-screen window: those are not readings of the bar and stay
// immediate.
internal static class GaugeStability
{
    // Hide for no room, no taskbar or a failed read. Explorer's bursts of ABN_POSCHANGED lasted 1.3 s at most (median 0.13 s)
    // and the slowest read during one took 1.5 s, so two slow reads and a burst fit in about 4.3 s. A real loss of room (a
    // button growing into the gauge's place) lasts minutes.
    public static readonly TimeSpan HideWindow = TimeSpan.FromSeconds(5);
    public const int HideReads = 3;

    // Move to a new position. The notification area grows by 28 px while a full-screen window exists and goes back with it;
    // 7 of the 82 changes the log showed lasted under 5 s (1.4 to 4.5 s), the median run was 54 s. A lasting move is applied
    // within about 5 s.
    public static readonly TimeSpan MoveWindow = TimeSpan.FromSeconds(5);
    public const int MoveReads = 3;

    // Show again after a hide for no room. A shorter wait than the hide's, only so that a room verdict that lasts one read
    // cannot flash the gauge back for the 5 s it then takes to hide again. Showing after any other hide is immediate.
    public static readonly TimeSpan RoomBackWindow = TimeSpan.FromSeconds(2);
    public const int RoomBackReads = 2;

    // Remove the gauge of a display whose taskbar, or the display itself, was not seen. Every absence the log showed apart from
    // one was under 3 s; the one real one (a monitor going away) lasted 155 s. Ten seconds is more than three times the longest
    // transient and costs a real removal ten seconds.
    public static readonly TimeSpan RemoveWindow = TimeSpan.FromSeconds(10);
    public const int RemoveReads = 3;

    // How often the set of other displays' gauges is checked against the displays and taskbars when nothing else asks, so a
    // gauge removed wrongly comes back within a minute even with no read of the main taskbar and no signal. One display
    // enumeration and one window walk: no UI Automation.
    public static readonly TimeSpan SetRecheckInterval = TimeSpan.FromSeconds(60);

    // When the first read that disagrees with the gauge arrives, two more reads are asked for, so the verdict is settled inside
    // its window instead of at the next poll (10 s with the change events installed, 20 s after the slow-read back-off).
    public static readonly TimeSpan[] ConfirmReadDelays = [TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(5)];

    // The same for a taskbar that was not seen: reconciles at 5 s and just past 10 s, so the third sees an absence of over 10 s.
    public static readonly TimeSpan[] RemoveConfirmDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10.5)];

    // Explorer creates the new secondary taskbar some time after the signal that it did.
    public static readonly TimeSpan[] SignalFollowUpDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
}

// A verdict that must hold before it is acted on: the same value on `reads` consecutive observations spanning `window`. Pure:
// the clock is the TimeProvider it is given, and it owns no timer.
internal sealed class HeldVerdict<T>(TimeProvider time, TimeSpan window, int reads) where T : IEquatable<T>
{
    private long _firstStamp;
    private long _lastStamp;

    // A candidate has been observed and not yet cleared.
    public bool Pending { get; private set; }

    public T? Candidate { get; private set; }

    // How many consecutive observations of Candidate there have been.
    public int Count { get; private set; }

    // Since the first observation of Candidate.
    public TimeSpan Age => Pending ? time.GetElapsedTime(_firstStamp) : TimeSpan.Zero;

    // From the first observation of Candidate to the last one.
    public TimeSpan Span => Pending ? time.GetElapsedTime(_firstStamp, _lastStamp) : TimeSpan.Zero;

    // Records one observation. A value that differs from the candidate starts again at count 1. True once the candidate has
    // held: seen on enough consecutive observations, the first of them old enough.
    public bool Observe(T value) => Observe(value, window, reads);

    // The same with a window and a count of this call's own, for a gate that carries verdicts of more than one kind.
    public bool Observe(T value, TimeSpan needWindow, int needReads)
    {
        long now = time.GetTimestamp();
        if (!Pending || !EqualityComparer<T>.Default.Equals(Candidate!, value))
        {
            Pending = true;
            Candidate = value;
            Count = 1;
            _firstStamp = now;
        }
        else
        {
            Count++;
        }

        _lastStamp = now;
        return Count >= needReads && time.GetElapsedTime(_firstStamp, now) >= needWindow;
    }

    // A read that agrees with how things are now: whatever was pending lapses.
    public void Clear()
    {
        Pending = false;
        Candidate = default;
        Count = 0;
    }
}
