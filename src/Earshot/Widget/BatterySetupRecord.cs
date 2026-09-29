using System.Globalization;
using System.Text.Json.Serialization;

namespace Earshot.Widget;

// Everything one set-up saw, kept as one file under the widget's data folder: the candidate's messages, the signal
// statistics, the derived threshold, what the owner said his iPhone showed, and the outcome. A file is written once
// and never replaced (BatterySetupStore.Save refuses to overwrite one).
//
// A message of the documented form is kept as its first nine bytes only, never the encrypted bytes after them. A
// message of any other form is kept whole, as received, because it is the only evidence of what that form is; Earshot
// does not know what those bytes are, so it cannot promise what they do not hold. There is no field here for a
// device address, a sender tag or a name, and none is ever passed in. The picks are evidence for DecodeProof only.
public sealed record BatterySetupRecord(
    int SchemaVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    BatterySetupListenStatus ListenStatus,
    string AppVersion,
    long AppleSectionsSeen,
    long ProximityItemsSeen,
    BatterySetupCandidate? Candidate,
    IReadOnlyList<BatterySetupSenderSummary> OtherSenders,
    BatterySetupPicks Picks)
{
    public const int CurrentSchemaVersion = 1;

    // The file this record is kept in. Digits are separated by dots, so a log line that names it has no run of digits
    // that could be read as payload bytes.
    [JsonIgnore]
    public string FileName => "setup-" + EndedAtUtc.UtcDateTime.ToString("yyyy'.'MM'.'dd'T'HH'.'mm'.'ss'Z'", CultureInfo.InvariantCulture) + ".json";

    // The reason a record is not usable, or null when every value is one a set-up could have written. A
    // hand-written or damaged file is refused here rather than trusted.
    internal string? Problem()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return "schema version " + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ", not 1";
        }

        if (ListenStatus is not (BatterySetupListenStatus.Found or BatterySetupListenStatus.ShortFormOnly))
        {
            return "a listen status that is never saved";
        }

        if (EndedAtUtc < StartedAtUtc)
        {
            return "an end before its start";
        }

        if (AppVersion is null || AppVersion.Length > 64)
        {
            return "no usable app version";
        }

        if (AppleSectionsSeen < 0 || ProximityItemsSeen < 0)
        {
            return "a negative count";
        }

        if (Picks is null || !Picks.IsValid)
        {
            return "picks outside 0 to 100 in steps of 10";
        }

        if (OtherSenders is null || OtherSenders.Any(s => s is null || s.Messages < 0 || s.OkFormMessages < 0 || s.OkFormMessages > s.Messages || s.RssiMedian > 0))
        {
            return "a bad other-sender summary";
        }

        if (Candidate is not { } c)
        {
            return "no candidate";
        }

        if (c.Messages < SetupRules.MinMessages || c.OkFormMessages < 0 || c.OtherFormMessages < 0 || c.OkFormMessages + c.OtherFormMessages != c.Messages)
        {
            return "message counts that do not add up";
        }

        if (c.RssiMin > c.RssiMedian || c.RssiMedian > c.RssiMax || c.RssiMax > 0 || c.RssiMin < -127)
        {
            return "signal figures out of order or out of range";
        }

        if (c.ThresholdDbm > c.RssiMin || c.ThresholdDbm < -127)
        {
            return "a threshold above the weakest message or out of range";
        }

        if (ListenStatus == BatterySetupListenStatus.Found && (c.LastOkMessage is null || c.OkFormMessages < 1))
        {
            return "a Found set-up with no documented-form message";
        }

        if (ListenStatus == BatterySetupListenStatus.ShortFormOnly && (c.LastOkMessage is not null || c.OkFormMessages != 0))
        {
            return "a short-form set-up that holds a documented-form message";
        }

        if (c.Captures is null || c.Captures.Count > c.Messages)
        {
            return "more captures than messages";
        }

        foreach (BatterySetupCapture capture in c.Captures)
        {
            if (capture is null || capture.ValueHex is null || capture.ValueHex.Length % 2 != 0 || capture.Length < 0 || capture.Length > 255
                || capture.ValueHex.Length > 2 * capture.Length || !IsUpperHex(capture.ValueHex))
            {
                return "a capture that is not upper-case hex within its length";
            }

            bool documentedForm = capture.Prefix == 0x01 && capture.Length == 25;
            if (documentedForm && capture.ValueHex.Length != 18)
            {
                return "a documented-form capture that is not exactly nine bytes";
            }
        }

        return null;
    }

    private static bool IsUpperHex(string text)
    {
        foreach (char ch in text)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}
