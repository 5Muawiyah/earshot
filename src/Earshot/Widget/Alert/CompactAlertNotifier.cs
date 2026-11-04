using Earshot.Contracts;

namespace Earshot.Widget.Alert;

// The fallback when a toast cannot be shown, and the whole notifier in safe mode or with a redirected data root: never touches the
// Start menu or the notification area, only the card the tray already has. It shows the alert's compact row, or the title and text
// as a notice when the alert has none. Subject to the card's own gates (the notification state, a full-screen application), which
// are the card's business, not this class's.
internal sealed class CompactAlertNotifier : INotifier
{
    private readonly ICardPresenter _cards;

    public CompactAlertNotifier(ICardPresenter cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _cards = cards;
    }

    public async Task<StepOutcome> NotifyAsync(string title, string text, CompactLine? line = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);

        bool shown = line is not null && _cards is CompactNoticePresenter notices
            ? await notices.ShowCompact(new CompactRequest(line.Content, CardAnchor.NearTray)).ConfigureAwait(false)
            : await _cards.ShowAsync(new CardContent(title, text), CardAnchor.NearTray, clickPoint: null).ConfigureAwait(false);
        return shown
            ? StepOutcomes.FromHResult("card-notify", 0)
            : StepOutcomes.NotAttempted("card-notify", "The card was held back.");
    }
}
