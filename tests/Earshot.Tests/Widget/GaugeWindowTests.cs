using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real executions kept for the gauge window: styles, MA_NOACTIVATE, a real UpdateLayeredWindow push,
// the foreground window unchanged after ShowAt, and the click-through proof (WindowFromPoint at a pill
// pixel is the gauge, at a pixel outside it is the window beneath). All on a private desktop
// (CardDesktop.Run): the gauge is genuinely shown, so it must never touch the owner's real screen.
//
// Each real GaugeWindow constructed here calls WidgetRealSurfaceGuardTests.AllowRealConstruction(RealWidgetSurface.GaugeWindow), so the
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            nint handle = gauge.Handle;
            nint result = Earshot.Tests.Phase5.TestWindows.Send(handle, NativeMethods.WM_MOUSEACTIVATE);
            Assert.AreEqual((nint)NativeMethods.MA_NOACTIVATE, result);
        });
    }

    // WinForms' base.WndProc calls SetCapture on both button-downs; before the fix neither up handler ever
    // released it, so the gauge kept capture after every click and a click on the card, the menu or the
    // case-open card that followed never reached them until something else released it.
    [TestMethod]
    public void CaptureIsReleasedAfterALeftButtonDownAndUp()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            nint handle = gauge.Handle;

            Earshot.Tests.Phase5.TestWindows.Send(handle, Earshot.Tests.Phase5.TestWindows.WM_LBUTTONDOWN);
            Earshot.Tests.Phase5.TestWindows.Send(handle, Earshot.Tests.Phase5.TestWindows.WM_LBUTTONUP);

            Assert.AreEqual((nint)0, Earshot.Tests.Phase5.TestWindows.GetCapture(),
                "GetCapture() must read 0 after a left down/up pair on the gauge.");
        });
    }

    [TestMethod]
    public void CaptureIsReleasedAfterARightButtonDownAndUp()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            nint handle = gauge.Handle;

            Earshot.Tests.Phase5.TestWindows.Send(handle, Earshot.Tests.Phase5.TestWindows.WM_RBUTTONDOWN);
            Earshot.Tests.Phase5.TestWindows.Send(handle, Earshot.Tests.Phase5.TestWindows.WM_RBUTTONUP);

            Assert.AreEqual((nint)0, Earshot.Tests.Phase5.TestWindows.GetCapture(),
                "GetCapture() must read 0 after a right down/up pair on the gauge.");
        });
    }

    // A private desktop has no foreground-window concept the way the input desktop does: GetForegroundWindow
    // returns 0 both before and after activating a form there, so comparing it before and after ShowAt would
    // pass even if SWP_NOACTIVATE were removed from GaugeWindow.ShowAt and it started stealing activation.
    // The window's own activation state (GetActiveWindow, the calling thread's active window) is real on a
    // private desktop and does discriminate, proved below by toggling the flag under test.
    //
    // The background window is shown but never made active by any real activation primitive (not
    // Form.Activate(), not SetActiveWindow either): the cause found here was never "activation" as such but
    // the Text Services Framework's own process-wide worker threads, which activating a window with the IME
    // enabled starts from the activating thread, binding that thread's desktop to them for the life of the
    // process and leaving the NEXT CardDesktop.Run's own desktop permanently ERROR_BUSY at CloseDesktop
    // (CardDesktopTextServicesTests has the full account and the two Microsoft doc URLs) - reproduced with
    // GaugeWindowTests run before ConnectCardTests' own (deliberately real) activation test, 5/5, and
    // confirmed down to SetActiveWindow alone with no Form.Activate() and no GaugeWindow involved at all:
    // what mattered was the IME being enabled on the activating thread, not which activation primitive ran
    // or which desktop went first. CardDesktop.Run's own thread now disables its IME before its first
    // window for exactly this reason, but this test still never activates anything for real, so a change to
    // that thread-level fix cannot silently bring the failure back here. With nothing ever really activated,
    // GetActiveWindow() starts at 0 and must stay 0 (never the gauge's handle) after a genuinely NOACTIVATE
    // show, which still discriminates: proved below by toggling the flag under test.
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
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

    // Before the fix, HideWindow's own SetWindowPos result was discarded outright: a real failure (the
    // window handle already gone, say) left nothing in the log to explain why the gauge never actually
    // disappeared. Proved here the same way AppBarRegistrationTests proves ABM_REMOVE's own outcome now
    // reaches the log: a real ShowAt then HideWindow, both against the real window, with the log checked
    // for the line HideWindow's own SetWindowPos step must now leave behind.
    [TestMethod]
    public void HideWindowRecordsItsOwnOutcomeInTheLog()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            var bounds = new Rectangle(50, 50, GaugeRenderer.WidthFor(96), 48);

            Earshot.Contracts.StepOutcome shown = gauge.ShowAt(bounds);
            Assert.IsTrue(shown.Ok, "ShowAt: " + shown.CodeName + " " + shown.Detail);
            Application.DoEvents();

            gauge.HideWindow();

            Assert.IsTrue(log.Has(LogLevel.Debug, "Gauge: set-window-pos:hide-gauge"),
                "HideWindow's own SetWindowPos outcome must reach the log: " +
                string.Join(" | ", log.Entries.Select(e => e.Level + ":" + e.Message)));
        });
    }

    // The log check above proves HideWindow reports its own outcome, but SetWindowPos would report exactly
    // the same success whether or not SWP_HIDEWINDOW itself was among the flags passed - a mutant that
    // dropped that one flag while leaving NOACTIVATE, NOMOVE, NOSIZE and NOZORDER in place would still make
    // the call, still get TRUE back, still log the same Debug line, and the window would stay on screen. The
    // only thing that actually proves the window disappeared is asking Windows whether it is still visible.
    [TestMethod]
    public void HideWindowActuallyMakesTheRealWindowInvisible()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var gauge = new GaugeWindow(log);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            var bounds = new Rectangle(50, 50, GaugeRenderer.WidthFor(96), 48);

            Earshot.Contracts.StepOutcome shown = gauge.ShowAt(bounds);
            Assert.IsTrue(shown.Ok, "ShowAt: " + shown.CodeName + " " + shown.Detail);
            Application.DoEvents();
            Assert.IsTrue(Earshot.Tests.Phase5.TestWindows.IsWindowVisible(gauge.Handle),
                "Sanity: the gauge must actually be visible after ShowAt for HideWindow's own effect to mean anything.");

            gauge.HideWindow();
            Application.DoEvents();

            Assert.IsFalse(Earshot.Tests.Phase5.TestWindows.IsWindowVisible(gauge.Handle),
                "HideWindow must make the real window invisible, not merely report success.");
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
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
