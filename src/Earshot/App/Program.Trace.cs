using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tracing;

namespace Earshot;

// The elevated Bluetooth trace mode.
//
//   trace <tray pid> <nonce>
//
// Started by the tray through the administrator prompt, once per recording; the tray made the three events named by the nonce
// before it asked. The helper stays alive for the whole recording, so stopping needs no second prompt, and ends when the tray
// has copied the trace out. Like fix-apply it is refused in safe mode and with a redirected data root, and the command line
// is matched exactly. A trace changes no device, service or pairing, and nothing in this mode touches the network.
internal static partial class Program
{
    static partial void TryRunTrace(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated from the user's own session, so held and written to the checked machine folder once the run has ended.
        var log = new HeldLog("trace");
        try
        {
            string? refusal = TracePathsRefusal(paths);
            if (refusal is not null)
            {
                log.Warn(refusal);
                ctx.ExitCode = ExitCodes.Refused;
                return;
            }

            ctx.ExitCode = RunTrace(ctx.Args, log, request =>
            {
                using var signals = new TraceSignals(request);
                return TraceHelper.Run(request, new TraceMachine(paths, new NtfsFolderSecurity()), signals, new WindowsProcessRunner(), log, () => DateTime.UtcNow);
            }, WindowsProcessToken.Current());
        }
        finally
        {
            log.FlushTo(MachineLog(paths, new NtfsFolderSecurity(), out string whyNot), whyNot);
        }
    }

    // The dispatcher refuses privileged modes in safe mode and with a redirected data root; this repeats the refusal so the
    // mode is safe whichever way it is reached.
    internal static string? TracePathsRefusal(Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.IsSafeMode)
        {
            return "Refused: trace does not run in safe mode.";
        }

        return paths.IsRedirected ? "Refused: trace does not run while " + Paths.DataRootVariable + " is set." : null;
    }

    // token: null in a test that is not about elevation.
    internal static int RunTrace(IReadOnlyList<string> args, ILog log, Func<TraceRequest, TraceOutcome> run, IProcessToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        if (!TraceRequest.TryParse(args, out TraceRequest? request, out string? problem) || request is null)
        {
            log.Warn("trace: rejected (" + problem + "). " + TraceRequest.Usage);
            return ExitCodes.Usage;
        }

        if (token is not null && !token.IsElevatedAdministrator)
        {
            log.Warn("trace: refused, because this run is not elevated.");
            return ExitCodes.Refused;
        }

        try
        {
            TraceOutcome outcome = run(request);
            log.Info("trace: " + outcome + ".");
            return TraceHelper.ExitCodeFor(outcome);
        }
        catch (Exception ex)
        {
            // The last resort around the helper, so an elevated mode never ends without a line in the log.
            log.Error("trace: failed: " + ex.GetType().Name + ": " + ex.Message, ex);
            return ExitCodes.Software;
        }
    }
}
