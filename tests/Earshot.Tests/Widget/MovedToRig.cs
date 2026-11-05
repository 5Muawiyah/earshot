using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A WidgetStatusService on a fake clock, a fake source and a fake device monitor, fed messages of one invented pair: the moved-to
// rule through the real selection code with the time under the test's hand. Synthetic messages only: WidgetFixtures' "no product"
// model and colour, invented sender tags.
internal sealed class MovedToRig : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly DateTimeOffset _start;

    public MovedToRig()
    {
        Log = new CapturingLog();
        Clock = new TestTimeProvider();
        Source = new FakeAdvertisementSource();
        Monitor = new FakeDeviceMonitor(Clock);
        Settings = new JsonSettingsStore(_temp.File("settings.json"), Log);
        Settings.Update(s => s.PinnedContainerId = Container);
        Service = new WidgetStatusService(
            () => Source, Settings, Monitor, () => null, Log, action => action(), Clock, new FakePairedModelSource(),
            new FakeHandsFreeBatterySource(), ProximityDecodeTable.Documented, runInBackground: work => work());
        Service.MovedTo += (_, e) => Moved.Add(e);
        _start = Clock.GetUtcNow();
        Service.Start();
    }

    public Guid Container { get; } = Guid.NewGuid();

    public CapturingLog Log { get; }

    public TestTimeProvider Clock { get; }

    public FakeAdvertisementSource Source { get; }

    public FakeDeviceMonitor Monitor { get; }

    public JsonSettingsStore Settings { get; }

    public WidgetStatusService Service { get; }

    public List<MovedToEventArgs> Moved { get; } = new();

    public double Now => (Clock.GetUtcNow() - _start).TotalSeconds;

    public AirPodsWhere Where => Service.Current.Where;

    public void AdvanceTo(double seconds)
    {
        TimeSpan wait = TimeSpan.FromSeconds(seconds) - (Clock.GetUtcNow() - _start);
        if (wait > TimeSpan.Zero)
        {
            Clock.Advance(wait);
        }
    }

    // One message of the invented pair at the given time, as the watcher would hand it over.
    public void Hear(
        double seconds, sbyte rssi = -62, bool caseKnown = false, byte modelLow = WidgetFixtures.ModelLow, byte colour = WidgetFixtures.Colour,
        uint tag = 1)
    {
        AdvanceTo(seconds);
        byte batteryB = caseKnown ? (byte)0x05 : (byte)0x0F;
        Source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(modelLow: modelLow, batteryA: 0xFF, batteryB: batteryB, colour: colour), rssi, Clock.GetUtcNow(), tag));
    }

    // The burst that links the set: five case-known messages half a second apart.
    public void Link()
    {
        for (int i = 0; i < 5; i++)
        {
            Hear(i * 0.5, caseKnown: true, rssi: -60);
        }
    }

    // The AirPods leave this PC without Earshot having asked.
    public void LeaveByOther(bool playing = true) => Service.NoteLeftByOther(Clock.GetUtcNow(), playing);

    public void ThisPc(bool active)
    {
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, active ? EndpointState.Active : EndpointState.Unplugged, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", active ? ConnectionState.Connected : ConnectionState.Disconnected, [endpoint]);
        Monitor.Raise(new DeviceSnapshot(model, [model], Clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        });
    }

    public bool LogHas(string fragment) => Log.Has(LogLevel.Info, fragment);

    // The leave, then the pair heard near for four seconds: the shape that is moved.
    public void LeaveAndBeHeardNear(bool playing = true)
    {
        LeaveByOther(playing);
        double t = Now;
        Hear(t + 0.9);
        Hear(t + 2.4);
        Hear(t + 3.8);
    }

    public void Dispose()
    {
        Service.Dispose();
        _temp.Dispose();
    }
}
