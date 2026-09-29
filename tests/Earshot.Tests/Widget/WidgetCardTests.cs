using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// WidgetCard: the dedicated three-column card. Pure checks create the card on the STA test thread, never
// shown, and render it from a synthetic snapshot with WidgetCard.RenderContent straight into a bitmap's
// own Graphics, never Control.DrawToBitmap (which was found to make the window briefly visible, and a
// normal-mode card briefly take the foreground, on whatever desktop the calling thread is attached to);
// the real executions are shown only on a private desktop (Earshot.Tests.Phase5.CardDesktop.Run), never
// the input desktop.
[TestClass]
public sealed class WidgetCardTests
{
    private const int WM_KEYDOWN = 0x0100;

    // Control.AccessibleName's own default falls back to Text, which is only ever "Earshot" (TrayStatus.
    // AppName): a screen reader could not tell this card apart from the gauge, the tray icon or any other
    // Earshot window by name alone. The ordinary card and the case-open notice get their own distinct names.
    [TestMethod]
    public void TheOrdinaryCardAndTheNoticeCardHaveTheirOwnDistinctAccessibleNames()
    {
        Phase5.CardSta.Run(() =>
        {
            using var ordinary = new WidgetCard(new CapturingLog());
            using var notice = new WidgetCard(new CapturingLog(), notice: true);

            Assert.AreEqual("Earshot: AirPods", ordinary.AccessibleName);
            Assert.AreEqual("Earshot: " + WidgetCopy.CaseOpen, notice.AccessibleName);
            Assert.AreNotEqual(ordinary.AccessibleName, notice.AccessibleName);
        });
    }

    [TestMethod]
    public void ANullPercentDrawsTheGlyphAndNoReadingNeverABarOrDigits()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(left: new PartReading(null, null, null))), 96);
            using Bitmap bitmap = Render(card);

            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
            Color background = bitmap.GetPixel(0, 0);
            Assert.IsFalse(HasInk(bitmap, layout.Left.Bar, background), "No bar for a null percent.");
            Assert.IsTrue(HasInk(bitmap, layout.Left.Percent, background), "\"No reading\" takes the percent line's own place.");
            Assert.IsTrue(HasInk(bitmap, layout.Left.Glyph, background), "The glyph itself is still drawn.");
        });
    }

    // The head (an ellipse) and the stem (a rounded rectangle) overlap where the stem meets the head:
    // FillMode.Alternate (the GraphicsPath default) XORs that overlap into a hole instead of filling it
    // solid, which FillMode.Winding fixes.
    [TestMethod]
    public void TheBudGlyphHasNoHoleWhereTheHeadAndStemMeet()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            using Bitmap bitmap = Render(card);

            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
            Rectangle bounds = layout.Left.Glyph;
            float headSize = bounds.Width * 0.6f;
            float stemCentreX = bounds.X + (headSize * 0.70f); // BudGlyphPath's own formula, mirror: false
            int x = (int)Math.Round(stemCentreX);
            int y = (int)Math.Round(bounds.Y + (headSize * 0.60f)); // just inside the head, at the stem's own top

            Color background = bitmap.GetPixel(0, 0);
            Color pixel = bitmap.GetPixel(x, y);
            Assert.AreNotEqual(background, pixel, "The head/stem junction must be filled solid, not a hole.");
        });
    }

    // The same light-mode blue the dark theme uses reads as too pale against a light background: light mode
    // gets a darker accent than dark mode, matching the mockup (#005FB8 against #3A96DD).
    [TestMethod]
    public void TheConnectButtonUsesADarkerAccentInLightModeThanInDarkMode()
    {
        Phase5.CardSta.Run(() =>
        {
            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
            // One button-height in from the left edge (past the rounded cap) and clear of the centred
            // "Connect" text, so this samples the fill colour rather than the white text drawn over it.
            Point centre = new(layout.Button.X + layout.Button.Height, layout.Button.Y + (layout.Button.Height / 2));

            using var lightCard = new WidgetCard(new CapturingLog());
            lightCard.SetTheme(Color.Black, highContrast: false); // dark ink => light background
            lightCard.Render(Model(Snapshot(), buttonEnabled: true), 96);
            using Bitmap lightBitmap = Render(lightCard);

            using var darkCard = new WidgetCard(new CapturingLog());
            darkCard.SetTheme(Color.White, highContrast: false); // light ink => dark background
            darkCard.Render(Model(Snapshot(), buttonEnabled: true), 96);
            using Bitmap darkBitmap = Render(darkCard);

            Assert.AreEqual(WidgetCard.AccentLight, lightBitmap.GetPixel(centre.X, centre.Y), "Light mode must use the darker accent.");
            Assert.AreEqual(WidgetCard.AccentDark, darkBitmap.GetPixel(centre.X, centre.Y), "Dark mode must use the lighter accent.");
            Assert.AreNotEqual(WidgetCard.AccentLight, WidgetCard.AccentDark, "Sanity: the two accents must actually differ.");
        });
    }

    // L, R and Case labels above the three columns, matching the mockup: WidgetCardLayoutTests proves the
    // layout puts Label above Glyph; this proves something is actually painted there.
    [TestMethod]
    public void TheThreeColumnsEachShowTheirOwnLabel()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            using Bitmap bitmap = Render(card);

            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
            Color background = bitmap.GetPixel(0, 0);
            Assert.IsTrue(HasInk(bitmap, layout.Left.Label, background), "The L label must be drawn.");
            Assert.IsTrue(HasInk(bitmap, layout.Right.Label, background), "The R label must be drawn.");
            Assert.IsTrue(HasInk(bitmap, layout.Case.Label, background), "The Case label must be drawn.");
        });
    }

    [TestMethod]
    public void APercentDrawsTheBarAndTheDigits()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(left: new PartReading(70, false, false))), 96);
            using Bitmap bitmap = Render(card);

            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
            Color background = bitmap.GetPixel(0, 0);
            Assert.IsTrue(HasInk(bitmap, layout.Left.Bar, background), "The bar is drawn for a known percent.");
            Assert.IsTrue(HasInk(bitmap, layout.Left.Percent, background), "The percent text is drawn.");
        });
    }

    [TestMethod]
    public void AChargingMarkAppearsOnlyWhenCharging()
    {
        Phase5.CardSta.Run(() =>
        {
            using var notCharging = new WidgetCard(new CapturingLog());
            notCharging.SetTheme(Color.Black, highContrast: false);
            notCharging.Render(Model(Snapshot(left: new PartReading(70, false, false))), 96);
            using Bitmap bitmapOff = Render(notCharging);

            using var charging = new WidgetCard(new CapturingLog());
            charging.SetTheme(Color.Black, highContrast: false);
            charging.Render(Model(Snapshot(left: new PartReading(70, true, false))), 96);
            using Bitmap bitmapOn = Render(charging);

            // Both cards draw the same glyph, bar, percent text, where line and read line (only Charging
            // differs), so any extra ink anywhere on the card is the bolt.
            Color background = bitmapOff.GetPixel(0, 0);
            var whole = new Rectangle(0, 0, bitmapOff.Width, bitmapOff.Height);
            int off = CountInk(bitmapOff, whole, background);
            int on = CountInk(bitmapOn, whole, background);
            Assert.IsGreaterThan(off, on, "A bolt is drawn while charging, adding ink the non-charging card does not have.");
        });
    }

    [TestMethod]
    public void AnInEarMarkAppearsOnlyWhenInEar()
    {
        Phase5.CardSta.Run(() =>
        {
            using var outOfEar = new WidgetCard(new CapturingLog());
            outOfEar.SetTheme(Color.Black, highContrast: false);
            outOfEar.Render(Model(Snapshot(left: new PartReading(70, false, false))), 96);
            using Bitmap bitmapOff = Render(outOfEar);

            using var inEar = new WidgetCard(new CapturingLog());
            inEar.SetTheme(Color.Black, highContrast: false);
            inEar.Render(Model(Snapshot(left: new PartReading(70, false, true))), 96);
            using Bitmap bitmapOn = Render(inEar);

            Color background = bitmapOff.GetPixel(0, 0);
            int off = CountInk(bitmapOff, new Rectangle(0, 0, bitmapOff.Width, bitmapOff.Height), background);
            int on = CountInk(bitmapOn, new Rectangle(0, 0, bitmapOn.Width, bitmapOn.Height), background);
            Assert.IsGreaterThan(off, on, "The in-ear mark adds ink somewhere on the card.");
        });
    }

    [TestMethod]
    public void TheSwitchRowIsDrawnOnlyWhenAutoPauseIsAvailable()
    {
        Phase5.CardSta.Run(() =>
        {
            using var without = new WidgetCard(new CapturingLog());
            without.SetTheme(Color.Black, highContrast: false);
            without.Render(Model(Snapshot(autoPauseAvailable: false)), 96);
            using Bitmap bitmapWithout = Render(without);

            using var with = new WidgetCard(new CapturingLog());
            with.SetTheme(Color.Black, highContrast: false);
            with.Render(Model(Snapshot(autoPauseAvailable: true), showSwitch: true), 96);
            using Bitmap bitmapWith = Render(with);

            WidgetCardLayout.Layout layoutWith = WidgetCardLayout.Compute(96, showSwitch: true);
            Assert.IsTrue(HasInk(bitmapWith, layoutWith.Switch, bitmapWith.GetPixel(0, 0)), "The switch row is drawn when available.");
            Assert.IsGreaterThan(bitmapWithout.Height, bitmapWith.Height, "The card is taller with the switch row.");
        });
    }

    // No part has a percent: the grid gives way to the one primary button. It is drawn in the accent colour,
    // the card is shorter without the grid, and no column is drawn.
    [TestMethod]
    public void TheSetupButtonReplacesTheGridWhenNoPartHasAPercent()
    {
        Phase5.CardSta.Run(() =>
        {
            using var with = new WidgetCard(new CapturingLog());
            with.SetTheme(Color.Black, highContrast: false);
            with.Render(Model(Snapshot(), showSetupButton: true), 96);
            using Bitmap bitmapWith = Render(with);

            using var without = new WidgetCard(new CapturingLog());
            without.SetTheme(Color.Black, highContrast: false);
            without.Render(Model(Snapshot(), showSetupButton: false), 96);
            using Bitmap bitmapWithout = Render(without);

            WidgetCardLayout.Layout layout = with.CurrentMainLayout;
            Assert.IsTrue(layout.ShowSetupButton);
            Assert.IsFalse(layout.ShowColumns, "No grid.");
            Point inside = new(layout.SetupButton.X + 6, layout.SetupButton.Y + (layout.SetupButton.Height / 2));
            Assert.AreEqual(WidgetCard.AccentLight, bitmapWith.GetPixel(inside.X, inside.Y), "The button is the accent fill.");
            Assert.IsGreaterThan(bitmapWith.Height, bitmapWithout.Height, "The card is taller with the grid than with the button.");
        });
    }

    [TestMethod]
    public void TheButtonReadsTryAgainAfterAShortFormOnlyRecord()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);

            card.Render(Model(Snapshot(), showSetupButton: true), 96);
            Rectangle plain = card.CurrentMainLayout.SetupButton;
            Assert.AreEqual(Rectangle.Empty, card.CurrentMainLayout.SetupCaption, "No caption before a set-up has failed to read.");

            card.Render(Model(Snapshot() with { SetupCouldNotRead = true }, showSetupButton: true), 96);
            WidgetCardLayout.Layout layout = card.CurrentMainLayout;
            using Bitmap bitmap = Render(card);

            Assert.AreNotEqual(Rectangle.Empty, layout.SetupCaption, "The caption line sits above the button.");
            Assert.IsTrue(HasInk(bitmap, layout.SetupCaption, bitmap.GetPixel(0, 0)), "The caption is drawn.");
            Assert.AreNotEqual(plain.Width, layout.SetupButton.Width, "The button is measured for its own text: Try again is not as wide as Set up battery.");
            Assert.IsTrue(layout.SetupButton.Top > plain.Top, "The caption pushes the button down.");
        });
    }

    // The bars are filled with the accent colour the card is given, not a fixed blue.
    [TestMethod]
    public void TheBatteryBarUsesTheAccentColourTheCardIsGiven()
    {
        Phase5.CardSta.Run(() =>
        {
            var accent = Color.FromArgb(0xC2, 0x39, 0xB3);
            using var card = new WidgetCard(new CapturingLog()) { AccentSource = new FixedAccent(accent) };
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(left: new PartReading(100, false, false))), 96);
            using Bitmap bitmap = Render(card);

            Rectangle bar = card.CurrentMainLayout.Left.Bar;
            Assert.AreEqual(accent, bitmap.GetPixel(bar.X + 2, bar.Y + (bar.Height / 2)), "The filled part of the bar is the accent.");
        });
    }

    private sealed class FixedAccent(Color colour) : ICardAccentSource
    {
        public Color AccentFor(bool lightTheme) => colour;
    }

    [TestMethod]
    public void StylesAreToolWindowAndTopmostWithoutNoActivateInNormalMode()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            nint handle = card.Handle;
            long style = Phase5.TestWindows.ExtendedStyle(handle);
            Assert.AreEqual((long)NativeMethods.WS_EX_TOOLWINDOW, style & NativeMethods.WS_EX_TOOLWINDOW);
            Assert.AreEqual((long)NativeMethods.WS_EX_TOPMOST, style & NativeMethods.WS_EX_TOPMOST);
            Assert.AreEqual(0L, style & NativeMethods.WS_EX_NOACTIVATE, "The normal card takes focus and activation.");
        });
    }

    [TestMethod]
    public void NoticeModeAddsNoActivateAndAnswersMouseActivateWithNoActivate()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog(), notice: true);
            nint handle = card.Handle;
            long style = Phase5.TestWindows.ExtendedStyle(handle);
            Assert.AreEqual((long)NativeMethods.WS_EX_NOACTIVATE, style & NativeMethods.WS_EX_NOACTIVATE);
            Assert.AreEqual((nint)NativeMethods.MA_NOACTIVATE, Phase5.TestWindows.Send(handle, NativeMethods.WM_MOUSEACTIVATE));
        });
    }

    [TestMethod]
    public void NoticeModeAlwaysShowsCaseOpenRegardlessOfTheSnapshotsWhere()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog(), notice: true);
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(where: AirPodsWhere.Elsewhere), otherDeviceLabel: "iPhone"), 96);

            Assert.AreEqual(WidgetCopy.CaseOpen, card.WhereLineText, "Notice mode never shows a live Where reading.");
        });
    }

    [TestMethod]
    public void NormalModeShowsTheLiveWhereReading()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(where: AirPodsWhere.Elsewhere), otherDeviceLabel: "iPhone"), 96);

            Assert.AreEqual(WidgetCopy.Where(AirPodsWhere.Elsewhere, "iPhone"), card.WhereLineText);
        });
    }

    [TestMethod]
    public void ShownWithShowThenActivateBecomesTheForegroundWindow()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            Assert.AreEqual(card.Handle, Phase5.TestWindows.GetActiveWindow(), "Show()+Activate() makes the card the active window.");
        });
    }

    [TestMethod]
    public void NoticeModeNeverBecomesTheForegroundWindow()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var background = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            background.Show();
            background.Activate();
            Application.DoEvents();
            nint before = Phase5.TestWindows.GetActiveWindow();

            using var card = new WidgetCard(new CapturingLog(), notice: true);
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            card.Location = new Point(200, 50);
            card.Show();
            Application.DoEvents();

            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "A notice-mode card must not steal the foreground.");
        });
    }

    [TestMethod]
    public void DeactivatingTheCardClosesItAndRaisesTheDeactivatedReason()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            Assert.IsTrue(card.Visible);

            WidgetCardCloseReason? reason = null;
            card.CloseRequested += (_, r) => reason = r;

            using var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(400, 50), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Application.DoEvents();

            Assert.IsFalse(card.Visible, "Losing activation hides the card.");
            Assert.AreEqual(WidgetCardCloseReason.Deactivated, reason);
        });
    }

    [TestMethod]
    public void TabMovesFocusBetweenTheButtonAndTheSwitchAndWrapsBack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(autoPauseAvailable: true), showSwitch: true, autoPauseOn: false), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "The button has the focus first.");
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Switch, card.FocusTarget);
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Gear, card.FocusTarget, "The gear is last in the order.");
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "Tab cycles back to the button.");
        });
    }

    // The rows draw in the order Button, Switch, SetupButton in the focus order, and Tab must follow it: a
    // reader tabbing through must never get stuck on a row that is not shown.
    [TestMethod]
    public void TabCyclesThroughTheButtonTheSwitchAndTheSetupButtonAndWrapsBack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(autoPauseAvailable: true), showSwitch: true, autoPauseOn: false, showSetupButton: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "The button has the focus first.");
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Switch, card.FocusTarget);
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.SetupButton, card.FocusTarget);
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Gear, card.FocusTarget, "The gear is last in the order.");
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "Tab cycles back to the button.");
        });
    }

    // With no switch row at all, Tab must still reach the set-up button directly from the button.
    [TestMethod]
    public void TabReachesTheSetupButtonDirectlyFromTheButtonWhenTheSwitchIsNotShown()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), showSwitch: false, showSetupButton: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.SetupButton, card.FocusTarget);
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Gear, card.FocusTarget, "The gear is last in the order.");
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "Tab cycles back to the button.");
        });
    }

    [TestMethod]
    public void EnterOnTheFocusedSetupButtonRaisesSetupRequestedAndKeepsTheCardOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), showSetupButton: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.SetupButton, card.FocusTarget);

            int requests = 0;
            card.SetupRequested += (_, _) => requests++;
            SendKey(card.Handle, Keys.Enter);

            Assert.AreEqual(1, requests);
            Assert.IsTrue(card.Visible, "The set-up runs on this card: it stays open.");
        });
    }

    [TestMethod]
    public void AGenuineClickOnTheSetupButtonRaisesSetupRequested()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), showSetupButton: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            int requests = 0;
            card.SetupRequested += (_, _) => requests++;

            Rectangle rect = card.CurrentMainLayout.SetupButton;
            Point centre = new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
            nint lParam = MakeLParam(centre.X, centre.Y);
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);

            Assert.AreEqual(1, requests);
            Assert.AreEqual(WidgetCardFocus.SetupButton, card.FocusTarget, "A click on the set-up button also moves focus to it.");
        });
    }

    // Mirrors ALeftUpWithNoMatchingLeftDownNeverActivatesTheButton: a left up over the set-up button with no
    // preceding left down on it must not activate it either.
    [TestMethod]
    public void ALeftUpWithNoMatchingLeftDownNeverActivatesTheSetupButton()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), showSetupButton: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            int requests = 0;
            card.SetupRequested += (_, _) => requests++;

            Rectangle rect = card.CurrentMainLayout.SetupButton;
            Point centre = new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, MakeLParam(centre.X, centre.Y));

            Assert.AreEqual(0, requests, "A left up with no matching left down must never activate the set-up button.");
        });
    }

    // Render's own defensive reset (mirroring the switch's): a reading arriving between two renders removes
    // this button, so focus already on it must move back to the Connect button.
    [TestMethod]
    public void FocusMovesBackToTheButtonWhenTheSetupButtonDisappearsUnderIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), showSetupButton: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.SetupButton, card.FocusTarget);

            card.Render(Model(Snapshot(left: new PartReading(70, false, false)), showSetupButton: false), 96);

            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget,
                "A reading arriving must not leave focus pointed at a button that no longer draws.");
        });
    }

    [TestMethod]
    public void EnterOnTheFocusedButtonRaisesExactlyOneToggleRequestAndCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), buttonEnabled: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            int toggles = 0;
            card.ToggleRequested += (_, _) => toggles++;
            SendKey(card.Handle, Keys.Enter);

            Assert.AreEqual(1, toggles);
            Assert.IsFalse(card.Visible, "Pressing Connect/Disconnect closes the card.");
        });
    }

    // OnMouseUp used to raise ToggleRequested for any button's up over Connect with no matching press on
    // the card at all - a drag that started elsewhere and released there, or a right or middle click, none
    // of them a genuine left click. This plants each of those shapes and requires none of them to activate
    // the button.
    [TestMethod]
    [DataRow(MouseButtons.Right)]
    [DataRow(MouseButtons.Middle)]
    public void AnUpFromAnyButtonOtherThanLeftNeverActivatesTheButton(MouseButtons button)
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), buttonEnabled: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            int toggles = 0;
            card.ToggleRequested += (_, _) => toggles++;

            Rectangle buttonRect = WidgetCardLayout.Compute(96, showSwitch: false).Button;
            Point centre = new(buttonRect.X + (buttonRect.Width / 2), buttonRect.Y + (buttonRect.Height / 2));
            int message = button switch
            {
                MouseButtons.Right => Phase5.TestWindows.WM_RBUTTONUP,
                MouseButtons.Middle => Phase5.TestWindows.WM_MBUTTONUP,
                _ => throw new ArgumentOutOfRangeException(nameof(button)),
            };
            Phase5.TestWindows.Send(card.Handle, message, 0, MakeLParam(centre.X, centre.Y));

            Assert.AreEqual(0, toggles, button + " up alone must never activate the Connect button.");
        });
    }

    // A left up over the button with no preceding left down on it (a drag that started elsewhere and
    // released over the button) must not activate it either.
    [TestMethod]
    public void ALeftUpWithNoMatchingLeftDownNeverActivatesTheButton()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), buttonEnabled: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            int toggles = 0;
            card.ToggleRequested += (_, _) => toggles++;

            Rectangle buttonRect = WidgetCardLayout.Compute(96, showSwitch: false).Button;
            Point centre = new(buttonRect.X + (buttonRect.Width / 2), buttonRect.Y + (buttonRect.Height / 2));
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, MakeLParam(centre.X, centre.Y));

            Assert.AreEqual(0, toggles, "A left up with no matching left down must never activate the Connect button.");
        });
    }

    [TestMethod]
    public void SpaceOnTheFocusedSwitchRaisesAutoPauseChangedAndDoesNotClose()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(autoPauseAvailable: true), showSwitch: true, autoPauseOn: false), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            SendKey(card.Handle, Keys.Tab);
            Assert.AreEqual(WidgetCardFocus.Switch, card.FocusTarget);

            bool? newValue = null;
            card.AutoPauseChanged += (_, on) => newValue = on;
            SendKey(card.Handle, Keys.Space);

            Assert.AreEqual(true, newValue, "The switch was off, so activating it asks to turn it on.");
            Assert.IsTrue(card.Visible, "The switch never closes the card.");
        });
    }

    [TestMethod]
    public void EscapeClosesTheCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            WidgetCardCloseReason? reason = null;
            card.CloseRequested += (_, r) => reason = r;
            SendKey(card.Handle, Keys.Escape);

            Assert.IsFalse(card.Visible);
            Assert.AreEqual(WidgetCardCloseReason.Escape, reason);
        });
    }

    [TestMethod]
    public void NoticeModeKeyboardDoesNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog(), notice: true);
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot(), buttonEnabled: true), 96);
            card.Location = new Point(50, 50);
            card.Show();
            Application.DoEvents();

            int toggles = 0;
            WidgetCardCloseReason? reason = null;
            card.ToggleRequested += (_, _) => toggles++;
            card.CloseRequested += (_, r) => reason = r;

            SendKey(card.Handle, Keys.Tab);
            SendKey(card.Handle, Keys.Enter);
            SendKey(card.Handle, Keys.Space);
            SendKey(card.Handle, Keys.Escape);

            Assert.AreEqual(0, toggles, "Enter never activates the button in notice mode.");
            Assert.IsNull(reason, "Escape never closes a notice-mode card.");
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "Tab never moves focus in notice mode.");
            Assert.IsTrue(card.Visible);
        });
    }

    [TestMethod]
    public void NoticeModeDismissesOnAClickOutsideBothTheButtonAndTheSwitch()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog(), notice: true);
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            card.Location = new Point(50, 50);
            card.Show();
            Application.DoEvents();

            WidgetCardCloseReason? reason = null;
            card.CloseRequested += (_, r) => reason = r;

            // The top-left corner: inside the card, outside the button (and there is no switch here).
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, MakeLParam(2, 2));

            Assert.IsFalse(card.Visible);
            Assert.AreEqual(WidgetCardCloseReason.ClickOutside, reason);
        });
    }

    [TestMethod]
    public void NormalModeDoesNotDismissOnAClickOutsideBothButtons()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(Model(Snapshot()), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            WidgetCardCloseReason? reason = null;
            card.CloseRequested += (_, r) => reason = r;

            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, MakeLParam(2, 2));

            Assert.IsTrue(card.Visible, "The normal card only closes on deactivation or an explicit action, not an inside-window miss click.");
            Assert.IsNull(reason);
        });
    }

    private static nint MakeLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    [TestMethod]
    public void RoundedCornersSucceedOnThisBuildOrAreSkippedBelowWindows11()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            _ = card.Handle;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                Assert.IsTrue(card.CornersApplied, "DWMWCP_ROUND is expected to succeed on Windows 11.");
            }
            else
            {
                Assert.IsFalse(card.CornersApplied, "Below build 22000 the corner preference is never asked for.");
            }
        });
    }

    // ---- The set-up pages

    private static WidgetCardModel SetupModel(WidgetCardView view, SetupViewModel page) =>
        Model(Snapshot(), showSetupButton: true, view: view, setup: page);

    private static readonly (WidgetCardView View, SetupViewModel Page, string Name)[] SetupPages =
    [
        (WidgetCardView.SetupListening, SetupViewModel.Listening(), "listening"),
        (WidgetCardView.SetupPick, SetupViewModel.Pick(new BatterySetupPicks(80, 60, 90, true, true, false)), "pick"),
        (WidgetCardView.SetupDone, SetupViewModel.Done(BatterySetupResultStatus.BatterySetUp), "done-battery"),
        (WidgetCardView.SetupDone, SetupViewModel.Done(BatterySetupResultStatus.CaseSetUp), "done-case"),
        (WidgetCardView.SetupDone, SetupViewModel.Done(BatterySetupResultStatus.CaseSetUpBudsSame), "done-buds-same"),
        (WidgetCardView.SetupDone, SetupViewModel.Done(BatterySetupResultStatus.SavedNeedsAnother), "done-saved-needs-another"),
        (WidgetCardView.SetupDone, SetupViewModel.Done(BatterySetupResultStatus.NotSaved), "done-not-saved"),
        (WidgetCardView.SetupDone, SetupViewModel.Done(BatterySetupResultStatus.CouldNotRead), "done-could-not-read"),
        (WidgetCardView.SetupFailed, SetupViewModel.Failed(BatterySetupListenStatus.NotFound), "failed-not-found"),
        (WidgetCardView.SetupFailed, SetupViewModel.Failed(BatterySetupListenStatus.Ambiguous), "failed-ambiguous"),
        (WidgetCardView.SetupFailed, SetupViewModel.Failed(BatterySetupListenStatus.WatcherNotStarted), "failed-bluetooth-off"),
    ];

    // Every set-up page is drawn through the card's own paint routine into an off-screen bitmap, at 100% in
    // light and in dark: never a Form shown, never DrawToBitmap. Each has ink in its header, its footer buttons
    // and (for the pick page) every picker; the dark and light renders differ.
    [TestMethod]
    public void EachSetupViewDrawsToABitmap()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach ((WidgetCardView view, SetupViewModel page, string name) in SetupPages)
            {
                Bitmap? light = null;
                foreach ((Color ink, string theme) in new[] { (Color.Black, "light"), (Color.White, "dark") })
                {
                    using var card = new WidgetCard(new CapturingLog());
                    card.SetTheme(ink, highContrast: false);
                    card.Render(SetupModel(view, page), 96);
                    Bitmap bitmap = Render(card);

                    WidgetCardLayout.SetupLayout layout = card.CurrentSetupLayout!;
                    Assert.AreEqual(360, bitmap.Width, name + " " + theme + ": 360 wide at 100%.");
                    Assert.AreEqual(layout.Frame.Height, bitmap.Height, name + " " + theme);
                    Color background = bitmap.GetPixel(0, 0);
                    Assert.IsTrue(HasInk(bitmap, layout.Frame.Back, background), name + " " + theme + ": the back arrow.");
                    Assert.IsTrue(HasInk(bitmap, layout.Frame.Title, background), name + " " + theme + ": the title.");
                    Assert.IsTrue(HasInk(bitmap, layout.Frame.Step, background), name + " " + theme + ": the step counter.");
                    foreach (Rectangle button in layout.Frame.Buttons)
                    {
                        Assert.IsTrue(HasInk(bitmap, button, layout.Frame.Footer.IsEmpty ? background : bitmap.GetPixel(2, layout.Frame.Footer.Top + 4)), name + " " + theme + ": a footer button.");
                    }

                    if (view == WidgetCardView.SetupPick)
                    {
                        foreach (WidgetCardLayout.PickerLayout picker in layout.Pickers)
                        {
                            Color box = bitmap.GetPixel(picker.Box.X + 4, picker.Box.Y + 4);
                            Assert.IsTrue(HasInk(bitmap, picker.Value, box), name + " " + theme + ": a picker value.");
                            Assert.IsTrue(HasInk(bitmap, picker.Up, box) && HasInk(bitmap, picker.Down, box), name + " " + theme + ": both chevrons.");
                            Assert.IsTrue(HasInk(bitmap, picker.Toggle, box), name + " " + theme + ": the Charging toggle.");
                        }
                    }
                    else
                    {
                        Assert.IsTrue(HasInk(bitmap, layout.StatusText, background), name + " " + theme + ": the status line.");
                    }

                    if (light is null)
                    {
                        light = bitmap;
                    }
                    else
                    {
                        Assert.AreNotEqual(light.GetPixel(0, 0), bitmap.GetPixel(0, 0), name + ": dark and light differ.");
                        light.Dispose();
                        bitmap.Dispose();
                    }
                }
            }
        });
    }

    // "Battery set up" is never drawn for a set-up that read nothing: the last page says so plainly.
    [TestMethod]
    public void ANothingProvedOutcomeShowsThePlainWordingNotBatterySetUp()
    {
        SetupViewModel nothing = SetupViewModel.Done(BatterySetupResultStatus.CouldNotRead);
        Assert.AreEqual(WidgetCopy.SetupCouldNotRead, nothing.Status);
        Assert.AreEqual(SetupIcon.Caution, nothing.Icon, "A caution, not a tick.");
        Assert.AreNotEqual(WidgetCopy.SetupBatterySetUp, nothing.Status);
        Assert.AreEqual(WidgetCopy.SetupCapturesKept, nothing.StatusSub);
        foreach (BatterySetupResultStatus status in Enum.GetValues<BatterySetupResultStatus>().Where(s => s != BatterySetupResultStatus.BatterySetUp))
        {
            Assert.AreNotEqual(WidgetCopy.SetupBatterySetUp, SetupViewModel.Done(status).Status, status + " must not claim the battery is set up.");
        }

        Assert.AreEqual(WidgetCopy.SetupBatterySetUp, SetupViewModel.Done(BatterySetupResultStatus.BatterySetUp).Status, "Only a proved bud order says it.");
        Assert.AreEqual(SetupIcon.Check, SetupViewModel.Done(BatterySetupResultStatus.BatterySetUp).Icon);
    }

    [TestMethod]
    public void PickersStepByTenAndClampToZeroAndOneHundred()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupPick, SetupViewModel.Pick(new BatterySetupPicks(50, 100, 0, false, false, false))), 96);
            var seen = new List<BatterySetupPicks>();
            card.SetupPicksChanged += (_, picks) => seen.Add(picks);

            card.HandleSetupKey(Keys.Tab);     // Save -> Back
            card.HandleSetupKey(Keys.Tab);     // Back -> left up
            card.HandleSetupKey(Keys.Space);
            Assert.AreEqual(60, card.Model.Setup!.Picks!.Left, "Up is one step of ten.");
            card.HandleSetupKey(Keys.Tab);     // left down
            card.HandleSetupKey(Keys.Space);
            card.HandleSetupKey(Keys.Space);
            Assert.AreEqual(40, card.Model.Setup.Picks!.Left, "Down is one step of ten.");

            card.HandleSetupKey(Keys.Tab);     // left charging
            card.HandleSetupKey(Keys.Tab);     // right up
            card.HandleSetupKey(Keys.Space);
            Assert.AreEqual(100, card.Model.Setup.Picks!.Right, "Clamped to 100: no change and no event.");
            int changesAtTheTop = seen.Count;
            card.HandleSetupKey(Keys.Space);
            Assert.AreEqual(changesAtTheTop, seen.Count, "A step that changes nothing raises nothing.");

            card.HandleSetupKey(Keys.Tab);     // right down
            card.HandleSetupKey(Keys.Tab);     // right charging
            card.HandleSetupKey(Keys.Tab);     // case up
            card.HandleSetupKey(Keys.Tab);     // case down
            card.HandleSetupKey(Keys.Space);
            Assert.AreEqual(0, card.Model.Setup.Picks!.Case, "Clamped to 0.");

            // The arrow keys step the picker the focus is in.
            card.HandleSetupKey(Keys.Up);
            Assert.AreEqual(10, card.Model.Setup.Picks!.Case);
            card.HandleSetupKey(Keys.Down);
            card.HandleSetupKey(Keys.Down);
            Assert.AreEqual(0, card.Model.Setup.Picks!.Case);
            Assert.AreEqual(seen[^1], card.Model.Setup.Picks, "The last event carries the card's own picks.");
        });
    }

    [TestMethod]
    public void ChargingTogglesFlip()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupPick, SetupViewModel.Pick(BatterySetupPicks.Default)), 96);

            card.HandleSetupKey(Keys.Tab);     // Save -> Back
            card.HandleSetupKey(Keys.Tab);     // left up
            card.HandleSetupKey(Keys.Tab);     // left down
            card.HandleSetupKey(Keys.Tab);     // left charging
            card.HandleSetupKey(Keys.Space);
            Assert.IsTrue(card.Model.Setup!.Picks!.LeftCharging);
            card.HandleSetupKey(Keys.Space);
            Assert.IsFalse(card.Model.Setup.Picks!.LeftCharging, "Space flips it back.");

            card.HandleSetupKey(Keys.Tab);     // right up
            card.HandleSetupKey(Keys.Tab);     // right down
            card.HandleSetupKey(Keys.Tab);     // right charging
            card.HandleSetupKey(Keys.Space);
            card.HandleSetupKey(Keys.Tab);     // case up
            card.HandleSetupKey(Keys.Tab);     // case down
            card.HandleSetupKey(Keys.Tab);     // case charging
            card.HandleSetupKey(Keys.Space);
            BatterySetupPicks picks = card.Model.Setup.Picks!;
            Assert.IsFalse(picks.LeftCharging);
            Assert.IsTrue(picks.RightCharging);
            Assert.IsTrue(picks.CaseCharging);
        });
    }

    [TestMethod]
    public void EscapeIsBackAndEnterIsSaveInPick()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupPick, SetupViewModel.Pick(BatterySetupPicks.Default)), 96);
            var actions = new List<SetupAction>();
            card.SetupActionRequested += (_, action) => actions.Add(action);

            card.HandleSetupKey(Keys.Escape);
            card.HandleSetupKey(Keys.Enter);
            card.HandleSetupKey(Keys.Tab);
            card.HandleSetupKey(Keys.Tab);  // on a picker chevron now
            card.HandleSetupKey(Keys.Enter);

            CollectionAssert.AreEqual(new[] { SetupAction.Back, SetupAction.Save, SetupAction.Save }, actions,
                "Escape is Back; Enter is Save, wherever the focus is.");
        });
    }

    [TestMethod]
    public void TheBackButtonAndTheFooterButtonsRaiseTheirActions()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupFailed, SetupViewModel.Failed(BatterySetupListenStatus.NotFound)), 96);
            var actions = new List<SetupAction>();
            card.SetupActionRequested += (_, action) => actions.Add(action);

            card.HandleSetupKey(Keys.Tab);     // Try again -> Back
            card.HandleSetupKey(Keys.Space);
            card.HandleSetupKey(Keys.Tab);     // Back -> Cancel
            card.HandleSetupKey(Keys.Space);
            card.HandleSetupKey(Keys.Tab);     // Cancel -> Try again
            card.HandleSetupKey(Keys.Space);

            CollectionAssert.AreEqual(new[] { SetupAction.Back, SetupAction.Cancel, SetupAction.TryAgain }, actions);
        });
    }

    // A set-up page keeps the card open when it loses the focus; the ordinary card closes. Real activation, on
    // a private desktop, like the other activation checks.
    [TestMethod]
    public void ASetupViewDoesNotCloseOnDeactivate()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupListening, SetupViewModel.Listening()), 96);
            var reasons = new List<WidgetCardCloseReason>();
            card.CloseRequested += (_, reason) => reasons.Add(reason);
            card.Location = new Point(200, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();

            other.Show();
            other.Activate();
            Application.DoEvents();

            Assert.IsEmpty(reasons, "A set-up page never asks to close because it lost the focus.");
            Assert.IsTrue(card.Visible);

            card.Render(Model(Snapshot(), showSetupButton: true), 96);
            card.Activate();
            Application.DoEvents();
            other.Activate();
            Application.DoEvents();

            CollectionAssert.AreEqual(new[] { WidgetCardCloseReason.Deactivated }, reasons, "The main view still closes on deactivation.");
        });
    }

    [TestMethod]
    public void NoticeModeAlwaysDrawsMain()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog(), notice: true);
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupPick, SetupViewModel.Pick(BatterySetupPicks.Default)), 96);
            using Bitmap bitmap = Render(card);

            Assert.AreEqual(WidgetCardView.Main, card.EffectiveView, "A notice ignores the view.");
            Assert.IsNull(card.CurrentSetupLayout);
            Assert.AreEqual(WidgetCardLayout.WidthAt96, bitmap.Width, "The main card's width, not the set-up page's.");
            Assert.IsFalse(card.CurrentMainLayout.ShowSetupButton, "And never a set-up button.");
        });
    }

    // The set-up page's parts land where the layout says: the spinner arc is drawn while listening, the check
    // while done, the caution when nothing was read.
    [TestMethod]
    public void TheStatusIconIsTheSpinnerTheCheckOrTheCaution()
    {
        Phase5.CardSta.Run(() =>
        {
            var seen = new List<Bitmap>();
            foreach (SetupViewModel page in new[]
            {
                SetupViewModel.Listening(),
                SetupViewModel.Done(BatterySetupResultStatus.BatterySetUp),
                SetupViewModel.Done(BatterySetupResultStatus.CouldNotRead),
            })
            {
                using var card = new WidgetCard(new CapturingLog());
                card.SetTheme(Color.Black, highContrast: false);
                card.Render(SetupModel(page.Icon == SetupIcon.Spinner ? WidgetCardView.SetupListening : WidgetCardView.SetupDone, page), 96);
                Bitmap bitmap = Render(card);
                Rectangle icon = card.CurrentSetupLayout!.StatusIcon;
                Assert.IsTrue(HasInk(bitmap, icon, bitmap.GetPixel(0, 0)), page.Icon + " draws something in the icon slot.");
                seen.Add(bitmap.Clone(icon, PixelFormat.Format32bppArgb));
                bitmap.Dispose();
            }

            Assert.AreNotEqual(CountInk(seen[0], new Rectangle(0, 0, 20, 20), seen[0].GetPixel(0, 0)), CountInk(seen[1], new Rectangle(0, 0, 20, 20), seen[1].GetPixel(0, 0)), "The spinner and the check are different shapes.");
            foreach (Bitmap bitmap in seen)
            {
                bitmap.Dispose();
            }
        });
    }

    // Turning the spinner repaints only its icon, and changes only the icon.
    [TestMethod]
    public void TheSpinnerTurnsWhenItsFrameAdvances()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(SetupModel(WidgetCardView.SetupListening, SetupViewModel.Listening(spinnerFrame: 0)), 96);
            using Bitmap first = Render(card);
            card.SetSpinnerFrame(5);
            using Bitmap second = Render(card);

            Rectangle icon = card.CurrentSetupLayout!.StatusIcon;
            bool iconChanged = false;
            bool restChanged = false;
            for (int y = 0; y < first.Height; y++)
            {
                for (int x = 0; x < first.Width; x++)
                {
                    if (first.GetPixel(x, y) != second.GetPixel(x, y))
                    {
                        if (icon.Contains(x, y))
                        {
                            iconChanged = true;
                        }
                        else
                        {
                            restChanged = true;
                        }
                    }
                }
            }

            Assert.IsTrue(iconChanged, "The arc moved.");
            Assert.IsFalse(restChanged, "Nothing but the icon changed.");
        });
    }

    private static void SendKey(nint handle, Keys key) => Phase5.TestWindows.Send(handle, WM_KEYDOWN, (nint)key, 0);

    // RenderContent, never Control.DrawToBitmap: a top-level, activatable Form like WidgetCard in its
    // normal (non-notice) mode was found to make the window briefly visible, and briefly take the
    // foreground, on whatever desktop the calling thread is attached to when DrawToBitmap does its own
    // internal layout. This card is never shown or activated, never has a handle at all, and never reaches
    // any desktop: RenderContent paints straight into the bitmap's own Graphics.
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

    private static bool HasInk(Bitmap bitmap, Rectangle rect, Color background) => CountInk(bitmap, rect, background) > 0;

    private static int CountInk(Bitmap bitmap, Rectangle rect, Color background)
    {
        Rectangle bounds = Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static WidgetSnapshot Snapshot(
        AirPodsWhere where = AirPodsWhere.ThisPc,
        PartReading? left = null,
        PartReading? right = null,
        PartReading? box = null,
        bool autoPauseAvailable = false,
        DateTimeOffset? readAt = null) =>
        new(
            where,
            left ?? PartReading.Unknown,
            right ?? PartReading.Unknown,
            box ?? PartReading.Unknown,
            BatteryReadAt: readAt,
            EarReadAt: null,
            LidOpen: null,
            WidgetWatcherState.Started,
            WatcherErrorCode: null,
            WatcherErrorName: null,
            ClaimExists: true,
            AutoPauseAvailable: autoPauseAvailable,
            WidgetCounters.Empty);

    private static WidgetCardModel Model(
        WidgetSnapshot snapshot,
        bool showSwitch = false,
        bool autoPauseOn = false,
        bool connectIntent = true,
        bool buttonEnabled = true,
        string otherDeviceLabel = "",
        bool showSetupButton = false,
        WidgetCardView view = WidgetCardView.Main,
        SetupViewModel? setup = null) =>
        new(snapshot, autoPauseOn, showSwitch, connectIntent, buttonEnabled, otherDeviceLabel, DateTimeOffset.UtcNow, showSetupButton, view, setup);
}
