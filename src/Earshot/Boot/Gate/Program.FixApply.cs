using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Repair;
using Earshot.Service;

namespace Earshot;

// The elevated Fix connection mode.
//
//   fix-apply <planFile> <sha256>
//
// Started by `fix` through the administrator prompt, never by a person by hand. The command line is attacker-controlled (any
// program the user runs can ask for the prompt), so it is matched exactly, the token and the environment are checked as install
// checks them, and the plan file is only a request to do less: FixApply builds the plan again from this PC and applies the
// part both name. A refused or malformed request changes nothing.
internal static partial class Program
{
    // The dispatcher declares this hook; this line repeats it so the file builds on its own.
    static partial void TryRunFixApply(RunContext ctx);

    static partial void TryRunFixApply(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated from the user's own session, so held and written to the checked machine folder once the run has ended.
        var log = new HeldLog("fix-apply");
        try
        {
            string? refusal = FixApplyPathsRefusal(paths);
            if (refusal is not null)
            {
                log.Warn(refusal);
                ctx.ExitCode = ExitCodes.Refused;
                return;
            }

            ctx.ExitCode = (int)Guarded(log, "fix-apply", () => RunFixApply(ctx.Args, WindowsProcessToken.Current(), log, request =>
                FixApply.Run(request, CreateFixApplyDeps(paths, log))));
        }
        finally
        {
            log.FlushTo(MachineLog(paths, new NtfsFolderSecurity(), out string whyNot), whyNot);
        }
    }

    // The dispatcher refuses privileged modes in safe mode and with a redirected data root; this repeats the refusal so the
    // mode is safe whichever way it is reached.
    internal static string? FixApplyPathsRefusal(Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.IsSafeMode)
        {
            return "Refused: fix-apply does not run in safe mode.";
        }

        return paths.IsRedirected ? "Refused: fix-apply does not run while " + Paths.DataRootVariable + " is set." : null;
    }

    internal static FixApplyDeps CreateFixApplyDeps(Paths paths, ILog log) => new()
    {
        Nodes = new CfgMgr32NodeApi(),
        Store = new GateStore(paths.MachineFolder),
        Folders = new NtfsFolderSecurity(),
        RunLock = new MachineGateMutex(),
        Bluetooth = new BluetoothServiceApi(),
        Log = log,
        LeftoversReader = MachineLeftovers.For(paths, new WindowsServiceControl()),
        OtherFootprint = FixFootprint.For(paths),
    };

    // environmentNames: as for RunGate.
    internal static GateExitCode RunFixApply(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<FixApplyRequest, FixApplyResult> run, IEnumerable<string>? environmentNames = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        GateExitCode? refusal = ElevatedModeRefusal(FixApply.Verb, token, log);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (!FixApply.TryParseArgs(args, out FixApplyRequest? request, out string? problem))
        {
            log.Warn("fix-apply: rejected (" + problem + "): " + DescribeArgs(args) + ". " + FixApply.Usage);
            return GateExitCode.Rejected;
        }

        if (EnvironmentRefusal(FixApply.Verb, environmentNames, log) is { } unsafeEnvironment)
        {
            return unsafeEnvironment;
        }

        FixApplyResult result = run(request);
        foreach (StepOutcome step in result.Steps)
        {
            string line = "fix-apply: " + GateActions.Describe(step);
            if (step.Ok)
            {
                log.Info(line);
            }
            else
            {
                log.Warn(line);
            }
        }

        log.Info("fix-apply: " + GateExitCodes.ResultName(result.Outcome) + " (" + (int)result.Outcome + ").");
        return result.Outcome;
    }
}
