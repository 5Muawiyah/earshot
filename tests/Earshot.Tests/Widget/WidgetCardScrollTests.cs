using System.Drawing;
using System.Windows.Forms;
using Earshot.Popup;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A page of the card that is taller than the space above the taskbar: the card is capped to that space and keeps its
// bottom edge, and the rows scroll under a header that stays. Every test opens a real card on a private desktop over a
// fixed work area, so the answer does not depend on the screen the tests run on.
[TestClass]
public sealed class WidgetCardScrollTests
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int MK_LBUTTON = 0x0001;

    private const int Gap = 12;

    // A screen with room for the whole settings page at 100%, and one that has none: 1280 by 600 less nothing, as a small
    // laptop screen or a hosted runner's.
    private static readonly Rectangle LargeWorkArea = new(0, 0, 1920, 1040);
    private static readonly Rectangle SmallWorkArea = new(0, 0, 1280, 600);

    // Little enough that the page is more than a page and a bit long.
    private static readonly Rectangle TinyWorkArea = new(0, 0, 1280, 400);

    // The work area of a 1024 by 768 screen with a taskbar, the smallest the tests run on (a hosted runner's). A test that gives a
    // card more room than this is only as good as the screen it runs on.
    private static readonly Rectangle RunnerWorkArea = new(0, 0, 1024, 728);

    // ---- The cap and the bottom edge

    [TestMethod]
    public void ASettingsPageTallerThanTheSpaceAboveTheTaskbarIsCappedToItAndKeepsItsBottomEdge()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            int bottom = page.Card.Bounds.Bottom;

            page.OpenSettings();

            Assert.AreEqual(WidgetCardView.Settings, page.Card.EffectiveView);
            Assert.AreEqual(bottom, page.Card.Bounds.Bottom, "It grows upward from the gauge, not down into the taskbar.");
            Assert.AreEqual(SmallWorkArea.Height - (2 * Gap), page.Card.Height, "The space above the taskbar, less the margin above and below.");
            Assert.IsGreaterThanOrEqualTo(SmallWorkArea.Top + Gap, page.Card.Bounds.Top, "A margin at the top of the work area.");
            Assert.IsLessThan(page.Card.CurrentSettingsLayout!.Frame.Height, page.Card.Height, "The page is taller than the card.");
        });
    }

    // The cap a page is given when it replaces the one on the card is worked out again from where the card is then, not carried over
    // from when it was shown: the card opened with room for the whole page, and by the time the page opens the work area has
    // changed (a display rearranged, the taskbar moved) and has less above the card.
    [TestMethod]
    public void APageThatReplacesTheOneOnTheCardIsCappedToTheWorkAreaAsItIsThenNotAsItWasWhenTheCardOpened()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(LargeWorkArea);
            int bottom = page.Card.Bounds.Bottom;
            var lower = new Rectangle(LargeWorkArea.X, 360, LargeWorkArea.Width, LargeWorkArea.Height - 360);
            page.WorkArea.Value = lower;

            page.OpenSettings(capped: false);

            Assert.AreEqual(bottom, page.Card.Bounds.Bottom, "It keeps its bottom edge.");
            Assert.AreEqual(bottom - (lower.Top + Gap), page.Card.Height, "Capped to the room above it in the work area as it is now.");
            Assert.IsGreaterThanOrEqualTo(lower.Top + Gap, page.Card.Bounds.Top, "A margin at the top of the work area as it is now.");
            Assert.IsLessThan(page.Card.CurrentSettingsLayout!.Frame.Height, page.Card.Height, "The page is taller than the card now.");
        });
    }

    [TestMethod]
    public void ASettingsPageThatFitsKeepsItsFullHeightAndItsBottomEdge()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(LargeWorkArea);
            int bottom = page.Card.Bounds.Bottom;
            page.OpenSettings(capped: false);
            int[] before = page.Card.CurrentControls().Select(c => c.Bounds.Y).ToArray();

            Wheel(page.Card, -120 * 10);

            Assert.AreEqual(bottom, page.Card.Bounds.Bottom, "It grows upward from the gauge, not down into the taskbar.");
            Assert.AreEqual(page.Card.CurrentSettingsLayout!.Frame.Height, page.Card.Height, "Nothing is cut when the page has room.");
            CollectionAssert.AreEqual(before, page.Card.CurrentControls().Select(c => c.Bounds.Y).ToArray(), "Nothing scrolls when everything is in view.");
        });
    }

    [TestMethod]
    public void AnOverTallPageIsCappedAtLargerScalesToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            // At 150% the settings page is about 1140 px tall, more than any work area a screen of 1024 by 768 has. The work area is
            // one a real window can have on any screen the tests run on: a card taller than the screen is cut to it by Windows,
            // whatever work area the presenter is told, and the test would then measure the screen and not the cap.
            using ScrollPage page = ScrollPage.Open(RunnerWorkArea, dpi: 144);
            Rectangle workArea = RunnerWorkArea;
            int bottom = page.Card.Bounds.Bottom;

            page.OpenSettings();

            int gap = 18;
            Assert.AreEqual(bottom, page.Card.Bounds.Bottom);
            Assert.AreEqual(workArea.Height - (2 * gap), page.Card.Height);
            Assert.IsGreaterThan(page.Card.Height, page.Card.CurrentSettingsLayout!.Frame.Height, "This is a page that does not fit, or the test proves nothing.");
        });
    }

    // ---- A small screen

    // The card on the work area of the smallest screen the tests run on, at every scale: it stays inside that work area, and every row
    // of the page can be scrolled to and pressed. A page that only worked with room for every row would pass on a large screen
    // and fail here.
    [TestMethod]
    public void OnASmallScreenAtEveryScaleTheCardStaysInsideTheWorkAreaAndEveryRowCanBeReached()
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach (int dpi in CardKit.Scales)
            {
                using ScrollPage page = ScrollPage.Open(RunnerWorkArea, dpi);
                page.Host.View = CardKit.Update(UpdateStage.Available);
                string at = " at " + dpi.ToString(System.Globalization.CultureInfo.InvariantCulture) + " dpi";
                Assert.IsTrue(RunnerWorkArea.Contains(page.Card.Bounds), "The main card is inside the work area" + at);

                page.OpenSettings();

                Assert.IsTrue(RunnerWorkArea.Contains(page.Card.Bounds), "The settings page is inside the work area" + at);
                int bodyTop = page.Card.CurrentSettingsLayout!.Frame.Body.Y;
                foreach (SettingsItem item in page.Card.CurrentSettingsLayout.Items.Where(i => i.Kind == SettingsItemKind.Row))
                {
                    page.Card.ScrollSettingsToForTest(item.Bounds.Top - bodyTop);
                    int offset = page.Card.SettingsScrollOffset;
                    Assert.IsGreaterThanOrEqualTo(bodyTop, item.Bounds.Top - offset, "The row " + item.Row + " starts in view" + at);
                    Assert.IsLessThanOrEqualTo(page.Card.ClientSize.Height, item.Bounds.Bottom - offset, "The row " + item.Row + " ends in view" + at);
                }

                CardKit.ClickPart(page.Card, SettingsRowId.About, SettingsPart.Button);

                Assert.AreEqual(WidgetCardView.Update, page.Presenter.ViewForTest, "The last row can be pressed" + at);
            }
        });
    }

    // ---- The wheel

    [TestMethod]
    public void TheWheelScrollsTheRowsByTheSameAmountAndLeavesTheHeaderWhereItIs()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            Rectangle[] before = page.Card.CurrentControls().Select(c => c.Bounds).ToArray();

            Wheel(page.Card, -120);

            Rectangle[] after = page.Card.CurrentControls().Select(c => c.Bounds).ToArray();
            Assert.AreEqual(before[0], after[0], "The back button is in the header, which does not scroll.");
            int moved = before[1].Y - after[1].Y;
            Assert.IsGreaterThan(0, moved, "One notch down moves the rows up.");
            Assert.IsLessThan(page.Viewport.Height, moved, "A notch is less than a page.");
            for (int i = 1; i < before.Length; i++)
            {
                Assert.AreEqual(moved, before[i].Y - after[i].Y, "Every row moves by the same amount: " + i);
                Assert.AreEqual(before[i].Size, after[i].Size);
            }

            Wheel(page.Card, 120 * 20);
            CollectionAssert.AreEqual(before, page.Card.CurrentControls().Select(c => c.Bounds).ToArray(), "Wheeling back up stops at the top.");
        });
    }

    [TestMethod]
    public void TheWheelStopsWhenTheLastRowIsInViewAndNotBefore()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();

            Wheel(page.Card, -120 * 100);

            CardControl last = page.Card.CurrentControls()[^1];
            Assert.IsLessThanOrEqualTo(page.Card.ClientSize.Height, last.Bounds.Bottom, "The last row is wholly in view.");
            Assert.IsLessThan(CardPlacement.Scale(40, 96), page.Card.ClientSize.Height - last.Bounds.Bottom, "Nothing more than the page's own bottom padding is left under it.");
            Rectangle[] atEnd = page.Card.CurrentControls().Select(c => c.Bounds).ToArray();

            Wheel(page.Card, -120);

            CollectionAssert.AreEqual(atEnd, page.Card.CurrentControls().Select(c => c.Bounds).ToArray(), "Nothing scrolls past the end.");
        });
    }

    // ---- Mouse and header

    [TestMethod]
    public void ARowScrolledIntoViewIsClickedWhereItIsDrawnAndTheOffsetSurvivesTheChange()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            page.OpenMore();
            Wheel(page.Card, -120 * 100);
            CardControl toggle = page.LeftClickToggle();
            Assert.IsGreaterThanOrEqualTo(page.Viewport.Top, toggle.Bounds.Top, "The toggle is in view.");

            CardKit.Click(page.Card, toggle.Bounds);

            CardKit.AssertCalls(page.Host, "leftClick:True");
            Assert.AreEqual(toggle.Bounds, page.LeftClickToggle().Bounds, "Choosing a setting draws the page again where it was, not from the top.");
        });
    }

    // A control that is cut by the bottom edge of the body is pressed where it can be seen, and the page then shows it whole.
    [TestMethod]
    public void AClickOnAControlCutByTheBottomOfThePageScrollsItWhollyIntoViewAndStillPressesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            page.OpenMore();
            Wheel(page.Card, -120 * 100);
            Rectangle toggle = page.LeftClickToggle().Bounds;
            for (int i = 0; i < 400 && !(toggle.Top < page.Viewport.Bottom && toggle.Bottom > page.Viewport.Bottom); i++)
            {
                Wheel(page.Card, 40);
                toggle = page.LeftClickToggle().Bounds;
            }

            Assert.IsTrue(toggle.Top < page.Viewport.Bottom && toggle.Bottom > page.Viewport.Bottom, "Sanity: the toggle is cut by the bottom of the page: " + toggle + " in " + page.Viewport);
            int offset = page.Card.SettingsScrollOffset;

            CardKit.Click(page.Card, Rectangle.Intersect(toggle, page.Viewport));

            CardKit.AssertCalls(page.Host, "leftClick:True");
            Assert.IsGreaterThan(offset, page.Card.SettingsScrollOffset, "The page scrolled down to bring it in.");
            Rectangle after = page.LeftClickToggle().Bounds;
            Assert.IsTrue(after.Top >= page.Viewport.Top && after.Bottom <= page.Viewport.Bottom, "The toggle is wholly in view: " + after + " in " + page.Viewport);
        });
    }

    // The same at the top edge, where the header ends: the part of a control under the header is not there to be pressed, the part
    // below it is. The control is the gauge order's header, which is near the top of the page (the rows below the middle of a page
    // that is a little taller than the card cannot be scrolled up under the header, there being no more page to scroll by).
    [TestMethod]
    public void AClickOnAControlCutByTheTopOfThePageScrollsItWhollyIntoViewAndStillPressesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            Rectangle OnScreen()
            {
                Rectangle onPage = CardKit.Part(page.Card, SettingsRowId.GaugeOrder, SettingsPart.Expand);
                return new Rectangle(onPage.X, onPage.Y - page.Card.SettingsScrollOffset, onPage.Width, onPage.Height);
            }

            Rectangle toggle = OnScreen();
            for (int i = 0; i < 400 && !(toggle.Top < page.Viewport.Top && toggle.Bottom > page.Viewport.Top); i++)
            {
                Wheel(page.Card, -40);
                toggle = OnScreen();
            }

            Assert.IsTrue(toggle.Top < page.Viewport.Top && toggle.Bottom > page.Viewport.Top, "Sanity: the header is cut by the top of the page: " + toggle + " in " + page.Viewport);
            int offset = page.Card.SettingsScrollOffset;

            CardKit.Click(page.Card, Rectangle.Intersect(toggle, page.Viewport));

            CardKit.AssertCalls(page.Host);
            Assert.IsTrue(page.Card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.GaugeOrder && i.Tiles.Count == 6), "The press opened the order's pictures.");
            Assert.IsLessThan(offset, page.Card.SettingsScrollOffset, "The page scrolled up to bring it in.");
            Rectangle after = OnScreen();
            Assert.IsTrue(after.Top >= page.Viewport.Top && after.Bottom <= page.Viewport.Bottom, "The header is wholly in view: " + after + " in " + page.Viewport);
        });
    }

    // The same with More open, which makes the page long enough that a row in the middle of it can be scrolled up under the header:
    // Hand back's switch, cut by the top edge, is pressed where it can be seen and then shown whole.
    [TestMethod]
    public void AClickOnAControlCutByTheTopOfAPageWithMoreOpenScrollsItWhollyIntoViewAndStillPressesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            page.OpenMore();
            Rectangle OnScreen()
            {
                Rectangle onPage = CardKit.Part(page.Card, SettingsRowId.HandBack, SettingsPart.Toggle);
                return new Rectangle(onPage.X, onPage.Y - page.Card.SettingsScrollOffset, onPage.Width, onPage.Height);
            }

            Rectangle toggle = OnScreen();
            for (int i = 0; i < 400 && !(toggle.Top < page.Viewport.Top && toggle.Bottom > page.Viewport.Top); i++)
            {
                Wheel(page.Card, toggle.Top < page.Viewport.Top ? 40 : -40);
                toggle = OnScreen();
            }

            Assert.IsTrue(toggle.Top < page.Viewport.Top && toggle.Bottom > page.Viewport.Top, "Sanity: the switch is cut by the top of the page: " + toggle + " in " + page.Viewport);
            int offset = page.Card.SettingsScrollOffset;
            page.Host.Calls.Clear();

            CardKit.Click(page.Card, Rectangle.Intersect(toggle, page.Viewport));

            CardKit.AssertCalls(page.Host, "handBack:False");
            Assert.IsLessThan(offset, page.Card.SettingsScrollOffset, "The page scrolled up to bring it in.");
            Rectangle after = OnScreen();
            Assert.IsTrue(after.Top >= page.Viewport.Top && after.Bottom <= page.Viewport.Bottom, "The switch is wholly in view: " + after + " in " + page.Viewport);
        });
    }

    [TestMethod]
    public void ARowScrolledUnderTheHeaderIsNotClickedThroughIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            Wheel(page.Card, -120 * 3);

            // The first rows are now drawn above the body, in the header's own band; nothing there is theirs.
            foreach (CardControl control in page.Card.CurrentControls().Skip(1).Where(c => c.Bounds.Bottom <= page.Viewport.Top))
            {
                CardKit.Click(page.Card, control.Bounds);
            }

            Assert.IsEmpty(page.Host.Calls, "A control that has scrolled out of view cannot be pressed where the header is.");
            Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest);
        });
    }

    [TestMethod]
    public void TheBackButtonStaysAndWorksWhileTheRowsAreScrolled()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            Rectangle back = page.Card.CurrentSettingsLayout!.Frame.Back;
            Wheel(page.Card, -120 * 100);
            Assert.AreEqual(back, page.Card.CurrentControls()[0].Bounds);

            CardKit.Click(page.Card, back);

            Assert.AreEqual(WidgetCardView.Main, page.Presenter.ViewForTest, "Back goes back from anywhere on the page.");
        });
    }

    [TestMethod]
    public void TheNextTimeThePageOpensItStartsAtTheTop()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            int top = page.Card.CurrentControls()[1].Bounds.Y;
            Wheel(page.Card, -120 * 100);
            Assert.AreNotEqual(top, page.Card.CurrentControls()[1].Bounds.Y);

            CardKit.Click(page.Card, page.Card.CurrentSettingsLayout!.Frame.Back);
            page.OpenSettings();

            Assert.AreEqual(top, page.Card.CurrentControls()[1].Bounds.Y);
        });
    }

    // ---- The keyboard

    [TestMethod]
    public void TabScrollsEachFocusedControlWhollyIntoViewAllTheWayRoundAndBack()
    {
        AssertTabKeepsTheFocusedControlInView(SmallWorkArea, 96);
    }

    [TestMethod]
    public void TabScrollsEachFocusedControlWhollyIntoViewAtOneAndAHalfTimesTheSize()
    {
        AssertTabKeepsTheFocusedControlInView(new Rectangle(0, 0, 1920, 1020), 144);
    }

    private static void AssertTabKeepsTheFocusedControlInView(Rectangle workArea, int dpi)
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(workArea, dpi);
            page.OpenSettings();
            page.Card.Activate();
            Application.DoEvents();
            int stops = page.Card.CurrentSettingsLayout!.Targets.Count;

            for (int step = 1; step <= stops + 1; step++)
            {
                Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
                SettingsTarget focus = page.Card.SettingsFocusTarget;
                CardControl control = page.Card.CurrentControls().First(c => c.Name == SettingsRows.NameOf(focus.Row, focus.Part, focus.Index));

                if (focus.Part == SettingsPart.Back)
                {
                    Assert.AreEqual(page.Card.CurrentSettingsLayout!.Frame.Back, control.Bounds, "Step " + step + ": the back button does not move.");
                    continue;
                }

                Assert.AreEqual(control.Bounds, Rectangle.Intersect(control.Bounds, page.Viewport), "Step " + step + ": " + control.Name + " is wholly in view, at " + control.Bounds + " in " + page.Viewport + ".");
            }
        });
    }

    [TestMethod]
    public void ShiftTabFromTheBackButtonGoesToTheLastRowAndScrollsItIntoView()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            page.Card.Activate();
            Application.DoEvents();
            Assert.AreEqual(new SettingsTarget(SettingsRowId.None, SettingsPart.Back), page.Card.SettingsFocusTarget);

            page.Card.HandleSettingsKey(Keys.Tab | Keys.Shift);

            SettingsTarget focus = page.Card.SettingsFocusTarget;
            Assert.AreEqual(page.Card.CurrentSettingsLayout!.Targets[^1], focus);
            CardControl control = page.Card.CurrentControls().First(c => c.Name == SettingsRows.NameOf(focus.Row, focus.Part, focus.Index));
            Assert.AreEqual(control.Bounds, Rectangle.Intersect(control.Bounds, page.Viewport), "The last row is in view.");
        });
    }

    // ---- What a screen reader sees

    [TestMethod]
    public void ARowOutOfViewIsOffscreenToAScreenReaderUntilTheFocusScrollsItIn()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            page.Card.Activate();
            Application.DoEvents();
            AccessibleObject card = page.Card.AccessibilityObject;
            int last = card.GetChildCount() - 1;

            Assert.IsFalse(card.GetChild(1)!.State.HasFlag(AccessibleStates.Offscreen), "The first row is in view.");
            Assert.IsTrue(card.GetChild(last)!.State.HasFlag(AccessibleStates.Offscreen), "The last row is below the card.");

            Wheel(page.Card, -120 * 100);

            AccessibleObject lastRow = card.GetChild(last)!;
            Assert.IsFalse(lastRow.State.HasFlag(AccessibleStates.Offscreen), "The last row has scrolled into view.");
            Assert.IsTrue(card.GetChild(1)!.State.HasFlag(AccessibleStates.Offscreen), "The first row has scrolled out of view.");
            Rectangle screen = page.Card.RectangleToScreen(page.Viewport);
            Assert.AreEqual(lastRow.Bounds, Rectangle.Intersect(lastRow.Bounds, screen), "Its bounds are where it is drawn now.");
            Assert.AreEqual(page.Card.RectangleToScreen(page.Card.CurrentControls()[last].Bounds), lastRow.Bounds);
        });
    }

    [TestMethod]
    public void PageUpPageDownHomeAndEndScrollAPageAtATime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(TinyWorkArea);
            page.OpenSettings();
            page.Card.Activate();
            Application.DoEvents();
            int viewport = page.Viewport.Height;
            int max = CardScroll.MaxOffset(page.Card.CurrentSettingsLayout!.Frame.Body.Height, viewport);
            int pageStep = CardScroll.PagePixels(viewport, 96);
            Assert.IsGreaterThan(pageStep, max, "The page is more than a page and a bit long, or the keys prove little.");

            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.PageDown, 0);
            Assert.AreEqual(pageStep, page.Card.SettingsScrollOffset);
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.PageDown, 0);
            Assert.AreEqual(Math.Min(2 * pageStep, max), page.Card.SettingsScrollOffset);
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.End, 0);
            Assert.AreEqual(max, page.Card.SettingsScrollOffset);
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.PageUp, 0);
            Assert.AreEqual(max - pageStep, page.Card.SettingsScrollOffset);
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Home, 0);
            Assert.AreEqual(0, page.Card.SettingsScrollOffset);
            Assert.IsEmpty(page.Host.Calls, "Scrolling keys change nothing.");
        });
    }

    // ---- The indicator

    [TestMethod]
    public void TheWheelMovesTheRowsByWhatWindowsIsSetToScroll()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines == 0)
            {
                Assert.Inconclusive("Windows is set not to scroll with the wheel.");
            }

            int max = CardScroll.MaxOffset(page.Card.CurrentSettingsLayout!.Frame.Body.Height, page.Viewport.Height);
            int expected = Math.Min(max, CardScroll.WheelPixels(-120, lines, 96, page.Viewport.Height));

            Wheel(page.Card, -120);

            Assert.AreEqual(expected, page.Card.SettingsScrollOffset);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TheIndicatorIsPaintedOnlyWhereThePageScrollsAndTheHeaderIsNeverPaintedOver(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard fits = CardKit.NewCard(dark);
            CardKit.RenderSettings(fits, CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            using Bitmap fitting = CardKit.Render(fits);
            Assert.IsFalse(fits.SettingsScrolls);
            Assert.AreEqual(Rectangle.Empty, fits.ScrollIndicatorBounds);
            var strip = new Rectangle(fits.ClientSize.Width - 8, fits.SettingsViewport.Top, 8, fits.SettingsViewport.Height);
            Assert.IsFalse(CardKit.HasInk(fitting, strip, fitting.GetPixel(0, 0)), "Nothing is drawn along the right edge of a page that fits.");

            using WidgetCard card = CardKit.NewCard(dark);
            card.MaxHeight = 500;
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            Assert.AreEqual(500, card.ClientSize.Height);
            Assert.IsTrue(card.SettingsScrolls);
            using Bitmap top = CardKit.Render(card);
            Rectangle barAtTop = card.ScrollIndicatorBounds;
            Assert.IsTrue(CardKit.HasInk(top, barAtTop, top.GetPixel(0, 0)), "The indicator is drawn where the page scrolls.");

            card.HandleSettingsKey(Keys.End);
            using Bitmap end = CardKit.Render(card);
            Assert.IsGreaterThan(barAtTop.Top, card.ScrollIndicatorBounds.Top, "It moves down with the rows.");
            Assert.IsTrue(CardKit.HasInk(end, card.ScrollIndicatorBounds, end.GetPixel(0, 0)));

            // The header is the same scrolled or not: nothing of a row shows under it.
            Rectangle header = card.CurrentSettingsLayout!.Frame.Header;
            for (int y = header.Top; y < header.Bottom; y++)
            {
                for (int x = header.Left; x < header.Right; x++)
                {
                    Assert.AreEqual(top.GetPixel(x, y), end.GetPixel(x, y), "Header pixel " + x + "," + y);
                }
            }

            // And the last toggle is drawn where the scroll put it.
            Rectangle toggle = card.CurrentControls()[^1].Bounds;
            Assert.IsTrue(CardKit.HasInk(end, toggle, end.GetPixel(0, 0)), "The last toggle is painted at " + toggle + ".");
        });
    }

    [TestMethod]
    public void TheBarCanBeDraggedAndAPressBesideItMovesAPage()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            WidgetCard card = page.Card;
            Rectangle bar = card.ScrollIndicatorBounds;
            Assert.IsFalse(bar.IsEmpty);
            int max = CardScroll.MaxOffset(card.CurrentSettingsLayout!.Frame.Body.Height, page.Viewport.Height);
            var grab = new Point(bar.X + (bar.Width / 2), bar.Y + (bar.Height / 2));

            Mouse(card, WM_LBUTTONDOWN, 0, grab);
            Mouse(card, WM_MOUSEMOVE, MK_LBUTTON, new Point(grab.X, grab.Y + 60));
            Assert.IsGreaterThan(0, card.SettingsScrollOffset, "Dragging the bar down moves the rows up.");
            Assert.IsGreaterThan(bar.Top, card.ScrollIndicatorBounds.Top, "The bar followed the pointer.");

            Mouse(card, WM_MOUSEMOVE, MK_LBUTTON, new Point(grab.X, grab.Y + 5000));
            Assert.AreEqual(max, card.SettingsScrollOffset, "Dragged far past the end, it stops at the end.");
            Mouse(card, WM_LBUTTONUP, 0, new Point(grab.X, grab.Y + 5000));

            Mouse(card, WM_MOUSEMOVE, 0, new Point(grab.X, grab.Y - 100));
            Assert.AreEqual(max, card.SettingsScrollOffset, "Once let go the bar does not follow the pointer.");

            // A press on the strip above the bar moves up a page; below it, down a page.
            int pageStep = CardScroll.PagePixels(page.Viewport.Height, 96);
            Rectangle atEnd = card.ScrollIndicatorBounds;
            Mouse(card, WM_LBUTTONDOWN, 0, new Point(atEnd.X + 1, atEnd.Y - 10));
            Mouse(card, WM_LBUTTONUP, 0, new Point(atEnd.X + 1, atEnd.Y - 10));
            Assert.AreEqual(Math.Max(0, max - pageStep), card.SettingsScrollOffset);
            Rectangle now = card.ScrollIndicatorBounds;
            Mouse(card, WM_LBUTTONDOWN, 0, new Point(now.X + 1, now.Bottom + 10));
            Mouse(card, WM_LBUTTONUP, 0, new Point(now.X + 1, now.Bottom + 10));
            Assert.AreEqual(max, card.SettingsScrollOffset);
            Assert.IsEmpty(page.Host.Calls, "Nothing on the page was pressed through the strip.");
        });
    }

    [TestMethod]
    public void TheBarIsWiderWhileThePointerIsNearItAndThinAgainWhenItIsAway()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ScrollPage page = ScrollPage.Open(SmallWorkArea);
            page.OpenSettings();
            WidgetCard card = page.Card;
            int thin = card.ScrollIndicatorBounds.Width;

            Mouse(card, WM_MOUSEMOVE, 0, new Point(card.ClientSize.Width - 5, page.Viewport.Top + 50));
            Assert.AreEqual(CardPlacement.Scale(CardScroll.IndicatorHotWidthAt96, 96), card.ScrollIndicatorBounds.Width);

            Mouse(card, WM_MOUSEMOVE, 0, new Point(100, page.Viewport.Top + 50));
            Assert.AreEqual(thin, card.ScrollIndicatorBounds.Width);
        });
    }

    // ---- Tooltips

    [TestMethod]
    public void ATooltipBelongsToWhereTheControlIsDrawnAndNotToTheHeaderItHasScrolledUnder()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.MaxHeight = 500;
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            int bodyTop = card.SettingsViewport.Top;
            CardControl[] withTips = card.CurrentControls().Skip(1).Where(c => c.Tip is { Length: > 0 }).ToArray();
            int tried = 0;

            foreach (CardControl control in withTips)
            {
                card.ScrollSettingsToForTest(0);
                CardControl atTop = card.CurrentControls().First(c => c.Name == control.Name);
                card.ScrollSettingsToForTest(atTop.Bounds.Top - bodyTop + 4);
                CardControl moved = card.CurrentControls().First(c => c.Name == control.Name);
                if (moved.Bounds.Top >= bodyTop)
                {
                    continue;
                }

                tried++;
                int x = moved.Bounds.X + (moved.Bounds.Width / 2);
                Assert.IsNull(card.TooltipAt(new Point(x, bodyTop - 2)), control.Name + ": the header is not the control's, though the control's top is up there.");
                Assert.AreEqual(control.Tip, card.TooltipAt(new Point(x, bodyTop + 2))?.Text, control.Name + ": the part in view is.");
            }

            Assert.IsGreaterThan(3, tried, "Enough controls were scrolled under the header for this to prove something.");
        });
    }

    // ---- Helpers

    private static void Mouse(WidgetCard card, int message, nint wParam, Point client)
    {
        nint lParam = (nint)(((client.Y & 0xFFFF) << 16) | (client.X & 0xFFFF));
        Phase5.TestWindows.Send(card.Handle, message, wParam, lParam);
    }

    private static void Wheel(WidgetCard card, int delta)
    {
        Point screen = card.PointToScreen(new Point(card.ClientSize.Width / 2, card.ClientSize.Height / 2));
        nint lParam = (nint)(((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF));
        nint wParam = (nint)((uint)(ushort)(short)delta << 16);
        Phase5.TestWindows.Send(card.Handle, WM_MOUSEWHEEL, wParam, lParam);
        Application.DoEvents();
    }

    // A real card over the real presenter, a fake host, and a work area that is given. The gauge sits on the taskbar just
    // under the work area, at the right-hand end, as it does on a bottom taskbar.
    private sealed class ScrollPage : IDisposable
    {
        public required FakeCardHost Host { get; init; }

        public required WidgetCardPresenter Presenter { get; init; }

        public required WidgetCard Card { get; init; }

        // The work area the card is placed in, which a test can change under a card that is open.
        public required System.Runtime.CompilerServices.StrongBox<Rectangle> WorkArea { get; init; }

        // The scrolling body of the page: the card under its header, in client pixels.
        public Rectangle Viewport
        {
            get
            {
                int top = Card.CurrentSettingsLayout!.Frame.Body.Y;
                return new Rectangle(0, top, Card.ClientSize.Width, Card.ClientSize.Height - top);
            }
        }

        public static ScrollPage Open(Rectangle workArea, int dpi = 96)
        {
            var host = new FakeCardHost();
            var log = new CapturingLog();
            WidgetCard? card = null;
            Rectangle gauge = new(workArea.Right - CardPlacement.Scale(200, dpi), workArea.Bottom, CardPlacement.Scale(74, dpi), CardPlacement.Scale(40, dpi));
            WidgetCardPresenterCallbacks callbacks = CardKit.Callbacks() with { Dpi = () => dpi };
            var current = new System.Runtime.CompilerServices.StrongBox<Rectangle>(workArea);
            var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), callbacks, CardKit.Inline, new Streaming.TestTimeProvider(), log, host, workAreaFor: _ => current.Value);
            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();
            return new ScrollPage { Host = host, Presenter = presenter, Card = card!, WorkArea = current };
        }

        // Opens the page with the gear. A test on a work area the page does not fit says so, and the card must then be shorter
        // than the page: what it does next is only worth proving on a card that is capped.
        public void OpenSettings(bool capped = true)
        {
            CardKit.Click(Card, Card.CurrentMainLayout.Gear);
            Assert.AreEqual(WidgetCardView.Settings, Presenter.ViewForTest, "The gear opened the settings page.");
            if (capped)
            {
                Assert.IsLessThan(Card.CurrentSettingsLayout!.Frame.Height, Card.Height, "The page is taller than the card.");
            }
        }

        // Opens More the way a person does: a press on its row, which scrolls to it first on a card shorter than the page.
        public void OpenMore()
        {
            CardKit.ClickPart(Card, SettingsRowId.More, SettingsPart.Expand);
            Assert.IsTrue(Card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.LeftClick), "The press opened More.");
        }

        // The toggle of the last row of More that has one, which the bottom of the page ends near.
        public CardControl LeftClickToggle() => Card.CurrentControls().Single(c => c.Name == "Left click connects");

        public void Dispose() => Presenter.Dispose();
    }
}
