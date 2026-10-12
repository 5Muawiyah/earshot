using System.Drawing;
using Earshot.Tests.TestWindow;
using Earshot.Tests.Widget;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tooltips;

// A tooltip names a control that shows no words, in one to three words. Words that already show are not said again, and a
// sentence is never a tooltip. The gauge keeps its figure lines (GaugeWindow, outside the card).
[TestClass]
public sealed class CardTooltipRulesTests
{
    private const int MaxWords = 3;

    private static readonly string[] Allowed = ["GaugeWindow.cs", "WidgetCard.Targets.cs"];

    private static int Words(string text) => text.Split([' ', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    // Every tooltip the card can show on this view: the text under every point of the card, at every scroll position, with
    // the control (if any) the point is on.
    private static List<(string Text, CardControl? Control, string View)> Collect(WidgetCard card, string view)
    {
        var found = new List<(string, CardControl?, string)>();
        int offsets = card.SettingsScrolls ? 8 : 1;
        for (int step = 0; step < offsets; step++)
        {
            if (card.SettingsScrolls)
            {
                card.ScrollSettingsToForTest(step * 150);
            }

            IReadOnlyList<CardControl> controls = card.CurrentControls();
            for (int y = 0; y < card.ClientSize.Height; y += 3)
            {
                for (int x = 0; x < card.ClientSize.Width; x += 3)
                {
                    if (card.TooltipAt(new Point(x, y)) is { } tip)
                    {
                        found.Add((tip.Text, controls.FirstOrDefault(c => c.Bounds.Contains(x, y)), view));
                    }
                }
            }
        }

        return found;
    }

    private static List<(string Text, CardControl? Control, string View)> EveryView()
    {
        var all = new List<(string, CardControl?, string)>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WidgetCardModel plain = CardKit.MainModel();
        WidgetCardModel full = CardKit.MainModel(CardKit.Snapshot(readAt: now - TimeSpan.FromMinutes(2)), updateVersion: "1.4.0") with { Now = now, ShowSwitch = true, AutoPauseOn = true, ShowSendTo = true };
        foreach ((string name, WidgetCardModel model, bool notice) in new (string, WidgetCardModel, bool)[]
        {
            ("main", plain, false),
            ("main full", full, false),
            ("main reading", full with { Refresh = BatteryRefreshView.Started }, false),
            ("main nothing heard", full with { Refresh = new BatteryRefreshView(Reading: false, Outcome: BatteryRefreshOutcome.NothingHeard) }, false),
            ("notice", plain, true),
            ("history", CardKit.HistoryModel(HistoryDays.View(2, now, TimeZoneInfo.Utc, null)), false),
            ("update", CardKit.UpdateModel(CardKit.Update(Earshot.Update.UpdateStage.Available)), false),
        })
        {
            using WidgetCard card = CardKit.NewCard(dark: false, notice);
            card.Render(model, 96);
            all.AddRange(Collect(card, name));
        }

        foreach (bool dark in CardKit.Themes)
        {
            using WidgetCard card = CardKit.NewCard(dark);
            card.MaxHeight = 500;
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), 96, more: true, order: true);
            all.AddRange(Collect(card, "settings"));
        }

        return all;
    }

    [TestMethod]
    public void EveryTooltipIsOneToThreeWordsOnAControlThatShowsNone()
    {
        Phase5.CardSta.Run(() =>
        {
            List<(string Text, CardControl? Control, string View)> tips = EveryView();
            Assert.IsGreaterThan(3, tips.Select(t => t.Text).Distinct().Count(), "The scan reached the icon-only controls.");
            foreach ((string text, CardControl? control, string view) in tips.DistinctBy(t => (t.Text, t.View)))
            {
                Assert.IsLessThanOrEqualTo(MaxWords, Words(text), view + ": \"" + text + "\" is a sentence, not a name.");
                Assert.IsNotNull(control, view + ": \"" + text + "\" is over words that already show (no control is there).");
                Assert.IsTrue(control.IconOnly, view + ": \"" + text + "\" is on " + control.Name + ", which shows words.");
            }
        });
    }

    // The code reaches a tooltip only through the card's table or the gauge: no other ToolTip control in the app or the tester.
    [TestMethod]
    public void NoOtherToolTipControlIsCreated()
    {
        string root = RepositoryLocator.RepositoryRoot();
        string objDir = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        var users = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(objDir, StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bToolTip(Text)?\b"))
            {
                users.Add(Path.GetFileName(file));
            }
        }

        CollectionAssert.AreEquivalent(Allowed, users.ToArray());
    }
}
