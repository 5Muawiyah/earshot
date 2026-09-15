using System.Security;
using Earshot.Contracts;
using Microsoft.Win32;

namespace Earshot.Tray;

// The registry values Open on startup reads and writes. StartupApproved is read only: there is no
// member to write it, on purpose.
internal interface IStartupRegistry
{
    // HKCU\...\Run\<name> as a string, or null when it is missing or not a string.
    string? ReadRunValue(string name);

    // HKCU\...\Explorer\StartupApproved\Run\<name> as bytes, or null when it is missing or not binary.
    byte[]? ReadStartupApproved(string name);

    void WriteRunValue(string name, string command);

    // Does nothing when the value is missing.
    void DeleteRunValue(string name);
}

// The real HKCU, used only by the tray.
internal sealed class CurrentUserStartupRegistry : IStartupRegistry
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? ReadRunValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public byte[]? ReadStartupApproved(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: false);
        return key?.GetValue(name) as byte[];
    }

    public void WriteRunValue(string name, string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void DeleteRunValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

// Open on startup, through the current user's Run key. The tray owns this value: it runs unelevated,
// so it writes the right user's hive even under Administrator protection.
//
// The Run value is the quoted exe path plus --startup. The command line may be at most 260 characters,
// and an unquoted path with spaces could start a different program.
// https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
// https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
//
// When the user turns Earshot off in Settings or Task Manager, Windows records it under
// Explorer\StartupApproved\Run as binary data whose first byte has bit 0x1 set (0x02 is on, 0x03 is
// off). That format is undocumented, so Earshot only reads it, treats bit 0x1 as "turned off in
// Windows", and never writes it.
//
// In safe mode, and whenever EARSHOT_DATA_ROOT redirects Earshot's folders, nothing is written: the change is
// logged instead. A run against a test data folder is always a first run, whose default would otherwise point
// the owner's real Open on startup at that build.
internal sealed class StartupRegistration
{
    public const string ValueName = "Earshot";
    public const string StartupArgument = "--startup";
    public const int MaxCommandLength = 260;

    public const string SafeModeMessage = "Safe mode: startup setting not changed.";
    public const string TestFolderMessage = "Test data folder: startup setting not changed.";
    public const string TurnedOffInWindowsMessage = "Turned off in Windows. Turn it on in Settings > Apps > Startup.";
    public const string PathTooLongMessage = "The Earshot folder path is too long to open on startup.";
    public const string NoExePathMessage = "Earshot could not find its own program file.";
    public const string ChangeFailedMessage = "Open on startup could not be changed.";
    public const string OnMessage = "Opens on startup";
    public const string OffMessage = "Does not open on startup";

    private readonly IStartupRegistry _registry;
    private readonly ILog _log;
    private readonly bool _safeMode;
    private readonly bool _redirected;
    private readonly string? _exePath;

    public StartupRegistration(IStartupRegistry registry, ILog log, bool safeMode, string? exePath, bool redirected = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(log);
        _registry = registry;
        _log = log;
        _safeMode = safeMode;
        _redirected = redirected;
        _exePath = exePath;
    }

    // True when this run must not write the Run value at all.
    public bool WritesBlocked => _safeMode || _redirected;

    // Why nothing is written, for the log and the card.
    public string BlockedMessage => _safeMode ? SafeModeMessage : TestFolderMessage;

    public static string CommandFor(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return "\"" + exePath + "\" " + StartupArgument;
    }

    // Bit 0x1 of the first byte set means the user turned the entry off in Windows.
    public static bool IsTurnedOffInWindows(byte[]? startupApproved) =>
        startupApproved is { Length: > 0 } && (startupApproved[0] & 0x1) != 0;

    public StartupState Read()
    {
        try
        {
            string? run = _registry.ReadRunValue(ValueName);
            if (string.IsNullOrWhiteSpace(run))
            {
                return StartupState.Off;
            }

            return IsTurnedOffInWindows(_registry.ReadStartupApproved(ValueName))
                ? StartupState.DisabledInWindows
                : StartupState.On;
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            _log.Warn("Open on startup could not be read from HKCU.", ex);
            return StartupState.Unknown;
        }
    }

    // True when the Run value is missing or starts a different file than this copy of Earshot, for
    // example after the Earshot folder moved, and the entry is not turned off in Windows. The path is
    // compared ignoring case, as Windows does; the argument must match exactly, because the tray accepts
    // only "--startup". Reads only; a registry error is logged and gives false.
    public bool RunValueNeedsRepair()
    {
        if (string.IsNullOrWhiteSpace(_exePath))
        {
            _log.Warn(NoExePathMessage + " The Open on startup Run value was not checked.");
            return false;
        }

        try
        {
            if (IsTurnedOffInWindows(_registry.ReadStartupApproved(ValueName)))
            {
                return false;
            }

            string? current = _registry.ReadRunValue(ValueName);
            bool startsThisFile = current is not null &&
                                  current.EndsWith("\" " + StartupArgument, StringComparison.Ordinal) &&
                                  string.Equals(current, CommandFor(_exePath), StringComparison.OrdinalIgnoreCase);
            return !startsThisFile;
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            _log.Warn("Open on startup could not be checked in HKCU.", ex);
            return false;
        }
    }

    // Turns Open on startup on or off. Never touches StartupApproved.
    public ControllerResult Apply(bool openOnStartup)
    {
        string action = openOnStartup ? "hkcu-run-write" : "hkcu-run-delete";
        if (WritesBlocked)
        {
            _log.Warn(BlockedMessage + " Open on startup " + (openOnStartup ? "on" : "off") + " was not written to HKCU\\" +
                CurrentUserStartupRegistry.RunKey + ".");
            return new ControllerResult(OpStatus.NotAttempted, BlockedMessage,
                [StepOutcomes.NotAttempted((_safeMode ? "safe-mode:" : "test-data-root:") + action, BlockedMessage)]);
        }

        return openOnStartup ? TurnOn(action) : TurnOff(action);
    }

    private ControllerResult TurnOn(string action)
    {
        if (string.IsNullOrWhiteSpace(_exePath))
        {
            _log.Error(NoExePathMessage + " Open on startup was not turned on.");
            return new ControllerResult(OpStatus.NotAttempted, NoExePathMessage, [StepOutcomes.NotAttempted(action, NoExePathMessage)]);
        }

        string command = CommandFor(_exePath);
        if (command.Length > MaxCommandLength)
        {
            _log.Error(PathTooLongMessage + " Command: " + command);
            return new ControllerResult(OpStatus.NotAttempted, PathTooLongMessage, [StepOutcomes.NotAttempted(action, PathTooLongMessage)]);
        }

        try
        {
            string? current = _registry.ReadRunValue(ValueName);
            bool upToDate = string.Equals(current, command, StringComparison.Ordinal);

            // StartupApproved is checked whether or not a Run value exists. An entry the user turned off
            // stays behind when the Run value is removed, and it still applies once the value is back.
            if (IsTurnedOffInWindows(_registry.ReadStartupApproved(ValueName)))
            {
                StepOutcome turnedOff = StepOutcomes.NotAttempted("startup-approved:turned-off", TurnedOffInWindowsMessage);
                if (upToDate)
                {
                    _log.Warn("Open on startup is turned off in Windows; Earshot does not change that setting.");
                    return new ControllerResult(OpStatus.NotAttempted, TurnedOffInWindowsMessage, [turnedOff]);
                }

                // The command is still written, so it starts this copy of Earshot once the user turns the
                // entry back on in Windows. Windows will not start it until then, so this is not a success.
                _registry.WriteRunValue(ValueName, command);
                _log.Warn("Open on startup: HKCU\\" + CurrentUserStartupRegistry.RunKey + "\\" + ValueName + " = " + command +
                    " written, but the entry is turned off in Windows, so Windows will not start it.");
                return new ControllerResult(OpStatus.Partial, TurnedOffInWindowsMessage, [turnedOff]);
            }

            if (upToDate)
            {
                return ControllerResult.Already(OnMessage);
            }

            _registry.WriteRunValue(ValueName, command);
            _log.Info("Open on startup turned on: HKCU\\" + CurrentUserStartupRegistry.RunKey + "\\" + ValueName + " = " + command);
            return ControllerResult.Ok(OnMessage);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            return Failed(action, ex);
        }
    }

    private ControllerResult TurnOff(string action)
    {
        try
        {
            if (_registry.ReadRunValue(ValueName) is null)
            {
                return ControllerResult.Already(OffMessage);
            }

            _registry.DeleteRunValue(ValueName);
            _log.Info("Open on startup turned off: HKCU\\" + CurrentUserStartupRegistry.RunKey + "\\" + ValueName + " removed.");
            return ControllerResult.Ok(OffMessage);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            return Failed(action, ex);
        }
    }

    private ControllerResult Failed(string action, Exception ex)
    {
        StepOutcome step = StepOutcomes.FromHResult(action, ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
        _log.Error(ChangeFailedMessage + " " + TrayReport.DescribeStep(step), ex);
        return ControllerResult.Fail(ChangeFailedMessage, [step]);
    }

    private static bool IsRegistryError(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException or IOException;
}
