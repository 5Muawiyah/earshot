using Earshot.Contracts;

namespace Earshot.App;

internal sealed partial class BlockCoordinator
{
    // A Fix connection run started from the tray has ended. It may have left a run half done or a new pair Earshot does not
    // protect (an adoption that failed), so the checks made at start run again now rather than at the next sign-in, and the
    // card about unprotected AirPods may be shown again. UI thread only, like the rest of the coordinator.
    public void RecheckProtectionAfterFix()
    {
        if (_closing || _disposed || !_started)
        {
            return;
        }

        _log.Info("A Fix connection run ended; the start checks run again.");
        _notProtectedShown = false;
        _ = RunFixStartChecksAsync();
    }
}
