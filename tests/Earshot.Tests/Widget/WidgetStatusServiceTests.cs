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
    private List<OwnedReadingEventArgs> _ownedReadingEvents = null!;
    private ClaimStore? _claimStore;

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
        _ownedReadingEvents = new List<OwnedReadingEventArgs>();
    }

    // Save now queues its disk write on a background thread. Wait for anything still pending before the
    // temp folder is torn down, or its own claim.json.tmp can still be open when Directory.Delete runs.
    public void Dispose()
    {
        _claimStore?.IdleAsync().GetAwaiter().GetResult();
        _temp.Dispose();
    }

    // SampleClaim below always carries threshold -70; the injectable overload matches it so Save's own
    // reload does not invalidate the claim these tests just wrote (a stored threshold is usable only
    // when it equals the current one, and WidgetDefaults.SignalThresholdDbm ships null).
    private ClaimStore NewClaimStore()
    {
        _claimStore = new ClaimStore(_temp.File("claim.json"), _log, static () => (sbyte)-70);
        return _claimStore;
    }

    // claimThreshold defaults to matching SampleClaim's fixed -70 dBm, so a test can drive ClaimAsync to an
    // actual Claimed outcome without WidgetDefaults.SignalThresholdDbm ever holding anything but null.
    private WidgetStatusService NewService(ClaimStore store, ProximityDecodeTable? table = null, sbyte? claimThreshold = -70)
    {
        var service = new WidgetStatusService(
            () => _source, store, _settings, _deviceMonitor, () => null, _log,
            action => { Interlocked.Increment(ref _posts); action(); }, _clock,
            () => table ?? ProximityDecodeTable.Unproved, () => claimThreshold);
        service.Changed += (sender, e) => { lock (_changedEvents) { _changedEvents.Add(e); } };
        service.CaseOpened += (sender, e) => { lock (_caseOpenedEvents) { _caseOpenedEvents.Add(e); } };
        service.OwnedReadingApplied += (sender, e) => { lock (_ownedReadingEvents) { _ownedReadingEvents.Add(e); } };
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

    // The production constructor must read ProximityDecodeTable.Current itself, not accept an arbitrary
    // table, so nothing composing this service can wire up anything but phase 0's own proved shape (which
    // ships Unproved, so only the case nibble is ever decoded).
    [TestMethod]
    public void TheProductionConstructorReadsProximityDecodeTableCurrentItself()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        var service = new WidgetStatusService(
            () => _source, store, _settings, _deviceMonitor, () => null, _log, action => action(), _clock);
        service.Start();

        _source.Raise(Owned(batteryA: 0x37, batteryB: 0x05)); // bud nibbles known were the order proved

        Assert.IsNull(service.Current.Left.Percent, "ProximityDecodeTable.Current ships Unproved: bud nibbles must not decode.");
        Assert.IsNull(service.Current.Right.Percent);
        Assert.AreEqual(50, service.Current.Case.Percent, "The case nibble needs no table and must still decode.");
        service.Dispose();
    }

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
        Assert.AreEqual(0, snapshot.Counters.AllSections);
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

    // M6, reviewer probe P2: per-bud InEar must go null once the reading is stale, on a table where the bud
    // order is proved (so this is not just about the Elsewhere/NotInUse rollup: the raw per-bud field must
    // clear too), while the battery percent it arrived with is unaffected, since battery never expires.
    [TestMethod]
    public void AStaleReadingClearsPerBudInEarButKeepsTheBatteryPercent()
    {
        var table = ProximityDecodeTable.Unproved with
        {
            HighNibbleIsRight = true,
            LeftInEarBit = 0,
            RightInEarBit = 1,
            InEarWhenSet = true,
        };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

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
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.Raise(Owned(batteryA: 0x0B)); // low nibble 0xB (11): out of range, not the documented 0xF

        Assert.IsTrue(_log.Has(LogLevel.Warn, "form may have drifted"));
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

    // Counters tick on almost every advertisement, so comparing the whole snapshot (WidgetCounters included)
    // to decide whether to raise Changed would fire it constantly even when nothing the UI shows moved.
    [TestMethod]
    public void CounterChangesAloneDoNotRaiseChanged()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.Raise(Owned(batteryB: 0x05));
        int changedAfterFirst = _changedEvents.Count;
        Assert.IsTrue(changedAfterFirst > 0, "The first reading must raise Changed.");

        _source.Raise(Owned(batteryB: 0x05)); // identical reading: counters tick, nothing UI-visible moves

        Assert.AreEqual(changedAfterFirst, _changedEvents.Count, "A counter-only change must not raise Changed again.");
    }

    // M7, reviewer probe P4: forgetting a claim must not leave the old battery or ear state around to be
    // shown, or compared for consistency, against whatever the owner claims next.
    [TestMethod]
    public void ForgetClaimClearsEveryReadingAndEarState()
    {
        var table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true, LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();
        _source.Raise(Owned(status: 0b0000_0011, batteryA: 0x35, batteryB: 0x05)); // both buds in ear, battery known
        Assert.AreEqual(50, service.Current.Case.Percent);
        Assert.IsNotNull(service.Current.Left.InEar);

        service.ForgetClaim();

        WidgetSnapshot snapshot = service.Current;
        Assert.IsNull(snapshot.Left.Percent, "ForgetClaim must not keep the old battery.");
        Assert.IsNull(snapshot.Right.Percent);
        Assert.IsNull(snapshot.Case.Percent);
        Assert.IsNull(snapshot.Left.InEar, "ForgetClaim must not keep the old ear state.");
        Assert.IsNull(snapshot.Right.InEar);
        Assert.IsNull(snapshot.EarReadAt);
        Assert.IsNull(snapshot.BatteryReadAt);
    }

    // M7, reviewer probe P4: a redone claim (a different set of AirPods, as far as the widget knows) must
    // not keep the previous claim's battery or ear state either.
    [TestMethod]
    public async Task ARedoneClaimClearsTheOldBatteryAndEarState()
    {
        var table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true, LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();
        _source.Raise(Owned(status: 0b0000_0011, batteryA: 0x35, batteryB: 0x05));
        Assert.AreEqual(50, service.Current.Case.Percent);
        Assert.IsNotNull(service.Current.Left.InEar);

        Task<ClaimOutcome> claimTask = service.ClaimAsync(CancellationToken.None);
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x02), -60, _clock.GetUtcNow(), SenderTag: 999));
        _clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await claimTask;
        Assert.AreEqual(ClaimOutcomeStatus.Claimed, outcome.Status);

        WidgetSnapshot snapshot = service.Current;
        Assert.IsNull(snapshot.Left.Percent, "A redone claim must not keep the old battery.");
        Assert.IsNull(snapshot.Right.Percent);
        Assert.IsNull(snapshot.Case.Percent);
        Assert.IsNull(snapshot.Left.InEar, "A redone claim must not keep the old ear state.");
        Assert.IsNull(snapshot.Right.InEar);
        Assert.IsNull(snapshot.EarReadAt);
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

    // Owner decision, 2026-09-27 ("same checks always"): a live connection to this PC used to waive battery
    // consistency for one candidate. It confirms nothing now; the owner's own reading is accepted only
    // because it is consistent with the claim, exactly as it would be off this PC.
    [TestMethod]
    public void TheOwnersOwnConsistentReadingWhileConnectedIsStillAccepted()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        using WidgetStatusService service = NewService(store);
        PinContainer();
        service.Start();
        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        var reading = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x03), -60, _clock.GetUtcNow(), SenderTag: 1);
        _source.Raise(reading);

        Assert.AreEqual(30, service.Current.Case.Percent);
        Assert.AreEqual(1, service.Current.Counters.Owned);
        Assert.AreEqual(3, store.Current!.Last.Case);
    }

    // Security reviewer's first ordering: a lone stranger (same model and colour as the claim, but a
    // battery jump the claim's last reading does not allow) arrives while this PC is connected. Nothing is
    // shown and claim.json is untouched, exactly as it would be with no connection at all.
    [TestMethod]
    public void ALoneStrangerWhileConnectedShowsNothingAndLeavesTheClaimUnchanged()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        using WidgetStatusService service = NewService(store);
        PinContainer();
        service.Start();
        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        var stranger = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x09), -60, _clock.GetUtcNow(), SenderTag: 7);
        _source.Raise(stranger);

        Assert.IsNull(service.Current.Case.Percent, "Nothing is shown for a same-model sender whose battery does not match the claim.");
        Assert.AreEqual(1, service.Current.Counters.BatteryInconsistent);
        Assert.AreEqual(0, service.Current.Counters.Owned);
        Assert.AreEqual(2, store.Current!.Last.Case, "claim.json must be unchanged.");
    }

    // Security reviewer's second ordering: the same stranger arrives first, then the owner's own consistent
    // reading follows straight after. The stranger changes nothing; the owner is still accepted.
    [TestMethod]
    public void AStrangerArrivingBeforeTheOwnerIsRejectedThenTheOwnerIsAccepted()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        using WidgetStatusService service = NewService(store);
        PinContainer();
        service.Start();
        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        var stranger = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x09), -60, _clock.GetUtcNow(), SenderTag: 7);
        _source.Raise(stranger);
        Assert.AreEqual(1, service.Current.Counters.BatteryInconsistent);

        var owner = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x03), -60, _clock.GetUtcNow(), SenderTag: 1);
        _source.Raise(owner);

        Assert.AreEqual(30, service.Current.Case.Percent, "The owner's own reading is accepted straight after a rejected stranger.");
        Assert.AreEqual(1, service.Current.Counters.Owned);
        Assert.AreEqual(3, store.Current!.Last.Case);
    }

    // Security reviewer's third ordering: the owner's reading is accepted first, then, long after any window
    // the old live-candidate concept ever had, a stranger arrives. There is no window left to matter: the
    // stranger is rejected on the ordinary consistency check alone and the claim keeps the owner's value.
    [TestMethod]
    public void AStrangerArrivingAfterTheOwnersWindowLapsesIsStillRejected()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        using WidgetStatusService service = NewService(store);
        PinContainer();
        service.Start();
        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        var owner = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x03), -60, _clock.GetUtcNow(), SenderTag: 1);
        _source.Raise(owner);
        Assert.AreEqual(1, service.Current.Counters.Owned);

        _clock.Advance(TimeSpan.FromMinutes(5));

        var stranger = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x09), -60, _clock.GetUtcNow(), SenderTag: 7);
        _source.Raise(stranger);

        Assert.AreEqual(1, service.Current.Counters.BatteryInconsistent);
        Assert.AreEqual(3, store.Current!.Last.Case, "The stranger, however much time has passed, never overwrites the owner's claim.");
    }

    // Every owned advertisement carries a new read time, so "save only when Last changes" (a record with an
    // AtUtc field) never holds: comparing the whole Last record would write on every single advert. Only the
    // battery values (the nibbles) actually decide whether the disk needs touching; the read time can move
    // in memory for free.
    [TestMethod]
    public async Task TwentyIdenticalAdvertsWriteTheClaimToDiskOnce()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        await store.IdleAsync();
        int baseline = store.DiskWriteCount;
        using WidgetStatusService service = NewService(store);
        service.Start();

        for (int i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var reading = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x03), -60, _clock.GetUtcNow(), SenderTag: 1);
            _source.Raise(reading);
        }

        await store.IdleAsync();

        Assert.AreEqual(30, service.Current.Case.Percent);
        Assert.AreEqual(1, store.DiskWriteCount - baseline, "20 adverts carrying the same battery value must write to disk once, not twenty times.");
    }

    // Same 20 identical adverts, but the file cannot be written at all: one save is attempted (and logged),
    // not twenty, because the service never even asks the store to save the 19 that changed nothing.
    [TestMethod]
    public async Task WithAReadOnlyClaimFileTwentyIdenticalAdvertsLogOneWarnNotTwenty()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        await store.IdleAsync();
        string path = _temp.File("claim.json");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        using WidgetStatusService service = NewService(store);

        try
        {
            service.Start();

            for (int i = 0; i < 20; i++)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                var reading = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x03), -60, _clock.GetUtcNow(), SenderTag: 1);
                _source.Raise(reading);
            }

            await store.IdleAsync();

            Assert.AreEqual(1, _log.Entries.Count(e => e.Level == Earshot.Contracts.LogLevel.Warn && e.Message.Contains("claim", StringComparison.OrdinalIgnoreCase)),
                "One failed-save warning, not twenty.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // The seam LowBatteryAlertService feeds from: raised for an Owned verdict, carrying the exact decoded
    // reading ApplyDecodedReadingLocked was given.
    [TestMethod]
    public void OwnedReadingAppliedIsRaisedForAnOwnedAdvertisement()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);

        service.Start();
        _source.Raise(Owned(batteryB: 0x05)); // case nibble 5 -> 50%, needs no proved table

        Assert.AreEqual(1, _ownedReadingEvents.Count);
        Assert.AreEqual(50, _ownedReadingEvents[0].Reading.Case.Percent);
    }

    // The owner's rule: a live connection to this PC confirms nothing. A battery jump the consistency check
    // refuses is refused while connected too, so the alert and auto-pause never see it.
    [TestMethod]
    public void OwnedReadingAppliedIsNotRaisedForAnInconsistentJumpEvenWhileConnected()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim(new OwnedBattery(2, 2, 2, _clock.GetUtcNow())));
        using WidgetStatusService service = NewService(store);
        PinContainer();
        service.Start();
        _deviceMonitor.Raise(ThisPcSnapshot(active: true));

        var reading = new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x09), -60, _clock.GetUtcNow(), SenderTag: 1);
        _source.Raise(reading);

        Assert.AreEqual(1, service.Current.Counters.BatteryInconsistent, "Sanity: this reading must be refused as inconsistent.");
        Assert.AreEqual(0, _ownedReadingEvents.Count, "A live connection must not let an inconsistent reading through.");
    }

    // NoClaim, ModelOrColourMismatch, SignalBelowThreshold, BatteryUnreadable and BatteryInconsistent all
    // return before ApplyDecodedReadingLocked runs: none of them may raise this.
    [TestMethod]
    public void OwnedReadingAppliedIsNeverRaisedForANonOwnedVerdict()
    {
        var store = NewClaimStore(); // never Save()d: every advertisement reads NoClaim
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.Raise(Owned(batteryB: 0x05));

        Assert.AreEqual(0, _ownedReadingEvents.Count, "NoClaim must not raise OwnedReadingApplied.");

        store.Save(SampleClaim());
        var stranger = new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(colour: WidgetFixtures.StrangerColour, batteryB: 0x09), -60, _clock.GetUtcNow(), SenderTag: 2);
        _source.Raise(stranger);

        Assert.AreEqual(0, _ownedReadingEvents.Count, "ModelOrColourMismatch must not raise OwnedReadingApplied.");
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

    // The lid-counter path (unlike the lid-bit path, where the assumed-false baseline making a true first
    // reading a rising edge is deliberate) has no real "previous" value on the very first owned reading: it
    // must only record a baseline, not treat "nothing to compare yet" as a change.
    [TestMethod]
    public void CaseOpenedIsNotRaisedByTheFirstReadingWithTheLidCounterBaseline()
    {
        var table = ProximityDecodeTable.Unproved with { LidCounterMask = 0xFF };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store, table);
        service.Start();

        _source.Raise(Owned(lid: 0x03)); // the very first owned reading: establishes the baseline only

        Assert.AreEqual(0, _caseOpenedEvents.Count, "The first reading must only establish the lid counter baseline.");

        _source.Raise(Owned(lid: 0x04)); // a genuine change from the baseline

        Assert.AreEqual(1, _caseOpenedEvents.Count);
    }

    // Round 2: a boolean "are we currently inside some uiPost action" cannot tell a genuine post apart from
    // code that simply runs synchronously nested inside an already-posted action, so it passed even when the
    // reviewer raised both events directly. This fake queues every posted action instead of running it
    // immediately, and records how many previously queued actions had FULLY finished (been dequeued and
    // returned) by the moment each event fires. A raise made through its own, separate uiPost call only runs
    // after the action that led to it has completed, so it fires with a completed-count of at least one; a
    // direct raise nested inside that same still-running action fires while the count is still what it was
    // before that action started.
    [TestMethod]
    public void ChangedAndCaseOpenedAreRaisedThroughUiPost()
    {
        var table = ProximityDecodeTable.Unproved with { LidOpenBit = 0 };
        var store = NewClaimStore();
        store.Save(SampleClaim());
        var queue = new Queue<Action>();
        int itemsCompleted = 0;
        var changedCompletedCountAtFire = new List<int>();
        var caseOpenedCompletedCountAtFire = new List<int>();

        var service = new WidgetStatusService(
            () => _source, store, _settings, _deviceMonitor, () => null, _log,
            action =>
            {
                Interlocked.Increment(ref _posts);
                queue.Enqueue(action);
            },
            _clock, () => table);
        service.Changed += (sender, e) => changedCompletedCountAtFire.Add(itemsCompleted);
        service.CaseOpened += (sender, e) => caseOpenedCompletedCountAtFire.Add(itemsCompleted);
        service.Start();
        int postsBefore = _posts;

        _source.Raise(Owned(lid: 0x01));

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
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();
        _source.StopResult = () => StepOutcomes.FromHResult("fake-stop", 5, detail: "ACCESS_DENIED", ok: false);

        service.Suspend();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "ACCESS_DENIED"), "A failing stop step must be logged at Warn with its detail.");
    }

    // StopSourceLocked's Stop() call (reached through Close) discarded the step outcome the same way.
    [TestMethod]
    public void ClosingWithAFailingStopStepLogsItAtWarn()
    {
        var store = NewClaimStore();
        var service = NewService(store);
        service.Start();
        _source.StopResult = () => StepOutcomes.FromHResult("fake-stop", 5, detail: "ACCESS_DENIED", ok: false);

        service.Close();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "ACCESS_DENIED"), "A failing stop step must be logged at Warn with its detail.");
    }

    // A failing start step must be logged at Warn too, not always at Info as if it had succeeded.
    [TestMethod]
    public void AFailingStartStepIsLoggedAtWarn()
    {
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        _source.StartResult = () => StepOutcomes.FromHResult("fake-start", 5, detail: "ACCESS_DENIED", ok: false);

        service.Start();

        Assert.IsTrue(_log.Has(LogLevel.Warn, "ACCESS_DENIED"), "A failing start step must be logged at Warn with its detail.");
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

    // A second Start must not double-subscribe the device monitor, the settings store or the source's own
    // Received event: the last of those would otherwise process every advertisement twice.
    [TestMethod]
    public void StartIsIdempotent()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);

        service.Start();
        service.Start();

        Assert.AreEqual(1, _source.StartCalls, "A second Start must not start a second source.");

        _source.Raise(Owned(batteryB: 0x05));

        Assert.AreEqual(1, service.Current.Counters.AllSections, "A second Start must not double-subscribe Received.");
    }

    // Close is final: a claim flow already under way when Close runs must not be applied to the service, or
    // raise Changed, once it completes afterwards.
    [TestMethod]
    public async Task AClaimCompletingAfterCloseDoesNotApplyOrRaiseChanged()
    {
        var store = NewClaimStore();
        var service = NewService(store);
        service.Start();

        Task<ClaimOutcome> claimTask = service.ClaimAsync(CancellationToken.None);
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), -60, _clock.GetUtcNow(), SenderTag: 1));

        service.Close(); // closes while the claim window is still open
        int changedBeforeCompletion = _changedEvents.Count;

        _clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await claimTask;

        Assert.AreEqual(ClaimOutcomeStatus.Claimed, outcome.Status, "The claim flow itself still completes and still writes claim.json.");
        Assert.IsFalse(service.Current.ClaimExists, "A claim completing after Close must not be applied to the closed service.");
        Assert.AreEqual(changedBeforeCompletion, _changedEvents.Count, "Nothing after Close raises Changed.");
    }

    [TestMethod]
    public void AStoppedSourceIsStartedAgainAfterTheRetryDelayDoubling()
    {
        // The prior version made the fake "succeed" on the very first retry (State reset to Started
        // unconditionally), so it proved only that one retry happens at 30 s and never exercised the
        // doubling or the cap at all. The fake here keeps failing every attempt, so every step of
        // 30, 60, 120, 240, 480, then the 15-minute cap repeating, is actually driven and checked.
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();

        _source.StartResult = () => StepOutcomes.FromHResult("fake-start", 1, ok: false);
        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));
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
        var store = NewClaimStore();
        using WidgetStatusService service = NewService(store);
        service.Start();
        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));
        _source.StateOverride = () => throw new IOException("radio gone", unchecked((int)0x80070005));

        _clock.Advance(WidgetTiming.WatcherRetryDelay);

        Assert.IsTrue(
            _log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Error && e.Message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)),
            "The exception's own code must be logged.");
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
    private static readonly Regex ForbiddenByteRun = new(
        @"[0-9A-F]{5,}|([0-9A-F]{2}[:\- ]){2,}[0-9A-F]{2}|\b\d{12}\b",
        RegexOptions.CultureInvariant);

    // Round 2: the regex missed a run of hex bytes separated by plain spaces (BitConverter.ToString's own
    // separator swapped for a space is a common enough shape to plant deliberately here).
    [TestMethod]
    public void ForbiddenByteRunCatchesSpaceSeparatedHex()
    {
        Assert.IsTrue(ForbiddenByteRun.IsMatch("payload AA BB CC DD read"), "Space-separated hex bytes must be caught.");
    }

    [TestMethod]
    public void LogLinesCarryCountsAndNeverBytes()
    {
        var store = NewClaimStore();
        store.Save(SampleClaim());
        using WidgetStatusService service = NewService(store);
        int baseline = _log.Entries.Count; // excludes Setup's own JsonSettingsStore bootstrap logging
        service.Start();

        _source.Raise(Owned(batteryB: 0x05));
        _source.RaiseStopped(new AdvertisementSourceStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1)));
        _clock.Advance(WidgetTiming.CountersLogInterval);

        List<LogEntry> widgetEntries = _log.Entries.Skip(baseline).ToList();
        Assert.IsTrue(widgetEntries.Count > 0, "The widget must have logged something to check.");
        foreach (LogEntry entry in widgetEntries)
        {
            Assert.IsFalse(ForbiddenByteRun.IsMatch(entry.Message), "A log line carried something that looks like raw bytes: " + entry.Message);
        }

        // Once a minute while anything changed, the counters line itself, counts only.
        Assert.IsTrue(
            widgetEntries.Any(e => e.Message.StartsWith("Widget counters:", StringComparison.Ordinal) && e.Message.Contains("ok=1", StringComparison.Ordinal)),
            "The once-a-minute counters line was not logged.");
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
