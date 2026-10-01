using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

internal enum TrayInstance
{
    NotRunning,
    Running,

    // The check itself failed, so nothing is known: a tray is not started on a guess.
    Unknown,
}

internal sealed record TrayProbe(TrayInstance State, StepOutcome Step);

// Whether an Earshot tray is running in this session. It reads the tray's own single-instance lock, so it answers the
// question a second start would be answered with.
internal interface ITrayInstanceProbe
{
    TrayProbe Probe();
}

// https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex.tryopenexisting
// The tray holds a named mutex for its whole life, created for the current user and the current session. This process is
// elevated and may run as another account (a different administrator, or the separate administrator account that
// Administrator protection uses), so it opens the name for the session only: a mutex that exists but whose security does not
// let this account open it is still a mutex that exists, so a refused open counts as running. Opening it takes no ownership
// and signals nothing.
internal sealed class MutexTrayInstanceProbe(string instanceName) : ITrayInstanceProbe
{
    public const string Step = "install-tray-probe";

    private static readonly NamedWaitHandleOptions SessionOnly = new() { CurrentSessionOnly = true, CurrentUserOnly = false };

    public TrayProbe Probe()
    {
        try
        {
            if (Mutex.TryOpenExisting(instanceName, SessionOnly, out Mutex? existing))
            {
                existing.Dispose();
                return new TrayProbe(TrayInstance.Running, new StepOutcome(Step, true, 0, "S_OK", "The tray's single-instance lock is held."));
            }

            return new TrayProbe(TrayInstance.NotRunning, new StepOutcome(Step, true, 0, "S_OK", "No tray holds the single-instance lock."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TrayProbe(TrayInstance.Running,
                StepOutcomes.FromHResult(Step, ex.HResult, "The single-instance lock exists and this account may not open it, so a tray is running: " + ex.Message, ok: true));
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            return new TrayProbe(TrayInstance.Unknown, StepOutcomes.FromHResult(Step, ex.HResult, ex.Message, ok: false));
        }
        catch (IOException ex)
        {
            return new TrayProbe(TrayInstance.Unknown, StepOutcomes.FromHResult(Step, ex.HResult, ex.Message, ok: false));
        }
        catch (ArgumentException ex)
        {
            return new TrayProbe(TrayInstance.Unknown, StepOutcomes.FromHResult(Step, ex.HResult, ex.Message, ok: false));
        }
    }
}

// Runs a registered task now, and reads how it stands. Every method returns the HRESULT.
internal interface ITaskRunner
{
    int RunNow(string taskPath);

    int ReadResult(string taskPath, out int state, out int? lastTaskResult);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
// RunEx with no parameters, no flags, no session and no user is what Run does: the scheduler starts the task for its own
// principal, which for an interactive-token principal is that user's signed-in session.
internal sealed class ComTaskRunner : ITaskRunner
{
    public int RunNow(string taskPath) =>
        ComTaskScheduler.WithTask(taskPath, task =>
        {
            int hr = task.RunEx(null, TaskSchedulerCom.TASK_RUN_NO_FLAGS, 0, null, out IRunningTask? running);
            if (running is not null)
            {
                Marshal.ReleaseComObject(running);
            }

            return hr;
        });

    public int ReadResult(string taskPath, out int state, out int? lastTaskResult)
    {
        int readState = TaskSchedulerCom.TASK_STATE_UNKNOWN;
        int? last = null;
        int hr = ComTaskScheduler.WithTask(taskPath, task =>
        {
            int stateHr = task.get_State(out readState);
            if (stateHr < 0)
            {
                readState = TaskSchedulerCom.TASK_STATE_UNKNOWN;
                return stateHr;
            }

            int lastHr = task.get_LastTaskResult(out int result);
            if (lastHr >= 0)
            {
                last = result;
            }

            return lastHr;
        });
        state = readState;
        lastTaskResult = last;
        return hr;
    }
}

// Starts the tray again, not elevated, in the signed-in user's session, once an update's install has finished.
//
// The install runs elevated, and a program an elevated process starts has the same elevated token, which the tray must
// never run with. So the tray is started the way Task Scheduler starts any task for a user: a one-shot task is
// registered in the \Earshot folder (only administrators and SYSTEM can write there) for the user whose SID the install
// was given, with the interactive-token logon type and the least run level, whose one action is the installed Earshot.exe
// with no arguments, and it is run at once and removed. Nothing the signed-in user can write is read or run: the user
// is a SID the install already checked, the program is in the administrators-only install folder, and the task's own
// security gives the user read only.
// https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-registertaskdefinition
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-deletetask
//
// It starts no tray when one is already running in this session (the single-instance lock is the probe), and it is called
// only after the install has registered the tasks and the service, so the tray it starts finds everything in place and
// blocks idle AirPods as any tray does. A tray started here is a start by hand, not the start at sign-in, so it takes no
// argument.
//
// Nothing here fails the install. A user who is not signed in, a task that will not register or run, or a tray that does not
// appear is a step with its raw code, a log line, and the sentence that Earshot starts at the next sign-in. Whether the
// started tray survives its task being removed is not documented, so the step after the removal says what was seen.
internal sealed class TrayRestarter
{
    public const string Step = "install-start-tray";

    // How long the new tray is waited for after its task was run: sixty looks a quarter of a second apart. A waiting
    // budget chosen here, not a measured figure.
    public const int SeenPolls = 60;

    public static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    // How many times the tray is looked at after its task was removed, PollEvery apart: eight looks cover about two seconds.
    // The documentation does not say whether, or how soon, removing a task ends an instance it started, so one look at the
    // moment of removal could find a tray that is about to end. A waiting budget chosen here, not a measured figure.
    public const int SurvivalLooks = 8;

    private readonly ITrayInstanceProbe _probe;
    private readonly ITaskRegistrar _tasks;
    private readonly ITaskRunner _runner;
    private readonly ILog _log;

    public TrayRestarter(ITrayInstanceProbe probe, ITaskRegistrar tasks, ITaskRunner runner, ILog log)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(log);
        _probe = probe;
        _tasks = tasks;
        _runner = runner;
        _log = log;
    }

    // For tests: the wait between two looks for the new tray. Returning false stops looking at once.
    internal Func<TimeSpan, bool> Wait { get; init; } = DeviceChangeLock.SleepAndContinue;

    private const string NextSignIn =
        "Earshot starts at your next sign-in if Open on startup is on, or when you start it.";

    // The steps, ending with one named Step whose Ok says whether a tray was started and seen. Never throws for a failed
    // call: each is a step.
    public IReadOnlyList<StepOutcome> Restart(string userSid, string installFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        var steps = new List<StepOutcome>();
        if (!Sddl.IsUserSid(userSid))
        {
            return Ended(steps, started: false, "the user is not one this program starts a program for");
        }

        TrayProbe before = _probe.Probe();
        steps.Add(before.Step);
        if (before.State == TrayInstance.Running)
        {
            _log.Info("install: a tray is already running in this session, so none was started.");
            steps.Add(new StepOutcome(Step, true, 0, "S_OK", "An Earshot tray is already running in this session, so none was started."));
            return steps;
        }

        if (before.State == TrayInstance.Unknown)
        {
            return Ended(steps, started: false, "whether a tray is running could not be checked");
        }

        // The folder was made by this install and checked there; it is read again because the one thing this run registers
        // for a user must never go into a folder that user, or anyone else, could write.
        int hr = _tasks.ReadFolderSddl(TaskPlan.FolderPath, out string? folderSddl);
        steps.Add(StepOutcomes.FromHResult("install-start-tray-folder-read", hr));
        IReadOnlyList<string> problems = hr < 0 ? ["The task folder security could not be read."] : AclCheck.CheckTaskFolder(folderSddl, userSid);
        if (problems.Count > 0)
        {
            steps.Add(StepOutcomes.NotAttempted("install-start-tray-folder-acl", string.Join(" ", problems)));
            return Ended(steps, started: false, "the task folder is not as Earshot needs");
        }

        TaskSpec spec = TaskPlan.TrayStartSpec(installFolder, userSid);

        // A task an earlier run left behind (it was stopped before it could remove it) would make the registration fail.
        hr = _tasks.DeleteTask(TaskPlan.FolderPath, spec.Name);
        steps.Add(StepOutcomes.FromHResult("install-start-tray-stale", hr,
            hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND ? "No earlier start task." : null,
            ok: hr >= 0 || hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND));

        hr = _tasks.Register(TaskPlan.FolderPath, spec, steps);
        steps.Add(StepOutcomes.FromHResult("install-start-tray-register", hr));
        if (hr < 0)
        {
            return Ended(steps, started: false, "its start task could not be registered");
        }

        bool started = RunAndWait(spec, steps);

        // Removed whether or not it ran: it is never meant to stay.
        hr = _tasks.DeleteTask(TaskPlan.FolderPath, spec.Name);
        steps.Add(StepOutcomes.FromHResult("install-start-tray-remove", hr, hr < 0 ? "The next install removes it." : null));

        // The tray is a child of the task. The documentation does not say what removing a task does to an instance that is
        // running, nor how soon, so what is left is looked at for about two seconds and recorded: survival is reported only
        // for a tray still running at the last look, and a tray gone at any look is reported as gone.
        if (started)
        {
            TrayProbe after = _probe.Probe();
            for (int look = 1; look < SurvivalLooks && after.State == TrayInstance.Running; look++)
            {
                if (!Wait(PollEvery))
                {
                    break;
                }

                after = _probe.Probe();
            }

            bool survived = after.State == TrayInstance.Running;
            steps.Add(new StepOutcome("install-start-tray-after-remove", survived, survived ? 0 : NativeCodes.NotAttempted, survived ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted),
                survived ? "The tray was still running about two seconds after its task was removed." : "The tray was no longer running after its task was removed."));
            if (!survived)
            {
                return Ended(steps, started: false, "the tray ended when its start task was removed");
            }
        }

        return started
            ? Ended(steps, started: true, "")
            : Ended(steps, started: false, "the tray did not start");
    }

    // Runs the registered task and waits for the tray to appear. True when it was seen.
    private bool RunAndWait(TaskSpec spec, List<StepOutcome> steps)
    {
        int hr = _runner.RunNow(spec.Path);
        steps.Add(StepOutcomes.FromHResult("install-start-tray-run", hr,
            hr < 0 ? "The user may not be signed in, or Task Scheduler refused the run." : null));
        if (hr < 0)
        {
            return false;
        }

        for (int look = 0; look < SeenPolls; look++)
        {
            TrayProbe probe = _probe.Probe();
            if (probe.State == TrayInstance.Running)
            {
                steps.Add(new StepOutcome("install-start-tray-seen", true, 0, "S_OK", "The tray is running."));
                return true;
            }

            if (!Wait(PollEvery))
            {
                break;
            }
        }

        // What the task says about itself, for a run that was accepted and produced nothing.
        int stateHr = _runner.ReadResult(spec.Path, out int state, out int? last);
        string said = stateHr < 0
            ? "The task could not be read."
            : "Task state " + state.ToString(CultureInfo.InvariantCulture) + ", last result " +
              (last is { } value ? "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture) : "unknown") + ".";
        steps.Add(StepOutcomes.FromHResult("install-start-tray-seen", stateHr < 0 ? stateHr : NativeCodes.NotAttempted,
            "No tray appeared. " + said, ok: false));
        return false;
    }

    private List<StepOutcome> Ended(List<StepOutcome> steps, bool started, string why)
    {
        if (started)
        {
            _log.Info("install: Earshot was started again, not elevated, in the signed-in user's session.");
            steps.Add(new StepOutcome(Step, true, 0, "S_OK", "Earshot was started again."));
        }
        else
        {
            string text = "Earshot was not started now: " + why + ". " + NextSignIn;
            _log.Warn("install: " + text);
            steps.Add(StepOutcomes.NotAttempted(Step, text));
        }

        return steps;
    }
}
