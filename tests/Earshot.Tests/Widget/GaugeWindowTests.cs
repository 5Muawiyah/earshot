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
//
// Each real GaugeWindow constructed here calls WidgetRealSurfaceGuardTests.AllowRealConstruction(), so the
// assembly-wide guard can tell these named, private-desktop executions apart from an unnoticed real
// construction anywhere else in the suite.
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction();
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction();
            nint handle = gauge.Handle;
            nint result = Earshot.Tests.Phase5.TestWindows.Send(handle, NativeMethods.WM_MOUSEACTIVATE);
            Assert.AreEqual((nint)NativeMethods.MA_NOACTIVATE, result);
        });
    }

    // A private desktop has no foreground-window concept the way the input desktop does: GetForegroundWindow
    // returns 0 both before and after activating a form there, so comparing it before and after ShowAt would
    // pass even if SWP_NOACTIVATE were removed from GaugeWindow.ShowAt and it started stealing activation.
    // The window's own activation state (GetActiveWindow, the calling thread's active window) is real on a
    // private desktop and does discriminate, proved below by toggling the flag under test.
    //
    // The background window is shown but never made active by any real activation primitive (not
    // Form.Activate(), not SetActiveWindow either): a real activation transition on a private desktop in
    // this process was found to leave the NEXT CardDesktop.Run's own desktop permanently ERROR_BUSY at
    // CloseDesktop, regardless of which real activation primitive performs it or which desktop performs it
    // first - reproduced with GaugeWindowTests run before ConnectCardTests' own (deliberately real)
    // activation test, 5/5, and confirmed down to SetActiveWindow alone with no Form.Activate() and no
    // GaugeWindow involved at all. With nothing ever really activated, GetActiveWindow() starts at 0 and
    // must stay 0 (never the gauge's handle) after a genuinely NOACTIVATE show, which still discriminates:
    // proved below by toggling the flag under test.
    [TestMethod]
    public void ShowAtDoesNotChangeTheActiveWindowAndRendersAVisibleBitmap()
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
            nint backgroundHandle = background.Handle;
            NativeMethods.SetWindowPos(backgroundHandle, 0, 0, 0, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
            Application.DoEvents();
            nint activeBefore = Earshot.Tests.Phase5.TestWindows.GetActiveWindow();

            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction();
            var bounds = new Rectangle(50, 50, GaugeRenderer.WidthFor(96), 48);
            Earshot.Contracts.StepOutcome shown = gauge.ShowAt(bounds);
            Assert.IsTrue(shown.Ok, "ShowAt: " + shown.CodeName + " " + shown.Detail);
            Application.DoEvents();

            nint activeAfter = Earshot.Tests.Phase5.TestWindows.GetActiveWindow();
            Assert.AreEqual(activeBefore, activeAfter, "A NOACTIVATE show must not change the thread's active window.");
            Assert.AreNotEqual(gauge.Handle, activeAfter, "The gauge must never become the active window.");

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

    // A drag that started outside the window and released inside it delivers WM_LBUTTONUP with no
    // preceding WM_LBUTTONDOWN on this window: it must not be counted as a click.
    [TestMethod]
    public void ALeftButtonUpWithNoPrecedingDownIsNotAClick()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction();
            nint handle = gauge.Handle;
            bool clicked = false;
            gauge.LeftClicked += (_, _) => clicked = true;

            Earshot.Tests.Phase5.TestWindows.Send(handle, NativeMethods.WM_LBUTTONUP);

            Assert.IsFalse(clicked, "An up with no preceding down on this window must not raise LeftClicked.");
        });
    }

    [TestMethod]
    public void ALeftButtonDownThenUpOnTheSameWindowIsAClick()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction();
            nint handle = gauge.Handle;
            bool clicked = false;
            gauge.LeftClicked += (_, _) => clicked = true;

            Earshot.Tests.Phase5.TestWindows.Send(handle, NativeMethods.WM_LBUTTONDOWN);
            Earshot.Tests.Phase5.TestWindows.Send(handle, NativeMethods.WM_LBUTTONUP);

            Assert.IsTrue(clicked, "A down followed by an up on the same window is a click.");
        });
    }

    // Mirrors ConnectCard.OnDpiChanged: the gauge is already sized for the display it is moving to, so a
    // real WM_DPICHANGED must never let WinForms' default handling apply the message's own suggested
    // rectangle. A real SendMessage (TestWindows.Send blocks until WndProc returns, exactly the real
    // synchronous delivery https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagew
    // documents), not a synthetic DpiChangedEventArgs, so the proof is the same WM_DPICHANGED path Windows
    // itself would deliver on a real monitor or DPI change.
    [TestMethod]
    public void DpiChangedNeverAppliesTheSuggestedRect()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction();
            var bounds = new Rectangle(50, 50, GaugeRenderer.WidthFor(96), 48);
            Earshot.Contracts.StepOutcome shown = gauge.ShowAt(bounds);
            Assert.IsTrue(shown.Ok, "ShowAt: " + shown.CodeName + " " + shown.Detail);
            Application.DoEvents();
            Rectangle before = gauge.Bounds;

            // A suggested rectangle nothing here would ever produce on its own, so an unwanted resize is
            // unmistakable if OnDpiChanged's cancellation stops working.
            var suggested = new RECT { left = 500, top = 500, right = 900, bottom = 800 };
            nint rectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
            try
            {
                Marshal.StructureToPtr(suggested, rectPtr, fDeleteOld: false);
                nint wParam = (nint)(192 | (192 << 16)); // MAKEWPARAM(192, 192): a new DPI of 192 on both axes.
                Earshot.Tests.Phase5.TestWindows.Send(gauge.Handle, NativeMethods.WM_DPICHANGED, wParam, rectPtr);
                Application.DoEvents();

                Assert.AreEqual(before, gauge.Bounds, "OnDpiChanged must cancel the event: the suggested rectangle from a real WM_DPICHANGED must never be applied.");
            }
            finally
            {
                Marshal.FreeHGlobal(rectPtr);
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
}
