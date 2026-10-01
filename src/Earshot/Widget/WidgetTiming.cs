namespace Earshot.Widget;

// Waiting budgets, injected so tests drive a TimeProvider. Each is a design choice made here, not a
// measurement.
public static class WidgetTiming
{
    // How fresh a reading of the chosen set has to be for the ear state (Elsewhere/NotInUse) to be shown at all.
    public static readonly TimeSpan EarFreshWindow = TimeSpan.FromSeconds(10);

    // How long a battery refresh listens after restarting the watcher before it gives up: twice the longest gap seen
    // between two messages of one sender (6.14 s) and about four times the longest for a whole set (3.11 s). In use the
    // set sends about one message every 1.7 s, so about seven are expected, and with the case open about four a second.
    // A closed case sends none, so no answer inside the window means the case is shut.
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(12);

    // How often Windows' own Hands-Free figure is read while the AirPods are on this PC. The figure counts as current for
    // two of these (BatteryFreshness.HeadsetCurrentWindow), so one missed read does not drop it.
    public static readonly TimeSpan HeadsetPollInterval = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan WatcherRetryDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan WatcherRetryLimit = TimeSpan.FromMinutes(15);

    // How long a bud has to return to the ear for auto-pause's second stage to resume what it paused.
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);

    // How often the counters line (counts, unknown-form shapes and the watcher state, never a byte) is
    // logged while anything has changed since the last one.
    public static readonly TimeSpan CountersLogInterval = TimeSpan.FromMinutes(1);
}
