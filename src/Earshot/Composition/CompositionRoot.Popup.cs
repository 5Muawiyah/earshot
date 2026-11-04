using Earshot.Widget;

namespace Earshot.Composition;

// Wires the notices. Building the presenter creates no window: the tray gives it the one card to show them on once that exists,
// and until then it logs and drops what it is given, so run modes that never show a card never create one.
internal static partial class CompositionRoot
{
    static partial void ConfigurePopup(ServiceRegistry r)
    {
        r.Cards = new CompactNoticePresenter(r.Log);
    }
}
