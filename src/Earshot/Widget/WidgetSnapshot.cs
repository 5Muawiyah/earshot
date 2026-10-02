namespace Earshot.Widget;

public enum AirPodsWhere { Unknown, ThisPc, Elsewhere, NotInUse }

public enum WidgetWatcherState { NotStarted, Started, Stopped, Off }   // Off: the setting is off

public sealed record WidgetSnapshot(
    AirPodsWhere Where,
    PartReading Left,
    PartReading Right,
    PartReading Case,
    DateTimeOffset? BatteryReadAt,      // the newest ReadAt among the parts that have one
    DateTimeOffset? EarReadAt,          // the last reading of the chosen set that carried an in-ear bit, while fresh
    bool? LidOpen,
    WidgetWatcherState Watcher,
    int? WatcherErrorCode, string? WatcherErrorName,
    bool AutoPauseAvailable,            // the decode table has in-ear bits: always false until they are known
    WidgetCounters Counters)
{
    // Windows' own Hands-Free battery figure for the AirPods: the percent and when it was read, nothing else. It is
    // one figure for the headset, never a bud's or the case's, and BatteryFreshness decides whether it is shown.
    // Not part of the positional shape, so a snapshot built without it reads Unknown.
    public PartReading Headset { get; init; } = PartReading.Unknown;

    // Whether a set of the broadcast is linked to the owner's AirPods (a case was opened near the PC and the set has not
    // been lost for too long). Listening: a paired model is known and nothing is linked.
    public BroadcastSelectionState Selection { get; init; } = BroadcastSelectionState.NoPairedModel;

    // The owner's pair's last readings and learned charge rates, as saved across restarts (LastReadingStore), and the
    // paired model they are shown for: BatteryFreshness shows a saved reading only when its model is this one.
    internal LastReadingBook LastReadings { get; init; } = LastReadingBook.Empty;

    public ushort? PairedModel { get; init; }

    // The latest time the service has worked anything out at. An estimate is never worked out for an earlier time, so a
    // clock that is put back holds it where it was rather than letting it fall.
    public DateTimeOffset? EstimateClock { get; init; }

    public static WidgetSnapshot Empty(WidgetWatcherState watcher) => new(
        AirPodsWhere.Unknown, PartReading.Unknown, PartReading.Unknown, PartReading.Unknown,
        BatteryReadAt: null, EarReadAt: null, LidOpen: null, watcher, WatcherErrorCode: null, WatcherErrorName: null,
        AutoPauseAvailable: false, WidgetCounters.Empty);
}

public sealed class CaseOpenedEventArgs(DateTimeOffset at) : EventArgs
{
    public DateTimeOffset At { get; } = at;
}

public sealed class CaseClosedEventArgs(DateTimeOffset at) : EventArgs
{
    public DateTimeOffset At { get; } = at;
}

// Every reading of the chosen set, decoded, as WidgetStatusService applied it to its own state.
public sealed class ReadingAppliedEventArgs(DecodedReading reading, DateTimeOffset at, long selectionGeneration = 0) : EventArgs
{
    public DecodedReading Reading { get; } = reading;

    public DateTimeOffset At { get; } = at;

    // How many times the chosen set has changed when this reading was applied (a first choice, a switch to another set,
    // a set chosen again after every chosen sender went quiet, a new paired model). Two readings with different values
    // come from different sets, so what one set showed is never compared with what another does.
    public long SelectionGeneration { get; } = selectionGeneration;
}
