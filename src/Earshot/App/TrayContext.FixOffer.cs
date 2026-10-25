using Earshot.Contracts;
using Earshot.Tray;

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

    private void ShowFixOffer(string title, string reason, CardPlace place)
    {
        if (_closing || _closed)
        {
            return;
        }

        var content = new CardContent(title, reason, new CardAction(FixOffer.ButtonLabel, StartFixConnection));
        if (place.ClickPoint is { } point)
        {
            _registry.Cards.Show(content, place.Anchor, point);
        }
        else
        {
            _registry.Cards.Show(content, place.Anchor);
        }
    }

    // The failure card again, with the same words and Connect in Windows under them. Pressing it allows the AirPods and opens
    // Windows' Bluetooth settings page; the coordinator shows its own card for the result and then leaves a connection made
    // there alone. Not offered in safe mode, which refuses every device action, so the button could only be refused.
    private void ShowConnectInWindowsOffer(Guid container, string title, string reason, CardPlace place)
    {
        if (_closing || _closed)
        {
            return;
        }

        var content = new CardContent(title, reason, new CardAction(ConnectInWindowsOffer.ButtonLabel, () => _ = ConnectInWindowsAsync(container, title, place)));
        if (place.ClickPoint is { } point)
        {
            _registry.Cards.Show(content, place.Anchor, point);
        }
        else
        {
            _registry.Cards.Show(content, place.Anchor);
        }
    }

    private async Task ConnectInWindowsAsync(Guid container, string title, CardPlace place)
    {
        if (_closing)
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

    private void StartFixConnectionNow()
    {
        if (_closing)
        {
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
