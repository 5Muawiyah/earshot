using Earshot.Infra;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The fully charged notice through the alert service: what is shown (BatteryFreshness.Shown) feeds one latch per part, a
// live or an estimated 100 notifies once per charge, and the setting gates only the notification.
[TestClass]
public sealed class FullyChargedNoticeTests : IDisposable
{
    private const ushort Model = 0x2027;

    private sealed class FakeWidgetStatus : IWidgetStatus
    {
        public WidgetSnapshot Current { get; set; } = WidgetSnapshot.Empty(WidgetWatcherState.Started);

        public event EventHandler? Changed;

        public event EventHandler<CaseOpenedEventArgs>? CaseOpened { add { } remove { } }

        public event EventHandler<CaseClosedEventArgs>? CaseClosed { add { } remove { } }

        public event EventHandler<ReadingAppliedEventArgs>? ReadingApplied { add { } remove { } }

        public Task RefreshAsync() => Task.CompletedTask;

        public Task<BatteryRefreshOutcome> RefreshBatteryAsync(CancellationToken ct) => Task.FromResult(BatteryRefreshOutcome.NotListening);

        public void Raise(WidgetSnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();
    private readonly FakeWidgetStatus _status = new();
    private readonly FakeNotifier _notifier = new();
    private readonly TestTimeProvider _clock = new();
    private readonly JsonSettingsStore _settings;

    public FullyChargedNoticeTests()
    {
        _settings = new JsonSettingsStore(_temp.File("settings.json"), _log);
    }

    public void Dispose() => _temp.Dispose();

    private LowBatteryAlertService NewService() => new(_status, _settings, _notifier, _clock);

    private static PartReading Heard(int? percent, DateTimeOffset at, bool charging = true) =>
        percent is null ? PartReading.Unknown : new PartReading(percent, charging, null) { ReadAt = at };

    // A linked set heard a second ago.
    private WidgetSnapshot Live(int? left = null, int? right = null, int? box = null, bool charging = true)
    {
        DateTimeOffset at = _clock.GetUtcNow() - TimeSpan.FromSeconds(1);
        return WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = AirPodsWhere.ThisPc,
            Selection = BroadcastSelectionState.Linked,
            PairedModel = Model,
            Left = Heard(left, at, charging),
            Right = Heard(right, at, charging),
            Case = Heard(box, at, charging),
        };
    }

    // Nothing linked: only what was saved is shown, by the estimate when the part was charging and a rate is known.
    private WidgetSnapshot Saved(ChargeComponent part, int percent, bool charging, TimeSpan ago, ushort model = Model, bool withRate = true)
    {
        DateTimeOffset at = _clock.GetUtcNow() - ago;
        var reading = new SavedReading(percent, charging, at, model);
        var book = new LastReadingBook(
            part == ChargeComponent.Left ? reading : null,
            part == ChargeComponent.Right ? reading : null,
            part == ChargeComponent.Case ? reading : null,
            withRate
                ? [new LearnedRate(model, ChargePart.Case, 30, at, TimeSpan.FromMinutes(20)), new LearnedRate(model, ChargePart.Bud, 30, at, TimeSpan.FromMinutes(20))]
                : Array.Empty<LearnedRate>());
        return WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = AirPodsWhere.NotInUse,
            Selection = BroadcastSelectionState.Listening,
            PairedModel = Model,
            LastReadings = book,
        };
    }

    [TestMethod]
    public void ALiveHundredNotifiesOnce()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Live(left: 100));
        _status.Raise(Live(left: 100));
        _status.Raise(Live(left: 100));

        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(("Earshot", "Left AirPod at 100%, fully charged"), _notifier.Calls[0]);
    }

    [TestMethod]
    public void EachPartNotifiesOnItsOwnWithItsOwnText()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Live(left: 100, right: 99, box: 100));
        _status.Raise(Live(left: 100, right: 100, box: 100));

        Assert.AreEqual(3, _notifier.Calls.Count);
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Left AirPod at 100%, fully charged"));
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Right AirPod at 100%, fully charged"));
        CollectionAssert.Contains(_notifier.Calls, ("Earshot", "Case at 100%, fully charged"));
    }

    [TestMethod]
    public void AnEstimatedHundredNotifiesOnceAndIsMarked()
    {
        using LowBatteryAlertService service = NewService();

        // 80 percent at 30 points an hour reaches 100 in 40 minutes; 41 minutes on, the estimate is 100.
        _status.Raise(Saved(ChargeComponent.Case, 80, charging: true, ago: TimeSpan.FromMinutes(20)));
        Assert.AreEqual(0, _notifier.Calls.Count, "An estimate of 90 is not 100.");

        _status.Raise(Saved(ChargeComponent.Case, 80, charging: true, ago: TimeSpan.FromMinutes(41)));
        _status.Raise(Saved(ChargeComponent.Case, 80, charging: true, ago: TimeSpan.FromMinutes(45)));

        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(("Earshot", "Case ≈100%, estimated"), _notifier.Calls[0]);
    }

    [TestMethod]
    public void AnEstimateThatReachesHundredAndALiveHundredAfterItAreOneNotice()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Saved(ChargeComponent.Case, 80, charging: true, ago: TimeSpan.FromMinutes(41)));
        _status.Raise(Live(box: 100));
        _status.Raise(Live(box: 100));

        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(("Earshot", "Case ≈100%, estimated"), _notifier.Calls[0]);
    }

    [TestMethod]
    public void ALiveReadingBelowHundredRearmsAndTheNextHundredIsAnotherCharge()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Live(left: 100));
        _status.Raise(Live(left: 95, charging: false));
        _status.Raise(Live(left: 100));

        Assert.AreEqual(2, _notifier.Calls.Count);
    }

    // A full part may report not charging; that is not a new charge and must not notify again at once.
    [TestMethod]
    public void ALiveHundredThatIsNotChargingDoesNotRearm()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Live(left: 100, charging: true));
        _status.Raise(Live(left: 100, charging: false));
        _status.Raise(Live(left: 100, charging: true));

        Assert.AreEqual(1, _notifier.Calls.Count);
    }

    // Neither an old reading nor an estimate says what the part is doing now, so neither arms it.
    [TestMethod]
    public void AnOldReadingBelowHundredDoesNotRearm()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Live(box: 100));
        _status.Raise(Saved(ChargeComponent.Case, 70, charging: false, ago: TimeSpan.FromHours(3)));
        _status.Raise(Live(box: 100));

        Assert.AreEqual(1, _notifier.Calls.Count);
    }

    // A last reading of 100 was already full before this run began: not news, and the live 100 that follows is the same charge.
    [TestMethod]
    public void ASavedHundredNotifiesNothingAndNeitherDoesTheLiveHundredAfterIt()
    {
        using LowBatteryAlertService service = NewService();

        _status.Raise(Saved(ChargeComponent.Case, 100, charging: false, ago: TimeSpan.FromHours(2)));
        _status.Raise(Live(box: 100));

        Assert.AreEqual(0, _notifier.Calls.Count);
    }

    [TestMethod]
    public void WithTheSettingOffNothingIsNotifiedAndTurningItOnDoesNotNotifyForAFullPart()
    {
        _settings.Update(s => s.Widget = s.Widget with { FullyChargedNotice = false });
        using LowBatteryAlertService service = NewService();

        _status.Raise(Live(left: 100, right: 100, box: 100));
        _status.Raise(Saved(ChargeComponent.Case, 80, charging: true, ago: TimeSpan.FromMinutes(41)));
        Assert.AreEqual(0, _notifier.Calls.Count);

        _settings.Update(s => s.Widget = s.Widget with { FullyChargedNotice = true });
        _status.Raise(Live(left: 100, right: 100, box: 100));
        Assert.AreEqual(0, _notifier.Calls.Count, "The parts filled while it was off; it is not news now.");

        _status.Raise(Live(left: 90));
        _status.Raise(Live(left: 100));
        Assert.AreEqual(1, _notifier.Calls.Count, "A new charge while it is on notifies.");
    }

    [TestMethod]
    public void TheSettingIsOnByDefault() =>
        Assert.IsTrue(WidgetSettings.Default.FullyChargedNotice);

    [TestMethod]
    public void AStrangerNeverNotifies()
    {
        using LowBatteryAlertService service = NewService();

        // A broadcast that is not linked to the owner's pair may be any pair's.
        foreach (BroadcastSelectionState state in new[] { BroadcastSelectionState.Listening, BroadcastSelectionState.NoPairedModel })
        {
            _status.Raise(Live(left: 100, right: 100, box: 100) with { Selection = state });
        }

        // A saved reading of another model is not the owner's pair's, charging or not.
        _status.Raise(Saved(ChargeComponent.Case, 80, charging: true, ago: TimeSpan.FromMinutes(41), model: 0x2014));
        _status.Raise(Saved(ChargeComponent.Left, 100, charging: true, ago: TimeSpan.FromMinutes(1), model: 0x2014));

        Assert.AreEqual(0, _notifier.Calls.Count);
    }
}
