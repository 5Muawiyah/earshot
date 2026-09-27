using System.Runtime.Versioning;

namespace Earshot.Widget;

// The CA1416 guard for the widget's WinRT calls, written the way Earshot.Streaming.PlatformGuard is.
// BluetoothLEAdvertisementWatcher and ToastNotificationManager.CreateToastNotifier(String) are both
// documented since the original Windows 10 SDK, build 10240; 10.0.19041.0 is not an API requirement, it is
// this project's own build floor (the TargetFramework moniker's Windows version, chosen for v1.1's audio
// streaming), used here too so every WinRT guard in the solution checks the same version. Directory.Build.props
// keeps the analyser's own floor below that (SupportedOSPlatformVersion 7.0), so every call site still needs
// a guard regardless.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager.createtoastnotifier
internal static class WidgetPlatformGuard
{
    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    internal static bool HasBleWatcher { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    internal static bool HasToastNotifications { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    // GlobalSystemMediaTransportControlsSessionManager needs Windows 10, version 1809 (10.0.17763.0).
    // https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager
    [SupportedOSPlatformGuard("windows10.0.17763.0")]
    internal static bool HasMediaSessions { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
}
