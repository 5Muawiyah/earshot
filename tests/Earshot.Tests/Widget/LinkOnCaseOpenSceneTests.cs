using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Whole scenes through the real status service with a fake watcher: the owner's AirPods are linked when the owner opens
// the case next to the PC, are shown only while connected to this PC, and are never mistaken for a pair of the same
// model worn nearby. The card, the gauge, the tooltip and the low battery alert all read BatteryFreshness.Shown, which
// is what these scenes read.
[TestClass]
public sealed class LinkOnCaseOpenSceneTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();

    // The owner's pair: both buds full, the case at 90%, in a closed case until the lid opens.
    private const int OwnerBuds = 0xA;
    private const byte OwnerCase = 0x9;

    private const uint OwnerLeftBud = 1;
    private const uint OwnerRightBud = 2;
    private const uint OwnerLeftBudNewAddress = 11;
    private const uint OwnerRightBudNewAddress = 12;
    private const uint StrangerBud = 21;
    private const uint StrangerOtherBud = 22;

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeDeviceMonitor _monitor = null!;
    private TestTimeProvider _clock = null!;
    private FakeAdvertisementSource _source = null!;
    private FakePairedModelSource _paired = null!;
    private List<ReadingAppliedEventArgs> _readings = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _clock = new TestTimeProvider();
        _monitor = new FakeDeviceMonitor(_clock);
        _source = new FakeAdvertisementSource();
        _paired = new FakePairedModelSource();
        _readings = new List<ReadingAppliedEventArgs>();
    }

    public void Dispose() => _temp.Dispose();

    private WidgetStatusService NewService()
    {
        var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null,
            ProximityDecodeTable.Documented, runInBackground: work => work());
        service.ReadingApplied += (_, e) => _readings.Add(e);
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

    private void Raise(uint tag, ProximityMessage m, sbyte rssi) =>
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(m.ModelHigh, m.ModelLow, m.Status, m.BatteryA, m.BatteryB, m.Lid, m.Colour, m.Reserved),
            rssi, _clock.GetUtcNow(), tag));

    // One bud's message of the owner's pair. Each bud reports itself first, so the two arrive with the nibbles swapped.
    private static ProximityMessage Owner(bool first, byte caseNibble) =>
        BroadcastFixtures.Bud(first, caseNibble: caseNibble, pairHigh: OwnerBuds, pairLow: OwnerBuds);

    // The owner opens the case: both buds send about four times a second between them, with the case level known.
    private void OpenTheCase(double seconds, sbyte rssi = -55, uint left = OwnerLeftBud, uint right = OwnerRightBud)
    {
        int steps = (int)Math.Round(seconds / 0.25);
        for (int i = 0; i <= steps; i++)
        {
            bool one = i % 2 == 0;
            Raise(one ? left : right, Owner(one, OwnerCase), rssi);
            Tick(0.25);
        }
    }

    // The owner's pair in use: out of the case, so the case level is unknown, a message from each bud every second.
    private void WearThem(double seconds, sbyte rssi = -55, uint left = OwnerLeftBud, uint right = OwnerRightBud)
    {
        for (int i = 0; i < (int)seconds; i++)
        {
            Raise(left, Owner(true, 0xF), rssi);
            Tick(0.5);
            Raise(right, Owner(false, 0xF), rssi);
            Tick(0.5);
        }
    }

    // A same-model pair worn nearby: 70% in each bud, the case level unknown, at -64 to -76 dBm.
    private void StrangerWornNearby(double seconds, sbyte? fixedRssi = null)
    {
        for (int i = 0; i < (int)(seconds * 2); i++)
        {
            sbyte rssi = fixedRssi ?? (sbyte)(-64 - ((i * 7) % 13));
            bool one = i % 2 == 0;
            Raise(one ? StrangerBud : StrangerOtherBud, BroadcastFixtures.Bud(one, caseNibble: 0xF, pairHigh: 0x7, pairLow: 0x7), rssi);
            Tick(0.5);
        }
    }

    // A same-model pair opens its own case: 70% in each bud, 60% in the case, both buds sending about four times a second.
    private void StrangerOpensTheCase(double seconds, sbyte rssi)
    {
        int steps = (int)Math.Round(seconds / 0.25);
        for (int i = 0; i <= steps; i++)
        {
            bool one = i % 2 == 0;
            Raise(one ? StrangerBud : StrangerOtherBud, BroadcastFixtures.Bud(one, caseNibble: 0x6, pairHigh: 0x7, pairLow: 0x7), rssi);
            Tick(0.25);
        }
    }

    private ShownBattery Shown(WidgetStatusService service) => BatteryFreshness.Shown(service.Current, _clock.GetUtcNow());

    private GaugeContent Gauge(WidgetStatusService service) =>
        GaugeContent.From(service.Current, _clock.GetUtcNow(), GaugeDisplaySettings.Default);

    private static void AssertNothingShown(ShownBattery shown, string why)
    {
        Assert.IsFalse(shown.Left.HasValue, why + ": no left figure.");
        Assert.IsFalse(shown.Right.HasValue, why + ": no right figure.");
        Assert.IsFalse(shown.Case.HasValue, why + ": no case figure.");
        Assert.IsNull(shown.WindowsPercent, why);
        Assert.IsNull(shown.Gauge, why + ": nothing on the gauge.");
    }

    // ---- The scene that was shown as the owner's

    // The owner's pair in its closed case sends nothing. A pair of the same model is worn nearby, 70% in each bud, its
    // case level unknown, at -64 to -76 dBm. It was shown as the owner's; it must show nothing.
    [TestMethod]
    public void TodaysSceneAStrangersWornPairNearbyWhileTheOwnersIsShutInItsCaseShowsNothing()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();

        StrangerWornNearby(seconds: 300);

        WidgetSnapshot snapshot = service.Current;
        Assert.AreNotEqual(BroadcastSelectionState.Linked, snapshot.Selection, "Nothing is linked.");
        AssertNothingShown(Shown(service), "A worn pair nearby is not the owner's");
        Assert.AreEqual(GaugeMode.MarkOnly, Gauge(service).Mode, "Connected with no figure: the mark alone.");
        Assert.IsNull(Gauge(service).Percent);
        Assert.IsEmpty(_readings, "Nothing of it reached the ear logic either.");
    }

    // The same scene, but the owner's pair had been linked an hour ago and has been silent since: the stranger is still
    // not the owner's however long it is there.
    [TestMethod]
    public void AStrangersWornPairNeverReplacesTheOwnersWhateverIsLeftOfTheLinkAfterTheOwnerWentQuiet()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        OpenTheCase(3);
        WearThem(5);

        // The owner's pair goes quiet (the case is shut) and the stranger's, much nearer, is heard for ten minutes.
        StrangerWornNearby(seconds: 600, fixedRssi: -40);

        ShownBattery shown = Shown(service);
        Assert.IsFalse(shown.Left.Percent == 70 || shown.Right.Percent == 70, "Not once is the stranger's 70% shown.");
        Assert.IsTrue(_readings.All(r => r.Reading.Left.Percent != 70 && r.Reading.Right.Percent != 70), "Nor reached the ear logic.");
        AssertNothingShown(shown, "The link was dropped when the owner's pair was lost for too long");
    }

    // ---- Linking on a case open

    // The risk the documents state: while the linked pair has been heard inside the ten second window another pair has to open
    // its case 8 dB nearer to take the link, and once the linked pair has been unheard for more than ten seconds any pair of the
    // same model that opens its case at -70 dBm or stronger is linked, with no margin to clear.
    [TestMethod]
    public void AStrangersCaseOpenedWhileTheOwnersPairWasHeardInTheLastTenSecondsDoesNotTakeTheLinkWithoutTheMargin()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        OpenTheCase(seconds: 3);
        Tick(5);

        StrangerOpensTheCase(seconds: 3, rssi: -70);

        Assert.AreEqual(100, Shown(service).Left.Percent, "Still the owner's: its last message was only a few seconds ago and the stranger is 15 dB weaker.");
    }

    [TestMethod]
    public void AStrangersCaseOpenedAfterTheOwnersPairWentQuietForMoreThanTenSecondsIsLinkedWithNoMargin()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        OpenTheCase(seconds: 3);
        Assert.AreEqual(100, Shown(service).Left.Percent, "Sanity: the owner's pair is linked.");
        Tick(11);

        StrangerOpensTheCase(seconds: 3, rssi: -70);

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);
        Assert.AreEqual(70, Shown(service).Left.Percent, "The stranger's pair is the linked one: 15 dB weaker than the owner's was, and no margin was asked.");
    }

    [TestMethod]
    public void ACaseOpenBurstFromTheOwnersPairLinksItAndShowsLeftRightAndCase()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();

        OpenTheCase(seconds: 3);

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);
        ShownBattery shown = Shown(service);
        Assert.AreEqual(100, shown.Left.Percent);
        Assert.AreEqual(100, shown.Right.Percent);
        Assert.AreEqual(90, shown.Case.Percent);
        Assert.IsTrue(shown.Left.Fresh);
        Assert.AreEqual(100, shown.Gauge?.Percent);
        Assert.AreEqual(GaugeMode.Reading, Gauge(service).Mode);
    }

    [TestMethod]
    public void ARefreshWhileConnectedAndNotLinkedListensForTheCaseOpenAndEndsHeardWhenItLinks()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        StrangerWornNearby(seconds: 10);

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        StrangerWornNearby(seconds: 5);
        Assert.IsFalse(refresh.IsCompleted, "A worn pair is heard, but it is not a case open.");

        OpenTheCase(seconds: 3);

        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.Heard, refresh.Result);
        Assert.AreEqual(100, Shown(service).Left.Percent);
    }

    [TestMethod]
    public void ARefreshWithNoCaseOpenEndsNothingHeardAndShowsNothing()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        StrangerWornNearby(seconds: 13);

        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, refresh.Result);
        AssertNothingShown(Shown(service), "No case was opened");
    }

    // ---- Following the linked set

    [TestMethod]
    public void TheLinkedSetRotatingItsAddressesContinuesAndKeepsShowingItsValues()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        OpenTheCase(3);
        WearThem(10);

        Tick(12); // the old addresses go quiet
        WearThem(8, left: OwnerLeftBudNewAddress, right: OwnerRightBudNewAddress);

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);
        ShownBattery shown = Shown(service);
        Assert.AreEqual(100, shown.Left.Percent);
        Assert.AreEqual(100, shown.Right.Percent);
        Assert.IsTrue(shown.Left.Fresh, "The new addresses' own messages are what is shown now.");
        Assert.AreEqual(90, shown.Case.Percent, "The case level was not said by the pair in use: the old one stays, greyed.");
        Assert.IsFalse(shown.Case.Fresh);
    }

    // After the rotation, and during it, a pair of the same model with other levels, however near and for however long,
    // is never taken for the owner's.
    [TestMethod]
    public void AStrangerWithOtherLevelsDuringOrAfterARotationIsNeverAdoptedAndTheOwnerIsFollowedWhenItReturns()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        OpenTheCase(3);
        WearThem(5);

        // The owner's old addresses are gone. A stranger, nearer, is heard for twenty seconds with its own levels.
        StrangerWornNearby(seconds: 20, fixedRssi: -35);

        ShownBattery during = Shown(service);
        Assert.IsFalse(during.Left.Percent == 70 || during.Right.Percent == 70, "The stranger's levels never show.");
        Assert.AreEqual(100, during.Left.Percent, "Still the owner's last values, within the two minutes.");

        // The owner's pair comes back under new addresses, with the levels it had.
        WearThem(10, left: OwnerLeftBudNewAddress, right: OwnerRightBudNewAddress);
        StrangerWornNearby(seconds: 5, fixedRssi: -35);

        ShownBattery after = Shown(service);
        Assert.AreEqual(100, after.Left.Percent);
        Assert.IsTrue(after.Left.Fresh, "...and it is followed.");
        Assert.IsFalse(_readings.Any(r => r.Reading.Left.Percent == 70), "None of the stranger's readings was applied.");
    }

    // ---- Dropping the link

    [TestMethod]
    public void ALinkLostForMoreThanTwoMinutesShowsNothingAndWaitsForTheNextCaseOpen()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Connect();
        OpenTheCase(3);
        WearThem(5);
        Assert.AreEqual(100, Shown(service).Left.Percent);

        Tick(110);
        Assert.AreEqual(100, Shown(service).Left.Percent, "Under two minutes: the link is kept, the value greyed.");
        Assert.IsFalse(Shown(service).Left.Fresh);

        Tick(15);
        Assert.AreNotEqual(BroadcastSelectionState.Linked, service.Current.Selection, "Over two minutes: the link is dropped.");
        AssertNothingShown(Shown(service), "The link was dropped");
        Assert.IsNull(service.Current.Left.Percent, "What the set said is not kept either.");

        // The owner's own pair in use, as it was, for ten minutes: without a case open nothing links.
        WearThem(600);
        AssertNothingShown(Shown(service), "Heard again, but no case was opened");

        OpenTheCase(3);
        Assert.AreEqual(100, Shown(service).Left.Percent, "The next case open links again.");
        Assert.AreEqual(90, Shown(service).Case.Percent);
    }

    // ---- Connected only

    [TestMethod]
    public void NothingIsShownWhileNotConnectedEvenWhenLinkedAndItAppearsOnConnecting()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        OpenTheCase(3); // linked, but the AirPods are not on this PC
        WearThem(3);

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection, "The link is made whether or not they are connected.");
        AssertNothingShown(Shown(service), "Not connected");
        Assert.AreEqual(GaugeMode.NotOnThisPc, Gauge(service).Mode);

        Connect();
        WearThem(2);

        Assert.AreEqual(100, Shown(service).Left.Percent, "Connected: the linked pair's values show.");
        Assert.AreEqual(GaugeMode.Reading, Gauge(service).Mode);

        Connect(active: false);
        AssertNothingShown(Shown(service), "Disconnected again");
        Assert.AreEqual(GaugeMode.NotOnThisPc, Gauge(service).Mode);
    }

    // ---- What is counted, what is logged and what is kept

    [TestMethod]
    public void TheLinkTheFollowAndTheDropAreCountedAndLoggedAsNumbersNeverAnAddress()
    {
        const uint left = 0xA1B2C3D4;
        const uint right = 0xB2C3D4E5;
        const uint newLeft = 0xC3D4E5F6;
        const uint newRight = 0xD4E5F607;
        using WidgetStatusService service = NewService();
        int baseline = _log.Entries.Count;
        service.Start();
        Connect();

        OpenTheCase(3, left: left, right: right);
        WearThem(5, left: left, right: right);
        Tick(12);
        WearThem(5, left: newLeft, right: newRight);
        Tick(125);
        _clock.Advance(WidgetTiming.CountersLogInterval);

        WidgetCounters counters = service.Current.Counters;
        Assert.AreEqual(1, counters.Links);
        Assert.AreEqual(1, counters.Followed);
        Assert.AreEqual(1, counters.Drops);
        Assert.AreEqual(0, counters.Switches);
        Assert.IsTrue(_log.Has(LogLevel.Info, "linked to the AirPods whose case was opened (1 in range)"));
        Assert.IsTrue(_log.Has(LogLevel.Info, "the linked set changed address and was followed"));
        Assert.IsTrue(_log.Has(LogLevel.Info, "the link was dropped"));
        string[] tags = ["A1B2C3D4", "B2C3D4E5", "C3D4E5F6", "D4E5F607"];
        foreach (LogEntry entry in _log.Entries.Skip(baseline))
        {
            foreach (string tag in tags)
            {
                Assert.IsFalse(entry.Message.Contains(tag, StringComparison.OrdinalIgnoreCase), "A log line named an address: " + entry.Message);
            }
        }

        LogEntry line = _log.Entries.Last(e => e.Message.StartsWith("Widget counters:", StringComparison.Ordinal));
        StringAssert.Contains(line.Message, " links=1 followed=1 drops=1 ");
    }

    // The link lives in memory only: nothing about it is written, and a new service starts with none.
    [TestMethod]
    public void TheLinkIsHeldInMemoryOnlyAndIsNotThereAfterARestart()
    {
        WidgetStatusService first = NewService();
        first.Start();
        Connect();
        OpenTheCase(3);
        WearThem(5);
        Assert.AreEqual(100, Shown(first).Left.Percent);
        string[] files = Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray()!;
        Assert.IsTrue(files.All(name => name!.StartsWith("settings", StringComparison.Ordinal)), "Nothing but the settings file is written, so nothing for the link: " + string.Join(", ", files));        first.Close();

        _source = new FakeAdvertisementSource();
        using WidgetStatusService second = NewService();
        second.Start();
        Connect();
        WearThem(5);

        Assert.AreNotEqual(BroadcastSelectionState.Linked, second.Current.Selection, "A restart forgets the link: the next case open makes one.");
        AssertNothingShown(Shown(second), "A new run, no case opened");
    }

    // Ear detection from the broadcast is trusted the same way: only the linked set's readings reach it.
    [TestMethod]
    public void TheEarLogicSeesTheLinkedSetsReadingsAndNeverAStrangers()
    {
        var table = ProximityDecodeTable.Documented with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null, table, runInBackground: work => work());
        service.ReadingApplied += (_, e) => _readings.Add(e);
        using WidgetStatusService owned = service;
        service.Start();
        Connect();

        StrangerWornNearby(seconds: 60, fixedRssi: -40);
        Assert.IsEmpty(_readings, "An unlinked pair's in-ear bits never reach the ear logic.");

        OpenTheCase(3);
        WearThem(4);

        Assert.IsNotEmpty(_readings);
        Assert.IsTrue(_readings.All(r => r.Reading.Left.Percent is null or 100), "Only the owner's pair.");
    }

    // ---- The low battery alert and the gauge follow both rules

    [TestMethod]
    public void TheLowBatteryAlertFollowsBothRulesAndTheStrangersLowBudsNeverAlert()
    {
        var notifier = new FakeNotifier();
        using WidgetStatusService service = NewService();
        using var alert = new LowBatteryAlertService(service, _settings, notifier, _clock);
        service.Start();

        // A worn pair of the same model, both buds at 10%, connected to this PC: not the owner's.
        Connect();
        for (int i = 0; i < 40; i++)
        {
            Raise(StrangerBud, BroadcastFixtures.Bud(true, caseNibble: 0xF, pairHigh: 0x1, pairLow: 0x1), -45);
            Tick(0.5);
        }

        Assert.AreEqual(0, notifier.Calls.Count, "A pair that was never linked alerts nothing.");

        // The owner's pair, linked, low, but not connected: not shown, so not alerted.
        Connect(active: false);
        for (int i = 0; i <= 12; i++)
        {
            Raise(i % 2 == 0 ? OwnerLeftBud : OwnerRightBud, BroadcastFixtures.Bud(i % 2 == 0, caseNibble: OwnerCase, pairHigh: 0x1, pairLow: 0x1), -55);
            Tick(0.25);
        }

        Assert.AreEqual(0, notifier.Calls.Count, "Not connected: nothing is shown, so nothing alerts.");

        // Connected and linked: shown, so alerted.
        Connect();
        Raise(OwnerLeftBud, BroadcastFixtures.Bud(true, caseNibble: OwnerCase, pairHigh: 0x1, pairLow: 0x1), -55);

        Assert.IsGreaterThan(0, notifier.Calls.Count, "Connected and linked, the same low buds are shown and alert.");
        Assert.IsTrue(notifier.Calls.All(c => c.Text.EndsWith("10%", StringComparison.Ordinal)));
    }
}
