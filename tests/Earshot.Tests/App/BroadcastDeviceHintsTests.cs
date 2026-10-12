using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The hints a timed-out connect is classified with, read from the widget's broadcast state. A pair that cannot be said to be
// away is never called away: the hint is Unknown, or the PC's side is left to look at.
[TestClass]
public sealed class BroadcastDeviceHintsTests
{
    private sealed class FakeWidgetStatus : IWidgetStatus
    {
        public WidgetSnapshot Current { get; set; } = WidgetSnapshot.Empty(WidgetWatcherState.Started);

        public event EventHandler? Changed { add { } remove { } }

        public event EventHandler<CaseOpenedEventArgs>? CaseOpened { add { } remove { } }

        public event EventHandler<CaseClosedEventArgs>? CaseClosed { add { } remove { } }

        public event EventHandler<ReadingAppliedEventArgs>? ReadingApplied { add { } remove { } }

        public Task RefreshAsync() => Task.CompletedTask;

        public Task<BatteryRefreshOutcome> RefreshBatteryAsync(CancellationToken ct) => Task.FromResult(BatteryRefreshOutcome.NotListening);
    }

    private static WidgetSnapshot Heard(
        DateTimeOffset? at, bool? lidOpen = null, AirPodsWhere where = AirPodsWhere.NotInUse,
        BroadcastSelectionState selection = BroadcastSelectionState.Linked, WidgetWatcherState watcher = WidgetWatcherState.Started) =>
        new(where, PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, at, null, lidOpen, watcher, null, null, false, WidgetCounters.Empty)
        {
            Selection = selection,
        };

    private static DeviceHint Hint(WidgetSnapshot? snapshot, ManualTime time) =>
        new BroadcastDeviceHints(() => snapshot, time).Read();

    [TestMethod]
    public void WithNoWidgetAListenerThatIsNotRunningOrNoLinkedPairTheHintIsUnknown()
    {
        var time = new ManualTime();
        DateTimeOffset now = time.GetUtcNow();

        Assert.AreEqual(DeviceHint.Unknown, Hint(null, time), "no widget");
        Assert.AreEqual(DeviceHint.Unknown, Hint(Heard(now, watcher: WidgetWatcherState.Off), time), "listener off");
        Assert.AreEqual(DeviceHint.Unknown, Hint(Heard(now, watcher: WidgetWatcherState.Stopped), time), "listener stopped");
        Assert.AreEqual(DeviceHint.Unknown, Hint(Heard(now, watcher: WidgetWatcherState.NotStarted), time), "listener not started");
        Assert.AreEqual(DeviceHint.Unknown, Hint(Heard(now, selection: BroadcastSelectionState.Listening), time), "no pair linked yet");
        Assert.AreEqual(DeviceHint.Unknown, Hint(Heard(now, selection: BroadcastSelectionState.NoPairedModel), time), "no paired model");
    }

    [TestMethod]
    public void ALinkedPairNotHeardWithinTheFreshWindowIsNotHeard()
    {
        var time = new ManualTime();
        DateTimeOffset heardAt = time.GetUtcNow();
        time.Advance(BroadcastDeviceHints.HeardWindow + TimeSpan.FromSeconds(1));

        Assert.AreEqual(DeviceHint.NotHeard, Hint(Heard(heardAt), time));
        Assert.AreEqual(DeviceHint.NotHeard, Hint(Heard(null), time), "A linked pair with no reading time is not counted as heard.");
    }

    [TestMethod]
    public void APairHeardExactlyAtTheEdgeOfTheWindowIsStillHeard()
    {
        var time = new ManualTime();
        DateTimeOffset heardAt = time.GetUtcNow();
        time.Advance(BroadcastDeviceHints.HeardWindow);

        Assert.AreEqual(DeviceHint.HeardNearby, Hint(Heard(heardAt), time));
    }

    [TestMethod]
    public void AHeardPairWhoseLidBitSaysShutIsLidClosedAndOneWhoseLidIsOpenOrUnseenIsHeardNearby()
    {
        var time = new ManualTime();
        DateTimeOffset now = time.GetUtcNow();

        Assert.AreEqual(DeviceHint.LidClosed, Hint(Heard(now, lidOpen: false), time));
        Assert.AreEqual(DeviceHint.HeardNearby, Hint(Heard(now, lidOpen: true), time));
        Assert.AreEqual(DeviceHint.HeardNearby, Hint(Heard(now, lidOpen: null), time), "A lid that was never seen says nothing.");
    }

    [TestMethod]
    public void AHeardPairInUseOnAnotherDeviceIsInUseElsewhere()
    {
        var time = new ManualTime();

        Assert.AreEqual(DeviceHint.InUseElsewhere, Hint(Heard(time.GetUtcNow(), where: AirPodsWhere.Elsewhere), time));
    }

    [TestMethod]
    public void TheTraysHintsAreUnknownUntilTheWidgetExistsAndThenFollowIt()
    {
        var time = new ManualTime();
        FakeWidgetStatus? widget = null;
        var services = Program.CreateCoordinatorServices(Paths.FromEnvironment(_ => null), new FakeSettings(), new CapturingLog(), null, time, () => widget, null);

        Assert.AreEqual(DeviceHint.Unknown, services.Hints.Read(), "The widget is built after the coordinator.");

        widget = new FakeWidgetStatus { Current = Heard(time.GetUtcNow()) };
        Assert.AreEqual(DeviceHint.HeardNearby, services.Hints.Read());

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(DeviceHint.NotHeard, services.Hints.Read());
    }

    // ---- through the classifier and the coordinator ----

    private static ToggleReport Toggle(CoordinatorHarness h)
    {
        Task<ToggleReport> task = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true));
        DateTimeOffset start = h.Time.GetUtcNow();
        h.Pump();
        while (!task.IsCompleted && h.Time.GetUtcNow() - start < TimeSpan.FromSeconds(40))
        {
            h.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.IsTrue(task.IsCompleted, "The connect did not finish within the clock's 40 s.");
        return task.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ATimedOutRequestIsTheDevicesOnlyWhenTheBroadcastSupportsItAndThePcsOtherwise()
    {
        var timedOut = new ConnectResult(ConnectOutcome.AttemptedTimedOut, "", []);

        foreach (DeviceHint away in new[] { DeviceHint.NotHeard, DeviceHint.LidClosed, DeviceHint.InUseElsewhere })
        {
            Assert.AreEqual(FailureClass.DeviceSide, ConnectFailureClassifier.Classify(timedOut, away, retried: false).Class, away.ToString());
        }

        ConnectFailure first = ConnectFailureClassifier.Classify(timedOut, DeviceHint.HeardNearby, retried: false);
        ConnectFailure second = ConnectFailureClassifier.Classify(timedOut, DeviceHint.HeardNearby, retried: true);
        Assert.AreEqual(FailureClass.Temporary, first.Class, "Heard nearby: tried once more before it is called the PC's.");
        Assert.AreEqual(FailureClass.PcSide, second.Class);
        Assert.AreEqual(ConnectFailureClassifier.DidNotAnswer, second.Reason);
    }

    [TestMethod]
    public void ACoordinatorGivenTheBroadcastHintsClassesANotHeardTimeoutDeviceSideAndAHeardOnePcSide()
    {
        var time = new ManualTime();
        FakeWidgetStatus widget = new() { Current = Heard(time.GetUtcNow()) };
        IDeviceHints hints = BroadcastDeviceHints.For(() => widget, time);

        using var h = new CoordinatorHarness(hints: hints);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));
        ToggleReport heard = Toggle(h);
        Assert.AreEqual(FailureClass.PcSide, heard.FailureClass, "Heard nearby and no answer: this PC's side after the one retry.");

        widget.Current = Heard(null);
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));
        ToggleReport away = Toggle(h);
        Assert.AreEqual(FailureClass.DeviceSide, away.FailureClass);
        Assert.AreEqual(ConnectFailureClassifier.NotHeardNearby, away.UserMessage);
    }
}
