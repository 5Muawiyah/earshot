using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Earshot.Widget.EarPause;

namespace Earshot.Composition;

// Builds the widget's data pipeline and wires it into the registry, gated by WidgetSettings.Enabled: off
// (the default is on, but an owner can turn it off), nothing is built, IWidgetStatus and MediaSessions
// stay null, and no WinRT type, source or claim file is touched.
//
// Unlike every other Configure* hook, this one is not called from CompositionRoot.Build: the status
// service needs a BootBlockStatus reader, and the block coordinator that provides one is built after the
// registry, from the registry itself. TrayContext calls BuildWidget from its own constructor, once the
// coordinator exists, and owns starting, suspending, resuming and closing the result.
//
// Auto-pause's live wiring is built here too (BuildAutoPauseService), now that IWidgetStatus carries
// OwnedReadingApplied: the render container ids and Where still come from IDeviceMonitor/IWidgetStatus's
// own public surface, not from the ownership verdict itself, so composition needs nothing WidgetStatusService
// does not already publish.
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
        ServiceRegistry r, Func<BootBlockStatus?> blockStatus, TimeProvider time, Func<IAdvertisementSource>? advertisementSourceFactory = null)
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

        // A claim is trusted only with the set-up record it names beside it and agreeing with it.
        var setupRecords = new BatterySetupStore(WidgetSetupFolder(Paths.Current), r.Log);
        var claimStore = new ClaimStore(Paths.Current.WidgetClaimFile, r.Log, setupRecords.Load);

        // The decode table comes from the set-up records under the widget folder and nowhere else: there is
        // no constant to read, so what is proved is exactly what the owner's own set-ups proved.
        var proof = new DecodeProofStore(WidgetSetupFolder(Paths.Current), WidgetProofFile(Paths.Current), r.Log, time);
        var status = new WidgetStatusService(
            advertisementSourceFactory ?? (static () => new WinRtAdvertisementSource()),
            claimStore,
            r.Settings,
            r.Monitor,
            blockStatus,
            r.Log,
            r.UiPost,
            time,
            proof);
        r.WidgetStatus = status;
        return status;
    }

    // The set-up records and the proof summary sit beside claim.json, under the same data root.
    internal static string WidgetSetupFolder(Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.WidgetFolder, "setup");
    }

    internal static string WidgetProofFile(Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.WidgetFolder, "proof.json");
    }

    // The low battery alert's real notifier chain: a toast, falling back to the card the tray already shows
    // its other one-off notices through; in safe mode or with a redirected data root the whole notifier is
    // that card alone, so a test run or a safe-mode run never writes a Start menu shortcut or shows a real
    // toast (the same safe-mode/redirected-root facts BuildWidget itself and NotificationRegistration read).
    internal static LowBatteryAlertService BuildLowBatteryAlertService(ServiceRegistry r, IWidgetStatus status)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(status);

        INotifier notifier = r.SafeMode || Paths.Current.IsRedirected
            ? new CardNotifier(r.Cards)
            : new ToastNotifier(NotificationRegistration.AppUserModelId, new CardNotifier(r.Cards), r.Log);

        return new LowBatteryAlertService(status, r.Settings, notifier);
    }

    // Auto-pause's live wiring: AutoPause itself (r.MediaSessions, wrapped in safe mode by the registry's
    // own setter, and the AutoPause setting) plus AutoPauseService, which feeds it from every
    // OwnedReadingApplied event. Only ever called once BuildWidget has already run and found the widget
    // enabled, which is the one thing that sets r.MediaSessions; a null MediaSessions here means something
    // upstream is already broken, so it is asserted rather than silently no-op'd.
    internal static AutoPauseService BuildAutoPauseService(
        ServiceRegistry r, IWidgetStatus status, Func<BootBlockStatus?> blockStatus, TimeProvider time, Func<bool?> broadcastObserved)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(broadcastObserved);
        ArgumentNullException.ThrowIfNull(r.MediaSessions);

        var autoPause = new AutoPause(r.MediaSessions, broadcastObserved, () => r.Settings.Current.Widget.AutoPause, r.Log);
        return new AutoPauseService(status, r.Monitor, blockStatus, r.Settings, autoPause, time, r.Log);
    }
}
