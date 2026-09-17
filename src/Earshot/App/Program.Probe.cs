using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// probe [audio|topology|nodes|services|task|battery|all] [--json] [--out <path>]
// probe icon --out <folder> [--json]
//
// Read-only diagnostics that reuse the production selection code. Each target is one elidable
// partial implemented in its feature's folder (Audio\ProbeAudio.cs and so on); a missing target
// prints "Not available in this build." and makes the exit code non-zero.
//
// icon is not part of all: it writes image files, so it runs only when named, and its --out is the folder
// the images go to (the report itself goes to the console).
internal static partial class Program
{
    internal const string ProbeUsage =
        "Usage: Earshot.exe probe [audio|topology|nodes|services|task|battery|all] [--json] [--out <path>] | probe icon --out <folder> [--json]";

    internal const string ProbeIconTarget = "icon";

    internal static readonly IReadOnlyList<string> ProbeTargets =
        ["audio", "topology", "nodes", "services", "task", "battery"];

    internal sealed record ProbeRequest(IReadOnlyList<string> Targets, bool Json, string? OutPath);

    static partial void TryRunProbe(RunContext ctx)
    {
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);

        if (!TryParseProbeArgs(ctx.Args, out ProbeRequest? request, out string? parseError))
        {
            ctx.ExitCode = ReportUsageError(log, "probe", parseError, ProbeUsage);
            return;
        }

        // probe icon's --out names the folder for its images; its report goes to the console.
        string? reportPath = IsIconProbe(request) ? null : request.OutPath;
        if (!CommandOutput.TryOpen(reportPath, out CommandOutput? output, out string? outputProblem))
        {
            log.Error("probe: " + outputProblem);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        using (output)
        using (var services = new RunServices(paths, log))
        {
            log.Info("probe " + string.Join(' ', request.Targets) + (request.Json ? " --json" : "") + " to " + output.Destination);
            ctx.ExitCode = RunProbe(request, output.Writer, services.Get);
        }
    }

    internal static bool TryParseProbeArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out ProbeRequest? request,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;
        string? target = null;
        string? outPath = null;
        bool json = false;

        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--json")
            {
                if (json)
                {
                    error = "--json was given twice.";
                    return false;
                }

                json = true;
            }
            else if (a == "--out")
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
            else if (a == "all" || a == ProbeIconTarget || ProbeTargets.Contains(a, StringComparer.Ordinal))
            {
                if (target is not null)
                {
                    error = "Only one probe target can be given.";
                    return false;
                }

                target = a;
            }
            else
            {
                error = "Unknown probe argument: " + a;
                return false;
            }
        }

        if (target == ProbeIconTarget && outPath is null)
        {
            error = "probe icon needs --out <folder>.";
            return false;
        }

        IReadOnlyList<string> targets = target is null or "all" ? ProbeTargets : [target];
        request = new ProbeRequest(targets, json, outPath);
        error = null;
        return true;
    }

    internal static bool IsIconProbe(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Targets.Count == 1 && request.Targets[0] == ProbeIconTarget;
    }

    // Runs each requested target and returns the first non-zero exit code, or 0.
    internal static int RunProbe(ProbeRequest request, TextWriter output, Func<ServiceRegistry> services)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(services);

        bool jsonArray = request.Json && request.Targets.Count > 1;
        int exitCode = ExitCodes.Ok;
        if (jsonArray)
        {
            output.WriteLine("[");
        }

        for (int i = 0; i < request.Targets.Count; i++)
        {
            string target = request.Targets[i];
            using var captured = new StringWriter(CultureInfo.InvariantCulture);
            TextWriter targetOut = jsonArray ? captured : output;

            if (!request.Json)
            {
                if (i > 0)
                {
                    output.WriteLine();
                }

                output.WriteLine("== " + target + " ==");
            }

            var probe = new ProbeContext(target, targetOut, request.Json, services);
            RunProbeTarget(probe, request.OutPath);
            if (!probe.Handled)
            {
                WriteProbeNotAvailable(probe);
                probe.ExitCode = ExitCodes.Unavailable;
            }

            if (jsonArray)
            {
                output.Write(AsJsonElement(target, captured.ToString()));
                output.WriteLine(i < request.Targets.Count - 1 ? "," : "");
            }

            if (exitCode == ExitCodes.Ok)
            {
                exitCode = probe.ExitCode;
            }
        }

        if (jsonArray)
        {
            output.WriteLine("]");
        }

        output.Flush();
        return exitCode;
    }

    private static void RunProbeTarget(ProbeContext ctx, string? outPath)
    {
        switch (ctx.Target)
        {
            case "audio":    ProbeAudio(ctx); break;
            case "topology": ProbeTopology(ctx); break;
            case "nodes":    ProbeNodes(ctx); break;
            case "services": ProbeServices(ctx); break;
            case "task":     ProbeTask(ctx); break;
            case "battery":  ProbeBattery(ctx); break;
            case ProbeIconTarget: ProbeIcon(ctx, outPath); break;
            default:         break;
        }
    }

    static partial void ProbeAudio(ProbeContext ctx);
    static partial void ProbeTopology(ProbeContext ctx);
    static partial void ProbeNodes(ProbeContext ctx);
    static partial void ProbeServices(ProbeContext ctx);
    static partial void ProbeTask(ProbeContext ctx);
    static partial void ProbeBattery(ProbeContext ctx);

    // folder: where the images go (probe icon's --out).
    static partial void ProbeIcon(ProbeContext ctx, string? folder);

    private static void WriteProbeNotAvailable(ProbeContext ctx)
    {
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", ctx.Target);
                w.WriteBoolean("available", false);
                w.WriteString("message", NotAvailableMessage);
                w.WriteEndObject();
            });
        }
        else
        {
            ctx.Out.WriteLine(NotAvailableMessage);
        }
    }

    // One element of the "all --json" array. A target that wrote something other than one JSON
    // value is still reported, as a string, so the array stays valid.
    private static string AsJsonElement(string target, string text)
    {
        string trimmed = text.Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            return trimmed;
        }
        catch (JsonException ex)
        {
            return ProbeContext.JsonText(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", target);
                w.WriteString("invalidJson", ex.Message);
                w.WriteString("output", trimmed);
                w.WriteEndObject();
            });
        }
    }

    // Prints a usage error where the user can see it: the console when there is one, the log always.
    private static int ReportUsageError(ILog log, string mode, string message, string usage)
    {
        log.Warn(mode + ": " + message);
        if (CommandOutput.TryOpen(outPath: null, out CommandOutput? output, out _))
        {
            using (output)
            {
                output.Writer.WriteLine(message);
                output.Writer.WriteLine(usage);
            }
        }

        return ExitCodes.Usage;
    }
}
