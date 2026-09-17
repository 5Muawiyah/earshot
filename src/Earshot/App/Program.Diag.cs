using System.Diagnostics.CodeAnalysis;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// diag <target> ... [--out <path>]
//
//   diag connect
//   diag disconnect
//   diag ks <reconnect|disconnect> <src|wave|all> [buffer4]
//   diag gate <verb> [address]        verb from GateVerbs except boot; address only for set-device
//   diag protect-unelevated <on|off>
//   diag battery-sweep
//
// LIVE single-shot actions for the owner's live tests, with JSON evidence written under
// %LOCALAPPDATA%\Earshot\livetest. Each target is one elidable partial implemented in its
// feature's folder. diag exists only for the owner's live tests, so in safe mode every target is
// refused here, before its implementation runs and before any service is built, battery-sweep
// included.
internal static partial class Program
{
    internal const string DiagUsage =
        "Usage: Earshot.exe diag connect | disconnect | ks <reconnect|disconnect> <src|wave|all> [buffer4] | " +
        "gate <verb> [address] | protect-unelevated <on|off> | battery-sweep [--out <path>]";

    internal const string DiagBatterySweepName = "battery-sweep";

    internal const string DiagKsBufferArgument = "buffer4";

    internal sealed record DiagRequest(string Target, IReadOnlyList<string> Args, string? OutPath);

    static partial void TryRunDiag(RunContext ctx)
    {
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);

        if (!TryParseDiagArgs(ctx.Args, out DiagRequest? request, out string? parseError))
        {
            ctx.ExitCode = ReportUsageError(log, "diag", parseError, DiagUsage);
            return;
        }

        if (!CommandOutput.TryOpen(request.OutPath, out CommandOutput? output, out string? outputProblem))
        {
            log.Error("diag: " + outputProblem);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        using (output)
        using (var services = new RunServices(paths, log))
        {
            log.Info("diag " + request.Target + " " + string.Join(' ', request.Args));
            ctx.ExitCode = RunDiag(request, output.Writer, paths.LiveTestFolder, paths.IsSafeMode, log, services.Get);
        }
    }

    internal static bool TryParseDiagArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out DiagRequest? request,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;
        string? outPath = null;
        var positional = new List<string>();

        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--out")
            {
                if (outPath is not null)
                {
                    error = "--out was given twice.";
                    return false;
                }

                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "--out needs a path.";
                    return false;
                }

                outPath = args[++i];
            }
            else
            {
                positional.Add(a);
            }
        }

        if (positional.Count == 0)
        {
            error = "A diag target is needed.";
            return false;
        }

        string target = positional[0];
        List<string> rest = positional.GetRange(1, positional.Count - 1);
        error = CheckDiagGrammar(target, rest);
        if (error is not null)
        {
            return false;
        }

        request = new DiagRequest(target, rest, outPath);
        return true;
    }

    // Returns what is wrong with the target's arguments, or null when they fit its grammar.
    private static string? CheckDiagGrammar(string target, List<string> rest)
    {
        switch (target)
        {
            case "connect":
            case "disconnect":
            case DiagBatterySweepName:
                return rest.Count == 0 ? null : "diag " + target + " takes no arguments.";

            case "ks":
                // buffer4 retries with a 4-byte zeroed data buffer, for the case where a filter answers the
                // documented request (no buffer) with a buffer error.
                if (rest.Count is not (2 or 3))
                {
                    return "diag ks needs <reconnect|disconnect> <src|wave|all> [buffer4].";
                }

                if (rest[0] is not ("reconnect" or "disconnect"))
                {
                    return "diag ks action must be reconnect or disconnect.";
                }

                if (rest[1] is not ("src" or "wave" or "all"))
                {
                    return "diag ks filter must be src, wave or all.";
                }

                return rest.Count == 2 || rest[2] == DiagKsBufferArgument ? null : "diag ks only takes buffer4 after the filter.";

            case "gate":
                if (rest.Count is < 1 or > 2)
                {
                    return "diag gate needs <verb> [address].";
                }

                if (!GateVerbs.All.Contains(rest[0]) || rest[0] == GateVerbs.Boot)
                {
                    return "diag gate verb is not one the tray sends.";
                }

                if (rest[0] == GateVerbs.SetDevice)
                {
                    return rest.Count == 2 && BoundaryValidation.IsAddress12(rest[1])
                        ? null
                        : "diag gate set-device needs a 12 character upper-case hex address.";
                }

                return rest.Count == 1 ? null : "Only set-device takes an address.";

            case "protect-unelevated":
                return rest.Count == 1 && rest[0] is ("on" or "off")
                    ? null
                    : "diag protect-unelevated needs on or off.";

            default:
                return "Unknown diag target: " + target;
        }
    }

    internal static int RunDiag(
        DiagRequest request,
        TextWriter output,
        string evidenceFolder,
        bool safeMode,
        ILog log,
        Func<ServiceRegistry> services)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(services);

        if (safeMode)
        {
            log.Warn(SafeDecorators.Message + " Refused: diag " + request.Target + ".");
            output.WriteLine(SafeDecorators.Message);
            output.Flush();
            return ExitCodes.Refused;
        }

        var diag = new DiagContext(request.Target, request.Args, evidenceFolder, output, services);
        switch (request.Target)
        {
            case "connect":            DiagConnect(diag); break;
            case "disconnect":         DiagDisconnect(diag); break;
            case "ks":                 DiagKs(diag); break;
            case "gate":               DiagGate(diag); break;
            case "protect-unelevated": DiagProtectUnelevated(diag); break;
            case DiagBatterySweepName: DiagBatterySweep(diag); break;
            default:                   break;
        }

        if (!diag.Handled)
        {
            log.Warn("diag " + request.Target + ": " + NotAvailableMessage);
            output.WriteLine(NotAvailableMessage);
            output.Flush();
            return ExitCodes.Unavailable;
        }

        output.Flush();
        return diag.ExitCode;
    }

    static partial void DiagConnect(DiagContext ctx);
    static partial void DiagDisconnect(DiagContext ctx);
    static partial void DiagKs(DiagContext ctx);
    static partial void DiagGate(DiagContext ctx);
    static partial void DiagProtectUnelevated(DiagContext ctx);
    static partial void DiagBatterySweep(DiagContext ctx);
}
