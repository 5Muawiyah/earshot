using Earshot.Popup;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Earshot.Widget;

// The battery history page's painter: a day's label between two step buttons, then one surface holding the 24 hour chart (percent
// labels at its left, time labels under it) and a legend of the three parts with their latest values. The geometry is
// HistoryChartLayout's; this draws it.
internal sealed partial class WidgetCard
{
    private void DrawHistoryPage(Graphics g, HistoryView view, HistoryPageLayout page, CardColours colours, bool focusVisible, SetupTarget focus)
    {
        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);
        int twelve = CardPlacement.Scale(12, _dpi);
        float scale = _dpi / 96f;

        DrawStepButton(g, page.DayBack, FluentGlyphs.ChevronLeft, left: true, view.CanBack, colours, focusVisible && focus == new SetupTarget(SetupTargetKind.Day, 0), radius);
        DrawStepButton(g, page.DayForward, FluentGlyphs.ChevronRight, left: false, view.CanForward, colours, focusVisible && focus == new SetupTarget(SetupTargetKind.Day, 1), radius);
        CardPaint.Text(
            g, view.DayLabel, page.DayLabel, _type, CardPlacement.Scale(14, _dpi), bold: true, colours.Text, StringAlignment.Center, StringAlignment.Center);

        CardPaint.Surface(g, page.Surface, radius, colours.RowFill, colours.RowStroke);

        HistoryChartGeometry chart = HistoryChartLayout.Compute(page.Plot, view.Window, view.Zone);
        int lineHeight = page.XLabels.Height;

        // Gridlines at 0, 50 and 100, and the percent labels at the left in the secondary text.
        foreach (HistoryLabel y in chart.YLabels)
        {
            using (var grid = new Pen(colours.Divider, 1f))
            {
                float row = (float)Math.Round(y.Position) + 0.5f;
                g.DrawLine(grid, page.Plot.Left, row, page.Plot.Right, row);
            }

            var rect = new Rectangle(page.YLabels.X, (int)Math.Round(y.Position) - (lineHeight / 2), page.YLabels.Width, lineHeight);
            DrawTabular(g, y.Text, rect, twelve, colours.TextSecondary, StringAlignment.Far);
        }

        // The time labels every 6 hours under the plot, each centred on its tick and kept inside the surface.
        int labelWidth = CardPlacement.Scale(40, _dpi);
        foreach (HistoryLabel x in chart.XLabels)
        {
            int left = (int)Math.Round(x.Position) - (labelWidth / 2);
            left = Math.Clamp(left, page.XLabels.Left, Math.Max(page.XLabels.Left, page.XLabels.Right - labelWidth));
            DrawTabular(g, x.Text, new Rectangle(left, page.XLabels.Y, labelWidth, page.XLabels.Height), twelve, colours.TextSecondary, StringAlignment.Center);
        }

        SmoothingMode before = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float width = Math.Max(1.5f, 2f * scale);

        // Left is drawn last so it is on top where the lines meet.
        foreach (HistoryLine line in chart.Lines.Reverse())
        {
            using Pen pen = LinePen(line.Part, colours, width);
            using var dot = new SolidBrush(pen.Color);
            foreach (IReadOnlyList<PointF> segment in line.Segments)
            {
                if (segment.Count >= 2)
                {
                    g.DrawLines(pen, segment.ToArray());
                }
                else if (segment.Count == 1)
                {
                    // One sample on its own: a dot, since a line needs two.
                    g.FillEllipse(dot, segment[0].X - width, segment[0].Y - width, 2 * width, 2 * width);
                }
            }
        }

        // Gaps in the stale style: a tertiary bracket along the bottom, one row for each part, with nothing drawn across the gap.
        using (var bracket = new Pen(colours.TextTertiary, 1f))
        {
            float tick = 4f * scale;
            foreach (HistoryGapMark gap in chart.Gaps)
            {
                float y = page.Plot.Bottom - ((3f + (4f * (int)gap.Part)) * scale);
                g.DrawLine(bracket, gap.X1, y, gap.X2, y);
                g.DrawLine(bracket, gap.X1, y - (tick / 2), gap.X1, y + (tick / 2));
                g.DrawLine(bracket, gap.X2, y - (tick / 2), gap.X2, y + (tick / 2));
            }
        }

        g.SmoothingMode = before;

        if (view.Window.Samples.Count == 0)
        {
            CardPaint.Text(g, WidgetCopy.HistoryNothingHeard, page.Plot, _type, twelve, bold: false, colours.TextSecondary, StringAlignment.Center, StringAlignment.Center);
        }

        // The legend: each part's line sample, its letter and its latest value.
        int swatch = CardPlacement.Scale(16, _dpi);
        int swatchGap = CardPlacement.Scale(6, _dpi);
        SmoothingMode legendBefore = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < page.Legend.Count; i++)
        {
            var part = (ChargeComponent)i;
            Rectangle cell = page.Legend[i];
            using (Pen pen = LinePen(part, colours, width))
            {
                float mid = cell.Y + (cell.Height / 2f);
                g.DrawLine(pen, cell.X, mid, cell.X + swatch, mid);
            }

            HistoryLatest? latest = chart.Latest.FirstOrDefault(l => l.Part == part);
            string text = HistoryStyle.LegendLabel(part) + (latest is null ? "" : " " + latest.Percent.ToString(CultureInfo.InvariantCulture) + "%");
            var textRect = new Rectangle(cell.X + swatch + swatchGap, cell.Y, Math.Max(1, cell.Width - swatch - swatchGap), cell.Height);
            CardPaint.Text(g, text, textRect, _type, twelve, bold: false, colours.Text, StringAlignment.Near, StringAlignment.Center);
        }

        g.SmoothingMode = legendBefore;
    }

    private static Pen LinePen(ChargeComponent part, CardColours colours, float width)
    {
        Color colour = HistoryStyle.ColourOf(part, colours.Accent, colours.Text, colours.TextTertiary, colours.HighContrast);
        var pen = new Pen(colour, width) { LineJoin = LineJoin.Round };
        switch (HistoryStyle.DashOf(part))
        {
            case HistoryDash.Dash:
                pen.DashPattern = [4f, 3f];
                break;
            case HistoryDash.Dot:
                pen.DashPattern = [1f, 2f];
                pen.DashCap = DashCap.Round;
                break;
        }

        return pen;
    }

    // An icon button holding a left or right chevron: the glyph, or two strokes without the icon font; dimmed when it cannot step.
    // The hover and pressed fill is drawn under it with the other icon buttons' (DrawFills).
    private void DrawStepButton(Graphics g, Rectangle rect, char glyph, bool left, bool enabled, CardColours colours, bool focused, int radius)
    {
        Color ink = enabled ? colours.Text : colours.TextDisabled;
        if (!CardPaint.TryGlyph(g, glyph, rect, ink, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale))
        {
            float s = _dpi / 96f;
            float cx = rect.X + (rect.Width / 2f);
            float cy = rect.Y + (rect.Height / 2f);
            float dx = (left ? -1 : 1) * 3f * s;
            using var pen = new Pen(ink, Math.Max(1f, 1.2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            SmoothingMode before = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLines(pen, new PointF[] { new(cx - dx, cy - (5 * s)), new(cx + dx, cy), new(cx - dx, cy + (5 * s)) });
            g.SmoothingMode = before;
        }

        if (focused)
        {
            CardPaint.Focus(g, rect, radius, colours, _dpi);
        }
    }

    // Text with every digit in a cell as wide as a "0", so a figure keeps its width as its digits change (GDI+ has no tabular
    // figures switch; the gauge does the same). Other characters take their own width. Drawn within bounds, vertically centred.
    private void DrawTabular(Graphics g, string text, Rectangle bounds, int pixelSize, Color colour, StringAlignment horizontal)
    {
        using Font font = _type.Font(pixelSize, false);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip };
        float cell = g.MeasureString("0", font, PointF.Empty, format).Width;
        float Advance(char c) => char.IsAsciiDigit(c) ? cell : g.MeasureString(c.ToString(), font, PointF.Empty, format).Width;
        float total = text.Sum(Advance);
        float x = horizontal switch
        {
            StringAlignment.Far => bounds.Right - total,
            StringAlignment.Center => bounds.X + ((bounds.Width - total) / 2f),
            _ => bounds.X,
        };
        float height = g.MeasureString("0", font, PointF.Empty, format).Height;
        float y = bounds.Y + ((bounds.Height - height) / 2f);
        foreach (char c in text)
        {
            float advance = Advance(c);
            float glyph = g.MeasureString(c.ToString(), font, PointF.Empty, format).Width;

            // A digit narrower than its cell is centred in it.
            g.DrawString(c.ToString(), font, brush, x + (char.IsAsciiDigit(c) ? (advance - glyph) / 2f : 0f), y, format);
            x += advance;
        }
    }
}
