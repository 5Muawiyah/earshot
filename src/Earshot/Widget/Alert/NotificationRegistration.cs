using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget.Alert;

// Writes (or leaves alone) the per-user Start menu shortcut the toast route's AppUserModelID needs. Real:
// CoCreateInstance(CLSID_ShellLink), the way Interop\CoreAudio.cs creates the Core Audio enumerator; a test
// fake stands in for it so no test ever writes a real shortcut.
internal interface IShellLinkWriter
{
    // Writes the shortcut so it targets targetPath and carries appUserModelId, or leaves it alone when it
    // already does both. Never throws for anything Windows did.
    StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId);
}

internal sealed class RealShellLinkWriter : IShellLinkWriter
{
    public StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);

        if (!WidgetPlatformGuard.HasToastNotifications)
        {
            return StepOutcomes.NotAvailable("shortcut-write", "This build of Windows has no shell link support to check.");
        }

        return WriteGuarded(shortcutPath, targetPath, appUserModelId);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static StepOutcome WriteGuarded(string shortcutPath, string targetPath, string appUserModelId)
    {
        int hr = ComActivation.Create(ShellLinkCom.CLSID_ShellLink, ComActivation.CLSCTX_INPROC_SERVER, out IShellLinkW? link);
        if (hr < 0 || link is null)
        {
            return StepOutcomes.FromHResult("shortcut-create", hr);
        }

        try
        {
            int setPathHr = link.SetPath(targetPath);
            if (setPathHr < 0)
            {
                return StepOutcomes.FromHResult("shortcut-set-path", setPathHr);
            }

            if (link is not IPropertyStore store)
            {
                return StepOutcomes.FromHResult("shortcut-property-store", CoreAudio.E_NOINTERFACE);
            }

            PROPERTYKEY key = ShellLinkCom.PKEY_AppUserModel_ID;
            var value = default(PROPVARIANT);
            value.vt = PropVariantInterop.VT_LPWSTR;
            value.pointerValue = Marshal.StringToCoTaskMemUni(appUserModelId);
            try
            {
                int setValueHr = store.SetValue(ref key, ref value);
                if (setValueHr < 0)
                {
                    return StepOutcomes.FromHResult("shortcut-set-appusermodelid", setValueHr);
                }
            }
            finally
            {
                PropVariantInterop.PropVariantClear(ref value);
            }

            int commitHr = store.Commit();
            if (commitHr < 0)
            {
                return StepOutcomes.FromHResult("shortcut-commit-property", commitHr);
            }

            if (link is not IPersistFile file)
            {
                return StepOutcomes.FromHResult("shortcut-persist-file", CoreAudio.E_NOINTERFACE);
            }

            int saveHr = file.Save(shortcutPath, fRemember: true);
            return StepOutcomes.FromHResult("shortcut-save", saveHr);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}

// The shortcut's own path, its target and its AppUserModelID, and when it must not be touched. Run once at
// tray start, idempotent: an existing shortcut with the same target and id is left alone by the writer, one
// with a different target is rewritten.
internal sealed class NotificationRegistration
{
    // CompanyName.ProductName, no spaces, under 128 characters, no version part so an upgrade keeps it.
    // https://learn.microsoft.com/en-us/windows/win32/shell/appids
    public const string AppUserModelId = "5Muawiyah.Earshot";
    public const string ShortcutFileName = "Earshot.lnk";

    public const string SafeModeMessage = "Safe mode: the notification shortcut was not written.";
    public const string TestFolderMessage = "Test data folder: the notification shortcut was not written.";
    public const string NoExePathMessage = "Earshot could not find its own program file.";

    private readonly IShellLinkWriter _writer;
    private readonly ILog _log;
    private readonly bool _safeMode;
    private readonly bool _redirected;
    private readonly string? _runningExePath;
    private readonly string? _installedExePath;
    private readonly Func<string, bool> _fileExists;

    public NotificationRegistration(
        IShellLinkWriter writer,
        ILog log,
        bool safeMode,
        bool redirected,
        string shortcutFolder,
        string? runningExePath,
        string? installedExePath = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutFolder);

        _writer = writer;
        _log = log;
        _safeMode = safeMode;
        _redirected = redirected;
        _runningExePath = runningExePath;
        _installedExePath = installedExePath;
        _fileExists = fileExists ?? File.Exists;
        ShortcutPath = Path.Combine(shortcutFolder, ShortcutFileName);
    }

    public string ShortcutPath { get; }

    // The file the shortcut targets: the installed copy when there is one, otherwise this copy, the same
    // preference StartupRegistration has.
    public string? TargetExePath =>
        !string.IsNullOrWhiteSpace(_installedExePath) && _fileExists(_installedExePath) ? _installedExePath : _runningExePath;

    // True when this run must not write the shortcut at all: a test run must never write one into the
    // owner's real Start menu.
    public bool WritesBlocked => _safeMode || _redirected;

    public string BlockedMessage => _safeMode ? SafeModeMessage : TestFolderMessage;

    public StepOutcome Register()
    {
        if (WritesBlocked)
        {
            string action = (_safeMode ? "safe-mode:" : "test-data-root:") + "shortcut-write";
            _log.Warn(BlockedMessage);
            return StepOutcomes.NotAttempted(action, BlockedMessage);
        }

        string? exePath = TargetExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _log.Error(NoExePathMessage + " The notification shortcut was not written.");
            return StepOutcomes.NotAttempted("shortcut-write", NoExePathMessage);
        }

        StepOutcome step = _writer.WriteShortcut(ShortcutPath, exePath, AppUserModelId);
        if (step.Ok)
        {
            _log.Info("Notification shortcut: " + ShortcutPath + " -> " + exePath);
        }
        else
        {
            _log.Warn("Could not write the notification shortcut " + ShortcutPath + ": " + step.CodeName +
                (step.Detail is null ? "" : " (" + step.Detail + ")"));
        }

        return step;
    }
}
