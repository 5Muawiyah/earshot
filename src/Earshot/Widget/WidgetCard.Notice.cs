using Earshot.Popup;

namespace Earshot.Widget;

// A notice carried by the card the owner asked for. While that card is open a notice does not open another window: on the main
// page the status row says it (growing to as many lines as it needs, up to three, with its button at the right of the last line);
// on the settings, update and history pages a one-row strip at the foot of the page says it, bottom pinned. Nothing is carried by
// an unasked card, which shows a notice as its own compact row.
internal sealed partial class WidgetCard
{
    private WidgetCardLayout.CompactLayout? _stripLayout;
    private int _noticeActionWidth;
    private bool _leftButtonDownOnStrip;

    // The notice's button was pressed, genuinely: a left down and up on it, or the keyboard or a screen reader.
    public event EventHandler? NoticeActionRequested;

    private CompactContent? Carried => _unasked ? null : _model.Notice;

    internal bool NoticeOnStatusRow => Carried is not null && EffectiveView == WidgetCardView.Main;

    internal bool NoticeOnStrip => Carried is not null && EffectiveView != WidgetCardView.Main;

    // The status row does something when pressed: a carried notice's button, or the way to Bluetooth settings when it is off.
    internal bool StatusIsAButton => !_unasked && (NoticeOnStatusRow ? Carried?.Action is not null : BluetoothOff);

    // The strip under a sub-page, for tests; null when the page carries none.
    internal WidgetCardLayout.CompactLayout? CurrentStripLayout => _stripLayout;

    // The height the carried notice adds below a sub-page.
    private int StripHeight => _stripLayout?.Height ?? 0;

    // Measures the carried notice for the page about to be laid out: the strip's layout under a sub-page, and for the main page the
    // button's width and how many lines the words need beside the glyph and the button. Returns the lines of the status row.
    private int MeasureNotice(Graphics measure)
    {
        _stripLayout = null;
        _noticeActionWidth = 0;
        if (Carried is not { } notice)
        {
            return 1;
        }

        if (NoticeOnStrip)
        {
            _stripLayout = ComputeCompactLayout(measure, notice);
            return 1;
        }

        int gap = CardPlacement.Scale(8, _dpi);
        if (notice.Action is { } action)
        {
            _noticeActionWidth = CardPaint.LineWidth(measure, action.Label, _type, CardPlacement.Scale(14, _dpi), bold: false) + (2 * gap);
        }

        int contentWidth = WidgetCardLayout.WidthFor(_dpi) - (2 * CardPlacement.Scale(WidgetCardLayout.PaddingAt96, _dpi));
        int glyph = notice.Glyph != '\0' ? TextFit.Grow(CardPaint.GlyphSizeAt96, _dpi, _look.TextScale) + gap : 0;
        int button = _noticeActionWidth > 0 ? _noticeActionWidth + gap : 0;
        int lines = CardPaint.Lines(measure, notice.Text, Math.Max(1, contentWidth - glyph - button), _type, CardPlacement.Scale(14, _dpi), bold: false);
        return Math.Clamp(lines, 1, WidgetCardLayout.CompactMaxLines);
    }

    // The carried notice's button on the main page: at the right of the status row's last line.
    private Rectangle NoticeActionRect()
    {
        if (!NoticeOnStatusRow || Carried?.Action is null || _noticeActionWidth <= 0)
        {
            return Rectangle.Empty;
        }

        Rectangle row = _mainLayout.WhereLine;
        int height = WidgetCardLayout.RowHeight(_dpi, _look.TextScale);
        return new Rectangle(row.Right - _noticeActionWidth, row.Bottom - height, _noticeActionWidth, height);
    }

    // The notice's button on the main page, for tests.
    internal Rectangle NoticeActionBounds => NoticeActionRect();

    private void DrawCarriedNotice(Graphics g, Rectangle bounds, CompactContent notice)
    {
        CardColours colours = Colours;
        int gap = CardPlacement.Scale(8, _dpi);
        int textX = bounds.X;
        if (notice.Glyph != '\0')
        {
            int box = TextFit.Grow(CardPaint.GlyphSizeAt96, _dpi, _look.TextScale);
            var iconRect = new Rectangle(bounds.X, bounds.Y + ((bounds.Height - box) / 2), box, box);
            if (CardPaint.TryGlyph(g, notice.Glyph, iconRect, CompactInk(notice.Ink, colours), _dpi, CardPaint.GlyphSizeAt96, _look.TextScale))
            {
                textX = iconRect.Right + gap;
            }
        }

        Rectangle action = NoticeActionRect();
        int right = action.IsEmpty ? bounds.Right : action.X - gap;
        bool wraps = bounds.Height > WidgetCardLayout.RowHeight(_dpi, _look.TextScale);
        DrawText(g, new Rectangle(textX, bounds.Y, Math.Max(1, right - textX), bounds.Height), notice.Text, colours.Text, TypeRole.Body, StringAlignment.Near, wrap: wraps);
        if (!action.IsEmpty && notice.Action is { } button)
        {
            DrawText(g, action, button.Label, colours.HighContrast ? colours.Text : colours.Accent, TypeRole.Body, StringAlignment.Far);
            if (_focus == WidgetCardFocus.Status && FocusShown)
            {
                CardPaint.Focus(g, action, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
            }
        }
    }

    // ---- The strip under a sub-page

    private Rectangle StripRect() =>
        _stripLayout is { } layout ? new Rectangle(0, ClientSize.Height - layout.Height, layout.Width, layout.Height) : Rectangle.Empty;

    private Rectangle StripActionRect()
    {
        if (_stripLayout is not { } layout || layout.Action.IsEmpty)
        {
            return Rectangle.Empty;
        }

        Rectangle strip = StripRect();
        return new Rectangle(layout.Action.X, strip.Y + layout.Action.Y, layout.Action.Width, layout.Action.Height);
    }

    // The strip's button under a sub-page, for tests.
    internal Rectangle StripActionBounds => StripActionRect();

    private void DrawStrip(Graphics g)
    {
        if (_stripLayout is not { } layout || Carried is not { } notice)
        {
            return;
        }

        CardColours colours = Colours;
        Rectangle strip = StripRect();
        using (var pen = new Pen(colours.ControlStroke, 1f))
        {
            g.DrawLine(pen, strip.Left, strip.Top, strip.Right, strip.Top);
        }

        if (!layout.Glyph.IsEmpty)
        {
            _ = CardPaint.TryGlyph(
                g, notice.Glyph, new Rectangle(layout.Glyph.X, strip.Y + layout.Glyph.Y, layout.Glyph.Width, layout.Glyph.Height),
                CompactInk(notice.Ink, colours), _dpi, WidgetCardLayout.CompactGlyphAt96, _look.TextScale);
        }

        DrawText(
            g, new Rectangle(layout.Text.X, strip.Y + layout.Text.Y, layout.Text.Width, layout.Text.Height), notice.Text, colours.Text, TypeRole.Body,
            StringAlignment.Near, wrap: layout.Lines > 1);
        if (notice.Action is { } action && StripActionRect() is { IsEmpty: false } button)
        {
            DrawText(g, button, action.Label, colours.HighContrast ? colours.Text : colours.Accent, TypeRole.Body, StringAlignment.Far);
        }
    }

    // Returns true when the press landed on the strip, so the page under it does not see it.
    private bool StripMouseDown(MouseEventArgs e)
    {
        if (_stripLayout is null)
        {
            return false;
        }

        _leftButtonDownOnStrip = e.Button == MouseButtons.Left && StripActionRect().Contains(e.Location);
        return StripRect().Contains(e.Location);
    }

    private bool StripMouseUp(MouseEventArgs e)
    {
        if (_stripLayout is null)
        {
            return false;
        }

        bool onButton = _leftButtonDownOnStrip && e.Button == MouseButtons.Left && StripActionRect().Contains(e.Location);
        _leftButtonDownOnStrip = false;
        if (onButton)
        {
            NoticeActionRequested?.Invoke(this, EventArgs.Empty);
        }

        return onButton || StripRect().Contains(e.Location);
    }
}
