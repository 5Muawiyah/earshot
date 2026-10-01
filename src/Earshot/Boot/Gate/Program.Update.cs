using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Update;

namespace Earshot;

// The elevated update mode.
//
//   update <zipPath> <sha256> <trayPid> <userSid> <address> <containerGuid>
//
// Started by the tray through the UAC prompt, from the installed Earshot.exe and never from a folder the signed-in
// user can write. The command line is attacker-controlled (any program the user runs can ask for the prompt), so it
// is matched exactly, and what it names is only ever read and hashed: the update goes on only if the zip's SHA-256
// is the one given, and installs only what that zip and its own file list say. UpdateActions has the steps.
internal static partial class Program
{
    internal const string UpdateUsage =
        "Usage: Earshot.exe update <zipPath> <sha256> <trayPid> <userSid> <address> <containerGuid>.";

    // The zip is always the download's own name, written with a drive letter. A mapped drive letter can still be a share.
    private const int MaxZipPathLength = 300;

    static partial void TryRunUpdate(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated from the user's own session, so held and written to the checked machine folder once the run has
        // ended, as install is.
        var log = new HeldLog("update");
        var layout = new InstallLayout(AppContext.BaseDirectory, paths.InstallFolder, paths.MachineFolder);
        try
        {
            // The tray has exited by the time the update ends, so how it ended is left in the machine folder for the
            // tray to read at its next start. Only a run whose command line was accepted records anything.
            ctx.ExitCode = (int)Guarded(log, "update", () => RunUpdate(ctx.Args, WindowsProcessToken.Current(), log, request =>
            {
                InstallResult result = CreateUpdateActions(layout, log).Run(request);
                new UpdateOutcomeRecorder(paths.MachineFolder, new NtfsFolderSecurity(), log, TimeProvider.System).RecordUpdateRun(result);
                return result;
            }));
        }
        finally
        {
            log.FlushTo(MachineLog(paths, new NtfsFolderSecurity(), out string whyNot), whyNot);
        }
    }

    // What the update runs with: the real process waiter and install starter, and the machine-wide lock of setup, update and
    // repair, so two of them never work on the install folder together.
    internal static UpdateActions CreateUpdateActions(InstallLayout layout, ILog log) =>
        new(layout, new NtfsFolderSecurity(), new ProcessExitWaiter(), new ChildInstallStarter(), log)
        {
            RunLock = InstallRunLock.Create(),
        };

    // environmentNames: as for RunGate.
    internal static GateExitCode RunUpdate(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<UpdateRequest, InstallResult> run, IEnumerable<string>? environmentNames = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        GateExitCode? refusal = ElevatedModeRefusal("update", token, log);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (!TryParseUpdateArgs(args, out UpdateRequest? request, out string? problem))
        {
            log.Warn("update: rejected (" + problem + "): " + DescribeArgs(args) + ". " + UpdateUsage);
            return GateExitCode.Rejected;
        }

        if (EnvironmentRefusal("update", environmentNames, log) is { } unsafeEnvironment)
        {
            return unsafeEnvironment;
        }

        log.Info("update from " + request.ZipPath + " for " + request.Install.UserSid + ", device " + request.Install.Address + ", container " +
                 request.Install.ContainerId.ToString("D") + ", waiting on process " + request.TrayProcessId.ToString(CultureInfo.InvariantCulture) + ".");
        InstallResult result = run(request);
        LogSteps(log, "update", result);
        return result.Outcome;
    }

    // Exactly [update, zipPath, sha256, trayPid, userSid, address, containerGuid]. The zip path is a string of a
    // drive letter, a colon and a backslash then the rest, already normalised, whose final component is update.zip.
    // Only the string and that final component are checked here, and UpdateActions refuses a final component that is a
    // link; a folder earlier in the path can still be a link or a share, so this does not promise the file is local.
    // What makes the file safe to use is that it is copied through one read-only handle and the copy must match the
    // hash given. The identity is checked by the rules install applies.
    internal static bool TryParseUpdateArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out UpdateRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;

        if (args.Count != 7 || args[0] != UpdateHandover.UpdateVerb)
        {
            problem = "wrong number of arguments";
            return false;
        }

        string zip = args[1];
        if (!TryCheckZipAndHash(zip, args[2], out problem))
        {
            return false;
        }

        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int trayPid) || trayPid <= 0)
        {
            problem = "the process id is not a positive number";
            return false;
        }

        if (!TryParseInstallArgs([UpdateHandover.InstallVerb, args[4], args[5], args[6]], out InstallRequest? install, out problem))
        {
            return false;
        }

        request = new UpdateRequest(zip, args[2].ToUpperInvariant(), trayPid, install);
        problem = null;
        return true;
    }

    // The checks the zip path and its hash get in every verb that takes a zip: only the strings are checked here.
    private static bool TryCheckZipAndHash(string zip, string hash, [NotNullWhen(false)] out string? problem)
    {
        if (zip.Length is 0 or > MaxZipPathLength || zip.Any(char.IsControl) || zip.Length < 3 || !char.IsAsciiLetter(zip[0]) || zip[1] != ':' || zip[2] != '\\')
        {
            problem = "the zip path does not start with a drive letter and a backslash";
            return false;
        }

        if (!string.Equals(Path.GetFullPath(zip), zip, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(zip), UpdateService.ZipFileName, StringComparison.OrdinalIgnoreCase))
        {
            problem = "the zip path is not a normalised path to " + UpdateService.ZipFileName;
            return false;
        }

        if (!FileManifest.IsSha256(hash))
        {
            problem = "the hash is not 64 hex characters";
            return false;
        }

        problem = null;
        return true;
    }

    // ---- install-zip ----

    internal const string InstallZipVerb = "install-zip";

    internal const string InstallZipUsage =
        "Usage: Earshot.exe install-zip <zipPath> <sha256> <userSid> <address> <containerGuid>.";

    // The first install from a checked download, run elevated from the verified copy's own Earshot.exe, because a PC with no
    // usable install has no installed program to hand over to. Started through the UAC prompt only, like update, so it
    // is a privileged mode: safe mode and a redirected data root refuse it before it runs. UpdateActions.RunFirstInstall has
    // the steps. It ends with the exit code of the install it ran, or UpdateActions.AlreadyInstalled.
    static partial void TryRunInstallZip(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated from the user's own session, so held and written to the machine folder once the run has ended, if that
        // folder exists by then: the install it ran creates it.
        var log = new HeldLog("install-zip");
        var layout = new InstallLayout(AppContext.BaseDirectory, paths.InstallFolder, paths.MachineFolder);
        try
        {
            ctx.ExitCode = (int)Guarded(log, "install-zip", () => RunInstallZip(ctx.Args, WindowsProcessToken.Current(), log, request =>
                new UpdateActions(layout, new NtfsFolderSecurity(), new ProcessExitWaiter(), new ChildInstallStarter(), log)
                {
                    RunLock = InstallRunLock.Create(),
                }.RunFirstInstall(request)));
        }
        finally
        {
            log.FlushTo(MachineLog(paths, new NtfsFolderSecurity(), out string whyNot), whyNot);
        }
    }

    // environmentNames: as for RunGate.
    internal static GateExitCode RunInstallZip(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<UpdateRequest, InstallResult> run, IEnumerable<string>? environmentNames = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        GateExitCode? refusal = ElevatedModeRefusal(InstallZipVerb, token, log);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (!TryParseInstallZipArgs(args, out UpdateRequest? request, out string? problem))
        {
            log.Warn("install-zip: rejected (" + problem + "): " + DescribeArgs(args) + ". " + InstallZipUsage);
            return GateExitCode.Rejected;
        }

        if (EnvironmentRefusal(InstallZipVerb, environmentNames, log) is { } unsafeEnvironment)
        {
            return unsafeEnvironment;
        }

        log.Info("install-zip from " + request.ZipPath + " for " + request.Install.UserSid + ", device " + request.Install.Address + ", container " +
                 request.Install.ContainerId.ToString("D", CultureInfo.InvariantCulture) + ".");
        InstallResult result = run(request);
        LogSteps(log, "install-zip", result);
        return result.Outcome;
    }

    // Exactly [install-zip, zipPath, sha256, userSid, address, containerGuid]: update without the tray's process id. The
    // request's TrayProcessId is 0, which nothing reads in this mode.
    internal static bool TryParseInstallZipArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out UpdateRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;

        if (args.Count != 6 || args[0] != InstallZipVerb)
        {
            problem = "wrong number of arguments";
            return false;
        }

        if (!TryCheckZipAndHash(args[1], args[2], out problem))
        {
            return false;
        }

        if (!TryParseInstallArgs([UpdateHandover.InstallVerb, args[3], args[4], args[5]], out InstallRequest? install, out problem))
        {
            return false;
        }

        request = new UpdateRequest(args[1], args[2].ToUpperInvariant(), 0, install);
        problem = null;
        return true;
    }
}
