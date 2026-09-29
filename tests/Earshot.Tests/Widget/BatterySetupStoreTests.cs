using System.Text.Json;
using Earshot.Composition;
using Earshot.Infra;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The set-up record: one file per set-up under the widget's data folder, never rewritten, holding only
// what the record definition names.
[TestClass]
public sealed class BatterySetupStoreTests
{
    private static BatterySetupRecord SampleRecord(int minutes = 0) =>
        SetupRecordFixtures.Record(SetupRecordFixtures.Message(high: 8, low: 4), SetupRecordFixtures.Picks(40, 80, 90, leftCharging: true), minutes);

    [TestMethod]
    public void SaveWritesOneFileUnderTheSetupFolderNamedByUtcTime()
    {
        using var temp = new TempFolder();
        var store = new BatterySetupStore(temp.File("setup"), new CapturingLog());

        string? name = store.Save(SampleRecord(minutes: 10));

        Assert.AreEqual("setup-2026.09.01T00.10.00Z.json", name);
        string[] files = Directory.GetFiles(temp.File("setup"));
        Assert.HasCount(1, files);
        Assert.AreEqual(name, Path.GetFileName(files[0]));
        Assert.IsFalse(File.Exists(files[0] + ".tmp"), "The temporary file must not be left behind.");
    }

    [TestMethod]
    public void ARecordIsNeverReplaced()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new BatterySetupStore(temp.File("setup"), log);
        Assert.IsNotNull(store.Save(SampleRecord()));
        string before = File.ReadAllText(temp.File("setup\\" + SampleRecord().FileName));

        string? again = store.Save(SampleRecord() with { AppVersion = "9.9.9" });

        Assert.IsNull(again, "A second record with the same name is refused, not written over the first.");
        Assert.AreEqual(before, File.ReadAllText(temp.File("setup\\" + SampleRecord().FileName)));
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "could not be saved"));
    }

    [TestMethod]
    public void TheFileHoldsExactlyTheDocumentedMembers()
    {
        using var temp = new TempFolder();
        var store = new BatterySetupStore(temp.File("setup"), new CapturingLog());
        BatterySetupRecord record = SampleRecord();
        BatterySetupCapture capture = new(record.EndedAtUtc, -58, 0x01, 25, Convert.ToHexString(WidgetFixtures.ProximityValue()[..9]));
        record = record with { Candidate = record.Candidate! with { Captures = [capture] } };
        string name = store.Save(record)!;

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(temp.File("setup\\" + name)));
        JsonElement root = document.RootElement;

        AssertMembers(root, "schemaVersion,startedAtUtc,endedAtUtc,listenStatus,appVersion,appleSectionsSeen,proximityItemsSeen,candidate,otherSenders,picks");
        Assert.AreEqual("Found", root.GetProperty("listenStatus").GetString(), "Enums are written as their names.");
        JsonElement candidate = root.GetProperty("candidate");
        AssertMembers(candidate, "messages,okFormMessages,otherFormMessages,rssiMin,rssiMedian,rssiMax,thresholdDbm,captures,lastOkMessage");
        AssertMembers(candidate.GetProperty("captures")[0], "atUtc,rssi,prefix,length,valueHex");
        AssertMembers(candidate.GetProperty("lastOkMessage"), "modelHigh,modelLow,status,batteryA,batteryB,lid,colour,reserved");
        AssertMembers(root.GetProperty("otherSenders")[0], "messages,okFormMessages,rssiMedian");
        AssertMembers(root.GetProperty("picks"), "left,right,case,leftCharging,rightCharging,caseCharging");
        Assert.AreEqual(90, root.GetProperty("picks").GetProperty("case").GetInt32());
    }

    // The member names, in order, exactly as the record definition lists them.
    private static void AssertMembers(JsonElement element, string expected) =>
        Assert.AreEqual(expected, string.Join(",", element.EnumerateObject().Select(p => p.Name)));

    private static readonly JsonSerializerOptions HandWritten = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [TestMethod]
    public void LoadAllReturnsRecordsInFileOrderAndSkipsAndLogsABadOne()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new BatterySetupStore(temp.File("setup"), log);
        Assert.IsNotNull(store.Save(SampleRecord(minutes: 2)));
        Assert.IsNotNull(store.Save(SampleRecord(minutes: 0)));
        Assert.IsNotNull(store.Save(SampleRecord(minutes: 1)));
        File.WriteAllText(temp.File("setup\\setup-2026.09.01T00.30.00Z.json"), "not json");
        File.WriteAllText(temp.File("setup\\notes.txt"), "not a record at all");

        IReadOnlyList<BatterySetupRecord> records = store.LoadAll();

        Assert.AreEqual(
            "setup-2026.09.01T00.00.00Z.json,setup-2026.09.01T00.01.00Z.json,setup-2026.09.01T00.02.00Z.json",
            string.Join(",", records.Select(r => r.FileName)), "In file-name order, whatever order they were written in.");
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "setup-2026.09.01T00.30.00Z.json"), "The bad file is named in the log.");
        Assert.IsTrue(File.Exists(temp.File("setup\\setup-2026.09.01T00.30.00Z.json")), "A bad record is left where it is.");
    }

    // Load ties a claim to its record, so it reads the same names LoadAll does and no other json file in the folder,
    // even one that holds a valid record.
    [TestMethod]
    public void LoadReadsOnlyARecordsOwnKindOfName()
    {
        using var temp = new TempFolder();
        var store = new BatterySetupStore(temp.File("setup"), new CapturingLog());
        string name = store.Save(SampleRecord(minutes: 3))!;
        string valid = File.ReadAllText(temp.File("setup\\" + name));
        File.WriteAllText(temp.File("setup\\other.json"), valid);

        Assert.IsNotNull(store.Load(name), "A record's own name still loads.");
        Assert.IsNull(store.Load("other.json"), "A valid record under another name is not one of the set-up records.");
    }

    [TestMethod]
    public void ARecordFolderThatDoesNotExistIsNoRecords()
    {
        using var temp = new TempFolder();
        var store = new BatterySetupStore(temp.File("never-made"), new CapturingLog());

        Assert.IsEmpty(store.LoadAll());
    }

    // A hand-written record is trusted only after every value is one a set-up could have written.
    [TestMethod]
    public void HandWrittenRecordsWithBadValuesAreRejected()
    {
        (string Why, Func<BatterySetupRecord, BatterySetupRecord> Damage)[] cases =
        [
            ("a pick that is not a multiple of ten", r => r with { Picks = r.Picks with { Left = 55 } }),
            ("a pick above 100", r => r with { Picks = r.Picks with { Case = 110 } }),
            ("a negative pick", r => r with { Picks = r.Picks with { Right = -10 } }),
            ("a schema version 2", r => r with { SchemaVersion = 2 }),
            ("a listen status that is never saved", r => r with { ListenStatus = BatterySetupListenStatus.NotFound }),
            ("an end before its start", r => r with { EndedAtUtc = r.StartedAtUtc.AddSeconds(-1) }),
            ("no candidate", r => r with { Candidate = null }),
            ("a signal minimum above the median", r => r with { Candidate = r.Candidate! with { RssiMin = -50, RssiMedian = -58 } }),
            ("a positive signal", r => r with { Candidate = r.Candidate! with { RssiMax = 5 } }),
            ("a threshold above the weakest message", r => r with { Candidate = r.Candidate! with { ThresholdDbm = -40 } }),
            ("counts that do not add up", r => r with { Candidate = r.Candidate! with { OkFormMessages = 1 } }),
            ("fewer than three messages", r => r with { Candidate = r.Candidate! with { Messages = 2, OkFormMessages = 2 } }),
            ("a Found record with no message", r => r with { Candidate = r.Candidate! with { LastOkMessage = null } }),
            ("a capture that is not hex", r => r with { Candidate = r.Candidate! with { Captures = [new BatterySetupCapture(r.EndedAtUtc, -58, 1, 25, "ZZ")] } }),
            ("a documented-form capture longer than nine bytes", r => r with { Candidate = r.Candidate! with { Captures = [new BatterySetupCapture(r.EndedAtUtc, -58, 1, 25, Convert.ToHexString(WidgetFixtures.ProximityValue()[..12]))] } }),
            ("more captures than messages", r => r with { Candidate = r.Candidate! with { Messages = 3, OkFormMessages = 3, Captures = Enumerable.Repeat(new BatterySetupCapture(r.EndedAtUtc, -58, 6, 1, "06"), 4).ToList() } }),
        ];
        int n = 0;
        foreach ((string why, Func<BatterySetupRecord, BatterySetupRecord> damage) in cases)
        {
            using var temp = new TempFolder();
            var log = new CapturingLog();
            var store = new BatterySetupStore(temp.File("setup"), log);
            BatterySetupRecord bad = damage(SampleRecord(minutes: n++));
            Directory.CreateDirectory(temp.File("setup"));
            File.WriteAllText(
                temp.File("setup\\" + bad.FileName),
                JsonSerializer.Serialize(bad, HandWritten));

            Assert.IsEmpty(store.LoadAll(), "Must be rejected: " + why);
            Assert.IsTrue(log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Warn), "Must be logged: " + why);
        }
    }

    [TestMethod]
    public void AMemberMissingFromAHandWrittenRecordIsRejected()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new BatterySetupStore(temp.File("setup"), log);
        BatterySetupRecord record = SampleRecord();
        Assert.IsNotNull(store.Save(record));
        string path = temp.File("setup\\" + record.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"picks\"", "\"picksX\"", StringComparison.Ordinal));

        Assert.IsEmpty(store.LoadAll(), "A record with no picks is not a record: the picks are half its evidence.");
        Assert.IsTrue(log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Warn));
    }

    [TestMethod]
    public void ThePathFollowsTheDataRoot()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null);

        string folder = CompositionRoot.WidgetSetupFolder(paths);
        string proof = CompositionRoot.WidgetProofFile(paths);

        Assert.AreEqual(Path.Combine(paths.WidgetFolder, "setup"), folder);
        Assert.AreEqual(Path.Combine(paths.WidgetFolder, "proof.json"), proof);
        Assert.IsTrue(folder.StartsWith(Path.Combine(temp.Path, "Local"), StringComparison.OrdinalIgnoreCase), folder);
        Assert.IsTrue(proof.StartsWith(Path.Combine(temp.Path, "Local"), StringComparison.OrdinalIgnoreCase), proof);
    }

    // The whole path from a listening window to the file: no sender tag, no address, no encrypted byte, no
    // name reaches what is written.
    [TestMethod]
    public async Task NoTagAddressOrEncryptedByteReachesTheFile()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();
        const uint Tag = 3141592653;
        Task<BatterySetupListen> task = BatterySetupFlow.ListenAsync(source, clock, WidgetTiming.SetupListenWindow, log, CancellationToken.None);
        for (int i = 0; i < 4; i++)
        {
            source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryA: 0x86, batteryB: 0x05), -55, clock.GetUtcNow(), Tag));
        }

        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.UnknownSeventeenByteForm(), -55, clock.GetUtcNow(), Tag));
        clock.Advance(WidgetTiming.SetupListenWindow);
        BatterySetupListen listen = await task;
        var record = new BatterySetupRecord(
            1, listen.StartedAtUtc, listen.EndedAtUtc, listen.Status, "1.1.0", listen.AppleSectionsSeen, listen.ProximityItemsSeen,
            listen.Candidate, listen.OtherSenders, SetupRecordFixtures.Picks(40, 80));
        var store = new BatterySetupStore(temp.File("setup"), log);

        string name = store.Save(record)!;
        string text = File.ReadAllText(temp.File("setup\\" + name));

        byte[] encrypted = WidgetFixtures.ProximityValue()[9..];
        Assert.DoesNotContain(Convert.ToHexString(encrypted[..4]), text, "The encrypted bytes of the documented form are never copied.");
        Assert.DoesNotContain(Tag.ToString(System.Globalization.CultureInfo.InvariantCulture), text, "The per-run sender tag is never written.");
        Assert.DoesNotContain("senderTag", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(text.Contains("\"valueHex\"", StringComparison.Ordinal), "Sanity: the captures are there.");
    }
}
