namespace Earshot.Widget;

// Waiting budgets, injected so tests drive a TimeProvider. Each is a design choice made here, not a
// measurement.
public static class WidgetTiming
{
    // How fresh a reading of the chosen set has to be for the ear state (Elsewhere/NotInUse) to be shown at all, and
    // for ear detection to act on it: a bud's in-ear value is used only if it was read no more than this long ago, and
    // an in-to-out change is taken only against a previous value that is itself this fresh. At about one message every
    // 1.7 seconds this spans about six messages, so an old "in" can never pair with a new "out".
    public static readonly TimeSpan EarFreshWindow = TimeSpan.FromSeconds(10);

    // How long a battery refresh listens after restarting the watcher before it gives up: twice the longest gap seen
    // between two messages of one sender (6.14 s) and about four times the longest for a whole set (3.11 s). In use the
    // set sends about one message every 1.7 s, so about seven are expected, and with the case open about four a second.
    // A closed case sends none, so no answer inside the window means the case is shut.
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(12);

    // How often Windows' own Hands-Free figure is read while the AirPods are on this PC. The figure counts as current for
    // two of these (BatteryFreshness.HeadsetCurrentWindow), so one missed read does not drop it.
    public static readonly TimeSpan HeadsetPollInterval = TimeSpan.FromSeconds(60);

    // How often the clock is looked at for a linked set that has been lost for longer than BroadcastRules.LostLimit.
    // Silence brings no message to notice it with, so without this a dropped link would show until the next one. Ten
    // seconds is a small part of the two minutes, so a drop is shown within about that of its time.
    public static readonly TimeSpan LinkCheckInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan WatcherRetryDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan WatcherRetryLimit = TimeSpan.FromMinutes(15);

    // How long a bud has to return to the ear for ear detection to resume what it paused. Long enough to take a bud out
    // for a short word, short enough that sound does not start by surprise minutes later. Measured from the pause; a
    // bud back at exactly this age still resumes, one a moment later does not, and neither ever resumes at a later
    // reading.
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);

    // How long after Earshot pauses a session Windows' own report of that pause may still arrive. A change reported
    // for the paused session inside this time is the pause itself; a later one is something the person did, and it
    // cancels the resume.
    public static readonly TimeSpan OwnPauseEchoWindow = TimeSpan.FromSeconds(2);

    // How often the counters line (counts, unknown-form shapes and the watcher state, never a byte) is
    // logged while anything has changed since the last one.
    public static readonly TimeSpan CountersLogInterval = TimeSpan.FromMinutes(1);
}
