using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Adopting a different pair drops the previous pair's last readings and link. The replaced buds are the same model as the
// old ones, so the model rule alone never noticed: the reading of the pair that was removed stayed on the card until the new
// pair's case was opened by the PC. Learned charge speeds are the case's and the model's, so they stay.
[TestClass]
public sealed class ForgetPairTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();
    private const uint OwnerLeftBud = 0xA1B2C3D4;
    private const uint OwnerRightBud = 0xB2C3D4E5;
    private const byte AllCharging = 0x70;

    private TempFolder _temp = null!;
    private EnvironmentVariableScope _dataRoot = null!;
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
        _dataRoot = new EnvironmentVariableScope(Paths.DataRootVariable, _temp.Path);
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _clock = new TestTimeProvider();
        _monitor = new FakeDeviceMonitor(_clock);
        _source = new FakeAdvertisementSource();
        _paired = new FakePairedModelSource();
    }

    [TestCleanup]
    public void Cleanup() => _dataRoot.Dispose();

    public void Dispose() => _temp.Dispose();

    private string StoreFile => Paths.FromEnvironment(Environment.GetEnvironmentVariable, _ => Path.Combine(_temp.Path, "profile")).LastReadingFile;

    private WidgetStatusService NewService()
    {
        _source = new FakeAdvertisementSource();
        var service = new WidgetStatusService(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, null,
            ProximityDecodeTable.Documented, runInBackground: work => work(), lastReadings: new LastReadingStore(StoreFile, _log));
        service.Start();
        return service;
    }

    private LinkMemoryStore LinkFile() => new(Path.Combine(Path.GetDirectoryName(StoreFile)!, "link.json"), _log);

    private DateTimeOffset Now => _clock.GetUtcNow();

    private void Tick(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    private void Raise(uint tag, ProximityMessage m, sbyte rssi) =>
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(m.ModelHigh, m.ModelLow, m.Status, m.BatteryA, m.BatteryB, m.Lid, m.Colour, m.Reserved),
            rssi, Now, tag));

    private void OwnerCaseOpen(double seconds, int budLevel, int caseLevel, byte charging = 0, double every = 0.25)
    {
        int steps = (int)Math.Round(seconds / every);
        for (int i = 0; i <= steps; i++)
        {
            bool one = i % 2 == 0;
            Raise(one ? OwnerLeftBud : OwnerRightBud,
                BroadcastFixtures.Bud(one, caseNibble: (byte)(charging | caseLevel), pairHigh: budLevel, pairLow: budLevel), -55);
            Tick(every);
        }
    }

    [TestMethod]
    public void AdoptingADifferentPairOfTheSameModelDropsItsReadingsAndLink()
    {
        const string reason = "a different pair was adopted";
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x7, caseLevel: 0x9);
        Assert.AreEqual(BroadcastSelectionState.Linked, service.Current.Selection);
        ShownBattery before = BatteryFreshness.Shown(service.Current, Now);
        Assert.AreEqual(70, before.Left.Percent, "The old pair's reading is on show before the pair is adopted.");
        Assert.IsNotNull(service.Current.LastReadings.Left);
        Assert.IsNotNull(LinkFile().Load(), "The link is remembered on disk before the pair is adopted.");

        var changed = 0;
        service.Changed += (_, _) => changed++;
        service.ForgetPair(reason);

        Assert.AreNotEqual(BroadcastSelectionState.Linked, service.Current.Selection, "The link is dropped.");
        ShownBattery after = BatteryFreshness.Shown(service.Current, Now);
        Assert.IsFalse(after.Left.HasValue || after.Right.HasValue || after.Case.HasValue, "Nothing of the old pair is shown.");
        Assert.IsNull(service.Current.LastReadings.Left);
        Assert.IsNull(service.Current.LastReadings.Right);
        Assert.IsNull(service.Current.LastReadings.Case);
        Assert.IsEmpty(service.Current.LastReadings.Marks);
        Assert.IsGreaterThan(0, changed, "The card and the gauge are told.");
        Assert.IsTrue(_log.Entries.Any(e => e.Message.Contains("a different pair was adopted, so the previous pair's last readings and link were dropped", StringComparison.Ordinal)));
        service.Close();

        // Not after a restart either: what was saved is the new, empty book, and the link memory is gone.
        Tick(60);
        using WidgetStatusService restarted = NewService();
        ShownBattery again = BatteryFreshness.Shown(restarted.Current, Now);
        Assert.IsFalse(again.Left.HasValue || again.Right.HasValue || again.Case.HasValue, "Nor after a restart.");
        Assert.IsNull(LinkFile().Load(), "The remembered link was dropped with the pair.");
    }

    // The log says why the readings went: a removed pair is not a different pair adopted.
    [TestMethod]
    public void TheLogLineSaysTheReasonItWasGiven()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x7, caseLevel: 0x9);

        service.ForgetPair("the pinned AirPods are no longer paired");

        Assert.IsTrue(_log.Entries.Any(e => e.Message == "Widget: the pinned AirPods are no longer paired, so the previous pair's last readings and link were dropped."),
            string.Join(" | ", _log.Entries.Select(e => e.Message)));
        Assert.IsFalse(_log.Entries.Any(e => e.Message.Contains("a different pair was adopted", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ANewPairsFirstCaseOpeningShowsOnlyItsOwnReading()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0x7, caseLevel: 0x9);
        service.ForgetPair("test");

        // The replaced buds, 100 percent, in a case at 90 percent, opened next to the PC.
        OwnerCaseOpen(3, budLevel: 0xA, caseLevel: 0x9);

        ShownBattery shown = BatteryFreshness.Shown(service.Current, Now);
        Assert.AreEqual(100, shown.Left.Percent);
        Assert.AreEqual(100, shown.Right.Percent);
        Assert.AreEqual(90, shown.Case.Percent);
    }

    [TestMethod]
    public void TheLearnedChargeSpeedsAreKept()
    {
        using WidgetStatusService service = NewService();
        OwnerCaseOpen(3, budLevel: 0xA, caseLevel: 0x4, charging: AllCharging);
        DateTimeOffset start = Now;
        int CaseAt(double minutes) => minutes < 1 ? 4 : minutes < 11 ? 5 : minutes < 21 ? 6 : 7;
        for (int s = 0; s <= 22 * 60; s++)
        {
            bool one = s % 2 == 0;
            Raise(one ? OwnerLeftBud : OwnerRightBud,
                BroadcastFixtures.Bud(one, caseNibble: (byte)(AllCharging | CaseAt((Now - start).TotalMinutes)), pairHigh: 0xA, pairLow: 0xA), -55);
            Tick(1);
        }

        LearnedRate? learned = service.Current.LastReadings.RateFor(BroadcastFixtures.PairedModel, ChargePart.Case);
        Assert.IsNotNull(learned, "A charge of the case was seen.");

        service.ForgetPair("test");

        LearnedRate? kept = service.Current.LastReadings.RateFor(BroadcastFixtures.PairedModel, ChargePart.Case);
        Assert.IsNotNull(kept, "The case is the same case, so its speed stays.");
        Assert.AreEqual(learned.PercentPerHour, kept.PercentPerHour, 0.001);
        Assert.IsNull(service.Current.LastReadings.Case);
    }

    [TestMethod]
    public void ForgettingWithNothingHeldChangesNothingAndDoesNotThrow()
    {
        using WidgetStatusService service = NewService();
        service.ForgetPair("test");
        Assert.AreNotEqual(BroadcastSelectionState.Linked, service.Current.Selection);
    }

    [TestMethod]
    public void TheSelectorForgetsItsLinkAndItsMemoryButKeepsTheModel()
    {
        var selector = new BroadcastSelector();
        selector.SetPairedModel(BroadcastFixtures.PairedModel);
        selector.LoadMemory(new Earshot.Widget.LinkMemory(BroadcastFixtures.PairedModel, 0x02, Now, Now));
        Assert.IsNotNull(selector.Memory);

        selector.ForgetLink();

        Assert.IsNull(selector.Memory);
        Assert.IsFalse(selector.IsLinked);
        Assert.AreEqual(BroadcastFixtures.PairedModel, selector.PairedModel, "The model is read from Windows, not from the link.");
    }
}
