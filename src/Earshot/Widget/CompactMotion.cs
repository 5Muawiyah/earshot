namespace Earshot.Widget;

// How the old content and the new cross when the card changes form or its compact row is replaced.
//   Sequential: a change of form (compact to full, full to compact). The old content fades out over the first 83 ms of 250 and
//     the new fades in over 83 ms from 167 ms; a fold (full to compact) runs the same crossfade compressed to 167 ms.
//   Cross: a compact row replaced in place by another: a straight crossfade over 83 ms.
// Durations are the design's motion table ("Motion"); every fade is linear.
internal enum FormFadeKind { Sequential, Cross }

internal static class CompactMotion
{
    // Compact to full, the height and the crossfade.
    public static TimeSpan GrowDuration => FluentMotion.Slow;

    // Full to compact.
    public static TimeSpan FoldDuration => FluentMotion.Normal;

    // A compact row replaced in place.
    public static TimeSpan ReplaceDuration => FluentMotion.Fast;

    // The take back button's glyph, ring and tick crossfade into one another.
    public static TimeSpan ActionFadeDuration => FluentMotion.Fast;

    // The tick stays this long before the card folds (the motion table's tick hold).
    public static readonly TimeSpan TickHold = TimeSpan.FromMilliseconds(800);

    // A ring turns once in this long.
    public static readonly TimeSpan TurnDuration = TimeSpan.FromMilliseconds(1000);

    // The opacity of the old and of the new content at progress (0 to 1) of the whole change.
    public static (double Old, double New) Alphas(FormFadeKind kind, double progress)
    {
        double p = Math.Clamp(double.IsFinite(progress) ? progress : 1, 0, 1);
        if (kind == FormFadeKind.Cross)
        {
            return (1 - p, p);
        }

        double fade = FluentMotion.Fast / FluentMotion.Slow;
        double inStart = (FluentMotion.Normal / FluentMotion.Slow);
        double oldAlpha = 1 - Math.Clamp(p / fade, 0, 1);
        double newAlpha = Math.Clamp((p - inStart) / fade, 0, 1);
        return (oldAlpha, newAlpha);
    }
}

// How long a compact row has left to hold, with the pointer able to stop the clock: pointer over pauses it, leaving resumes it with
// the time that remained. Pure: the caller says what time it is (in milliseconds on any steady clock). A hold of null is a row that
// stays until it is closed, and never elapses.
internal sealed class CompactHold
{
    private TimeSpan? _remaining;
    private long _at;
    private bool _paused;

    // True once a hold has been started (and not stopped), whether or not it is paused.
    public bool Active { get; private set; }

    public bool Paused => _paused;

    // Starts, or restarts, the hold of hold from now. Null stops it: the row stays.
    public void Start(TimeSpan? hold, long nowMs)
    {
        Active = hold is not null;
        _remaining = hold;
        _at = nowMs;
        _paused = false;
    }

    public void Stop()
    {
        Active = false;
        _remaining = null;
        _paused = false;
    }

    public void Pause(long nowMs)
    {
        if (!Active || _paused)
        {
            return;
        }

        _remaining = RemainingAt(nowMs);
        _paused = true;
    }

    public void Resume(long nowMs)
    {
        if (!Active || !_paused)
        {
            return;
        }

        _at = nowMs;
        _paused = false;
    }

    // What is left at nowMs, or null when there is no hold. Never below zero.
    public TimeSpan? RemainingAt(long nowMs)
    {
        if (!Active || _remaining is not { } left)
        {
            return null;
        }

        if (_paused)
        {
            return left;
        }

        TimeSpan rest = left - TimeSpan.FromMilliseconds(Math.Max(0, nowMs - _at));
        return rest < TimeSpan.Zero ? TimeSpan.Zero : rest;
    }

    // Whether the hold has run out at nowMs. A paused hold never does.
    public bool ElapsedAt(long nowMs) => Active && !_paused && RemainingAt(nowMs) == TimeSpan.Zero;
}
