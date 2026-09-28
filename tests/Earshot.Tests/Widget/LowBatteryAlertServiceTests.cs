using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class LowBatteryAlertServiceTests : IDisposable
{
    // IWidgetStatus's whole public surface, with only OwnedReadingApplied actually driven: this is proving
    // the WIRING (LowBatteryAlertService feeding LowBatteryLatch and INotifier correctly), not IWidgetStatus
    // or WidgetStatusService itself, which WidgetStatusServiceTests already covers.
    private sealed class FakeWidgetStatus : IWidgetStatus
    {
        public WidgetSnapshot Current => WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true);

        public bool ClaimAvailable => false;

        public event EventHandler? Changed;

        public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

        public event EventHandler<OwnedReadingEventArgs>? OwnedReadingApplied;

        public Task<ClaimOutcome> ClaimAsync(CancellationToken ct) => throw new NotSupportedException();

        public void ForgetClaim() => throw new NotSupportedException();

        public Task RefreshAsync() => Task.CompletedTask;

        public void Raise(DecodedReading reading, DateTimeOffset at) =>
            OwnedReadingApplied?.Invoke(this, new OwnedReadingEventArgs(reading, at));

        // Unused by this test class; kept so the type fully implements the interface without a warning.
        internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        internal void RaiseCaseOpened(DateTimeOffset at) => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(at));
    }

    private TempFolder _temp = null!;
    private CapturingLog _log = null!;
    private JsonSettingsStore _settings = null!;
    private FakeWidgetStatus _status = null!;
    private FakeNotifier _notifier = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
        _status = new FakeWidgetStatus();
        _notifier = new FakeNotifier();
    }

    public void Dispose() => _temp.Dispose();

    private LowBatteryAlertService NewService() => new(_status, _settings, _notifier);

    private static DecodedReading Reading(int? left = null, int? right = null, int? box = null) =>
        new(new PartReading(left, null, null), new PartReading(right, null, null), new PartReading(box, null, null), null, null);

    [TestMethod]
    public void AReadingExactlyAtTheThresholdNotifiesOnce()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow);

        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(("Earshot", "Left AirPod at 20%"), _notifier.Calls[0]);
    }

    [TestMethod]
    public void AReadingAboveTheThresholdDoesNotNotify()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Reading(left: 30), DateTimeOffset.UtcNow);

        Assert.AreEqual(0, _notifier.Calls.Count);
    }

    [TestMethod]
    public void TwoReadingsAtTheThresholdInARowNotifyOnlyOnce()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow);
        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow);

        Assert.AreEqual(1, _notifier.Calls.Count, "The latch, not this service, owns not repeating: two readings at the boundary must fire once.");
    }

    // Every part is checked independently: a single reading with two parts crossing the threshold at once
    // must notify for each.
    [TestMethod]
    public void ARightAndACaseReadingEachNotifyWithTheirOwnText()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Reading(right: 10, box: 20), DateTimeOffset.UtcNow);

        Assert.AreEqual(2, _notifier.Calls.Count);
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Right AirPod at 10%"));
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Case at 20%"));
    }

    // The setting gates only the notification call. The latch keeps being fed while the setting is off, so
    // its Armed/Fired bookkeeping never falls out of step with the real battery: turning the alert back on
    // must not immediately re-fire for a part that has been low the whole time, only for a genuine new drop.
    [TestMethod]
    public void WithTheAlertOffTheLatchIsStillFedButNothingIsNotified()
    {
        _settings.Update(s => s.Widget = s.Widget with { LowBatteryAlert = false });
        using LowBatteryAlertService service = NewService();

        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow);

        Assert.AreEqual(0, _notifier.Calls.Count, "The alert is off: nothing must be notified.");
        Assert.AreEqual(LatchState.Fired, service.LeftLatchStateForTest, "The latch must still have been fed while the alert was off.");

        _settings.Update(s => s.Widget = s.Widget with { LowBatteryAlert = true });
        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow); // still the same low reading, not a new drop

        Assert.AreEqual(0, _notifier.Calls.Count, "Turning the alert back on must not itself re-fire for a part that never changed.");

        _status.Raise(Reading(left: 30), DateTimeOffset.UtcNow); // re-arms
        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow); // a genuine new drop, alert now on

        Assert.AreEqual(1, _notifier.Calls.Count, "A genuine new drop while the alert is on must notify.");
    }

    [TestMethod]
    public void AThresholdSettingChangeCallsSetThreshold()
    {
        using LowBatteryAlertService service = NewService();
        _status.Raise(Reading(left: 30), DateTimeOffset.UtcNow); // above the default 20% threshold: no fire
        Assert.AreEqual(0, _notifier.Calls.Count);

        _settings.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 30 });

        Assert.AreEqual(LatchState.Armed, service.LeftLatchStateForTest, "SetThreshold must have run: the latch is still Armed, ready for the new threshold.");

        _status.Raise(Reading(left: 30), DateTimeOffset.UtcNow); // now at the new threshold: fires

        Assert.AreEqual(1, _notifier.Calls.Count, "The new threshold (30) must actually be in effect on LowBatteryLatch.");
    }

    [TestMethod]
    public void DisposeStopsFeedingTheLatchAndNotifying()
    {
        LowBatteryAlertService service = NewService();
        service.Dispose();

        _status.Raise(Reading(left: 20), DateTimeOffset.UtcNow);

        Assert.AreEqual(0, _notifier.Calls.Count, "A disposed service must not react to further readings.");
    }
}
