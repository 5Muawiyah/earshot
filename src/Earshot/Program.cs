using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Infra;
using Earshot.Interop;

namespace Earshot;

// Entry point for every run mode. This file only dispatches; each mode is implemented in its own
// partial file:
//
//   (no argument), --startup          TryRunTray       App\Program.Tray.cs
//   gate <verb> <nonce> [address]     TryRunGate       Boot\Gate\Program.Gate.cs
//   gate-protect <verb> <nonce>       TryRunGate, in protect mode (the \Earshot\Protect task only)
//   install ... / uninstall           TryRunInstall, TryRunUninstall
//   probe [target] [--json] [--out]   TryRunProbe      App\Program.Probe.cs
//   diag <target> ...                 TryRunDiag       App\Program.Diag.cs
//
// Dispatch runs before WinForms is initialised and before the single-instance mutex, so the
// headless modes never touch WinForms and install is never mistaken for a second tray.
// A hook that is not implemented is removed by the compiler; the mode then logs
// "Not available in this build." and exits with ExitCodes.Unavailable. That includes the tray.
//
// gate, gate-protect, install and uninstall change device nodes, Bluetooth services, scheduled tasks and
// machine folders. They are refused before dispatch in safe mode (EARSHOT_SAFE_MODE) and whenever
// EARSHOT_DATA_ROOT is set, because an elevated process started from a user session may inherit a
// variable that user set, which would move install's writes to a folder the user controls while the
// gate kept reading %ProgramData%. The SYSTEM task never has either variable, so this costs nothing.
internal static partial class Program
{
    internal sealed class RunContext
    {
        public required string[] Args { get; init; }

        // Set by the hook that ran, including 0 for success. Every hook sets it, the tray included;
        // a mode that leaves it null was not implemented in this build.
        public int? ExitCode { get; set; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // First call in every mode: later DLL loads search System32 and the application folder
        // only, never the current directory or PATH.
        // https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories
        if (!NativeMethods.SetDefaultDllDirectories(
                NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32 | NativeMethods.LOAD_LIBRARY_SEARCH_APPLICATION_DIR))
        {
            int error = Marshal.GetLastPInvokeError();
            return StartupFailure(ExitCodes.OsError,
                "SetDefaultDllDirectories failed with Win32 error " + error.ToString(CultureInfo.InvariantCulture) +
                ". Earshot does not run without a safe DLL search order.");
        }

        try
        {
            InteropLayout.AssertSizes();
        }
        catch (InvalidOperationException ex)
        {
            return StartupFailure(ExitCodes.Software, ex.Message);
        }

        Paths paths;
        try
        {
            paths = Paths.Current;
        }
        catch (InvalidOperationException ex)
        {
            return StartupFailure(ExitCodes.Config, ex.Message);
        }

        return Dispatch(args, paths, new FileLog(paths.LogFolder));
    }

    // Runs the mode named by args[0] and returns the process exit code.
    internal static int Dispatch(string[] args, Paths paths, ILog log)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        string mode = args.Length == 0 ? "" : args[0];
        string? refusal = PrivilegedModeRefusal(mode, paths);
        if (refusal is not null)
        {
            log.Warn(refusal);
            return ExitCodes.Refused;
        }

        var ctx = new RunContext { Args = args };
        string label;
        switch (mode)
        {
            case "gate":      label = mode; TryRunGate(ctx); break;
            case "gate-protect": label = mode; TryRunGate(ctx); break;
            case "install":   label = mode; TryRunInstall(ctx); break;
            case "uninstall": label = mode; TryRunUninstall(ctx); break;
            case "probe":     label = mode; TryRunProbe(ctx); break;
            case "diag":      label = mode; TryRunDiag(ctx); break;
            default:          label = "tray"; TryRunTray(ctx); break;
        }

        return ExitCodeFor(label, ctx, log);
    }

    // The exit code a hook set, or ExitCodes.Unavailable (logged) when no hook ran for the mode.
    internal static int ExitCodeFor(string label, RunContext ctx, ILog log)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(log);

        if (ctx.ExitCode is int code)
        {
            return code;
        }

        log.Error("'" + label + "': " + NotAvailableMessage);
        return ExitCodes.Unavailable;
    }

    internal static readonly IReadOnlySet<string> PrivilegedModes =
        new HashSet<string>(StringComparer.Ordinal) { "gate", "gate-protect", "install", "uninstall" };

    // Why a privileged mode must not run with these paths, or null when it may (or the mode is not
    // privileged). See the header comment.
    internal static string? PrivilegedModeRefusal(string mode, Paths paths)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(paths);

        if (!PrivilegedModes.Contains(mode))
        {
            return null;
        }

        if (paths.IsSafeMode)
        {
            return SafeDecorators.Message + " Refused: " + mode + ".";
        }

        if (paths.IsRedirected)
        {
            return "Refused: " + mode + " does not run while " + Paths.DataRootVariable + " is set.";
        }

        return null;
    }

    internal const string NotAvailableMessage = NullResults.NotAvailableMessage;

    static partial void TryRunTray(RunContext ctx);
    static partial void TryRunGate(RunContext ctx);
    static partial void TryRunInstall(RunContext ctx);
    static partial void TryRunUninstall(RunContext ctx);
    static partial void TryRunProbe(RunContext ctx);
    static partial void TryRunDiag(RunContext ctx);

    // A failure before any mode runs. Written to the debugger output, and to the log when the
    // log folder can be resolved.
    private static int StartupFailure(int exitCode, string message)
    {
        Trace.WriteLine("Earshot startup failed: " + message);
        try
        {
            new FileLog(Paths.Current.LogFolder).Error("Startup failed: " + message);
        }
        catch (InvalidOperationException ex)
        {
            Trace.WriteLine("Earshot log folder unavailable: " + ex.Message);
        }

        return exitCode;
    }
}
