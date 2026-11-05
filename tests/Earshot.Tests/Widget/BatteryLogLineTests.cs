using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The battery log lines that tell, from a day's log alone, what was shown and why: no address, name or ID in any of them.
[TestClass]
public sealed class BatteryLogLineTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();
    private const uint LeftBud = 1;
    private const uint RightBud = 2;

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeDeviceMonitor _monitor = null!;
    private TestTimeProvider _clock = null!;
    private FakeAdvertisementSource _source = null!;
    private FakePairedModelSource _paired = null!;
    private FakeHandsFreeBatterySource _handsFree = null!;

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
        _handsFree = new FakeHandsFreeBatterySource();
    }

    public void Dispose() => _temp.Dispose();

    private WidgetStatusService NewService() =>
        new(() => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, _handsFree,
            ProximityDecodeTable.Documented, runInBackground: work => work());

    private void ConnectToThisPc()
    {
        _settings.Update(s =>
        {
            s.PinnedContainerId = Container;
            s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
        });
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, EndpointState.Active, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", ConnectionState.Connected, new[] { endpoint });
        _monitor.Raise(new DeviceSnapshot(model, new[] { model }, _clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        });
    }

    private void Tick(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    // One message of the pair from one bud: pairHigh and pairLow are the bud nibbles, the case nibble the case level. The sample is
    // stamped lateByMs before the clock now, as a radio timestamp runs behind.
    private void Send(bool left, byte caseNibble, int pairHigh = 0x7, int pairLow = 0x4, int lateByMs = 0, sbyte rssi = -55) =>
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(
                batteryA: BroadcastFixtures.Bud(left, caseNibble: caseNibble, pairHigh: pairHigh, pairLow: pairLow).BatteryA,
                batteryB: caseNibble,
                status: BroadcastFixtures.Bud(left, caseNibble: caseNibble, pairHigh: pairHigh, pairLow: pairLow).Status),
            rssi, _clock.GetUtcNow() - TimeSpan.FromMilliseconds(lateByMs), left ? LeftBud : RightBud));

    // The case opened next to the PC: the burst that links the set, case at 90%.
    private WidgetStatusService LinkedAndOnThisPc()
    {
        WidgetStatusService service = NewService();
        service.Start();
        ConnectToThisPc();
        for (int i = 0; i <= 12; i++)
        {
            Send(left: i % 2 == 0, caseNibble: 0x9);
            Tick(0.25);
        }

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection, "Sanity: the pair is linked.");
        return service;
    }

    private int Count(string pattern) => _log.Entries.Count(e => Regex.IsMatch(e.Message, pattern));

    [TestMethod]
    public void AFirstValueOfAPartLogsFromNoneAndAChangeLogsTheOldAndNewWithItsAge()
    {
        using WidgetStatusService service = LinkedAndOnThisPc();

        Assert.AreEqual(1, Count(@"^Battery: case none to 90, live, read \d+ ms ago\.$"));
        Assert.AreEqual(2, Count(@"^Battery: (left|right) none to (70|40), live"));

        Send(left: true, caseNibble: 0x6, lateByMs: 400);
        Assert.AreEqual(0, Count(@"^Battery: case 90 to 60"), "One message is not a change.");

        Send(left: false, caseNibble: 0x6, lateByMs: 400);
        Assert.AreEqual(1, Count(@"^Battery: case 90 to 60, live, read 400 ms ago\.$"));
        Send(left: true, caseNibble: 0x6, lateByMs: 400);
        Assert.AreEqual(1, Count(@"^Battery: case 90 to 60"), "A repeat of the same value is no change.");
    }

    [TestMethod]
    public void TheGaugeLogsItsNumberAndWhichBudAndKindOnlyWhenOneOfThemChanges()
    {
        using WidgetStatusService service = LinkedAndOnThisPc();
        string lower = service.Current.Left.Percent == 40 ? "left" : "right";

        for (int i = 0; i < 6; i++)
        {
            Send(left: i % 2 == 0, caseNibble: 0x9);
            Tick(0.25);
        }

        Assert.AreEqual(1, Count(@"^Battery: gauge 40 from " + lower + @", live\.$"), "Said once, not at every message.");
        LiveTests.LiveTest25Lines.AssertLogged(_log, LiveTests.LiveTest25Lines.GaugeFigure);

        Tick(45);
        _clock.Advance(WidgetTiming.HeadsetPollInterval);

        Assert.AreEqual(1, Count(@"^Battery: gauge 40 from " + lower + @", last\.$"), "The kind changed, so it is said again.");
    }

    [TestMethod]
    public void ACaseThatMovesMoreThanOneStepInsideAMinuteWarnsOnceAndIsStillShown()
    {
        using WidgetStatusService service = LinkedAndOnThisPc();

        Tick(41);
        Send(left: true, caseNibble: 0x6);
        Send(left: false, caseNibble: 0x6);

        Assert.IsTrue(_log.Has(LogLevel.Warn, "Battery: case moved 30 points in 41250 ms, more than one step inside a minute."), "The warning names the move and the time.");
        Assert.AreEqual(1, _log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("case moved", StringComparison.Ordinal)));
        Assert.AreEqual(60, service.Current.Case.Percent, "It does not hold the reading back.");
    }

    [TestMethod]
    public void AStepOfTenOrAMoveAfterAMinuteDoesNotWarn()
    {
        using WidgetStatusService service = LinkedAndOnThisPc();

        Tick(20);
        Send(left: true, caseNibble: 0x8);
        Send(left: false, caseNibble: 0x8);
        Tick(90);
        Send(left: true, caseNibble: 0x3);
        Send(left: false, caseNibble: 0x3);

        Assert.AreEqual(30, service.Current.Case.Percent);
        Assert.AreEqual(0, _log.Entries.Count(e => e.Message.Contains("moved", StringComparison.Ordinal) && e.Message.StartsWith("Battery:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WornUnlinkedCountsCaseUnknownMessagesOfThePairedModelThatAreNotLinked()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ConnectToThisPc();

        for (int i = 0; i < 5; i++)
        {
            Send(left: i % 2 == 0, caseNibble: 0xF);
            Tick(1);
        }

        Assert.AreEqual(5, service.Current.Counters.WornUnlinked);
        Assert.AreEqual(0, service.Current.Counters.Chosen);

        Tick(70);
        Assert.IsTrue(_log.Entries.Any(e => e.Message.StartsWith("Widget counters:", StringComparison.Ordinal) && e.Message.Contains(" wornUnlinked=5 ", StringComparison.Ordinal)),
            "The counters line carries it beside chosen.");
    }

    [TestMethod]
    public void ABurstThatDidNotLinkIsLoggedWithItsReasonCountSpanAndMedianSignal()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        ConnectToThisPc();

        Send(left: true, caseNibble: 0x9, rssi: -64);
        Tick(0.45);
        Send(left: false, caseNibble: 0x9, rssi: -60);
        Tick(0.45);
        Send(left: true, caseNibble: 0x9, rssi: -68);
        Tick(7);
        Send(left: false, caseNibble: 0xF);

        Assert.IsTrue(
            _log.Has(LogLevel.Info, "Widget: a case-known burst did not link (too few): 3 messages in 900 ms, median -64 dBm."),
            string.Join(" | ", _log.Entries.Select(e => e.Message).Where(m => m.Contains("burst", StringComparison.Ordinal))));
        Assert.AreEqual(1, service.Current.Counters.LinkMissTooFew, "The counter stays.");
    }

    [TestMethod]
    public void NoBatteryLogLineCarriesAnAddressOrAName()
    {
        using WidgetStatusService service = LinkedAndOnThisPc();
        Send(left: true, caseNibble: 0x6);
        Send(left: false, caseNibble: 0x6);
        Tick(45);
        _clock.Advance(WidgetTiming.HeadsetPollInterval);

        foreach (LogEntry entry in _log.Entries.Where(e => e.Message.StartsWith("Battery:", StringComparison.Ordinal)))
        {
            StringAssert.DoesNotMatch(entry.Message, new Regex("[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{12}|AirPods"), entry.Message);
        }
    }
}
