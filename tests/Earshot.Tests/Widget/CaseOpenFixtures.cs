using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// Synthetic traffic for the case-open rule. Nothing here is read from a device: the model and colour are WidgetFixtures' own
// "no product" bytes, the senders are invented tags, and the encrypted tail is WidgetFixtures' fixed filler, never a capture.
// The timings are the shapes the saved records show (scratch evidence table), written as numbers.
internal static class CaseOpenFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public const uint SenderA = 1;
    public const uint SenderB = 2;
    public const uint Stranger = 9;

    public static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    // One message of the linked set at a time, as the tracker is fed it.
    public static CaseOpenStep Feed(
        CaseOpenTracker tracker, uint sender, double seconds, bool caseKnown = true, byte lid = 0x52, sbyte rssi = -60, bool linkMade = false) =>
        tracker.Observe(sender, caseKnown, lid, rssi, At(seconds), linkMade);

    // The 30 September capture, as offsets from its first message: both senders went through four lid values inside one
    // unbroken run, never sending a case-unknown message in between. The first value a sender sent is the one before its
    // first change. Sender A changed at 2.1, 2.6 and 23.3 s; sender B at 2.1, 2.3 and 24.6 s.
    public static byte LidByteAt(uint sender, double offset)
    {
        const double second = 2.1;
        double third = sender == SenderA ? 2.6 : 2.3;
        double fourth = sender == SenderA ? 23.3 : 24.6;
        if (offset < second)
        {
            return 0x51;
        }

        if (offset < third)
        {
            return 0x59;
        }

        return offset < fourth ? (byte)0x52 : (byte)0x5A;
    }

    // The capture's run: 110 messages in 26.1 s, two senders taking turns, the lid byte as above.
    public static IEnumerable<(uint Sender, double Seconds, byte Lid)> CaptureRun(double startSeconds)
    {
        const int messages = 110;
        const double length = 26.1;
        for (int i = 0; i < messages; i++)
        {
            double offset = length * i / (messages - 1);
            uint sender = i % 2 == 0 ? SenderA : SenderB;
            yield return (sender, startSeconds + offset, LidByteAt(sender, offset));
        }
    }
}

// A WidgetStatusService on a fake clock and a fake source, fed messages of one invented set: what the case-open rule does
// through the real selection code, with the time under the test's hand.
internal sealed class CaseOpenHarness : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly TestTimeProvider _clock = new();
    private readonly FakeAdvertisementSource _source = new();
    private readonly DateTimeOffset _start;

    public CaseOpenHarness()
    {
        Log = new CapturingLog();
        var settings = new JsonSettingsStore(_temp.File("settings.json"), Log);
        Service = new WidgetStatusService(
            () => _source, settings, new FakeDeviceMonitor(_clock), () => null, Log,
            action => action(), _clock, new FakePairedModelSource(), new FakeHandsFreeBatterySource(), ProximityDecodeTable.Documented,
            runInBackground: work => work());
        Service.CaseOpened += (sender, e) => { lock (Opened) { Opened.Add(e); } };
        Service.CaseClosed += (sender, e) => { lock (Closed) { Closed.Add(e); } };
        _start = _clock.GetUtcNow();
        Service.Start();
    }

    public CapturingLog Log { get; }

    public WidgetStatusService Service { get; }

    public List<CaseOpenedEventArgs> Opened { get; } = new();

    public List<CaseClosedEventArgs> Closed { get; } = new();

    // Seconds since the harness started.
    public double Now => (_clock.GetUtcNow() - _start).TotalSeconds;

    public void AdvanceTo(double seconds)
    {
        TimeSpan wait = TimeSpan.FromSeconds(seconds) - (_clock.GetUtcNow() - _start);
        if (wait > TimeSpan.Zero)
        {
            _clock.Advance(wait);
        }
    }

    public void Advance(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    // One message at the given time. caseKnown false sends the case level as unknown (buds in use).
    public void SendAt(double seconds, uint tag = CaseOpenFixtures.SenderA, byte lid = 0x52, bool caseKnown = true, sbyte rssi = -60)
    {
        AdvanceTo(seconds);
        byte batteryB = caseKnown ? (byte)0x05 : (byte)0x0F;
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(batteryA: 0xFF, batteryB: batteryB, lid: lid),
            rssi, _clock.GetUtcNow(), tag));
    }

    // A message of a different set: other bud levels, weaker, so it can never take the link.
    public void SendStrangerAt(double seconds)
    {
        AdvanceTo(seconds);
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(batteryA: 0x37, batteryB: 0x02),
            -85, _clock.GetUtcNow(), CaseOpenFixtures.Stranger));
    }

    // Five case-known messages half a second apart from time zero: the burst that links the set, so the fifth message, at
    // two seconds, is the link.
    public void Link()
    {
        for (int i = 0; i < 5; i++)
        {
            SendAt(i * 0.5);
        }
    }

    // One message every half second, from the time given up to and including the last one given.
    public void Steady(double fromSeconds, double toSeconds)
    {
        for (double t = fromSeconds; t < toSeconds - 1e-9; t += 0.5)
        {
            SendAt(t);
        }

        SendAt(toSeconds);
    }

    public bool LogHas(string fragment) => Log.Has(LogLevel.Info, fragment);

    public void Dispose()
    {
        Service.Dispose();
        _temp.Dispose();
    }
}
