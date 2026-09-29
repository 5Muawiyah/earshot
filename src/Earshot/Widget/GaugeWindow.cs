using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// The layered overlay gauge: an owned, topmost tool window over free taskbar space, never parented into
// Shell_TrayWnd. Paints nothing itself through WinForms; content comes from GaugeRenderer as a
// premultiplied bitmap pushed with UpdateLayeredWindow, whose alpha-0 pixels let a click pass through to
// the taskbar underneath (https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows). Never sets Form.Opacity or Form.TransparencyKey (both call
// SetLayeredWindowAttributes, after which UpdateLayeredWindow fails until the style bit is cleared and
// set again) and never WS_EX_TRANSPARENT (which would pass every click through, the gauge's own
// included). UI thread only.
internal sealed class GaugeWindow : Form, IGaugeSurface
{
    internal const int ExtendedStyles = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW |
        NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED;

    private readonly ILog _log;
    private bool _tracking;
    private bool _shown;
    private bool _leftButtonDown;

    // Test seam only: counts real constructions so WidgetRealSurfaceGuardTests can prove a test harness
    // never builds this class in place of a fake. Never read or reset in production.
    internal static int ConstructionCount;

    // True from a successful ShowAt until HideWindow (or a fresh, unshown instance): both go through
    // SetWindowPos directly rather than Form.Show/Hide, so Control.Visible never reflects this on its own
    // and IsHandleCreated stays true even once hidden (the handle is never destroyed by either call).
    internal bool IsShown => _shown;

    public GaugeWindow(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        Interlocked.Increment(ref ConstructionCount);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        SetStyle(ControlStyles.Selectable, false);
        AccessibleName = "Earshot";
        AccessibleRole = AccessibleRole.PushButton;
    }

    public event EventHandler? LeftClicked;

    public event EventHandler<Point>? RightClicked;

    protected override bool ShowWithoutActivation => true;

    // Mirrors ConnectCard.OnDpiChanged: the gauge is already sized and rendered for the display it is
    // moving to (GaugeController re-measures the taskbar and calls Render at the new DPI on the very next
    // layout, driven by TaskbarWatcher's own poll rather than this event), so the resize
    // DefWindowProc would otherwise apply here is cancelled rather than fought afterwards.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= ExtendedStyles;
            return cp;
        }
    }

    // Renders content and pushes it through UpdateLayeredWindow, at the window's current screen location
    // (or the location bounds gives, for the first push before the window has ever been positioned).
    public void Render(WidgetSnapshot snapshot, int dpi, Rectangle bounds, Color ink, bool hover, string fontFamily)
    {
        using Bitmap bitmap = GaugeRenderer.Render(snapshot, dpi, bounds.Height, ink, hover, fontFamily);
        Push(bitmap, bounds.Location);
    }

    public StepOutcome ShowAt(Rectangle bounds)
    {
        nint handle = Handle; // creates the window, hidden, the first time
        const string Step = "set-window-pos:show-gauge";
        if (!NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
        {
            return StepOutcomes.FromWin32(Step, unchecked((uint)Marshal.GetLastPInvokeError()));
        }

        _shown = true;
        return StepOutcomes.FromWin32(Step, 0);
    }

    public StepOutcome MoveTo(Rectangle bounds)
    {
        const string Step = "set-window-pos:move-gauge";
        if (!NativeMethods.SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
        {
            return StepOutcomes.FromWin32(Step, unchecked((uint)Marshal.GetLastPInvokeError()));
        }

        return StepOutcomes.FromWin32(Step, 0);
    }

    // Re-raises the window above whatever opened after it, without moving or resizing it.
    public StepOutcome Raise()
    {
        const string Step = "set-window-pos:raise-gauge";
        if (!NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE))
        {
            return StepOutcomes.FromWin32(Step, unchecked((uint)Marshal.GetLastPInvokeError()));
        }

        return StepOutcomes.FromWin32(Step, 0);
    }

    // Hides the window. The result is the raw SetWindowPos outcome (success when there was nothing to hide);
    // the controller writes it into the hide event with the reason, so this method logs nothing itself.
    public StepOutcome HideWindow()
    {
        const string Step = "set-window-pos:hide-gauge";
        if (!_shown || !IsHandleCreated)
        {
            return StepOutcomes.FromWin32(Step, 0);
        }

        _shown = false;
        bool ok = NativeMethods.SetWindowPos(Handle, 0, 0, 0, 0, 0,
            NativeMethods.SWP_HIDEWINDOW | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
        // _shown moves to false either way: the controller has already decided the gauge is not shown
        // whether or not the window itself actually disappeared.
        return StepOutcomes.FromWin32(Step, ok ? 0 : unchecked((uint)Marshal.GetLastPInvokeError()), ok: ok);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                m.Result = NativeMethods.MA_NOACTIVATE;
                return;

            case NativeMethods.WM_MOUSEMOVE:
                TrackLeave();
                base.WndProc(ref m);
                return;

            case NativeMethods.WM_MOUSELEAVE:
                _tracking = false;
                base.WndProc(ref m);
                return;

            // WinForms' own base.WndProc calls SetCapture on both button-downs (the default Control
            // handling for WM_LBUTTONDOWN/WM_RBUTTONDOWN), so this window keeps receiving the matching
            // move/up messages even if the pointer leaves it before the button is released. Both up
            // handlers below release that capture in turn: left before this fix never did, so the gauge
            // kept capture after every click and a following click on the card, the menu or the case-open
            // card - all separate windows - never reached them until the gauge's own capture was released
            // by something else. https://learn.microsoft.com/en-us/windows/win32/inputdev/about-mouse-input#mouse-capture
            case NativeMethods.WM_LBUTTONDOWN:
                _leftButtonDown = true;
                base.WndProc(ref m);
                return;

            case NativeMethods.WM_RBUTTONDOWN:
                base.WndProc(ref m);
                return;

            case NativeMethods.WM_LBUTTONUP:
                // Only an up that follows a down on this same window is a click: a drag that started
                // outside the window and released inside it must not count (it would otherwise deliver an
                // up with no preceding down here).
                if (_leftButtonDown)
                {
                    _leftButtonDown = false;
                    LeftClicked?.Invoke(this, EventArgs.Empty);
                }

                NativeMethods.ReleaseCapture();
                return;

            case NativeMethods.WM_RBUTTONUP:
            {
                int x = unchecked((short)(long)m.LParam);
                int y = unchecked((short)((long)m.LParam >> 16));
                Point client = new(x, y);
                Point screen = PointToScreen(client);
                RightClicked?.Invoke(this, screen);
                NativeMethods.ReleaseCapture();
                return;
            }

            // Capture has already moved on by the time this arrives (to another window, or released
            // outright): the documented contract is that a window handling it must not call ReleaseCapture
            // itself. Only the down-tracking flag needs resetting, so a capture lost mid-press (the pointer
            // grabbed by another window, Alt+Tab, or any other cancel path) never leaves _leftButtonDown
            // stuck true for a later, unrelated up to answer as a click.
            // https://learn.microsoft.com/en-us/windows/win32/inputmsg/wm-capturechanged
            case NativeMethods.WM_CAPTURECHANGED:
                _leftButtonDown = false;
                base.WndProc(ref m);
                return;

            default:
                base.WndProc(ref m);
                return;
        }
    }

    private void TrackLeave()
    {
        if (_tracking || !IsHandleCreated)
        {
            return;
        }

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = LayeredWindow.TME_LEAVE,
            hwndTrack = Handle,
        };
        _tracking = LayeredWindow.TrackMouseEvent(ref track);
    }

    // Copies bitmap's premultiplied pixels into a fresh top-down 32 bpp DIB section and pushes it with
    // UpdateLayeredWindow. A DDB from Bitmap.GetHbitmap carries no alpha channel, so it is never used
    // here.
    private void Push(Bitmap bitmap, Point location)
    {
        nint screenDc = LayeredWindow.GetDC(0);
        nint memDc = 0;
        nint dib = 0;
        nint oldObject = 0;
        try
        {
            memDc = LayeredWindow.CreateCompatibleDC(screenDc);
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = bitmap.Width,
                    biHeight = -bitmap.Height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            dib = LayeredWindow.CreateDIBSection(screenDc, in info, 0, out nint bits, 0, 0);
            if (dib == 0 || bits == 0)
            {
                _log.Warn("The gauge bitmap could not be pushed: CreateDIBSection failed.");
                return;
            }

            oldObject = LayeredWindow.SelectObject(memDc, dib);

            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = bitmap.Width * 4;
                var row = new byte[stride];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, stride);
                    Marshal.Copy(row, 0, bits + (y * stride), stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var pptDst = new POINT { x = location.X, y = location.Y };
            var pptSrc = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = LayeredWindow.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = LayeredWindow.AC_SRC_ALPHA,
            };

            if (!LayeredWindow.UpdateLayeredWindow(Handle, 0, in pptDst, in size, memDc, in pptSrc, 0, in blend, LayeredWindow.ULW_ALPHA))
            {
                StepOutcome outcome = StepOutcomes.FromWin32("update-layered-window", unchecked((uint)Marshal.GetLastPInvokeError()));
                _log.Warn("The gauge bitmap could not be pushed: " + outcome.CodeName + " (" + outcome.Code + ") " + outcome.Detail);
            }
        }
        finally
        {
            if (memDc != 0 && oldObject != 0)
            {
                LayeredWindow.SelectObject(memDc, oldObject);
            }

            if (dib != 0)
            {
                LayeredWindow.DeleteObject(dib);
            }

            if (memDc != 0)
            {
                LayeredWindow.DeleteDC(memDc);
            }

            if (screenDc != 0)
            {
                _ = LayeredWindow.ReleaseDC(0, screenDc);
            }
        }
    }
}
