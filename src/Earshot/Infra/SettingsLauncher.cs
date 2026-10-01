using System.Globalization;
using System.Runtime.Versioning;
using Earshot.Contracts;

namespace Earshot.Infra;

// Opens a page of Windows Settings. The one real implementation calls Launcher.LaunchUriAsync, which the documentation
// shows for ms-settings: links in desktop apps; a safe-mode decorator refuses; tests use a fake, so nothing in a test
// run opens a window.
// https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
internal interface ISettingsLauncher
{
    // True when Windows took the request. Never throws for a refusal or a failed launch: both answer false, with the
    // reason in the log.
    Task<bool> OpenAsync(string uri, CancellationToken ct);
}

internal sealed class WindowsSettingsLauncher : ISettingsLauncher
{
    public const string Scheme = "ms-settings";

    // Launcher.LaunchUriAsync is documented from the first Windows 10 build (10240). 19041 is this project's own build
    // floor, used for every WinRT guard in the solution so they all check the same version.
    // https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchuriasync
    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    private static bool HasLauncher { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    private readonly ILog _log;

    public WindowsSettingsLauncher(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Only a Settings link is ever opened: this is a way into Windows Settings, not a general launcher.
    public static bool IsSettingsUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> OpenAsync(string uri, CancellationToken ct)
    {
        if (!IsSettingsUri(uri))
        {
            _log.Warn("Sound settings: refused to open a link that is not a Windows Settings link.");
            return false;
        }

        if (!HasLauncher)
        {
            _log.Warn("Sound settings: this version of Windows cannot open Settings from here.");
            return false;
        }

        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Sound settings: Windows did not open Settings (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + ").");
            return false;
        }
    }
}

// Safe mode, and a run whose data lives somewhere other than the real folder: nothing is opened, and the refusal is logged.
internal sealed class SafeSettingsLauncher : ISettingsLauncher
{
    public const string Message = "Safe mode: no device actions.";

    private readonly ILog _log;

    public SafeSettingsLauncher(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public Task<bool> OpenAsync(string uri, CancellationToken ct)
    {
        _log.Warn(Message + " Refused: open sound settings.");
        return Task.FromResult(false);
    }
}
