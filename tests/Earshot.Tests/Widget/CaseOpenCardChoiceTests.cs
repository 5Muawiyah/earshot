using System.Drawing;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The case-open card's choices as pure rules: which displays it goes on, which a full-screen application keeps it off,
// where it rests on each, its close times, its screen reader announcement and the settings page's row. No window: these
// run anywhere.
[TestClass]
public sealed class CaseOpenCardChoiceTests
{
    private const string IdOne = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
    private const string IdTwo = @"\\?\DISPLAY#BBB0002#5&1a2b3c4d&0&UID104#{monitor-interface}";
    private const string IdThree = @"\\?\DISPLAY#CCC0003#5&1a2b3c4d&0&UID108#{monitor-interface}";

    private static readonly Rectangle Left1080 = new(0, 0, 1920, 1080);
    private static readonly Rectangle Right1080 = new(1920, 0, 1920, 1080);
    private static readonly Rectangle Far1080 = new(3840, 0, 1920, 1080);

    internal static DisplayInfo Display(string id, string device, Rectangle bounds, bool primary, int dpi = 96) =>
        new(id, device, bounds, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - 48), primary, dpi);

    internal static readonly DisplayInfo One = Display(IdOne, @"\\.\DISPLAY1", Left1080, true);
    internal static readonly DisplayInfo Two = Display(IdTwo, @"\\.\DISPLAY2", Right1080, false);
    internal static readonly DisplayInfo Three = Display(IdThree, @"\\.\DISPLAY3", Far1080, false, 144);

    private static readonly DisplayInfo[] All = [One, Two, Three];

    private static readonly int[] CloseChoices = [0, 5, 10, 30, 60];

    private static readonly int[] BoxIndexes = [0, 1, 2];

    private static string[] Ids(IReadOnlyList<DisplayInfo> displays) => displays.Select(d => d.Id).ToArray();

    // ---- Which displays

    [TestMethod]
    public void ByDefaultTheCardGoesWhereTheGaugeIs()
    {
        CollectionAssert.AreEqual(new[] { IdOne }, Ids(CaseOpenCardDisplayChoice.Targets([], GaugeDisplayChoice.MainDisplay, All)), "The gauge on the main display.");
        CollectionAssert.AreEqual(new[] { IdTwo }, Ids(CaseOpenCardDisplayChoice.Targets([], IdTwo, All)), "The gauge set to display 2.");
        CollectionAssert.AreEqual(new[] { IdOne }, Ids(CaseOpenCardDisplayChoice.Targets([], GaugeDisplayChoice.AllDisplays, All)), "The gauge on all displays: the main display.");
        CollectionAssert.AreEqual(new[] { IdOne }, Ids(CaseOpenCardDisplayChoice.Targets([], @"\\?\DISPLAY#GONE", All)), "The gauge's display gone: the main display, as the gauge.");
    }

    [TestMethod]
    public void AllDisplaysIsEveryDisplay() =>
        CollectionAssert.AreEqual(new[] { IdOne, IdTwo, IdThree }, Ids(CaseOpenCardDisplayChoice.Targets([CaseOpenCardDisplayChoice.All], "", All)));

    [TestMethod]
    public void ASetIsTheChosenDisplaysThatAreConnected()
    {
        CollectionAssert.AreEqual(new[] { IdTwo, IdThree }, Ids(CaseOpenCardDisplayChoice.Targets([IdThree, IdTwo], "", All)), "In the order Windows lists them.");
        CollectionAssert.AreEqual(new[] { IdThree }, Ids(CaseOpenCardDisplayChoice.Targets([IdThree.ToUpperInvariant(), "gone"], "", All)), "Matched without case; a gone one left out.");
        CollectionAssert.AreEqual(new[] { IdTwo }, Ids(CaseOpenCardDisplayChoice.Targets(["gone"], IdTwo, All)), "None connected: where the gauge is, not nowhere.");
        Assert.IsEmpty(CaseOpenCardDisplayChoice.Targets([], "", []), "No displays read: none.");
    }

    [TestMethod]
    public void TickingAndClearingBoxesMakeAndUndoASet()
    {
        string[] two = CaseOpenCardDisplayChoice.WithDisplay([], IdTwo, on: true, "", All);
        CollectionAssert.AreEqual(new[] { IdOne, IdTwo }, two, "Ticking display 2 keeps the gauge's display 1 that the card was on.");

        string[] justTwo = CaseOpenCardDisplayChoice.WithDisplay(two, IdOne, on: false, "", All);
        CollectionAssert.AreEqual(new[] { IdTwo }, justTwo);

        Assert.IsEmpty(CaseOpenCardDisplayChoice.WithDisplay(justTwo, IdTwo, on: false, "", All), "Clearing the last box goes back to where the gauge is.");

        string[] fromAll = CaseOpenCardDisplayChoice.WithDisplay([CaseOpenCardDisplayChoice.All], IdThree, on: false, "", All);
        CollectionAssert.AreEqual(new[] { IdOne, IdTwo }, fromAll, "Clearing one box of all displays leaves the others.");
    }

    [TestMethod]
    public void TheDisplaysButtonStepsBetweenWhereTheGaugeIsAndAllDisplays()
    {
        CollectionAssert.AreEqual(new[] { CaseOpenCardDisplayChoice.All }, CaseOpenCardDisplayChoice.Next([]));
        Assert.IsEmpty(CaseOpenCardDisplayChoice.Next([CaseOpenCardDisplayChoice.All]));
        Assert.IsEmpty(CaseOpenCardDisplayChoice.Next([IdTwo]), "A set steps back to where the gauge is.");
        Assert.AreEqual("Where the gauge is", CaseOpenCardDisplayChoice.Label([]));
        Assert.AreEqual("All displays", CaseOpenCardDisplayChoice.Label([CaseOpenCardDisplayChoice.All]));
        Assert.AreEqual("Chosen displays", CaseOpenCardDisplayChoice.Label([IdTwo]));
    }

    // ---- Full screen: the gauge's own rule, display by display

    private static ForegroundWindowReading Game(Rectangle monitor) =>
        new(new WindowIdentity("GameWindowClass", false), monitor, monitor, "Display");

    [TestMethod]
    public void AFullScreenApplicationKeepsTheCardOffItsOwnDisplayOnly()
    {
        ForegroundWindowReading game = Game(Right1080);
        foreach (int state in new[] { Shell.QUNS_BUSY, Shell.QUNS_RUNNING_D3D_FULL_SCREEN })
        {
            Assert.IsFalse(CaseOpenCardFullScreen.Covers(state, 3, game, One), "Display 1 has no full-screen application.");
            Assert.IsTrue(CaseOpenCardFullScreen.Covers(state, 3, game, Two), "Display 2 has it.");
            Assert.IsFalse(CaseOpenCardFullScreen.Covers(state, 3, game, Three));
        }
    }

    [TestMethod]
    public void AWindowThatDoesNotCoverItsDisplayIsNotFullScreen()
    {
        var maximised = new ForegroundWindowReading(new WindowIdentity("Chrome_WidgetWin_1", false), new Rectangle(1920, 0, 1920, 1032), Right1080, "Display 2");
        Assert.IsFalse(CaseOpenCardFullScreen.Covers(Shell.QUNS_BUSY, 3, maximised, Two));
    }

    [TestMethod]
    public void OneDisplayOrAnUnreadForegroundCountsAsCovered()
    {
        Assert.IsTrue(CaseOpenCardFullScreen.Covers(Shell.QUNS_BUSY, 1, null, One), "With one display the state alone says it.");
        Assert.IsTrue(CaseOpenCardFullScreen.Covers(Shell.QUNS_BUSY, 3, null, Two), "With several and no foreground read, every display, as the gauge.");
    }

    [TestMethod]
    public void NoFullScreenStateLeavesEveryDisplayFree()
    {
        foreach (int state in new[] { Shell.QUNS_ACCEPTS_NOTIFICATIONS, Shell.QUNS_APP })
        {
            Assert.IsTrue(CaseOpenCardFullScreen.AllowsAnyDisplay(state));
            Assert.IsFalse(CaseOpenCardFullScreen.Covers(state, 3, Game(Right1080), Two), "A full-screen-sized window without the state is not a full-screen application.");
        }
    }

    [TestMethod]
    public void PresentationQuietTimeAndNotPresentKeepItOffEveryDisplay()
    {
        foreach (int state in new[] { Shell.QUNS_PRESENTATION_MODE, Shell.QUNS_QUIET_TIME, Shell.QUNS_NOT_PRESENT })
        {
            Assert.IsFalse(CaseOpenCardFullScreen.AllowsAnyDisplay(state));
            Assert.IsTrue(CaseOpenCardFullScreen.Covers(state, 3, null, One));
        }
    }

    // ---- Where it rests

    [TestMethod]
    public void OnAGaugesDisplayItSitsAboveTheGaugeAndElsewhereInTheCornerByTheTaskbar()
    {
        var size = new Size(360, 200);
        var gauge = new Rectangle(1920 + 1500, 1080 - 44, 74, 40);

        Rectangle above = CaseOpenCardPlacement.On(Two, size, gauge, GaugePosition.RightEnd);
        Assert.IsTrue(above.Bottom <= gauge.Top, "Above the gauge.");
        Assert.IsTrue(Two.WorkArea.Contains(above));

        Rectangle corner = CaseOpenCardPlacement.On(One, size, gauge: null, GaugePosition.RightEnd);
        Assert.IsTrue(One.WorkArea.Contains(corner));
        Assert.IsGreaterThan(One.WorkArea.Width / 2, corner.X, "The bottom-right corner, by the taskbar's end.");
        Assert.IsGreaterThan(One.WorkArea.Height / 2, corner.Y);

        Rectangle notThisGauge = CaseOpenCardPlacement.On(One, size, gauge, GaugePosition.RightEnd);
        Assert.AreEqual(corner, notThisGauge, "A gauge on another display is not this display's.");
    }

    [TestMethod]
    public void ATaskbarAtTheTopPutsTheCardInTheTopCorner()
    {
        DisplayInfo top = new(IdOne, @"\\.\DISPLAY1", Left1080, new Rectangle(0, 48, 1920, 1032), true, 96);
        Assert.AreEqual(TaskbarEdge.Top, CaseOpenCardPlacement.EdgeOf(top));
        Rectangle rest = CaseOpenCardPlacement.On(top, new Size(360, 200), null, GaugePosition.RightEnd);
        Assert.IsLessThan(200, rest.Y, "At the top.");
    }

    // ---- Close times

    [TestMethod]
    public void TheCloseChoicesAreUntilTheCaseClosesAndFiveTenThirtyAndSixtySeconds()
    {
        CollectionAssert.AreEqual(CloseChoices, CaseOpenCardClose.Choices.ToArray());
        Assert.IsNull(CaseOpenCardClose.After(0));
        Assert.AreEqual(TimeSpan.FromSeconds(30), CaseOpenCardClose.After(30));
        Assert.IsNull(CaseOpenCardClose.After(7), "Not a choice: until the case closes.");
        Assert.AreEqual(5, CaseOpenCardClose.Next(0));
        Assert.AreEqual(0, CaseOpenCardClose.Next(60), "Wraps round.");
        Assert.AreEqual("Until the case closes", CaseOpenCardClose.Label(0));
        Assert.AreEqual("10 s", CaseOpenCardClose.Label(10));
    }

    // ---- What a screen reader is told

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TheAnnouncementSaysEachPartAndMarksAnythingNotLive()
    {
        var live = new ShownBattery(
            new ShownPart(70, false, ReadingKind.Live, Now), new ShownPart(70, false, ReadingKind.Live, Now), new ShownPart(50, true, ReadingKind.Live, Now),
            null, null, null);
        Assert.AreEqual("AirPods case open. Left 70%, Right 70%, Case 50%.", WidgetCopy.CaseOpenAnnouncement(live, Now));

        var mixed = live with
        {
            Right = ShownPart.None,
            Case = new ShownPart(60, true, ReadingKind.Estimated, Now - TimeSpan.FromHours(2)),
        };
        string text = WidgetCopy.CaseOpenAnnouncement(mixed, Now);
        StringAssert.Contains(text, "Right no reading");
        StringAssert.Contains(text, "Case ≈60% (estimated, read 2 h ago)", "An estimate keeps its sign and the age of its reading.");
    }

    // ---- The settings page's row

    private sealed class CountingMeasure : ICardTextMeasure
    {
        public int Width(string text, int pixelSize) => text.Length * pixelSize / 2;

        public int Lines(string text, int width, int pixelSize, int lineHeight) => 1;
    }

    private static CardSettingsValues Values(bool expanded, int displays) =>
        FakeCardHost.Defaults() with
        {
            CaseOpenCardExpanded = expanded,
            CaseOpenCardDisplayOptions = All.Take(displays).Select((d, i) => new DisplayOption(d.Id, "Display " + (i + 1))).ToList(),
            CaseOpenCardShownOn = [IdOne],
        };

    [TestMethod]
    public void TheCaseOpenCardRowIsASwitchWithAChevronAndItsChoicesShowOnlyExpanded()
    {
        SettingsLayout closed = SettingsPageLayout.Compute(Values(expanded: false, displays: 3), 96, new CountingMeasure());
        SettingsItem row = closed.Items.Single(i => i.Row == SettingsRowId.CaseCard);
        Assert.AreEqual(FluentGlyphs.Preview, row.Glyph, "The Preview icon, E8A0.");
        Assert.AreEqual("Case-open card", row.Label);
        Assert.IsTrue(closed.Targets.Contains(new SettingsTarget(SettingsRowId.CaseCard, SettingsPart.Toggle)));
        Assert.IsTrue(closed.Targets.Contains(new SettingsTarget(SettingsRowId.CaseCard, SettingsPart.Expand)));
        Assert.IsTrue(row.A.Right <= row.B.Left, "The switch, then the chevron.");
        Assert.IsFalse(closed.Items.Any(i => i.Row is SettingsRowId.CaseCardClose or SettingsRowId.CaseCardDisplays or SettingsRowId.CaseCardDisplay));

        SettingsLayout open = SettingsPageLayout.Compute(Values(expanded: true, displays: 3), 96, new CountingMeasure());
        SettingsRowId[] rows = open.Items.Where(i => i.Kind == SettingsItemKind.Row).Select(i => i.Row).ToArray();
        int at = Array.IndexOf(rows, SettingsRowId.CaseCard);
        CollectionAssert.AreEqual(
            new[] { SettingsRowId.CaseCardClose, SettingsRowId.CaseCardDisplays, SettingsRowId.CaseCardDisplay, SettingsRowId.CaseCardDisplay, SettingsRowId.CaseCardDisplay },
            rows.Skip(at + 1).Take(5).ToArray(), "Close, Displays, then a box per display, inside the row's expander.");
        Assert.IsTrue(open.Items.Where(i => i.Row is SettingsRowId.CaseCardClose or SettingsRowId.CaseCardDisplays or SettingsRowId.CaseCardDisplay).All(i => i.Nested && i.Glyph == '\0'));
        CollectionAssert.AreEqual(
            BoxIndexes,
            open.Targets.Where(t => t.Part == SettingsPart.Check).Select(t => t.Index).ToArray(), "Each box is a keyboard stop of its own.");
        Assert.IsFalse(new SettingsTarget(SettingsRowId.CaseCardDisplay, SettingsPart.Check, 0).SameStop(new SettingsTarget(SettingsRowId.CaseCardDisplay, SettingsPart.Check, 1)));
    }

    [TestMethod]
    public void WithOneDisplayThereAreNoBoxes()
    {
        SettingsLayout open = SettingsPageLayout.Compute(Values(expanded: true, displays: 1), 96, new CountingMeasure());
        Assert.IsTrue(open.Items.Any(i => i.Row == SettingsRowId.CaseCardDisplays), "The displays choice stays.");
        Assert.IsFalse(open.Items.Any(i => i.Row == SettingsRowId.CaseCardDisplay));
    }
}
