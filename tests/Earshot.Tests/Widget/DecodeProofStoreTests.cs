using System.Text.Json;
using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.SetupRecordFixtures;

namespace Earshot.Tests.Widget;

// Where the decode table lives at run time: derived from the records at start and after each new one, and the
// one observation that is not derived from records, kept in proof.json.
[TestClass]
public sealed class DecodeProofStoreTests : IDisposable
{
    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private TestTimeProvider _clock = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _clock = new TestTimeProvider();
    }

    public void Dispose() => _temp.Dispose();

    private string SetupFolder => _temp.File("setup");

    private string ProofFile => _temp.File("proof.json");

    private DecodeProofStore NewStore() => new(SetupFolder, ProofFile, _log, _clock);

    private static string Members(JsonElement element) => string.Join(",", element.EnumerateObject().Select(p => p.Name));

    private static BatterySetupRecord HighIsRight(int minutes) => Record(Message(high: 8, low: 4), Picks(left: 40, right: 80), minutes);

    [TestMethod]
    public void TheTableIsDerivedFromTheRecordsAtConstruction()
    {
        var records = new BatterySetupStore(SetupFolder, _log);
        Assert.IsNotNull(records.Save(HighIsRight(0)));
        Assert.IsNotNull(records.Save(HighIsRight(1)));

        DecodeProofStore store = NewStore();

        Assert.AreEqual(true, store.Table.HighNibbleIsRight, "Two agreeing records on disk prove the order at start.");
        Assert.HasCount(2, store.Records);
        Assert.IsTrue(File.Exists(ProofFile), "The summary is written at construction too.");
    }

    [TestMethod]
    public void WithNoRecordsTheTableIsTheUnprovedOne()
    {
        DecodeProofStore store = NewStore();

        Assert.AreEqual(ProximityDecodeTable.Unproved, store.Table);
        Assert.IsNull(store.BroadcastsWhilePlayingFromThisPc, "Not observed: null, never false.");
        Assert.IsNull(store.Newest);
    }

    [TestMethod]
    public void AddRecordReEvaluatesAndWritesProofJson()
    {
        DecodeProofStore store = NewStore();
        Assert.IsNull(store.Table.HighNibbleIsRight);

        store.AddRecord(HighIsRight(0));
        Assert.IsNull(store.Table.HighNibbleIsRight, "One record proves nothing.");
        store.AddRecord(HighIsRight(1));

        Assert.AreEqual(true, store.Table.HighNibbleIsRight);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ProofFile));
        JsonElement root = document.RootElement;
        Assert.AreEqual("schemaVersion,evaluatedAtUtc,records,fields,broadcastWhilePlayingFromThisPc,notes", Members(root));
        Assert.AreEqual(2, root.GetProperty("records").GetInt32());
        JsonElement fields = root.GetProperty("fields");
        Assert.AreEqual(
            "highNibbleIsRight,flipBit,caseNibble,caseChargingBit,rightChargingBit,leftChargingBit,leftInEarBit,rightInEarBit,lid", Members(fields));
        JsonElement order = fields.GetProperty("highNibbleIsRight");
        Assert.AreEqual("Proved", order.GetProperty("status").GetString());
        Assert.IsTrue(order.GetProperty("value").GetBoolean());
        Assert.AreEqual(2, order.GetProperty("agree").GetInt32());
        Assert.AreEqual(0, order.GetProperty("disagree").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, fields.GetProperty("flipBit").GetProperty("value").ValueKind);
        Assert.AreEqual("Proved", fields.GetProperty("caseNibble").GetProperty("status").GetString(), "Two records whose case nibble agrees with the pick prove the case too.");
        Assert.AreEqual("NotProvableBySetup", fields.GetProperty("lid").GetProperty("status").GetString());
        JsonElement broadcast = root.GetProperty("broadcastWhilePlayingFromThisPc");
        Assert.AreEqual("ownedMessages,firstAtUtc,lastAtUtc,proved", Members(broadcast));
        Assert.IsFalse(broadcast.GetProperty("proved").GetBoolean());
    }

    [TestMethod]
    public void AStatusChangeIsLoggedOnceWithNoBytes()
    {
        DecodeProofStore store = NewStore();

        store.AddRecord(HighIsRight(0));
        Assert.IsEmpty(_log.Entries.Where(e => e.Message.StartsWith("Decode proof", StringComparison.Ordinal)), "One record changes no status.");
        store.AddRecord(HighIsRight(1));
        store.AddRecord(HighIsRight(2));

        LogEntry[] lines = _log.Entries.Where(e => e.Message.StartsWith("Decode proof", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, lines, "The order and the case became proved once each; a third agreeing record changes nothing.");
        Assert.IsTrue(lines.All(l => l.Level == Earshot.Contracts.LogLevel.Info));
        Assert.AreEqual("Decode proof: HighNibbleIsRight Proved (2 agree, 0 disagree).", lines[0].Message);
        Assert.AreEqual("Decode proof: CaseNibble Proved (2 agree, 0 disagree).", lines[1].Message);
    }

    [TestMethod]
    public void ADisagreeingRecordWithdrawsTheOrderAndWarns()
    {
        DecodeProofStore store = NewStore();
        store.AddRecord(HighIsRight(0));
        store.AddRecord(HighIsRight(1));
        BatterySetupRecord disagreeing = Record(Message(high: 4, low: 8), Picks(left: 40, right: 80), 2);

        store.AddRecord(disagreeing);

        Assert.IsNull(store.Table.HighNibbleIsRight, "The buds stop decoding at once.");
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "Decode proof: HighNibbleIsRight Withdrawn (2 agree, 1 disagree)."));
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "Decode proof: bud order withdrawn; " + disagreeing.FileName + " disagrees with the proved order."));
    }

    // Numbers, statuses and file names only: no byte, no sender tag, no model or colour byte.
    [TestMethod]
    public void NoLogLineCarriesABytesLikeRun()
    {
        DecodeProofStore store = NewStore();
        store.AddRecord(HighIsRight(0));
        store.AddRecord(HighIsRight(1));
        store.AddRecord(Record(Message(high: 4, low: 8), Picks(left: 40, right: 80), 2));
        for (int i = 0; i < 30; i++)
        {
            store.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 3));
        }

        Assert.IsNotEmpty(_log.Entries);
        foreach (LogEntry entry in _log.Entries)
        {
            Assert.IsFalse(Regex.IsMatch(entry.Message, "[0-9A-F]{5,}"), "A hex run in: " + entry.Message);
            Assert.DoesNotContain("EEEE", entry.Message);
        }
    }

    [TestMethod]
    public void TwentyOwnedMessagesOverSixtySecondsProveTheBroadcast()
    {
        DecodeProofStore store = NewStore();

        // Every three seconds: the twentieth message is at 57 s, the twenty-first at 60 s.
        for (int i = 0; i <= 19; i++)
        {
            store.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 3));
        }

        Assert.IsNull(store.BroadcastsWhilePlayingFromThisPc, "Twenty messages, but over 57 s.");

        store.NoteOwnedWhileThisPcRenders(Start.AddSeconds(60));

        Assert.AreEqual(true, store.BroadcastsWhilePlayingFromThisPc);
        Assert.AreEqual(1, _log.Entries.Count(e => e.Message.StartsWith("Broadcast while playing from this PC", StringComparison.Ordinal)));
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Info, "Broadcast while playing from this PC: 21 owned messages over 60 s; proved."));

        store.NoteOwnedWhileThisPcRenders(Start.AddSeconds(63));
        Assert.AreEqual(1, _log.Entries.Count(e => e.Message.StartsWith("Broadcast while playing from this PC", StringComparison.Ordinal)), "Logged once.");
    }

    [TestMethod]
    public void NineteenMessagesOrFiftyNineSecondsDoNot()
    {
        DecodeProofStore few = NewStore();
        for (int i = 0; i < 19; i++)
        {
            few.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 5));   // 19 messages over 90 s
        }

        Assert.IsNull(few.BroadcastsWhilePlayingFromThisPc, "Nineteen is not twenty.");

        using var other = new TempFolder();
        var quick = new DecodeProofStore(other.File("setup"), other.File("proof.json"), _log, _clock);
        for (int i = 0; i < 25; i++)
        {
            quick.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 2.4));   // 25 messages over 57.6 s
        }

        Assert.IsNull(quick.BroadcastsWhilePlayingFromThisPc, "Twenty-five messages over less than a minute is not a minute.");
        quick.NoteOwnedWhileThisPcRenders(Start.AddSeconds(59.9));
        Assert.IsNull(quick.BroadcastsWhilePlayingFromThisPc, "Still under sixty seconds.");
        quick.NoteOwnedWhileThisPcRenders(Start.AddSeconds(61));
        Assert.AreEqual(true, quick.BroadcastsWhilePlayingFromThisPc, "Sixty seconds and more than twenty messages.");
    }

    // Twenty messages spread over days would say nothing about the broadcast continuing during one stretch of
    // playing: a gap between two counted messages ends the run.
    [TestMethod]
    public void TheCountRestartsAfterAGap()
    {
        DecodeProofStore store = NewStore();
        for (int i = 0; i < 15; i++)
        {
            store.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 2));      // 0..28 s
        }

        for (int i = 0; i < 15; i++)
        {
            store.NoteOwnedWhileThisPcRenders(Start.AddSeconds(90 + (i * 2)));   // after a 62 s gap: a new run
        }

        Assert.IsNull(store.BroadcastsWhilePlayingFromThisPc, "Fifteen and fifteen are not thirty in a row.");
        Assert.AreEqual(15, store.OwnedMessagesCounted, "The count restarted at the gap.");
    }

    [TestMethod]
    public void TheObservationSurvivesARestartThroughProofJson()
    {
        DecodeProofStore first = NewStore();
        for (int i = 0; i <= 21; i++)
        {
            first.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 3));
        }

        Assert.AreEqual(true, first.BroadcastsWhilePlayingFromThisPc);

        DecodeProofStore restarted = NewStore();

        Assert.AreEqual(true, restarted.BroadcastsWhilePlayingFromThisPc, "Read back from proof.json at start.");
    }

    [TestMethod]
    public void APartialCountIsReadBackAtStart()
    {
        DecodeProofStore first = NewStore();
        first.NoteOwnedWhileThisPcRenders(Start);

        DecodeProofStore restarted = NewStore();

        Assert.AreEqual(1, restarted.OwnedMessagesCounted);
        Assert.IsNull(restarted.BroadcastsWhilePlayingFromThisPc);
    }

    [TestMethod]
    public void AMissingProofJsonCostsOnlyTheObservation()
    {
        var records = new BatterySetupStore(SetupFolder, _log);
        Assert.IsNotNull(records.Save(HighIsRight(0)));
        Assert.IsNotNull(records.Save(HighIsRight(1)));
        DecodeProofStore first = NewStore();
        for (int i = 0; i <= 21; i++)
        {
            first.NoteOwnedWhileThisPcRenders(Start.AddSeconds(i * 3));
        }

        File.Delete(ProofFile);
        DecodeProofStore restarted = NewStore();

        Assert.AreEqual(true, restarted.Table.HighNibbleIsRight, "The table is derived from the records, not from the summary.");
        Assert.IsNull(restarted.BroadcastsWhilePlayingFromThisPc, "Only the observation restarts.");
    }

    [TestMethod]
    public void AnUnreadableProofJsonIsLoggedAndCostsOnlyTheObservation()
    {
        var records = new BatterySetupStore(SetupFolder, _log);
        Assert.IsNotNull(records.Save(HighIsRight(0)));
        Assert.IsNotNull(records.Save(HighIsRight(1)));
        File.WriteAllText(ProofFile, "{ this is not json");

        DecodeProofStore store = NewStore();

        Assert.AreEqual(true, store.Table.HighNibbleIsRight);
        Assert.IsNull(store.BroadcastsWhilePlayingFromThisPc);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "decode proof summary is not valid"));
    }

    // A hand-written summary that claims the broadcast is proved is believed only when it is well formed;
    // nothing in it can prove a bud order, which is always re-derived from the records.
    [TestMethod]
    public void AHandWrittenSummaryCannotProveABudOrder()
    {
        File.WriteAllText(
            ProofFile,
            "{\"schemaVersion\":1,\"evaluatedAtUtc\":\"2026-09-01T00:00:00+00:00\",\"records\":9,\"fields\":{\"highNibbleIsRight\":{\"status\":\"Proved\",\"value\":true,\"agree\":9,\"disagree\":0}}," +
            "\"broadcastWhilePlayingFromThisPc\":{\"ownedMessages\":0,\"firstAtUtc\":null,\"lastAtUtc\":null,\"proved\":false},\"notes\":[]}");

        DecodeProofStore store = NewStore();

        Assert.IsNull(store.Table.HighNibbleIsRight, "The table comes from the records, not from what the summary says.");
    }
}
