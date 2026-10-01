using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase3;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The battery refresh at the service: it restarts the passive listen, reads Windows' own figure, and waits for the
// chosen set to be heard again or for twelve seconds to pass.
[TestClass]
public sealed class BatteryRefreshTests : IDisposable
{
    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private TestTimeProvider _clock = null!;
    private FakeDeviceMonitor _monitor = null!;
    private FakeAdvertisementSource _source = null!;
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
        _handsFree = new FakeHandsFreeBatterySource();
    }

    public void Dispose() => _temp.Dispose();

    private WidgetStatusService NewService() =>
        new(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, new FakePairedModelSource(), _handsFree,
            ProximityDecodeTable.Documented, runInBackground: work => work());

    private AdvertisementSample Message(DateTimeOffset? at = null, uint tag = 1) =>
        new(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(batteryA: 0x56, batteryB: 0x0A), -60, at ?? _clock.GetUtcNow(), tag);

    // The chosen set with values: three messages two seconds apart, the last choosing it.
    private void Choose()
    {
        for (int i = 0; i < 3; i++)
        {
            _source.Raise(Message());
            _clock.Advance(TimeSpan.FromMilliseconds(1100));
        }
    }

    [TestMethod]
    public void ARefreshRestartsTheListenAndEndsHeardWhenTheChosenSetIsHeardAgain()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        int starts = _source.StartCalls;
        int stops = _source.StopCalls;

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(stops + 1, _source.StopCalls, "The listen is stopped once.");
        Assert.AreEqual(starts + 1, _source.StartCalls, "...and started again, a new run.");
        Assert.IsFalse(refresh.IsCompleted, "Nothing has been heard since.");

        _source.Raise(Message());

        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.Heard, refresh.Result);
        Assert.IsTrue(_log.Has(LogLevel.Info, "Widget: battery refresh ended: Heard."), "The end is on the log, which the live test reads.");
    }

    // A message that was sent before the restart, but reaches the service after it (the UI queue), is not the
    // answer: the refresh waits for one sent afterwards.
    [TestMethod]
    public void AMessageSentBeforeTheRestartIsNotTheAnswer()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        DateTimeOffset before = _clock.GetUtcNow() - TimeSpan.FromSeconds(1);

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        _source.Raise(Message(at: before));

        Assert.IsFalse(refresh.IsCompleted, "That message is older than the restart.");

        _source.Raise(Message());
        Assert.AreEqual(BatteryRefreshOutcome.Heard, refresh.Result);
    }

    [TestMethod]
    public void WithNothingHeardItEndsAtTwelveSecondsAndNotBefore()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        _clock.Advance(WidgetTiming.RefreshWindow - TimeSpan.FromMilliseconds(100));
        Assert.IsFalse(refresh.IsCompleted, "Not yet twelve seconds.");

        _clock.Advance(TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(TimeSpan.FromSeconds(12), WidgetTiming.RefreshWindow);
        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, refresh.Result);
    }

    [TestMethod]
    public void WindowsFigureAnswersWhenNothingIsHeard()
    {
        _handsFree.Percent = 64;
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        int readsBefore = _handsFree.Reads;

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        Assert.AreEqual(readsBefore + 1, _handsFree.Reads, "A refresh starts a read of Windows' figure.");
        _clock.Advance(WidgetTiming.RefreshWindow);

        Assert.AreEqual(BatteryRefreshOutcome.WindowsFigure, refresh.Result);
        Assert.AreEqual(64, service.Current.Headset.Percent);
    }

    [TestMethod]
    public void HearingTheSetBeatsWindowsFigure()
    {
        _handsFree.Percent = 64;
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        _source.Raise(Message());

        Assert.AreEqual(BatteryRefreshOutcome.Heard, refresh.Result);
    }

    [TestMethod]
    public void NoValueIsClearedByARefresh()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        _source.Raise(Message());
        WidgetSnapshot before = service.Current;
        Assert.AreEqual(60, before.Left.Percent);

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        WidgetSnapshot during = service.Current;
        _clock.Advance(WidgetTiming.RefreshWindow);

        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, refresh.Result);
        Assert.AreEqual(before.Left, during.Left);
        Assert.AreEqual(before.Right, service.Current.Right);
        Assert.AreEqual(before.Case, service.Current.Case);
        Assert.AreEqual(60, service.Current.Left.Percent, "What was read stays, with its own read time.");
    }

    [TestMethod]
    public void ASecondRequestWhileOneRunsJoinsIt()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();

        Task<BatteryRefreshOutcome> first = service.RefreshBatteryAsync(CancellationToken.None);
        int stops = _source.StopCalls;
        Task<BatteryRefreshOutcome> second = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(stops, _source.StopCalls, "The second request does not restart the listen again.");
        _source.Raise(Message());

        Assert.AreEqual(BatteryRefreshOutcome.Heard, first.Result);
        Assert.AreEqual(BatteryRefreshOutcome.Heard, second.Result);
    }

    [TestMethod]
    public void CancellingEndsOnlyTheCallersWait()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        using var cts = new CancellationTokenSource();

        Task<BatteryRefreshOutcome> cancelled = service.RefreshBatteryAsync(cts.Token);
        Task<BatteryRefreshOutcome> other = service.RefreshBatteryAsync(CancellationToken.None);
        cts.Cancel();

        Assert.IsTrue(cancelled.IsCanceled);
        Assert.IsFalse(other.IsCompleted, "The refresh itself goes on.");

        _source.Raise(Message());
        Assert.AreEqual(BatteryRefreshOutcome.Heard, other.Result);
    }

    [TestMethod]
    public void ACancelledTokenReturnsACancelledTaskAndStartsNothing()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        int stops = _source.StopCalls;

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(new CancellationToken(canceled: true));

        Assert.IsTrue(refresh.IsCanceled);
        Assert.AreEqual(stops, _source.StopCalls);
    }

    [TestMethod]
    public void ASetChosenDuringTheWaitAnswersToo()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        for (int i = 0; i < 3 && !refresh.IsCompleted; i++)
        {
            _source.Raise(Message());
            _clock.Advance(TimeSpan.FromMilliseconds(1100));
        }

        Assert.IsTrue(refresh.IsCompleted, "The first choice is made after two seconds and three messages.");
        Assert.AreEqual(BatteryRefreshOutcome.Heard, refresh.Result);
    }

    [TestMethod]
    public void WithBluetoothOffItSaysSoAndAvailabilityFollowsTheWatcher()
    {
        _source.StartResult = () => AdvertisementSourceCodes.RadioOff("fake-start");
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.IsFalse(service.BatteryRefreshAvailable);

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.BluetoothOff, refresh.Result);

        _source.StartResult = null; // Bluetooth is on again
        Task<BatteryRefreshOutcome> again = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.IsTrue(service.BatteryRefreshAvailable, "The refresh made the one immediate attempt and it worked.");
        Assert.IsFalse(again.IsCompleted, "It waits for the set to be heard.");
    }

    [TestMethod]
    public void AWatcherThatStopsWithBluetoothOffDuringTheWaitEndsItBluetoothOff()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));

        Assert.AreEqual(BatteryRefreshOutcome.BluetoothOff, refresh.Result);
    }

    [TestMethod]
    public void WithTheWidgetOffSuspendedOrClosedThereIsNothingToListenTo()
    {
        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        using WidgetStatusService off = NewService();
        off.Start();
        Assert.AreEqual(BatteryRefreshOutcome.NotListening, off.RefreshBatteryAsync(CancellationToken.None).Result);
        Assert.IsFalse(off.BatteryRefreshAvailable);

        _settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        using WidgetStatusService service = NewService();
        service.Start();
        service.Suspend();
        Assert.AreEqual(BatteryRefreshOutcome.NotListening, service.RefreshBatteryAsync(CancellationToken.None).Result);
        service.Resume();

        service.Close();
        Assert.AreEqual(BatteryRefreshOutcome.NotListening, service.RefreshBatteryAsync(CancellationToken.None).Result);
    }

    [TestMethod]
    public void SuspendingOrClosingDuringTheWaitEndsItNotListening()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        service.Suspend();

        Assert.AreEqual(BatteryRefreshOutcome.NotListening, refresh.Result);
    }

    [TestMethod]
    public void ARefreshAfterOneHasEndedStartsANewRun()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> first = service.RefreshBatteryAsync(CancellationToken.None);
        _clock.Advance(WidgetTiming.RefreshWindow);
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, first.Result);
        int starts = _source.StartCalls;

        Task<BatteryRefreshOutcome> second = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(starts + 1, _source.StartCalls);
        _source.Raise(Message());
        Assert.AreEqual(BatteryRefreshOutcome.Heard, second.Result);
    }
}
