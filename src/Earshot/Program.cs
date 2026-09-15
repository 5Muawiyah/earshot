using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
//   install ... / uninstall           TryRunInstall, TryRunUninstall
//   probe [target] [--json] [--out]   TryRunProbe      App\Program.Probe.cs
//   diag <target> ...                 TryRunDiag       App\Program.Diag.cs
//
// Dispatch runs before WinForms is initialised and before the single-instance mutex, so the
// headless modes never touch WinForms and install is never mistaken for a second tray.
// A hook that is not implemented is removed by the compiler; the mode then reports
// "Not available in this build." and exits with ExitCodes.Unavailable.
internal static partial class Program
{
    internal sealed class RunContext
    {
        public required string[] Args { get; init; }

        // Set by the hook that ran, including 0 for success. A headless mode that leaves it null
        // was not implemented in this build.
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

        try
        {
            _ = Paths.Current;
        }
        catch (InvalidOperationException ex)
        {
            return StartupFailure(ExitCodes.Config, ex.Message);
        }

        var ctx = new RunContext { Args = args };
        string mode = args.Length == 0 ? "" : args[0];
        switch (mode)
        {
            case "gate":      TryRunGate(ctx); break;
            case "install":   TryRunInstall(ctx); break;
            case "uninstall": TryRunUninstall(ctx); break;
            case "probe":     TryRunProbe(ctx); break;
            case "diag":      TryRunDiag(ctx); break;
            default:          TryRunTray(ctx); return ctx.ExitCode ?? ExitCodes.Ok;
        }

        if (ctx.ExitCode is int code)
        {
            return code;
        }

        new FileLog(Paths.Current.LogFolder).Error("'" + mode + "': " + NotAvailableMessage);
        return ExitCodes.Unavailable;
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
