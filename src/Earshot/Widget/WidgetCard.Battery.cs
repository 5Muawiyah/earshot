using System.Drawing.Drawing2D;
using Earshot.Popup;

namespace Earshot.Widget;

// What the card says about the battery beyond the three columns: the read line, and the ink a part that is not
// fresh is drawn in. The values themselves come from the model's ShownParts (BatteryFreshness), so a part's age
// is decided in one place and the card only picks a colour.
internal sealed partial class WidgetCard
{
    // How much of its colour a value that is not fresh keeps. Greyed, not hidden: the figure is still the last one
    // read, and the read line says how old it is.
    private const float StaleInkOpacity = 0.55f;

    // The line under the where line: Windows' own figure when that is what is shown ("Windows reads 70%"), else
    // when the battery was last read ("Battery read 4 min ago"). The age is the newest part's: a part that is old
    // greys by itself, and must not make buds that were just heard read as old.
    internal string ReadLineText =>
        _model.Refresh?.ReadLine is { } refreshing ? refreshing
        : _model.ShownParts.WindowsPercent is int figure
            ? WidgetCopy.WindowsReads(figure)
            : AsksToOpenTheCase
                ? WidgetCopy.OpenTheCaseToShowBattery
                : WidgetCopy.BatteryReadLine(_model.ShownParts.NewestReadAt, _model.Now);

    // The AirPods are connected to this PC, no pair is linked and no figure of any kind is shown: the line says what makes
    // one show, with an earbud icon, in the place the age takes once a battery has been read.
    internal bool AsksToOpenTheCase =>
        !_notice
        && _model.Snapshot.Where == AirPodsWhere.ThisPc
        && _model.Snapshot.Selection == BroadcastSelectionState.Listening
        && _model.ShownParts.NewestReadAt is null;

    // The same line as drawn beside its clock icon: the age alone ("4 min ago"), since the icon says what is aged. The
    // full line is the tooltip, and what is drawn when no icon font is installed. A line that is not an age (a refresh
    // under way, Windows' own figure) is the same short or long.
    internal string ReadLineShort =>
        _model.Refresh?.ReadLine is { } refreshing ? refreshing
        : _model.ShownParts.WindowsPercent is int figure
            ? WidgetCopy.WindowsReads(figure)
            : AsksToOpenTheCase
                ? WidgetCopy.OpenTheCaseToShowBattery
                : WidgetCopy.ReadAge(_model.ShownParts.NewestReadAt, _model.Now);

    private static Color MutedInk(Color ink) => Color.FromArgb((int)Math.Round(ink.A * StaleInkOpacity), ink);

    // Whether the refresh icon is turning, so the presenter only runs its animation while one is.
    internal bool RefreshReading => _model.Refresh is { Reading: true };

    // Moves the turning icon on and repaints only it.
    internal void SetRefreshFrame(int frame)
    {
        if (_model.Refresh is not { Reading: true } view)
        {
            return;
        }

        _model = _model with { Refresh = view with { SpinFrame = frame } };
        if (IsHandleCreated && !_mainLayout.Refresh.IsEmpty)
        {
            Invalidate(_mainLayout.Refresh);
        }
    }

    private void DrawRefreshIcon(Graphics g, WidgetCardLayout.Layout layout)
    {
        if (layout.Refresh.IsEmpty)
        {
            return;
        }

        CardColours colours = Colours;
        bool reading = _model.Refresh is { Reading: true };
        Color ink = reading ? colours.Accent : colours.Text;
        Rectangle bounds = layout.Refresh;
        GraphicsState saved = g.Save();
        if (reading)
        {
            float centreX = bounds.X + (bounds.Width / 2f);
            float centreY = bounds.Y + (bounds.Height / 2f);
            g.TranslateTransform(centreX, centreY);
            g.RotateTransform((_model.Refresh!.SpinFrame % BatteryRefreshView.SpinFrames) * (360f / BatteryRefreshView.SpinFrames));
            g.TranslateTransform(-centreX, -centreY);
        }

        if (!CardPaint.TryGlyph(g, FluentGlyphs.Refresh, bounds, ink, _dpi))
        {
            DrawRefreshArrow(g, bounds, ink);
        }

        g.Restore(saved);
        if (_focus == WidgetCardFocus.Refresh && FocusShown)
        {
            CardPaint.Focus(g, bounds, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
        }
    }

    // The icon where no icon font is installed: most of a circle with an arrowhead at its open end, 16 px, a 1.2 px
    // stroke and round caps, like the gear beside it.
    private void DrawRefreshArrow(Graphics g, Rectangle button, Color colour)
    {
        float s = _dpi / 96f;
        float left = button.X + ((button.Width - (16 * s)) / 2f);
        float top = button.Y + ((button.Height - (16 * s)) / 2f);
        PointF P(float x, float y) => new(left + (x * s), top + (y * s));
        using var pen = new Pen(colour, 1.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawArc(pen, left + (2f * s), top + (2f * s), 12f * s, 12f * s, -60f, 280f);
        g.DrawLines(pen, new[] { P(11.6f, 0.8f), P(11.6f, 4.2f), P(8.2f, 4.2f) });
    }
}
