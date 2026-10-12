using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Repair;
using Earshot.Service;

namespace Earshot;

// The Fix connection mode.
//
//   fix [--out <path>]
//
// Runs in the owner's session without elevation: it plans from reads, asks on the screen, and only after an answer starts the
// elevated fix-apply mode (one administrator prompt) and, after a second answer, removes and pairs the AirPods again.
// FixConnection has the steps. It takes no option that says yes; with nobody at the screen it prints the plan and ends with
// ExitCodes.PlanOnly. In safe mode it never counts anyone as present, so it only prints.
internal static partial class Program
{
    // The dispatcher declares this hook; this line repeats it so the file builds on its own.
    static partial void TryRunFix(RunContext ctx);

    static partial void TryRunFix(RunContext ctx)
    {
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);

        string? outPath = null;
        for (int i = 1; i + 1 < ctx.Args.Length; i++)
        {
            if (ctx.Args[i] == "--out")
            {
                outPath = ctx.Args[i + 1];
            }
        }

        if (!FixConnection.TryParseArgs(ctx.Args, out string problem))
        {
            log.Warn("fix: " + problem);
            ctx.ExitCode = ReportUsageError(log, "fix", problem, FixConnection.Usage);
            return;
        }

        if (!CommandOutput.TryOpen(outPath, out CommandOutput? output, out string? outputProblem))
        {
            log.Error("fix: " + outputProblem);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        using (output)
        {
            ctx.ExitCode = RunFix(ctx.Args, paths, log, output.Writer);
        }
    }

    // The message loop is the UI thread's, because the dialogs are TaskDialogs and the re-pair has a window. The flow itself
    // is async and continues on that thread after each await.
    private static int RunFix(IReadOnlyList<string> args, Paths paths, ILog log, TextWriter output)
    {
        using var cancel = new CancellationTokenSource();
        using var ui = new FixRePairWindow(cancel);
        using var worker = new SystemWorker(log);
        FixHost host = CreateFixHost(paths, log, output, ui, worker);

        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var context = new ApplicationContext();
        FixResult? result = null;
        EventHandler? idle = null;
        idle = (_, _) =>
        {
            Application.Idle -= idle;
            _ = StartAsync();
        };

        async Task StartAsync()
        {
            try
            {
                result = await FixConnection.RunAsync(args, host, cancel.Token);
            }
            catch (Exception ex)
            {
                // Surfaced: the exit code says it failed, and the log has the type and the raw HRESULT.
                log.Error("fix: stopped with " + ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").", ex);
                output.WriteLine("Fix connection stopped: " + ex.GetType().Name + ".");
            }
            finally
            {
                context.ExitThread();
            }
        }

        Application.Idle += idle;
        Application.Run(context);
        return result?.ExitCode ?? ExitCodes.Software;
    }

    private static FixHost CreateFixHost(Paths paths, ILog log, TextWriter output, FixRePairWindow ui, SystemWorker worker)
    {
        var nodes = new CfgMgr32NodeReader();
        var store = new GateStore(paths.MachineFolder);
        var records = new StoreFixRecords(store, FixFootprint.For(paths));
        var service = new WindowsServiceControl();

        // Earshot installed: the gate task is reached the way the tray reaches it. Otherwise there is nothing to ask.
        IFixGate gate = FixRules.ShouldEndWithBlock(records.ReadDevice(), records.ReadConfig())
            ? new TaskGateFixGate(worker, CreateTaskGate(paths, store, log), store, nodes, log)
            : new NoInstalledGate();

        string executable = File.Exists(paths.InstalledExe) ? paths.InstalledExe : Environment.ProcessPath ?? paths.InstalledExe;
        IInputDesktop desktop = paths.IsSafeMode || paths.IsRedirected ? new NobodyThere() : new WindowsInputDesktop();
        return new FixHost
        {
            Nodes = nodes,
            Records = records,
            Leftovers = MachineLeftovers.For(paths, service),
            Desktop = desktop,
            Prompter = new TaskDialogPrompter(),
            PlanFiles = new FixPlanFiles(Path.Combine(paths.LocalFolder, "fix")),
            Elevator = new ShellRunasFixElevator(new ShellRunasLauncher(), executable),
            Pairing = new WinRtPairingApi(),
            RePairUi = ui,
            Gate = gate,
            Recovery = new FixRecovery(Path.Combine(paths.LocalFolder, "fix-window.json"), TimeProvider.System, ProcessIsAlive),
            Output = output,
            Log = log,
            MachineFolder = paths.MachineFolder,
        };
    }

    private static TaskSchedulerGate CreateTaskGate(Paths paths, GateStore store, ILog log)
    {
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        return new TaskSchedulerGate(new ComScheduledTasks(), store, paths.InstallFolder, sid, AccountSids.Translate, TimeProvider.System, TaskSchedulerGate.WaitOrCancelled, log);
    }

    internal static bool ProcessIsAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById: no process with that id is running.
            return false;
        }
    }

    // Safe mode and a redirected data root never count anyone as present, so the run prints and stops.
    private sealed class NobodyThere : IInputDesktop
    {
        public DesktopReading Read() => new(UserInteractive: false, ThreadDesktop: null, InputDesktop: null, InputError: 0);
    }
}
