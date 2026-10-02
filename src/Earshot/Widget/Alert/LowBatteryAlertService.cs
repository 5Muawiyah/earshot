using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.Widget.Alert;

// Wires IWidgetStatus.Changed into LowBatteryLatch and, when a latch fires, into INotifier. The alert acts on what
// the widget shows: every Changed, the same BatteryFreshness.Shown the card and the gauge read gives the values,
// and only a value that is fresh feeds a latch. Left, Right and the case are fed only while their own broadcast
// value is fresh; Windows' own figure, one number for the headset, is fed only while it is what is shown. A greyed
// value feeds nothing: it neither alerts nor re-arms, so a low value that greys and returns does not alert again.
//
// The latch is fed regardless of WidgetSettings.LowBatteryAlert: only the actual notification call is gated on
// the setting. Feeding it always, rather than only while the alert is on, is not what protects a part that
// crossed while the alert was off: LowBatteryLatch.Apply fires from Armed the moment a reading is at or below
// the threshold, no 10% margin needed for that, so skipping the latch while the alert is off and feeding it
// again once the owner turns it back on would still catch that on the very next reading. What feeding it always
// actually buys is closer to the opposite: a part that fires while the alert is off does so silently (Fired, no
// notification), and Fired needs a reading at least 10% back above the threshold before it can fire again - so a
// part that has stayed low the whole time the alert was off does not, by itself, produce a notification the
// moment the owner turns the alert back on. Kept this way anyway, so the latch's own Armed/Fired bookkeeping
// always matches the real battery independent of the setting; the owner sees whatever state the setting
// controls, nothing about the latch's own history.
internal sealed class LowBatteryAlertService : IDisposable
{
    private readonly IWidgetStatus _status;
    private readonly ISettingsStore _settings;
    private readonly INotifier _notifier;
    private readonly TimeProvider _time;
    private readonly LowBatteryLatch _latch;
    private readonly FullyChargedLatch _fullLatch = new();
    private readonly ISpentStore? _spentStore;
    private readonly SpentMark?[] _spentMarks = new SpentMark?[3];
    private readonly bool[] _spentSeeded = new bool[3];
    private bool _disposed;

    // spentStore, when given, keeps which parts have had their fully charged notice across restarts (SpentMark): without it
    // the latch is in memory only.
    public LowBatteryAlertService(IWidgetStatus status, ISettingsStore settings, INotifier notifier, TimeProvider time, ISpentStore? spentStore = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(time);

        _status = status;
        _settings = settings;
        _notifier = notifier;
        _time = time;
        _latch = new LowBatteryLatch(settings.Current.Widget.LowBatteryThresholdPercent);
        _spentStore = spentStore;
        foreach (SpentMark mark in spentStore?.LoadSpent() ?? [])
        {
            _spentMarks[(int)mark.Component] = mark;
        }

        _status.Changed += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
    }

    // For LowBatteryAlertServiceTests: the latch's own state, read only. Never fed from outside this class.
    internal LatchState LeftLatchStateForTest => _latch.Left;

    internal LatchState RightLatchStateForTest => _latch.Right;

    internal LatchState CaseLatchStateForTest => _latch.Case;

    internal LatchState HeadsetLatchStateForTest => _latch.Headset;

    // For CompositionRootLowBatteryAlertTests: which notifier CompositionRoot.BuildLowBatteryAlertService
    // actually composed, so a test can prove safe mode and a redirected data root never construct a real
    // ToastNotifier without ever calling NotifyAsync (which, for a real ToastNotifier, would attempt the real
    // WinRT call).
    internal INotifier NotifierForTest => _notifier;

    private void OnSettingsChanged(object? sender, EarshotSettings settings) =>
        _latch.SetThreshold(settings.Widget.LowBatteryThresholdPercent);

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        WidgetSnapshot snapshot = _status.Current;
        ShownBattery shown = BatteryFreshness.Shown(snapshot, _time.GetUtcNow());
        bool alertOn = _settings.Current.Widget.LowBatteryAlert;

        // The fully charged notice reads the same shown values, wherever the pair is: a case charging on a desk, away from this
        // PC, is the use for an estimate reaching 100. Fed always, notified only while the setting is on.
        bool fullOn = _settings.Current.Widget.FullyChargedNotice;
        NotifyFull(ChargeComponent.Left, shown.Left, fullOn, WidgetCopy.FullyChargedLeftText);
        NotifyFull(ChargeComponent.Right, shown.Right, fullOn, WidgetCopy.FullyChargedRightText);
        NotifyFull(ChargeComponent.Case, shown.Case, fullOn, WidgetCopy.FullyChargedCaseText);

        // The card and the gauge show the owner's pair wherever it is; the alert stays for AirPods on this PC, as it was:
        // a live value of a pair that is not here is not one the person is listening on.
        if (snapshot.Where != AirPodsWhere.ThisPc)
        {
            shown = ShownBattery.None;
        }

        if (_latch.ApplyLeft(FreshPercent(shown.Left)) && alertOn && shown.Left.Percent is int left)
        {
            Notify(WidgetCopy.LowBatteryLeftText(left));
        }

        if (_latch.ApplyRight(FreshPercent(shown.Right)) && alertOn && shown.Right.Percent is int right)
        {
            Notify(WidgetCopy.LowBatteryRightText(right));
        }

        if (_latch.ApplyCase(FreshPercent(shown.Case)) && alertOn && shown.Case.Percent is int box)
        {
            Notify(WidgetCopy.LowBatteryCaseText(box));
        }

        if (_latch.ApplyHeadset(shown.WindowsPercent) && alertOn && shown.WindowsPercent is int headset)
        {
            Notify(WidgetCopy.LowBatteryHeadsetText(headset));
        }
    }

    // Feeds one part to the fully charged latch and keeps the spent marks beside it: a part saved as spent on the reading it is
    // shown from is spent again at the first look (a restart is not a new charge), and a change of the latch is written.
    private void NotifyFull(ChargeComponent component, ShownPart part, bool on, Func<bool, string> text)
    {
        int index = (int)component;
        SpentMark? key = part is { HasValue: true, ReadAt: DateTimeOffset readAt, Percent: int percent }
            ? new SpentMark(component, readAt, part.ReadPercent ?? percent)
            : null;

        if (!_spentSeeded[index] && key is not null)
        {
            _spentSeeded[index] = true;
            if (_spentMarks[index] == key)
            {
                _fullLatch.Spend(component);
            }
        }

        bool wasSpent = _fullLatch.IsSpent(component);
        FullyChargedStep step = _fullLatch.Apply(component, part);
        bool isSpent = _fullLatch.IsSpent(component);
        if (isSpent && !wasSpent && key is not null)
        {
            SetSpentMark(index, key);
        }
        else if (!isSpent && wasSpent)
        {
            SetSpentMark(index, null);
        }

        if (step != FullyChargedStep.None && on)
        {
            Notify(text(step == FullyChargedStep.Estimated));
        }
    }

    private void SetSpentMark(int index, SpentMark? mark)
    {
        _spentMarks[index] = mark;
        _spentStore?.SaveSpent(_spentMarks.OfType<SpentMark>().ToList());
    }

    // A greyed value is not what is shown as current, so it feeds nothing.
    private static int? FreshPercent(ShownPart part) => part.Fresh ? part.Percent : null;

    // Fire and forget, the same way every other event-driven async call in this codebase is (BlockCoordinator,
    // TrayContext): INotifier's own two implementations never throw for anything Windows did, so there is
    // nothing here for a silent catch to hide.
    private void Notify(string text) => _ = _notifier.NotifyAsync(TrayStatus.AppName, text);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _status.Changed -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
