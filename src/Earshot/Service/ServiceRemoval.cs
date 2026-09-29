using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Service;

// Stops and deletes the hand-back service. Uninstall and a failed install both use it, so a service is never left
// registered by either, and it is stopped before it is deleted so its program is no longer in use when the install
// folder is removed.
internal static class ServiceRemoval
{
    // How long a service gets to stop. The service's own work is bounded well inside this, and Windows gives a stop
    // 30 seconds before it reports a time-out. A waiting budget chosen here.
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    // True when the service is gone or marked for deletion after it stopped. A service that would not stop is still
    // deleted (marked, and removed when the computer restarts), but the result is false so the caller reports it as
    // not complete. Every call is a step with the Win32 code.
    public static bool Remove(IServiceControl service, Func<TimeSpan, bool> wait, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(steps);
        ServiceQuery read = service.Query(ServicePlan.ServiceName);
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
            steps.Add(service.Stop(ServicePlan.ServiceName));
            StepOutcome waited = service.WaitForState(ServicePlan.ServiceName, AdvApi32.SERVICE_STOPPED, StopWait, wait);
            steps.Add(waited);
            if (!waited.Ok)
            {
                stopped = false;
                steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Stop,
                    "The Earshot hand-back service did not stop, so it is deleted when the computer restarts."));
            }
        }

        StepOutcome deleted = service.Delete(ServicePlan.ServiceName);
        steps.Add(deleted);
        return deleted.Ok && stopped;
    }
}
