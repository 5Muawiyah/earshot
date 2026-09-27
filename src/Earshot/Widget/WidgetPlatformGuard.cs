using System.Runtime.Versioning;

namespace Earshot.Widget;

// The CA1416 guard for the widget's WinRT calls, written the way Earshot.Streaming.PlatformGuard is:
// BluetoothLEAdvertisementWatcher and ToastNotificationManager.CreateToastNotifier(String) both need
// Windows 10, version 2004 (10.0.19041.0), and Directory.Build.props keeps the analyser's floor below that,
// so every call site still needs the guard.
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
