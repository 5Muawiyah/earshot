using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Widget.EarPause;

namespace Earshot.App;

// The tray's side of pause on leave (Earshot.Widget.EarPause.PauseOnLeave): it builds the pieces from what the
// registry holds and hands the result to the coordinator, which feeds it the AirPods' render side and asks it to pause
// before Earshot's own disconnect. The setting is read at each use, so turning it on or off needs no restart.
internal sealed partial class TrayContext
{
    private PauseOnLeave? _pauseOnLeave;

    // Called once, from the constructor. Needs the audio worker (a read of the AirPods' audio sessions runs on it) and
    // Windows Media Controls; without a worker (discovery unavailable, or a test with none) there is nothing to sample
    // with, so nothing is built and the setting has no effect, which is logged once.
    private void InitPauseOnLeave()
    {
        if (_registry.Worker is not AudioWorker worker)
        {
            _log.Info("Pause when AirPods leave this PC: not available, because there is no audio worker to read the AirPods' audio with.");
            return;
        }

        // The registry's setter wraps the source in safe mode, so a pause is refused there, like every device action.
        _registry.MediaSessions ??= new WindowsMediaSessions(_log);
        IMediaSessions? sessions = _registry.MediaSessions;
        if (sessions is null)
        {
            return;
        }

        _pauseOnLeave = new PauseOnLeave(
            sessions, new CoreAudioRenderActivity(worker), () => _registry.Settings.Current.PauseWhenAirPodsLeave, _time, _log);
        _coordinator.LeavePause = _pauseOnLeave;
    }

    private void ClosePauseOnLeave()
    {
        _coordinator.LeavePause = null;
        _pauseOnLeave?.Dispose();
        _pauseOnLeave = null;
    }
}
