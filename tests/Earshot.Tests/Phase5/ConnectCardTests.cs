using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Boot;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// The real card window. Most checks run on an STA thread, where the window is created but never shown:
// they read the hidden window or render it with DrawToBitmap. The activation check shows the card, on a
// private desktop that is never on screen.
[TestClass]
public sealed class ConnectCardTests
{
    private const int WideEnough = 1896;

    // Status lines Earshot puts on cards, including the longest.
    private static string[] KnownStatuses() =>
    [
        TrayStatus.CardConnecting,
        TrayStatus.CardConnected,
        TrayStatus.CardDisconnected,
        TrayStatus.CardBlockedAtBoot,
        TrayStatus.MicrophoneNotice,
        TrayStatus.NotFoundMessage(new EarshotSettings()),
        TrayContext.BusyMessage,
        TrayContext.SomethingWentWrongMessage,
        BlockController.NotPresentBlockMessage,
        BlockController.NotSetUpMessage,
        SafeDecorators.Message,
    ];

    [TestMethod]
    public void TheWindowNeverActivatesAndStaysOutOfTheTaskbar()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            nint hwnd = card.Handle;

            long style = TestWindows.ExtendedStyle(hwnd);
            Assert.AreEqual((long)NativeMethods.WS_EX_NOACTIVATE, style & NativeMethods.WS_EX_NOACTIVATE, "Not activated by a click.");
            Assert.AreEqual((long)NativeMethods.WS_EX_TOOLWINDOW, style & NativeMethods.WS_EX_TOOLWINDOW, "Not in the taskbar or Alt+Tab.");
            Assert.AreEqual((long)NativeMethods.WS_EX_TOPMOST, style & NativeMethods.WS_EX_TOPMOST, "Above other windows.");
            Assert.IsFalse(TestWindows.IsWindowVisible(hwnd), "Creating the card shows nothing.");
            Assert.IsFalse(card.ShowInTaskbar);
            Assert.IsFalse(card.TopMost, "Topmost comes from the extended style; the TopMost setter activates.");
            Assert.AreEqual(FormBorderStyle.None, card.FormBorderStyle);

            PropertyInfo showWithoutActivation = typeof(ConnectCard).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsTrue((bool)showWithoutActivation.GetValue(card)!);

            Assert.AreEqual((nint)NativeMethods.MA_NOACTIVATE, TestWindows.Send(hwnd, NativeMethods.WM_MOUSEACTIVATE));
        });
    }

    [TestMethod]
    public void ShowingReplacingAndHidingTheCardLeavesTheActiveWindowAndFocusAlone()
    {
        CardDesktop.Run(() =>
        {
            using var host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(100, 100),
                Size = new Size(320, 200),
                ShowInTaskbar = false,
            };
            var box = new TextBox();
            host.Controls.Add(box);
            host.Show();
            host.Activate();
            box.Focus();
            Application.DoEvents();
            Assert.AreEqual(host.Handle, TestWindows.GetActiveWindow(), "The host window is active before any card.");
            Assert.AreEqual(box.Handle, TestWindows.GetFocus(), "The host's text box has the focus before any card.");

            using var card = new ConnectCard(new CapturingLog());
            Size size = card.Prepare(new CardContent(Desktops.AirPodsName, TrayStatus.CardConnecting), 96, CardTheme.Dark, WideEnough);
            StepOutcome shown = card.ShowAt(new Rectangle(new Point(600, 400), size));
            Application.DoEvents();
            Assert.IsTrue(shown.Ok, TrayReport.DescribeStep(shown));
            Assert.IsTrue(TestWindows.IsWindowVisible(card.Handle), "The card is shown.");
            AssertHostKeepsActivationAndFocus(host, box, card, "after the card is shown");

            // A newer card replaces the content and moves the window that is already on screen.
            size = card.Prepare(new CardContent(Desktops.AirPodsName, TrayStatus.MicrophoneNotice), 96, CardTheme.Light, WideEnough);
            shown = card.ShowAt(new Rectangle(new Point(500, 380), size));
            Application.DoEvents();
            Assert.IsTrue(shown.Ok, TrayReport.DescribeStep(shown));
            Assert.IsTrue(TestWindows.IsWindowVisible(card.Handle));
            AssertHostKeepsActivationAndFocus(host, box, card, "after the card is replaced");

            StepOutcome? hidden = card.HideCard();
            Application.DoEvents();
            Assert.IsNotNull(hidden);
            Assert.IsTrue(hidden.Ok, TrayReport.DescribeStep(hidden));
            Assert.IsFalse(TestWindows.IsWindowVisible(card.Handle), "The card is hidden.");
            Assert.IsFalse(card.IsShownOnScreen());
            AssertHostKeepsActivationAndFocus(host, box, card, "after the card is hidden");

            // Control: the same kind of window, still WS_EX_NOACTIVATE and answering MA_NOACTIVATE, shown by
            // SetWindowPos without SWP_NOACTIVATE takes the activation and the focus. So the checks above can
            // see an activation, and ShowAt's SWP_NOACTIVATE is what prevents one.
            using var control = new ConnectCard(new CapturingLog());
            Size controlSize = control.Prepare(new CardContent(Desktops.AirPodsName, TrayStatus.CardConnected), 96, CardTheme.Dark, WideEnough);
            Assert.IsTrue(NativeMethods.SetWindowPos(control.Handle, NativeMethods.HWND_TOPMOST, 600, 600, controlSize.Width, controlSize.Height, NativeMethods.SWP_SHOWWINDOW));
            Application.DoEvents();
            Assert.AreEqual(control.Handle, TestWindows.GetActiveWindow(), "Without SWP_NOACTIVATE the window is activated.");
            Assert.AreNotEqual(box.Handle, TestWindows.GetFocus(), "Without SWP_NOACTIVATE the host loses the focus.");
        });
    }

    [TestMethod]
    public void AClickWithAnyButtonRaisesClicked()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            int clicks = 0;
            card.Clicked += (_, _) => clicks++;

            TestWindows.Send(card.Handle, TestWindows.WM_LBUTTONDOWN);
            TestWindows.Send(card.Handle, TestWindows.WM_LBUTTONUP);
            Assert.AreEqual(1, clicks);

            TestWindows.Send(card.Handle, TestWindows.WM_RBUTTONDOWN);
            TestWindows.Send(card.Handle, TestWindows.WM_RBUTTONUP);
            Assert.AreEqual(2, clicks);
        });
    }

    [TestMethod]
    public void TheRenderedCardHasTheNameAndStatusAndNoBatteryText()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            foreach (CardPalette palette in new[] { CardTheme.Dark, CardTheme.Light })
            {
                foreach (string status in KnownStatuses())
                {
                    var content = new CardContent(Desktops.AirPodsName, status);
                    Size size = card.Prepare(content, 96, palette, WideEnough);
                    using Bitmap bitmap = Render(card, size);

                    IReadOnlyList<string> painted = card.LastPaintedText();
                    CollectionAssert.AreEqual(new[] { Desktops.AirPodsName, status }, painted.ToArray(), "The card draws the name and the status and nothing else.");
                    string beyondName = string.Concat(painted.Skip(1));
                    Assert.IsFalse(beyondName.Contains('%', StringComparison.Ordinal), "No percentage: " + beyondName);
                    Assert.IsFalse(beyondName.Any(char.IsDigit), "No figure: " + beyondName);

                    AssertOnlyTheTwoLinesAreDrawn(card, bitmap, palette);
                }
            }

            Assert.AreEqual(0, card.Controls.Count, "No child control could draw anything else.");
        });
    }

    [TestMethod]
    public void DigitsInTheDeviceNameAreTheOnlyDigitsOnTheCard()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            const string Name = "AirPods Pro 3";

            Size size = card.Prepare(new CardContent(Name, TrayStatus.CardConnected), 144, CardTheme.Dark, WideEnough);
            using Bitmap bitmap = Render(card, size);

            IReadOnlyList<string> painted = card.LastPaintedText();
            Assert.HasCount(2, painted);
            Assert.AreEqual(Name, painted[0]);
            Assert.IsFalse(painted.Skip(1).Any(line => line.Any(char.IsDigit) || line.Contains('%', StringComparison.Ordinal)));
            AssertOnlyTheTwoLinesAreDrawn(card, bitmap, CardTheme.Dark);
        });
    }

    [TestMethod]
    public void TheCurlyApostropheInTheNameIsKept()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());

            Size size = card.Prepare(new CardContent(Desktops.AirPodsName, TrayStatus.CardConnected), 96, CardTheme.Light, WideEnough);
            using Bitmap bitmap = Render(card, size);

            Assert.AreEqual(Desktops.AirPodsName, card.LastPaintedText()[0]);
            Assert.IsTrue(card.LastPaintedText()[0].Contains((char)0x2019, StringComparison.Ordinal));
            Assert.AreEqual(Desktops.AirPodsName, card.AccessibleName);
        });
    }

    [TestMethod]
    public void TheCardIsSizedForTheDpiItIsPreparedFor()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            var content = new CardContent(Desktops.AirPodsName, BlockController.NotSetUpMessage);

            Size at96 = card.Prepare(content, 96, CardTheme.Dark, int.MaxValue);
            Rectangle title96 = card.TitleLineBounds();
            Size at144 = card.Prepare(content, 144, CardTheme.Dark, int.MaxValue);
            Rectangle title144 = card.TitleLineBounds();
            Size at192 = card.Prepare(content, 192, CardTheme.Dark, int.MaxValue);
            Rectangle title192 = card.TitleLineBounds();

            Assert.AreEqual(new Point(16, 12), title96.Location);
            Assert.AreEqual(new Point(24, 18), title144.Location);
            Assert.AreEqual(new Point(32, 24), title192.Location);
            AssertScaled(at96.Width, at144.Width, 1.5);
            AssertScaled(at96.Height, at144.Height, 1.5);
            AssertScaled(at96.Width, at192.Width, 2.0);
            AssertScaled(at96.Height, at192.Height, 2.0);
            Assert.AreEqual(at192, card.ClientSize);
        });
    }

    [TestMethod]
    public void TheCardIsNeverWiderThanItIsAllowedAndKeepsOneLineEach()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            var content = new CardContent(Desktops.AirPodsName, BlockController.NotPresentBlockMessage);

            Size single = card.Prepare(new CardContent(Desktops.AirPodsName, TrayStatus.CardConnected), 96, CardTheme.Dark, WideEnough);
            int oneLine = card.StatusLineBounds().Height;

            Size narrow = card.Prepare(content, 96, CardTheme.Dark, 300);
            Assert.AreEqual(300, narrow.Width);
            Assert.AreEqual(single.Height, narrow.Height, "A long status is cut, not wrapped.");
            Assert.AreEqual(oneLine, card.StatusLineBounds().Height);

            Size wide = card.Prepare(content, 96, CardTheme.Dark, WideEnough);
            Assert.IsLessThanOrEqualTo(ConnectCard.MaxWidthAt96, wide.Width);
            Assert.IsGreaterThanOrEqualTo(ConnectCard.MinWidthAt96, single.Width);
        });
    }

    [TestMethod]
    public void HidingACardThatWasNeverShownMakesNoCall()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            Assert.IsNull(card.HideCard());
            _ = card.Handle;
            Assert.IsNull(card.HideCard());
            Assert.IsFalse(card.IsShownOnScreen());
        });
    }

    [TestMethod]
    public void TheFormsTimerElapsesOnceAfterTheLastRestart()
    {
        CardSta.Run(() =>
        {
            using var timer = new FormsCardTimer();
            int elapsed = 0;
            timer.Elapsed += (_, _) => elapsed++;
            var clock = Stopwatch.StartNew();

            timer.Restart(TimeSpan.FromMilliseconds(1000));
            CardSta.PumpUntil(() => clock.ElapsedMilliseconds >= 300, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, elapsed);

            TimeSpan restartedAt = clock.Elapsed;
            timer.Restart(TimeSpan.FromMilliseconds(1000));
            Assert.IsTrue(CardSta.PumpUntil(() => elapsed > 0, TimeSpan.FromSeconds(10)));
            Assert.IsGreaterThanOrEqualTo(900.0, (clock.Elapsed - restartedAt).TotalMilliseconds, "The restart began a new interval.");

            TimeSpan firedAt = clock.Elapsed;
            CardSta.PumpUntil(() => clock.Elapsed - firedAt >= TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, elapsed, "It elapses once per restart.");

            timer.Restart(TimeSpan.FromMilliseconds(100));
            timer.Stop();
            TimeSpan stoppedAt = clock.Elapsed;
            CardSta.PumpUntil(() => clock.Elapsed - stoppedAt >= TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, elapsed, "Stop cancels the interval.");
        });
    }

    private static Bitmap Render(ConnectCard card, Size size)
    {
        Assert.AreEqual(size, card.ClientSize);
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        return bitmap;
    }

    // Every pixel outside the title and status lines is the card background (or the painted border when DWM
    // draws none), and each line has drawn something.
    private static void AssertOnlyTheTwoLinesAreDrawn(ConnectCard card, Bitmap bitmap, CardPalette palette)
    {
        Rectangle title = card.TitleLineBounds();
        Rectangle status = card.StatusLineBounds();
        int width = bitmap.Width;
        int height = bitmap.Height;
        int background = palette.Background.ToArgb() & 0xFFFFFF;
        int border = palette.Border.ToArgb() & 0xFFFFFF;
        bool paintedBorder = !card.HasDwmFrame();
        int inkInTitle = 0;
        int inkInStatus = 0;

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[data.Stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, data.Stride);
                for (int x = 0; x < width; x++)
                {
                    int rgb = row[(x * 3) + 2] << 16 | row[(x * 3) + 1] << 8 | row[x * 3];
                    if (title.Contains(x, y))
                    {
                        inkInTitle += rgb != background ? 1 : 0;
                        continue;
                    }

                    if (status.Contains(x, y))
                    {
                        inkInStatus += rgb != background ? 1 : 0;
                        continue;
                    }

                    bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                    int expected = paintedBorder && edge ? border : background;
                    if (rgb != expected)
                    {
                        Assert.Fail(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"Pixel {x},{y} is {rgb:X6}, expected {expected:X6}: something was drawn outside the name and status lines."));
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.IsGreaterThan(0, inkInTitle, "The name was drawn.");
        Assert.IsGreaterThan(0, inkInStatus, "The status was drawn.");
    }

    private static void AssertHostKeepsActivationAndFocus(Form host, TextBox box, ConnectCard card, string when)
    {
        nint active = TestWindows.GetActiveWindow();
        nint focus = TestWindows.GetFocus();
        Assert.AreNotEqual(card.Handle, active, "The card was activated " + when + ".");
        Assert.AreEqual(host.Handle, active, "The host window lost the activation " + when + ".");
        Assert.AreEqual(box.Handle, focus, "The host's text box lost the focus " + when + ".");
    }

    private static void AssertScaled(int at96, int scaled, double factor)
    {
        double expected = at96 * factor;
        Assert.IsLessThanOrEqualTo(expected * 0.1, Math.Abs(scaled - expected), string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{scaled} is not about {factor} times {at96}."));
    }
}
