using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The history chart as geometry (no drawing), the days it steps over and the words said for it. Pure: a fixed rectangle, a fixed
// clock and fixed zones, so every position can be written out.
[TestClass]
public sealed class HistoryChartLayoutTests
{
    // Friday 2 October 2026, 15:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly TimeZoneInfo Plus1 = TimeZoneInfo.CreateCustomTimeZone("plus1", TimeSpan.FromHours(1), "plus1", "plus1");

    // A 240 by 100 plot at (10, 20): 10 px an hour across a 24 hour window, 1 px for each percent.
    private static readonly RectangleF Plot = new(10, 20, 240, 100);

    private static HistorySample Sample(ChargeComponent part, int percent, DateTimeOffset at, bool charging = false) => new(part, percent, charging, at);

    private static HistoryWindow WindowOf(DateTimeOffset end, params HistorySample[] samples) => HistoryStore.Window(samples, end);

    // ---- Points

    [TestMethod]
    public void APointIsMappedToThePlotWithZeroAtTheBottomAndTheWindowEndAtTheRight()
    {
        DateTimeOffset end = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        HistoryWindow window = WindowOf(
            end,
            Sample(ChargeComponent.Left, 100, end - TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1)),
            Sample(ChargeComponent.Left, 50, end - TimeSpan.FromHours(12)),
            Sample(ChargeComponent.Left, 0, end));

        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, window, Utc);

        PointF[] points = chart.Lines.Single(l => l.Part == ChargeComponent.Left).Segments.SelectMany(seg => seg).ToArray(); // the samples are hours apart: gaps, one point each
        Assert.HasCount(3, points);
        Assert.AreEqual(10f + (240f / 1440f), points[0].X, 0.001f, "One minute after the start.");
        Assert.AreEqual(20f, points[0].Y, 0.001f, "100% is the plot's top.");
        Assert.AreEqual(130f, points[1].X, 0.001f, "Noon of the window is its middle.");
        Assert.AreEqual(70f, points[1].Y, 0.001f, "50% is half way.");
        Assert.AreEqual(250f, points[2].X, 0.001f, "The end is the plot's right.");
        Assert.AreEqual(120f, points[2].Y, 0.001f, "0% is the plot's bottom.");
    }

    [TestMethod]
    public void EveryPartHasALineAndAPartWithNoSampleHasNoSegment()
    {
        HistoryWindow window = WindowOf(Now, Sample(ChargeComponent.Right, 80, Now - TimeSpan.FromHours(1)));

        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, window, Utc);

        Assert.AreEqual("Left Right Case", string.Join(" ", chart.Lines.Select(l => l.Part)));
        Assert.IsEmpty(chart.Lines[0].Segments);
        Assert.HasCount(1, chart.Lines[1].Segments);
        Assert.IsEmpty(chart.Lines[2].Segments);
    }

    // ---- Gaps

    [TestMethod]
    public void AGapSplitsThePolylineAndIsMarkedWithoutALineAcrossIt()
    {
        DateTimeOffset a = Now - TimeSpan.FromHours(10);
        DateTimeOffset b = a + TimeSpan.FromMinutes(1);
        DateTimeOffset c = b + TimeSpan.FromMinutes(60);
        DateTimeOffset d = c + TimeSpan.FromMinutes(1);
        HistoryWindow window = WindowOf(
            Now,
            Sample(ChargeComponent.Left, 90, a), Sample(ChargeComponent.Left, 89, b), Sample(ChargeComponent.Left, 70, c), Sample(ChargeComponent.Left, 69, d));

        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, window, Utc);

        HistoryLine left = chart.Lines[0];
        Assert.HasCount(2, left.Segments, "Two runs, not one line across the hour with nothing heard.");
        Assert.HasCount(2, left.Segments[0]);
        Assert.HasCount(2, left.Segments[1]);
        HistoryGapMark mark = chart.Gaps.Single();
        Assert.AreEqual(ChargeComponent.Left, mark.Part);
        Assert.AreEqual(left.Segments[0][1].X, mark.X1, 0.001f, "The mark runs from the end of the first run.");
        Assert.AreEqual(left.Segments[1][0].X, mark.X2, 0.001f, "To the start of the next.");
    }

    [TestMethod]
    public void AGapInOnePartDoesNotSplitAnother()
    {
        DateTimeOffset a = Now - TimeSpan.FromHours(5);
        HistoryWindow window = WindowOf(
            Now,
            Sample(ChargeComponent.Left, 90, a), Sample(ChargeComponent.Left, 80, a + TimeSpan.FromHours(1)),
            Sample(ChargeComponent.Right, 90, a), Sample(ChargeComponent.Right, 89, a + TimeSpan.FromMinutes(5)), Sample(ChargeComponent.Right, 88, a + TimeSpan.FromMinutes(9)));

        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, window, Utc);

        Assert.HasCount(2, chart.Lines[0].Segments);
        Assert.HasCount(1, chart.Lines[1].Segments);
        Assert.AreEqual(ChargeComponent.Left, chart.Gaps.Single().Part);
    }

    [TestMethod]
    public void ALoneSampleIsASegmentOfOnePoint()
    {
        HistoryWindow window = WindowOf(Now, Sample(ChargeComponent.Case, 40, Now - TimeSpan.FromHours(2)));

        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, window, Utc);

        Assert.HasCount(1, chart.Lines[2].Segments.Single());
    }

    // ---- Labels

    [TestMethod]
    public void ThePercentLabelsAreZeroFiftyAndOneHundredAtTheirHeights()
    {
        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, WindowOf(Now), Utc);

        Assert.AreEqual("0 50 100", string.Join(" ", chart.YLabels.Select(l => l.Text)));
        Assert.AreEqual("120 70 20", string.Join(" ", chart.YLabels.Select(l => l.Position)));
    }

    [TestMethod]
    public void TheTimeLabelsAreEverySixHoursOnTheLocalClockWithTheWindowsStartIncluded()
    {
        // The 24 hours up to this morning's midnight: 00:00 at the left edge, then 06:00, 12:00 and 18:00.
        DateTimeOffset end = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, WindowOf(end), Utc);

        Assert.AreEqual("00:00 06:00 12:00 18:00", string.Join(" ", chart.XLabels.Select(l => l.Text)));
        Assert.AreEqual("10 70 130 190", string.Join(" ", chart.XLabels.Select(l => l.Position)));
    }

    [TestMethod]
    public void TheTimeLabelsOfTodayFollowTheClockNotTheWindowsStart()
    {
        // 15:00 now: the window starts at 15:00 yesterday, so the labels are 18:00, 00:00, 06:00, 12:00.
        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, WindowOf(Now), Utc);

        Assert.AreEqual("18:00 00:00 06:00 12:00", string.Join(" ", chart.XLabels.Select(l => l.Text)));
        Assert.AreEqual(10f + (3f * 10f), chart.XLabels[0].Position, 0.001f, "Three hours into the window.");
    }

    [TestMethod]
    public void TheTimeLabelsUseTheZonesClock()
    {
        HistoryChartGeometry chart = HistoryChartLayout.Compute(Plot, WindowOf(Now), Plus1);

        Assert.AreEqual("18:00 00:00 06:00 12:00", string.Join(" ", chart.XLabels.Select(l => l.Text)));
        Assert.AreEqual(10f + (2f * 10f), chart.XLabels[0].Position, 0.001f, "17:00 UTC is 18:00 at +1, two hours into the window.");
    }

    // ---- Latest values

    [TestMethod]
    public void TheLegendValuesAreEachPartsNewestSampleInTheWindow()
    {
        HistoryWindow window = WindowOf(
            Now,
            Sample(ChargeComponent.Left, 90, Now - TimeSpan.FromHours(3)), Sample(ChargeComponent.Left, 70, Now - TimeSpan.FromMinutes(5), charging: true),
            Sample(ChargeComponent.Case, 40, Now - TimeSpan.FromHours(6)));

        IReadOnlyList<HistoryLatest> latest = HistoryChartLayout.LatestOf(window);

        Assert.HasCount(2, latest, "Right has nothing in the window.");
        Assert.AreEqual(new HistoryLatest(ChargeComponent.Left, 70, true, Now - TimeSpan.FromMinutes(5)), latest[0]);
        Assert.AreEqual(ChargeComponent.Case, latest[1].Part);
    }

    // ---- Days

    [TestMethod]
    public void TodayIsTheLastTwentyFourHoursAndEarlierDaysEndAtALocalMidnight()
    {
        Assert.AreEqual(Now, HistoryDays.End(0, Now, Utc));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), HistoryDays.End(1, Now, Utc), "Yesterday ends at this morning's midnight.");
        Assert.AreEqual(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), HistoryDays.End(3, Now, Utc));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.FromHours(1)), HistoryDays.End(1, Now, Plus1), "At +1 the midnight is an hour earlier in UTC.");
    }

    [TestMethod]
    public void TheLabelsAreTodayYesterdayThenTheWeekdayName()
    {
        string labels = string.Join(", ", Enumerable.Range(0, 7).Select(d => HistoryDays.Label(d, Now, Utc)));

        Assert.AreEqual("Today, Yesterday, Wednesday, Tuesday, Monday, Sunday, Saturday", labels);
    }

    [TestMethod]
    public void SteppingIsBoundedByTodayAndTheOldestKeptDay()
    {
        Assert.IsFalse(HistoryDays.CanGoForward(0), "Forward is off on today.");
        Assert.IsTrue(HistoryDays.CanGoBack(0));
        Assert.IsTrue(HistoryDays.CanGoForward(HistoryDays.MaxDaysBack));
        Assert.IsFalse(HistoryDays.CanGoBack(HistoryDays.MaxDaysBack), "Back is off at the oldest kept day.");
        Assert.AreEqual(HistoryDays.MaxDaysBack, HistoryDays.Clamp(40));
        Assert.AreEqual(0, HistoryDays.Clamp(-3));
        Assert.AreEqual(HistoryDays.MaxDaysBack + 1, HistoryStore.Retention.Days, "Today and six more: the seven days the store keeps.");
        Assert.IsGreaterThanOrEqualTo(
            Now - HistoryStore.Retention, HistoryDays.End(HistoryDays.MaxDaysBack, Now, Utc) - HistoryStore.WindowLength, "The oldest day starts inside what is kept.");
    }

    [TestMethod]
    public void TheViewAsksTheQueryForTheDaysWindowAndSaysWhatSteppingCanDo()
    {
        DateTimeOffset asked = default;
        HistoryView view = HistoryDays.View(
            1, Now, Utc, end =>
            {
                asked = end;
                return WindowOf(end, Sample(ChargeComponent.Left, 70, end - TimeSpan.FromHours(1)));
            });

        Assert.AreEqual(HistoryDays.End(1, Now, Utc), asked);
        Assert.AreEqual("Yesterday", view.DayLabel);
        Assert.IsTrue(view.CanBack);
        Assert.IsTrue(view.CanForward);
        Assert.HasCount(1, view.Window.Samples);

        HistoryView none = HistoryDays.View(0, Now, Utc, null);
        Assert.IsEmpty(none.Window.Samples, "No store, an empty day.");
        Assert.IsFalse(none.CanForward);
    }

    // ---- Words and style

    [TestMethod]
    public void TheChartSummaryNamesTheDayAndEachPartsLatestValue()
    {
        HistoryWindow window = WindowOf(
            Now,
            Sample(ChargeComponent.Left, 70, Now - TimeSpan.FromMinutes(5), charging: true), Sample(ChargeComponent.Right, 80, Now - TimeSpan.FromMinutes(5)),
            Sample(ChargeComponent.Case, 50, Now - TimeSpan.FromHours(6)));

        string text = HistorySpeech.Summary("Today", window, Utc);

        Assert.AreEqual("Battery history, Today. Left 70%, charging at 14:55. Right 80% at 14:55. Case 50% at 09:00.", text);
    }

    [TestMethod]
    public void TheChartSummaryCountsTheGapsAndSaysWhenNothingWasHeard()
    {
        Assert.AreEqual("Battery history, Yesterday. Nothing heard.", HistorySpeech.Summary("Yesterday", WindowOf(Now), Utc));

        DateTimeOffset a = Now - TimeSpan.FromHours(5);
        HistoryWindow gapped = WindowOf(Now, Sample(ChargeComponent.Left, 90, a), Sample(ChargeComponent.Left, 80, a + TimeSpan.FromHours(1)));
        StringAssert.EndsWith(HistorySpeech.Summary("Today", gapped, Utc), "Nothing heard in 1 stretch.");
    }

    [TestMethod]
    public void ThePartsDifferInDashAsWellAsInColour()
    {
        HistoryDash[] dashes = Enum.GetValues<ChargeComponent>().Select(HistoryStyle.DashOf).ToArray();

        Assert.HasCount(3, dashes.Distinct(), "A dash pattern each, so colour is not the only difference.");
        Color accent = Color.FromArgb(0, 0x5F, 0xB8);
        Color[] tints = Enum.GetValues<ChargeComponent>().Select(p => HistoryStyle.ColourOf(p, accent, Color.Black, Color.Gray, highContrast: false)).ToArray();
        Assert.AreEqual(accent, tints[0], "Left is the accent.");
        Assert.HasCount(3, tints.Distinct());
        Assert.IsGreaterThan(accent.GetBrightness(), tints[1].GetBrightness(), "Right is lighter.");
        Assert.IsLessThan(accent.GetBrightness(), tints[2].GetBrightness(), "Case is darker.");
    }

    [TestMethod]
    public void UnderHighContrastThePartsAreHighlightWindowTextAndGrayText()
    {
        Color highlight = Color.Blue;
        Color text = Color.White;
        Color gray = Color.Gray;

        Color[] colours = Enum.GetValues<ChargeComponent>().Select(p => HistoryStyle.ColourOf(p, highlight, text, gray, highContrast: true)).ToArray();

        Assert.AreEqual(highlight, colours[0]);
        Assert.AreEqual(text, colours[1]);
        Assert.AreEqual(gray, colours[2]);
    }

    // ---- The page

    [TestMethod]
    public void ThePageHasTheStepButtonsAtItsEndsAndTheChartInsideItsSurface()
    {
        HistoryPageLayout page = HistoryPageLayout.Compute(96, 1.0);

        Assert.AreEqual(32, page.DayBack.Width, "An icon button, 32 square at 100%.");
        Assert.AreEqual(32, page.DayBack.Height);
        Assert.AreEqual(32, page.DayForward.Width);
        Assert.AreEqual(12, page.DayBack.X);
        Assert.AreEqual(360 - 12, page.DayForward.Right);
        Assert.IsTrue(page.Surface.Contains(page.Plot));
        Assert.IsTrue(page.Surface.Contains(page.XLabels));
        Assert.HasCount(3, page.Legend);
        Assert.IsTrue(page.Legend.All(r => page.Surface.Contains(r)));
        Assert.AreEqual(120, page.Plot.Height);
        Assert.IsGreaterThan(page.Plot.Bottom, page.XLabels.Top, "The time labels are under the plot.");
        Assert.IsGreaterThanOrEqualTo(page.Surface.Bottom, page.Frame.Height, "The frame holds the surface.");
    }

    [TestMethod]
    public void ThePageGrowsWithTheTextSize()
    {
        HistoryPageLayout plain = HistoryPageLayout.Compute(96, 1.0);
        HistoryPageLayout large = HistoryPageLayout.Compute(96, 1.5);

        Assert.AreEqual(40, large.DayBack.Width, "Icon button box 16t + 16.");
        Assert.IsGreaterThan(plain.Frame.Height, large.Frame.Height);
    }
}
