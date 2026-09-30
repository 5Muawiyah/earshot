using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;

namespace Earshot.Widget.Alert;

// What an existing shortcut currently targets and carries, read back before NotificationRegistration
// decides whether to touch it.
internal readonly record struct ExistingShortcut(string TargetPath, string? AppUserModelId);

// NotFound: no shortcut is there yet, safe to write one. Ok: read cleanly, Existing is set. Unreadable: the
// file is there but a COM call failed partway through reading it; Failure carries the raw HRESULT. NotFound
// and Unreadable are deliberately different outcomes: only NotFound may be overwritten without a second
// thought, since a shortcut that exists but could not be parsed might still be someone else's file this
// build simply failed to read.
internal enum ShortcutReadStatus { NotFound, Ok, Unreadable }

internal readonly record struct ShortcutRead(ShortcutReadStatus Status, ExistingShortcut? Existing, StepOutcome? Failure);

// Writes the per-user Start menu shortcut the toast route's AppUserModelID needs, and reads one back so the
// caller can decide whether it already needs no change. Real: CoCreateInstance(CLSID_ShellLink), the way
// Interop\CoreAudio.cs creates the Core Audio enumerator; a test fake stands in for it so no test ever
// writes or reads a real shortcut.
internal interface IShellLinkWriter
{
    // Writes the shortcut so it targets targetPath and carries appUserModelId. Never throws for anything
    // Windows did. The caller (NotificationRegistration) decides whether to call this at all: it reads the
    // existing shortcut first and leaves an already-correct one, or one it could not read, alone.
    StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId);

    // What shortcutPath currently targets and carries. Never throws for anything Windows did; every failure
    // to read an existing file is reported through Unreadable and its HRESULT, never folded into NotFound.
    ShortcutRead ReadShortcut(string shortcutPath);
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

    private static readonly ShortcutRead NotFound = new(ShortcutReadStatus.NotFound, null, null);

    public ShortcutRead ReadShortcut(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);

        if (!WidgetPlatformGuard.HasToastNotifications || !File.Exists(shortcutPath))
        {
            return NotFound;
        }

        return ReadGuarded(shortcutPath);
    }

    private static ShortcutRead Unreadable(string step, int hr) =>
        new(ShortcutReadStatus.Unreadable, null, StepOutcomes.FromHResult(step, hr, ok: false));

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static ShortcutRead ReadGuarded(string shortcutPath)
    {
        int hr = ComActivation.Create(ShellLinkCom.CLSID_ShellLink, ComActivation.CLSCTX_INPROC_SERVER, out IShellLinkW? link);
        if (hr < 0 || link is null)
        {
            return Unreadable("shortcut-read-create", hr);
        }

        try
        {
            if (link is not IPersistFile file)
            {
                return Unreadable("shortcut-read-persist-file", CoreAudio.E_NOINTERFACE);
            }

            int loadHr = file.Load(shortcutPath, ShellLinkCom.STGM_READ);
            if (loadHr < 0)
            {
                return Unreadable("shortcut-read-load", loadHr);
            }

            var pathBuilder = new StringBuilder(ShellLinkCom.MAX_PATH);
            int getPathHr = link.GetPath(pathBuilder, ShellLinkCom.MAX_PATH, 0, 0);
            if (getPathHr < 0)
            {
                return Unreadable("shortcut-read-getpath", getPathHr);
            }

            string? appUserModelId = null;
            if (link is IPropertyStore store)
            {
                var read = PropVariantInterop.ReadString(store, ShellLinkCom.PKEY_AppUserModel_ID);
                if (read.HasValue)
                {
                    appUserModelId = read.Value;
                }
            }

            return new ShortcutRead(ShortcutReadStatus.Ok, new ExistingShortcut(pathBuilder.ToString(), appUserModelId), null);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}

// The shortcut's own path, its target and its AppUserModelID, and when it must not be touched. Run once at
// tray start, idempotent: an existing shortcut is read first; one that already targets the right exe and
// carries the right AppUserModelID is left alone; one that targets a different path this run itself knows as
// Earshot's (a stale install location) is rewritten; one whose target is neither is left alone and logged,
// since it is not this application's shortcut to overwrite.
internal sealed class NotificationRegistration
{
    // CompanyName.ProductName, no spaces, under 128 characters, no version part so an upgrade keeps it.
    // https://learn.microsoft.com/en-us/windows/win32/shell/appids
    public const string AppUserModelId = "5Muawiyah.Earshot";
    public const string ShortcutFileName = "Earshot.lnk";

    public const string SafeModeMessage = "Safe mode: the notification shortcut was not written.";
    public const string TestFolderMessage = "Test data folder: the notification shortcut was not written.";
    public const string NoExePathMessage = "Earshot could not find its own program file.";
    public const string InstallDamagedMessage = "Earshot is installed but its program is missing, so the notification shortcut was not written. Choose Repair Earshot.";
    public const string NoProgramsFolderMessage = "The Start menu Programs folder was not found; the notification shortcut was not written.";

    private readonly IShellLinkWriter _writer;
    private readonly ILog _log;
    private readonly bool _safeMode;
    private readonly bool _redirected;
    private readonly string? _runningExePath;
    private readonly string? _installedExePath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _folderExists;

    public NotificationRegistration(
        IShellLinkWriter writer,
        ILog log,
        bool safeMode,
        bool redirected,
        string shortcutFolder,
        string? runningExePath,
        string? installedExePath = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? folderExists = null)
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
        _folderExists = folderExists ?? Directory.Exists;
        ShortcutPath = Path.Combine(shortcutFolder, ShortcutFileName);
    }

    // Environment.GetFolderPath(SpecialFolder.Programs) returns an empty string, rather than throwing, when
    // the folder does not exist and no SpecialFolderOption asked it to be created
    // (https://learn.microsoft.com/en-us/dotnet/api/system.environment.getfolderpath): the constructor
    // above still rejects that outright (ArgumentException.ThrowIfNullOrWhiteSpace), a real programming
    // error for any other caller, but the one call site that feeds it straight from GetFolderPath must not
    // let a missing folder abort the whole tray. Returns null, and logs, instead of constructing.
    public static NotificationRegistration? TryCreate(
        IShellLinkWriter writer,
        ILog log,
        bool safeMode,
        bool redirected,
        string? shortcutFolder,
        string? runningExePath,
        string? installedExePath = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? folderExists = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (string.IsNullOrWhiteSpace(shortcutFolder))
        {
            log.Warn(NoProgramsFolderMessage);
            return null;
        }

        return new NotificationRegistration(writer, log, safeMode, redirected, shortcutFolder, runningExePath, installedExePath, fileExists, folderExists);
    }

    public string ShortcutPath { get; }

    // The file the shortcut targets: the installed copy when there is one, otherwise this copy, the same
    // preference StartupRegistration has, and nothing when an install exists whose program is missing and this is not it.
    public string? TargetExePath =>
        !string.IsNullOrWhiteSpace(_installedExePath) && _fileExists(_installedExePath) ? _installedExePath
        : InstallDamaged ? null
        : _runningExePath;

    // An install is there, but its program is not, and this copy is not the installed one.
    public bool InstallDamaged =>
        !string.IsNullOrWhiteSpace(_installedExePath) && !_fileExists(_installedExePath) &&
        !PathsEqual(_installedExePath, _runningExePath) &&
        Path.GetDirectoryName(_installedExePath) is { Length: > 0 } folder && _folderExists(folder);

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
            if (InstallDamaged)
            {
                _log.Warn(InstallDamagedMessage);
                return StepOutcomes.NotAttempted("shortcut-write", InstallDamagedMessage);
            }

            _log.Error(NoExePathMessage + " The notification shortcut was not written.");
            return StepOutcomes.NotAttempted("shortcut-write", NoExePathMessage);
        }

        ShortcutRead existing = _writer.ReadShortcut(ShortcutPath);
        if (existing.Status == ShortcutReadStatus.Unreadable)
        {
            StepOutcome failure = existing.Failure!;
            _log.Warn(
                "Earshot.lnk exists but could not be read (" + failure.CodeName + ", 0x" + failure.Code.ToString("X8") +
                "): left alone, registration skipped.");
            return StepOutcomes.NotAttempted("shortcut-write", "An existing shortcut could not be read and was left alone.");
        }

        if (existing.Status == ShortcutReadStatus.Ok)
        {
            ExistingShortcut current = existing.Existing!.Value;
            if (PathsEqual(current.TargetPath, exePath) && current.AppUserModelId == AppUserModelId)
            {
                _log.Info("Notification shortcut already targets " + exePath + " with the right id: left alone.");
                return StepOutcomes.FromHResult("shortcut-write", 0, detail: "Already correct: left alone.");
            }

            if (!IsOurs(current.TargetPath))
            {
                _log.Warn(
                    "Earshot.lnk already exists and targets " + current.TargetPath + ", not Earshot: left alone.");
                return StepOutcomes.NotAttempted("shortcut-write", "An existing shortcut with a different target was left alone.");
            }
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

    // "Ours" means the existing shortcut's target is a path this run itself knows as Earshot's, whether
    // or not it is today's preferred one (installed over running), so a stale-but-still-Earshot target is
    // still safe to rewrite; anything else is never overwritten.
    private bool IsOurs(string existingTarget) =>
        PathsEqual(existingTarget, _runningExePath) || PathsEqual(existingTarget, _installedExePath);

    private static bool PathsEqual(string? a, string? b) => SamePath.AreEqual(a, b);
}
