namespace Earshot.Widget;

// Waiting budgets, injected so tests drive a TimeProvider. Each is a design choice made here, not a
// measurement, until its own use notes that a phase 0 result replaces it.
public static class WidgetTiming
{
    // How fresh an owned reading has to be for the ear state (Elsewhere/NotInUse) to be shown at all.
    public static readonly TimeSpan EarFreshWindow = TimeSpan.FromSeconds(10);

    // How long a claiming run collects advertisements before deciding.
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan WatcherRetryDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan WatcherRetryLimit = TimeSpan.FromMinutes(15);

    // How long a bud has to return to the ear for auto-pause's second stage to resume what it paused.
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);

    // How often the counters line (counts, unknown-form shapes and the watcher state, never a byte) is
    // logged while anything has changed since the last one.
    public static readonly TimeSpan CountersLogInterval = TimeSpan.FromMinutes(1);
}
