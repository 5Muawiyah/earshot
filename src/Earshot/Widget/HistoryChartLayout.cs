using Earshot.Popup;
using System.Globalization;

namespace Earshot.Widget;

// How the battery history page steps back over the days the store keeps, and what each day is called. Pure: the clock and the
// time zone are handed in.
//
// Design choices, none of them from a source: day 0 is Today, the 24 hours ending now; day n of 1 to 6 is the 24 hours ending at the
// local midnight that closes the calendar date n days ago (so Yesterday is the 24 hours up to this morning's midnight). The store
// keeps 7 days (HistoryStore.Retention), so the oldest day that is whole is 6 days back. A day that has a daylight saving change in
// it is still 24 hours (HistoryStore.WindowLength), so on that day its first or last hour is the neighbour's.
internal static class HistoryDays
{
    // Today and the six calendar days before it: the 7 days the store keeps.
    public const int MaxDaysBack = 6;

    public static int Clamp(int day) => Math.Clamp(day, 0, MaxDaysBack);

    // Back is offered until the oldest kept day; forward is offered on any day but today.
    public static bool CanGoBack(int day) => day < MaxDaysBack;

    public static bool CanGoForward(int day) => day > 0;

    // The end of a day's 24 hour window: now for today, else the local midnight that closes the date that many days ago.
    public static DateTimeOffset End(int day, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        int d = Clamp(day);
        if (d == 0)
        {
            return now;
        }

        DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return LocalMidnight(today.AddDays(1 - d), zone);
    }

    // "Today", "Yesterday", then the weekday name of the date the window covers ("Monday").
    public static string Label(int day, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        int d = Clamp(day);
        if (d == 0)
        {
            return WidgetCopy.HistoryToday;
        }

        if (d == 1)
        {
            return WidgetCopy.HistoryYesterday;
        }

        DateTime date = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(-d);
        return date.ToString("dddd", CultureInfo.InvariantCulture);
    }

    // The page's view of a day: its label, its window from the store (an empty one when there is no store) and what stepping can do.
    public static HistoryView View(int day, DateTimeOffset now, TimeZoneInfo zone, Func<DateTimeOffset, HistoryWindow>? query)
    {
        int d = Clamp(day);
        DateTimeOffset end = End(d, now, zone);
        HistoryWindow window = query?.Invoke(end) ?? HistoryStore.Window([], end);
        string label = Label(d, now, zone);
        return new HistoryView(d, label, window, zone, CanGoBack(d), CanGoForward(d), HistorySpeech.Summary(label, window, zone));
    }

    private static DateTimeOffset LocalMidnight(DateTime date, TimeZoneInfo zone)
    {
        DateTime local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // A zone whose clocks skip midnight: the first moment that exists.
            local = local.AddHours(1);
        }

        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}

// What the history page draws: the day, its window, and which of the two step buttons work.
internal sealed record HistoryView(
    int Day, string DayLabel, HistoryWindow Window, TimeZoneInfo Zone, bool CanBack, bool CanForward, string Summary);

// How a part's line is drawn. Dash patterns differ so the chart does not rely on colour alone (a design choice, for the
// high-contrast themes above all): Left solid, Right dashed, Case dotted.
internal enum HistoryDash { Solid, Dash, Dot }

internal static class HistoryStyle
{
    // The share of white or black the accent is mixed with for the two other lines (design choices): Right lighter, Case darker.
    public const double LighterShare = 0.4;
    public const double DarkerShare = 0.4;

    public static HistoryDash DashOf(ChargeComponent part) => part switch
    {
        ChargeComponent.Left => HistoryDash.Solid,
        ChargeComponent.Right => HistoryDash.Dash,
        _ => HistoryDash.Dot,
    };

    // Left is the accent, Right the accent mixed with white, Case the accent mixed with black. Under a high-contrast theme the
    // three are Highlight, WindowText and GrayText (the accent, the primary text and the tertiary text of the theme's tokens).
    public static Color ColourOf(ChargeComponent part, Color accent, Color text, Color tertiary, bool highContrast)
    {
        if (highContrast)
        {
            return part switch
            {
                ChargeComponent.Left => accent,
                ChargeComponent.Right => text,
                _ => tertiary,
            };
        }

        return part switch
        {
            ChargeComponent.Left => accent,
            ChargeComponent.Right => Mix(accent, Color.White, LighterShare),
            _ => Mix(accent, Color.Black, DarkerShare),
        };
    }

    public static Color Mix(Color from, Color to, double share)
    {
        int Channel(int a, int b) => (int)Math.Round(a + ((b - a) * share), MidpointRounding.AwayFromZero);
        return Color.FromArgb(from.A, Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    public static string LegendLabel(ChargeComponent part) => part switch
    {
        ChargeComponent.Left => WidgetCopy.LeftLabel,
        ChargeComponent.Right => WidgetCopy.RightLabel,
        _ => WidgetCopy.CaseLabel,
    };

    public static string SpokenLabel(ChargeComponent part) => part switch
    {
        ChargeComponent.Left => WidgetCopy.LeftWord,
        ChargeComponent.Right => WidgetCopy.RightWord,
        _ => WidgetCopy.CaseLabel,
    };
}

// The words a screen reader gets for the chart: the day and each part's latest value.
internal static class HistorySpeech
{
    // "Battery history, Today. Left 70%, charging, at 14:32. Right 80% at 14:32. Case 50% at 09:10. Nothing heard in 1 stretch."
    // Only parts with a sample in the window are named, each with the time of that sample; with none, "Nothing heard".
    public static string Summary(string dayLabel, HistoryWindow window, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(zone);
        var parts = new List<string>();
        foreach (HistoryLatest latest in HistoryChartLayout.LatestOf(window))
        {
            string time = HistoryChartLayout.TimeText(latest.At, zone);
            parts.Add(
                HistoryStyle.SpokenLabel(latest.Part) + " " + latest.Percent.ToString(CultureInfo.InvariantCulture) + "%" +
                (latest.Charging ? ", charging" : "") + " at " + time);
        }

        string head = WidgetCopy.HistoryChartName + ", " + dayLabel + ". ";
        if (parts.Count == 0)
        {
            return head + WidgetCopy.HistoryNothingHeard + ".";
        }

        string text = head + string.Join(". ", parts) + ".";
        if (window.Gaps.Count > 0)
        {
            text += " " + WidgetCopy.HistoryGaps(window.Gaps.Count);
        }

        return text;
    }
}

// A part's newest sample in a window.
internal sealed record HistoryLatest(ChargeComponent Part, int Percent, bool Charging, DateTimeOffset At);

// One part's line: each segment is a run of points with no gap in it, so a gap splits the line instead of being drawn across.
internal sealed record HistoryLine(ChargeComponent Part, IReadOnlyList<IReadOnlyList<PointF>> Segments);

// A stretch of one part with nothing heard: x positions in the plot, drawn as a bracket (see HistoryChartLayout).
internal sealed record HistoryGapMark(ChargeComponent Part, float X1, float X2);

// A label and where it sits along its axis (an x for the time labels, a y for the percent labels), in the plot's pixels.
internal sealed record HistoryLabel(string Text, float Position);

internal sealed record HistoryChartGeometry(
    RectangleF Plot,
    IReadOnlyList<HistoryLine> Lines,
    IReadOnlyList<HistoryGapMark> Gaps,
    IReadOnlyList<HistoryLabel> XLabels,
    IReadOnlyList<HistoryLabel> YLabels,
    IReadOnlyList<HistoryLatest> Latest);

// The chart as geometry, from a window and the rectangle it is drawn in. Pure: nothing is drawn here.
//
// x: the window's start (not included) at the plot's left and its end at the plot's right. y: 0% at the bottom and 100% at the top.
// A gap (HistoryStore.GapAfter between two of a part's samples) breaks that part's line there and is drawn, in the stale style
// (tertiary), as a bracket along the plot's bottom edge from one side of the gap to the other: no line is drawn across a
// stretch with nothing heard, since a line would say a level was known there (design choice, preferred to a dashed segment, which
// would look like the Right line's dashes and would show an interpolated level). Time labels are at the local clock hours that
// are multiples of 6, from the window's start (included) to its end (not).
internal static class HistoryChartLayout
{
    public const int LabelEveryHours = 6;

    public static readonly int[] PercentLabels = [0, 50, 100];

    public static HistoryChartGeometry Compute(RectangleF plot, HistoryWindow window, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(zone);
        double span = Math.Max(1, (window.End - window.Start).TotalSeconds);
        float X(DateTimeOffset at) => plot.Left + (float)(Math.Clamp((at - window.Start).TotalSeconds / span, 0, 1) * plot.Width);
        float Y(int percent) => plot.Bottom - (Math.Clamp(percent, 0, 100) / 100f * plot.Height);

        var broken = new HashSet<(ChargeComponent, DateTimeOffset, DateTimeOffset)>(window.Gaps.Select(g => (g.Part, g.From, g.To)));
        var lines = new List<HistoryLine>();
        foreach (ChargeComponent part in Enum.GetValues<ChargeComponent>())
        {
            var segments = new List<IReadOnlyList<PointF>>();
            List<PointF>? current = null;
            HistorySample? previous = null;
            foreach (HistorySample sample in window.Samples.Where(s => s.Part == part).OrderBy(s => s.At))
            {
                if (current is null || (previous is not null && broken.Contains((part, previous.At, sample.At))))
                {
                    current = [];
                    segments.Add(current);
                }

                current.Add(new PointF(X(sample.At), Y(sample.Percent)));
                previous = sample;
            }

            lines.Add(new HistoryLine(part, segments));
        }

        var gaps = window.Gaps.Select(g => new HistoryGapMark(g.Part, X(g.From), X(g.To))).ToList();
        var xLabels = new List<HistoryLabel>();
        foreach (DateTimeOffset tick in TimeTicks(window, zone))
        {
            xLabels.Add(new HistoryLabel(TimeText(tick, zone), X(tick)));
        }

        var yLabels = PercentLabels.Select(p => new HistoryLabel(p.ToString(CultureInfo.InvariantCulture), Y(p))).ToList();
        return new HistoryChartGeometry(plot, lines, gaps, xLabels, yLabels, LatestOf(window));
    }

    // Each part's newest sample in the window, Left, Right, Case; a part with none is left out.
    public static IReadOnlyList<HistoryLatest> LatestOf(HistoryWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var list = new List<HistoryLatest>();
        foreach (ChargeComponent part in Enum.GetValues<ChargeComponent>())
        {
            HistorySample? newest = window.Samples.Where(s => s.Part == part).OrderBy(s => s.At).LastOrDefault();
            if (newest is not null)
            {
                list.Add(new HistoryLatest(part, newest.Percent, newest.Charging, newest.At));
            }
        }

        return list;
    }

    // The instants in [Start, End) that are 00:00, 06:00, 12:00 or 18:00 on the zone's clock. Found by stepping 15 minutes, which
    // lands on every whole hour of every zone's offset and stays right across a daylight saving change.
    public static IReadOnlyList<DateTimeOffset> TimeTicks(HistoryWindow window, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(zone);
        var ticks = new List<DateTimeOffset>();
        long step = TimeSpan.FromMinutes(15).Ticks;
        long first = (window.Start.UtcTicks + step - 1) / step * step;
        for (long t = first; t < window.End.UtcTicks; t += step)
        {
            DateTimeOffset at = new DateTimeOffset(t, TimeSpan.Zero);
            DateTimeOffset local = TimeZoneInfo.ConvertTime(at, zone);
            if (local.Minute == 0 && local.Hour % LabelEveryHours == 0)
            {
                ticks.Add(at);
            }
        }

        return ticks;
    }

    // "14:32" on the zone's 24 hour clock, which is how the time labels and the spoken summary give a time.
    public static string TimeText(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
}

// Where everything of the history page sits, in client pixels, from the display scale and the text size. Pure. The page: a row with
// the two step buttons at its ends and the day between them, then one surface (the settings page's row fill and stroke) holding the
// chart (percent labels at its left, time labels under it) and the legend under that.
internal sealed record HistoryPageLayout(
    SubPageFrame.FrameLayout Frame,
    Rectangle DayBack,
    Rectangle DayLabel,
    Rectangle DayForward,
    Rectangle Surface,
    Rectangle Plot,
    Rectangle YLabels,
    Rectangle XLabels,
    IReadOnlyList<Rectangle> Legend)
{
    public const int PlotHeightAt96 = 120;
    public const int SurfacePaddingAt96 = 12;
    public const int YLabelWidthAt96 = 28;
    public const int YLabelGapAt96 = 4;
    public const int AxisGapAt96 = 4;
    public const int LegendGapAt96 = 12;
    public const int DayGapAt96 = 8;
    public const int LabelLineAt96 = 16;

    // The right end of the plot is kept this far from the surface's edge so a time label near it fits.
    public const int PlotRightAt96 = 12;

    public static HistoryPageLayout Compute(int dpi, double textScale)
    {
        int width = CardPlacement.Scale(SubPageFrame.WidthAt96, dpi);
        int side = CardPlacement.Scale(SettingsPageLayout.BodySideAt96, dpi);
        int surfaceWidth = width - (2 * side);
        int pad = CardPlacement.Scale(SurfacePaddingAt96, dpi);
        int button = WidgetCardLayout.IconButtonSize(dpi, textScale);
        int line = TextFit.Grow(LabelLineAt96, dpi, textScale);
        int y = CardPlacement.Scale(SettingsPageLayout.BodyTopAt96, dpi);

        var back = new Rectangle(side, y, button, button);
        var forward = new Rectangle(width - side - button, y, button, button);
        var label = new Rectangle(back.Right, y, Math.Max(1, forward.X - back.Right), button);
        y = back.Bottom + CardPlacement.Scale(DayGapAt96, dpi);

        int plotHeight = CardPlacement.Scale(PlotHeightAt96, dpi);
        int yLabelWidth = CardPlacement.Scale(YLabelWidthAt96, dpi);
        int yLabelGap = CardPlacement.Scale(YLabelGapAt96, dpi);
        int axisGap = CardPlacement.Scale(AxisGapAt96, dpi);
        int legendGap = CardPlacement.Scale(LegendGapAt96, dpi);
        int surfaceHeight = pad + plotHeight + axisGap + line + legendGap + line + pad;
        var surface = new Rectangle(side, y, surfaceWidth, surfaceHeight);

        int plotLeft = surface.X + pad + yLabelWidth + yLabelGap;
        int plotRight = surface.Right - CardPlacement.Scale(PlotRightAt96, dpi);
        var plot = new Rectangle(plotLeft, surface.Y + pad, Math.Max(1, plotRight - plotLeft), plotHeight);
        var yLabels = new Rectangle(surface.X + pad, plot.Y, yLabelWidth, plot.Height);
        var xLabels = new Rectangle(surface.X + pad, plot.Bottom + axisGap, surface.Width - (2 * pad), line);

        // Three equal columns for the legend: a swatch, the part's letter or word and its latest value.
        int legendTop = xLabels.Bottom + legendGap;
        int legendWidth = surface.Width - (2 * pad);
        int column = legendWidth / 3;
        var legend = new List<Rectangle>();
        for (int i = 0; i < 3; i++)
        {
            int x = surface.X + pad + (i * column);
            legend.Add(new Rectangle(x, legendTop, i == 2 ? legendWidth - (2 * column) : column, line));
        }

        int bodyHeight = surface.Bottom + CardPlacement.Scale(SettingsPageLayout.BodyBottomAt96, dpi);
        SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight, buttonCount: 0, textScale);
        int offset = frame.Body.Y;
        Rectangle Shift(Rectangle r) => new(r.X, r.Y + offset, r.Width, r.Height);
        return new HistoryPageLayout(
            frame, Shift(back), Shift(label), Shift(forward), Shift(surface), Shift(plot), Shift(yLabels), Shift(xLabels), legend.Select(Shift).ToList());
    }
}
