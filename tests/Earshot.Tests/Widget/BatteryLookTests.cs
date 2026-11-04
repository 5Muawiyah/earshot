using System.Drawing;
using System.Drawing.Imaging;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// How a battery value looks: a live reading in the primary ink and the accent (caution when it is at or under the Low battery
// alerts level), a last reading in tertiary with no bolt and its age in the fewest characters, an unknown part with its mark in
// the disabled ink and no number.
[TestClass]
public sealed class BatteryLookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] AgeWords = ["4m", "2h", "3d"];

    private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

    // ---- The age in the fewest characters

    [TestMethod]
    public void TheShortAgeIsMinutesHoursOrDaysRoundedDownWithAMinimumOfOneMinute()
    {
        (TimeSpan Age, string Text)[] cases =
        [
            (TimeSpan.FromSeconds(31), "1m"),
            (TimeSpan.FromSeconds(59), "1m"),
            (Min(1), "1m"),
            (TimeSpan.FromSeconds(119), "1m"),
            (Min(4) + TimeSpan.FromSeconds(59), "4m"),
            (Min(59) + TimeSpan.FromSeconds(59), "59m"),
            (Min(60), "1h"),
            (Min(179), "2h"),
            (TimeSpan.FromHours(23) + Min(59), "23h"),
            (TimeSpan.FromHours(24), "1d"),
            (TimeSpan.FromDays(3) + TimeSpan.FromHours(23), "3d"),
            (TimeSpan.FromSeconds(-5), "1m"),
        ];
        foreach ((TimeSpan age, string text) in cases)
        {
            Assert.AreEqual(text, WidgetCopy.ShortAge(age), age.ToString());
        }
    }

    [TestMethod]
    public void TheShortAgeIsNeverZeroMinutesOrAWordForNow()
    {
        for (int s = -10; s <= 7200; s += 7)
        {
            string text = WidgetCopy.ShortAge(TimeSpan.FromSeconds(s));
            Assert.AreNotEqual("0m", text);
            StringAssert.Matches(text, new System.Text.RegularExpressions.Regex("^[1-9][0-9]*[mhd]$"), s + " s");
        }
    }

    // ---- The words behind it

    private static ShownPart Live(int percent, bool charging) => new(percent, charging, ReadingKind.Live, Now);

    private static ShownPart Last(int percent, TimeSpan age, bool charging = false) => new(percent, charging, ReadingKind.Last, Now - age);

    [TestMethod]
    public void ALastReadingsTooltipHasTheFullWordsAndNoChargingOrAnAbout()
    {
        Assert.AreEqual("Left 70%, read 4 minutes ago", WidgetCopy.ColumnTip("Left", Last(70, Min(4), charging: true), Now));
        Assert.AreEqual("Left 70%, read 1 minute ago", WidgetCopy.ColumnTip("Left", Last(70, Min(1)), Now));
        Assert.AreEqual("Left 70%, read 1 hour ago", WidgetCopy.ColumnTip("Left", Last(70, Min(60)), Now));
        Assert.AreEqual("Left 70%, read 2 hours ago", WidgetCopy.ColumnTip("Left", Last(70, Min(130)), Now));
        Assert.AreEqual("Left 70%, read 1 day ago", WidgetCopy.ColumnTip("Left", Last(70, TimeSpan.FromHours(25)), Now));
        Assert.AreEqual("Left 70%, read 3 days ago", WidgetCopy.ColumnTip("Left", Last(70, TimeSpan.FromDays(3.5)), Now));
        Assert.AreEqual("Left 70%, charging", WidgetCopy.ColumnTip("Left", Live(70, charging: true), Now), "A live reading still says it is charging.");
        Assert.AreEqual("Left 70%", WidgetCopy.ColumnTip("Left", Live(70, charging: false), Now));
    }

    [TestMethod]
    public void WhatIsSpokenOfALastReadingHasTheSameWordsAndNoCharging()
    {
        Assert.AreEqual("Left 70%, last read 4 minutes ago", WidgetCopy.SpokenReading("Left", Last(70, Min(4), charging: true), Now));
        Assert.AreEqual("Left 70%, charging", WidgetCopy.SpokenReading("Left", Live(70, charging: true), Now));
    }

    [TestMethod]
    public void ACardColumnsLineGivesTheShortAgeForALastReading()
    {
        Assert.AreEqual("70% · 4m", WidgetCopy.PartLine(Last(70, Min(4)), Now));
        Assert.AreEqual("70%", WidgetCopy.PartLine(Live(70, false), Now));
    }

    // ---- The card's pictures

    private static PartReading Reading(int percent, bool charging, TimeSpan age) =>
        new(percent, charging, InEar: null) { ReadAt = Now - age };

    private static WidgetSnapshot Snapshot(PartReading? left = null, PartReading? right = null, PartReading? box = null) =>
        new(AirPodsWhere.ThisPc, left ?? PartReading.Unknown, right ?? PartReading.Unknown, box ?? PartReading.Unknown,
            BatteryReadAt: null, EarReadAt: null, LidOpen: null, WidgetWatcherState.Started, WatcherErrorCode: null, WatcherErrorName: null,
            AutoPauseAvailable: false, WidgetCounters.Empty)
        {
            Selection = BroadcastSelectionState.Linked,
        };

    private static WidgetCardModel Model(WidgetSnapshot snapshot, int? lowBatteryThreshold = null)
    {
        var model = new WidgetCardModel(snapshot, false, false, true, true, "", Now, WidgetCardView.Main, null);
        return lowBatteryThreshold is int level ? model with { LowBatteryThresholdPercent = level } : model;
    }

    private static Bitmap Render(WidgetCard card)
    {
        Size size = card.ClientSize;
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            card.RenderContent(g);
        }

        return bitmap;
    }

    private static bool SamePixels(Bitmap first, Bitmap second, Rectangle rect)
    {
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            for (int x = rect.Left; x < rect.Right; x++)
            {
                if (first.GetPixel(x, y) != second.GetPixel(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Bitmap Draw(WidgetSnapshot snapshot, int? threshold = null)
    {
        using var card = new WidgetCard(new CapturingLog());
        card.SetTheme(Color.Black, highContrast: false);
        card.Render(Model(snapshot, threshold), 96);
        return Render(card);
    }

    [TestMethod]
    public void ALastReadingDrawsNoBoltEvenIfItWasChargingWhenRead()
    {
        Phase5.CardSta.Run(() =>
        {
            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
            TimeSpan stale = Min(4);

            using Bitmap lastCharging = Draw(Snapshot(left: Reading(70, true, stale)));
            using Bitmap lastNot = Draw(Snapshot(left: Reading(70, false, stale)));
            using Bitmap liveCharging = Draw(Snapshot(left: Reading(70, true, TimeSpan.FromSeconds(2))));
            using Bitmap liveNot = Draw(Snapshot(left: Reading(70, false, TimeSpan.FromSeconds(2))));

            Assert.IsTrue(SamePixels(lastCharging, lastNot, layout.Left.BoltSlot), "A last reading shows no bolt: it was charging then, and now is unknown.");
            Assert.IsFalse(SamePixels(liveCharging, liveNot, layout.Left.BoltSlot), "A live charging reading shows the bolt.");
        });
    }

    [TestMethod]
    public void TheAgeLineOnTheCardIsTheShortAge()
    {
        Phase5.CardSta.Run(() =>
        {
            CardPaint.DrawnTextLog = [];
            try
            {
                using Bitmap bitmap = Draw(Snapshot(left: Reading(70, false, Min(4)), right: Reading(60, false, TimeSpan.FromHours(2)), box: Reading(50, false, TimeSpan.FromDays(3))));
                string[] drawn = CardPaint.DrawnTextLog.Select(d => d.Text).ToArray();

                CollectionAssert.IsSubsetOf(AgeWords, drawn);
            }
            finally
            {
                CardPaint.DrawnTextLog = null;
            }
        });
    }

    [TestMethod]
    public void OnlyALiveReadingAtOrUnderTheAlertLevelIsDrawnInCaution()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), lowBatteryThreshold: 30), 96);

            Assert.AreEqual(card.CautionInk, card.ValueInk(Live(30, false)), "At the level: caution.");
            Assert.AreEqual(card.CautionInk, card.ValueInk(Live(10, true)));
            Assert.AreNotEqual(card.CautionInk, card.ValueInk(Live(40, false)), "Over the level: the primary ink.");
            Assert.AreNotEqual(card.CautionInk, card.ValueInk(Last(10, Min(4))), "A last reading is old, not low: tertiary.");
            Assert.AreEqual(card.ValueInk(Last(10, Min(4))), card.ValueInk(Last(90, Min(4))));
            Assert.AreNotEqual(card.ValueInk(Live(40, false)), card.ValueInk(Last(40, Min(4))));
        });
    }

    [TestMethod]
    public void TheLevelIsTheAlertsLevelAndNotFixedAtTwenty()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);

            card.Render(Model(Snapshot()), 96);
            Assert.AreEqual(card.CautionInk, card.ValueInk(Live(WidgetSettings.DefaultLowBatteryThresholdPercent, false)), "The default level is the setting's default.");
            Assert.AreNotEqual(card.CautionInk, card.ValueInk(Live(30, false)));

            card.Render(Model(Snapshot(), lowBatteryThreshold: 50), 96);
            Assert.AreEqual(card.CautionInk, card.ValueInk(Live(50, false)), "A higher level in the settings moves the caution with it.");

            card.Render(Model(Snapshot(), lowBatteryThreshold: 10), 96);
            Assert.AreNotEqual(card.CautionInk, card.ValueInk(Live(20, false)), "A lower level too.");
        });
    }

    [TestMethod]
    public void AnUnknownPartHasItsMarkInTheDisabledInkNoNumberAndTheBarTrackOnly()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            CardPaint.DrawnTextLog = [];
            try
            {
                card.Render(Model(Snapshot(left: Reading(70, false, TimeSpan.FromSeconds(2)))), 96);
                using Bitmap bitmap = Render(card);
                WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
                Color background = bitmap.GetPixel(0, 0);

                Assert.AreEqual(card.DisabledInk, card.MarkInk(ShownPart.None), "An unknown part's mark is in the disabled ink.");
                Assert.AreNotEqual(card.DisabledInk, card.MarkInk(Live(70, false)));
                Assert.AreNotEqual(card.DisabledInk, card.MarkInk(Last(70, Min(4))), "A last reading's mark is not disabled: the part is known.");
                Assert.IsFalse(CardPaint.DrawnTextLog.Any(d => d.Text == WidgetCopy.NoReading || d.Text == "-" || d.Text == "—" || d.Text.EndsWith('%') && d.Bounds.IntersectsWith(layout.Right.Percent)),
                    "No number, no words and no dash in an unknown part's place.");
                bool track = false;
                for (int x = layout.Right.Bar.Left; x < layout.Right.Bar.Right && !track; x++)
                {
                    for (int y = layout.Right.Bar.Top; y < layout.Right.Bar.Bottom; y++)
                    {
                        if (bitmap.GetPixel(x, y) != background)
                        {
                            track = true;
                            break;
                        }
                    }
                }

                Assert.IsTrue(track, "The bar's track is drawn.");
            }
            finally
            {
                CardPaint.DrawnTextLog = null;
            }
        });
    }

    // The presenter hands the card the Low battery alerts level, and the default when it has none.
    [TestMethod]
    public void ThePresenterHandsTheCardTheAlertsLevel()
    {
        WidgetCardPresenterCallbacks Callbacks(Func<int>? level) => new(
            CurrentSnapshot: () => WidgetSnapshot.Empty(WidgetWatcherState.Started), AutoPauseOn: () => false, CurrentIntent: () => null,
            IsBusy: () => false, Dpi: () => 96, Ink: () => Color.Black, HighContrast: () => false, OtherDeviceLabel: () => "",
            RequestToggle: _ => { }, SetAutoPause: (_, _) => { }, LowBatteryThreshold: level);

        Assert.AreEqual(40, WidgetCardPresenter.BuildModel(Callbacks(() => 40), TimeProvider.System).LowBatteryThresholdPercent);
        Assert.AreEqual(WidgetSettings.DefaultLowBatteryThresholdPercent, WidgetCardPresenter.BuildModel(Callbacks(null), TimeProvider.System).LowBatteryThresholdPercent);
    }
}
