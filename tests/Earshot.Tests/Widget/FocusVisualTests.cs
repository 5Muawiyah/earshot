using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The focus visual: no control draws a dotted focus rectangle, and keyboard focus draws the Windows 11 focus
// visual (a 1 px gap, a 1 px inner stroke, a 2 px outer stroke) while mouse use draws nothing. Everything that
// needs a real window runs on a private desktop, never the owner's.
[TestClass]
public sealed class FocusVisualTests
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_QUERYUISTATE = 0x0129;

    private static readonly int[] Dpis = [96, 120, 144];
    private static readonly Color[] Inks = [Color.White, Color.Black];

    // The card's own model, nothing but the default: the focus tests are about the button, not the readings.
    private static WidgetCardModel Model() => WidgetCardModel.Empty with { ButtonEnabled = true, ConnectIntent = true };

    private static Bitmap RenderWith(WidgetCard card, Color background)
    {
        card.OverrideBackgroundForCaptureOnly = background;
        Size size = card.ClientSize;
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        card.RenderContent(g);
        return bitmap;
    }

    private static WidgetCard ShownCard(int dpi, Color ink)
    {
        var card = new WidgetCard(new CapturingLog());
        card.SetTheme(ink, highContrast: false);
        card.Render(Model(), dpi);
        card.Location = new Point(50, 50);
        card.Show();
        card.Activate();
        Application.DoEvents();
        return card;
    }

    private static WidgetCard UnshownCard(int dpi, Color ink)
    {
        var card = new WidgetCard(new CapturingLog());
        card.SetTheme(ink, highContrast: false);
        card.Render(Model(), dpi);
        return card;
    }

    private static void AssertSameBitmaps(Bitmap expected, Bitmap actual, string message)
    {
        Assert.AreEqual(expected.Size, actual.Size, message);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (expected.GetPixel(x, y) != actual.GetPixel(x, y))
                {
                    Assert.Fail(message + " First difference at (" + x + ", " + y + "): " + expected.GetPixel(x, y) + " against " + actual.GetPixel(x, y) + ".");
                }
            }
        }
    }

    [TestMethod]
    public void ACardOpenedWithTheMouseDrawsNoFocusMarkAnywhere()
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                foreach (Color ink in Inks)
                {
                    Color background = ink == Color.White ? Color.Black : Color.White;
                    using WidgetCard shown = ShownCard(dpi, ink);
                    using WidgetCard resting = UnshownCard(dpi, ink);
                    Assert.IsTrue(shown.ContainsFocus, "The shown card holds the focus, which is what used to draw the dotted box round Connect.");

                    using Bitmap withFocus = RenderWith(shown, background);
                    using Bitmap withoutFocus = RenderWith(resting, background);
                    AssertSameBitmaps(withoutFocus, withFocus, "A mouse-opened card draws exactly what an unfocused one does (dpi " + dpi + ", ink " + ink.Name + ").");
                }
            }
        });
    }

    [TestMethod]
    public void AClickOnTheCardTakesTheFocusMarkAwayAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard shown = ShownCard(96, Color.White);
            using WidgetCard resting = UnshownCard(96, Color.White);
            Phase5.TestWindows.Send(shown.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            Phase5.TestWindows.Send(shown.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            using (Bitmap keyboard = RenderWith(shown, Color.Black))
            using (Bitmap plain = RenderWith(resting, Color.Black))
            {
                Assert.IsTrue(Differs(keyboard, plain), "Tab brings the focus mark.");
            }

            // A press on the title row, away from every control.
            nint point = (nint)((8 << 16) | 8);
            Phase5.TestWindows.Send(shown.Handle, WM_LBUTTONDOWN, 1, point);
            Phase5.TestWindows.Send(shown.Handle, WM_LBUTTONUP, 0, point);
            using Bitmap afterClick = RenderWith(shown, Color.Black);
            using Bitmap rest = RenderWith(resting, Color.Black);
            AssertSameBitmaps(rest, afterClick, "A mouse press hides the keyboard focus mark.");
        });
    }

    private static bool Differs(Bitmap a, Bitmap b)
    {
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Blends colour over background the way a pixel of that alpha lands on an opaque one.
    private static Color Over(Color colour, Color background)
    {
        double a = colour.A / 255.0;
        int Mix(int top, int bottom) => (int)Math.Round((top * a) + (bottom * (1 - a)));
        return Color.FromArgb(Mix(colour.R, background.R), Mix(colour.G, background.G), Mix(colour.B, background.B));
    }

    private static void AssertNear(Color expected, Color actual, string message)
    {
        Assert.IsTrue(
            Math.Abs(expected.R - actual.R) <= 4 && Math.Abs(expected.G - actual.G) <= 4 && Math.Abs(expected.B - actual.B) <= 4,
            message + " Expected " + expected + ", saw " + actual + ".");
    }

    private static void AssertCloser(Color stroke, Color background, Color actual, string message)
    {
        static int Gap(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        Assert.IsTrue(Gap(actual, stroke) * 4 < Gap(stroke, background), message + " Stroke " + stroke + ", background " + background + ", saw " + actual + ".");
    }

    [TestMethod]
    public void AfterTheTabKeyTheOuterStrokeSurroundsConnectInTheThemesOuterColour()
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                foreach (Color ink in Inks)
                {
                    bool dark = ink == Color.White;
                    Color background = dark ? Color.Black : Color.White;
                    using WidgetCard card = ShownCard(dpi, ink);
                    // Button, then the gear, then the battery refresh, then the button again: a keyboard walk that ends on Connect.
                    Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
                    Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
                    Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
                    Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget);

                    using Bitmap bitmap = RenderWith(card, background);
                    Rectangle button = card.CurrentMainLayout.Button;
                    // The pixel in the middle of the outer stroke: gap and inner stroke, then half the outer one.
                    int gap = Math.Max(1, Popup.CardPlacement.Scale(1, dpi));
                    int inner = Math.Max(1, Popup.CardPlacement.Scale(1, dpi));
                    int outer = Math.Max(1, Popup.CardPlacement.Scale(2, dpi));
                    int offset = gap + inner + (outer / 2);
                    int cx = button.X + (button.Width / 2);
                    int cy = button.Y + (button.Height / 2);
                    Color expected = Over(dark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE4, 0, 0, 0), background);
                    string where = " (dpi " + dpi + ", " + (dark ? "dark" : "light") + ")";
                    AssertNear(expected, bitmap.GetPixel(cx, button.Top - 1 - offset), "Top edge" + where);
                    AssertNear(expected, bitmap.GetPixel(cx, button.Bottom + offset), "Bottom edge" + where);
                    // Connect is a pill, so the ends are the tip of a curve and the stroke covers part of the
                    // pixel there: it must still be much nearer the stroke than the background.
                    AssertCloser(expected, background, bitmap.GetPixel(button.Left - 1 - offset, cy), "Left end" + where);
                    AssertCloser(expected, background, bitmap.GetPixel(button.Right + offset, cy), "Right end" + where);
                }
            }
        });
    }

    // WM_QUERYUISTATE answers the control's own UI state; bit 0x1 is UISF_HIDEFOCUS, which stops a list view
    // drawing its dotted item rectangle.
    // https://learn.microsoft.com/en-us/windows/win32/menurc/wm-queryuistate
    [TestMethod]
    public void TheDevicePickersListNeverDrawsItsItemFocusRectangle()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var form = new DevicePickerForm("AirPods", Guid.Empty);
            form.Show();
            Application.DoEvents();
            FieldInfo? field = typeof(DevicePickerForm).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "The picker keeps its list in _list.");
            var list = (ListView)field.GetValue(form)!;
            nint state = Phase5.TestWindows.Send(list.Handle, WM_QUERYUISTATE);
            Assert.AreEqual(1, (int)state & 1, "UISF_HIDEFOCUS is set on the list.");
        });
    }

    // The list keeps the flag when Windows clears it for keyboard use.
    [TestMethod]
    public void TheListKeepsItsFocusRectangleHiddenWhenWindowsClearsTheFlagForKeyboardUse()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var form = new DevicePickerForm("AirPods", Guid.Empty);
            form.Show();
            Application.DoEvents();
            var list = (ListView)typeof(DevicePickerForm).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            const int WM_UPDATEUISTATE = 0x0128;
            Phase5.TestWindows.Send(list.Handle, WM_UPDATEUISTATE, (nint)((0x3 << 16) | 2), 0);
            nint state = Phase5.TestWindows.Send(list.Handle, WM_QUERYUISTATE);
            Assert.AreEqual(1, (int)state & 1, "A clear of hide-focus and hide-accelerators leaves hide-focus set.");
        });
    }

    [TestMethod]
    public void ThePickersListShowsTheFocusVisualOnlyForTheKeyboard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var form = new DevicePickerForm("AirPods", Guid.Empty);
            form.Show();
            Application.DoEvents();
            var list = (ListView)typeof(DevicePickerForm).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            list.Focus();
            Application.DoEvents();
            Assert.IsFalse(form.ListFocusVisualShowing, "Focus alone, as when the dialog opens, shows nothing.");
            Phase5.TestWindows.Send(list.Handle, WM_KEYDOWN, (nint)Keys.Down, 0);
            Assert.IsTrue(form.ListFocusVisualShowing, "A key shows it.");
            Phase5.TestWindows.Send(list.Handle, WM_LBUTTONDOWN, 1, (nint)((5 << 16) | 5));
            Assert.IsFalse(form.ListFocusVisualShowing, "A mouse press hides it.");
        });
    }

    [TestMethod]
    public void TheCueStartsHiddenForAMouseOpenAndShowsFromTheKeysThatMoveFocus()
    {
        var cue = new KeyboardFocusCue();
        Assert.IsFalse(cue.Visible);
        foreach (Keys key in new[] { Keys.Enter, Keys.Space, Keys.Escape, Keys.A, Keys.ShiftKey })
        {
            cue.KeyDown(key);
            Assert.IsFalse(cue.Visible, key + " uses a control and does not show the cue.");
        }

        foreach (Keys key in new[] { Keys.Tab, Keys.Tab | Keys.Shift, Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Home, Keys.End })
        {
            cue.Reset(false);
            cue.KeyDown(key);
            Assert.IsTrue(cue.Visible, key + " moves focus and shows the cue.");
            cue.KeyDown(Keys.Enter);
            Assert.IsTrue(cue.Visible, "Enter leaves it showing.");
            cue.MouseDown();
            Assert.IsFalse(cue.Visible, "A mouse press hides it again.");
        }

        Assert.IsTrue(new KeyboardFocusCue(openedByKeyboard: true).Visible, "A keyboard open shows it from the start.");
        var reopened = new KeyboardFocusCue(openedByKeyboard: true);
        reopened.Reset(false);
        Assert.IsFalse(reopened.Visible);
    }

    [TestMethod]
    public void TheVisualsGeometryIsAGapAnInnerStrokeAndAnOuterStrokeScaledWithDpi()
    {
        FocusMetrics at96 = FocusMetrics.For(96);
        Assert.AreEqual(new FocusMetrics(1, 1, 2), at96);
        Assert.AreEqual(4, at96.Reach, "4 px beyond the control at 100%.");
        Assert.AreEqual(new FocusMetrics(1, 1, 3), FocusMetrics.For(120));
        Assert.AreEqual(new FocusMetrics(2, 2, 3), FocusMetrics.For(144));
        Assert.AreEqual(1, FocusMetrics.For(24).Gap, "Never thinner than a pixel.");
    }

    [TestMethod]
    public void ThePaletteIsTheWindows11OneForEachThemeAndTheSystemsOwnInHighContrast()
    {
        Assert.AreEqual(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), FocusPalette.For(dark: true, highContrast: false).OuterStroke);
        Assert.AreEqual(Color.FromArgb(0xB3, 0x00, 0x00, 0x00), FocusPalette.For(dark: true, highContrast: false).InnerStroke);
        Assert.AreEqual(Color.FromArgb(0xE4, 0x00, 0x00, 0x00), FocusPalette.For(dark: false, highContrast: false).OuterStroke);
        Assert.AreEqual(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF), FocusPalette.For(dark: false, highContrast: false).InnerStroke);
        Assert.AreEqual(SystemColors.WindowText, FocusPalette.For(dark: true, highContrast: true).OuterStroke);
        Assert.AreEqual(SystemColors.Window, FocusPalette.For(dark: false, highContrast: true).InnerStroke);
    }

    [TestMethod]
    public void TheInnerStrokeSitsOneGapOutAndTheGapStaysClear()
    {
        const int dpi = 96;
        using var bitmap = new Bitmap(60, 40, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Gray);
            FocusVisual.Draw(g, new Rectangle(10, 10, 40, 20), 4, dpi, FocusPalette.For(dark: false, highContrast: false));
        }

        // Mid-grey, so a white inner stroke and a black outer stroke both show against it.
        int white = Color.Gray.ToArgb();
        Assert.AreEqual(white, bitmap.GetPixel(30, 10 - 1).ToArgb(), "The gap is untouched.");
        Assert.AreNotEqual(white, bitmap.GetPixel(30, 10 - 2).ToArgb(), "The inner stroke is the second pixel out.");
        Assert.AreNotEqual(white, bitmap.GetPixel(30, 10 - 3).ToArgb(), "The outer stroke is the third and fourth.");
        Assert.AreNotEqual(white, bitmap.GetPixel(30, 10 - 4).ToArgb());
        Assert.AreEqual(white, bitmap.GetPixel(30, 10 - 5).ToArgb(), "Nothing beyond 4 px.");
        Assert.AreEqual(white, bitmap.GetPixel(30, 20).ToArgb(), "Nothing inside the control.");
    }

    [TestMethod]
    public void AFluentButtonNeverAsksForTheDottedRectangleAndPaintsTheVisualInsideItsBounds()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var form = new Form { ClientSize = new Size(200, 80), StartPosition = FormStartPosition.Manual, Location = new Point(20, 20) };
            var button = new FluentButton { Text = "OK", Location = new Point(20, 20), Size = new Size(100, 32), AutoSize = false };
            form.Controls.Add(button);
            form.Show();
            Application.DoEvents();

            PropertyInfo? cues = typeof(FluentButton).GetProperty("ShowFocusCues", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cues);
            Assert.IsFalse((bool)cues.GetValue(button)!, "ShowFocusCues is the framework's hook for the dotted rectangle.");

            using var bitmap = new Bitmap(button.Width, button.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                button.DrawFocusVisual(g);
            }

            int reach = FocusMetrics.For(button.DeviceDpi).Reach;
            FocusMetrics m = FocusMetrics.For(button.DeviceDpi);
            int y = m.Reach - 1 - (m.Gap + m.InnerWidth + (m.OuterWidth / 2));
            Color outer = Over(FocusPalette.For(dark: false, highContrast: false).OuterStroke, Color.White);
            AssertNear(outer, bitmap.GetPixel(button.Width / 2, y), "The outer stroke runs along the inside of the top edge.");
            Assert.AreEqual(Color.White.ToArgb(), bitmap.GetPixel(button.Width / 2, reach + 2).ToArgb(), "Inside the visual the button's own face is left alone.");
        });
    }

    // A supplementary scan, not proof: the behaviour above is the proof.
    [TestMethod]
    public void NoSourceFileAsksForADottedFocusRectangle()
    {
        string src = Path.Combine(RepoRoot(), "src", "Earshot");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (text.Contains("DashStyle.Dot", StringComparison.Ordinal) || text.Contains("DrawFocusRectangle", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(src, file));
            }
        }

        Assert.AreEqual(0, offenders.Count, "Dotted focus rectangles in: " + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
