using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot;

// What a read of a registered task returned. Sddl, Xml, LastTaskResult or LastRunTime is null when that
// read failed, and Steps holds the failure with its HRESULT.
internal sealed record TaskReadback(string? Sddl, string? Xml, int State, int? LastTaskResult, double? LastRunTime, IReadOnlyList<StepOutcome> Steps);

// The state read while waiting for a run. LastTaskResult or LastRunTime is null when that read failed, and
// Steps holds the failure with its HRESULT.
internal sealed record TaskRunState(int State, int? LastTaskResult, double? LastRunTime)
{
    public IReadOnlyList<StepOutcome> Steps { get; init; } = [];
}

// Task Scheduler reads and RunEx as the tray uses them, non-elevated. Every method returns the HRESULT; a
// missing task is 0x80070002.
internal interface IScheduledTasks
{
    int ReadTask(string taskPath, out TaskReadback? task);

    int ReadRunState(string taskPath, out TaskRunState? state);

    int Run(string taskPath, string[] parameters);
}

// Connects to the local Task Scheduler on the calling thread (a SystemWorker or install's worker) and
// releases every object it creates before returning.
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskservice-connect
internal static class ComTaskScheduler
{
    public const int SecurityInformation =
        TaskSchedulerCom.OWNER_SECURITY_INFORMATION | TaskSchedulerCom.GROUP_SECURITY_INFORMATION | TaskSchedulerCom.DACL_SECURITY_INFORMATION;

    public static int WithService(Func<ITaskService, int> action)
    {
        int hr = TaskSchedulerCom.TryCreateService(out ITaskService? service);
        if (hr < 0 || service is null)
        {
            return hr < 0 ? hr : ComActivation.E_POINTER;
        }

        try
        {
            hr = service.Connect(null, null, null, null);
            return hr < 0 ? hr : action(service);
        }
        finally
        {
            Marshal.ReleaseComObject(service);
        }
    }

    public static int WithFolder(string folderPath, Func<ITaskService, ITaskFolder, int> action) =>
        WithService(service =>
        {
            int hr = service.GetFolder(folderPath, out ITaskFolder? folder);
            if (hr < 0 || folder is null)
            {
                return hr < 0 ? hr : ComActivation.E_POINTER;
            }

            try
            {
                return action(service, folder);
            }
            finally
            {
                Marshal.ReleaseComObject(folder);
            }
        });

    public static int WithTask(string taskPath, Func<IRegisteredTask, int> action) =>
        WithFolder("\\", (_, root) =>
        {
            int hr = root.GetTask(taskPath, out IRegisteredTask? task);
            if (hr < 0 || task is null)
            {
                return hr < 0 ? hr : ComActivation.E_POINTER;
            }

            try
            {
                return action(task);
            }
            finally
            {
                Marshal.ReleaseComObject(task);
            }
        });

    // GetTask then the security descriptor, XML, state and last result. Opening the task is the call that
    // decides the HRESULT; a failed property read is a step and leaves that value empty.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-getsecuritydescriptor
    public static int ReadTask(string taskPath, out TaskReadback? readback)
    {
        TaskReadback? result = null;
        int hr = WithTask(taskPath, task =>
        {
            var steps = new List<StepOutcome>();
            int sdHr = task.GetSecurityDescriptor(SecurityInformation, out string? sddl);
            if (sdHr < 0)
            {
                steps.Add(StepOutcomes.FromHResult("task-sddl:" + taskPath, sdHr));
                sddl = null;
            }

            int xmlHr = task.get_Xml(out string? xml);
            if (xmlHr < 0)
            {
                steps.Add(StepOutcomes.FromHResult("task-xml:" + taskPath, xmlHr));
                xml = null;
            }

            int stateHr = task.get_State(out int state);
            if (stateHr < 0)
            {
                steps.Add(StepOutcomes.FromHResult("task-state:" + taskPath, stateHr));
                state = TaskSchedulerCom.TASK_STATE_UNKNOWN;
            }

            (int? last, double? lastRun) = ReadLastRun(task, taskPath, steps);
            result = new TaskReadback(sddl, xml, state, last, lastRun, steps);
            return 0;
        });
        readback = result;
        return hr;
    }

    // LastTaskResult and LastRunTime, each null with its own step when the read fails. A failed read is
    // never shown as 0: 0 would read as a successful result, or as a run time the wait compares against.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-get_lastruntime
    public static (int? LastTaskResult, double? LastRunTime) ReadLastRun(IRegisteredTask task, string taskPath, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(steps);
        int lastHr = task.get_LastTaskResult(out int last);
        if (lastHr < 0)
        {
            steps.Add(StepOutcomes.FromHResult("task-last-result-read:" + taskPath, lastHr));
        }

        int runHr = task.get_LastRunTime(out double lastRun);
        if (runHr < 0)
        {
            steps.Add(StepOutcomes.FromHResult("task-last-run-time-read:" + taskPath, runHr));
        }

        return (lastHr < 0 ? null : last, runHr < 0 ? null : lastRun);
    }
}

internal sealed class ComScheduledTasks : IScheduledTasks
{
    public int ReadTask(string taskPath, out TaskReadback? task) => ComTaskScheduler.ReadTask(taskPath, out task);

    public int ReadRunState(string taskPath, out TaskRunState? state)
    {
        TaskRunState? result = null;
        int hr = ComTaskScheduler.WithTask(taskPath, task =>
        {
            int stateHr = task.get_State(out int taskState);
            if (stateHr < 0)
            {
                return stateHr;
            }

            var steps = new List<StepOutcome>();
            (int? last, double? lastRun) = ComTaskScheduler.ReadLastRun(task, taskPath, steps);
            result = new TaskRunState(taskState, last, lastRun) { Steps = steps };
            return 0;
        });
        state = result;
        return hr;
    }

    // RunEx with a SAFEARRAY of BSTR: $(Arg0) is the verb, $(Arg1) the nonce, $(Arg2) the address for
    // set-device. Flags 0, session 0, user null.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
    public int Run(string taskPath, string[] parameters) =>
        ComTaskScheduler.WithTask(taskPath, task =>
        {
            int hr = task.RunEx(parameters, TaskSchedulerCom.TASK_RUN_NO_FLAGS, 0, null, out IRunningTask? running);
            if (running is not null)
            {
                Marshal.ReleaseComObject(running);
            }

            return hr;
        });
}

internal enum TaskHealth
{
    Ready,          // present, and its SDDL and XML match what install registers
    Missing,        // 0x80070002: not set up
    NeedsRepair,    // present, read in full, and its security or definition differs
    Unreadable,     // any other failure to open it, or to read its security descriptor or XML
}

internal sealed record TaskVerification(string TaskName, TaskHealth Health, IReadOnlyList<string> Problems, IReadOnlyList<StepOutcome> Steps, TaskReadback? Task);

internal enum GateRunOutcome
{
    Completed,      // the gate ran; see Status and the node read
    NotSetUp,
    NeedsRepair,
    RunFailed,      // RunEx or a state read failed
    TaskUnreadable, // the task could not be read before RunEx, so nothing was sent
    TaskDisabled,   // RunEx on a disabled task returns S_OK and does nothing
    TimedOut,
    Cancelled,
}

internal sealed record GateRunResult(GateRunOutcome Outcome, GateStatusFile? Status, int? LastTaskResult, IReadOnlyList<StepOutcome> Steps);

// The tray's side of the elevation boundary. Before each RunEx it checks the task read-only (the SDDL grants
// no one else write and grants this user execute, the principal is SYSTEM or this user, the action runs
// %ProgramFiles%\Earshot\Earshot.exe with the fixed arguments) and refuses a task that fails. After RunEx it
// polls until the task has left QUEUED and RUNNING and the run is visible (its status file, or a new
// LastRunTime), within a timeout. The status file and LastTaskResult are advisory; the caller reads the
// real node state afterwards.
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-get_state
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-get_lasttaskresult
internal sealed class TaskSchedulerGate
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // The task's own ExecutionTimeLimit plus a margin for the scheduler to start and stop it.
    public static readonly TimeSpan GateTimeout = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProtectTimeout = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30);

    private readonly IScheduledTasks _tasks;
    private readonly GateStore _store;
    private readonly string _installFolder;
    private readonly string? _userSid;
    private readonly Func<string, AccountLookup> _accountToSid;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, bool> _wait;
    private readonly ILog? _log;

    // wait returns false when cancelled. log, when given, gets one line with a UTC time as soon as each RunEx
    // returns, before the run is polled, so a request sent just before the process ended (a session end) is on
    // record even though its result never is.
    public TaskSchedulerGate(
        IScheduledTasks tasks,
        GateStore store,
        string installFolder,
        string? userSid,
        Func<string, AccountLookup> accountToSid,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, bool> wait,
        ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        ArgumentNullException.ThrowIfNull(accountToSid);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(wait);
        _tasks = tasks;
        _store = store;
        _installFolder = installFolder;
        _userSid = userSid;
        _accountToSid = accountToSid;
        _time = time;
        _wait = wait;
        _log = log;
    }

    public static bool WaitOrCancelled(TimeSpan delay, CancellationToken ct) => !ct.WaitHandle.WaitOne(delay);

    public TaskVerification Verify(string taskName)
    {
        string path = TaskPlan.TaskPath(taskName);
        var steps = new List<StepOutcome>();
        int hr = _tasks.ReadTask(path, out TaskReadback? task);
        if (hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND)
        {
            steps.Add(StepOutcomes.FromHResult("task-open:" + path, hr, "Not set up."));
            return new TaskVerification(taskName, TaskHealth.Missing, ["The task is not registered."], steps, null);
        }

        if (hr < 0 || task is null)
        {
            steps.Add(StepOutcomes.FromHResult("task-open:" + path, hr < 0 ? hr : ComActivation.E_POINTER));
            return new TaskVerification(taskName, TaskHealth.Unreadable, ["The task could not be read."], steps, null);
        }

        steps.AddRange(task.Steps);

        // A security descriptor or definition that could not be read (its failed step is in task.Steps) says nothing
        // about the task, so it is neither ready nor in need of repair.
        if (task.Sddl is null || task.Xml is null)
        {
            return new TaskVerification(taskName, TaskHealth.Unreadable, ["The task's security or definition could not be read."], steps, task);
        }

        if (!Sddl.IsUserSid(_userSid))
        {
            return new TaskVerification(taskName, TaskHealth.NeedsRepair, ["The current user has no usable SID."], steps, task);
        }

        bool userMayRun = !string.Equals(taskName, TaskPlan.BootTaskName, StringComparison.Ordinal);
        var problems = AclCheck.CheckTask(task.Sddl, _userSid!, userMayRun)
            .Concat(TaskXmlCheck.VerifyInstalled(task.Xml, taskName, _installFolder, _userSid!, _accountToSid, steps))
            .ToList();
        return new TaskVerification(taskName, problems.Count == 0 ? TaskHealth.Ready : TaskHealth.NeedsRepair, problems, steps, task);
    }

    public GateRunResult Run(string taskName, string verb, string nonce, string? address, TimeSpan timeout, CancellationToken ct)
    {
        if (!GateVerbs.All.Contains(verb) || verb == GateVerbs.Boot || !BoundaryValidation.IsNonce(nonce) ||
            (verb == GateVerbs.SetDevice) != (address is not null) ||
            (address is not null && !BoundaryValidation.IsAddress12(address)))
        {
            throw new ArgumentException("Not a request the tray sends: " + verb + ".", nameof(verb));
        }

        // The Protect task runs gate-protect, which takes only the protect verbs; the Gate task refuses them.
        string expectedTask = GateModes.IsProtectVerb(verb) ? TaskPlan.ProtectTaskName : TaskPlan.GateTaskName;
        if (!string.Equals(taskName, expectedTask, StringComparison.Ordinal))
        {
            throw new ArgumentException(verb + " is sent to " + TaskPlan.TaskPath(expectedTask) + ", not " + taskName + ".", nameof(taskName));
        }

        TaskVerification check = Verify(taskName);
        var steps = new List<StepOutcome>(check.Steps);
        foreach (string problem in check.Problems)
        {
            steps.Add(StepOutcomes.NotAttempted("task-check:" + taskName, problem));
        }

        switch (check.Health)
        {
            case TaskHealth.Missing:
                return new GateRunResult(GateRunOutcome.NotSetUp, null, null, steps);
            case TaskHealth.NeedsRepair:
                return new GateRunResult(GateRunOutcome.NeedsRepair, null, null, steps);
            case TaskHealth.Unreadable:
                return new GateRunResult(GateRunOutcome.TaskUnreadable, null, null, steps);
        }

        TaskReadback before = check.Task!;
        if (before.State == TaskSchedulerCom.TASK_STATE_DISABLED)
        {
            steps.Add(StepOutcomes.NotAttempted("task-run:" + taskName, "The task is disabled."));
            return new GateRunResult(GateRunOutcome.TaskDisabled, null, null, steps);
        }

        string path = TaskPlan.TaskPath(taskName);
        string[] parameters = address is null ? [verb, nonce] : [verb, nonce, address];
        int hr = _tasks.Run(path, parameters);
        StepOutcome sent = StepOutcomes.FromHResult("task-run:" + path, hr, verb + " " + nonce);
        steps.Add(sent);
        _log?.Write(sent.Ok ? LogLevel.Info : LogLevel.Warn,
            "RunEx " + path + " " + verb + " " + nonce + " returned " + sent.CodeName + " at " +
            _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + ".");
        if (hr < 0)
        {
            return new GateRunResult(
                hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND ? GateRunOutcome.NotSetUp : GateRunOutcome.RunFailed,
                null, null, steps);
        }

        // A LastRunTime that could not be read, before the run or now, is never compared: the run is then seen
        // only through its status file, and a gate that exits without one ends at the timeout.
        long started = _time.GetTimestamp();
        var readFailures = new HashSet<(string Step, int Code)>();
        while (true)
        {
            GateRead<GateStatusFile> status = _store.ReadStatus(nonce);
            hr = _tasks.ReadRunState(path, out TaskRunState? state);
            if (hr < 0 || state is null)
            {
                steps.Add(StepOutcomes.FromHResult("task-state:" + path, hr < 0 ? hr : ComActivation.E_POINTER));
                return new GateRunResult(GateRunOutcome.RunFailed, status.Value, null, steps);
            }

            // Each distinct failed read is recorded once, not once per poll.
            steps.AddRange(state.Steps.Where(s => readFailures.Add((s.Step, s.Code))));

            bool running = state.State is TaskSchedulerCom.TASK_STATE_QUEUED or TaskSchedulerCom.TASK_STATE_RUNNING;
            bool newRunTime = before.LastRunTime is double was && state.LastRunTime is double now && now != was;
            bool ran = status.Status != GateReadStatus.Missing || newRunTime;
            if (!running && ran)
            {
                if (status.Status == GateReadStatus.Missing)
                {
                    // The status file is read before the run state, so a gate that wrote it and exited between
                    // those two reads would otherwise lose its per-node outcomes.
                    status = _store.ReadStatus(nonce);
                }

                if (status.Status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
                {
                    steps.Add(status.Step);
                }

                steps.Add(LastResultStep(state.LastTaskResult));
                return new GateRunResult(GateRunOutcome.Completed, status.Value, state.LastTaskResult, steps);
            }

            if (state.State == TaskSchedulerCom.TASK_STATE_DISABLED)
            {
                steps.Add(StepOutcomes.NotAttempted("task-run:" + taskName, "The task is disabled."));
                return new GateRunResult(GateRunOutcome.TaskDisabled, null, null, steps);
            }

            if (_time.GetElapsedTime(started) >= timeout)
            {
                steps.Add(StepOutcomes.NotAttempted("task-wait:" + taskName,
                    "No result within " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s."));
                return new GateRunResult(GateRunOutcome.TimedOut, status.Value, null, steps);
            }

            if (!_wait(PollInterval, ct))
            {
                steps.Add(StepOutcomes.NotAttempted("task-wait:" + taskName, "Stopped waiting; the request was already sent."));
                return new GateRunResult(GateRunOutcome.Cancelled, status.Value, null, steps);
            }
        }
    }

    // LastTaskResult is advisory: that it carries the gate's exit code is undocumented. ExitCodes.Refused is
    // what Program.Dispatch returns when EARSHOT_SAFE_MODE or EARSHOT_DATA_ROOT reaches the gate's
    // environment, so it is named apart from a gate failure.
    internal static StepOutcome LastResultStep(int? lastTaskResult)
    {
        if (lastTaskResult is not int last)
        {
            return StepOutcomes.NotAvailable("task-last-result", "LastTaskResult could not be read. Advisory only.");
        }

        if (last == ExitCodes.Refused)
        {
            return new StepOutcome("task-last-result", false, last, "refused-by-environment",
                "Earshot refuses gate runs while EARSHOT_SAFE_MODE or EARSHOT_DATA_ROOT is set. Advisory only.");
        }

        string name = GateExitCodes.NameOf(last) ?? NativeCodes.Name(last);
        return new StepOutcome("task-last-result", last == 0, last, name, "Advisory only.");
    }
}
