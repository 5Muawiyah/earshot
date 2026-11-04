using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Windows' own figure is latched per connected stretch: once a bud has been live since the AirPods came to this PC, it is not
// shown again until they leave, so a source can switch once, Windows to a bud, and never back.
[TestClass]
public sealed class WindowsFigureTests : IDisposable
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
    }

    public void Dispose() => _temp.Dispose();

    private WidgetStatusService NewService() =>
        new(() => _source, _settings, _deviceMonitor, () => null, _log, action => action(), _clock, _paired, _handsFree,
            ProximityDecodeTable.Documented, runInBackground: work => work());

    private void SetThisPc(bool active)
    {
        _settings.Update(s =>
        {
            s.PinnedContainerId = Container;
            s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
        });
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, active ? EndpointState.Active : EndpointState.Unplugged, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", active ? ConnectionState.Connected : ConnectionState.Disconnected, new[] { endpoint });
        _deviceMonitor.Raise(new DeviceSnapshot(model, new[] { model }, _clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        });
    }

    private void Message(byte batteryA, byte batteryB) =>
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(batteryA: batteryA, batteryB: batteryB), -60, _clock.GetUtcNow(), 1));

    // The case opened next to the PC: the burst that links the set.
    private void LinkTheSet()
    {
        for (int i = 0; i < 5; i++)
        {
            Message(0xFF, 0x05);
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }
    }

    private void Tick(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    private ShownBattery Shown(WidgetStatusService service) => BatteryFreshness.Shown(service.Current, _clock.GetUtcNow());

    [TestMethod]
    public void OnceABudHasBeenLiveWindowsFigureDoesNotComeBackWhenTheBudGoesStale()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        SetThisPc(active: true);
        Assert.AreEqual(70, Shown(service).WindowsPercent, "No bud yet: Windows' figure is what is shown.");
        Assert.AreEqual(BatterySource.Windows, Shown(service).Gauge?.Source);

        LinkTheSet();
        Message(0x56, 0x0A);
        Assert.IsNull(Shown(service).WindowsPercent, "A bud is live: its reading replaces Windows' figure.");
        Assert.AreEqual(BatterySource.Broadcast, Shown(service).Gauge?.Source);

        Tick(45);
        _handsFree.Percent = 68;
        _clock.Advance(WidgetTiming.HeadsetPollInterval - TimeSpan.FromSeconds(45));

        ShownBattery shown = Shown(service);
        Assert.IsNull(shown.WindowsPercent, "The bud has gone past the live window, and Windows' figure does not come back.");
        Assert.AreEqual(BatterySource.Broadcast, shown.Gauge?.Source);
        Assert.AreEqual(ReadingKind.Last, shown.Gauge?.Kind, "The gauge keeps the bud's last reading.");
        Assert.AreEqual(50, shown.Gauge?.Percent, "The lower of the two buds the message gave.");
    }

    [TestMethod]
    public void ABudThatIsFreshWhenTheAirPodsConnectCountsAsLiveInThisStretch()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        LinkTheSet();
        Message(0x56, 0x0A);

        SetThisPc(active: true);
        Tick(45);
        _handsFree.Percent = 68;
        _clock.Advance(WidgetTiming.HeadsetPollInterval - TimeSpan.FromSeconds(45));

        Assert.IsNull(Shown(service).WindowsPercent, "The bud was live as they connected, so Windows' figure is not shown in this stretch.");
    }

    [TestMethod]
    public void LeavingAndComingBackStartsTheLatchAgain()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        SetThisPc(active: true);
        LinkTheSet();
        Message(0x56, 0x0A);
        Tick(60);
        Assert.IsNull(Shown(service).WindowsPercent, "Latched for this stretch.");

        SetThisPc(active: false);
        Tick(60);
        _handsFree.Percent = 66;
        SetThisPc(active: true);

        Assert.AreEqual(66, Shown(service).WindowsPercent, "A new stretch with no bud live shows Windows' figure again.");
    }

    [TestMethod]
    public void NoBudEverLiveLeavesWindowsFigureAsItWas()
    {
        _handsFree.Percent = 70;
        using WidgetStatusService service = NewService();
        service.Start();
        SetThisPc(active: true);

        Tick(100);
        _handsFree.Percent = 64;
        _clock.Advance(WidgetTiming.HeadsetPollInterval);

        Assert.AreEqual(64, Shown(service).WindowsPercent);
    }
}
