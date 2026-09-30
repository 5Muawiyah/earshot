using Earshot.Contracts;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Update;

namespace Earshot.App;

// One elevated operation at a time in the tray: setup, repair or update. Two of them would each ask for an administrator
// prompt and each work on the install folder, the scheduled tasks and the service, so a second one is refused with the
// reason while the first runs, and the menu and the card say so (MenuModel, CardSettingsValues.ElevatedRunNote). The
// machine-wide lock in InstallActions and UpdateActions is what keeps two runs apart when they do not come from one tray;
// this is the half that tells the person why nothing started.
//
// Exit waits for an elevated setup or repair that is still running before its hand-back and block (ExitAsync): the repair
// registers the boot block's scheduled tasks again, and a block sent meanwhile could meet a task that is being replaced.
//
// An update is different: the tray ends for it, and the elevated program it starts replaces the folder the tray runs from.
// Before that program starts, the tray finishes its own closing device work (the hand-back and the block, with the same
// limits as Exit) and then starts the program and ends without another device call (PrepareHandOverAsync).
internal sealed partial class TrayContext
{
    internal const string SetupRun = "setup";
    internal const string RepairRun = "repair";
    internal const string UpdateRun = "update";

    // The elevated operation under way, or null. Claimed on the UI thread before anything awaits, so two clicks cannot both
    // start one.
    private string? _elevatedRun;

    // Completed when the elevated program of a setup or repair has ended or been given up on: it covers the administrator
    // prompt and the wait for the run. Null while there is none. Exit waits on it.
    private TaskCompletionSource? _elevatedProgram;

    // The tray has done its closing device work for an elevated update and does not take any more: it ends when the
    // elevated program has started, or when starting it failed.
    private bool _closedForHandOver;

    private TimeSpan _elevatedExitWait = TimeSpan.FromSeconds(90);

    // The elevated operation under way, for the menu, the card and tests.
    internal string? ElevatedRunInProgress => _elevatedRun;

    // What the person is told when an action they chose would start a second elevated operation, and what a card says while
    // Exit waits for one.
    internal static string FinishingMessage(string run) => MenuModel.FinishingFirst(run, capital: true);

    // What Exit says when it had to close before the elevated operation ended.
    internal static string ClosedBeforeElevatedEndedMessage(string run) => "Earshot closed before the " + run + " had finished.";

    private bool TryBeginElevatedRun(string run, CardPlace place)
    {
        if (_elevatedRun is { } running)
        {
            _log.Info(run + ": not started, the " + running + " is still running.");
            ShowCard(TrayStatus.AppName, FinishingMessage(running), place);
            return false;
        }

        _elevatedRun = run;
        UpdatePresentation(forceIcon: false);
        return true;
    }

    private void EndElevatedRun(string run)
    {
        if (_elevatedRun == run)
        {
            _elevatedRun = null;
            UpdatePresentation(forceIcon: false);
        }
    }

    // Runs the operation that asks for the administrator prompt and waits for its program, and lets Exit wait for it.
    private async Task<T> WithElevatedProgramAsync<T>(Func<Task<T>> operation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _elevatedProgram = done;
        try
        {
            return await operation();
        }
        finally
        {
            _elevatedProgram = null;
            done.TrySetResult();
        }
    }

    // Exit's wait, before anything of its own is sent. Null when there was nothing to wait for or it ended in time;
    // otherwise the notice that Earshot closed first, which is also logged.
    private async Task<string?> WaitForElevatedProgramAsync(CardPlace place)
    {
        if (_elevatedProgram is not { } program)
        {
            return null;
        }

        string run = _elevatedRun ?? "operation";
        _log.Info("Exit: the " + run + " is still running, so the hand-back and the block wait for it, up to " + Seconds(_elevatedExitWait) + ".");
        place.Show(_registry.Cards, TrayStatus.AppName, FinishingMessage(run));
        if (await CompletesWithinAsync(program.Task, _elevatedExitWait))
        {
            _log.Info("Exit: the " + run + " ended, so the hand-back and the block go on.");
            return null;
        }

        _log.Warn("Exit: the " + run + " was still running after " + Seconds(_elevatedExitWait) + ", so Earshot closes without waiting longer. " +
            "The block may meet a scheduled task the " + run + " is still registering; the BootBlock task blocks the AirPods at the next start.");
        return ClosedBeforeElevatedEndedMessage(run);
    }

    // The elevated update's hand-over, before the elevated program starts. Returns null to go on, or the reason it must
    // not. Runs on the UI thread. The tray has its closing device work done when this returns null, and takes no more
    // actions; it ends once the program has started (OnUpdateHandedOver) or, when starting it failed, once the card has
    // said so (ExitAfterFailedHandOverAsync).
    private Task<string?> PrepareHandOverAsync()
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.UiPost(async () =>
        {
            try
            {
                done.SetResult(await CloseDeviceWorkForHandOverAsync());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private async Task<string?> CloseDeviceWorkForHandOverAsync()
    {
        if (_closing)
        {
            _log.Info("Update: not handed over, because Earshot is closing.");
            return UpdateCopy.ClosingNotice;
        }

        // The same steps as Exit's (ExitAsync), up to the point where Exit ends: no more input, then the hand-back and the
        // block before closing, with their limits. The elevated program starts only after they have finished, because an
        // installed version that does not wait for this program to end would otherwise install while they still run.
        CardPlace place = CardPlace.NearTray;
        _closing = true;
        _closedForHandOver = true;
        _log.Info("Update: Earshot does its closing device work first, then starts the elevated program and ends.");
        _notifyIconVisibility.Visible = false;
        _picker?.Close();
        _exitPlace = place;
        _coordinator.BeginShutdown(PrepareExitHandBack());
        _lifetime.Cancel();
        string? notice = await FinishClosingWorkAsync(place, exceptAction: IsElevatedFlowAction);
        if (notice is not null)
        {
            _log.Info("Update: " + notice);
            place.Show(_registry.Cards, TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), notice);
        }

        return null;
    }

    // The actions that start an elevated flow. One of them is what is waiting for the hand-over, so the closing work does not
    // wait for it.
    private static bool IsElevatedFlowAction(string action) =>
        action.StartsWith(SetupRun, StringComparison.Ordinal) || action.StartsWith(RepairRun, StringComparison.Ordinal) || action.StartsWith(UpdateRun, StringComparison.Ordinal);

    // The elevated program did not start (the prompt was declined, or Windows would not start it) after the closing work is
    // done, so there is nothing left to do but say so and end: the tray cannot go back to what it was. It starts itself again,
    // not elevated, once it has ended and let go of its single-instance lock.
    private async Task ExitAfterFailedHandOverAsync()
    {
        if (!_closedForHandOver)
        {
            return;
        }

        // Started again only when this process is not elevated: a program it starts has its token, and Earshot never runs elevated.
        _log.Info("Update: the elevated program did not start, so Earshot ends" + (_isElevated() ? "." : " and starts again."));
        if (_updateRunningExe is { } running && !_isElevated())
        {
            StartAfterExit = running;
        }

        try
        {
            await Task.Delay(_exitNoticeTime);
        }
        finally
        {
            ExitThread();
        }
    }
}
