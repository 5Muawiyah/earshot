using System.Diagnostics.CodeAnalysis;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Service;

namespace Earshot;

// The elevated repair mode.
//
//   repair <userSid> <address> <containerGuid>
//
// Started by the tray through the UAC prompt, from the installed Earshot.exe and never from another folder: a repair run
// anywhere else is refused before it checks or changes anything. The command line is attacker-controlled (any program
// the user runs can ask for the prompt), so it is matched exactly. What it does is install's own repair, run from the
// installed copy (InstallActions with RepairOnly): the install folder must grant no one but administrators write, every
// file listed in the installed Earshot.files.json must match its recorded SHA-256, and then the machine configuration,
// the device file, the scheduled tasks and the hand-back service are registered again exactly as setup registers them,
// each step with its raw code. A file that is missing or does not match stops it with nothing changed: the tray, which
// checked the same files first, has already gone for the release of this version instead.
//
// The tray has usually outlived the run, but it may not have, so how the run ended is written to update-outcome.json in
// the machine folder, as an update's is, and the tray says it once.
internal static partial class Program
{
    internal const string RepairVerb = "repair";

    internal const string RepairUsage = "Usage: Earshot.exe repair <userSid> <address> <containerGuid>.";

    static partial void TryRunRepair(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated from the user's own session, so held and written to the checked machine folder once the run has ended,
        // as install is.
        var log = new HeldLog("repair");
        var layout = new InstallLayout(AppContext.BaseDirectory, paths.InstallFolder, paths.MachineFolder);
        try
        {
            ctx.ExitCode = (int)Guarded(log, "repair", () => RunRepair(ctx.Args, WindowsProcessToken.Current(), log, request =>
            {
                // Task Scheduler COM runs on an MTA thread, as it does in the tray.
                using var worker = new SystemWorker(log);
                InstallResult result = worker.RunAsync(_ => CreateRepairActions(layout, log).Run(request)).GetAwaiter().GetResult();
                if (Earshot.Update.ReleaseVersion.Running(typeof(Program).Assembly) is { } running)
                {
                    new UpdateOutcomeRecorder(paths.MachineFolder, new NtfsFolderSecurity(), log, TimeProvider.System).RecordRepairRun(result, running.ToString());
                }

                return result;
            }));
        }
        finally
        {
            log.FlushTo(MachineLog(paths, new NtfsFolderSecurity(), out string whyNot), whyNot);
        }
    }

    // What repair runs with: install's real devices, tasks, folders and service control, limited to the installed copy.
    internal static InstallActions CreateRepairActions(InstallLayout layout, ILog log) =>
        new(layout, new NtfsFolderSecurity(), new CfgMgr32NodeReader(), new ComTaskRegistrar(), AccountSids.Translate, log, new BluetoothServiceReader(),
            new WindowsServiceControl())
        {
            RepairOnly = true,
        };

    // environmentNames: as for RunGate.
    internal static GateExitCode RunRepair(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<InstallRequest, InstallResult> run, IEnumerable<string>? environmentNames = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        GateExitCode? refusal = ElevatedModeRefusal("repair", token, log);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (!TryParseRepairArgs(args, out InstallRequest? request, out string? problem))
        {
            log.Warn("repair: rejected (" + problem + "): " + DescribeArgs(args) + ". " + RepairUsage);
            return GateExitCode.Rejected;
        }

        if (EnvironmentRefusal("repair", environmentNames, log) is { } unsafeEnvironment)
        {
            return unsafeEnvironment;
        }

        log.Info("repair for " + request.UserSid + ", device " + request.Address + ", container " + request.ContainerId.ToString("D") + ".");
        InstallResult result = run(request);
        LogSteps(log, "repair", result);
        return result.Outcome;
    }

    // Exactly [repair, userSid, address, containerGuid]: install's identity with no principal flag, so the tasks are
    // always registered for SYSTEM.
    internal static bool TryParseRepairArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out InstallRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;

        if (args.Count != 4 || args[0] != RepairVerb)
        {
            problem = "wrong number of arguments";
            return false;
        }

        return TryParseInstallArgs([Earshot.Update.UpdateHandover.InstallVerb, args[1], args[2], args[3]], out request, out problem);
    }
}
