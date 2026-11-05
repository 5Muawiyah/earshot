using System.Drawing;
using System.Windows.Forms;
using Earshot.Tests;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The compact card on the real WidgetCard: what it draws (against the design's tokens, at 100% and 150% display scale and text size,
// in light, dark and high contrast), how it grows and folds with its bottom edge kept, its hold, and its button. The windows these
// tests show are on a private desktop.
[TestClass]
public sealed class WidgetCardCompactTests
{
    private static readonly TimeSpan Six = TimeSpan.FromSeconds(6);

    public static IEnumerable<object[]> ThemesAndScales() => DesignPixels.ThemesAndScales();

    private static CompactContent MovedTo { get; } = new('', InkRole.Primary, "Moved to iPhone", new CompactAction("Take back", '', "Taking back"), Six)
    {
        Kind = UnaskedKind.MovedTo,
    };

    private static CompactContent Low { get; } = new('', InkRole.Caution, "Left 20%", null, Six) { Kind = UnaskedKind.LowFallback };

    private static CompactContent Charged { get; } = new('', InkRole.Accent, "Fully charged", null, Six) { Kind = UnaskedKind.ChargedFallback };

    private static CompactContent Failure { get; } = new(
        '\0', InkRole.Primary,
        "The AirPods did not answer. They may be in the case, out of range or connected to another device. Check them and try again.",
        new CompactAction("Connect in Windows"), TimeSpan.FromSeconds(12));

    private static WidgetCardModel Compact(CompactContent content, CompactActionState state = CompactActionState.Idle) =>
        CardKit.MainModel() with { Form = CardForm.Compact, Compact = content, CompactState = state };

    private static WidgetCardModel Full() => CardKit.MainModel();

    private static void NeedsIconFont()
    {
        if (FluentGlyphs.Family is null)
        {
            Assert.Inconclusive("No icon font on this PC, so no glyph is drawn.");
        }
    }

    // ---- What it draws

    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheCompactCardIsOneRowOfTheTablesHeightOnTheSolidSurface(DesignTheme theme, int dpi, double t)
    {
        CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t, notice: true);
            card.Render(Compact(MovedTo), dpi);
            using Bitmap bitmap = CardKit.Render(card);

            int expected = WidgetCardLayout.ForCompact(dpi, t, 1, hasGlyph: true, WidgetCardLayout.CompactButton.Icon).Height;
            Assert.AreEqual(CardPlacement.Scale(360, dpi), bitmap.Width);
            Assert.AreEqual(expected, bitmap.Height);
            Assert.AreEqual(1, card.CurrentCompactLayout!.Lines);
            if (dpi == 96 && t == 1.0)
            {
                Assert.AreEqual(56, bitmap.Height, "360 x 56 at 100% text");
            }

            if (dpi == 96 && t == 1.5)
            {
                Assert.AreEqual(66, bitmap.Height, "360 x 66 at 150% text");
            }

            DesignTokens tokens = DesignPixels.Tokens(theme);
            DesignPixels.AssertSameColour(tokens.SolidSurface, bitmap.GetPixel(2, 2), "The surface");
            DesignPixels.AssertSameColour(tokens.SolidSurface, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height - 3), "The surface");
        });
    }

    // The glyph's ink is the role's colour: primary, caution or accent; under high contrast all of them the window text.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheGlyphIsDrawnInItsRolesInkAndTheWordsInThePrimaryInk(DesignTheme theme, int dpi, double t)
    {
        NeedsIconFont();
        CardSta.Run(() =>
        {
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var colours = CardColours.For(tokens, DesignPixels.Accent(theme));
            bool hc = theme == DesignTheme.HighContrast;
            foreach ((CompactContent content, Color ink) in new[]
            {
                (MovedTo, colours.Text), (Low, hc ? colours.Text : colours.Caution), (Charged, hc ? colours.Text : colours.Accent),
            })
            {
                using WidgetCard card = DesignPixels.NewCard(theme, t, notice: true);
                card.Render(Compact(content), dpi);
                using Bitmap bitmap = CardKit.Render(card);
                WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;
                Color surface = tokens.SolidSurface;

                Color glyph = DesignPixels.Extreme(bitmap, Rectangle.Inflate(layout.Glyph, 1, 1), surface);
                Color expected = DesignPixels.Over(ink, surface);
                Assert.IsLessThanOrEqualTo(
                    (DesignPixels.Distance(expected, surface) * 45 / 100) + 6, DesignPixels.Distance(glyph, expected),
                    content.Text + ": the glyph is in its role's ink (expected " + expected + ", drawn " + glyph + ")");

                Color words = DesignPixels.Extreme(bitmap, layout.Text, surface);
                Color primary = DesignPixels.Over(colours.Text, surface);
                int allowance = Math.Max(60, (int)(DesignPixels.Distance(primary, surface) * 0.4));
                Assert.IsLessThanOrEqualTo(allowance, DesignPixels.Distance(words, primary), content.Text + ": the words are in the primary ink, drawn " + words);
            }
        });
    }

    [TestMethod]
    public void ACautionGlyphIsNotThePrimaryInkAndAChargedOneIsNotEither()
    {
        NeedsIconFont();
        CardSta.Run(() =>
        {
            DesignTokens tokens = DesignPixels.Tokens(DesignTheme.Light);
            var colours = CardColours.For(tokens, DesignPixels.Accent(DesignTheme.Light));
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            card.Render(Compact(Low), 96);
            using Bitmap low = CardKit.Render(card);
            Color glyph = DesignPixels.Extreme(low, card.CurrentCompactLayout!.Glyph, tokens.SolidSurface);

            Assert.IsLessThan(
                DesignPixels.Distance(glyph, DesignPixels.Over(colours.Text, tokens.SolidSurface)),
                DesignPixels.Distance(glyph, DesignPixels.Over(colours.Caution, tokens.SolidSurface)),
                "Nearer caution than primary: " + glyph);
        });
    }

    [TestMethod]
    public void ARowOfLowOrChargedHasNoButtonAndAMovedToRowHasOne()
    {
        NeedsIconFont();
        CardSta.Run(() =>
        {
            DesignTokens tokens = DesignPixels.Tokens(DesignTheme.Dark);
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Dark, 1.0, notice: true);

            card.Render(Compact(Low), 96);
            Assert.IsTrue(card.CurrentCompactLayout!.Action.IsEmpty);
            using (Bitmap bitmap = CardKit.Render(card))
            {
                Assert.IsFalse(CardKit.HasInk(bitmap, new Rectangle(300, 0, 60, 56), tokens.SolidSurface), "Nothing at the right end");
            }

            card.Render(Compact(MovedTo), 96);
            WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;
            Assert.AreEqual(new Rectangle(360 - 16 - 32, 12, 32, 32), layout.Action, "A 32 x 32 button, 16 from the edge");
            using (Bitmap bitmap = CardKit.Render(card))
            {
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.Action, tokens.SolidSurface), "Its glyph shows");
            }
        });
    }

    [TestMethod]
    public void ALongNoticeWrapsToAtMostThreeLinesAndTheHeightFollows()
    {
        CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            card.Render(Compact(Failure), 96);

            WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;
            Assert.IsGreaterThan(1, layout.Lines, "The reason is longer than one row");
            Assert.IsLessThanOrEqualTo(3, layout.Lines);
            Assert.AreEqual(56 + (20 * (layout.Lines - 1)), layout.Height);
            Assert.AreEqual(layout.Height, card.ClientSize.Height);
            Assert.AreEqual(360 - 16, layout.Action.Right, "The text button is right-aligned");
            Assert.AreEqual(layout.Height - 12, layout.Action.Bottom, "on the last line");
            Assert.IsTrue(layout.Glyph.IsEmpty, "A notice with no glyph draws none");
            Assert.AreEqual(16, layout.Text.X);
        });
    }

    [TestMethod]
    public void AButtonInProgressDrawsAStillNinetyDegreeArcAtTwelveOClockAndNoGlyph()
    {
        CardSta.Run(() =>
        {
            DesignTokens tokens = DesignPixels.Tokens(DesignTheme.Dark);
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Dark, 1.0, notice: true);
            card.Render(Compact(MovedTo, CompactActionState.Progress), 96);
            using Bitmap bitmap = CardKit.Render(card);
            Rectangle button = card.CurrentCompactLayout!.Action;
            int mid = button.Y + (button.Height / 2);

            var top = new Rectangle(button.X, button.Y, button.Width, mid - button.Y - 1);
            var bottom = new Rectangle(button.X, mid + 2, button.Width, button.Bottom - mid - 2);
            Assert.IsTrue(CardKit.HasInk(bitmap, top, tokens.SolidSurface), "The arc is at the top of the ring");
            Assert.IsFalse(CardKit.HasInk(bitmap, bottom, tokens.SolidSurface), "and not at the bottom");
            Color ink = DesignPixels.Extreme(bitmap, top, tokens.SolidSurface);
            var accent = DesignPixels.Accent(DesignTheme.Dark);
            Assert.IsLessThanOrEqualTo(90, DesignPixels.Distance(ink, accent), "in the accent, drawn " + ink);
            Assert.AreEqual(CompactActionState.Progress, card.ActionStateShown);
            Assert.AreEqual(0f, card.RingAngleForTest, "Still: nothing animates without a driver");
        });
    }

    [TestMethod]
    public void ADoneButtonDrawsATickInTheAccent()
    {
        NeedsIconFont();
        CardSta.Run(() =>
        {
            DesignTokens tokens = DesignPixels.Tokens(DesignTheme.Light);
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            card.Render(Compact(MovedTo, CompactActionState.Done), 96);
            using Bitmap bitmap = CardKit.Render(card);
            Rectangle button = card.CurrentCompactLayout!.Action;

            Color tick = DesignPixels.Extreme(bitmap, button, tokens.SolidSurface);
            Assert.IsLessThanOrEqualTo(90, DesignPixels.Distance(tick, DesignPixels.Accent(DesignTheme.Light)), "The tick is in the accent, drawn " + tick);
        });
    }

    // ---- Names

    [TestMethod]
    public void TheWindowIsNamedAfterItsWordsAndTheButtonByItsLabel()
    {
        CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            string before = card.AccessibleName ?? string.Empty;

            card.Render(Compact(MovedTo), 96);

            Assert.AreEqual("Earshot: Moved to iPhone", card.AccessibleName);
            CardControl button = card.CurrentControls().Single(c => c.Role == CardControlRole.PushButton);
            Assert.AreEqual("Take back", button.Name);
            Assert.AreEqual("Take back", button.Tooltip, "An icon-only button has a tooltip, in its own words");
            Assert.IsTrue(button.IconOnly);
            Assert.IsTrue(button.Enabled);
            Assert.AreEqual(button.Tooltip, card.TooltipAt(new Point(button.Bounds.X + 4, button.Bounds.Y + 4))?.Text);
            Assert.IsNull(card.TooltipAt(new Point(20, 4)), "The words have none");

            card.Render(Compact(MovedTo, CompactActionState.Progress), 96);
            CardControl running = card.CurrentControls().Single(c => c.Role == CardControlRole.PushButton);
            Assert.AreEqual("Taking back", running.Name);
            Assert.IsFalse(running.Enabled, "Clicks are ignored while it runs");

            card.Render(Full(), 96);
            Assert.AreEqual(before, card.AccessibleName, "The full card's own name comes back");
        });
    }

    [TestMethod]
    public void ATextActionIsAButtonNamedByItsLabelWithNoTooltip()
    {
        CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            card.Render(Compact(Failure), 96);

            CardControl button = card.CurrentControls().Single(c => c.Role == CardControlRole.PushButton);

            Assert.AreEqual("Connect in Windows", button.Name);
            Assert.IsNull(button.Tooltip, "Its words show, so it has no tooltip");
            Assert.IsFalse(button.IconOnly);
        });
    }

    // ---- The hold, the pointer and the clicks

    private sealed class Steady
    {
        public long Ms { get; set; }

        public long Now() => Ms;
    }

    private static (WidgetCard Card, Steady Time, List<WidgetCardCloseReason> Closed) ShownWithHold(CompactContent content, CompactActionState state = CompactActionState.Idle)
    {
        var card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
        var time = new Steady();
        card.HoldClock = time.Now;
        var closed = new List<WidgetCardCloseReason>();
        card.CloseRequested += (_, reason) => closed.Add(reason);
        card.Render(Compact(content, state), 96);
        card.Show();
        Application.DoEvents();
        return (card, time, closed);
    }

    [TestMethod]
    public void TheRowHoldsSixSecondsAndThenFoldsAway()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Steady time, List<WidgetCardCloseReason> closed) = ShownWithHold(MovedTo);
            using (card)
            {
                Assert.IsTrue(card.HoldActive, "A shown row with a hold is holding (no motion: from the moment it is shown)");
                time.Ms = 5999;
                card.CheckHold();
                Assert.IsEmpty(closed, "Not yet");
                time.Ms = 6000;
                card.CheckHold();
                CollectionAssert.AreEqual(new[] { WidgetCardCloseReason.HoldEnded }, closed);
                Assert.IsFalse(card.HoldActive);
            }
        });
    }

    [TestMethod]
    public void ThePointerOverStopsTheClockAndLeavingGoesOnWithTheTimeThatRemained()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Steady time, List<WidgetCardCloseReason> closed) = ShownWithHold(MovedTo);
            using (card)
            {
                time.Ms = 4000;
                card.NotePointer(true);
                Assert.IsTrue(card.HoldPaused);
                time.Ms = 100_000;
                card.CheckHold();
                Assert.IsEmpty(closed, "Over it the hold never runs out");
                Assert.AreEqual(TimeSpan.FromSeconds(2), card.HoldRemaining);

                card.NotePointer(false);
                time.Ms = 101_999;
                card.CheckHold();
                Assert.IsEmpty(closed);
                time.Ms = 102_000;
                card.CheckHold();
                Assert.HasCount(1, closed, "Two seconds after the pointer left");
            }
        });
    }

    [TestMethod]
    public void ARowReplacedInPlaceStartsItsHoldAgain()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Steady time, List<WidgetCardCloseReason> closed) = ShownWithHold(Charged);
            using (card)
            {
                time.Ms = 5000;
                card.Render(Compact(Low), 96);

                Assert.AreEqual(Six, card.HoldRemaining, "The six seconds start again");
                time.Ms = 10_999;
                card.CheckHold();
                Assert.IsEmpty(closed);
            }
        });
    }

    [TestMethod]
    public void GrowingToTheFullCardLeavesNoTimer()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Steady time, List<WidgetCardCloseReason> closed) = ShownWithHold(MovedTo);
            using (card)
            {
                card.Render(Full(), 96);

                Assert.IsFalse(card.HoldActive);
                time.Ms = 600_000;
                card.CheckHold();
                Assert.IsEmpty(closed, "It closes by its close button, not on a timer");
                Assert.AreEqual(CardForm.Full, card.ShownForm);
            }
        });
    }

    [TestMethod]
    public void ARowThatStaysUpHasNoHold()
    {
        CardDesktop.Run(() =>
        {
            CompactContent stays = MovedTo with { Hold = null };
            (WidgetCard card, Steady time, List<WidgetCardCloseReason> closed) = ShownWithHold(stays);
            using (card)
            {
                Assert.IsFalse(card.HoldActive);
                time.Ms = 600_000;
                card.CheckHold();
                Assert.IsEmpty(closed);
            }
        });
    }

    [TestMethod]
    public void TheTickHoldsEightHundredMillisecondsAndThenTheCardFolds()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Steady time, List<WidgetCardCloseReason> closed) = ShownWithHold(MovedTo, CompactActionState.Progress);
            using (card)
            {
                time.Ms = 3000;
                card.Render(Compact(MovedTo, CompactActionState.Done), 96);

                Assert.AreEqual(TimeSpan.FromMilliseconds(800), card.HoldRemaining);
                time.Ms = 3799;
                card.CheckHold();
                Assert.IsEmpty(closed);
                time.Ms = 3800;
                card.CheckHold();
                CollectionAssert.AreEqual(new[] { WidgetCardCloseReason.HoldEnded }, closed);
            }
        });
    }

    [TestMethod]
    public void AGenuineClickOnTheButtonAsksForItAndAClickOnTheBodyAsksToGrow()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, _, _) = ShownWithHold(MovedTo);
            using (card)
            {
                int action = 0;
                int body = 0;
                card.CompactActionRequested += (_, _) => action++;
                card.CompactBodyClicked += (_, _) => body++;
                WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;

                CardKit.Click(card, layout.Action);
                Assert.AreEqual((1, 0), (action, body), "The button");

                CardKit.Click(card, layout.Text);
                Assert.AreEqual((1, 1), (action, body), "The body");

                // A press that began on the body and was let go on the button is neither.
                Point onBody = new(layout.Text.X + 4, layout.Text.Y + 4);
                Point onButton = new(layout.Action.X + 4, layout.Action.Y + 4);
                TestWindows.Send(card.Handle, TestWindows.WM_LBUTTONDOWN, 0, (nint)(((onBody.Y & 0xFFFF) << 16) | (onBody.X & 0xFFFF)));
                TestWindows.Send(card.Handle, TestWindows.WM_LBUTTONUP, 0, (nint)(((onButton.Y & 0xFFFF) << 16) | (onButton.X & 0xFFFF)));
                Assert.AreEqual((1, 1), (action, body), "A drag from the body onto the button");
            }
        });
    }

    [TestMethod]
    public void TheButtonIgnoresClicksWhileItsWorkRuns()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, _, _) = ShownWithHold(MovedTo, CompactActionState.Progress);
            using (card)
            {
                int action = 0;
                card.CompactActionRequested += (_, _) => action++;

                CardKit.Click(card, card.CurrentCompactLayout!.Action);

                Assert.AreEqual(0, action);
            }
        });
    }

    // ---- Grow and fold, with the bottom edge kept, on the fake display

    private sealed class Driven
    {
        public required WidgetCard Card { get; init; }

        public required FakeVBlankClock Clock { get; init; }

        public required Steady Time { get; init; }

        public required Rectangle Rest { get; init; }

        public void Advance(double ms)
        {
            Clock.RunUntil(Clock.Now + TimeSpan.FromMilliseconds(ms));
            Time.Ms += (long)ms;
        }
    }

    private static Driven Present(bool animations, CompactContent content, WidgetCardModel? model = null)
    {
        var card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
        var clock = new FakeVBlankClock(60);
        var time = new Steady();
        card.HoldClock = time.Now;
        card.AttachMotion(clock, () => animations);
        card.Render(model ?? Compact(content), 96);
        Size size = card.ClientSize;
        var rest = new Rectangle(200, 900 - size.Height, size.Width, size.Height);
        card.PresentAnimated(rest, 40);
        Application.DoEvents();
        var driven = new Driven { Card = card, Clock = clock, Time = time, Rest = rest };
        driven.Advance(300);
        return driven;
    }

    [TestMethod]
    public void TheHoldStartsWhenTheEntranceHasFinishedNotBefore()
    {
        CardDesktop.Run(() =>
        {
            var card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            using (card)
            {
                var clock = new FakeVBlankClock(60);
                var time = new Steady();
                card.HoldClock = time.Now;
                card.AttachMotion(clock, () => true);
                card.Render(Compact(MovedTo), 96);
                card.PresentAnimated(new Rectangle(200, 844, 360, 56), 40);
                Application.DoEvents();

                clock.RunUntil(TimeSpan.FromMilliseconds(100));
                time.Ms = 100;
                Assert.IsFalse(card.HoldActive, "Still coming in at 100 ms");

                time.Ms = 260;
                clock.RunUntil(TimeSpan.FromMilliseconds(300));
                Assert.IsTrue(card.HoldActive, "The entrance ends at 250 ms");
                Assert.AreEqual(Six, card.HoldRemaining, "Six seconds counted from when it finished coming in");
            }
        });
    }

    [TestMethod]
    public void GrowingToTheFullCardMovesPointToPointWithTheBottomEdgePinned()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: true, MovedTo);
            using WidgetCard card = d.Card;
            int bottom = d.Rest.Bottom;
            Assert.AreEqual(bottom, card.Bounds.Bottom);

            card.Render(Full(), 96);
            int fullHeight = WidgetCardLayout.Compute(96, showSwitch: false, showGear: true, showRefresh: false).Height;
            Assert.IsTrue(card.IsMoving, "It grows, it does not jump");
            Assert.AreEqual(bottom, card.RestBounds.Bottom, "The bottom edge is where it was");
            Assert.AreEqual(fullHeight, card.RestBounds.Height);
            Assert.IsTrue(card.FormFading, "The old row is being faded out");

            d.Advance(60);
            Assert.AreEqual(bottom, card.Bounds.Bottom, "Pinned part way");
            Assert.IsGreaterThan(56, card.Bounds.Height, "Part way up");
            Assert.IsLessThan(fullHeight, card.Bounds.Height);
            (double oldAlpha, double newAlpha) = card.FormFadeAlphasForTest;
            Assert.IsLessThan(1.0, oldAlpha, "The old row is fading");
            Assert.AreEqual(0.0, newAlpha, "The new content has not begun (it starts at 167 ms)");

            d.Advance(300);
            Assert.AreEqual(bottom, card.Bounds.Bottom);
            Assert.AreEqual(fullHeight, card.Bounds.Height, "Grown");
            Assert.IsFalse(card.IsMoving);
            Assert.IsFalse(card.FormFading, "The crossfade is over and the old picture is let go");
        });
    }

    [TestMethod]
    public void FoldingToTheCompactRowTakesOneHundredAndSixtySevenMillisecondsWithTheBottomPinned()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: true, MovedTo, Full());
            using WidgetCard card = d.Card;
            int bottom = d.Rest.Bottom;
            int fullHeight = card.Bounds.Height;

            card.Render(Compact(MovedTo), 96);
            d.Advance(100);
            Assert.AreEqual(bottom, card.Bounds.Bottom);
            Assert.IsTrue(card.IsMoving, "Still folding at 100 ms");
            Assert.IsLessThan(fullHeight, card.Bounds.Height);

            d.Advance(80);
            Assert.IsFalse(card.IsMoving, "Done by 167 ms");
            Assert.AreEqual(56, card.Bounds.Height);
            Assert.AreEqual(bottom, card.Bounds.Bottom);
        });
    }

    [TestMethod]
    public void WithAnimationEffectsOffEveryChangeLandsOnItsEndFrameInOneStep()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: false, MovedTo);
            using WidgetCard card = d.Card;
            int bottom = d.Rest.Bottom;

            card.Render(Full(), 96);
            int fullHeight = WidgetCardLayout.Compute(96, showSwitch: false, showGear: true, showRefresh: false).Height;
            Assert.IsFalse(card.IsMoving);
            Assert.IsFalse(card.FormFading);
            Assert.AreEqual(fullHeight, card.ClientSize.Height, "At its full height at once");

            card.Render(Compact(MovedTo, CompactActionState.Progress), 96);
            Assert.AreEqual(56, card.ClientSize.Height, "and back to the row at once");
            Assert.IsFalse(card.FormFading);
            Assert.AreEqual(1.0, card.ActionFadeProgress, "The button's ring is in place, not fading in");
            Assert.AreEqual(0f, card.RingAngleForTest, "and still, at 12 o'clock");
            Assert.AreEqual(0, d.Clock.Subscriptions, "Nothing asks for a frame");
            Assert.AreEqual(bottom, card.Bounds.Bottom, "Nothing moved the bottom edge away");
        });
    }

    [TestMethod]
    public void ARowReplacedInPlaceCrossfadesInEightyThreeMilliseconds()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: true, Charged);
            using WidgetCard card = d.Card;

            card.Render(Compact(Low), 96);
            Assert.IsTrue(card.FormFading);
            Assert.IsFalse(card.IsMoving, "The same size, so nothing moves");

            d.Advance(40);
            (double oldAlpha, double newAlpha) = card.FormFadeAlphasForTest;
            Assert.IsTrue(oldAlpha is > 0.1 and < 0.9, "Part way across: " + oldAlpha);
            Assert.AreEqual(1.0, oldAlpha + newAlpha, 1e-9, "A straight crossfade");

            d.Advance(60);
            Assert.IsFalse(card.FormFading, "Over by 83 ms");
        });
    }

    [TestMethod]
    public void TheFullCardsButtonCrossfadesToTheRingTurnsOnceASecondAndCrossfadesBack()
    {
        CardDesktop.Run(() =>
        {
            WidgetCardModel idle = Full();
            Driven d = Present(animations: true, Charged, idle);
            using WidgetCard card = d.Card;

            card.Render(idle with { Work = CardButtonWork.Connecting }, 96);
            Assert.AreEqual(CardButtonWork.Connecting, card.WorkShown);
            Assert.AreEqual(0.0, card.WorkFadeProgress, "The label starts to give way.");
            d.Advance(40);
            Assert.IsTrue(card.WorkFadeProgress is > 0.1 and < 0.9, "Part way across: " + card.WorkFadeProgress);
            d.Advance(60);
            Assert.AreEqual(1.0, card.WorkFadeProgress, "Over by 83 ms.");

            d.Advance(400);
            Assert.IsTrue(card.WorkRingAngleForTest is > 170f and < 190f, "Half a turn 500 ms after it began: " + card.WorkRingAngleForTest);
            d.Advance(500);
            Assert.IsTrue(card.WorkRingAngleForTest is < 20f or > 340f, "A whole turn after a second: " + card.WorkRingAngleForTest);

            card.Render(idle, 96);
            Assert.AreEqual(CardButtonWork.None, card.WorkShown);
            Assert.AreEqual(0.0, card.WorkFadeProgress, "The ring gives way to the label.");
            d.Advance(100);
            Assert.AreEqual(1.0, card.WorkFadeProgress);
            Assert.AreEqual(0f, card.WorkRingAngleForTest, "The ring stopped.");
        });
    }

    [TestMethod]
    public void TheRingTurnsOnceEverySecondWhileItsWorkRunsAndStopsWhenItEnds()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: true, MovedTo);
            using WidgetCard card = d.Card;

            card.Render(Compact(MovedTo, CompactActionState.Progress), 96);
            d.Advance(500);
            Assert.IsTrue(card.RingAngleForTest is > 170f and < 190f, "Half a turn after 500 ms: " + card.RingAngleForTest);
            d.Advance(500);
            Assert.IsTrue(card.RingAngleForTest is < 20f or > 340f, "A whole turn after a second: " + card.RingAngleForTest);

            card.Render(Compact(MovedTo, CompactActionState.Done), 96);
            d.Advance(200);
            Assert.AreEqual(0f, card.RingAngleForTest, "Stopped");
            Assert.AreEqual(CompactActionState.Done, card.ActionStateShown);
        });
    }

    [TestMethod]
    public void AFoldThatIsCancelledComesBackInFromWhereItIs()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: true, MovedTo);
            using WidgetCard card = d.Card;

            card.HideAnimated();
            d.Advance(60);
            Assert.IsTrue(card.IsExiting, "Folding away");

            Assert.IsTrue(card.CancelFold());
            Assert.IsFalse(card.IsExiting);
            d.Advance(400);
            Assert.IsTrue(card.Visible, "It did not hide");
            Assert.AreEqual(d.Rest.Y, card.Bounds.Y, "and it is back at rest");
        });
    }

    [TestMethod]
    public void AFoldCannotBeCancelledWhenNothingIsFolding()
    {
        CardDesktop.Run(() =>
        {
            Driven d = Present(animations: true, MovedTo);
            using WidgetCard card = d.Card;

            Assert.IsFalse(card.CancelFold());
        });
    }
}
