using Earshot.Infra;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class LowBatteryAlertServiceTests : IDisposable
{
    // IWidgetStatus's whole public surface, with a settable Current and only Changed actually driven: this is
    // proving the WIRING (LowBatteryAlertService reading what is shown and feeding LowBatteryLatch and
    // INotifier), not IWidgetStatus or WidgetStatusService itself, which WidgetStatusServiceTests covers.
    private sealed class FakeWidgetStatus : IWidgetStatus
    {
        public WidgetSnapshot Current { get; set; } = WidgetSnapshot.Empty(WidgetWatcherState.Started);

        public event EventHandler? Changed;

        public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

        public event EventHandler<ReadingAppliedEventArgs>? ReadingApplied;

        public Task RefreshAsync() => Task.CompletedTask;

        public Task<BatteryRefreshOutcome> RefreshBatteryAsync(CancellationToken ct) => Task.FromResult(BatteryRefreshOutcome.NotListening);

        public void Raise(WidgetSnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Unused by this test class; kept so the type fully implements the interface without a warning.
        internal void RaiseCaseOpened(DateTimeOffset at) => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(at));

        internal void RaiseReading(DecodedReading reading, DateTimeOffset at) => ReadingApplied?.Invoke(this, new ReadingAppliedEventArgs(reading, at));
    }

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeWidgetStatus _status = null!;
    private FakeNotifier _notifier = null!;
    private TestTimeProvider _clock = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _status = new FakeWidgetStatus();
        _notifier = new FakeNotifier();
        _clock = new TestTimeProvider();
    }

    public void Dispose() => _temp.Dispose();

    private LowBatteryAlertService NewService() => new(_status, _settings, _notifier, _clock);

    // A snapshot whose parts were read this long ago. A value read within 30 s is fresh and is what is shown;
    // older it is greyed and feeds nothing.
    private WidgetSnapshot Snapshot(int? left = null, int? right = null, int? box = null, double ageSeconds = 1, int? headset = null, double headsetAgeSeconds = 1, AirPodsWhere where = AirPodsWhere.ThisPc)
    {
        DateTimeOffset readAt = _clock.GetUtcNow() - TimeSpan.FromSeconds(ageSeconds);
        PartReading Part(int? percent) => percent is null ? PartReading.Unknown : new PartReading(percent, null, null) { ReadAt = readAt };
        return WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = where,
            Left = Part(left),
            Right = Part(right),
            Case = Part(box),
            Headset = headset is null ? PartReading.Unknown : new PartReading(headset, null, null) { ReadAt = _clock.GetUtcNow() - TimeSpan.FromSeconds(headsetAgeSeconds) },
        };
    }

    [TestMethod]
    public void AFreshValueExactlyAtTheThresholdNotifiesOnce()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 20));

        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(("Earshot", "Left AirPod at 20%"), _notifier.Calls[0]);
    }

    [TestMethod]
    public void AValueAboveTheThresholdDoesNotNotify()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 30));

        Assert.AreEqual(0, _notifier.Calls.Count);
    }

    [TestMethod]
    public void TwoSnapshotsAtTheThresholdInARowNotifyOnlyOnce()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 20));
        _status.Raise(Snapshot(left: 20));

        Assert.AreEqual(1, _notifier.Calls.Count, "The latch, not this service, owns not repeating.");
    }

    // Every part is checked independently: a single snapshot with two parts crossing the threshold at once must
    // notify for each.
    [TestMethod]
    public void ARightAndACaseValueEachNotifyWithTheirOwnText()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(right: 10, box: 20));

        Assert.AreEqual(2, _notifier.Calls.Count);
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Right AirPod at 10%"));
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Case at 20%"));
    }

    // The alert acts on what is shown, and a greyed value is not shown as current.
    [TestMethod]
    public void AValueThatIsNotFreshNeverAlerts()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 10, ageSeconds: 31));

        Assert.AreEqual(0, _notifier.Calls.Count, "A greyed low value is not shown as current, so it does not alert.");
        Assert.AreEqual(LatchState.Armed, service.LeftLatchStateForTest);
    }

    [TestMethod]
    public void TheSameLowValueAfterItGreysAndComesBackDoesNotAlertAgain()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 10));
        Assert.AreEqual(1, _notifier.Calls.Count);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _status.Raise(Snapshot(left: 10, ageSeconds: 300)); // greyed
        _status.Raise(Snapshot(left: 10)); // fresh again, the same value

        Assert.AreEqual(1, _notifier.Calls.Count, "It neither re-alerted nor re-armed while greyed.");
        Assert.AreEqual(LatchState.Fired, service.LeftLatchStateForTest);
    }

    [TestMethod]
    public void AGreyedValueAboveTheThresholdDoesNotRearmALatchThatFired()
    {
        using LowBatteryAlertService service = NewService();
        _status.Raise(Snapshot(left: 10));

        _status.Raise(Snapshot(left: 80, ageSeconds: 300)); // a greyed high value: not shown, so it re-arms nothing

        Assert.AreEqual(LatchState.Fired, service.LeftLatchStateForTest);

        _status.Raise(Snapshot(left: 80)); // a fresh high value re-arms
        Assert.AreEqual(LatchState.Armed, service.LeftLatchStateForTest);
    }

    // Windows' own figure is one number for the headset. It alerts only when it is what is shown: no bud has a
    // fresh broadcast value and the figure is current.
    [TestMethod]
    public void WindowsFigureAlertsOnlyWhenItIsWhatIsShown()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 60, headset: 15));
        Assert.AreEqual(0, _notifier.Calls.Count, "A fresh broadcast bud is shown, so Windows' figure is not.");

        _status.Raise(Snapshot(left: 60, ageSeconds: 120, headset: 15));
        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(("Earshot", "AirPods at 15%"), _notifier.Calls[0]);
    }

    [TestMethod]
    public void AnOldWindowsFigureAndOneForAirPodsOffThisPcNeverAlert()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(headset: 10, headsetAgeSeconds: 200));
        _status.Raise(Snapshot(headset: 10, where: AirPodsWhere.NotInUse));

        Assert.AreEqual(0, _notifier.Calls.Count);
    }

    // The alert follows what is shown, and nothing is shown for AirPods that are not on this PC.
    [TestMethod]
    public void ALowBroadcastValueNeverAlertsWhileTheAirPodsAreNotOnThisPc()
    {
        using LowBatteryAlertService service = NewService();

        foreach (AirPodsWhere where in new[] { AirPodsWhere.Unknown, AirPodsWhere.Elsewhere, AirPodsWhere.NotInUse })
        {
            _status.Raise(Snapshot(left: 10, right: 10, box: 10, where: where));
        }

        Assert.AreEqual(0, _notifier.Calls.Count, "No figure is shown, so none alerts.");
        Assert.AreEqual(LatchState.Armed, service.LeftLatchStateForTest);

        _status.Raise(Snapshot(left: 10, where: AirPodsWhere.ThisPc));
        Assert.AreEqual(1, _notifier.Calls.Count, "Once the AirPods are on this PC the same value is shown and alerts.");
    }

    // The setting gates only the notification call. The latch keeps being fed while the setting is off, so its
    // Armed/Fired bookkeeping never falls out of step with the real battery: turning the alert back on must not
    // immediately re-fire for a part that has been low the whole time, only for a genuine new drop.
    [TestMethod]
    public void WithTheAlertOffTheLatchIsStillFedButNothingIsNotified()
    {
        _settings.Update(s => s.Widget = s.Widget with { LowBatteryAlert = false });
        using LowBatteryAlertService service = NewService();

        _status.Raise(Snapshot(left: 20));

        Assert.AreEqual(0, _notifier.Calls.Count, "The alert is off: nothing must be notified.");
        Assert.AreEqual(LatchState.Fired, service.LeftLatchStateForTest, "The latch must still have been fed while the alert was off.");

        _settings.Update(s => s.Widget = s.Widget with { LowBatteryAlert = true });
        _status.Raise(Snapshot(left: 20)); // still the same low value, not a new drop

        Assert.AreEqual(0, _notifier.Calls.Count, "Turning the alert back on must not itself re-fire for a part that never changed.");

        _status.Raise(Snapshot(left: 30)); // re-arms
        _status.Raise(Snapshot(left: 20)); // a genuine new drop, alert now on

        Assert.AreEqual(1, _notifier.Calls.Count, "A genuine new drop while the alert is on must notify.");
    }

    [TestMethod]
    public void AThresholdSettingChangeCallsSetThreshold()
    {
        using LowBatteryAlertService service = NewService();
        _status.Raise(Snapshot(left: 30)); // above the default 20% threshold: no fire
        Assert.AreEqual(0, _notifier.Calls.Count);

        _settings.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 30 });

        Assert.AreEqual(LatchState.Armed, service.LeftLatchStateForTest, "SetThreshold must have run: the latch is still Armed, ready for the new threshold.");

        _status.Raise(Snapshot(left: 30)); // now at the new threshold: fires

        Assert.AreEqual(1, _notifier.Calls.Count, "The new threshold (30) must actually be in effect on LowBatteryLatch.");
    }

    [TestMethod]
    public void DisposeStopsFeedingTheLatchAndNotifying()
    {
        LowBatteryAlertService service = NewService();
        service.Dispose();

        _status.Raise(Snapshot(left: 20));

        Assert.AreEqual(0, _notifier.Calls.Count, "A disposed service must not react to further changes.");
    }
}
