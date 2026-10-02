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
// estimate with what it reads, and the file holds no address and no name.
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

    // The owner's case charging with the lid open long enough to learn its rate; the lid shut, the case estimated; the lid
    // opened again, what it reads replaces the estimate.
    [TestMethod]
    public void TheCaseOpeningAgainReplacesTheEstimateWhichNeverFellBelowTheLastReading()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0xA, caseLevel: 0x4, charging: AllCharging);

        // The lid stays open: a message a second, the case stepping up every ten minutes from the first minute.
        DateTimeOffset start = Now;
        int CaseAt(double minutes) => minutes < 1 ? 4 : minutes < 11 ? 5 : minutes < 21 ? 6 : 7;
        for (int s = 0; s <= 22 * 60; s++)
        {
            bool one = s % 2 == 0;
            Raise(one ? OwnerLeftBud : OwnerRightBud,
                BroadcastFixtures.Bud(one, caseNibble: (byte)(AllCharging | CaseAt((Now - start).TotalMinutes)), pairHigh: 0xA, pairLow: 0xA), -55);
            Tick(1);
        }

        LearnedRate? rate = service.Current.LastReadings.RateFor(BroadcastFixtures.PairedModel, ChargePart.Case);
        Assert.IsNotNull(rate, "A charge of the case was seen, so its rate is learned.");
        Assert.AreEqual(60.0, rate.PercentPerHour, 1.0, "20 points from the step at 1 minute to the step at 21.");

        // The lid shuts at 70%. Over the next 21 minutes the estimate grows 21 points; it never shows less than 70.
        DateTimeOffset shut = Now;
        for (int m = 0; m <= 20; m++)
        {
            ShownPart box = BatteryFreshness.Shown(service.Current, Now).Case;
            Assert.IsGreaterThanOrEqualTo(70, box.Percent!.Value, "Never below the last reading, at minute " + m);
            Tick(60);
        }

        ShownPart grown = BatteryFreshness.Shown(service.Current, Now).Case;
        Assert.AreEqual(ReadingKind.Estimated, grown.Kind);
        Assert.AreEqual(91, grown.Percent, "70, read as the lid shut, plus 60 an hour for 21 minutes.");
        Assert.AreEqual(70, grown.ReadPercent);
        Assert.AreEqual("≈91% · 21 min", WidgetCopy.PartLine(grown, Now));
        Assert.IsGreaterThan(shut - TimeSpan.FromMinutes(2), grown.ReadAt!.Value);

        // The lid opens again: the case reads 80% and has stopped charging. That replaces the estimate at once.
        Tick(10 * 60);
        OwnerCaseOpen(3, budLevel: 0xA, caseLevel: 0x8);
        ShownPart read = BatteryFreshness.Shown(service.Current, Now).Case;
        Assert.AreEqual(ReadingKind.Live, read.Kind);
        Assert.AreEqual(80, read.Percent);

        // Shut again, not charging: it stays a last reading at 80, however long.
        Tick(5 * 60 * 60);
        ShownPart later = BatteryFreshness.Shown(service.Current, Now).Case;
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
        string[] allowed = ["SchemaVersion", "Left", "Right", "Case", "Rates", "Percent", "Charging", "ReadAt", "Model", "Part", "PercentPerHour", "MeasuredAt", "SpanMinutes"];
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
}
