using System.Security.Principal;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Install;
using Earshot.Tray;
using Earshot.Widget.Alert;

namespace Earshot;

// uninstall-ui and repair-ui: what Settings, Apps, Installed apps runs for Uninstall and Modify (InstalledAppsEntry). Both take
// no arguments, run not elevated and only from the installed program: a copy anywhere else is refused, as is any run in safe
// mode or with a redirected data folder, so a test or a stray copy never raises the administrator prompt. UiVerbs has the steps.
internal static partial class Program
{
    static partial void TryRunUninstallUi(RunContext ctx) => ctx.ExitCode = RunUiVerb(ctx, repair: false);

    static partial void TryRunRepairUi(RunContext ctx) => ctx.ExitCode = RunUiVerb(ctx, repair: true);

    private static int RunUiVerb(RunContext ctx, bool repair)
    {
        string verb = repair ? InstalledAppsEntry.RepairVerb : InstalledAppsEntry.UninstallVerb;
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);
        var messages = new MessageBoxVerbMessages();
        if (ctx.Args.Length != 1)
        {
            log.Warn(verb + ": rejected, it takes no arguments.");
            return ExitCodes.Usage;
        }

        if (paths.IsSafeMode || paths.IsRedirected)
        {
            log.Warn(verb + ": refused, because safe mode or a test data folder is set.");
            return ExitCodes.Refused;
        }

        string? running = Environment.ProcessPath;
        if (!NotificationRegistration.PathsEqual(running, paths.InstalledExe))
        {
            log.Warn(verb + ": refused, because this is not the installed program.");
            messages.Show(UiVerbCopy.NotInstalledCopy);
            return ExitCodes.Refused;
        }

        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var cleanup = new PerUserCleanup(
            new CurrentUserStartupRegistry(), new RealShellLinkWriter(), Path.Combine(programs, NotificationRegistration.ShortcutFileName), File.Delete);
        var verbs = new UiVerbs(new ShellRunasLauncher(), cleanup, paths.InstalledExe, messages, log);
        if (!repair)
        {
            return verbs.Uninstall();
        }

        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value ?? "";
        }

        DeviceIdentity? device = SetupValues.ReadMachineIdentity(paths, new NtfsFolderSecurity(), log);
        return verbs.Repair(sid, device);
    }
}
