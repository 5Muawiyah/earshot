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
    private bool _disposed;

    public LowBatteryAlertService(IWidgetStatus status, ISettingsStore settings, INotifier notifier, TimeProvider time)
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
        ShownBattery shown = BatteryFreshness.Shown(_status.Current, _time.GetUtcNow());
        bool alertOn = _settings.Current.Widget.LowBatteryAlert;

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
