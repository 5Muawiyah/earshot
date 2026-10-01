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
        _handsFree = new FakeHandsFreeBatterySource();
        _paired = new FakePairedModelSource();
    }

    public void Dispose() => _temp.Dispose();

    private WidgetStatusService NewService() =>
        new(
            () => _source, _settings, _monitor, () => null, _log, action => action(), _clock, _paired, _handsFree,
            ProximityDecodeTable.Documented, runInBackground: work => work());

    // A refresh that has not ended would block a bare .Result for ever, so a regression that stops one ending hung the
    // run instead of failing a test: the end is asserted first.
    private static BatteryRefreshOutcome Outcome(Task<BatteryRefreshOutcome> task)
    {
        Assert.IsTrue(task.IsCompleted, "The refresh has ended.");
        return task.Result;
    }

    private AdvertisementSample Message(DateTimeOffset? at = null, uint tag = 1) =>
        new(
            ProximityParser.AppleCompanyId,
            WidgetFixtures.Proximity(batteryA: 0x56, batteryB: 0x0A), -60, at ?? _clock.GetUtcNow(), tag);

    // The linked set with values: the owner opens the case, so five messages with the case level known, half a second
    // apart, the last of them linking the set.
    private void Choose()
    {
        for (int i = 0; i < 5; i++)
        {
            _source.Raise(Message());
            _clock.Advance(TimeSpan.FromMilliseconds(500));
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
        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
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
        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
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
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, Outcome(refresh));
    }

    private static readonly Guid Container = Guid.NewGuid();

    // The AirPods become this PC's output, with the pinned device the figure is read for.
    private void PutOnThisPc(bool active = true)
    {
        _settings.Update(s =>
        {
            s.PinnedContainerId = Container;
            s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
        });
        var endpoint = new AudioEndpoint("ep1", EndpointFlow.Render, active ? EndpointState.Active : EndpointState.Unplugged, "AirPods", Container);
        var model = new DeviceModel(Container, "AirPods", active ? ConnectionState.Connected : ConnectionState.Disconnected, new[] { endpoint });
        _monitor.Raise(new DeviceSnapshot(model, new[] { model }, _clock.GetUtcNow())
        {
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        });
    }

    [TestMethod]
    public void WindowsFigureAnswersWhenNothingIsHeard()
    {
        _handsFree.Percent = 64;
        using WidgetStatusService service = NewService();
        service.Start();
        PutOnThisPc();
        Choose();
        int readsBefore = _handsFree.Reads;

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        Assert.AreEqual(readsBefore + 1, _handsFree.Reads, "A refresh starts a read of Windows' figure.");
        _clock.Advance(WidgetTiming.RefreshWindow);

        Assert.AreEqual(BatteryRefreshOutcome.WindowsFigure, Outcome(refresh));
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

        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
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

        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, Outcome(refresh));
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

        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(first));
        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(second));
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
        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(other));
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

        for (int i = 0; i < 5 && !refresh.IsCompleted; i++)
        {
            _source.Raise(Message());
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.IsTrue(refresh.IsCompleted, "The link is made after five messages with the case level known, two seconds apart.");
        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
    }

    [TestMethod]
    public void WithBluetoothOffItSaysSoAndTheNextRefreshMakesTheOneImmediateAttempt()
    {
        _source.StartResult = () => AdvertisementSourceCodes.RadioOff("fake-start");
        using WidgetStatusService service = NewService();
        service.Start();

        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.IsTrue(refresh.IsCompleted);
        Assert.AreEqual(BatteryRefreshOutcome.BluetoothOff, Outcome(refresh));

        _source.StartResult = null; // Bluetooth is on again
        int starts = _source.StartCalls;
        Task<BatteryRefreshOutcome> again = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(starts + 1, _source.StartCalls, "The refresh made the one immediate attempt.");
        Assert.IsFalse(again.IsCompleted, "It worked, so it waits for the set to be heard.");
    }

    [TestMethod]
    public void AWatcherThatStopsWithBluetoothOffDuringTheWaitEndsItBluetoothOff()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));

        Assert.AreEqual(BatteryRefreshOutcome.BluetoothOff, Outcome(refresh));
    }

    [TestMethod]
    public void WithTheWidgetOffSuspendedOrClosedThereIsNothingToListenTo()
    {
        _settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        using WidgetStatusService off = NewService();
        off.Start();
        Assert.AreEqual(BatteryRefreshOutcome.NotListening, Outcome(off.RefreshBatteryAsync(CancellationToken.None)));

        _settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        using WidgetStatusService service = NewService();
        service.Start();
        service.Suspend();
        Assert.AreEqual(BatteryRefreshOutcome.NotListening, Outcome(service.RefreshBatteryAsync(CancellationToken.None)));
        service.Resume();

        service.Close();
        Assert.AreEqual(BatteryRefreshOutcome.NotListening, Outcome(service.RefreshBatteryAsync(CancellationToken.None)));
    }

    [TestMethod]
    public void SuspendingOrClosingDuringTheWaitEndsItNotListening()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        service.Suspend();

        Assert.AreEqual(BatteryRefreshOutcome.NotListening, Outcome(refresh));
    }

    // What the card reads to clear "Nothing heard": a battery read time after the refresh started. Buds heard with the case
    // unsaid (its lid is shut) are that, whatever age the case's own figure has.
    [TestMethod]
    public void BudsHeardDuringARefreshWithTheCaseUnsaidMoveTheBatteryReadTimePastTheRefreshStart()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        _source.Raise(Message());
        _clock.Advance(TimeSpan.FromSeconds(9));
        DateTimeOffset started = _clock.GetUtcNow();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);
        Assert.IsTrue(service.Current.BatteryReadAt < started, "Before anything is heard the newest reading is from before the refresh.");

        _clock.Advance(TimeSpan.FromSeconds(1));
        _source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryA: 0x56, batteryB: 0x0F), -60, _clock.GetUtcNow(), 1));

        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
        Assert.IsTrue(service.Current.BatteryReadAt >= started, "The buds' own reading is newer than the refresh.");
        Assert.IsTrue(service.Current.Case.ReadAt < started, "The case still carries its old figure.");
    }

    [TestMethod]
    public void ClosingDuringTheWaitEndsItNotListening()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        service.Close();

        Assert.AreEqual(BatteryRefreshOutcome.NotListening, Outcome(refresh));
    }

    // The addresses rotated past the window: the linked set is followed under its new address, and a refresh asked for
    // meanwhile ends Heard on the message that follows it, not before.
    [TestMethod]
    public void ARefreshDuringAnAddressChangeWaitsForTheFollowAndEndsHeardWhenItIsMade()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        _clock.Advance(TimeSpan.FromSeconds(12));
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        for (int i = 0; i < 4; i++)
        {
            _source.Raise(Message(tag: 7));
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.IsFalse(refresh.IsCompleted, "A set heard for a second and a half under a new address is not followed yet.");

        _source.Raise(Message(tag: 7));

        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
        Assert.AreEqual(1, service.Current.Counters.Followed);
    }

    // A model that could not be read at start is read again by a refresh, and the set can then be chosen.
    [TestMethod]
    public void ARefreshReadsThePairedModelAgainWhileNoneIsHeld()
    {
        _paired.Model = null;
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(1, _paired.Reads);
        Assert.AreEqual(BroadcastSelectionState.NoPairedModel, service.Current.Selection);

        _paired.Model = BroadcastFixtures.PairedModel;
        Task<BatteryRefreshOutcome> refresh = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(2, _paired.Reads, "The refresh asked again.");
        Assert.AreEqual(BroadcastSelectionState.Listening, service.Current.Selection);
        for (int i = 0; i < 5 && !refresh.IsCompleted; i++)
        {
            _source.Raise(Message());
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(refresh));
        Assert.AreEqual(60, service.Current.Left.Percent);
    }

    [TestMethod]
    public void ARefreshDoesNotReadThePairedModelAgainWhileOneIsHeld()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(1, _paired.Reads);

        _ = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(1, _paired.Reads);
    }

    [TestMethod]
    public void ARetryOfTheListenReadsThePairedModelAgainWhileNoneIsHeld()
    {
        _paired.Model = null;
        _source.StartResult = () => AdvertisementSourceCodes.RadioOff("fake-start");
        using WidgetStatusService service = NewService();
        service.Start();
        Assert.AreEqual(1, _paired.Reads);

        _paired.Model = BroadcastFixtures.PairedModel;
        _clock.Advance(WidgetTiming.WatcherRetryDelay);

        Assert.AreEqual(2, _paired.Reads, "The retry asked again.");
        Assert.AreEqual(BroadcastSelectionState.Listening, service.Current.Selection);
    }

    [TestMethod]
    public void ARefreshAfterOneHasEndedStartsANewRun()
    {
        using WidgetStatusService service = NewService();
        service.Start();
        Choose();
        Task<BatteryRefreshOutcome> first = service.RefreshBatteryAsync(CancellationToken.None);
        _clock.Advance(WidgetTiming.RefreshWindow);
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, Outcome(first));
        int starts = _source.StartCalls;

        Task<BatteryRefreshOutcome> second = service.RefreshBatteryAsync(CancellationToken.None);

        Assert.AreEqual(starts + 1, _source.StartCalls);
        _source.Raise(Message());
        Assert.AreEqual(BatteryRefreshOutcome.Heard, Outcome(second));
    }
}
