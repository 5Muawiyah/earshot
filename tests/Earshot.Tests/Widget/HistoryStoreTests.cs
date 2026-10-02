using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The battery history file: live readings only, at most one sample per part per minute, nothing older than 7 days, a 24 hour
// window with its gaps, no address or name in the file, and a file that cannot be read never written over.
[TestClass]
public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly int[] KeptLevels = [80, 82];
    private static readonly string[] FileMembers = ["SchemaVersion", "Samples", "Part", "Percent", "Charging", "At"];

    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();
    private readonly FixedClock _clock = new(Start);

    public void Dispose() => _temp.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private string FilePath => _temp.File(Path.Combine("widget", "battery-history.json"));

    private HistoryStore NewStore() => new(FilePath, _log, _clock);

    private static PartReading Part(int? percent, bool charging = false) =>
        percent is null ? PartReading.Unknown : new PartReading(percent, charging, null);

    private static DecodedReading Reading(int? left = null, int? right = null, int? box = null, bool charging = false) =>
        new(Part(left, charging), Part(right, charging), Part(box, charging), null, null);

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text);
    }

    [TestMethod]
    public void ALiveReadingIsKeptPerPartWithItsFlagAndTime()
    {
        HistoryStore store = NewStore();

        store.Record(Reading(left: 80, right: 75, box: 60, charging: true), Start);

        HistoryWindow window = store.Query(Start);
        Assert.HasCount(3, window.Samples);
        CollectionAssert.Contains(window.Samples.ToList(), new HistorySample(ChargeComponent.Left, 80, true, Start));
        CollectionAssert.Contains(window.Samples.ToList(), new HistorySample(ChargeComponent.Case, 60, true, Start));

        HistoryStore again = NewStore();
        Assert.HasCount(3, again.Query(Start).Samples, "It comes back from the file.");
    }

    [TestMethod]
    public void APartTheMessageGivesNoLevelForIsNotRecorded()
    {
        HistoryStore store = NewStore();

        store.Record(Reading(left: 80), Start);

        Assert.HasCount(1, store.Query(Start).Samples);
    }

    [TestMethod]
    public void AtMostOneSamplePerPartPerMinute()
    {
        HistoryStore store = NewStore();

        store.Record(Reading(left: 80), Start);
        store.Record(Reading(left: 81), Start + TimeSpan.FromSeconds(30));
        store.Record(Reading(left: 81), Start + TimeSpan.FromSeconds(59));
        store.Record(Reading(left: 82), Start + TimeSpan.FromSeconds(60));
        store.Record(Reading(left: 70), Start - TimeSpan.FromMinutes(5)); // a clock put back

        CollectionAssert.AreEqual(KeptLevels, store.Query(Start + TimeSpan.FromMinutes(1)).Samples.Select(s => s.Percent).ToArray());
    }

    [TestMethod]
    public void SamplesOlderThanSevenDaysArePrunedOnWrite()
    {
        HistoryStore store = NewStore();
        store.Record(Reading(left: 50), Start);

        DateTimeOffset later = Start + TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        _clock.Now = later;
        store.Record(Reading(left: 60), later);

        HistoryStore reread = NewStore();
        List<HistorySample> kept = reread.Query(later).Samples.ToList();
        Assert.HasCount(1, kept, "The old sample is gone from memory and from the file.");
        Assert.AreEqual(60, kept[0].Percent);
        Assert.DoesNotContain("\"Percent\":50", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void SamplesOlderThanSevenDaysAreDroppedAtLoad()
    {
        HistoryStore store = NewStore();
        store.Record(Reading(left: 50), Start);
        store.Record(Reading(left: 55), Start + TimeSpan.FromDays(6));

        _clock.Now = Start + TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
        HistoryStore reread = NewStore();
        reread.Load();

        // Query over a window wide enough to show the old one if it were kept is not possible (24 h), so ask at its own time.
        Assert.IsEmpty(reread.Query(Start + TimeSpan.FromHours(1)).Samples, "The first sample is past 7 days at load.");
        Assert.HasCount(1, reread.Query(Start + TimeSpan.FromDays(6)).Samples);
    }

    [TestMethod]
    public void TheDayWindowHoldsTheLast24HoursAndNamesTheGaps()
    {
        HistoryStore store = NewStore();
        store.Record(Reading(left: 90), Start - TimeSpan.FromHours(30)); // outside the window
        store.Record(Reading(left: 80, box: 40), Start - TimeSpan.FromHours(3));
        store.Record(Reading(left: 78), Start - TimeSpan.FromHours(3) + TimeSpan.FromMinutes(10)); // exactly 10 minutes: not a gap
        store.Record(Reading(left: 70, box: 45), Start - TimeSpan.FromHours(2)); // left: gap of 1 h 50 min; case: gap of 1 h
        store.Record(Reading(left: 69), Start - TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        HistoryWindow window = store.Query(Start);

        Assert.AreEqual(Start - TimeSpan.FromHours(24), window.Start);
        Assert.AreEqual(Start, window.End);
        Assert.HasCount(6, window.Samples);
        Assert.IsTrue(window.Samples.SequenceEqual(window.Samples.OrderBy(s => s.At)), "Oldest first.");
        Assert.HasCount(2, window.Gaps);
        Assert.IsTrue(window.Gaps.Contains(new HistoryGap(ChargeComponent.Left, Start - TimeSpan.FromHours(3) + TimeSpan.FromMinutes(10), Start - TimeSpan.FromHours(2))));
        Assert.IsTrue(window.Gaps.Contains(new HistoryGap(ChargeComponent.Case, Start - TimeSpan.FromHours(3), Start - TimeSpan.FromHours(2))));
    }

    [TestMethod]
    public void TheFileHoldsNoAddressOrNameOnlyPartsLevelsFlagsAndTimes()
    {
        NewStore().Record(Reading(left: 80, right: 75, box: 60, charging: true), Start);

        string text = File.ReadAllText(FilePath);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var names = new HashSet<string>();
        foreach (System.Text.Json.JsonProperty p in doc.RootElement.EnumerateObject())
        {
            names.Add(p.Name);
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement item in p.Value.EnumerateArray())
                {
                    foreach (System.Text.Json.JsonProperty q in item.EnumerateObject())
                    {
                        names.Add(q.Name);
                    }
                }
            }
        }

        CollectionAssert.AreEquivalent(FileMembers, names.ToArray());
    }

    [TestMethod]
    public void ACorruptFileIsNotOverwrittenAndWhatWasHeardStaysInMemory()
    {
        WriteRaw("{ \"SchemaVersion\": 1, \"Samples\": [ { \"Part\": \"le");
        string before = File.ReadAllText(FilePath);
        HistoryStore store = NewStore();

        store.Record(Reading(left: 80), Start);
        store.Record(Reading(left: 79), Start + TimeSpan.FromMinutes(2));

        Assert.AreEqual(before, File.ReadAllText(FilePath), "The unusable file is left as it is.");
        Assert.IsFalse(File.Exists(FilePath + ".tmp"));
        Assert.HasCount(2, store.Query(Start + TimeSpan.FromMinutes(2)).Samples, "Kept for this run.");
        Assert.IsNotEmpty(_log.Entries);
    }

    [TestMethod]
    public void ACorruptFileThatIsRemovedIsAFreshStart()
    {
        WriteRaw("not json");
        HistoryStore store = NewStore();
        store.Record(Reading(left: 80), Start);
        File.Delete(FilePath);

        store.Record(Reading(left: 79), Start + TimeSpan.FromMinutes(2));

        Assert.HasCount(2, NewStore().Query(Start + TimeSpan.FromMinutes(2)).Samples);
    }

    [TestMethod]
    public void AFileOfANewerSchemaIsLeftAlone()
    {
        const string newer = "{ \"SchemaVersion\": 99, \"Samples\": [] }";
        WriteRaw(newer);
        HistoryStore store = NewStore();

        store.Record(Reading(left: 80), Start);

        Assert.AreEqual(newer, File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void AnOutOfRangeSampleIsLeftOutOnItsOwn()
    {
        WriteRaw("""
            { "SchemaVersion": 1, "Samples": [
              { "Part": "left", "Percent": 70, "Charging": false, "At": 1790931600 },
              { "Part": "left", "Percent": 900, "Charging": false, "At": 1790931660 },
              { "Part": "elsewhere", "Percent": 50, "Charging": false, "At": 1790931720 } ] }
            """);
        _clock.Now = DateTimeOffset.FromUnixTimeSeconds(1790931800);

        HistoryWindow window = NewStore().Query(_clock.Now);

        Assert.HasCount(1, window.Samples);
        Assert.AreEqual(70, window.Samples[0].Percent);
    }
}
