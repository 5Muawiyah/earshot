using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tray;

namespace Earshot.Popup;

// What the card presenter needs from the card window. ConnectCard is the window; tests supply their own.
internal interface ICardSurface : IDisposable
{
    // The user clicked the card with any mouse button.
    event EventHandler? Clicked;

    bool IsDisposed { get; }

    // Lays the card out for content at dpi, no wider than maxWidth physical pixels, and returns its size
    // in physical pixels. Nothing is shown yet.
    Size Prepare(CardContent content, int dpi, CardPalette palette, int maxWidth);

    // Moves, sizes and shows the prepared card at bounds without activating it.
    StepOutcome ShowAt(Rectangle bounds);

    // Hides the card without activating anything. Null when it was not shown, so no call was made.
    StepOutcome? HideCard();
}

// The popup card: a small borderless window with the device name on one line and a status on the next.
// There is no battery element, no placeholder and no empty slot for one: phase 0 found no battery source
// on this hardware, so the card draws those two lines and nothing else.
//
// It never takes focus or activation:
//   ShowWithoutActivation   true, in case anything ever calls Show
//   CreateParams            WS_EX_NOACTIVATE (not activated by a click, no taskbar button),
//                           WS_EX_TOOLWINDOW (not in the taskbar or Alt+Tab), WS_EX_TOPMOST
//   WM_MOUSEACTIVATE        answered MA_NOACTIVATE, so a click reaches the card and activates nothing
//   showing                 SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW), never Show()
//   topmost                 from CreateParams and HWND_TOPMOST only: the TopMost property setter calls
//                           SetWindowPos without SWP_NOACTIVATE, so it is never set
//   no focus                the form is not selectable and has no child controls
// https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
// https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.showwithoutactivation
//
// Size. The presenter reads the DPI of the display the card is going to and calls Prepare before the card
// moves there, so the text is measured for that display. Fonts are created in pixels for that DPI. When
// the move then changes the window's DPI, the WinForms rescale is cancelled, because the size is already
// right for the new display.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.ondpichanged
//
// Corners. On Windows 11 (build 22000 and later) the window asks DWM for rounded corners and a border in
// the palette's colour. Rounding is a hint; on older builds, or when DWM refuses, the card paints its own
// one pixel border instead.
// https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners
// https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
//
// UI thread only.
internal sealed class ConnectCard : Form, ICardSurface
{
    internal const int ExtendedStyles = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;

    // Type sizes in points and spacing in pixels at 96 DPI. Layout choices, scaled for the display.
    internal const float TitlePoints = 10.5f;
    internal const float StatusPoints = 9f;
    internal const int PaddingXAt96 = 16;
    internal const int PaddingYAt96 = 12;
    internal const int LineGapAt96 = 2;
    internal const int MinWidthAt96 = 240;
    internal const int MaxWidthAt96 = 560;

    // One line each, no mnemonic underscores, an ellipsis when a line is too long for the widest card.
    private const TextFormatFlags LineFormat =
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis;

    private static readonly Size Unbounded = new(int.MaxValue, int.MaxValue);

    private readonly ILog _log;
    private readonly string _fontFamily;
    private readonly List<string> _painted = new();
    private CardContent _content = new("", "");
    private CardPalette _palette = CardTheme.Light;
    private Font? _titleFont;
    private Font? _statusFont;
    private int _fontDpi;
    private Rectangle _titleBounds;
    private Rectangle _statusBounds;
    private bool _dwmFrame;
    private bool _shown;
    private string? _lastFrameProblem;

    public ConnectCard(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;

        using (Font? messageFont = SystemFonts.MessageBoxFont)
        {
            _fontFamily = messageFont?.Name ?? FontFamily.GenericSansSerif.Name;
        }

        Text = TrayStatus.AppName;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _palette.Background;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public event EventHandler? Clicked;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= ExtendedStyles;
            return cp;
        }
    }

    public Size Prepare(CardContent content, int dpi, CardPalette palette, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(palette);

        int effectiveDpi = dpi > 0 ? dpi : CardPlacement.BaseDpi;
        (Font title, Font status) = FontsFor(effectiveDpi);
        _content = content;
        _palette = palette;
        AccessibleName = content.Title;
        AccessibleDescription = content.Status;

        int padX = CardPlacement.Scale(PaddingXAt96, effectiveDpi);
        int padY = CardPlacement.Scale(PaddingYAt96, effectiveDpi);
        int gap = CardPlacement.Scale(LineGapAt96, effectiveDpi);
        Size titleText = TextRenderer.MeasureText(content.Title, title, Unbounded, LineFormat);
        Size statusText = TextRenderer.MeasureText(content.Status, status, Unbounded, LineFormat);
        int titleHeight = Math.Max(titleText.Height, title.Height);
        int statusHeight = Math.Max(statusText.Height, status.Height);

        int widest = Math.Max(1, Math.Min(CardPlacement.Scale(MaxWidthAt96, effectiveDpi), maxWidth));
        int narrowest = Math.Min(CardPlacement.Scale(MinWidthAt96, effectiveDpi), widest);
        int width = Math.Clamp(Math.Max(titleText.Width, statusText.Width) + (2 * padX), narrowest, widest);
        int textWidth = Math.Max(1, width - (2 * padX));

        _titleBounds = new Rectangle(padX, padY, textWidth, titleHeight);
        _statusBounds = new Rectangle(padX, padY + titleHeight + gap, textWidth, statusHeight);
        Size size = new(width, _statusBounds.Bottom + padY);
        if (!_shown)
        {
            // A card on screen takes its new size and position together in ShowAt.
            ClientSize = size;
        }

        Invalidate();
        return size;
    }

    public StepOutcome ShowAt(Rectangle bounds)
    {
        // Reading Handle creates the window, hidden, the first time.
        nint handle = Handle;
        ApplyBorderColour(handle);
        BackColor = _palette.Background;
        Invalidate();

        const string Step = "set-window-pos:show-card";
        if (!NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
        {
            return StepOutcomes.FromWin32(Step, unchecked((uint)Marshal.GetLastPInvokeError()), Describe(bounds));
        }

        _shown = true;

        // Paint now, so the first frame on screen is the card rather than an empty window.
        Update();
        return StepOutcomes.FromWin32(Step, 0, Describe(bounds));
    }

    public StepOutcome? HideCard()
    {
        if (!_shown || !IsHandleCreated)
        {
            return null;
        }

        _shown = false;
        if (!NativeMethods.SetWindowPos(Handle, 0, 0, 0, 0, 0,
                NativeMethods.SWP_HIDEWINDOW | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER))
        {
            return StepOutcomes.FromWin32("set-window-pos:hide-card", unchecked((uint)Marshal.GetLastPInvokeError()));
        }

        return StepOutcomes.FromWin32("set-window-pos:hide-card", 0);
    }

    // True while the card is on screen.
    internal bool IsShownOnScreen() => _shown;

    // The strings the last paint drew, in order. The card draws nothing else.
    internal IReadOnlyList<string> LastPaintedText() => _painted.ToArray();

    // Where the title and status lines are drawn, in client coordinates.
    internal Rectangle TitleLineBounds() => _titleBounds;

    internal Rectangle StatusLineBounds() => _statusBounds;

    // True when DWM rounds the corners and draws the border, so the card paints no border itself.
    internal bool HasDwmFrame() => _dwmFrame;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // Do not activate; keep the mouse message so the click still dismisses the card.
            m.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCorners(Handle);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Prepare already sized the card for the display it moved to.
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint fills the whole client area.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _painted.Clear();

        Graphics g = e.Graphics;
        g.Clear(_palette.Background);
        if (!_dwmFrame)
        {
            using var pen = new Pen(_palette.Border);
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        DrawLine(g, _content.Title, _titleFont, _titleBounds, _palette.Title);
        DrawLine(g, _content.Status, _statusFont, _statusBounds, _palette.Status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _statusFont?.Dispose();
            _titleFont = null;
            _statusFont = null;
        }

        base.Dispose(disposing);
    }

    private void DrawLine(Graphics g, string text, Font? font, Rectangle bounds, Color colour)
    {
        if (font is null || text.Length == 0)
        {
            return;
        }

        _painted.Add(text);
        TextRenderer.DrawText(g, text, font, bounds, colour, _palette.Background, LineFormat);
    }

    // The title and status fonts in pixels for dpi, reused while the DPI stays the same.
    private (Font Title, Font Status) FontsFor(int dpi)
    {
        if (_titleFont is null || _statusFont is null || _fontDpi != dpi)
        {
            _titleFont?.Dispose();
            _statusFont?.Dispose();
            _titleFont = new Font(_fontFamily, TitlePoints * dpi / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
            _statusFont = new Font(_fontFamily, StatusPoints * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontDpi = dpi;
        }

        return (_titleFont, _statusFont);
    }

    private void ApplyCorners(nint handle)
    {
        _dwmFrame = false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        int preference = Dwm.DWMWCP_ROUND;
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, in preference, sizeof(int));
        _dwmFrame = hr >= 0;
        NoteFrame(hr >= 0 ? null : StepOutcomes.FromHResult("dwm-corner-preference:card", hr), "rounded corners");
    }

    private void ApplyBorderColour(nint handle)
    {
        if (!_dwmFrame || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        // A COLORREF is 0x00BBGGRR. High contrast keeps the system border.
        int colour = _palette.HighContrast
            ? unchecked((int)Dwm.DWMWA_COLOR_DEFAULT)
            : _palette.Border.R | (_palette.Border.G << 8) | (_palette.Border.B << 16);
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_BORDER_COLOR, in colour, sizeof(int));
        NoteFrame(hr >= 0 ? null : StepOutcomes.FromHResult("dwm-border-colour:card", hr), "border colour");
    }

    // A frame attribute that failed is logged once per distinct failure; the card still shows.
    private void NoteFrame(StepOutcome? failure, string what)
    {
        if (failure is null)
        {
            return;
        }

        string problem = TrayReport.DescribeStep(failure);
        if (problem == _lastFrameProblem)
        {
            return;
        }

        _lastFrameProblem = problem;
        _log.Warn("The card " + what + " could not be set, so it keeps the plain frame: " + problem);
    }

    private static string Describe(Rectangle r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y} {r.Width}x{r.Height}");
}
