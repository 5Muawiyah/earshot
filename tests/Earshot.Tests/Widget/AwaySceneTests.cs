using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Away from this PC, whole scenes through the real status service, the real selection and link code and the real last
// reading store under EARSHOT_DATA_ROOT, with a fake watcher and a fake clock: the owner's pair's last readings are saved
// and shown after a restart with their age, a stranger's pair is never saved or shown, a case opened again replaces the
// last reading with what it reads, and the file holds no address and no name.
[TestClass]
public sealed class AwaySceneTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();

    // Sender tags, distinctive so the file can be searched for them in hex and in decimal.
    private const uint OwnerLeftBud = 0xA1B2C3D4;
    private const uint OwnerRightBud = 0xB2C3D4E5;
    private const uint StrangerBud = 0xC3D4E5F6;
    private const uint StrangerOtherBud = 0xD4E5F607;

    // The charging bits of the documented table's BatteryB: the case (6), one bud (5) and the other (4).
    private const byte AllCharging = 0x70;

    private TempFolder _temp = null!;
    private EnvironmentVariableScope _dataRoot = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeDeviceMonitor _monitor = null!;
    private TestTimeProvider _clock = null!;
    private FakeAdvertisementSource _source = null!;
    private FakePairedModelSource _paired = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _dataRoot = new EnvironmentVariableScope(Paths.DataRootVariable, _temp.Path);
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _clock = new TestTimeProvider();
        _monitor = new FakeDeviceMonitor(_clock);
        _source = new FakeAdvertisementSource();
        _paired = new FakePairedModelSource();
    }

    [TestCleanup]
    public void Cleanup() => _dataRoot.Dispose();

    public void Dispose() => _temp.Dispose();

    // The file as the composition finds it: Paths read from this process's environment, EARSHOT_DATA_ROOT included. The
    // special folders come from the temp folder, so the same paths come out on a machine with no Windows profile.
    private string StoreFile => Paths.FromEnvironment(Environment.GetEnvironmentVariable, _ => Path.Combine(_temp.Path, "profile")).LastReadingFile;

    // A new process's service: a new store over the same file, read at start.
    private WidgetStatusService NewService()
    {
        _source = new FakeAdvertisementSource();
        var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null,
            ProximityDecodeTable.Documented, runInBackground: work => work(), lastReadings: new LastReadingStore(StoreFile, _log));
        service.Start();
        return service;
    }

    private void Connect(bool active = true)
    {
        _settings.Update(s =>
        {
            s.PinnedContainerId = Container;
            s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
        });
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, active ? EndpointState.Active : EndpointState.Unplugged, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", active ? ConnectionState.Connected : ConnectionState.Disconnected, new[] { endpoint });
        _monitor.Raise(new DeviceSnapshot(model, new[] { model }, _clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        });
    }

    private void Tick(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    private DateTimeOffset Now => _clock.GetUtcNow();

    private void Raise(uint tag, ProximityMessage m, sbyte rssi) =>
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(m.ModelHigh, m.ModelLow, m.Status, m.BatteryA, m.BatteryB, m.Lid, m.Colour, m.Reserved),
            rssi, Now, tag));

    // The owner's case open next to the PC for the given time: both buds at budLevel, the case at caseLevel (tenths), the
    // charging bits as given, about four messages a second between the two buds at -55 dBm.
    private void OwnerCaseOpen(double seconds, int budLevel, int caseLevel, byte charging = 0, double every = 0.25)
    {
        int steps = (int)Math.Round(seconds / every);
        for (int i = 0; i <= steps; i++)
        {
            bool one = i % 2 == 0;
            Raise(one ? OwnerLeftBud : OwnerRightBud,
                BroadcastFixtures.Bud(one, caseNibble: (byte)(charging | caseLevel), pairHigh: budLevel, pairLow: budLevel), -55);
            Tick(every);
        }
    }

    [TestMethod]
    public void ARestartShowsTheLastReadingsWithTheirAge()
    {
        using (WidgetStatusService first = NewService())
        {
            OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);
            Assert.AreEqual(BroadcastSelectionState.Linked, first.Current.Selection);
            Assert.IsTrue(File.Exists(StoreFile), "Written on change.");
            first.Close();
        }

        Tick(2 * 60 * 60);
        using WidgetStatusService second = NewService();

        WidgetSnapshot snapshot = second.Current;
        Assert.AreNotEqual(BroadcastSelectionState.Linked, snapshot.Selection, "The link itself is never kept.");
        ShownBattery shown = BatteryFreshness.Shown(snapshot, Now);
        foreach ((string name, ShownPart part, int value) in new[] { ("left", shown.Left, 60), ("right", shown.Right, 60), ("case", shown.Case, 80) })
        {
            Assert.AreEqual(value, part.Percent, name);
            Assert.AreEqual(ReadingKind.Last, part.Kind, name + " is a last reading, never live.");
            Assert.AreEqual("Last read 2 h ago", WidgetCopy.PartTip(part, Now), name);
        }

        GaugeContent away = GaugeContent.From(snapshot, Now, GaugeDisplaySettings.Default);
        Assert.AreEqual(GaugeMode.CaseAway, away.Mode);
        Assert.AreEqual("Not on this PC\r\nCase 80%\r\nLast read 2 h ago", away.Tooltip);

        Connect();
        GaugeContent here = GaugeContent.From(second.Current, Now, GaugeDisplaySettings.Default);
        Assert.AreEqual(GaugeMode.Reading, here.Mode, "Connected, the buds' last reading is on the gauge, not the mark alone.");
        Assert.AreEqual(60, here.Percent);
        Assert.IsTrue(here.Tertiary);
        StringAssert.EndsWith(here.Tooltip, "Last read 2 h ago");
    }

    [TestMethod]
    public void AStrangersPairIsNeverSavedAndNeverShown()
    {
        using (WidgetStatusService service = NewService())
        {
            Connect();

            // Worn nearby: the case level unknown, however strong.
            for (int i = 0; i < 600; i++)
            {
                bool one = i % 2 == 0;
                Raise(one ? StrangerBud : StrangerOtherBud, BroadcastFixtures.Bud(one, caseNibble: 0xF, pairHigh: 0x3, pairLow: 0x3), -50);
                Tick(0.5);
            }

            // Its case opened, but not next to the PC.
            for (int i = 0; i < 40; i++)
            {
                bool one = i % 2 == 0;
                Raise(one ? StrangerBud : StrangerOtherBud, BroadcastFixtures.Bud(one, caseNibble: AllCharging | 0x2, pairHigh: 0x3, pairLow: 0x3), -80);
                Tick(0.25);
            }

            Assert.AreNotEqual(BroadcastSelectionState.Linked, service.Current.Selection);
            ShownBattery shown = BatteryFreshness.Shown(service.Current, Now);
            Assert.IsFalse(shown.Left.HasValue || shown.Right.HasValue || shown.Case.HasValue, "Nothing of the stranger is shown.");
            Assert.AreEqual(GaugeMode.MarkOnly, GaugeContent.From(service.Current, Now, GaugeDisplaySettings.Default).Mode);
            service.Close();
        }

        Assert.IsFalse(File.Exists(StoreFile), "Nothing was saved.");
        Tick(60);
        using WidgetStatusService restarted = NewService();
        ShownBattery after = BatteryFreshness.Shown(restarted.Current, Now);
        Assert.IsFalse(after.Left.HasValue || after.Right.HasValue || after.Case.HasValue, "Nor shown after a restart.");
    }

    // The owner's case charging with the lid open; the lid shuts at 70%. Whatever the time since, and across a restart with the
    // clock anywhere, the case reads 70 as a last reading: nothing grows it. The lid opening again reads 80, which replaces it.
    [TestMethod]
    public void AChargingCaseThatIsShutKeepsItsLastReadingAndTheLidOpeningAgainReplacesIt()
    {
        using (WidgetStatusService service = NewService())
        {
            OwnerCaseOpen(3, budLevel: 0xA, caseLevel: 0x7, charging: AllCharging);

            foreach (double minutes in new[] { 1.0, 21, 90, 600 })
            {
                Tick(minutes * 60);
                ShownPart box = BatteryFreshness.Shown(service.Current, Now).Case;
                Assert.AreEqual(70, box.Percent, "At " + minutes + " minutes.");
                Assert.AreEqual(ReadingKind.Last, box.Kind, "At " + minutes + " minutes.");
                Tick(-minutes * 60);
            }

            service.Close();
        }

        // Restarted, with the clock put forward and back: the same reading.
        foreach (double hours in new[] { 3.0, -2 })
        {
            Tick(hours * 60 * 60);
            using WidgetStatusService restarted = NewService();
            ShownPart box = BatteryFreshness.Shown(restarted.Current, Now).Case;
            Assert.AreEqual(70, box.Percent);
            Assert.AreEqual(ReadingKind.Last, box.Kind);
            Tick(-hours * 60 * 60);
        }

        using WidgetStatusService again = NewService();
        Tick(10 * 60);
        OwnerCaseOpen(3, budLevel: 0xA, caseLevel: 0x8);
        ShownPart read = BatteryFreshness.Shown(again.Current, Now).Case;
        Assert.AreEqual(ReadingKind.Live, read.Kind);
        Assert.AreEqual(80, read.Percent);

        Tick(5 * 60 * 60);
        ShownPart later = BatteryFreshness.Shown(again.Current, Now).Case;
        Assert.AreEqual(ReadingKind.Last, later.Kind);
        Assert.AreEqual(80, later.Percent);
    }

    [TestMethod]
    public void TheFileUnderTheDataRootHoldsNoAddressAndNoName()
    {
        using (WidgetStatusService service = NewService())
        {
            OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8, charging: AllCharging);
            service.Close();
        }

        Assert.IsTrue(StoreFile.StartsWith(_temp.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Under EARSHOT_DATA_ROOT: " + StoreFile);
        string text = File.ReadAllText(StoreFile);
        foreach (uint tag in new[] { OwnerLeftBud, OwnerRightBud })
        {
            StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex(tag.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            Assert.IsFalse(text.Contains(tag.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), "No sender tag in decimal.");
        }

        Assert.IsFalse(text.Contains(Phase4.RecordedNodes.AirPodsAddress, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("AirPods", StringComparison.OrdinalIgnoreCase), "No name.");

        // Every member is one the file's shape allows, and none of them can hold an address or a name.
        string[] allowed = ["SchemaVersion", "Left", "Right", "Case", "Percent", "Charging", "ReadAt", "Model", "Part", "ReadPercent", "Spent"];
        using var json = System.Text.Json.JsonDocument.Parse(text);
        var names = new List<string>();
        void Walk(System.Text.Json.JsonElement e)
        {
            if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (System.Text.Json.JsonProperty p in e.EnumerateObject())
                {
                    names.Add(p.Name);
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement item in e.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }

        Walk(json.RootElement);
        CollectionAssert.IsSubsetOf(names.Distinct().ToList(), allowed, "Members: " + string.Join(", ", names.Distinct()));
        Assert.AreEqual(60, json.RootElement.GetProperty("Left").GetProperty("Percent").GetInt32());
        Assert.AreEqual(BroadcastFixtures.PairedModel, json.RootElement.GetProperty("Case").GetProperty("Model").GetInt32());
    }

    // A value change is written at once; a read time alone at most once a minute, and the last one at Close.
    [TestMethod]
    public void AReadTimeAloneIsWrittenAtMostOnceAMinuteAndAtClose()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);
        DateTimeOffset firstWrite = new LastReadingStore(StoreFile, _log).Load().Left!.ReadAt;

        OwnerCaseOpen(20, budLevel: 0x6, caseLevel: 0x8);
        Assert.AreEqual(firstWrite, new LastReadingStore(StoreFile, _log).Load().Left!.ReadAt, "Twenty seconds of the same values: not written again.");

        OwnerCaseOpen(3, budLevel: 0x7, caseLevel: 0x8);
        Assert.AreEqual(70, new LastReadingStore(StoreFile, _log).Load().Left!.Percent, "A new value is written at once.");

        OwnerCaseOpen(5, budLevel: 0x7, caseLevel: 0x8);
        DateTimeOffset lastHeard = service.Current.LastReadings.Left!.ReadAt;
        service.Close();
        Assert.AreEqual(lastHeard, new LastReadingStore(StoreFile, _log).Load().Left!.ReadAt, "Close writes the last read time.");
    }


    // One bud's message of a same-model stranger's pair: 70% in each bud, 60% in the case, the lid open.
    private void StrangerCaseOpen(double seconds, sbyte rssi, double every = 0.25)
    {
        int steps = (int)Math.Round(seconds / every);
        for (int i = 0; i <= steps; i++)
        {
            bool one = i % 2 == 0;
            Raise(one ? StrangerBud : StrangerOtherBud, BroadcastFixtures.Bud(one, caseNibble: 0x6, pairHigh: 0x7, pairLow: 0x7), rssi);
            Tick(every);
        }
    }

    // A stranger's pair of the same model that does not take the link (it is not 8 dB nearer than the owner's, heard a
    // moment ago) is never saved, however long it keeps its case open.
    [TestMethod]
    public void AStrangerThatDoesNotTakeTheLinkNeverReplacesTheSavedReadings()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);
        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);

        StrangerCaseOpen(8, rssi: -60);

        LastReadingBook saved = new LastReadingStore(StoreFile, _log).Load();
        Assert.AreEqual(60, saved.Left?.Percent, "The owner's buds.");
        Assert.AreEqual(80, saved.Case?.Percent, "The owner's case.");
        Assert.AreEqual(60, service.Current.LastReadings.Left?.Percent);
    }

    // The owner's accepted same-model risk (docs/architecture.md, "The accepted risk"): a stranger's pair of the same model
    // that opens its case 8 dB nearer than the linked pair's takes the link by the rule, and what it reads replaces the
    // saved readings. This documents the behaviour; the link rule is unchanged.
    [TestMethod]
    public void AStrangerThatTakesTheLinkByTheRuleReplacesTheSavedReadings()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);

        StrangerCaseOpen(6, rssi: -45);

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);
        LastReadingBook saved = new LastReadingStore(StoreFile, _log).Load();
        Assert.AreEqual(70, saved.Left?.Percent, "The stranger's buds now stand where the owner's were.");
        Assert.AreEqual(60, saved.Case?.Percent, "...and its case.");
    }

    // The other way in, also accepted: once the linked pair has been silent for more than ten seconds a same-model pair that
    // opens its case near is linked with no margin to clear.
    [TestMethod]
    public void AStrangerThatOpensItsCaseAfterTheOwnersPairWentSilentReplacesTheSavedReadings()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);
        Tick(12);

        StrangerCaseOpen(6, rssi: -65);

        LastReadingBook saved = new LastReadingStore(StoreFile, _log).Load();
        Assert.AreEqual(70, saved.Left?.Percent);
        Assert.AreEqual(60, saved.Case?.Percent);
    }

    private sealed class RecordingStore : ILastReadingStore
    {
        public List<LastReadingBook> Saved { get; } = [];

        public LastReadingBook Load() => LastReadingBook.Empty;

        public void Save(LastReadingBook book)
        {
            lock (Saved)
            {
                Saved.Add(book);
            }
        }
    }

    // Each book the service takes to be written carries the next number in order, taken under its lock, so the store can
    // drop one that arrives after a later one (LastReadingStoreTests has the store's half).
    [TestMethod]
    public void EveryBookTakenToBeWrittenCarriesTheNextSequenceNumber()
    {
        var recording = new RecordingStore();
        _source = new FakeAdvertisementSource();
        using var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null,
            ProximityDecodeTable.Documented, runInBackground: work => work(), lastReadings: recording);
        service.Start();

        OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);
        OwnerCaseOpen(3, budLevel: 0x7, caseLevel: 0x8);
        OwnerCaseOpen(3, budLevel: 0x8, caseLevel: 0x9);
        service.Close();

        Assert.IsGreaterThanOrEqualTo(4, recording.Saved.Count, "Three changes of value and the close.");
        long previous = 0;
        foreach (LastReadingBook book in recording.Saved)
        {
            Assert.IsGreaterThan(previous, book.Sequence, "Strictly increasing, from 1.");
            previous = book.Sequence;
        }
    }

    private sealed class RecordingHistory : IHistoryStore
    {
        public List<(DecodedReading Reading, DateTimeOffset At)> Recorded { get; } = [];

        public void Record(DecodedReading reading, DateTimeOffset at) => Recorded.Add((reading, at));

        public HistoryWindow Query(DateTimeOffset end) => HistoryStore.Window([], end);
    }

    // Only a live message of the linked pair reaches the history: a stranger's, however strong, and the owner's pair before
    // it is linked, never do; nothing is recorded for a reading the service only shows from the saved book.
    [TestMethod]
    public void OnlyLiveMessagesOfTheLinkedPairReachTheHistory()
    {
        var history = new RecordingHistory();
        _source = new FakeAdvertisementSource();
        using var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null,
            ProximityDecodeTable.Documented, runInBackground: work => work(), lastReadings: new LastReadingStore(StoreFile, _log), history: history);
        service.Start();
        Connect();

        for (int i = 0; i < 40; i++)
        {
            bool one = i % 2 == 0;
            Raise(one ? StrangerBud : StrangerOtherBud, BroadcastFixtures.Bud(one, caseNibble: AllCharging | 0x2, pairHigh: 0x3, pairLow: 0x3), -80);
            Tick(0.25);
        }

        Assert.IsEmpty(history.Recorded, "A stranger's pair is not recorded.");

        OwnerCaseOpen(3, budLevel: 0x6, caseLevel: 0x8);

        Assert.IsNotEmpty(history.Recorded, "The owner's pair, once linked, is.");
        Assert.IsTrue(history.Recorded.All(r => r.Reading.Left.Percent is null or 60 && r.Reading.Case.Percent is null or 80));
        service.Close();
    }
}
