using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// doctor [--out <path>]
// settings list | get <name> | set <name> <value> [--out <path>]
//
// Both are for a person or an AI at a terminal. doctor reads this PC and changes nothing; settings edits the user's settings
// file. Neither needs administrator rights, and neither touches a device.
internal static partial class Program
{
    internal const string DoctorUsage = "Usage: Earshot.exe doctor [--out <path>]";

    static partial void TryRunDoctor(RunContext ctx)
    {
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);
        if (!TrySplitOut(ctx.Args.Skip(1).ToList(), out List<string> rest, out string? outPath, out string? parseError) || rest.Count != 0)
        {
            ctx.ExitCode = ReportUsageError(log, "doctor", parseError ?? "doctor takes no other words.", DoctorUsage);
            return;
        }

        if (!CommandOutput.TryOpen(outPath, out CommandOutput? output, out string? problem))
        {
            log.Error("doctor: " + problem);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        using (output)
        {
            ctx.ExitCode = Commands.DoctorCommand.Run(new Commands.RealDoctorSource(paths, log), output.Writer);
        }
    }

    static partial void TryRunSettings(RunContext ctx)
    {
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);
        if (!TrySplitOut(ctx.Args.Skip(1).ToList(), out List<string> rest, out string? outPath, out string? parseError))
        {
            ctx.ExitCode = ReportUsageError(log, "settings", parseError ?? "", Commands.SettingsCommand.Usage);
            return;
        }

        if (!CommandOutput.TryOpen(outPath, out CommandOutput? output, out string? problem))
        {
            log.Error("settings: " + problem);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        using (output)
        {
            var store = new JsonSettingsStore(paths.SettingsFile, log);
            ctx.ExitCode = Commands.SettingsCommand.Run(rest, store, output.Writer);
        }
    }

    // Takes "--out <path>" out of the words. False, with the reason, when --out has no path.
    internal static bool TrySplitOut(List<string> args, out List<string> rest, out string? outPath, out string? error)
    {
        rest = [];
        outPath = null;
        error = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--out")
            {
                if (i + 1 >= args.Count)
                {
                    error = "--out needs a path.";
                    return false;
                }

                outPath = args[++i];
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        return true;
    }
}
