using System.Drawing;
using System.Drawing.Text;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every piece of text a page of the card draws through CardPaint.Text, CardPaint.Wrapped and the card's own text helpers is drawn
// whole: not cut short with an ellipsis because the rectangle it was given was sized without looking at the words. The card records
// each piece as it draws it (CardPaint.DrawnTextLog: the words, the rectangle, the font, the format and the size asked for), and each
// is measured here with GDI+'s own count of how many characters fit that rectangle, in that font, with that format and the card's text
// hint. So the check is of what is drawn, not of the layout's opinion of it, and it covers the texts nobody thought to name. Not
// recorded: the history chart's per-character figures (DrawTabular), the gauge's number, and glyphs. Text sizes 100% to 225% at display
// scales 100% to 200%. A text that does not fit is drawn smaller, but only at the larger sizes: at 100% and 125% none may be, and at
// no size may a text be smaller than the same text is at 100%.
[TestClass]
public sealed class CardTextFitTests
{
    private static readonly int[] Dpis = [96, 120, 144, 192];
    private static readonly double[] Scales = [1.0, 1.25, 1.5, 2.0, 2.25];

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static WidgetSnapshot Heard(TimeSpan age)
    {
        DateTimeOffset at = Now - age;
        return CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = at }, new PartReading(60, false, null) { ReadAt = at }, at) with
        {
            Case = new PartReading(90, false, null) { ReadAt = at },
        };
    }

    // The pages: a name for the failure message and a way to put the page on a card at a display scale.
    private static IEnumerable<(string Name, bool Notice, Action<WidgetCard, int> Show)> Pages()
    {
        foreach (TimeSpan age in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(4), TimeSpan.FromHours(30) })
        {
            TimeSpan captured = age;
            yield return ("main card, reading " + age, false, (card, dpi) => card.Render(CardKit.MainModel(Heard(captured), updateVersion: "1.4.0") with { ShowSwitch = true, AutoPauseOn = true, Now = Now }, dpi));
            yield return ("case-open card, reading " + age, true, (card, dpi) => card.Render(CardKit.MainModel(Heard(captured)) with { Now = Now }, dpi));
        }

        foreach (BatteryRefreshOutcome outcome in new[] { BatteryRefreshOutcome.NothingHeard, BatteryRefreshOutcome.BluetoothOff })
        {
            BatteryRefreshOutcome captured = outcome;
            yield return ("main card, " + outcome, false, (card, dpi) => card.Render(CardKit.MainModel(Heard(TimeSpan.FromSeconds(5))) with { Refresh = new BatteryRefreshView(false, 0, captured), Now = Now }, dpi));
        }

        yield return ("main card, nothing heard", false, (card, dpi) => card.Render(CardKit.MainModel(CardKit.Snapshot()) with { Now = Now }, dpi));
        yield return ("main card, on another device", false, (card, dpi) => card.Render(CardKit.MainModel(Heard(TimeSpan.FromSeconds(5)) with { Where = AirPodsWhere.Elsewhere }) with { Now = Now }, dpi));
        yield return ("settings, everything open", false, (card, dpi) =>
            CardKit.RenderSettings(
                card,
                CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true, InstalledVersion = "1.4.0", ConnectFailure = "Ctrl+Alt+Shift+A is already in use by another program, so it was not set. Windows reported error 1409." }),
                dpi, more: true, order: true));

        foreach (UpdateStage stage in Enum.GetValues<UpdateStage>())
        {
            UpdateStage captured = stage;
            int? percent = stage == UpdateStage.Downloading ? 60 : null;
            string? reason = stage is UpdateStage.CheckFailed or UpdateStage.DownloadFailed or UpdateStage.HandoverFailed ? "The server did not answer in time." : null;
            yield return ("update flow, " + stage, false, (card, dpi) => card.Render(CardKit.UpdateModel(CardKit.Update(captured, percent, reason)), dpi));
            yield return ("Updates page, " + stage, false, (card, dpi) => card.Render(CardKit.UpdateModelWithRows(CardKit.Update(captured, percent, reason), autoCheck: true, showRepair: true, version: "1.4.0"), dpi));
        }

        yield return ("history, today", false, (card, dpi) => card.Render(CardKit.HistoryModel(HistoryDays.View(0, Now, TimeZoneInfo.Utc, null)), dpi));
        yield return ("history, two days back", false, (card, dpi) => card.Render(CardKit.HistoryModel(HistoryDays.View(2, Now, TimeZoneInfo.Utc, null)), dpi));
    }

    [TestMethod]
    public void EveryTextOnEveryPageIsDrawnWholeAtEveryTextSizeAndDisplayScale()
    {
        Phase5.CardSta.Run(() =>
        {
            var cut = new List<string>();
            var shrunk = new List<string>();
            var atOneHundred = new Dictionary<(string Page, int Dpi, string Text), float>();
            int checkedTexts = 0;
            using var probe = new Bitmap(1, 1);
            using Graphics measure = Graphics.FromImage(probe);
            measure.TextRenderingHint = CardPaint.CardTextHint;
            foreach ((string name, bool notice, Action<WidgetCard, int> show) in Pages())
            {
                foreach (int dpi in Dpis)
                {
                    foreach (double scale in Scales)
                    {
                        using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, scale, notice);
                        CardPaint.DrawnTextLog = [];
                        try
                        {
                            show(card, dpi);
                            using Bitmap bitmap = CardKit.Render(card);
                            foreach (DrawnText drawn in CardPaint.DrawnTextLog)
                            {
                                if (drawn.Bounds.Width <= 0 || drawn.Bounds.Height <= 0 || drawn.Text.Length == 0)
                                {
                                    continue;
                                }

                                checkedTexts++;

                                // Drawn at the size asked for at 100% and 125% text, and never smaller than at 100%.
                                string where = name + " (dpi " + dpi + ", text " + scale + "): \"" + drawn.Text + "\"";
                                if (scale <= 1.25 && drawn.FontSize < drawn.RequestedPixels - 0.5f)
                                {
                                    shrunk.Add(where + " drawn at " + drawn.FontSize + " px, asked for " + drawn.RequestedPixels);
                                }

                                var key = (name, dpi, drawn.Text);
                                if (scale == 1.0)
                                {
                                    atOneHundred[key] = drawn.FontSize;
                                }
                                else if (atOneHundred.TryGetValue(key, out float baseline) && drawn.FontSize < baseline - 0.5f)
                                {
                                    shrunk.Add(where + " drawn at " + drawn.FontSize + " px, under " + baseline + " px at 100% text");
                                }

                                using var font = new Font(drawn.FontFamily, drawn.FontSize, drawn.FontStyle, drawn.FontUnit);
                                using var format = new StringFormat(drawn.Flags) { Alignment = drawn.Horizontal, LineAlignment = drawn.Vertical, Trimming = drawn.Trimming };
                                _ = measure.MeasureString(drawn.Text, font, new SizeF(drawn.Bounds.Width, drawn.Bounds.Height), format, out int fitted, out _);
                                if (fitted < drawn.Text.Length)
                                {
                                    cut.Add(name + " (dpi " + dpi + ", text " + scale + "): \"" + drawn.Text + "\" " + fitted + "/" + drawn.Text.Length + " in " + drawn.Bounds.Width + "x" + drawn.Bounds.Height);
                                }
                            }
                        }
                        finally
                        {
                            CardPaint.DrawnTextLog = null;
                        }
                    }
                }
            }

            Assert.IsGreaterThan(500, checkedTexts, "Sanity: the card recorded the text it drew.");
            string[] smaller = shrunk.Distinct().ToArray();
            Assert.IsEmpty(smaller, smaller.Length + " texts are drawn smaller than they should be:\n" + string.Join("\n", smaller.Take(60)));
            string[] distinct = cut.Distinct().ToArray();
            Assert.IsEmpty(distinct, distinct.Length + " texts are cut short:\n" + string.Join("\n", distinct.Take(2000)));
        });
    }
}
