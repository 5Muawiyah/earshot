using Earshot.Contracts;

namespace Earshot.App;

// The menu tick for "Hand back on shut down, sleep and Exit" saves the setting, and this carries it to config.json for the
// service that hands the AirPods back at shut down.
internal sealed partial class TrayContext
{
    // Sends the setting through the gate as an operation, so a failure shows the card path every gate change does.
    // Only once Earshot is set up: before that the task that would carry the request does not exist, and setup's own
    // status read finds config.json disagreeing with the setting and sends it then. Nothing is sent while the status
    // is unread: the coordinator's own read does that.
    private void MirrorHandBackSetting(bool on, CardPlace place)
    {
        if (BlockStatus is not { TasksInstalled: true })
        {
            return;
        }

        string action = on ? GateVerbs.SetHandBackOn : GateVerbs.SetHandBackOff;
        Launch(action, () => RunOperationAsync(action, ct => _coordinator.SetHandBackAtShutdownAsync(on, ct), place), place);
    }
}
