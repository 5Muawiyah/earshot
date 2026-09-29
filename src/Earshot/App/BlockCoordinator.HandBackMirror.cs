using Earshot.Contracts;

namespace Earshot.App;

// The hand-back setting on its way to the service that hands the AirPods back at shut down. The service cannot read the
// tray's settings file, so the setting is copied into the machine folder's config.json by a gate verb, sent through the
// same exclusive slot every other device action uses. Two things send it: the menu tick, and once per tray run a status
// read that finds config.json disagreeing with the setting (for instance right after setup, when the setting was off).
internal sealed partial class BlockCoordinator
{
    // Set once a mirror has been sent this run, so a request the gate could not carry out is not repeated at every read.
    private bool _handBackMirrorSent;

    // Copies the setting to config.json as an operation of its own. Refused while the session is ending, a hand-back is
    // running or the machine is asleep, like every gate write. A gate change, so once it is running its result is
    // waited for.
    public Task<ControllerResult> SetHandBackAtShutdownAsync(bool handBack, CancellationToken ct = default)
    {
        string verb = handBack ? GateVerbs.SetHandBackOn : GateVerbs.SetHandBackOff;

        // Any attempt counts, so a status read after a change that failed does not send it a second time by itself.
        _handBackMirrorSent = true;
        return RunExclusiveAsync(
            verb,
            async token =>
            {
                ControllerResult set = await _block.SetHandBackAtShutdownAsync(handBack, CancellationToken.None);

                // Read at once, however it ended, so the status the tray holds shows config.json as it is now.
                await ReadBlockStatusAsync(CancellationToken.None);
                return set;
            },
            ct,
            () => RefusedAtSessionEnd(verb),
            () => StoppedAtSessionEnd(verb));
    }

    // Called with every status read that is kept. Sends the mirror when the tasks are installed, config.json was read and
    // says something other than the setting, and nothing that must not be interrupted is going on. Queued as an operation
    // of its own and never awaited here, because this runs inside operations that hold the exclusive slot.
    private void MirrorHandBackSetting(BootBlockStatus status)
    {
        if (_handBackMirrorSent || _disposed || _closing || _sessionEnding || _handingBack || _sleeping || _options.SafeMode)
        {
            return;
        }

        if (!status.TasksInstalled || status.HandBackAtShutdownMirror is not bool mirror)
        {
            return;
        }

        bool setting = Settings.HandBackOnShutdownAndSleep;
        if (mirror == setting)
        {
            return;
        }

        _handBackMirrorSent = true;
        string verb = setting ? GateVerbs.SetHandBackOn : GateVerbs.SetHandBackOff;
        _log.Info("Hand-back mirror: config.json reads " + (mirror ? "on" : "off") + ", the setting is " + (setting ? "on" : "off") + "; sending " + verb + ".");
        _ = SendHandBackMirrorAsync(setting);
    }

    private async Task SendHandBackMirrorAsync(bool setting)
    {
        try
        {
            ControllerResult result = await SetHandBackAtShutdownAsync(setting, CancellationToken.None);
            if (result.IsSuccess)
            {
                _log.Info("Hand-back mirror: " + result.UserMessage + ".");
            }
            else
            {
                _log.Warn("Hand-back mirror: " + result.UserMessage + " " + string.Join(" | ", result.Steps.Where(s => !s.Ok).Select(s => s.Step + " " + s.CodeName)));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Hand-back mirror stopped with " + ex.GetType().Name + ".", ex);
        }
    }
}
