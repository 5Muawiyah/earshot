using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.App;

// The Fix offer: after a Connect that failed on this PC's side often enough, the failure card carries one button, Fix connection,
// under the reason. The card is shown again with the same words and the button added, in place, so the person sees the reason
// they were already reading and one thing to press. Nothing is done until they press it.
internal sealed partial class TrayContext
{
    private IFixLauncher _fixLauncher = new OwnProgramFixLauncher();

    // For tests: what starts Fix connection.
    internal IFixLauncher FixLauncher
    {
        get => _fixLauncher;
        set => _fixLauncher = value ?? throw new ArgumentNullException(nameof(value));
    }

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

    private void StartFixConnection()
    {
        if (_closing)
        {
            return;
        }

        _log.Info("Fix connection: started from the card.");
        if (!_fixLauncher.Start(_log))
        {
            ShowCard(TrayStatus.AppName, FixOffer.NotStartedMessage, CardPlace.NearTray);
        }
    }
}
