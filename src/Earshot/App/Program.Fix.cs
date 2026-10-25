using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Diagnostics;
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
    internal const string FixOutputLogPrefix = "fix output: ";

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

        // The tray and Setup.exe start this verb with no console and no --out: the lines go to the log then, since the run's own
        // window shows the person the result.
        if (!CommandOutput.TryOpen(outPath, new LogFallback(log, FixOutputLogPrefix), out CommandOutput? output, out string? outputProblem))
        {
            log.Error("fix: " + outputProblem);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        using (output)
        {
            // One run at a time on this PC: a Fix started from the tray, from Setup.exe and from the install script is three
            // programs, so the lock is a named mutex. The run that finds it taken starts nothing and says nothing else.
            using FixRunLock? runLock = FixRunLock.TryAcquire(FixRunLock.NameFor(paths), log);
            if (runLock is null)
            {
                log.Warn("fix: another Fix connection or Pair AirPods run holds the lock, so this run was not started.");
                output.Writer.WriteLine(FixRunLock.FixAlreadyRunningMessage);
                ctx.ExitCode = ExitCodes.AlreadyRunning;
                return;
            }

            try
            {
                ctx.ExitCode = RunFix(ctx.Args, paths, log, output.Writer);
            }
            finally
            {
                // However the run was started, the tray hears that it ended and runs its start checks again: a run can end with a
                // new pair Earshot does not protect.
                FixEndedSignal.Raise(FixEndedSignal.NameFor(paths), log);
            }
        }
    }

    // The message loop is the UI thread's, because the dialogs are TaskDialogs and the re-pair has a window. The flow itself
    // is async and continues on that thread after each await.
    private static int RunFix(IReadOnlyList<string> args, Paths paths, ILog log, TextWriter output)
    {
        using var cancel = new CancellationTokenSource();
        using var ui = new FixRePairWindow(cancel);
        using var worker = new SystemWorker(log);
        IProcessToken token = WindowsProcessToken.Current();
        FixElevation elevation = ChooseFixElevation(paths, token, log);
        if (elevation.Refusal is { } refusal)
        {
            // Surfaced with the raw code. Nothing was asked and nothing changed.
            log.Warn("fix: " + GateActions.Describe(refusal));
            output.WriteLine(FixConnection.NoProtectedFolderNote + " (" + refusal.CodeName + ", " + refusal.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
            return ExitCodes.Software;
        }

        try
        {
            return RunFix(args, paths, log, output, cancel, ui, worker, elevation);
        }
        finally
        {
            if (elevation.PlanRoot is not null)
            {
                FixPlanRoot.Remove(elevation.PlanRoot, log);
            }
        }
    }

    // Already an elevated administrator (Setup.exe runs this verb that way): the apply step runs in this process and the plan
    // goes to a folder only administrators can reach, so there is no second administrator prompt. Safe mode and a redirected
    // data root keep the ordinary pair, whose apply step the fix-apply mode refuses there.
    private static FixElevation ChooseFixElevation(Paths paths, IProcessToken token, ILog log)
    {
        var ordinaryFiles = new FixPlanFiles(Path.Combine(paths.LocalFolder, "fix"));
        var prompting = new ShellRunasFixElevator(new ShellRunasLauncher(), File.Exists(paths.InstalledExe) ? paths.InstalledExe : Environment.ProcessPath ?? paths.InstalledExe);
        if (paths.IsSafeMode || paths.IsRedirected)
        {
            return new FixElevation(ordinaryFiles, prompting, false, null, null);
        }

        return FixElevationChoice.Choose(
            token, new NtfsFolderSecurity(), paths.MachineFolder, Guid.NewGuid(),
            (planPath, planSha256) => ApplyFixInProcess(planPath, planSha256, paths, token, log, request => FixApply.Run(request, CreateFixApplyDeps(paths, log))),
            ordinaryFiles, prompting);
    }

    private static int RunFix(IReadOnlyList<string> args, Paths paths, ILog log, TextWriter output, CancellationTokenSource cancel, FixRePairWindow ui, SystemWorker worker, FixElevation elevation)
    {
        FixHost host = CreateFixHost(paths, log, output, ui, worker, elevation);

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

    // Safe mode against the real data folder writes nothing, the connect history included; any other run has a history file.
    internal static ConnectHistory? OpenConnectHistory(Paths paths, ILog log) =>
        paths.IsSafeMode && !paths.IsRedirected ? null : new ConnectHistory(paths.ConnectHistoryFile, TimeProvider.System, log);

    internal static FixHost CreateFixHost(Paths paths, ILog log, TextWriter output, FixRePairWindow ui, SystemWorker worker, FixElevation elevation)
    {
        var nodes = new CfgMgr32NodeReader();
        var store = new GateStore(paths.MachineFolder);
        var records = new StoreFixRecords(store, FixFootprint.For(paths));
        var service = new WindowsServiceControl();

        // Earshot installed: the gate task is reached the way the tray reaches it. Otherwise there is nothing to ask.
        IFixGate gate = FixRules.ShouldEndWithBlock(records.ReadDevice(), records.ReadConfig())
            ? new TaskGateFixGate(worker, CreateTaskGate(paths, store, log), store, nodes, log)
            : new NoInstalledGate();

        IInputDesktop desktop = paths.IsSafeMode || paths.IsRedirected ? new NobodyThere() : new WindowsInputDesktop();
        return new FixHost
        {
            Nodes = nodes,
            Records = records,
            Leftovers = MachineLeftovers.For(paths, service),
            Desktop = desktop,
            Prompter = new TaskDialogPrompter(),
            PlanFiles = elevation.PlanFiles,
            Elevator = elevation.Elevator,
            Pairing = new WinRtPairingApi(),
            RePairUi = ui,
            Gate = gate,
            Recovery = new FixRecovery(Path.Combine(paths.LocalFolder, "fix-window.json"), TimeProvider.System, ProcessIsAlive),
            Radio = RadioCheck.CreateReal(log),
            History = OpenConnectHistory(paths, log),
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

    // The apply step of an already-elevated run: the checks of the fix-apply mode (safe mode and a redirected data root, the
    // token, the environment, the shape of the arguments) are the same ones, because this goes through the same entry point.
    internal static int ApplyFixInProcess(string planPath, string planSha256, Paths paths, IProcessToken token, ILog log, Func<FixApplyRequest, FixApplyResult> run, IEnumerable<string>? environmentNames = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        string? refusal = FixApplyPathsRefusal(paths);
        if (refusal is not null)
        {
            log.Warn(refusal);
            return ExitCodes.Refused;
        }

        return (int)RunFixApply([FixApply.Verb, planPath, planSha256], token, log, run, environmentNames);
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
