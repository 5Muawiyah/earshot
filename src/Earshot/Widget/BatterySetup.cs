using System.Text.Json.Serialization;

namespace Earshot.Widget;

public enum BatterySetupStage { Listening, Pick, Done, Failed }

public enum BatterySetupListenStatus
{
    Found,             // a candidate with at least one documented-form message; a claim can be drafted
    ShortFormOnly,     // a candidate that sent only forms the parser does not read; captures kept, no claim
    NotFound,          // no sender reached the minimum message count in the window
    Ambiguous,         // two senders too close in signal, or another sender clears the derived threshold
    WatcherNotStarted, // Bluetooth off or the watcher stopped
    Cancelled
}

// What one listening window produced. The candidate carries its own messages only; other senders are counts.
public sealed record BatterySetupListen(
    BatterySetupListenStatus Status,
    string Message,                        // WidgetCopy text for the Failed view, or ""
    BatterySetupCandidate? Candidate,      // Found and ShortFormOnly only
    IReadOnlyList<BatterySetupSenderSummary> OtherSenders,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long AppleSectionsSeen,
    long ProximityItemsSeen);

public sealed record BatterySetupCandidate(
    int Messages, int OkFormMessages, int OtherFormMessages,
    sbyte RssiMin, sbyte RssiMedian, sbyte RssiMax,
    sbyte ThresholdDbm,                    // RssiMin minus SetupRules.SignalMarginDb, clamped to sbyte
    IReadOnlyList<BatterySetupCapture> Captures,
    ProximityMessage? LastOkMessage);      // the claim is drafted from this one

// One message the candidate sent. ValueHex is the documented form's first nine bytes (prefix to reserved; the
// encrypted bytes are never copied), or the whole item value for any other form.
public sealed record BatterySetupCapture(DateTimeOffset AtUtc, sbyte Rssi, byte? Prefix, int Length, string ValueHex);

public sealed record BatterySetupSenderSummary(int Messages, int OkFormMessages, sbyte RssiMedian);

// What the owner said his iPhone shows: 0 to 100 in steps of 10, and whether each part shows as charging.
// Evidence only: never displayed as a reading, never alerted on, never acted on.
public sealed record BatterySetupPicks(
    int Left, int Right, int Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging)
{
    public static BatterySetupPicks Default { get; } = new(50, 50, 50, false, false, false);

    [JsonIgnore]
    public bool IsValid => IsStep(Left) && IsStep(Right) && IsStep(Case);

    private static bool IsStep(int value) => value is >= 0 and <= 100 && value % 10 == 0;
}

// SavedNeedsAnother: the record is kept and read, but one record proves nothing, so nothing is shown yet.
public enum BatterySetupResultStatus { CaseSetUp, BatterySetUp, CaseSetUpBudsSame, SavedNeedsAnother, CouldNotRead }

public sealed record BatterySetupResult(BatterySetupResultStatus Status, string RecordFileName, DecodeProofResult Proof);
