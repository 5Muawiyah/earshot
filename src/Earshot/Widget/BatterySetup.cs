using System.Text.Json.Serialization;

namespace Earshot.Widget;

public enum BatterySetupStage { Listening, Pick, Done, Failed }

public enum BatterySetupListenStatus
{
    Found,             // a candidate with at least one documented-form message; a claim can be drafted
    ShortFormOnly,     // a record written by an earlier build: a candidate that sent only forms the parser does not read; no claim
    NotFound,          // no documented-form set reached the minimum message count in the window (short-form senders never count)
    Ambiguous,         // two sets too close in signal, or another set clears the derived threshold
    WatcherNotStarted, // Bluetooth off or the watcher stopped
    Cancelled
}

// What one listening window produced. The candidate carries its own messages only; other senders are counts.
public sealed record BatterySetupListen(
    BatterySetupListenStatus Status,
    string Message,                        // WidgetCopy text for the Failed view, or ""
    BatterySetupCandidate? Candidate,      // Found only (ShortFormOnly is no longer produced by a listen)
    IReadOnlyList<BatterySetupSenderSummary> OtherSenders,   // one summary per other documented-form set
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long AppleSectionsSeen,
    long ProximityItemsSeen);

// The candidate is one set of AirPods: every documented-form sender that SetupSenderGroups merged, so Messages, the
// signal figures and Captures cover all of them. OtherFormMessages is zero for a set-up made by this build (a
// short-form message is never the candidate's); earlier records may hold more.
public sealed record BatterySetupCandidate(
    int Messages, int OkFormMessages, int OtherFormMessages,
    sbyte RssiMin, sbyte RssiMedian, sbyte RssiMax,
    sbyte ThresholdDbm,                    // RssiMin minus SetupRules.SignalMarginDb, clamped to sbyte
    IReadOnlyList<BatterySetupCapture> Captures,
    ProximityMessage? LastOkMessage,       // the newest documented-form message of any of the senders; the claim is drafted from it
    IReadOnlyList<string>? SenderTags = null,                        // each merged sender's tag (a per-run keyed hash), eight upper-case hex digits
    IReadOnlyList<ProximityMessage>? SenderLastOkMessages = null,    // each merged sender's newest documented-form message, in SenderTags order
    IReadOnlyList<BatterySetupCapture>? ShortFormCaptures = null);   // messages of forms the parser does not read, from any sender: evidence only

// One message the candidate sent. ValueHex is the documented form's first nine bytes (prefix to reserved; the
// encrypted bytes are never copied), or the whole item value for any other form.
// SenderTag is the sender's tag for this run only (a keyed hash of its address that no later run can reproduce), so a
// study of a record can tell which sender a message came from.
public sealed record BatterySetupCapture(DateTimeOffset AtUtc, sbyte Rssi, byte? Prefix, int Length, string ValueHex, string? SenderTag = null);

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
// NotSaved: the record could not be written, so nothing was proved or claimed.
public enum BatterySetupResultStatus { CaseSetUp, BatterySetUp, CaseSetUpBudsSame, SavedNeedsAnother, NotSaved, CouldNotRead }

public sealed record BatterySetupResult(BatterySetupResultStatus Status, string RecordFileName, DecodeProofResult Proof);
