using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real executions kept for the gauge window: styles, MA_NOACTIVATE, a real UpdateLayeredWindow push,
// the foreground window unchanged after ShowAt, and the click-through proof (WindowFromPoint at a pill
// pixel is the gauge, at a pixel outside it is the window beneath). All on a private desktop
// (CardDesktop.Run): the gauge is genuinely shown, so it must never touch the owner's real screen.
[TestClass]
public sealed class GaugeWindowTests
{
    [TestMethod]
    public void StylesAreExactlyTheDocumentedSet()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            nint handle = gauge.Handle;
            long exStyle = Earshot.Tests.Phase5.TestWindows.ExtendedStyle(handle);
            const long Expected = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED;
            Assert.AreEqual(Expected, exStyle & Expected, "The four documented extended styles must all be set.");
        });
    }

    [TestMethod]
    public void MouseActivateAnswersNoActivate()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            nint handle = gauge.Handle;
            nint result = Earshot.Tests.Phase5.TestWindows.Send(handle, NativeMethods.WM_MOUSEACTIVATE);
            Assert.AreEqual((nint)NativeMethods.MA_NOACTIVATE, result);
        });
    }

    [TestMethod]
    public void ShowAtDoesNotChangeTheForegroundWindowAndRendersAVisibleBitmap()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            using var background = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(400, 200),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                BackColor = Color.Black,
            };
            background.Show();
            background.Activate();
            Application.DoEvents();
            nint foregroundBefore = GetForegroundWindow();

            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            var bounds = new Rectangle(50, 50, GaugeRenderer.WidthFor(96), 48);
            Earshot.Contracts.StepOutcome shown = gauge.ShowAt(bounds);
            Assert.IsTrue(shown.Ok, "ShowAt: " + shown.CodeName + " " + shown.Detail);
            Application.DoEvents();

            Assert.AreEqual(foregroundBefore, GetForegroundWindow(), "A NOACTIVATE show must not steal the foreground.");

            WidgetSnapshot snapshot = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with
            {
                Where = AirPodsWhere.ThisPc,
                Left = new PartReading(70, false, null),
            };
            gauge.Render(snapshot, 96, bounds, Color.White, hover: false, "Segoe UI");
            Application.DoEvents();

            // The pill covers the whole gauge at at least alpha 1, so its centre must now show the gauge
            // itself when queried by point, and a corner outside the rounded pill must show what is
            // beneath it (the click-through proof, https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
            Point centre = new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
            nint atCentre = WindowFromPoint(centre);
            Assert.AreEqual(gauge.Handle, atCentre, "The pill pixel must belong to the gauge.");

            Point corner = new(bounds.X, bounds.Y);
            nint atCorner = WindowFromPoint(corner);
            Assert.AreEqual(background.Handle, atCorner, "Outside the pill, the click must reach the window beneath.");
        });
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
}
