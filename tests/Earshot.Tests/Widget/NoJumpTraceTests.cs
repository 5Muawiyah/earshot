using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// An hour of synthetic messages through the real status service with a fake clock, with the things that made figures jump put in
// on purpose: a stray value among the right ones, two senders that disagree, a minute of silence, a bud that goes unheard while
// the other is, a new case open. What each surface shows is sampled every half second and held to the definition of a jump:
//   1. a shown value changes only when a reading with that value was heard at that moment (or, for the gauge, when it is a bud's or
//      Windows' figure that is shown);
//   2. no value goes A, B, A within a minute;
//   3. nothing moves more than one step of the broadcast (10 points) between two samples a minute apart, from one source, when
//      the input steps by one at a time.
// The surfaces are the card's three parts and the gauge's number; the tooltip and the speech are worked out from the same
// ShownBattery, so they cannot move when these do not.
[TestClass]
public sealed class NoJumpTraceTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();
    private const uint LeftBud = 1;
    private const uint RightBud = 2;
    private const int HourSeconds = 3600;
    private const double Tick = 0.5;
    private const double SendEvery = 1.7;

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
        _handsFree = new FakeHandsFreeBatterySource { Percent = 70 };
    }

    public void Dispose() => _temp.Dispose();

    private readonly record struct Sample(double At, int? Left, int? Right, int? Case, int? Gauge, BatterySource? Source, int? Windows);

    // The true bud levels in tenths: one step down every ten minutes.
    private static (int High, int Low) TrueBuds(double seconds)
    {
        int steps = Math.Min(9, (int)(seconds / 600));
        return (10 - steps, 9 - steps);
    }

    private void Send(uint tag, int high, int low, byte caseNibble)
    {
        bool first = tag == LeftBud;
        ProximityMessage m = BroadcastFixtures.Bud(first, caseNibble: caseNibble, pairHigh: high, pairLow: low);
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(batteryA: m.BatteryA, batteryB: m.BatteryB, status: m.Status),
            -55, _clock.GetUtcNow(), tag));
    }

    private List<Sample> RunTheHour(out WidgetStatusService service)
    {
        service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, _handsFree,
            ProximityDecodeTable.Documented, runInBackground: work => work());
        service.Start();
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

        var samples = new List<Sample>();
        DateTimeOffset start = _clock.GetUtcNow();
        double nextLeft = 0;
        double nextRight = SendEvery / 2;
        double nextStray = 97;
        bool linked = false;

        for (double t = 0; t < HourSeconds; t += Tick)
        {
            (int high, int low) = TrueBuds(t);
            byte caseNibble = 0xF;
            bool caseOpen = t < 3 || (t >= 1800 && t < 1803);
            if (caseOpen)
            {
                caseNibble = 0x9;
            }

            bool silent = t >= 2400 && t < 2460;
            bool oneBudUnheard = t >= 3000 && t < 3120;
            bool disagree = t >= 1500 && t < 1620;

            if (!silent)
            {
                double gap = caseOpen ? 0.25 : SendEvery;
                if (t >= nextLeft)
                {
                    Send(LeftBud, oneBudUnheard ? 0xF : high, low, caseNibble);
                    nextLeft += gap;
                }

                if (t >= nextRight)
                {
                    int h = high;
                    int l = low;
                    if (disagree)
                    {
                        h = high - 1;
                    }

                    if (t >= nextStray)
                    {
                        h = 2;
                        l = 2;
                        nextStray += 97;
                    }

                    Send(RightBud, oneBudUnheard ? 0xF : h, l, caseNibble);
                    nextRight += gap;
                }
            }
            else
            {
                nextLeft = t + Tick;
                nextRight = t + Tick;
            }

            _clock.Advance(TimeSpan.FromSeconds(Tick));
            ShownBattery shown = BatteryFreshness.Shown(service.Current, _clock.GetUtcNow());
            samples.Add(new Sample(
                (_clock.GetUtcNow() - start).TotalSeconds, shown.Left.Percent, shown.Right.Percent, shown.Case.Percent,
                shown.Gauge?.Percent, shown.Gauge?.Source, shown.WindowsPercent));
            linked |= service.Current.Selection == BroadcastSelectionState.Linked;
        }

        Assert.IsTrue(linked, "Sanity: the pair was linked.");
        return samples;
    }

    private static List<(double At, int Value)> Changes(IEnumerable<Sample> samples, Func<Sample, int?> surface)
    {
        var changes = new List<(double, int)>();
        int? previous = null;
        foreach (Sample s in samples)
        {
            int? value = surface(s);
            if (value is int v && v != previous)
            {
                changes.Add((s.At, v));
            }

            previous = value ?? previous;
        }

        return changes;
    }

    private static readonly (string Name, Func<Sample, int?> Surface)[] Surfaces =
    [
        ("left", s => s.Left),
        ("right", s => s.Right),
        ("case", s => s.Case),
        ("gauge", s => s.Gauge),
    ];

    [TestMethod]
    public void NoSurfaceGoesAThenBThenAWithinAMinute()
    {
        List<Sample> samples = RunTheHour(out WidgetStatusService service);
        using (service)
        {
            foreach ((string name, Func<Sample, int?> surface) in Surfaces)
            {
                List<(double At, int Value)> changes = Changes(samples, surface);
                Assert.IsGreaterThanOrEqualTo(name == "case" ? 1 : 2, changes.Count, name + " showed a figure, and a bud or the gauge stepped down at least once.");
                for (int i = 0; i + 2 < changes.Count; i++)
                {
                    bool aba = changes[i].Value == changes[i + 2].Value && changes[i].Value != changes[i + 1].Value;
                    Assert.IsFalse(aba && changes[i + 2].At - changes[i].At <= 60,
                        name + " went " + changes[i].Value + ", " + changes[i + 1].Value + ", " + changes[i + 2].Value + " within " +
                        (changes[i + 2].At - changes[i].At) + " s, from " + changes[i].At + " s.");
                }
            }
        }
    }

    [TestMethod]
    public void NoSurfaceMovesMoreThanOneStepBetweenSamplesAMinuteApart()
    {
        List<Sample> samples = RunTheHour(out WidgetStatusService service);
        using (service)
        {
            int perMinute = (int)(60 / Tick);
            foreach ((string name, Func<Sample, int?> surface) in Surfaces)
            {
                for (int i = 0; i + perMinute < samples.Count; i++)
                {
                    int? a = surface(samples[i]);
                    int? b = surface(samples[i + perMinute]);
                    bool sameSource = samples[i].Source == samples[i + perMinute].Source;
                    if (a is int from && b is int to && sameSource)
                    {
                        Assert.IsLessThanOrEqualTo(10, Math.Abs(to - from),
                            name + " went from " + from + " at " + samples[i].At + " s to " + to + " a minute later.");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void AChangeInAPartIsAReadingThatWasHeardAtThatMomentAndTheGaugesNumberIsABudsOrWindows()
    {
        List<Sample> samples = RunTheHour(out WidgetStatusService service);
        using (service)
        {
            foreach ((string name, Func<Sample, int?> surface) in new[] { Surfaces[0], Surfaces[1] })
            {
                foreach ((double at, int value) in Changes(samples, surface))
                {
                    (int high, int low) = TrueBuds(at);
                    bool wasHeard = value == high * 10 || value == low * 10 || value == (high - 1) * 10 || value == 20;
                    Assert.IsTrue(wasHeard, name + " showed " + value + " at " + at + " s, which no message of the set said then.");
                    Assert.AreNotEqual(20, value, name + " showed the stray at " + at + " s.");
                }
            }

            foreach (Sample s in samples.Where(s => s.Gauge is not null))
            {
                bool abud = s.Gauge == s.Left || s.Gauge == s.Right || s.Gauge == s.Windows;
                Assert.IsTrue(abud, "The gauge showed " + s.Gauge + " at " + s.At + " s, which no bud or Windows figure was showing.");
            }
        }
    }

    [TestMethod]
    public void TheGaugeGoesFromWindowsToABudAtMostOnceAndNeverBack()
    {
        List<Sample> samples = RunTheHour(out WidgetStatusService service);
        using (service)
        {
            int toBroadcast = 0;
            int backToWindows = 0;
            BatterySource? previous = null;
            foreach (Sample s in samples.Where(s => s.Source is not null))
            {
                if (previous == BatterySource.Windows && s.Source == BatterySource.Broadcast)
                {
                    toBroadcast++;
                }

                if (previous == BatterySource.Broadcast && s.Source == BatterySource.Windows)
                {
                    backToWindows++;
                }

                previous = s.Source;
            }

            Assert.IsLessThanOrEqualTo(1, toBroadcast);
            Assert.AreEqual(0, backToWindows, "Windows' figure did not come back.");
        }
    }

    [TestMethod]
    public void TheHourEndsOnTheTrueLevelsAndNoEstimateWasShown()
    {
        List<Sample> samples = RunTheHour(out WidgetStatusService service);
        using (service)
        {
            (int high, int low) = TrueBuds(HourSeconds - 1);
            Sample last = samples[^1];
            CollectionAssert.AreEquivalent(new[] { high * 10, low * 10 }, new[] { last.Left!.Value, last.Right!.Value });
            Assert.AreEqual(90, last.Case, "The case keeps the last reading it gave, at its own age.");
            Assert.AreEqual(Math.Min(high, low) * 10, last.Gauge);
        }
    }
}
