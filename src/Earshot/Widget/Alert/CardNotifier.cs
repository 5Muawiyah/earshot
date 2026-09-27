using Earshot.Contracts;

namespace Earshot.Widget.Alert;

// The fallback when a toast cannot be shown, and the whole notifier in safe mode or with a redirected data
// root: never touches the Start menu or the notification area, only the card the tray already has. Subject
// to the card presenter's own SHQueryUserNotificationState gate, which is the card presenter's business,
// not this class's.
internal sealed class CardNotifier : INotifier
{
    private readonly ICardPresenter _cards;

    public CardNotifier(ICardPresenter cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _cards = cards;
    }

    public async Task<StepOutcome> NotifyAsync(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);

        bool shown = await _cards.ShowAsync(new CardContent(title, text), CardAnchor.NearTray, clickPoint: null).ConfigureAwait(false);
        return shown
            ? StepOutcomes.FromHResult("card-notify", 0)
            : StepOutcomes.NotAttempted("card-notify", "The card was held back.");
    }
}
