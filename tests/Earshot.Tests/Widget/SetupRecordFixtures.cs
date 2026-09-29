using Earshot.Widget;

namespace Earshot.Tests.Widget;

// Synthetic battery set-up records and claims for the widget's tests. Nothing here is read from a device: the
// model, colour and battery bytes are the fixtures' own, and every message is built from WidgetFixtures.
// WidgetFixtureHygieneTests scans this file like the rest of the folder.
internal static class SetupRecordFixtures
{
    public static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // One documented-form message as a ProximityMessage. The nibbles are the ones a test cares about:
    // high and low are BatteryA's two bud nibbles, caseNibble the low nibble of BatteryB, and chargingBits the
    // high nibble of BatteryB.
    public static ProximityMessage Message(int high, int low, int caseNibble = 5, int chargingBits = 0, byte status = 0x00) =>
        new(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, status, (byte)((high << 4) | low), (byte)((chargingBits << 4) | caseNibble), 0x00, WidgetFixtures.Colour, 0x00);

    public static BatterySetupPicks Picks(int left, int right, int box = 50, bool leftCharging = false, bool rightCharging = false, bool caseCharging = false) =>
        new(left, right, box, leftCharging, rightCharging, caseCharging);

    // A usable Found record: the candidate's newest documented-form message and the picks the owner made.
    // minutes moves its end time, so records sort and name themselves in the order a test builds them.
    public static BatterySetupRecord Record(ProximityMessage message, BatterySetupPicks picks, int minutes = 0)
    {
        DateTimeOffset end = Start.AddMinutes(minutes);
        var candidate = new BatterySetupCandidate(
            Messages: 5, OkFormMessages: 5, OtherFormMessages: 0, RssiMin: -60, RssiMedian: -58, RssiMax: -55, ThresholdDbm: -70,
            Captures: [], LastOkMessage: message);
        return new BatterySetupRecord(
            1, end.AddSeconds(-20), end, BatterySetupListenStatus.Found, "1.1.0", 40, 12, candidate,
            [new BatterySetupSenderSummary(4, 4, -79)], picks);
    }

    // A record whose candidate sent only forms the parser does not read: no message to decode.
    public static BatterySetupRecord ShortFormRecord(BatterySetupPicks picks, int minutes = 0)
    {
        DateTimeOffset end = Start.AddMinutes(minutes);
        var candidate = new BatterySetupCandidate(
            Messages: 5, OkFormMessages: 0, OtherFormMessages: 5, RssiMin: -60, RssiMedian: -58, RssiMax: -55, ThresholdDbm: -70,
            Captures: [], LastOkMessage: null);
        return new BatterySetupRecord(
            1, end.AddSeconds(-20), end, BatterySetupListenStatus.ShortFormOnly, "1.1.0", 40, 12, candidate, [], picks);
    }

    // Records that prove the bud order: two set-ups where the high nibble is the right bud (right 8, left 4).
    public static IReadOnlyList<BatterySetupRecord> TwoRecordsProvingHighIsRight() =>
    [
        Record(Message(high: 8, low: 4), Picks(left: 40, right: 80), minutes: 0),
        Record(Message(high: 6, low: 9), Picks(left: 90, right: 60), minutes: 1),
    ];

    public static WidgetClaim Claim(OwnedBattery? last = null, bool namedOrder = false, sbyte threshold = -70) => new(
        SchemaVersion: 2,
        ModelHigh: WidgetFixtures.ModelHigh,
        ModelLow: WidgetFixtures.ModelLow,
        Colour: WidgetFixtures.Colour,
        SignalThresholdDbm: threshold,
        SignalMinDbm: -60,
        SignalMedianDbm: -58,
        SignalMaxDbm: -55,
        SignalSamples: 12,
        SetupRecord: "setup-2026.09.27T00.00.00Z.json",
        ClaimedAtUtc: Start,
        Last: last ?? new OwnedBattery(null, null, null, Start),
        NibblesAreNamedOrder: namedOrder);
}
