using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// WidgetCard: the dedicated three-column card. Pure checks create the card on the STA test thread, never
// shown, and render it from a synthetic snapshot with DrawToBitmap; the real executions are shown only on
// a private desktop (Earshot.Tests.Phase5.CardDesktop.Run), never the input desktop.
[TestClass]
public sealed class WidgetCardTests
{
    private const int WM_KEYDOWN = 0x0100;

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
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "Tab cycles back to the button.");
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

    private static void SendKey(nint handle, Keys key) => Phase5.TestWindows.Send(handle, WM_KEYDOWN, (nint)key, 0);

    private static Bitmap Render(WidgetCard card)
    {
        Size size = card.ClientSize;
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
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
        string otherDeviceLabel = "") =>
        new(snapshot, autoPauseOn, showSwitch, connectIntent, buttonEnabled, otherDeviceLabel, DateTimeOffset.UtcNow);
}
