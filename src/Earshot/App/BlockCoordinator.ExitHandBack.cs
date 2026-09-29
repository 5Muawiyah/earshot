using Earshot.Contracts;

namespace Earshot.App;

// Exit from the tray menu while the AirPods are connected to this PC hands them back the way a shut down does:
// release, disconnect, confirm, block, by the same procedure (HandBackCoreAsync), not a second one. It runs as the
// block before closing, so it is ordered after any operation Exit had to wait for, and Exit's own wait for the
// coordinator to go idle covers it.
//
// The cap is the shut-down one (TrayStartOptions.HandBackBudget, 4 s, and 1.5 s of it for the disconnect). Exit has
// no Windows deadline, but the owner is waiting at the click, so the cap is taken from the existing ones rather than
// invented: it is the longest of them, and the one the procedure was sized to (a disconnect first, then a block that
// is not vetoed), so it costs a normal Exit nothing and bounds a stuck one. Once the block has been sent to the gate
// it runs in its own SYSTEM process, so a cap that passes after that point does not stop it.
//
// Exit with the setting off, or with the AirPods not connected to this PC, is what it was before: the block before
// closing, and "Closed while in use" when the AirPods are in use.
//
// Exit while a connect or a disconnect is in flight: the operation is cancelled and finishes first, as it always did,
// and only then is it decided whether the AirPods are connected. A connect that came up despite the cancel has them
// connected, so they are handed back; one that did not leaves nothing to hand back, so it is the ordinary Exit. A
// disconnect that finished is not repeated; one the cancel stopped before render left ACTIVE leaves them connected, so
// Exit sends its own.
internal sealed partial class BlockCoordinator
{
    // What Exit says when the hand-back did not leave the AirPods disconnected and blocked. The block failing is the
    // at-rest case: the raw code is in the log line the hand-back wrote (HandBackText.Finished).
    public const string ExitNotBlockedMessage = "The AirPods are not blocked. This PC may take them back.";
    public const string ExitNotDisconnectedMessage = "The AirPods did not disconnect from this PC.";
    public const string ExitNotDisconnectedBlockedMessage = "The AirPods did not disconnect from this PC, but they are blocked.";

    private ExitHandBackPlan? _exitHandBack;
    private bool _exitHandBackRunning;
    private HandBackOutcome? _handBackOutcome;

    // True while Exit's hand-back is running, for the tray's card that says so.
    public bool ExitHandBackRunning => _exitHandBackRunning;

    // True when Exit chosen now would hand the AirPods back: the setting is on and the snapshot in hand says the
    // AirPods are connected to this PC. Asked by the tray before Exit, so it can let go of Play from a phone first.
    public bool ExitHandBackApplies =>
        Settings.HandBackOnShutdownAndSleep && _started && !_disposed &&
        CoordinatorRules.RenderOf(_snapshot, WatchedContainer()) == RenderState.Active;

    // What the hand-back last ended with, for tests and Exit's notice.
    internal HandBackOutcome? LastHandBackOutcome => _handBackOutcome;

    private bool ExitHandBackWanted => _exitHandBack is not null && Settings.HandBackOnShutdownAndSleep;

    // Written when Exit reaches a connected AirPods with the setting off, so a reader can tell "off" from "never
    // reached". Not written when they are not connected: nothing would have been handed back either way.
    private void NoteExitHandBackOff()
    {
        if (_exitHandBack is not null && !Settings.HandBackOnShutdownAndSleep &&
            CoordinatorRules.RenderOf(_snapshot, WatchedContainer()) == RenderState.Active)
        {
            _log.Info(HandBackText.Off(HandBackTrigger.Exit));
        }
    }

    // The first look of the block before closing. True when it handed the AirPods back, so nothing else is read or
    // sent: false when Exit is not to hand back, or the AirPods turned out not to be connected after all.
    private async Task<bool> ExitHandBackFirstAsync()
    {
        if (!ExitHandBackWanted)
        {
            return false;
        }

        Guid container = WatchedContainer();
        if (CoordinatorRules.RenderOf(_snapshot, container) != RenderState.Active)
        {
            return false;
        }

        // The snapshot in hand may be old: the operation Exit waited for may have ended in a disconnect. A read
        // that fails is not an observation that they left, so it hands back all the same (the at-rest side).
        DeviceSnapshot? now = await RefreshSnapshotAsync();
        if (now is not null && CoordinatorRules.RenderOf(now, container) == RenderState.NotActive)
        {
            _log.Info("Closing: the AirPods are no longer connected to this PC, so nothing is handed back.");
            return false;
        }

        await ExitHandBackAsync();
        return true;
    }

    private async Task ExitHandBackAsync()
    {
        ExitHandBackPlan plan = _exitHandBack ?? throw new InvalidOperationException("Exit's hand-back was started without a plan.");
        _exitHandBackRunning = true;
        _handBackOutcome = null;
        RaiseChanged();

        // One deadline, taken once: HandBackCoreAsync enforces it itself and nothing here races a second timer
        // against it (the same rule HandBackAsync states for the shut-down hold).
        DateTimeOffset t0 = _time.GetUtcNow();
        try
        {
            await HandBackCoreAsync(HandBackTrigger.Exit, t0, t0 + plan.Budget, plan.DisconnectWait, plan.StreamingHeld);
        }
        catch (Exception ex)
        {
            // Never silent: the backstop for whatever the procedure's own steps did not catch and log.
            _log.Error(HandBackText.Prefix(HandBackTrigger.Exit) + "ended unexpectedly (" + ex.GetType().Name + ": " + ex.Message + ").", ex);
            RecordHandBack("failed", HandBackBlockOutcome.NotBlocked, ex.GetType().Name);
        }
        finally
        {
            _exitHandBackRunning = false;
            RaiseChanged();
        }

        ClosingNotice = ExitNoticeFor(_handBackOutcome);
        if (ClosingNotice is { } notice)
        {
            _log.Warn(HandBackText.ExitWillSay(notice));
        }
    }

    private void RecordHandBack(string disconnect, HandBackBlockOutcome block, string detail) =>
        _handBackOutcome = new HandBackOutcome(disconnect, block, detail);

    // A block not sent is only a problem when it leaves the nodes enabled by Earshot's doing: the boot block off,
    // not set up, or the nodes already blocked leave nothing to do. A status never read, a protect verb that may
    // still run and a pin that may move each leave them enabled.
    private static HandBackBlockOutcome BlockNotSentOutcome(string reason) =>
        reason.StartsWith(ProtectMayRunReason, StringComparison.Ordinal) ||
        reason == PinMayMoveReason ||
        reason.Contains("never read", StringComparison.Ordinal)
            ? HandBackBlockOutcome.NotBlocked
            : HandBackBlockOutcome.NotNeeded;

    private static HandBackBlockOutcome BlockResultOutcome(ControllerResult result)
    {
        if (result.IsSuccess)
        {
            return HandBackBlockOutcome.Blocked;
        }

        // Safe mode refuses every device action: nothing was tried, so nothing failed.
        if (result.Status == OpStatus.NotAttempted)
        {
            return HandBackBlockOutcome.NotNeeded;
        }

        return CoordinatorRules.MayStillRun(result) ? HandBackBlockOutcome.CutShort : HandBackBlockOutcome.NotBlocked;
    }

    // What Exit tells the owner, or null when the AirPods were let go and blocked (or nothing needed blocking).
    // No notice in safe mode, where no device action is ever tried.
    private string? ExitNoticeFor(HandBackOutcome? outcome)
    {
        if (outcome is null || _options.SafeMode)
        {
            return null;
        }

        bool disconnected = outcome.Disconnect is "confirmed" or "nothing to disconnect";
        return outcome.Block switch
        {
            HandBackBlockOutcome.NotBlocked => ExitNotBlockedMessage,
            HandBackBlockOutcome.CutShort => ClosedBeforeChangeEndedMessage,
            HandBackBlockOutcome.Blocked => disconnected ? null : ExitNotDisconnectedBlockedMessage,
            _ => disconnected ? null : ExitNotDisconnectedMessage,
        };
    }
}
