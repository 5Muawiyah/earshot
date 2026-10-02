using System.Drawing.Imaging;
using Earshot.Contracts;

namespace Earshot.Widget;

// Building the card before it is asked for (CardPrewarm decides when).
internal sealed partial class WidgetCardPresenter
{
    // How many times Prewarm built a card, for tests.
    internal int PrewarmCountForTest { get; private set; }

    // Makes the card, gives it the look and its first layout, draws the first frame into an off-screen bitmap (which warms the
    // fonts and the drawing path) and creates its window handle, all without showing, placing or activating it: Visible stays
    // false, so IsShown is false and the first real open takes the same path a later one does. The handle is created by
    // reading Handle, which for a Form creates the window hidden. UI thread only; does nothing once a card exists or after
    // Dispose.
    internal void Prewarm()
    {
        if (_disposed || _card is not null)
        {
            return;
        }

        WidgetCard card = EnsureCard();
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(BuildModel(), CardDpi);
        Size size = card.ClientSize;
        if (size.Width > 0 && size.Height > 0)
        {
            using var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(frame);
            card.RenderContent(g);
        }

        _ = card.Handle;
        PrewarmCountForTest++;
        _log.Write(LogLevel.Debug, "Widget card: built ahead of the first open, not shown.");
    }
}
