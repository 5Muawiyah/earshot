using System.Globalization;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Tray;
using Earshot.Widget.Alert;

namespace Earshot.Install;

// The per-user part of removing Earshot, which the elevated uninstall cannot do: under Administrator protection an elevated run
// has a profile of its own, so it would not reach the signed-in user's registry hive or Start menu. Each step touches only what
// is exactly Earshot's: the Open on startup value only when it starts the installed program with the argument Earshot writes,
// and the Start menu shortcut only when it targets the installed program. Anything else is left alone and said so.
internal sealed class PerUserCleanup(IStartupRegistry registry, IShellLinkWriter links, string shortcutPath, Action<string> deleteFile)
{
    public IReadOnlyList<StepOutcome> Run(string installedExe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedExe);
        return [RemoveRunValue(installedExe), RemoveShortcut(installedExe)];
    }

    private StepOutcome RemoveRunValue(string installedExe)
    {
        const string Step = "remove-run-value";
        try
        {
            string? value = registry.ReadRunValue(StartupRegistration.ValueName);
            if (value is null)
            {
                return StepOutcomes.FromHResult(Step, 0, "There was no Open on startup value.");
            }

            if (!string.Equals(value, StartupRegistration.CommandFor(installedExe), StringComparison.OrdinalIgnoreCase))
            {
                return StepOutcomes.NotAttempted(Step, "The Open on startup value starts something other than the installed program, so it was left alone.");
            }

            registry.DeleteRunValue(StartupRegistration.ValueName);
            return StepOutcomes.FromHResult(Step, 0, "Removed.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            return StepOutcomes.FromHResult(Step, ex.HResult, ex.Message);
        }
    }

    private StepOutcome RemoveShortcut(string installedExe)
    {
        const string Step = "remove-start-menu-shortcut";
        ShortcutRead read = links.ReadShortcut(shortcutPath);
        switch (read.Status)
        {
            case ShortcutReadStatus.NotFound:
                return StepOutcomes.FromHResult(Step, 0, "There was no shortcut.");
            case ShortcutReadStatus.Unreadable:
                return read.Failure is { } failure
                    ? failure with { Step = Step, Ok = false }
                    : StepOutcomes.NotAttempted(Step, "The shortcut could not be read, so it was left alone.");
        }

        if (read.Existing is not { } existing || !NotificationRegistration.PathsEqual(existing.TargetPath, installedExe))
        {
            return StepOutcomes.NotAttempted(Step, "The shortcut targets something other than the installed program, so it was left alone.");
        }

        try
        {
            deleteFile(shortcutPath);
            return StepOutcomes.FromHResult(Step, 0, "Removed.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return StepOutcomes.FromHResult(Step, ex.HResult, ex.Message);
        }
    }
}

// Where the person is told how a Modify or Uninstall from Installed apps went. The verbs have no window of their own.
internal interface IUiVerbMessages
{
    void Show(string text);
}

internal sealed class MessageBoxVerbMessages : IUiVerbMessages
{
    public void Show(string text) =>
        MessageBox.Show(text, TrayStatus.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
}

internal static class UiVerbCopy
{
    public const string Declined = "Nothing was changed.";
    public const string UninstallPartial = "Earshot was removed, but not everything. Run Uninstall again from Setup or the install script.";
    public const string NoDevice = "Earshot cannot repair without your AirPods set up. Run Setup to set them up again.";
    public const string NotInstalledCopy = "This is not the installed Earshot, so it was left alone.";

    public static string UninstallFailed(int code) => "Earshot could not be removed. Code " + code.ToString(CultureInfo.InvariantCulture) + ".";

    public static string RepairFailed(int code) => "Earshot could not be repaired. Code " + code.ToString(CultureInfo.InvariantCulture) + ".";
}

// "uninstall-ui" and "repair-ui", the two commands Installed apps runs for Uninstall and Modify. Both run unelevated, raise the
// one administrator prompt for the elevated verb of the installed program, and report the outcome in a message. Uninstall's
// per-user steps run only after the elevated uninstall has ended with exit code 0, so a declined prompt or a failed uninstall
// leaves the Open on startup value and the shortcut in place. They run in this process, which needs no file of the install
// folder after it starts, so the elevated uninstall finds this program's own file in use and schedules it for the next restart
// like any program running from the folder it removes.
internal sealed class UiVerbs(IElevatedLauncher launcher, PerUserCleanup cleanup, string installedExe, IUiVerbMessages messages, ILog log)
{
    // ERROR_CANCELLED: the person declined the administrator prompt.
    internal const int ErrorCancelled = 1223;

    public int Uninstall()
    {
        ElevatedRun run = launcher.RunAsync(installedExe, "uninstall", CancellationToken.None).GetAwaiter().GetResult();
        if (run.ExitCode is not { } code)
        {
            return Declined(run);
        }

        log.Info("uninstall-ui: the elevated uninstall ended with exit code " + code.ToString(CultureInfo.InvariantCulture) + ".");
        if (code != 0)
        {
            messages.Show(code == (int)Boot.Gate.GateExitCode.Partial ? UiVerbCopy.UninstallPartial : UiVerbCopy.UninstallFailed(code));
            return code;
        }

        foreach (StepOutcome step in cleanup.Run(installedExe))
        {
            log.Write(step.Ok ? LogLevel.Info : LogLevel.Warn, "uninstall-ui: " + TrayReport.DescribeStep(step));
        }

        return 0;
    }

    public int Repair(string userSid, DeviceIdentity? identity)
    {
        if (identity is null || !Boot.Sddl.IsUserSid(userSid))
        {
            messages.Show(UiVerbCopy.NoDevice);
            return ExitCodes.Config;
        }

        string arguments = "repair " + userSid + " " + identity.Address + " " + identity.ContainerId.ToString("D", CultureInfo.InvariantCulture);
        ElevatedRun run = launcher.RunAsync(installedExe, arguments, CancellationToken.None).GetAwaiter().GetResult();
        if (run.ExitCode is not { } code)
        {
            return Declined(run);
        }

        log.Info("repair-ui: the elevated repair ended with exit code " + code.ToString(CultureInfo.InvariantCulture) + ".");
        if (code != 0)
        {
            messages.Show(UiVerbCopy.RepairFailed(code));
        }

        return code;
    }

    private int Declined(ElevatedRun run)
    {
        log.Warn("The administrator prompt did not lead to a run: " + TrayReport.DescribeStep(run.Step));
        messages.Show(UiVerbCopy.Declined);
        return ErrorCancelled;
    }
}
