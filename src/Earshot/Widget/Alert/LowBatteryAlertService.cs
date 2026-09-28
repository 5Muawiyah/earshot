using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.Widget.Alert;

// Wires IWidgetStatus.OwnedReadingApplied into LowBatteryLatch and, when a latch fires, into INotifier.
//
// The latch is fed on every owned reading regardless of WidgetSettings.LowBatteryAlert: only the actual
// notification call is gated on the setting. The alternative (skip feeding the latch while the setting is
// off) would leave a part that has been low the whole time silent the instant the owner turns the alert
// back on, since the latch would still read Armed and need a fresh 10% step to fire; feeding it always keeps
// its Armed/Fired bookkeeping exactly in step with the real battery, so the owner sees the state the setting
// controls, nothing about the latch itself.
internal sealed class LowBatteryAlertService : IDisposable
{
    private readonly IWidgetStatus _status;
    private readonly ISettingsStore _settings;
    private readonly INotifier _notifier;
    private readonly LowBatteryLatch _latch;
    private bool _disposed;

    public LowBatteryAlertService(IWidgetStatus status, ISettingsStore settings, INotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifier);

        _status = status;
        _settings = settings;
        _notifier = notifier;
        _latch = new LowBatteryLatch(settings.Current.Widget.LowBatteryThresholdPercent);

        _status.OwnedReadingApplied += OnOwnedReadingApplied;
        _settings.Changed += OnSettingsChanged;
    }

    // For LowBatteryAlertServiceTests: the latch's own state, read only. Never fed from outside this class.
    internal LatchState LeftLatchStateForTest => _latch.Left;

    internal LatchState RightLatchStateForTest => _latch.Right;

    internal LatchState CaseLatchStateForTest => _latch.Case;

    // For CompositionRootLowBatteryAlertTests: which notifier CompositionRoot.BuildLowBatteryAlertService
    // actually composed, so a test can prove safe mode and a redirected data root never construct a real
    // ToastNotifier without ever calling NotifyAsync (which, for a real ToastNotifier, would attempt the real
    // WinRT call).
    internal INotifier NotifierForTest => _notifier;

    private void OnSettingsChanged(object? sender, EarshotSettings settings) =>
        _latch.SetThreshold(settings.Widget.LowBatteryThresholdPercent);

    private void OnOwnedReadingApplied(object? sender, OwnedReadingEventArgs e)
    {
        DecodedReading reading = e.Reading;
        bool alertOn = _settings.Current.Widget.LowBatteryAlert;

        if (_latch.ApplyLeft(reading.Left.Percent) && alertOn && reading.Left.Percent is int left)
        {
            Notify(WidgetCopy.LowBatteryLeftText(left));
        }

        if (_latch.ApplyRight(reading.Right.Percent) && alertOn && reading.Right.Percent is int right)
        {
            Notify(WidgetCopy.LowBatteryRightText(right));
        }

        if (_latch.ApplyCase(reading.Case.Percent) && alertOn && reading.Case.Percent is int box)
        {
            Notify(WidgetCopy.LowBatteryCaseText(box));
        }
    }

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
        _status.OwnedReadingApplied -= OnOwnedReadingApplied;
        _settings.Changed -= OnSettingsChanged;
    }
}
