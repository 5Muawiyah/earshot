namespace Earshot.Widget;

public enum AirPodsWhere { Unknown, ThisPc, Elsewhere, NotInUse }

public enum WidgetWatcherState { NotStarted, Started, Stopped, Off }   // Off: the setting is off

public sealed record WidgetSnapshot(
    AirPodsWhere Where,
    PartReading Left,
    PartReading Right,
    PartReading Case,
    DateTimeOffset? BatteryReadAt,      // the oldest ReadAt among the parts that have one
    DateTimeOffset? EarReadAt,          // the last owned reading that carried a proved in-ear bit, while fresh
    bool? LidOpen,
    WidgetWatcherState Watcher,
    int? WatcherErrorCode, string? WatcherErrorName,
    bool ClaimExists,
    bool AutoPauseAvailable,            // the gate constant is true and the in-ear bits are proved
    WidgetCounters Counters)
{
    public static WidgetSnapshot Empty(WidgetWatcherState watcher, bool claimExists) => new(
        AirPodsWhere.Unknown, PartReading.Unknown, PartReading.Unknown, PartReading.Unknown,
        BatteryReadAt: null, EarReadAt: null, LidOpen: null, watcher, WatcherErrorCode: null, WatcherErrorName: null,
        claimExists, AutoPauseAvailable: false, WidgetCounters.Empty);
}

public sealed class CaseOpenedEventArgs(DateTimeOffset at) : EventArgs
{
    public DateTimeOffset At { get; } = at;
}

// Every owned reading, decoded, as WidgetStatusService applied it to its own state (the point right after
// ApplyDecodedReadingLocked, which only an Owned verdict reaches). Deliberately carries no
// OwnershipVerdict: reaching this event at all already means Owned, and keeping the verdict enum itself
// out of this shape means it does not move when OwnershipVerdict's own members do.
public sealed class OwnedReadingEventArgs(DecodedReading reading, DateTimeOffset at) : EventArgs
{
    public DecodedReading Reading { get; } = reading;

    public DateTimeOffset At { get; } = at;
}
