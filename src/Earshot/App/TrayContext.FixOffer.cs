using Earshot.Contracts;
using Earshot.Tray;
using Earshot.Widget;

namespace Earshot.App;

// The Fix offer: after a Connect that failed on this PC's side often enough, the failure card carries one button, Fix connection,
// under the reason. The card is shown again with the same words and the button added, in place, so the person sees the reason
// they were already reading and one thing to press. Nothing is done until they press it.
internal sealed partial class TrayContext
{
    private IFixLauncher? _fixLauncher;

    // For tests: what starts Fix connection. The real one tells the coordinator when the run has ended, on the UI thread, so the
    // start checks run again (a run can end with a new pair Earshot does not protect).
    internal IFixLauncher FixLauncher
    {
        get => _fixLauncher ??= new OwnProgramFixLauncher(() => _registry.UiPost(() => _coordinator.RecheckProtectionAfterFix()));
        set => _fixLauncher = value ?? throw new ArgumentNullException(nameof(value));
    }

    // The menu's Fix connection...: always offered, since the fix run needs no install and asks its own questions.
    private void WireFixConnection() => _menu.FixConnectionClicked += (_, _) =>
    {
        _log.Info("Fix connection: started from the menu.");
        StartFixConnectionNow();
    };

    // Says a failed connect or disconnect on the one card: in the status row of the card that is open, else on a card that opens for the
    // next step, else nowhere (the gauge's cross and tooltip say it). Completes with what became of it.
    private async Task<FailureShown> SayFailureAsync(string reason, FailureNextStep? next, bool mustBeSaid = false)
    {
        if (_closing || _closed || ToggleCard is not { } card)
        {
            return FailureShown.NotShown;
        }

        return await card.ShowFailed(reason, next, mustBeSaid);
    }

    // Connect in Windows, from the failure line: it allows the AirPods and opens Windows' Bluetooth settings; the coordinator tells the
    // result and then leaves a connection made there alone. Not offered in safe mode, which refuses every device action, so the button
    // could only be refused.
    private async Task ConnectInWindowsAsync(Guid container, string title, CardPlace place)
    {
        if (_closing || RefusedWhilePairing("Connect in Windows", place))
        {
            return;
        }

        _log.Info("Connect in Windows: started from the card.");
        try
        {
            ToggleReport report = await _coordinator.ConnectInWindowsAsync(new ToggleRequest(true, container, title, place), _lifetime.Token);
            _log.Write(report.IsSuccess || report.Cancelled ? LogLevel.Info : LogLevel.Warn,
                TrayReport.Describe("connect in windows", report.Status, report.UserMessage, report.Steps));
            if (report.StartPairAirPods)
            {
                // Not set up and no AirPods paired: there is nothing to connect in Windows, so the person is taken to pairing.
                StartPairAirPods(place);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _log.Info("Connect in Windows: cancelled because Earshot is closing.");
        }
        catch (Exception ex)
        {
            ReportUnexpected("connect in windows", ex, place);
        }
    }

    private void StartFixConnection()
    {
        if (_closing)
        {
            return;
        }

        _log.Info("Fix connection: started from the card.");
        StartFixConnectionNow();
    }

    internal void FixConnectionFromSettings()
    {
        _log.Info("Fix connection: started from the settings page.");
        StartFixConnectionNow();
    }

    // A Fix the tray started ended at once because another Fix connection or Pair AirPods run held the lock.
    internal void SayFixRefusedByTheLock()
    {
        _log.Info("Fix connection: the run ended because another Fix connection or pairing holds the lock.");
        ShowCard(TrayStatus.AppName, FixRunLock.LockHeldMessage, CardPlace.NearTray);
    }

    private void StartFixConnectionNow()
    {
        if (_closing)
        {
            return;
        }

        // A pairing or an elevated run of the tray's own holds the lock or the tray's attention: a Fix started now would only be
        // refused, so the press says why instead.
        if (_elevatedRun is { } running)
        {
            _log.Info("Fix connection: not started, because " + running + " is running.");
            ShowCard(TrayStatus.AppName, running == PairRun ? FixRunLock.PairAlreadyRunningMessage : FinishingMessage(running), CardPlace.NearTray);
            return;
        }

        // One run at a time: a press while the last one is still open (from the card, the menu or the settings page) says so and
        // starts nothing.
        if (FixLauncher.IsRunning)
        {
            _log.Info("Fix connection: it is already running, so it was not started again.");
            ShowCard(TrayStatus.AppName, FixOffer.AlreadyRunningMessage, CardPlace.NearTray);
            return;
        }

        if (!FixLauncher.Start(_log))
        {
            ShowCard(TrayStatus.AppName, FixOffer.NotStartedMessage, CardPlace.NearTray);
        }
    }
}
