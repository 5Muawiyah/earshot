using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Service;

// Stops the hand-back service and waits until it has really gone. A service says "stopped" before its process has exited,
// and an install folder with a running program in it can be neither moved nor deleted, so the stop is not finished until
// the process is gone too. The process is the one read before the stop, by its id; an id read afterwards could belong to
// something else.
internal static class ServiceStopper
{
    // How long a service gets to stop, and its process to exit. The service's own work is bounded well inside this, and
    // Windows gives a stop 30 seconds before it reports a time-out. A waiting budget chosen here.
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    // True when the service reached the stopped state and its process was gone. before is the read taken while the service
    // ran. Every call is a step with the Win32 code; a failed stop request is a step, and the wait after it decides.
    public static bool StopAndWait(
        IServiceControl service, string name, ServiceQuery before, TimeSpan timeout, Func<TimeSpan, bool> wait, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(steps);
        steps.Add(service.Stop(name));
        StepOutcome stopped = service.WaitForState(name, AdvApi32.SERVICE_STOPPED, timeout, wait);
        steps.Add(stopped);
        if (!stopped.Ok)
        {
            return false;
        }

        if (before.ProcessId is not uint processId || processId == 0)
        {
            return true;
        }

        StepOutcome exited = service.WaitForProcessExit(processId, timeout, wait);
        steps.Add(exited);
        return exited.Ok;
    }
}

// Stops and deletes the hand-back service. Uninstall and a failed install both use it, so a service is never left
// registered by either, and it is stopped before it is deleted so its program is no longer in use when the install
// folder is removed.
internal static class ServiceRemoval
{
    public static TimeSpan StopWait => ServiceStopper.StopWait;

    // True when the service is gone or marked for deletion after it stopped. A service that would not stop is still
    // deleted (marked, and removed when the computer restarts), but the result is false so the caller reports it as
    // not complete. Every call is a step with the Win32 code. name is the service to remove; only a test names another.
    public static bool Remove(IServiceControl service, Func<TimeSpan, bool> wait, IList<StepOutcome> steps, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(steps);
        name ??= ServicePlan.ServiceName;
        ServiceQuery read = service.Query(name);
        if (read.Presence == ServicePresence.Missing)
        {
            steps.Add(new StepOutcome(ServiceSteps.Existing, true, 0, "S_OK", "No service."));
            return true;
        }

        if (read.Presence == ServicePresence.Unknown)
        {
            foreach (StepOutcome step in read.Steps.Where(s => !s.Ok))
            {
                steps.Add(step);
            }

            steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Delete,
                "Whether the Earshot hand-back service is registered could not be read, so it was not removed."));
            return false;
        }

        bool stopped = true;
        if (read.State != AdvApi32.SERVICE_STOPPED)
        {
            stopped = ServiceStopper.StopAndWait(service, name, read, StopWait, wait, steps);
            if (!stopped)
            {
                steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Stop,
                    "The Earshot hand-back service did not stop, so it is deleted when the computer restarts."));
            }
        }

        StepOutcome deleted = service.Delete(name);
        steps.Add(deleted);
        return deleted.Ok && stopped;
    }
}
