namespace Earshot.Widget;

// The scale an open card is drawn at. Fixed when the card is shown and read again only when the displays change: the host's
// scale (TrayContext's layout scale) is rewritten by every taskbar poll, so a card that asked for it at each redraw would
// be resized and placed again whenever that value moved, with nothing about the card having changed.
internal sealed class CardDpiLatch
{
    private int? _fixed;

    // The scale to draw at now: the one fixed at the show, or the host's while nothing is shown.
    public int Current(Func<int> host) => _fixed ?? host();

    // A card is being shown: fix the scale, the display's own when one is given, else the host's now.
    public int FixAtShow(int? displayDpi, Func<int> host)
    {
        _fixed = displayDpi ?? host();
        return _fixed.Value;
    }

    // The displays changed: read the scale again, from the display when it is known, else from the host.
    public void RereadOnDisplayChange(int? displayDpi, Func<int> host)
    {
        if (_fixed is not null)
        {
            _fixed = displayDpi ?? host();
        }
    }

    // The card is gone: nothing is fixed.
    public void Release() => _fixed = null;
}
