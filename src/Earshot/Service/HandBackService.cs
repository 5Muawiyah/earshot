using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Service;

// The hand-back service: a Local System process that starts with Windows, does nothing while the computer is in use,
// and when Windows starts to shut down blocks the pinned AirPods if the tray icon did not already hand them back.
//
// It does one thing, and nothing else is reachable from it. Its only work is the shut-down block of
// GateActions.RunPreshutdown, which disables the pinned device's nodes and never enables one. It answers three
// controls: stop, interrogate, and the pre-shutdown control that only the system can send; the access list the
// install applies takes stop and interrogate away from every standard user, so only an administrator can send either. It opens no pipe, socket, RPC endpoint, COM server or
// window, and takes no request from the tray or any user: the setting it acts on and the device it blocks are read
// from the machine folder, which only administrators can write, after that folder's access list has been checked.
//
// The control handler must return at once, so the work runs on a thread of its own while the handler has already told
// the control manager the service is stopping. The status "stopped" is reported once, by the service's own main
// thread, as the last thing it does.
// https://learn.microsoft.com/en-us/windows/win32/services/service-control-handler-function
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setservicestatus
// https://learn.microsoft.com/en-us/windows/win32/services/writing-a-servicemain-function
internal sealed class HandBackService : IDisposable
{
    // The service's own limits, in milliseconds. They are the values the control manager is told: how long the
    // start-up may take, and how long a stop may take.
    internal const uint StartPendingWaitHintMs = 3_000;
    internal const uint StopPendingWaitHintMs = 10_000;

    // The controls it takes: stop, and the pre-shutdown control. Interrogate has no bit because every service takes it: the
    // control manager sends it to the handler like any other control, and the handler answers NO_ERROR without a report,
    // since the state has not changed.
    internal const uint AcceptedControls = AdvApi32.SERVICE_ACCEPT_STOP | AdvApi32.SERVICE_ACCEPT_PRESHUTDOWN;

    private const uint NoError = 0;

    private readonly IServiceHost _host;
    private readonly Func<IProcessToken> _token;
    private readonly IFolderSecurity _folders;
    private readonly string _installFolder;
    private readonly string _machineFolder;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Func<string, DateTimeOffset, PreshutdownResult> _preshutdown;
    private readonly Func<string> _baseDirectory;
    private readonly ManualResetEventSlim _stop = new(false);

    private nint _statusHandle;
    private int _checkPoint;
    private int _preshutdownStarted;
    private int _stoppedReported;
    private volatile bool _accepting;
    private Thread? _worker;

    // preshutdown runs the shut-down block: its nonce, and the time by which it must be finished. baseDirectory is the
    // folder the running program is in.
    public HandBackService(
        IServiceHost host, Func<IProcessToken> token, IFolderSecurity folders, string installFolder, string machineFolder,
        ILog log, TimeProvider time, Func<string, DateTimeOffset, PreshutdownResult> preshutdown, Func<string>? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineFolder);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(preshutdown);
        _host = host;
        _token = token;
        _folders = folders;
        _installFolder = installFolder;
        _machineFolder = machineFolder;
        _log = log;
        _time = time;
        _preshutdown = preshutdown;
        _baseDirectory = baseDirectory ?? (() => AppContext.BaseDirectory);
    }

    public void Dispose() => _stop.Dispose();

    // Why the service refused to start, as the exit code the process ends with; null when it started and stopped as it
    // should.
    public GateExitCode? Refusal { get; private set; }

    // Runs on the thread the control manager creates for the service.
    public void ServiceMain(string[] args)
    {
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            // Anything unexpected still ends with the status "stopped", with a code the control manager records as an
            // error, so a failed service is never left looking like a running one.
            _log.Error(HandBackServiceText.Prefix + "the service stopped with " + ex.GetType().Name + ".", ex);
            Refusal = GateExitCode.Failed;
            ReportStopped(AdvApi32.ERROR_SERVICE_SPECIFIC_ERROR, (uint)GateExitCode.Failed);
        }
    }

    private void Run()
    {
        // The handler comes first: nothing can be reported without it.
        _statusHandle = _host.RegisterHandler(ServicePlan.ServiceName, Handle, out uint registerError);
        if (_statusHandle == 0)
        {
            _log.Error(HandBackServiceText.HandlerNotRegistered(registerError));
            Refusal = GateExitCode.Failed;
            return;
        }

        Report(AdvApi32.SERVICE_START_PENDING, 0, NoError, 0, checkPoint: NextCheckPoint(), StartPendingWaitHintMs);

        GateExitCode? refused = CheckStart();
        if (refused is GateExitCode code)
        {
            Refusal = code;
            ReportStopped(AdvApi32.ERROR_SERVICE_SPECIFIC_ERROR, (uint)code);
            return;
        }

        LogMachineFolder();

        // Accepting controls only once the state is running: a control is not taken while the start is pending.
        _accepting = true;
        Report(AdvApi32.SERVICE_RUNNING, AcceptedControls, NoError, 0, checkPoint: 0, waitHint: 0);
        Volatile.Write(ref _checkPoint, 0);
        _log.Info(HandBackServiceText.Running(_baseDirectory()));

        _stop.Wait();

        // A shut-down block in progress is bounded by its own budget, and finishes before the service says it has stopped.
        Volatile.Read(ref _worker)?.Join();
        _log.Info(HandBackServiceText.Stopped);
        ReportStopped(NoError, 0);
    }

    // The three things that must hold before the service does anything: it runs as Local System, from the install
    // folder, and that folder cannot be written by anyone but an administrator. Null when all hold.
    private GateExitCode? CheckStart()
    {
        IProcessToken token;
        try
        {
            token = _token();
        }
        catch (System.Security.SecurityException ex)
        {
            _log.Error(HandBackServiceText.Refused("the process token could not be read", GateExitCode.NotElevated), ex);
            return GateExitCode.NotElevated;
        }

        if (!token.IsLocalSystem)
        {
            _log.Error(HandBackServiceText.Refused("not running as Local System", GateExitCode.NotElevated));
            return GateExitCode.NotElevated;
        }

        string baseDirectory = _baseDirectory();
        if (!IntegrityCopy.IsInside(baseDirectory, _installFolder))
        {
            _log.Error(HandBackServiceText.Refused("the program is not in the install folder", GateExitCode.NotFromInstallFolder));
            return GateExitCode.NotFromInstallFolder;
        }

        var steps = new List<StepOutcome>();
        if (!FolderTrust.IsTrusted(_folders, _installFolder, AclCheck.CheckInstallFolder, "install-folder-acl", steps))
        {
            foreach (StepOutcome step in steps.Where(s => !s.Ok))
            {
                _log.Error(HandBackServiceText.Prefix + GateActions.Describe(step));
            }

            _log.Error(HandBackServiceText.Refused("the install folder can be changed by someone other than an administrator", GateExitCode.FolderNotSecure));
            return GateExitCode.FolderNotSecure;
        }

        return null;
    }

    // What the machine folder looks like at start, for the log only. The check that counts is the one made at the
    // shut down, because the folder may be repaired or removed while the service runs.
    private void LogMachineFolder()
    {
        StepOutcome read = _folders.ReadSddl(_machineFolder, out string? sddl);
        if (!read.Ok)
        {
            _log.Info(HandBackServiceText.MachineFolder(
                read.Code == unchecked((int)0x80070003) ? "not present" : "failed its check: " + read.Step + " " + read.CodeName));
            return;
        }

        IReadOnlyList<string> problems = AclCheck.CheckMachineFolder(sddl);
        _log.Info(HandBackServiceText.MachineFolder(problems.Count == 0 ? "ok" : "failed its check: " + string.Join("; ", problems)));
    }

    // The control handler. It is called on the control manager's thread, must return at once, and takes no lock and makes
    // no blocking call. Anything but stop, interrogate and pre-shutdown is not implemented, whoever sent it.
    internal uint Handle(uint control, uint eventType, nint eventData)
    {
        if (!_accepting)
        {
            return AdvApi32.ERROR_CALL_NOT_IMPLEMENTED;
        }

        switch (control)
        {
            case AdvApi32.SERVICE_CONTROL_INTERROGATE:
                // The state has not changed, so nothing is reported.
                return NoError;

            case AdvApi32.SERVICE_CONTROL_STOP:
                _log.Info(HandBackServiceText.StopReceived);
                ReportStopPending();
                _stop.Set();
                return NoError;

            case AdvApi32.SERVICE_CONTROL_PRESHUTDOWN:
                StartPreshutdown();
                return NoError;

            default:
                _log.Write(LogLevel.Debug, HandBackServiceText.ControlNotHandled(control));
                return AdvApi32.ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    private void StartPreshutdown()
    {
        if (Interlocked.Exchange(ref _preshutdownStarted, 1) == 1)
        {
            _log.Info(HandBackServiceText.PreshutdownReceivedAgain);
            return;
        }

        // The budget runs from the moment the control arrived.
        DateTimeOffset arrived = _time.GetUtcNow();
        _log.Info(HandBackServiceText.PreshutdownReceived(arrived));
        ReportStopPending();
        var worker = new Thread(() => RunPreshutdown(arrived)) { IsBackground = false, Name = "Earshot preshutdown" };
        Volatile.Write(ref _worker, worker);
        worker.Start();
    }

    private void RunPreshutdown(DateTimeOffset arrived)
    {
        try
        {
            string nonce = Guid.NewGuid().ToString("N");
            PreshutdownResult result = _preshutdown(nonce, arrived + PreshutdownBudget.Total);
            LogResult(result, arrived);
        }
        catch (Exception ex)
        {
            _log.Error(HandBackServiceText.PreshutdownStopped(ex), ex);
        }
        finally
        {
            // ServiceMain reports "stopped" once this thread has ended.
            _stop.Set();
        }
    }

    private void LogResult(PreshutdownResult result, DateTimeOffset arrived)
    {
        TimeSpan elapsed = _time.GetUtcNow() - arrived;
        if (result.CutShortWaitingFor is { } waitingFor)
        {
            _log.Warn(HandBackServiceText.CutShort(elapsed, waitingFor));
        }
        else if (!result.BlockSent && result.Outcome == GateExitCode.Success)
        {
            _log.Info(HandBackServiceText.NothingToDo(result.Reason));
        }

        if (result.BlockSentUtc is { } sent)
        {
            _log.Info(HandBackServiceText.BlockSent(sent));
        }

        if (result.VetoedNodes > 0)
        {
            if (result.RetryAfter is { } after)
            {
                _log.Info(HandBackServiceText.Veto(result.VetoedNodes, after));
            }
            else
            {
                _log.Warn(HandBackServiceText.Prefix + "veto on " + result.VetoedNodes + " node(s); not sent again, too little time was left.");
            }
        }

        string line = HandBackServiceText.Finished(elapsed, HandBackServiceText.BlockClause(result), result.State, HandBackServiceText.StatusClause(result));
        if (result.Outcome == GateExitCode.Success)
        {
            _log.Info(line);
        }
        else
        {
            _log.Warn(line);
        }
    }

    private void ReportStopPending() =>
        Report(AdvApi32.SERVICE_STOP_PENDING, 0, NoError, 0, NextCheckPoint(), StopPendingWaitHintMs);

    private uint NextCheckPoint() => (uint)Interlocked.Increment(ref _checkPoint);

    // The last report, and the only one that says "stopped", made once whatever ended the service.
    private void ReportStopped(uint win32ExitCode, uint serviceSpecificExitCode)
    {
        if (Interlocked.Exchange(ref _stoppedReported, 1) == 1)
        {
            return;
        }

        Report(AdvApi32.SERVICE_STOPPED, 0, win32ExitCode, serviceSpecificExitCode, checkPoint: 0, waitHint: 0);
    }

    private void Report(uint state, uint controls, uint win32ExitCode, uint serviceSpecificExitCode, uint checkPoint, uint waitHint)
    {
        var report = new ServiceStatusReport(state, controls, win32ExitCode, serviceSpecificExitCode, checkPoint, waitHint);
        if (!_host.SetStatus(_statusHandle, in report, out uint error))
        {
            _log.Error(HandBackServiceText.StatusNotReported(state, error));
        }
    }
}

// The entry the service run mode calls: hands the process to the control manager and says how it ended.
internal static class HandBackServiceProgram
{
    public static GateExitCode Run(
        IServiceHost host, Func<IProcessToken> token, IFolderSecurity folders, string installFolder, string machineFolder,
        ILog log, TimeProvider time, Func<string, DateTimeOffset, PreshutdownResult> preshutdown, Func<string>? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(log);
        using var service = new HandBackService(host, token, folders, installFolder, machineFolder, log, time, preshutdown, baseDirectory);

        uint error = host.RunDispatcher(ServicePlan.ServiceName, service.ServiceMain);
        if (error == AdvApi32.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
        {
            // Started from a console or by a user: nothing was opened, read or written.
            log.Error(HandBackServiceText.NotStartedByTheControlManager(error));
            return GateExitCode.NotAService;
        }

        if (error != 0)
        {
            log.Error(HandBackServiceText.DispatcherFailed(error));
            return GateExitCode.Failed;
        }

        return service.Refusal ?? GateExitCode.Success;
    }
}
