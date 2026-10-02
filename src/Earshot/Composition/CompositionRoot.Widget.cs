using Earshot.Battery;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Earshot.Widget.EarPause;

namespace Earshot.Composition;

// Builds the widget's data pipeline and wires it into the registry, gated by WidgetSettings.Enabled: off
// (the default is on, but an owner can turn it off), nothing is built, IWidgetStatus and MediaSessions
// stay null, and no WinRT type or source is touched.
//
// Unlike every other Configure* hook, this one is not called from CompositionRoot.Build: the status
// service needs a BootBlockStatus reader, and the block coordinator that provides one is built after the
// registry, from the registry itself. TrayContext calls BuildWidget from its own constructor, once the
// coordinator exists, and owns starting, suspending, resuming and closing the result.
//
// Auto-pause's live wiring is built here too (BuildAutoPauseService), now that IWidgetStatus carries
// ReadingApplied: the render container ids and Where still come from IDeviceMonitor/IWidgetStatus's own public
// surface, so composition needs nothing WidgetStatusService does not already publish.
internal static partial class CompositionRoot
{
    // Returns the concrete service, not just IWidgetStatus: Start, Suspend, Resume and Close are not on
    // that interface (WidgetStatusService's own header explains why), and TrayContext needs them for the
    // widget's lifecycle. registry.WidgetStatus is also set, to the same instance, for the UI side.
    //
    // advertisementSourceFactory: null (every production caller) means the real WinRtAdvertisementSource;
    // TrayStartOptions.AdvertisementSourceFactory is how a widget-enabled tray-level test supplies a fake
    // instead, so building the widget's data pipeline in a test never starts a real Bluetooth watcher
    // (WidgetRealSurfaceGuardTests).
    internal static WidgetStatusService? BuildWidget(
        ServiceRegistry r, Func<BootBlockStatus?> blockStatus, TimeProvider time, Func<IAdvertisementSource>? advertisementSourceFactory = null,
        IPairedModelSource? pairedModel = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(time);

        if (!r.Settings.Current.Widget.Enabled)
        {
            return null;
        }

        // The setter already wraps this in safe mode (ServiceRegistry.MediaSessions), the same way
        // Connection, Block and Protection are wrapped on assignment.
        r.MediaSessions = new WindowsMediaSessions(r.Log);

        // The paired AirPods' model is read from the pinned device's own nodes; it picks the candidates out of the
        // broadcast. Windows' own Hands-Free figure comes from the registry's battery provider when that is one that
        // reads it. Nothing of the link is written; the owner's pair's last readings and learned charge rates are kept in
        // the widget folder (Paths.LastReadingFile, under EARSHOT_DATA_ROOT when that is set), values and times only.
        var status = new WidgetStatusService(
            advertisementSourceFactory ?? (static () => new WinRtAdvertisementSource()),
            r.Settings,
            r.Monitor,
            blockStatus,
            r.Log,
            r.UiPost,
            time,
            pairedModel ?? new NodePairedModelSource(),
            r.Battery as IHandsFreeBatterySource,
            new LastReadingStore(Paths.Current.LastReadingFile, r.Log),
            new HistoryStore(Paths.Current.BatteryHistoryFile, r.Log, time));
        r.WidgetStatus = status;
        return status;
    }

    // The low battery alert's real notifier chain: a toast, falling back to the card the tray already shows
    // its other one-off notices through; in safe mode or with a redirected data root the whole notifier is
    // that card alone, so a test run or a safe-mode run never writes a Start menu shortcut or shows a real
    // toast (the same safe-mode/redirected-root facts BuildWidget itself and NotificationRegistration read).
    internal static LowBatteryAlertService BuildLowBatteryAlertService(ServiceRegistry r, IWidgetStatus status, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(time);

        INotifier notifier = r.SafeMode || Paths.Current.IsRedirected
            ? new CardNotifier(r.Cards)
            : new ToastNotifier(NotificationRegistration.AppUserModelId, new CardNotifier(r.Cards), r.Log);

        // The fully charged notice's spent parts are kept in the status service's own store, beside the last readings.
        return new LowBatteryAlertService(status, r.Settings, notifier, time, (status as WidgetStatusService)?.SpentStore);
    }

    // Auto-pause's live wiring: AutoPause itself (r.MediaSessions, wrapped in safe mode by the registry's
    // own setter, and the AutoPause setting) plus AutoPauseService, which feeds it from every
    // ReadingApplied event. Only ever called once BuildWidget has already run and found the widget
    // enabled, which is the one thing that sets r.MediaSessions; a null MediaSessions here means something
    // upstream is already broken, so it is asserted rather than silently no-op'd.
    internal static AutoPauseService BuildAutoPauseService(
        ServiceRegistry r, IWidgetStatus status, Func<BootBlockStatus?> blockStatus, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(r.MediaSessions);

        var autoPause = new AutoPause(r.MediaSessions, () => r.Settings.Current.Widget.AutoPause, r.Log);
        return new AutoPauseService(status, r.Monitor, blockStatus, r.Settings, autoPause, time, r.Log);
    }
}
