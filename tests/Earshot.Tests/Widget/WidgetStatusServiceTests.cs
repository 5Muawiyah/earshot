using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Infra;
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
    private int _posts;
    private List<EventArgs> _changedEvents = null!;
    private List<CaseOpenedEventArgs> _caseOpenedEvents = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _clock = new TestTimeProvider();
        _deviceMonitor = new FakeDeviceMonitor(_clock);
        _source = new FakeAdvertisementSource();
        _posts = 0;
        _changedEvents = new List<EventArgs>();
        _caseOpenedEvents = new List<CaseOpenedEventArgs>();
    }

    public void Dispose() => _temp.Dispose();

    private ClaimStore NewClaimStore() => new(_temp.File("claim.json"), _log);

    private WidgetStatusService NewService(ClaimStore store, ProximityDecodeTable? table = null)
    {
        var service = new WidgetStatusService(
            () => _source, store, _settings, _deviceMonitor, () => null, _log,
            action => { Interlocked.Increment(ref _posts); action(); }, _clock,
            () => table ?? ProximityDecodeTable.Unproved);
        service.Changed += (sender, e) => { lock (_changedEvents) { _changedEvents.Add(e); } };
        service.CaseOpened += (sender, e) => { lock (_caseOpenedEvents) { _caseOpenedEvents.Add(e); } };
        return service;
    }

    private static WidgetClaim SampleClaim(OwnedBattery? last = null) => new(
        1, WidgetFixtures.ModelHigh, WidgetFixtures.ModelLow, WidgetFixtures.Colour, -70,
        new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
        last ?? new OwnedBattery(null, null, null, new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)));

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

    private AdvertisementSample Owned(byte batteryA = 0x00, byte batteryB = 0x00, byte status = 0x00, byte lid = 0x00, sbyte rssi = -60) =>
        new(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(status: status, batteryA: batteryA, batteryB: batteryB, lid: lid), rssi, _clock.GetUtcNow(), SenderTag: 1);

    [TestMethod]
    public void BeforeAnyReadingEverythingIsUnknown()
    {
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();

        WidgetSnapshot snapshot = service.Current;

        Assert.AreEqual(AirPodsWhere.Unknown, snapshot.Where);
        Assert.IsNull(snapshot.Left.Percent);
        Assert.IsNull(snapshot.Right.Percent);
        Assert.IsNull(snapshot.Case.Percent);
        Assert.IsNull(snapshot.BatteryReadAt);
        Assert.IsNull(snapshot.EarReadAt);
        Assert.IsNull(snapshot.LidOpen);
        Assert.AreEqual(WidgetWatcherState.Started, snapshot.Watcher);
        Assert.IsFalse(snapshot.ClaimExists);
        Assert.IsFalse(snapshot.AutoPauseAvailable);
        Assert.AreEqual(0, snapshot.Counters.AllAdvertisements);
    }

    [TestMethod]
    public void ThisPcComesFromCoreAudioAlone()
    {
        PinContainer();
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();

        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        Assert.AreEqual(AirPodsWhere.ThisPc, service.Current.Where);

        _deviceMonitor.Raise(ThisPcSnapshot(active: false));

        Assert.AreNotEqual(AirPodsWhere.ThisPc, service.Current.Where);
    }

    [TestMethod]
    public void AnOwnedFreshReadingWithABudInEarWhileNotOnThisPcIsElsewhere()
    {
        var table = ProximityDecodeTable.Unproved with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        _source.Raise(Owned(status: 0b0000_0001)); // left in ear

        Assert.AreEqual(AirPodsWhere.Elsewhere, service.Current.Where);
    }

    [TestMethod]
    public void AnOwnedFreshReadingWithNoBudInEarIsNotInUse()
    {
        var table = ProximityDecodeTable.Unproved with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        _source.Raise(Owned(status: 0b0000_0000)); // neither bud in ear

        Assert.AreEqual(AirPodsWhere.NotInUse, service.Current.Where);
    }

    [TestMethod]
    public void WithTheInEarBitsUnprovedWhereIsUnknownOffThisPc()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store); // Unproved table: no in-ear bits

        service.Start();
        _source.Raise(Owned());

        Assert.AreEqual(AirPodsWhere.Unknown, service.Current.Where);
    }

    [TestMethod]
    public void AStaleReadingLeavesWhereUnknownAndEarStateNull()
    {
        var table = ProximityDecodeTable.Unproved with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        _source.Raise(Owned(status: 0b0000_0001));
        Assert.AreEqual(AirPodsWhere.Elsewhere, service.Current.Where);

        _clock.Advance(WidgetTiming.EarFreshWindow + TimeSpan.FromSeconds(1));

        Assert.AreEqual(AirPodsWhere.Unknown, service.Current.Where);
        Assert.IsNull(service.Current.EarReadAt);
    }

    [TestMethod]
    public void BatteryKeepsItsReadTimeAndNeverExpires()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        service.Start();

        DateTimeOffset at = _clock.GetUtcNow();
        _source.Raise(Owned(batteryB: 0x05));
        Assert.AreEqual(50, service.Current.Case.Percent);
        Assert.AreEqual(at, service.Current.Case.ReadAt);

        _clock.Advance(TimeSpan.FromDays(1));

        Assert.AreEqual(50, service.Current.Case.Percent);
        Assert.AreEqual(at, service.Current.Case.ReadAt);
    }

    [TestMethod]
    public void BatteryReadAtIsTheOldestKnownPart()
    {
        var table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        DateTimeOffset first = _clock.GetUtcNow();
        _source.Raise(Owned(batteryA: 0x00, batteryB: 0x05)); // case known now, buds known now too (nibble 0)
        _clock.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset second = _clock.GetUtcNow();
        _source.Raise(Owned(batteryA: 0xFF, batteryB: 0x06)); // buds unknown this time, case updates

        Assert.AreEqual(60, service.Current.Case.Percent);
        Assert.AreEqual(second, service.Current.Case.ReadAt);
        Assert.AreEqual(first, service.Current.Left.ReadAt);
        Assert.AreEqual(first, service.Current.BatteryReadAt, "The oldest known ReadAt among the parts, so it lines up with the buds, not the case.");
    }

    [TestMethod]
    public void AnUnknownPercentLeavesThePartAsItWas()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.Raise(Owned(batteryB: 0x05));
        Assert.AreEqual(50, service.Current.Case.Percent);

        _source.Raise(Owned(batteryB: 0x0F)); // case now unknown (0xF)

        Assert.AreEqual(50, service.Current.Case.Percent, "An unknown percent must not overwrite the last known one.");
    }

    [TestMethod]
    public void AStrangerChangesNothingButTheCount()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        service.Start();

        var stranger = new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(colour: WidgetFixtures.StrangerColour, batteryB: 0x09), -60, _clock.GetUtcNow(), SenderTag: 2);
        _source.Raise(stranger);

        WidgetSnapshot snapshot = service.Current;
        Assert.IsNull(snapshot.Case.Percent);
        Assert.AreEqual(AirPodsWhere.Unknown, snapshot.Where);
        Assert.AreEqual(1, snapshot.Counters.ModelOrColourMismatch);
        Assert.AreEqual(0, snapshot.Counters.Owned);
    }

    [TestMethod]
    public void CaseOpenedIsRaisedOnlyForAnOwnedAdvertisement()
    {
        var table = ProximityDecodeTable.Unproved with { LidOpenBit = 0 };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        var stranger = new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(colour: WidgetFixtures.StrangerColour, lid: 0x01), -60, _clock.GetUtcNow(), SenderTag: 2);
        _source.Raise(stranger);
        Assert.AreEqual(0, _caseOpenedEvents.Count);

        _source.Raise(Owned(lid: 0x01)); // lid bit rising edge, owned sender
        Assert.AreEqual(1, _caseOpenedEvents.Count);
    }

    [TestMethod]
    public void CaseOpenedIsRaisedOncePerEdgeOrCounterChange()
    {
        var table = ProximityDecodeTable.Unproved with { LidOpenBit = 0 };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        _source.Raise(Owned(lid: 0x01));
        _source.Raise(Owned(lid: 0x01)); // still open: not a new edge
        _source.Raise(Owned(lid: 0x00)); // closes
        _source.Raise(Owned(lid: 0x01)); // opens again: a second edge

        Assert.AreEqual(2, _caseOpenedEvents.Count);
    }

    [TestMethod]
    public void ChangedAndCaseOpenedAreRaisedThroughUiPost()
    {
        var table = ProximityDecodeTable.Unproved with { LidOpenBit = 0 };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();
        int postsBefore = _posts;

        _source.Raise(Owned(lid: 0x01));

        Assert.IsTrue(_posts > postsBefore);
        Assert.IsTrue(_changedEvents.Count > 0);
        Assert.AreEqual(1, _caseOpenedEvents.Count);
    }

    [TestMethod]
    public void TheStoppedEventIsLoggedWithItsErrorAndShownInTheSnapshot()
    {
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));

        WidgetSnapshot snapshot = service.Current;
        Assert.AreEqual(WidgetWatcherState.Stopped, snapshot.Watcher);
        Assert.AreEqual(1, snapshot.WatcherErrorCode);
        Assert.AreEqual("RadioNotAvailable", snapshot.WatcherErrorName);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "RadioNotAvailable"));
    }

    [TestMethod]
    public void SuspendStopsTheSourceAndResumeStartsIt()
    {
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);

        service.Suspend();
        Assert.AreEqual(WidgetWatcherState.Stopped, service.Current.Watcher);
        Assert.AreEqual(1, _source.StopCalls);

        service.Resume();
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
        Assert.AreEqual(2, _source.StartCalls);
    }

    [TestMethod]
    public void AStoppedSourceIsStartedAgainAfterTheRetryDelayDoubling()
    {
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.State = AdvertisementSourceState.Aborted; // the next Start() will still "succeed" per the fake
        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));
        int startsAfterStop = _source.StartCalls;

        _clock.Advance(WidgetTiming.WatcherRetryDelay - TimeSpan.FromSeconds(1));
        Assert.AreEqual(startsAfterStop, _source.StartCalls, "Too early: the first retry must not have run yet.");

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(startsAfterStop + 1, _source.StartCalls);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
    }

    [TestMethod]
    public void RefreshTriesOnceAtOnce()
    {
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();
        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));
        int startsAfterStop = _source.StartCalls;

        service.RefreshAsync().GetAwaiter().GetResult();

        Assert.AreEqual(startsAfterStop + 1, _source.StartCalls);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
    }

    [TestMethod]
    public void LogLinesCarryCountsAndNeverBytes()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.Raise(Owned(batteryB: 0x05));
        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));

        var longHexRun = new Regex("[0-9a-fA-F]{5,}", RegexOptions.CultureInvariant);
        List<LogEntry> widgetEntries = _log.Entries.Where(e => e.Message.Contains("Widget", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(widgetEntries.Count > 0, "The widget must have logged something to check.");
        foreach (LogEntry entry in widgetEntries)
        {
            Assert.IsFalse(longHexRun.IsMatch(entry.Message), "A log line carried something that looks like raw bytes: " + entry.Message);
        }
    }

    [TestMethod]
    public void WithTheSettingOffNoSourceIsConstructed()
    {
        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        var store = NewClaimStore();
        int factoryCalls = 0;
        var service = new WidgetStatusService(
            () => { factoryCalls++; return _source; }, store, _settings, _deviceMonitor, () => null, _log,
            action => action(), _clock, () => ProximityDecodeTable.Unproved);

        service.Start();

        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(WidgetWatcherState.Off, service.Current.Watcher);
        service.Dispose();
    }

    [TestMethod]
    public void TurningTheSettingOnStartsOne()
    {
        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        var store = NewClaimStore();
        int factoryCalls = 0;
        var service = new WidgetStatusService(
            () => { factoryCalls++; return _source; }, store, _settings, _deviceMonitor, () => null, _log,
            action => action(), _clock, () => ProximityDecodeTable.Unproved);
        service.Start();
        Assert.AreEqual(WidgetWatcherState.Off, service.Current.Watcher);

        _settings.Update(s => s.Widget = s.Widget with { Enabled = true });

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(WidgetWatcherState.Started, service.Current.Watcher);
        service.Dispose();
    }
}
