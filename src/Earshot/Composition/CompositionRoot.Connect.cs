using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Contracts.Null;

namespace Earshot.Composition;

// Connect and disconnect. Needs the audio worker and the device monitor from the audio hook, which runs first.
// Building the controller starts nothing. In safe mode the registry wraps it, so no request is ever sent.
internal static partial class CompositionRoot
{
    static partial void ConfigureConnect(ServiceRegistry r) => AddConnection(r);

    // Sets r.Connection to the real controller, or leaves the null controller in place and logs why.
    internal static void AddConnection(ServiceRegistry r)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (r.Worker is not AudioWorker worker)
        {
            r.Log.Warn("Connect and disconnect are not available: " +
                       (r.Worker is null ? "no audio worker was created." : "the audio worker is not the Core Audio worker.") +
                       " Left click reports that it is not available.");
            return;
        }

        if (r.Monitor is NullDeviceMonitor)
        {
            r.Log.Warn("Connect and disconnect are not available: there is no device monitor to confirm a state change. " +
                       "Left click reports that it is not available.");
            return;
        }

        r.Connection = new ConnectionController(worker, r.Monitor, r.Log);
    }
}
