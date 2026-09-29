using Earshot.Tray;

namespace Earshot.App;

// The tray's side of the hand-back on Exit (BlockCoordinator.ExitHandBackAsync does the work): what is let go
// of first, the budgets, and the one card shown while it runs.
internal sealed partial class TrayContext
{
    // Shown at the Exit click while the AirPods are being let go of and blocked. The result, when it is not a clean
    // one, is the closing notice (BlockCoordinator.ExitNotBlockedMessage and the others), kept up for ExitNoticeTime.
    public const string ExitHandBackMessage = "Handing the AirPods back.";

    private bool _exitHandBackCardShown;

    // Called once, by Exit, before the coordinator starts closing. When Exit will hand the AirPods back, a held
    // Play from a phone connection is let go of first (release comes before disconnect), the same way a shut down
    // does it; nothing is let go of early when the AirPods are not connected or the setting is off, so that Exit
    // is what it was. The budgets are the shut-down ones (TrayStartOptions.HandBackBudget and DisconnectHandBackWait).
    private ExitHandBackPlan PrepareExitHandBack()
    {
        bool streamingHeld = false;
        if (_coordinator.ExitHandBackApplies)
        {
            streamingHeld = _streaming is not null;
            StopStreaming(wait: false, "Earshot is closing");
        }

        return new ExitHandBackPlan(_handBackBudget, _disconnectHandBackWait, streamingHeld);
    }

    // Once, when the hand-back starts: the card follows the Exit click like every card after it.
    private void ShowExitHandBackCard()
    {
        if (_exitPlace is { } place && !_exitHandBackCardShown && _coordinator.ExitHandBackRunning)
        {
            _exitHandBackCardShown = true;
            place.Show(_registry.Cards, TrayStatus.AppName, ExitHandBackMessage);
        }
    }
}
