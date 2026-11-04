using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// PartConfirm through the real status service with a fake watcher: what is shown and what is saved is the confirmed reading,
// not the last message.
[TestClass]
public sealed class PartConfirmWiringTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();
    private const int Buds = 0xA;
    private const uint LeftBud = 1;
    private const uint RightBud = 2;

    private TempFolder _temp = null!;
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
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _clock = new TestTimeProvider();
        _monitor = new FakeDeviceMonitor(_clock);
        _source = new FakeAdvertisementSource();
        _paired = new FakePairedModelSource();
    }

    public void Dispose() => _temp.Dispose();

    private WidgetStatusService NewService()
    {
        var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null,
            ProximityDecodeTable.Documented, runInBackground: work => work());
        service.RefindEnabled = true;
        return service;
    }

    private void Connect()
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

    private void Send(uint tag, ProximityMessage m, sbyte rssi = -55) =>
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(m.ModelHigh, m.ModelLow, m.Status, m.BatteryA, m.BatteryB, m.Lid, m.Colour, m.Reserved),
            rssi, _clock.GetUtcNow(), tag));

    // A message of the owner's pair: buds at 100%, the case at the given nibble.
    private void CaseMessage(bool left, byte caseNibble)
    {
        Send(left ? LeftBud : RightBud, BroadcastFixtures.Bud(left, caseNibble: caseNibble, pairHigh: Buds, pairLow: Buds));
        Tick(0.25);
    }

    private WidgetStatusService LinkedService()
    {
        WidgetStatusService service = NewService();
        service.Start();
        Connect();
        for (int i = 0; i <= 12; i++)
        {
            CaseMessage(left: i % 2 == 0, caseNibble: 0x9);
        }

        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection, "Sanity: the pair is linked.");
        Assert.AreEqual(90, service.Current.Case.Percent, "Sanity: the case reads 90.");
        return service;
    }

    [TestMethod]
    public void OneStrayCaseValueIsNeitherShownNorSaved()
    {
        using WidgetStatusService service = LinkedService();

        CaseMessage(left: true, caseNibble: 0x4);

        Assert.AreEqual(90, service.Current.Case.Percent, "A single message of 40 changes nothing.");
        Assert.AreEqual(90, service.Current.LastReadings.Case!.Percent, "Nor does it reach the saved last reading.");

        CaseMessage(left: false, caseNibble: 0x9);
        CaseMessage(left: true, caseNibble: 0x9);
        Assert.AreEqual(90, service.Current.Case.Percent);
    }

    [TestMethod]
    public void TwoMessagesInARowOfANewCaseValueMoveIt()
    {
        using WidgetStatusService service = LinkedService();

        CaseMessage(left: true, caseNibble: 0x4);
        CaseMessage(left: false, caseNibble: 0x4);

        Assert.AreEqual(40, service.Current.Case.Percent);
        Assert.AreEqual(40, service.Current.LastReadings.Case!.Percent, "What is saved is what is shown.");
    }

    [TestMethod]
    public void TwoSendersAlternatingHoldTheLastConfirmedValueAndTheRunLogsItOnce()
    {
        using WidgetStatusService service = LinkedService();

        for (int i = 0; i < 20; i++)
        {
            CaseMessage(left: true, caseNibble: 0x4);
            CaseMessage(left: false, caseNibble: 0x9);
        }

        Assert.AreEqual(90, service.Current.Case.Percent);
        Assert.AreEqual(1, _log.Entries.Count(l => l.Message.Contains("last confirmed value is held", StringComparison.Ordinal)), "Logged once for the run.");
    }
}
