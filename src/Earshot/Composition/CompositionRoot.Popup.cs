using Earshot.Popup;

namespace Earshot.Composition;

// Wires the popup card. Building the presenter creates no window: the card and its timer are made on the
// UI thread the first time a card is shown, so run modes that never show a card never create one.
internal static partial class CompositionRoot
{
    static partial void ConfigurePopup(ServiceRegistry r)
    {
        r.Cards = new CardPresenter(r.Log, r.UiPost);
    }
}
