using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class WidgetStatusServiceTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeDeviceMonitor _deviceMonitor = null!;
    private TestTimeProvider _clock = null!;
    private FakeAdvertisementSource _source = null!;
    private FakePairedModelSource _paired = null!;
    private FakeHandsFreeBatterySource _handsFree = null!;
    private int _posts;
    private List<EventArgs> _changedEvents = null!;
    private List<CaseOpenedEventArgs> _caseOpenedEvents = null!;
    private List<CaseClosedEventArgs> _caseClosedEvents = null!;
    private List<ReadingAppliedEventArgs> _readingEvents = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _clock = new TestTimeProvider();
        _deviceMonitor = new FakeDeviceMonitor(_clock);
        _source = new FakeAdvertisementSource();
        _paired = new FakePairedModelSource();
        _handsFree = new FakeHandsFreeBatterySource();
        _posts = 0;
        _changedEvents = new List<EventArgs>();
        _caseOpenedEvents = new List<CaseOpenedEventArgs>();
        _caseClosedEvents = new List<CaseClosedEventArgs>();
        _readingEvents = new List<ReadingAppliedEventArgs>();
    }

    public void Dispose() => _temp.Dispose();

    // The documented table, or one with extra bits a test wants to exercise (in-ear, the lid).
    private WidgetStatusService NewService(ProximityDecodeTable? table = null, Action<Action>? runInBackground = null)
    {
        var service = new WidgetStatusService(
            () => _source, _settings, _deviceMonitor, () => null, _log,
            action => { Interlocked.Increment(ref _posts); action(); }, _clock, _paired, _handsFree, table ?? ProximityDecodeTable.Documented,
            runInBackground: runInBackground ?? (work => work()));
        service.Changed += (sender, e) => { lock (_changedEvents) { _changedEvents.Add(e); } };
        service.CaseOpened += (sender, e) => { lock (_caseOpenedEvents) { _caseOpenedEvents.Add(e); } };
        service.CaseClosed += (sender, e) => { lock (_caseClosedEvents) { _caseClosedEvents.Add(e); } };
        service.ReadingApplied += (sender, e) => { lock (_readingEvents) { _readingEvents.Add(e); } };
        return service;
    }

    private static ProximityDecodeTable WithInEarBits() =>
        ProximityDecodeTable.Documented with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };

    private DeviceSnapshot ThisPcSnapshot(bool active)
    {
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, active ? EndpointState.Active : EndpointState.Unplugged, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", active ? ConnectionState.Connected : ConnectionState.Disconnected, new[] { endpoint });
        return new DeviceSnapshot(model, new[] { model }, _clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        };
    }

    private void PinContainer() => _settings.Update(s => s.PinnedContainerId = Container);

    // A message of the invented set at the current time, from one sender. batteryA and batteryB default to the
    // documented "unknown" so a test that does not care about a part never sets it.
    private AdvertisementSample Owned(
        byte batteryA = 0xFF, byte batteryB = 0x0F, byte status = 0x00, byte lid = 0x00, sbyte rssi = -60, uint tag = 1,
        byte colour = WidgetFixtures.Colour, byte modelLow = WidgetFixtures.ModelLow) =>
        new(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(modelLow: modelLow, status: status, batteryA: batteryA, batteryB: batteryB, lid: lid, colour: colour), rssi, _clock.GetUtcNow(), tag);

    // A set is linked when its case is opened near the PC: five messages with the case level known inside five seconds,
    // the first and last two seconds apart. A test that needs the set linked sends that burst, which says the case is at
    // 50% and nothing about the buds; what the test then raises is what the linked set says.
    private void Prime(byte lid = 0x00, uint tag = 1)
    {
        for (int i = 0; i < 5; i++)
        {
            _source.Raise(Owned(batteryB: 0x05, lid: lid, tag: tag));
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }
    }

    private AdvertisementSample From(uint tag, ProximityMessage m, sbyte rssi = -60) =>
        new(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(m.ModelHigh, m.ModelLow, m.Status, m.BatteryA, m.BatteryB, m.Lid, m.Colour, m.Reserved),
            rssi, _clock.GetUtcNow(), tag);

    private void Raise(uint tag, ProximityMessage m, sbyte rssi = -60) => _source.Raise(From(tag, m, rssi));

    private void Tick(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    // Sends the same message from one sender every half second for the given time, both ends included.
    private void Send(uint tag, ProximityMessage m, sbyte rssi, double seconds)
    {
        int steps = (int)Math.Round(seconds / 0.5);
        for (int i = 0; i <= steps; i++)
        {
            Raise(tag, m, rssi);
            if (i < steps)
            {
                Tick(0.5);
            }
        }
    }

    // ---- Selection: the paired model, the chosen set, and what only that set may show

    [TestMethod]
    public void StartReadsThePairedModelForThePinnedDevice()
    {
        _settings.Update(s =>
        {
            s.PinnedContainerId = Container;
            s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
        });
        using WidgetStatusService service = NewService();

        service.Start();

        Assert.AreEqual(1, _paired.Reads);
        Assert.AreEqual(Container, _paired.LastContainer);
        Assert.AreEqual(Phase4.RecordedNodes.AirPodsAddress, _paired.LastAddress);
    }

    [TestMethod]
    public void ASettingsChangeThatLeavesThePinnedDeviceAloneReadsNothingAgainButAnotherDeviceDoes()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(1, _paired.Reads);

        _settings.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 30 });
        Assert.AreEqual(1, _paired.Reads, "Nothing about the pinned device changed.");

        _settings.Update(s => s.PinnedContainerId = Container);
        Assert.AreEqual(2, _paired.Reads, "A different pinned device is a different pair of AirPods.");
    }

    [TestMethod]
    public void ADifferentModelAfterAPinnedChangeStartsOverAndDropsTheValues()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A));
        Assert.AreEqual(100, service.Current.Case.Percent);

        _paired.Model = 0x1234;
        _settings.Update(s => s.PinnedContainerId = Container);

        Assert.IsNull(service.Current.Case.Percent, "The values belonged to the other pair.");
        Assert.AreEqual(BroadcastSelectionState.Listening, service.Current.Selection);
    }

    [TestMethod]
    public void WithNoPairedModelNothingIsShownTheMessagesAreCountedAndTheReasonIsLogged()
    {
        _paired.Model = null;
        _paired.Steps = [StepOutcomes.FromConfigRet("cm-list", CfgMgr32.CR_FAILURE, "The device list could not be read.")];
        using WidgetStatusService service = NewService();

        service.Start();
        Prime();
        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A));

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(BroadcastSelectionState.NoPairedModel, snapshot.Selection);
        Assert.IsNull(snapshot.Case.Percent);
        Assert.IsNull(snapshot.Left.Percent);
        Assert.AreEqual(6, snapshot.Counters.NoPairedModel);
        Assert.AreEqual(0, snapshot.Counters.Chosen);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "CR_FAILURE"), "The raw code of the failed read is logged.");
        Assert.IsTrue(_log.Has(LogLevel.Warn, "no paired AirPods model"));
    }

    [TestMethod]
    public void OnlyTheChosenSetsValuesAreShownAndTheOtherSetIsCounted()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage near = BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4);
        ProximityMessage far = BroadcastFixtures.Bud(first: true, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1);

        for (int i = 0; i < 10; i++)
        {
            Raise(1, near, -50);
            Raise(2, far, -70);
            Tick(0.5);
        }

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(BroadcastSelectionState.Linked, snapshot.Selection);
        Assert.AreEqual(90, snapshot.Case.Percent);
        Assert.AreEqual(40, snapshot.Left.Percent);
        Assert.AreEqual(70, snapshot.Right.Percent);
        Assert.IsGreaterThan(0, snapshot.Counters.OtherSet);
        Assert.AreEqual(2, snapshot.Counters.Sets);
    }

    [TestMethod]
    public void AnotherModelAndAnotherColourAreCountedAndNeverShown()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        Raise(9, BroadcastFixtures.Bud(first: true, modelLow: WidgetFixtures.StrangerModelLow, pairHigh: 0x1, pairLow: 0x2, caseNibble: 0x3), -30);
        Raise(8, BroadcastFixtures.Bud(first: true, colour: WidgetFixtures.StrangerColour, pairHigh: 0x1, pairLow: 0x2, caseNibble: 0x3), -30);

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(1, snapshot.Counters.ModelMismatch);
        Assert.AreEqual(1, snapshot.Counters.ColourMismatch);
        Assert.AreEqual(50, snapshot.Case.Percent, "Only the linked set's own case level, from the burst that linked it.");
        Assert.IsNull(snapshot.Left.Percent);
    }

    [TestMethod]
    public void TheSeventeenByteFormIsNeverDecodedWhateverItsBytes()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        for (int i = 0; i < 20; i++)
        {
            _source.Raise(new AdvertisementSample(
                ProximityParser.AppleCompanyId, WidgetFixtures.UnknownSeventeenByteForm(), -20, _clock.GetUtcNow(), 77));
            Tick(0.5);
        }

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(20, snapshot.Counters.UnknownForm);
        Assert.AreEqual(50, snapshot.Case.Percent, "Only the linked set's own case level, from the burst that linked it.");
        Assert.IsNull(snapshot.Left.Percent);
        Assert.IsNull(snapshot.Right.Percent);
        Assert.IsTrue(snapshot.Counters.UnknownForms.Any(s => s.Prefix == 0x06 && s.Length == 17 && s.Count == 20));
    }

    // Each bud sends on its own address and carries the pair swapped, with bit 5 of the status differing: one set,
    // one left and right, and the card never alternates.
    [TestMethod]
    public void OneSetFromTwoAddressesShowsOnePairAndNeverFlickers()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        var pairs = new List<(int? Left, int? Right)>();
        service.ReadingApplied += (s, e) => pairs.Add((e.Reading.Left.Percent, e.Reading.Right.Percent));

        for (int i = 0; i < 20; i++)
        {
            Raise(1, new ProximityMessage(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, 0x00, 0x56, 0x3A, 0, WidgetFixtures.Colour, 0), -58);
            Tick(0.3);
            Raise(2, new ProximityMessage(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, 0x20, 0x65, 0x3A, 0, WidgetFixtures.Colour, 0), -56);
            Tick(0.4);
        }

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(60, snapshot.Left.Percent);
        Assert.AreEqual(50, snapshot.Right.Percent);
        Assert.AreEqual(100, snapshot.Case.Percent);
        Assert.AreEqual(true, snapshot.Left.Charging);
        Assert.AreEqual(true, snapshot.Right.Charging);
        Assert.AreEqual(false, snapshot.Case.Charging);
        Assert.AreEqual(1, snapshot.Counters.Sets);
        Assert.AreEqual(0, snapshot.Counters.BudOrderDisagree);
        Assert.IsGreaterThan(10, pairs.Count);
        Assert.IsTrue(pairs.All(p => p == (60, 50)), "Every reading of the set decodes to the same left and right.");
    }

    [TestMethod]
    public void TwoSendersOfTheChosenSetThatDisagreeOnWhichBudIsWhichAreCountedAndLoggedOnce()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        for (int i = 0; i < 20; i++)
        {
            // The same flip bit with the pair swapped: the rule reads them as left 50, right 60 and left 60, right 50.
            Raise(1, new ProximityMessage(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, 0x00, 0x56, 0x0A, 0, WidgetFixtures.Colour, 0), -58);
            Tick(0.3);
            Raise(2, new ProximityMessage(WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, 0x00, 0x65, 0x0A, 0, WidgetFixtures.Colour, 0), -56);
            Tick(0.4);
        }

        Assert.IsGreaterThan(0, service.Current.Counters.BudOrderDisagree);
        Assert.AreEqual(1, _log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("read different left and right levels", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnotherPairOpeningItsCaseClearlyNearerTakesTheLinkAndTheOldPairsValuesAreDropped()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage a = BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4);

        // The pair that opens its case says its own case level (20%), so what is shown for the case afterwards can only
        // be the old pair's 90% if the switch failed to drop it.
        ProximityMessage b = BroadcastFixtures.Bud(first: true, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1);
        Send(1, a, -60, 3);
        Assert.AreEqual(90, service.Current.Case.Percent);

        for (int i = 0; i <= 16; i++)
        {
            Raise(1, a, -60);
            Raise(2, b, -51);
            Tick(0.25);
        }

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(1, snapshot.Counters.Switches);
        Assert.AreEqual(20, snapshot.Case.Percent, "The old pair's case level is not shown against the new pair.");
        Assert.AreEqual(10, snapshot.Left.Percent);
        Assert.AreEqual(30, snapshot.Right.Percent);
    }

    // A set heard under a new address while the chosen one still sent is not the chosen one, whatever it says.
    [TestMethod]
    public void AnEqualSetUnderANewAddressWhileTheChosenSetStillSentIsNotShown()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage a = BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4);
        Send(1, a, -60, 3);
        int appliedBefore = _readingEvents.Count;
        Tick(3.0);

        Raise(5, a, -60);

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(appliedBefore, _readingEvents.Count, "Nothing was applied: the chosen set was heard inside the window.");
        Assert.AreEqual(1, snapshot.Counters.OtherSet);
        Assert.AreEqual(0, snapshot.Counters.Switches);
    }

    // The addresses rotated: the linked set goes quiet under its old addresses and is heard under a new one with the fields
    // it last said. It is followed, the values on show stay greyed as they age until the new address's own messages
    // replace them, and nothing the new address does not say (the case level, which a pair in use does not send) is
    // carried over as current.
    [TestMethod]
    public void AfterAnAddressChangeTheValuesOnShowStayUntilTheFollowedSetsOwnMessagesReplaceThem()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage inUse = BroadcastFixtures.Bud(first: true, caseNibble: 0xF, pairHigh: 0x7, pairLow: 0x4);
        Send(1, BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4), -60, 3);
        DateTimeOffset caseHeardAt = _clock.GetUtcNow();
        Tick(0.5);
        Send(1, inUse, -60, 2);
        DateTimeOffset budsHeardAt = _clock.GetUtcNow();
        Tick(12);

        // The new address says the buds' levels and not the case's.
        Send(5, inUse, -60, 1.5);
        WidgetSnapshot waiting = service.Current;
        Assert.AreEqual(0, waiting.Counters.Followed, "Not followed yet: four messages inside a second and a half.");
        Assert.AreEqual(40, waiting.Left.Percent, "The old values stand, with their own read time.");
        Assert.AreEqual(budsHeardAt, waiting.Left.ReadAt);

        Tick(0.5);
        Send(5, inUse, -60, 1.0);

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(BroadcastSelectionState.Linked, snapshot.Selection);
        Assert.AreEqual(1, snapshot.Counters.Followed);
        Assert.AreEqual(40, snapshot.Left.Percent);
        Assert.AreEqual(70, snapshot.Right.Percent);
        Assert.AreEqual(_clock.GetUtcNow(), snapshot.Left.ReadAt, "The new address's own message replaced the read time.");
        Assert.AreEqual(90, snapshot.Case.Percent, "The case level was not said again: the old one stays.");
        Assert.AreEqual(caseHeardAt, snapshot.Case.ReadAt, "...with its own, old read time, so it is greyed and never current.");
        Assert.AreEqual(0, snapshot.Counters.Switches);
        Assert.AreEqual(1, snapshot.Counters.Links, "Only the case open linked.");
        Assert.IsTrue(_log.Has(LogLevel.Info, "the linked set changed address and was followed"));
    }

    // The values on show stay when a set is followed, but the set may be another pair, so what pairs readings by set
    // (ear detection) is told: the readings of the set followed carry a later generation than those before the quiet.
    [TestMethod]
    public void ASetFollowedToANewAddressCarriesALaterSelectionGeneration()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage inUse = BroadcastFixtures.Bud(first: true, caseNibble: 0xF, pairHigh: 0x7, pairLow: 0x4);
        Send(1, BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4), -60, 3);
        Tick(0.5);
        Send(1, inUse, -60, 2);
        long before = _readingEvents[^1].SelectionGeneration;
        Tick(12);

        Send(5, inUse, -60, 2.5);

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);
        Assert.IsTrue(_log.Has(LogLevel.Info, "the linked set changed address and was followed"));
        Assert.IsGreaterThan(before, _readingEvents[^1].SelectionGeneration, "A set followed may be another pair: its readings do not pair with the earlier ones.");
    }

    // One message of a far sender that said what the chosen set said, then fields of its own: once it has left the
    // window of that message it is another set, and none of its values reach the card.
    [TestMethod]
    public void AFarStrangerThatMatchedOnceAndThenDiffersNeverShowsItsValues()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage owner = BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4);
        ProximityMessage strangers = BroadcastFixtures.Bud(first: true, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1);
        Send(1, owner, -50, 3);
        Tick(0.5);
        Raise(5, owner, -85);
        for (int i = 0; i < 30; i++)
        {
            Tick(0.5);
            Raise(1, owner, -50);
            Raise(5, strangers, -85);
        }

        int applied = _readingEvents.Count;
        for (int i = 0; i < 6; i++)
        {
            Tick(0.5);
            Raise(1, owner, -50);
            Raise(5, strangers, -85);
        }

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(applied + 6, _readingEvents.Count, "Only the owner's six messages were applied.");
        Assert.AreEqual(90, snapshot.Case.Percent);
        Assert.AreEqual(40, snapshot.Left.Percent);
        Assert.AreEqual(70, snapshot.Right.Percent);
        Assert.IsTrue(_readingEvents.Skip(applied).All(e => e.Reading.Case.Percent == 90 && e.Reading.Left.Percent == 40 && e.Reading.Right.Percent == 70));
    }

    // The same stranger heard in the ten seconds after its one equal message, while it still merges into the set: its
    // own fields do not reach the card then either.
    [TestMethod]
    public void AStrangerThatMergedOnceNeverShowsItsDifferentValuesWhileItStillMerges()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage owner = BroadcastFixtures.Bud(first: true, caseNibble: 0x9, pairHigh: 0x7, pairLow: 0x4);
        ProximityMessage strangers = BroadcastFixtures.Bud(first: true, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1);
        Send(1, owner, -50, 3);
        Tick(0.5);
        Raise(5, owner, -85);
        int applied = _readingEvents.Count;

        for (int i = 0; i < 20; i++)
        {
            Tick(0.5);
            Raise(1, owner, -50);
            Raise(5, strangers, -85);
        }

        Assert.AreEqual(applied + 20, _readingEvents.Count, "Only the owner's twenty messages were applied.");
        Assert.IsTrue(_readingEvents.Skip(applied).All(e => e.Reading.Case.Percent == 90 && e.Reading.Left.Percent == 40 && e.Reading.Right.Percent == 70));
        Assert.AreEqual(90, service.Current.Case.Percent);
        Assert.AreEqual(40, service.Current.Left.Percent);
        Assert.AreEqual(70, service.Current.Right.Percent);
    }

    // What pairs readings by chosen set (ear detection) is told when the chosen set changes.
    [TestMethod]
    public void TheSelectionGenerationOfAReadingGrowsWhenAnotherSetIsChosenAndHoldsWhileTheSameSetIsHeard()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ProximityMessage a = BroadcastFixtures.Bud(first: true);
        ProximityMessage b = BroadcastFixtures.OtherSet();
        Send(1, a, -60, 3);
        long[] firstSet = _readingEvents.Select(e => e.SelectionGeneration).ToArray();
        Assert.IsNotEmpty(firstSet);
        Assert.AreEqual(1, firstSet.Distinct().Count(), "One chosen set, one generation.");

        for (int i = 0; i <= 16; i++)
        {
            Raise(1, a, -60);
            Raise(3, b, -51);
            Tick(0.25);
        }

        long last = _readingEvents[^1].SelectionGeneration;
        Assert.IsGreaterThan(firstSet[0], last, "Another set was chosen: its readings carry a later generation.");
    }

    [TestMethod]
    public void TheChosenSetIsReleasedAfterAnHourWithNoMessageFromIt()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);

        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        Assert.AreEqual(BroadcastSelectionState.Listening, service.Current.Selection);
    }

    [TestMethod]
    public void ReadingAppliedIsRaisedForTheChosenSetsMessagesOnly()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        int afterPrime = _readingEvents.Count;

        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A));
        Assert.AreEqual(afterPrime + 1, _readingEvents.Count);
        Assert.AreEqual(100, _readingEvents[^1].Reading.Case.Percent);

        Raise(9, BroadcastFixtures.Bud(first: true, modelLow: WidgetFixtures.StrangerModelLow), -30);
        Raise(8, BroadcastFixtures.Bud(first: true, colour: WidgetFixtures.StrangerColour), -30);

        Assert.AreEqual(afterPrime + 1, _readingEvents.Count, "A message of another model or colour raises nothing.");
    }

    [TestMethod]
    public void TheCountersLineKeepsTheFieldsTheLiveTestReadsInTheirOrder()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A));

        _clock.Advance(WidgetTiming.CountersLogInterval);

        LogEntry line = _log.Entries.Last(e => e.Message.StartsWith("Widget counters:", StringComparison.Ordinal));
        string[] keys = ["allSections=", "apple=", "other=", "items=", "ok=", "truncated=", "unknownForm=", "modelMismatch=", "colourMismatch=", "otherSet=", "chosen=", "noPairedModel=", "budOrderDisagree=", "switches=", "links=", "followed=", "drops=", "sets=", "unknownFormShapes="];
        int at = -1;
        foreach (string key in keys)
        {
            int next = line.Message.IndexOf(key, StringComparison.Ordinal);
            Assert.IsGreaterThan(at, next, key + " must come after the field before it in: " + line.Message);
            at = next;
        }

        Assert.Contains(" allSections=6 ", line.Message);
        Assert.Contains(" ok=6 ", line.Message);
        Assert.Contains(" chosen=2 ", line.Message);
    }

    [TestMethod]
    public void AutoPauseIsAvailableOnlyWhenTheTableHasInEarBits()
    {
        using WidgetStatusService documented = NewService();
        documented.Start();
        Assert.IsFalse(documented.Current.AutoPauseAvailable, "The documented table sets no in-ear bit.");

        var other = new FakeAdvertisementSource();
        using var withBits = new WidgetStatusService(
            () => other, _settings, _deviceMonitor, () => null, _log, action => action(), _clock, _paired, null, WithInEarBits());
        withBits.Start();
        Assert.IsTrue(withBits.Current.AutoPauseAvailable);
    }

    [TestMethod]
    public void WhereIsUnknownOffThisPcWhileTheInEarBitsAreNotSet()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        Prime();
        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A));

        Assert.AreEqual(AirPodsWhere.Unknown, service.Current.Where);
    }

    [TestMethod]
    public void BeforeAnyReadingEverythingIsUnknown()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        WidgetSnapshot snapshot = service.Current;

        Assert.AreEqual(AirPodsWhere.Unknown, snapshot.Where);
        Assert.IsNull(snapshot.Left.Percent);
        Assert.IsNull(snapshot.Right.Percent);
        Assert.IsNull(snapshot.Case.Percent);
        Assert.IsNull(snapshot.Headset.Percent);
        Assert.IsNull(snapshot.BatteryReadAt);
        Assert.IsNull(snapshot.EarReadAt);
        Assert.IsNull(snapshot.LidOpen);
        Assert.AreEqual(WidgetWatcherState.Started, snapshot.Watcher);
        Assert.AreEqual(BroadcastSelectionState.Listening, snapshot.Selection);
        Assert.IsFalse(snapshot.AutoPauseAvailable);
        Assert.AreEqual(0, snapshot.Counters.AllSections);
    }

    // ---- Windows' own Hands-Free figure, read beside the broadcast and held apart from it

    private void PinAndConnect()
    {
        _settings.Update(s =>
        {
            s.PinnedContainerId = Container;
            s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
        });
        _deviceMonitor.Raise(ThisPcSnapshot(active: true));
    }

    [TestMethod]
    public void WindowsFigureIsReadOnceWhenTheAirPodsBecomeThisPcsOutputWithThePinnedDevice()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(0, _handsFree.Reads, "Not on this PC yet: nothing is read.");

        PinAndConnect();

        Assert.AreEqual(1, _handsFree.Reads);
        Assert.AreEqual(Container, _handsFree.LastContainer);
        Assert.AreEqual(Phase4.RecordedNodes.AirPodsAddress, _handsFree.LastAddress);
        Assert.AreEqual(70, service.Current.Headset.Percent);
        Assert.AreEqual(_clock.GetUtcNow(), service.Current.Headset.ReadAt);
    }

    [TestMethod]
    public void WindowsFigureIsReadEveryMinuteWhileOnThisPcAndNotOtherwise()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        PinAndConnect();
        Assert.AreEqual(1, _handsFree.Reads);

        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(2, _handsFree.Reads);
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(3, _handsFree.Reads);

        _deviceMonitor.Raise(ThisPcSnapshot(active: false));
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        _clock.Advance(WidgetTiming.HeadsetPollInterval);

        Assert.AreEqual(3, _handsFree.Reads, "Off this PC it is not read.");
    }

    [TestMethod]
    public void ARunningReadSkipsTheNextTickAndTheOneAfterItFinishesReadsAgain()
    {
        var queued = new Queue<Action>();
        using WidgetStatusService service = NewService(runInBackground: queued.Enqueue);
        service.Start();
        PinAndConnect();
        Assert.AreEqual(1, queued.Count, "The connect starts one read.");

        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(1, queued.Count, "The first read has not finished, so the tick starts none.");
        Assert.AreEqual(0, _handsFree.Reads);

        queued.Dequeue()();
        Assert.AreEqual(1, _handsFree.Reads);

        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(1, queued.Count, "Once finished, the next tick reads again.");
    }

    [TestMethod]
    public void NoWindowsFigureIsReadWhileSuspendedOrAfterClose()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        PinAndConnect();
        int before = _handsFree.Reads;

        service.Suspend();
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(before, _handsFree.Reads, "Suspended: nothing is read.");

        service.Resume();
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(before + 1, _handsFree.Reads);

        service.Close();
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        Assert.AreEqual(before + 1, _handsFree.Reads, "Closed: nothing is read.");
    }

    [TestMethod]
    public void AFigureIsHeldApartFromTheBudsAndOnlyShownWhenNoBudIsFresh()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A));
        PinAndConnect();

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(70, snapshot.Headset.Percent);
        Assert.AreEqual(60, snapshot.Left.Percent, "It is never written into Left or Right.");
        Assert.AreEqual(50, snapshot.Right.Percent);
        Assert.IsNull(BatteryFreshness.Shown(snapshot, _clock.GetUtcNow()).WindowsPercent, "A fresh bud is shown, so Windows' figure is not.");

        _clock.Advance(TimeSpan.FromSeconds(45));
        _handsFree.Percent = 68;
        _clock.Advance(WidgetTiming.HeadsetPollInterval - TimeSpan.FromSeconds(45));

        ShownBattery shown = BatteryFreshness.Shown(service.Current, _clock.GetUtcNow());
        Assert.AreEqual(68, shown.WindowsPercent, "With no bud fresh and Windows' figure current, it is what is shown.");
    }

    [TestMethod]
    public void NoFigureLogsWhyOnceAndClearsTheFigureAnEarlierReadFound()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        PinAndConnect();
        Assert.AreEqual(70, service.Current.Headset.Percent);

        _handsFree.Percent = null;
        _handsFree.Note = "No figure: the property was empty or absent on 8 device nodes and 1 paired objects.";
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        _clock.Advance(WidgetTiming.HeadsetPollInterval);
        _clock.Advance(WidgetTiming.HeadsetPollInterval);

        Assert.IsNull(service.Current.Headset.Percent, "A read that finds none says there is none now: the old figure is not left to age.");
        Assert.IsNull(service.Current.Headset.ReadAt);
        Assert.AreEqual(1, _log.Entries.Count(e => e.Message.Contains("empty or absent on 8 device nodes", StringComparison.Ordinal)), "The same finding is logged once.");
    }

    // A read that was running when the AirPods left this PC finishes after they have gone: its figure is not theirs.
    [TestMethod]
    public void AFigureReadThatFinishesAfterTheAirPodsLeftThisPcIsNotTaken()
    {
        var queued = new Queue<Action>();
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService(runInBackground: queued.Enqueue);
        service.Start();
        PinAndConnect();
        Assert.AreEqual(1, queued.Count, "The connect starts one read.");

        _deviceMonitor.Raise(ThisPcSnapshot(active: false));
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        queued.Dequeue()();
        _clock.Advance(WidgetTiming.RefreshWindow);

        Assert.IsNull(service.Current.Headset.Percent, "The AirPods are not on this PC any more.");
        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, refresh.Result, "A figure for AirPods that left is not an answer to a refresh.");
    }

    [TestMethod]
    public void AFailingStepIsLoggedAtWarnWithItsRawCode()
    {
        _handsFree.Steps = [StepOutcomes.FromConfigRet("hands-free-battery:cm-property", CfgMgr32.CR_FAILURE, "The battery property of a node could not be read.")];
        using WidgetStatusService service = NewService();
        service.Start();

        PinAndConnect();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "hands-free-battery:cm-property CR_FAILURE"));
    }

    [TestMethod]
    public void AnUnexpectedExceptionIsLoggedWithItsCodeAndTheNextTickReadsAgain()
    {
        _handsFree.Throws = new InvalidOperationException("a bug in this process");
        using WidgetStatusService service = NewService();
        service.Start();

        PinAndConnect();

        Assert.IsTrue(_log.Has(LogLevel.Error, "unexpected error (0x"));
        _handsFree.Throws = null;
        _handsFree.Percent = 55;
        _clock.Advance(WidgetTiming.HeadsetPollInterval);

        Assert.AreEqual(55, service.Current.Headset.Percent, "The failed read did not leave the service believing one was still running.");
    }

    // ---- The lifecycle, the logs and the rest, as they were before the battery came from the broadcast


    // Close, and turning the setting off, both call RunStopOutsideLock with disposeSource true: the source
    // is meant to actually be released, not just forgotten. Every test elsewhere in this file that ends with
    // service.Dispose() would read exactly the same whether or not that Dispose call ever reached the
    // source: nothing in the service's own public state (Current, the events, _source becoming null from
    // the outside) tells the two apart, since _source is private. Only asking the fake itself whether its
    // own Dispose ran proves the source is actually let go, not merely dropped.
    [TestMethod]
    public void CloseActuallyDisposesTheSourceNotJustTheServicesOwnReferenceToIt()
    {
        WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(0, _source.DisposeCalls, "Sanity: nothing has disposed the source yet.");

        service.Close();

        Assert.AreEqual(1, _source.DisposeCalls, "Close must dispose the source it stopped, not merely stop tracking it.");
    }

    [TestMethod]
    public void ThisPcComesFromCoreAudioAlone()
    {
        PinContainer();
        using WidgetStatusService service = NewService();
        service.Start();

        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        Assert.AreEqual(AirPodsWhere.ThisPc, service.Current.Where);

        _deviceMonitor.Raise(ThisPcSnapshot(active: false));

        Assert.AreNotEqual(AirPodsWhere.ThisPc, service.Current.Where);
    }

    [TestMethod]
    public void AnOwnedFreshReadingWithABudInEarWhileNotOnThisPcIsElsewhere()
    {
        ProximityDecodeTable table = WithInEarBits();
        using WidgetStatusService service = NewService(table);
        service.Start();
        Prime();

        _source.Raise(Owned(status: 0b0000_0001)); // left in ear

        Assert.AreEqual(AirPodsWhere.Elsewhere, service.Current.Where);
    }

    [TestMethod]
    public void AnOwnedFreshReadingWithNoBudInEarIsNotInUse()
    {
        ProximityDecodeTable table = WithInEarBits();
        using WidgetStatusService service = NewService(table);
        service.Start();
        Prime();

        _source.Raise(Owned(status: 0b0000_0000)); // neither bud in ear

        Assert.AreEqual(AirPodsWhere.NotInUse, service.Current.Where);
    }

    [TestMethod]
    public void AStaleReadingLeavesWhereUnknownAndEarStateNull()
    {
        ProximityDecodeTable table = WithInEarBits();
        using WidgetStatusService service = NewService(table);
        service.Start();
        Prime();

        _source.Raise(Owned(status: 0b0000_0001));
        Assert.AreEqual(AirPodsWhere.Elsewhere, service.Current.Where);

        _clock.Advance(WidgetTiming.EarFreshWindow + TimeSpan.FromSeconds(1));

        Assert.AreEqual(AirPodsWhere.Unknown, service.Current.Where);
        Assert.IsNull(service.Current.EarReadAt);
    }

    // M6, reviewer probe P2: per-bud InEar must go null once the reading is stale, on a table where the bud
    // order is proved (so this is not just about the Elsewhere/NotInUse rollup: the raw per-bud field must
    // clear too), while the battery percent it arrived with is unaffected, since battery never expires.
    [TestMethod]
    public void AStaleReadingClearsPerBudInEarButKeepsTheBatteryPercent()
    {
        ProximityDecodeTable table = WithInEarBits();
        using WidgetStatusService service = NewService(table);
        service.Start();
        Prime();

        // High nibble (right) 3 -> 30%, low nibble (left) 5 -> 50%; both bits set: both buds in ear.
        _source.Raise(Owned(status: 0b0000_0011, batteryA: 0x35));

        Assert.AreEqual(true, service.Current.Left.InEar);
        Assert.AreEqual(true, service.Current.Right.InEar);
        Assert.AreEqual(50, service.Current.Left.Percent);
        Assert.AreEqual(30, service.Current.Right.Percent);

        _clock.Advance(WidgetTiming.EarFreshWindow + TimeSpan.FromSeconds(1));

        Assert.IsNull(service.Current.Left.InEar, "A stale reading must not keep reporting a bud as in or out of the ear.");
        Assert.IsNull(service.Current.Right.InEar);
        Assert.AreEqual(50, service.Current.Left.Percent, "Battery never expires, whatever the ear state does.");
        Assert.AreEqual(30, service.Current.Right.Percent);
    }

    // BatteryNibble.IsOutOfRange (11 to 14) needed a caller: a nibble that shape is logged as the form
    // drifting, never as an ordinary unknown (0xF) and never with the nibble value itself in the line.
    [TestMethod]
    public void AnOutOfRangeNibbleLogsThatTheFormDrifted()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        _source.Raise(Owned(batteryA: 0x0B)); // low nibble 0xB (11): out of range, not the documented 0xF

        Assert.IsTrue(_log.Has(LogLevel.Warn, "form may have drifted"));
    }

    // A value is not cleared by its age: it stays with its own read time and the card greys it. What clears it is the link
    // being dropped, after the linked set has been lost for longer than two minutes.
    [TestMethod]
    public void BatteryKeepsItsReadTimeWhileLinkedAndIsClearedWhenTheLinkIsDropped()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        DateTimeOffset at = _clock.GetUtcNow();
        _source.Raise(Owned(batteryB: 0x05));
        Assert.AreEqual(50, service.Current.Case.Percent);
        Assert.AreEqual(at, service.Current.Case.ReadAt);

        _clock.Advance(TimeSpan.FromSeconds(110));

        Assert.AreEqual(50, service.Current.Case.Percent, "Lost for under two minutes: still linked, so still kept.");
        Assert.AreEqual(at, service.Current.Case.ReadAt);
        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);

        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.IsNull(service.Current.Case.Percent, "Lost for over two minutes: the link is dropped and nothing is kept.");
        Assert.AreEqual(BroadcastSelectionState.Listening, service.Current.Selection);
        Assert.AreEqual(1, service.Current.Counters.Drops);
        Assert.IsTrue(_log.Has(LogLevel.Info, "the link was dropped"));
    }

    [TestMethod]
    public void BatteryReadAtIsTheNewestKnownPart()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        DateTimeOffset first = _clock.GetUtcNow();
        _source.Raise(Owned(batteryA: 0x00, batteryB: 0x05)); // case known now, buds known now too (nibble 0)
        _clock.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset second = _clock.GetUtcNow();
        _source.Raise(Owned(batteryA: 0xFF, batteryB: 0x06)); // buds unknown this time, case updates

        Assert.AreEqual(60, service.Current.Case.Percent);
        Assert.AreEqual(second, service.Current.Case.ReadAt);
        Assert.AreEqual(first, service.Current.Left.ReadAt);
        Assert.AreEqual(second, service.Current.BatteryReadAt, "The newest known ReadAt among the parts.");
    }

    // The case says nothing while its lid is shut (its nibble reads unknown), so its figure is old; the buds were
    // just heard. The battery was read just now, and only the case is old.
    [TestMethod]
    public void BudsHeardJustNowWithTheCaseUnsaidMakeTheBatteryReadTimeNowNotTheCasesOldOne()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0A)); // case 100 now
        DateTimeOffset caseReadAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromSeconds(9));

        _source.Raise(Owned(batteryA: 0x56, batteryB: 0x0F)); // buds known, case unknown

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(100, snapshot.Case.Percent, "The case keeps its last figure.");
        Assert.AreEqual(caseReadAt, snapshot.Case.ReadAt);
        Assert.AreEqual(_clock.GetUtcNow(), snapshot.Left.ReadAt);
        Assert.AreEqual(_clock.GetUtcNow(), snapshot.BatteryReadAt, "The card says the battery was read just now, not nine seconds ago.");
    }

    [TestMethod]
    public void AnUnknownPercentLeavesThePartAsItWas()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        _source.Raise(Owned(batteryB: 0x05));
        Assert.AreEqual(50, service.Current.Case.Percent);

        _source.Raise(Owned(batteryB: 0x0F)); // case now unknown (0xF)

        Assert.AreEqual(50, service.Current.Case.Percent, "An unknown percent must not overwrite the last known one.");
    }

    // Counters tick on almost every advertisement, so comparing the whole snapshot (WidgetCounters included)
    // to decide whether to raise Changed would fire it constantly even when nothing the UI shows moved.
    [TestMethod]
    public void CounterChangesAloneDoNotRaiseChanged()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        _source.Raise(Owned(batteryB: 0x05));
        int changedAfterFirst = _changedEvents.Count;
        Assert.IsTrue(changedAfterFirst > 0, "The first reading must raise Changed.");

        _source.Raise(Owned(batteryB: 0x05)); // identical reading: counters tick, nothing UI-visible moves

        Assert.AreEqual(changedAfterFirst, _changedEvents.Count, "A counter-only change must not raise Changed again.");
    }

    // ---- The case opening and closing (CaseOpenTracker), through the real selection and link code

    // The case opening next to the PC is what links a pair (Prime: five case-known messages over two seconds), and the
    // message that makes the link is the first open: not one before it, while nothing is linked.
    [TestMethod]
    public void AnOpenThatLinksThePairIsAnOpenAndNothingOpensBeforeTheLink()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        for (int i = 0; i < 4; i++)
        {
            _source.Raise(Owned(batteryB: 0x05));
            Tick(0.5);
        }

        Assert.AreEqual(0, _caseOpenedEvents.Count, "Four case-known messages do not link, so nothing has opened yet.");

        _source.Raise(Owned(batteryB: 0x05));

        Assert.AreEqual(1, _caseOpenedEvents.Count, "The message that links the pair is its case opening.");
        Assert.AreEqual(_clock.GetUtcNow(), _caseOpenedEvents[0].At);
    }

    [TestMethod]
    public void TheLinkedPairsCaseKnownMessagesStartingAfterSilenceAreAnOpen()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        Tick(CaseOpenTracker.CloseAfter.TotalSeconds);
        Assert.AreEqual(1, _caseClosedEvents.Count, "Silence closed the first open.");

        _source.Raise(Owned(batteryB: 0x05));

        Assert.AreEqual(2, _caseOpenedEvents.Count, "The case opened again.");
    }

    // A closed case sends nothing, so nothing can open from it, however long it stays shut.
    [TestMethod]
    public void AClosedCaseNeverOpens()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();

        for (int i = 0; i < 30; i++)
        {
            Tick(10);
        }

        Assert.AreEqual(1, _caseOpenedEvents.Count);
        Assert.AreEqual(1, _caseClosedEvents.Count, "Closed once, and only once.");
    }

    // Buds in use send the case level as unknown (0xF): the linked pair's own worn buds never open anything, and a pair worn
    // nearby is never linked, so it never opens anything either.
    [TestMethod]
    public void WornBudsNeverOpen()
    {
        using (WidgetStatusService service = NewService())
        {
            service.Start();
            for (int i = 0; i < 20; i++)
            {
                _source.Raise(Owned(batteryA: 0x88, batteryB: 0x0F, tag: 7));
                Tick(1);
            }

            Assert.AreEqual(0, _caseOpenedEvents.Count, "A worn pair is never linked and never opens.");
        }

        _caseOpenedEvents.Clear();
        _caseClosedEvents.Clear();
        using WidgetStatusService linked = NewService();
        linked.Start();
        Prime();
        for (int i = 0; i < 30; i++)
        {
            _source.Raise(Owned(batteryB: 0x0F));
            Tick(1);
        }

        Assert.AreEqual(1, _caseOpenedEvents.Count, "The linked pair's buds taken out and worn open nothing.");
        Assert.AreEqual(1, _caseClosedEvents.Count, "And the case counts as closed once its case-known messages stopped.");
    }

    // The lid shut and opened again inside the close time: the case-known messages never stopped, but the lid open counter
    // moved. The pair's other bud carries the change a moment later; that is the same open, not a second.
    [TestMethod]
    public void TheLidCounterChangingWhileTheCaseStaysOpenIsANewOpen()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime(lid: 0x03);
        for (int i = 0; i < 12; i++)
        {
            _source.Raise(Owned(batteryB: 0x05, lid: 0x03, tag: 1));
            Tick(0.125);
            _source.Raise(Owned(batteryB: 0x05, lid: 0x03, tag: 2));
            Tick(0.125);
        }

        Assert.AreEqual(1, _caseOpenedEvents.Count, "The same counter is the same open.");

        _source.Raise(Owned(batteryB: 0x05, lid: 0x04, tag: 1));
        Assert.AreEqual(2, _caseOpenedEvents.Count, "A moved counter is a new open.");

        Tick(0.25);
        _source.Raise(Owned(batteryB: 0x05, lid: 0x04, tag: 2));
        Assert.AreEqual(2, _caseOpenedEvents.Count, "The other bud's copy of the change is the same open.");
        Assert.AreEqual(0, _caseClosedEvents.Count, "The case never closed in between.");
    }

    // The close time is CaseOpenTracker.CloseAfter after the last case-known message, to the tick.
    [TestMethod]
    public void TheCaseClosesWhenItsCaseKnownMessagesStopForTheCloseTime()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime(); // the last case-known message was half a second ago

        Tick(CaseOpenTracker.CloseAfter.TotalSeconds - 0.6);
        Assert.AreEqual(0, _caseClosedEvents.Count, "Not yet.");

        Tick(0.1);
        Assert.AreEqual(1, _caseClosedEvents.Count, "Closed at the close time.");
    }

    // One sender's longest gap in the saved records is 6.14 s: a case with one bud in it, sending that slowly, stays open.
    [TestMethod]
    public void ASenderAsSlowAsTheSlowestRecordedKeepsTheCaseOpen()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Prime();
        for (int i = 0; i < 6; i++)
        {
            Tick(6.14);
            _source.Raise(Owned(batteryB: 0x05));
        }

        Assert.AreEqual(1, _caseOpenedEvents.Count);
        Assert.AreEqual(0, _caseClosedEvents.Count);
    }

    // A boolean "are we currently inside some uiPost action" cannot tell a genuine post apart from code
    // that simply runs synchronously nested inside an already-posted action, so it passed even when both
    // events were raised directly. This fake queues every posted action instead of running it
    // immediately, and records how many previously queued actions had FULLY finished (been dequeued and
    // returned) by the moment each event fires. A raise made through its own, separate uiPost call only runs
    // after the action that led to it has completed, so it fires with a completed-count of at least one; a
    // direct raise nested inside that same still-running action fires while the count is still what it was
    // before that action started.
    [TestMethod]
    public void ChangedAndCaseOpenedAreRaisedThroughUiPost()
    {
        var queue = new Queue<Action>();
        int itemsCompleted = 0;
        var changedCompletedCountAtFire = new List<int>();
        var caseOpenedCompletedCountAtFire = new List<int>();

        var service = new WidgetStatusService(
            () => _source, _settings, _deviceMonitor, () => null, _log,
            action =>
            {
                Interlocked.Increment(ref _posts);
                queue.Enqueue(action);
            },
            _clock, _paired, null, ProximityDecodeTable.Documented);
        service.Changed += (sender, e) => changedCompletedCountAtFire.Add(itemsCompleted);
        service.CaseOpened += (sender, e) => caseOpenedCompletedCountAtFire.Add(itemsCompleted);
        service.Start();
        int postsBefore = _posts;

        // The case opening links the pair: the fifth message's handling posts CaseOpened.
        Prime();

        while (queue.Count > 0)
        {
            Action item = queue.Dequeue();
            item();
            itemsCompleted++;
        }

        Assert.IsTrue(_posts > postsBefore);
        Assert.IsTrue(changedCompletedCountAtFire.Count > 0, "Changed must have been raised.");
        Assert.IsTrue(
            changedCompletedCountAtFire.All(v => v >= 1),
            "Changed must be raised from its own uiPost action, not nested inside the action that led to it.");
        Assert.AreEqual(1, caseOpenedCompletedCountAtFire.Count);
        Assert.IsTrue(
            caseOpenedCompletedCountAtFire.All(v => v >= 1),
            "CaseOpened must be raised from its own uiPost action, not nested inside the action that led to it.");
        service.Dispose();
    }

    // Suspend's own Stop() call discarded the step outcome. A failing stop must be logged, at Warn.
    [TestMethod]
    public void SuspendLogsAFailingStopStepAtWarn()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        _source.StopResult = () => StepOutcomes.FromHResult("fake-stop", 5, detail: "ACCESS_DENIED", ok: false);

        service.Suspend();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "ACCESS_DENIED"), "A failing stop step must be logged at Warn with its detail.");
    }

    // StopSourceLocked's Stop() call (reached through Close) discarded the step outcome the same way.
    [TestMethod]
    public void ClosingWithAFailingStopStepLogsItAtWarn()
    {
        var service = NewService();
        service.Start();
        _source.StopResult = () => StepOutcomes.FromHResult("fake-stop", 5, detail: "ACCESS_DENIED", ok: false);

        service.Close();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "ACCESS_DENIED"), "A failing stop step must be logged at Warn with its detail.");
    }

    // A failing start step must be logged at Warn too, not always at Info as if it had succeeded.
    [TestMethod]
    public void AFailingStartStepIsLoggedAtWarn()
    {
        using WidgetStatusService service = NewService();
        _source.StartResult = () => StepOutcomes.FromHResult("fake-start", 5, detail: "ACCESS_DENIED", ok: false);

        service.Start();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "ACCESS_DENIED"), "A failing start step must be logged at Warn with its detail.");
    }

    [TestMethod]
    public void TheStoppedEventIsLoggedWithItsErrorAndShownInTheSnapshot()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(WidgetWatcherState.Stopped, snapshot.Watcher);
        Assert.AreEqual(1, snapshot.WatcherErrorCode);
        Assert.AreEqual("RadioNotAvailable", snapshot.WatcherErrorName);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "RadioNotAvailable"));
    }

    [TestMethod]
    public void SuspendStopsTheSourceAndResumeStartsIt()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        service.Suspend();
        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher);
        Assert.AreEqual(1, _source.StopCalls);

        service.Resume();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
        Assert.AreEqual(2, _source.StartCalls);
    }

    // A late Stopped(Success) from the run Suspend's own Stop() just ended, arriving after Resume has
    // already started a new one, must not show as the current watcher state or schedule a redundant retry -
    // it belongs to a generation this service has already moved on from.
    [TestMethod]
    public void ALateStoppedFromASupersededGenerationIsIgnored()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        service.Suspend();
        service.Resume();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
        int startsBeforeStaleEvent = _source.StartCalls;

        // Generation 1 is the run Suspend's Stop() ended; Resume moved the fake (and the service) on to
        // generation 2, so this is exactly the shape of a late arrival.
        _source.RaiseStopped(new AdvertisementSourceStopped(0, "Success", StepOutcomes.FromHResult("fake-stop", 0), 1));

        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher, "A stale Stopped must not show as the current watcher state.");
        Assert.IsTrue(_log.Has(LogLevel.Info, "superseded"), "A stale Success must be logged at Info, not treated as a real problem.");

        _clock.Advance(WidgetTiming.WatcherRetryLimit);
        Assert.AreEqual(startsBeforeStaleEvent, _source.StartCalls, "A stale Stopped must not schedule a redundant retry.");
    }

    // A Stopped that genuinely belongs to the current generation must still be shown and still retried:
    // the fix above must not swallow a real stop just because a generation number now exists.
    [TestMethod]
    public void ACurrentGenerationStoppedIsStillShownAndRetried()
    {
        using WidgetStatusService service = NewService();
        service.Start();

        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)); // the convenience overload: current generation

        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher);
        Assert.AreEqual(1, service.Current.WatcherErrorCode);
        Assert.AreEqual("RadioNotAvailable", service.Current.WatcherErrorName);

        _clock.Advance(WidgetTiming.WatcherRetryDelay);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher, "A current-generation Stopped must still schedule and run a retry.");
    }

    // Retry gaps, first half: a Start that fails synchronously, with no Stopped event ever coming to carry
    // the reason or trigger OnStopped's own retry, must keep its own error and still get a retry scheduled.
    [TestMethod]
    public void AFailingInitialStartKeepsTheErrorNameAndSchedulesARetry()
    {
        using WidgetStatusService service = NewService();
        _source.StartResult = () => StepOutcomes.FromHResult("fake-start", 5, detail: "ACCESS_DENIED", ok: false);

        service.Start();

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(WidgetWatcherState.Stopped, snapshot.Watcher);
        Assert.AreEqual(5, snapshot.WatcherErrorCode, "A failed start's own error code must be kept, not discarded.");
        Assert.IsFalse(string.IsNullOrEmpty(snapshot.WatcherErrorName), "A failed start's own error name must be kept, not discarded.");

        _source.StartResult = null; // the scheduled retry now succeeds
        _clock.Advance(WidgetTiming.WatcherRetryDelay);

        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher, "A failing first start must still have scheduled a retry.");
    }

    // With Bluetooth off the real watcher's Start throws (0x800710DF) instead of raising Stopped with
    // RadioNotAvailable. The service must show the same state as for that Stopped event, log the raw code,
    // report the set-up as unavailable, and keep retrying on the doubling schedule until Bluetooth is on.
    [TestMethod]
    public void AStartThatFailsBecauseBluetoothIsOffShowsAsRadioNotAvailableAndKeepsRetrying()
    {
        using WidgetStatusService service = NewService();
        _source.StartResult = () => AdvertisementSourceCodes.RadioOff("fake-start");

        service.Start();

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(WidgetWatcherState.Stopped, snapshot.Watcher);
        Assert.AreEqual(1, snapshot.WatcherErrorCode, "The same code a Stopped(RadioNotAvailable) carries.");
        Assert.AreEqual("RadioNotAvailable", snapshot.WatcherErrorName);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "0x800710DF"), "The raw code must be in the log.");

        // The same state as the event path, field for field.
        _source = new FakeAdvertisementSource();
        using WidgetStatusService other = NewService();
        other.Start();
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));
        Assert.AreEqual(other.Current.WatcherErrorCode, snapshot.WatcherErrorCode);
        Assert.AreEqual(other.Current.WatcherErrorName, snapshot.WatcherErrorName);
        Assert.AreEqual(other.Current.Watcher, snapshot.Watcher);
    }

    [TestMethod]
    public void ABluetoothOffStartIsRetriedOnTheDoublingScheduleAndRecoversWhenBluetoothComesOn()
    {
        using WidgetStatusService service = NewService();
        _source.StartResult = () => AdvertisementSourceCodes.RadioOff("fake-start");
        service.Start();
        int starts = _source.StartCalls;

        _clock.Advance(WidgetTiming.WatcherRetryDelay - TimeSpan.FromSeconds(1));
        Assert.AreEqual(starts, _source.StartCalls, "Too early for the first retry.");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(starts + 1, _source.StartCalls, "The first retry must run at the retry delay.");
        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher);
        Assert.AreEqual("RadioNotAvailable", service.Current.WatcherErrorName, "Every retry keeps the radio-off state.");

        _clock.Advance(WidgetTiming.WatcherRetryDelay * 2 - TimeSpan.FromSeconds(1));
        Assert.AreEqual(starts + 1, _source.StartCalls, "The second retry waits twice as long.");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(starts + 2, _source.StartCalls);

        _source.StartResult = null; // Bluetooth is now on
        _clock.Advance(WidgetTiming.WatcherRetryDelay * 4);

        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
        Assert.IsNull(service.Current.WatcherErrorCode, "A successful start clears the radio-off error.");
    }

    // Any other failing code is still shown as itself, not as Bluetooth being off.
    [TestMethod]
    public void AStartThatFailsForAnotherReasonIsNotShownAsBluetoothOff()
    {
        using WidgetStatusService service = NewService();
        _source.StartResult = () => StepOutcomes.FromHResult("fake-start", unchecked((int)0x800710DE), detail: "COMException", ok: false);

        service.Start();

        Assert.AreEqual(unchecked((int)0x800710DE), service.Current.WatcherErrorCode);
        Assert.AreNotEqual("RadioNotAvailable", service.Current.WatcherErrorName);
    }

    // Retry gaps, second half: a retry that was already dequeued from the thread pool when Suspend ran
    // (disposing a timer never stops a callback already in flight) must not start the watcher during
    // suspend. OnRetryDue is called directly to stand in for that already-running callback, since the fake
    // clock's own timers fire synchronously and Suspend's cancel would otherwise simply prevent the call
    // from happening at all, proving nothing about this race.
    [TestMethod]
    public void ARetryAlreadyInFlightWhenSuspendRunsDoesNotStartTheWatcher()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)); // schedules a retry

        service.Suspend(); // wins the race: _suspended is now true and the timer is disposed
        int startsAtSuspend = _source.StartCalls;

        service.OnRetryDue(); // the already-in-flight callback, running anyway

        Assert.AreEqual(startsAtSuspend, _source.StartCalls, "A retry already in flight when Suspend ran must not start the watcher.");
        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher);

        service.Resume();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher, "Resume itself must still start the watcher normally afterwards.");
    }

    // Watcher lifecycle: OnRetryDue's own early check only catches a Suspend that landed before source.Start()
    // was ever called. This proves the later race, where Suspend lands while that Start() call is still in
    // flight (the real WinRT call can block) and only returns Ok after Suspend has already set _suspended and
    // stopped the source once. Without re-checking _suspended after the start returns, the service marks
    // itself Started and leaves the real source running underneath a service that believes it is idle.
    [TestMethod]
    public void ARetryRacingSuspendWhileStartIsInFlightStopsTheSourceOnceItReturns()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        _source.ArmBlockingStart();
        Task retryTask = Task.Run(() => service.OnRetryDue());
        Assert.IsTrue(_source.WaitForStartEntered(TimeSpan.FromSeconds(5)), "Start was not entered in time.");

        service.Suspend(); // wins the race while the retry's own Start() call is still in flight
        int stopsAtSuspend = _source.StopCalls;

        _source.ReleaseStart();
        Assert.IsTrue(retryTask.Wait(TimeSpan.FromSeconds(5)), "The retry did not complete in time.");

        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher,
            "A retry that only started after Suspend ran must not leave the watcher marked Started.");
        Assert.IsTrue(_source.StopCalls > stopsAtSuspend,
            "The source that the race started after Suspend ran must be stopped again, not left running.");
    }

    // Watcher lifecycle, the same race against Close instead of Suspend: Close disposes and nulls _source, but
    // only after its own Stop() call returns, which happens outside the service's lock exactly where the
    // retry's Start() is blocked here. The existing _closed check already stops the service reporting Started,
    // but on its own it never stops the source the race just (re)started, leaving a live source disposed of by
    // nothing.
    [TestMethod]
    public void ARetryRacingCloseWhileStartIsInFlightStopsTheSourceOnceItReturns()
    {
        var service = NewService();
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        _source.ArmBlockingStart();
        Task retryTask = Task.Run(() => service.OnRetryDue());
        Assert.IsTrue(_source.WaitForStartEntered(TimeSpan.FromSeconds(5)), "Start was not entered in time.");

        service.Close(); // wins the race while the retry's own Start() call is still in flight
        int stopsAtClose = _source.StopCalls;

        _source.ReleaseStart();
        Assert.IsTrue(retryTask.Wait(TimeSpan.FromSeconds(5)), "The retry did not complete in time.");

        Assert.IsTrue(_source.StopCalls > stopsAtClose,
            "The source that the race started after Close ran must be stopped again, not left running with nothing tracking it.");
    }

    // Start and Stop must run outside the service's own lock. Proved with a fake whose Stop blocks
    // until a handler that needs the same lock (here, the public Current getter) has actually taken it: on
    // the old code, Suspend calls Stop while still holding the lock, so the blocked Stop and the blocked
    // locked call deadlock each other; on the new code, Stop runs unlocked, so the locked call sails through
    // while Stop is still blocked.
    [TestMethod]
    public void SuspendCallsStopOutsideTheServiceLockSoAConcurrentLockedCallDoesNotDeadlock()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        _source.ArmBlockingStop();
        Task suspendTask = Task.Run(() => service.Suspend());
        Assert.IsTrue(_source.WaitForStopEntered(TimeSpan.FromSeconds(5)), "Stop was not entered in time.");

        // While Stop() is blocked inside the fake, a call that needs the service's own lock must still
        // complete promptly.
        Task<WidgetSnapshot> lockedCall = Task.Run(() => service.Current);
        bool completedInTime = lockedCall.Wait(TimeSpan.FromSeconds(5));
        _source.ReleaseStop();
        Assert.IsTrue(suspendTask.Wait(TimeSpan.FromSeconds(5)), "Suspend itself must complete once Stop is released.");

        Assert.IsTrue(
            completedInTime,
            "A call needing the service's own lock deadlocked while Stop() was blocked: Stop is still being called while the lock is held.");
    }

    // A second Start must not double-subscribe the device monitor, the settings store or the source's own
    // Received event: the last of those would otherwise process every advertisement twice.
    [TestMethod]
    public void StartIsIdempotent()
    {
        using WidgetStatusService service = NewService();

        service.Start();
        service.Start();

        Assert.AreEqual(1, _source.StartCalls, "A second Start must not start a second source.");

        _source.Raise(Owned(batteryB: 0x05));

        Assert.AreEqual(1, service.Current.Counters.AllSections, "A second Start must not double-subscribe Received.");
    }

    [TestMethod]
    public void AStoppedSourceIsStartedAgainAfterTheRetryDelayDoubling()
    {
        // The prior version made the fake "succeed" on the very first retry (State reset to Started
        // unconditionally), so it proved only that one retry happens at 30 s and never exercised the
        // doubling or the cap at all. The fake here keeps failing every attempt, so every step of
        // 30, 60, 120, 240, 480, then the 15-minute cap repeating, is actually driven and checked.
        using WidgetStatusService service = NewService();
        service.Start();

        _source.StartResult = () => StepOutcomes.FromHResult("fake-start", 1, ok: false);
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));
        int expectedStarts = _source.StartCalls;

        TimeSpan[] expectedDelays =
        {
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480),
            WidgetTiming.WatcherRetryLimit, WidgetTiming.WatcherRetryLimit,
        };

        foreach (TimeSpan delay in expectedDelays)
        {
            _clock.Advance(delay - TimeSpan.FromSeconds(1));
            Assert.AreEqual(expectedStarts, _source.StartCalls, "Too early for the next retry (delay " + delay + ").");

            _clock.Advance(TimeSpan.FromSeconds(1));
            expectedStarts++;
            Assert.AreEqual(expectedStarts, _source.StartCalls, "The retry due at " + delay + " did not run.");
            Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher, "Every retry keeps failing, so the watcher must stay Stopped.");
        }
    }

    // An exception from a state read (a COM property on the real watcher) on a real timer thread must never
    // escape and end the process: every timer callback catches, logs with the code, and carries on as though
    // this attempt simply failed.
    [TestMethod]
    public void ARetryTimerCallbackThatThrowsIsCaughtAndLoggedNotLeftToEscape()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));
        _source.StateOverride = () => throw new IOException("radio gone", unchecked((int)0x80070005));

        _clock.Advance(WidgetTiming.WatcherRetryDelay);

        Assert.IsTrue(
            _log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Error && e.Message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)),
            "The exception's own code must be logged.");
    }

    // The retry timer is one-shot, so a callback that throws used to leave nothing armed: the log said it
    // would try again and it never did. Two throwing cycles, then a working one, prove it keeps trying on
    // the doubling schedule and recovers.
    [TestMethod]
    public void AThrowingRetryIsArmedAgainOnTheDoublingScheduleAndRecoversOnceItWorks()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)); // arms the first retry
        int starts = _source.StartCalls;
        _source.StateOverride = () => throw new IOException("radio gone", unchecked((int)0x80070005));

        _clock.Advance(WidgetTiming.WatcherRetryDelay);
        Assert.AreEqual(starts + 1, _source.StartCalls, "First cycle: the retry runs and throws.");
        Assert.AreEqual(1, RetryErrorCount());

        _clock.Advance(WidgetTiming.WatcherRetryDelay * 2 - TimeSpan.FromSeconds(1));
        Assert.AreEqual(starts + 1, _source.StartCalls, "The next attempt waits twice as long.");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(starts + 2, _source.StartCalls, "Second cycle: a throwing callback must have armed it again.");
        Assert.AreEqual(2, RetryErrorCount());

        _source.StateOverride = null;
        _clock.Advance(WidgetTiming.WatcherRetryDelay * 4);

        Assert.AreEqual(starts + 3, _source.StartCalls, "Third cycle: still armed.");
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        _clock.Advance(WidgetTiming.WatcherRetryLimit * 2);
        Assert.AreEqual(starts + 3, _source.StartCalls, "Once the watcher runs, nothing is left armed.");
    }

    // A throw that lands after Suspend must not arm anything: Resume starts the watcher itself.
    [TestMethod]
    public void AThrowingRetryDoesNotArmAgainWhileSuspended()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));
        _source.StateOverride = () => throw new IOException("radio gone", unchecked((int)0x80070005));
        service.Suspend();
        _source.StateOverride = null;
        int starts = _source.StartCalls;

        _clock.Advance(WidgetTiming.WatcherRetryLimit * 2);

        Assert.AreEqual(starts, _source.StartCalls, "Nothing starts the watcher while suspended.");
    }

    private int RetryErrorCount() =>
        _log.Entries.Count(e => e.Level == Earshot.Contracts.LogLevel.Error && e.Message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase));

    [TestMethod]
    public void RefreshTriesOnceAtOnce()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));
        int startsAfterStop = _source.StartCalls;

        service.RefreshAsync().GetAwaiter().GetResult();

        Assert.AreEqual(startsAfterStop + 1, _source.StartCalls);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
    }

    // The prior version filtered _log.Entries to messages containing the literal, capitalised substring
    // "Widget", so a line from any widget-adjacent class that does not happen to spell it that way (or any
    // deliberately injected leak) was invisible to the byte check regardless of what it carried. Every log
    // entry produced during the run is scanned now, with a pattern that also catches dash- and
    // colon-separated byte runs (BitConverter.ToString's own format, and a MAC-style address) and a bare
    // 12-digit address, not only a long contiguous run of hex digits.
    // Upper-case only: every hex byte this codebase ever formats (BitConverter.ToString, an HResult's "X8"/
    // "X2") comes out upper-case, and restricting to that avoids a false positive on an ordinary lower-case
    // English word that happens to spell five hex letters in a row (RadioNotAvailable... Unreadable has
    // "eadab": e, a, d, a, b are all valid hex digits).
    //
    // The hex-only shapes above never covered a decimal dump of a section (a comma-separated run of byte
    // values, 0-255, five or more in a row - the same width as the hex run above, since a raw section is at
    // least 5 bytes here).
    //
    // The last shape is base64 (Convert.ToBase64String), which is what a dump of a section looks like when
    // someone reaches for the shortest text form: eight or more characters from the base64 alphabet, or seven
    // followed by "=" padding (a 5-byte section is seven characters and one "="), with at least one digit, one
    // capital and one lower-case letter. Ordinary log words are letters only, or carry a digit only after an
    // "=" (a counter), so they do not match; a real encoded run of that length has all three almost always.
    private static readonly Regex ForbiddenByteRun = new(
        @"[0-9A-F]{5,}|([0-9A-F]{2}[:\- ]){2,}[0-9A-F]{2}|\b\d{12}\b|(?:\b(?:25[0-5]|2[0-4]\d|1\d\d|\d\d?)\b[,\s]+){4,}\b(?:25[0-5]|2[0-4]\d|1\d\d|\d\d?)\b" +
        @"|(?<![A-Za-z0-9+/])(?=[A-Za-z0-9+/]*[0-9])(?=[A-Za-z0-9+/]*[A-Z])(?=[A-Za-z0-9+/]*[a-z])(?:[A-Za-z0-9+/]{7,}={1,2}|[A-Za-z0-9+/]{8,})",
        RegexOptions.CultureInvariant);

    // The regex missed a run of hex bytes separated by plain spaces (BitConverter.ToString's own separator
    // swapped for a space is a common enough shape to plant deliberately here).
    [TestMethod]
    public void ForbiddenByteRunCatchesSpaceSeparatedHex()
    {
        Assert.IsTrue(ForbiddenByteRun.IsMatch("payload AA BB CC DD read"), "Space-separated hex bytes must be caught.");
    }

    [TestMethod]
    public void ForbiddenByteRunCatchesADecimalDumpOfASection()
    {
        Assert.IsTrue(ForbiddenByteRun.IsMatch("raw bytes 6, 32, 33, 34, 35, 36 read"), "A decimal dump of a section must be caught.");
    }

    // A section logged as base64 (Convert.ToBase64String) is as much a dump of bytes as the hex and decimal
    // shapes above, and none of them matched it. The samples are built at run time from arithmetic, so no
    // literal payload sits in this file.
    [TestMethod]
    [DataRow(5)]
    [DataRow(9)]
    [DataRow(17)]
    [DataRow(24)]
    public void ForbiddenByteRunCatchesABase64DumpOfASection(int length)
    {
        byte[] section = Enumerable.Range(0, length).Select(i => (byte)(31 + (i * 37 % 200))).ToArray();
        string dump = Convert.ToBase64String(section);

        Assert.IsTrue(ForbiddenByteRun.IsMatch("section " + dump + " read"), "A base64 dump of a " + length + "-byte section (" + dump + ") must be caught.");
    }

    // Ordinary log words and the widget's own counters names are runs of letters from the base64 alphabet
    // too; only a run that also has a digit and both cases reads as encoded bytes.
    [TestMethod]
    public void ForbiddenByteRunLeavesOrdinaryWordsAlone()
    {
        Assert.IsFalse(ForbiddenByteRun.IsMatch("Widget counters: watcher=Started modelMismatch=0 colourMismatch=0 budOrderDisagree=0 noPairedModel=0"));
        Assert.IsFalse(ForbiddenByteRun.IsMatch("Widget watcher start: watcher-start ERROR_DEVICE_NOT_AVAILABLE (Bluetooth is off)."));
    }

    [TestMethod]
    public void LogLinesCarryCountsAndNeverBytes()
    {
        using WidgetStatusService service = NewService();
        int baseline = _log.Entries.Count; // excludes Setup's own JsonSettingsStore bootstrap logging
        service.Start();
        Prime();

        _source.Raise(Owned(batteryB: 0x05));
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));
        _clock.Advance(WidgetTiming.CountersLogInterval);

        List<LogEntry> widgetEntries = _log.Entries.Skip(baseline).ToList();
        Assert.IsTrue(widgetEntries.Count > 0, "The widget must have logged something to check.");
        foreach (LogEntry entry in widgetEntries)
        {
            Assert.IsFalse(ForbiddenByteRun.IsMatch(entry.Message), "A log line carried something that looks like raw bytes: " + entry.Message);
        }

        // Once a minute while anything changed, the counters line itself, counts only.
        Assert.IsTrue(
            widgetEntries.Any(e => e.Message.StartsWith("Widget counters:", StringComparison.Ordinal) && e.Message.Contains("ok=6", StringComparison.Ordinal)),
            "The once-a-minute counters line was not logged.");
    }

    // LogLinesCarryCountsAndNeverBytes above only ever raises a recognised Proximity message and a Stopped
    // event: the unknown-form path (RecordUnknownForm, reached when ProximityParser itself returns
    // UnknownForm) never ran under any byte check at all. WidgetFixtures.UnknownSeventeenByteForm is proved
    // elsewhere (ProximityParserTests) to produce exactly that status; this raises it through the real
    // service and checks every resulting log line, the "Widget counters:" summary (which reports the
    // unknown form's prefix and length, per RecordUnknownForm's own design) included.
    [TestMethod]
    public void LogLinesNeverCarryBytesOnTheUnknownFormPath()
    {
        using WidgetStatusService service = NewService();
        int baseline = _log.Entries.Count;
        service.Start();
        Prime();

        byte[] payload = WidgetFixtures.UnknownSeventeenByteForm();
        _source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, payload, Rssi: -60, _clock.GetUtcNow(), SenderTag: 1));
        _clock.Advance(WidgetTiming.CountersLogInterval);

        // The exact text a base64 dump of this very payload (whole, or without its first byte) would be.
        string asBase64 = Convert.ToBase64String(payload);
        string withoutPrefix = Convert.ToBase64String(payload.AsSpan(1));

        List<LogEntry> widgetEntries = _log.Entries.Skip(baseline).ToList();
        Assert.IsTrue(widgetEntries.Count > 0, "The widget must have logged something to check.");
        foreach (LogEntry entry in widgetEntries)
        {
            Assert.IsFalse(ForbiddenByteRun.IsMatch(entry.Message), "A log line on the unknown-form path carried something that looks like raw bytes: " + entry.Message);
            Assert.IsFalse(entry.Message.Contains(asBase64, StringComparison.Ordinal), "A log line carried the payload as base64: " + entry.Message);
            Assert.IsFalse(entry.Message.Contains(withoutPrefix, StringComparison.Ordinal), "A log line carried the payload's body as base64: " + entry.Message);
        }
    }

    [TestMethod]
    public void WithTheSettingOffNoSourceIsConstructed()
    {
        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        int factoryCalls = 0;
        var service = new WidgetStatusService(
            () => { factoryCalls++; return _source; }, _settings, _deviceMonitor, () => null, _log,
            action => action(), _clock, _paired);

        service.Start();

        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(WidgetWatcherState.Off, service.Current.Watcher);
        service.Dispose();
    }

    [TestMethod]
    public void TurningTheSettingOnStartsOne()
    {
        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        int factoryCalls = 0;
        var service = new WidgetStatusService(
            () => { factoryCalls++; return _source; }, _settings, _deviceMonitor, () => null, _log,
            action => action(), _clock, _paired);
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Off, service.Current.Watcher);

        _settings.Update(s => s.Widget = s.Widget with { Enabled = true });

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
        service.Dispose();
    }

    // Generations: the setting going off then on builds a fresh source (WithTheSettingOffNoSourceIsConstructed
    // and TurningTheSettingOnStartsOne above prove that construction). The service's own generation count
    // never resets, but each source only ever knows about its own Start() calls, so a fresh instance starts
    // its idea of "its generation" over from whatever it begins at. A genuine Stopped from that fresh, current
    // source must still be shown and retried, not dropped for looking older than the service's own count.
    [TestMethod]
    public void ASettingsOffThenOnGenuineStoppedFromTheFreshSourceIsNotDroppedAsStale()
    {
        var sources = new List<FakeAdvertisementSource>();
        using var service = new WidgetStatusService(
            () => { var built = new FakeAdvertisementSource(); sources.Add(built); return built; },
            _settings, _deviceMonitor, () => null, _log,
            action => { Interlocked.Increment(ref _posts); action(); }, _clock, _paired);

        service.Start();
        Assert.AreEqual(1, sources.Count);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        _settings.Update(s => s.Widget = s.Widget with { Enabled = true });

        Assert.AreEqual(2, sources.Count, "The setting going off then on must build a fresh source, not reuse the old one.");
        FakeAdvertisementSource fresh = sources[1];
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        // A genuine Stopped from the fresh, current source, tagged with its own idea of its generation.
        fresh.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));

        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher,
            "A genuine Stopped from the current, freshly built source must not be dropped as a stale generation.");
    }

    // ---- Battery set-up: completion, what is proved, and what is never shown
}
