using Earshot.Contracts;
using Earshot.Widget.EarPause;

namespace Earshot.App;

// The coordinator's two hooks for pause on leave (Earshot.Widget.EarPause.PauseOnLeave): it is told what every
// snapshot shows of the AirPods' render side, so a leave Earshot did not start is seen, and it is asked to pause
// just before Earshot's own disconnect, so the sound does not jump to the speakers. The coordinator decides
// nothing about pausing; with none set (safe tests, no audio worker) both hooks do nothing.
internal sealed partial class BlockCoordinator
{
    private PauseOnLeave? _leavePause;

    // Set once by the tray, before the monitor starts. The coordinator never disposes it.
    internal PauseOnLeave? LeavePause
    {
        get => _leavePause;
        set => _leavePause = value;
    }

    // What one accepted snapshot shows of the watched container's render side. An operation in flight is named, so a
    // change Earshot itself is making (a protection change drops the link and brings it back) is not taken for a leave.
    private void NoteRenderForPauseOnLeave(DeviceSnapshot snapshot)
    {
        if (_leavePause is not { } pause)
        {
            return;
        }

        Guid container = WatchedContainer();
        _ = pause.OnRender(CoordinatorRules.RenderOf(snapshot, container), container, _time.GetUtcNow(), _current is not null ? _currentName : null);
    }

    private Task PauseBeforeOwnLeaveAsync(string why, Guid container, TimeSpan cap, CancellationToken ct) =>
        _leavePause is { } pause ? pause.BeforeOwnLeaveAsync(why, container, cap, ct) : Task.CompletedTask;

    private void NoteOwnLeaveEnded(bool left) => _leavePause?.AfterOwnLeave(left);
}
