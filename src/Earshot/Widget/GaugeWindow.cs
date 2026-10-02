using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tray;

namespace Earshot.Widget;

// The layered overlay gauge: a topmost tool window over free taskbar space, owned by the taskbar it sits on (SetOwner, so
// it stays above it) but never parented into Shell_TrayWnd. Paints nothing itself through WinForms; content comes from GaugeRenderer as a
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

    // accent: where the ring's colour comes from; null means the real Windows accent colour (the shared
    // AccentColourService), which is what production uses.
    public GaugeWindow(ILog log, IAccentColours? accent = null, Func<GaugeOrder>? order = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _order = order ?? (static () => GaugeOrder.RingNumberBolt);
        _accent = accent ?? AccentColourService.Shared(log);
        _accent.Changed += OnAccentChanged;
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

    // The window handle, or 0 while none has been created: reading Handle would create it.
    public nint WindowHandle => IsHandleCreated ? Handle : 0;

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

    // What the last Render was asked for, so a hover change or an accent colour change can draw the same
    // thing again without waiting for the next poll.
    private sealed record RenderRequest(
        WidgetSnapshot Snapshot, DateTimeOffset Now, GaugeDisplaySettings Settings, int Dpi, Rectangle Bounds, Color Ink, string FontFamily);

    private readonly IAccentColours _accent;
    private readonly Func<GaugeOrder> _order;
    private readonly ToolTip _tip = new() { ShowAlways = true };
    // Test seam only: how many bitmaps have been pushed, so a test can tell a repaint that changed something
    // from one that was skipped as identical. Never read in production.
    internal int PushCount { get; private set; }

    private RenderRequest? _lastRequest;
    private GaugePushKey? _lastPush;
    private string? _lastTooltip;
    private bool _hover;

    // Renders content and pushes it through UpdateLayeredWindow at bounds' location. The gauge draws its own
    // fixed size (GaugeLayout), which the placement also made the window's.
    public void Render(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings, int dpi, Rectangle bounds, Color ink, string fontFamily)
    {
        _lastRequest = new RenderRequest(snapshot, now, settings, dpi, bounds, ink, fontFamily);
        Repaint();
    }

    private void Repaint()
    {
        if (_lastRequest is not { } r)
        {
            return;
        }

        bool light = r.Ink.GetBrightness() < 0.5f;
        GaugePalette palette = GaugePalette.Create(light, _accent.AccentFor(light), SystemInformation.HighContrast, r.Ink);
        GaugeContent content = GaugeContent.From(r.Snapshot, r.Now, r.Settings);
        GaugeLayout layout = GaugeLayout.For(r.Dpi, _order());
        GaugePushKey key = GaugePushKey.Of(content, palette, layout, _hover, r.FontFamily, r.Bounds.Location);
        if (key != _lastPush)
        {
            using Bitmap bitmap = GaugeRenderer.Render(content, palette, layout, _hover, r.FontFamily);
            Push(bitmap, r.Bounds.Location);
            PushCount++;
            _lastPush = key;
        }

        // The tooltip is not drawn into the bitmap, so it is kept current apart from the push: set only when its text changed
        // (setting a ToolTip again to the same text still tells the tooltip window).
        if (!string.Equals(content.Tooltip, _lastTooltip, StringComparison.Ordinal))
        {
            AccessibleDescription = content.Tooltip.Replace("\r\n", ", ", StringComparison.Ordinal);
            if (IsHandleCreated)
            {
                _tip.SetToolTip(this, content.Tooltip);
                _lastTooltip = content.Tooltip;
            }
        }
    }

    private void OnAccentChanged(object? sender, EventArgs e) => Repaint();

    private void SetHover(bool hover)
    {
        if (_hover != hover)
        {
            _hover = hover;
            Repaint();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _accent.Changed -= OnAccentChanged;
            _tip.Dispose();
        }

        base.Dispose(disposing);
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

    // The window this one is owned by, or 0. Read from the window, not remembered: when an owner window is destroyed (an
    // Explorer that ended) this window is left with no owner and reads 0, where a remembered handle would still say it
    // is owned. The controller compares it with the taskbar window there is now.
    public nint OwnerWindow => IsHandleCreated ? NativeMethods.GetWindow(Handle, NativeMethods.GW_OWNER) : 0;

    // Makes the gauge the owned window of the taskbar. "An owned window is always above its owner in the z-order": when
    // the shell raises the taskbar, the system raises the gauge with it in the same step, so there is never a frame with
    // the taskbar over the gauge and nothing for a raise to do. The gauge stays a top-level window (it is never a child
    // of the taskbar's window, which the XAML taskbar draws over). The owner may belong to another process.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#owned-windows
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw
    //
    // Owning a window of another process also joins this thread's input queue to the owner's thread. No page says so; a
    // probe on this machine showed it: the shell's GetActiveWindow then answers with this window, and a wait this thread
    // makes without pumping stalls the shell's input. Earshot must not stay joined to Explorer, so the two are separated
    // straight after the owner is set (DetachInputFrom). The z-order rule does not depend on the join: the probe held the
    // gauge above a shell window that raised itself hundreds of times, with the join undone. A gauge that cannot be
    // separated is not left owned. An owner of 0 clears the owner, and a window with no owner is joined to nothing.
    public StepOutcome SetOwner(nint owner)
    {
        const string Step = "set-window-long-ptr:gauge-owner";
        nint previous = NativeMethods.SetWindowLongPtrW(Handle, NativeMethods.GWLP_HWNDPARENT, owner);
        uint error = unchecked((uint)Marshal.GetLastPInvokeError());

        // 0 comes back both for "there was no owner before" and for a failure; only the last error tells them apart.
        if (previous == 0 && error != 0)
        {
            return StepOutcomes.FromWin32(Step, error);
        }

        if (OwnerWindow != owner)
        {
            return StepOutcomes.FromWin32(Step, 0, "The window did not take the owner.", ok: false);
        }

        return owner == 0 ? StepOutcomes.FromWin32(Step, 0) : DetachInputFrom(owner);
    }

    // AttachThreadInput, replaceable by a test that needs it to fail with a code the system does not give on demand. It
    // leaves the last error where the call it stands for would.
    internal Func<uint, uint, bool, bool> SeparateThreads = NativeMethods.AttachThreadInput;

    // Separates this window's thread from the owner's: the input queues the owner call joined. ERROR_INVALID_PARAMETER from
    // AttachThreadInput means the two were not joined (an owner on this thread, or one the system did not join), so there is
    // nothing to undo. Any other failure leaves the gauge joined to the shell's queue, so the owner is taken off again, which
    // also ends the join (probe), and the failure is reported with its raw code.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-attachthreadinput
    private StepOutcome DetachInputFrom(nint owner)
    {
        const string Step = "attach-thread-input:detach-gauge-owner";
        uint ownerThread = NativeMethods.GetWindowThreadProcessId(owner, out _);
        uint ownThread = NativeMethods.GetWindowThreadProcessId(Handle, out _);
        if (ownerThread == 0 || ownThread == 0 || ownerThread == ownThread)
        {
            return StepOutcomes.FromWin32(Step, 0);
        }

        if (SeparateThreads(ownThread, ownerThread, false))
        {
            _log.Write(LogLevel.Debug, "Gauge: the input queue was separated from the owner's thread.");
            return StepOutcomes.FromWin32(Step, 0);
        }

        const uint ErrorInvalidParameter = 87;
        uint code = unchecked((uint)Marshal.GetLastPInvokeError());
        if (code == ErrorInvalidParameter)
        {
            return StepOutcomes.FromWin32(Step, 0);
        }

        _ = NativeMethods.SetWindowLongPtrW(Handle, NativeMethods.GWLP_HWNDPARENT, 0);
        uint clearCode = unchecked((uint)Marshal.GetLastPInvokeError());
        string detail = OwnerWindow == 0
            ? "The owner was taken off again."
            : "The owner could not be taken off again (" + StepOutcomes.FromWin32(Step, clearCode).CodeName + ").";
        return StepOutcomes.FromWin32(Step, code, detail);
    }

    public void HideWindow()
    {
        if (!_shown || !IsHandleCreated)
        {
            return;
        }

        _shown = false;
        bool ok = NativeMethods.SetWindowPos(Handle, 0, 0, 0, 0, 0,
            NativeMethods.SWP_HIDEWINDOW | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
        // _shown still moves to false either way: the caller (GaugeController's own Hidden/Off transitions)
        // has already decided the gauge must not be treated as shown any more regardless of whether the
        // window itself actually disappeared, the same way ShowAt and Raise above record their own raw code
        // rather than staying silent about one (AppBarRegistration.Dispose's own ABM_REMOVE fix uses the
        // same Debug-on-success, Warn-on-failure split for exactly the same reason).
        StepOutcome outcome = StepOutcomes.FromWin32("set-window-pos:hide-gauge",
            ok ? 0 : unchecked((uint)Marshal.GetLastPInvokeError()), ok: ok);
        _log.Write(ok ? LogLevel.Debug : LogLevel.Warn, "Gauge: " + TrayReport.DescribeStep(outcome));
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
                SetHover(true);
                base.WndProc(ref m);
                return;

            case NativeMethods.WM_MOUSELEAVE:
                _tracking = false;
                SetHover(false);
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
