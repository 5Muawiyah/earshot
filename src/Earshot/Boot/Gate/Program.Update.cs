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
                InstallResult result = new UpdateActions(layout, new NtfsFolderSecurity(), new ProcessExitWaiter(), new ChildInstallStarter(), log)
                {
                    RunLock = InstallRunLock.Create(),
                }.Run(request);
                new UpdateOutcomeRecorder(paths.MachineFolder, new NtfsFolderSecurity(), log, TimeProvider.System).RecordUpdateRun(result);
                return result;
            }));
        }
        finally
        {
            log.FlushTo(MachineLog(paths, new NtfsFolderSecurity(), out string whyNot), whyNot);
        }
    }

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

        if (!FileManifest.IsSha256(args[2]))
        {
            problem = "the hash is not 64 hex characters";
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
}
