using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// Which of the card's two focusable items has the keyboard focus. There are no child controls, so focus is
// tracked here and painted as the system focus rectangle.
internal enum WidgetCardFocus { Button, Switch }

// Why the card asked to be hidden. WidgetCardPresenter uses Deactivated to run the toggle-close rule: a
// second gauge click within SystemInformation.DoubleClickTime of a deactivate-close does not reopen it, the
// same gesture as a second click on the volume flyout's own icon. The other reasons it simply hides for.
//
// ClickOutside is notice mode only: a click that landed on the card but missed both the button and the
// switch. A notice-mode card is never activated (WS_EX_NOACTIVATE), so it never deactivates either; this
// is its only way to notice "the owner clicked past it".
internal enum WidgetCardCloseReason { Deactivated, Escape, Action, ClickOutside }

// Everything the card draws, handed in by WidgetCardPresenter on every show and every refresh. Immutable,
// so a paint never races a concurrent update.
internal sealed record WidgetCardModel(
    WidgetSnapshot Snapshot,
    bool AutoPauseOn,
    bool ShowSwitch,
    bool ConnectIntent,     // true: the button reads Connect; false: Disconnect
    bool ButtonEnabled,     // false while TrayContext.IsBusy or the link is changing
    string OtherDeviceLabel,
    DateTimeOffset Now)
{
    public static WidgetCardModel Empty { get; } = new(
        WidgetSnapshot.Empty(WidgetWatcherState.NotStarted, claimExists: false),
        AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true, ButtonEnabled: true,
        OtherDeviceLabel: "", Now: DateTimeOffset.UtcNow);
}

// The dedicated three-column card: Left, Right, Case battery, where the AirPods are, when the battery was
// last read, and a Connect/Disconnect button, plus the auto-pause switch when the snapshot says it is
// available. Built for the gauge's left click with LeftClickConnects off (the default). See the report on
// TrayContext.Widget.OnWidgetCardRequested for why an earlier pass reused ConnectCard/CardPresenter (a
// one-line card) instead of this.
//
// Owner-painted, no child controls, one Form: OnPaintBackground is empty, OnPaint starts with
// Graphics.Clear and draws everything else with GDI+ FillPath/DrawString, never TextRenderer: GDI text
// writes alpha 0, invisible on a window composited through alpha the way this card's translucent backdrop
// is (https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows), the same
// fact ConnectCard and GaugeRenderer already build on. OptimizedDoubleBuffer stays off so a buffered blit
// never throws away the alpha the DWM recipe needs.
//
// Unlike ConnectCard this card takes focus and keyboard input (shown with Show() then Activate(), like the
// volume flyout), so CreateParams omits WS_EX_NOACTIVATE and ControlStyles.Selectable is left at its
// default (true) rather than turned off.
//
// notice: true is the case-open card (CaseOpenCardPresenter), its own instance, never shared with the
// gauge-anchored card: WS_EX_NOACTIVATE is set once in CreateParams and a style set at creation is not
// toggled at run time, so the two modes cannot be the same live window. Everything notice mode needs beyond
// CreateParams/WndProc/ShowWithoutActivation (already wired for it before this) is guarded on the _notice
// field directly, in place: the Where line reads "Case open" always (OnPaint), Enter/Escape/Tab do nothing
// (OnKeyDown), no focus rectangle is ever painted (DrawButton/DrawSwitch), and a click that misses both the
// button and the switch closes it (OnMouseUp) since a notice-mode card is never activated and so never
// deactivates either.
internal sealed class WidgetCard : Form
{
    // Design choice, not a measurement: a system-ish accent for the Connect state, not a read of any
    // Windows API or system accent colour.
    internal static readonly Color AccentBlue = Color.FromArgb(0x60, 0xCD, 0xFF);

    private readonly ILog _log;
    private readonly bool _notice;
    private readonly string _fontFamily;
    private WidgetCardModel _model = WidgetCardModel.Empty;
    private int _dpi = CardPlacement.BaseDpi;
    private CardPalette _palette = CardTheme.Light;
    private bool _dark;
    private bool _dwmBackdropOk;
    private bool _cornersApplied;
    private WidgetCardFocus _focus = WidgetCardFocus.Button;
    private string? _lastFrameProblem;
    private bool _leftButtonDownOnButton;
    private bool _leftButtonDownOnSwitch;

    public WidgetCard(ILog log, bool notice = false)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _notice = notice;

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
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
    }

    // The user pressed the focused Connect/Disconnect button (Enter, Space or a mouse click on it).
    public event EventHandler? ToggleRequested;

    // The user toggled the switch (Enter, Space or a mouse click on it), to the new value.
    public event EventHandler<bool>? AutoPauseChanged;

    // The card wants to be hidden. WidgetCardPresenter hides it (it may already be hidden by the time this
    // is observed: the card hides itself first, see RequestClose) and does its own bookkeeping.
    public event EventHandler<WidgetCardCloseReason>? CloseRequested;

    protected override bool ShowWithoutActivation => _notice;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            if (_notice)
            {
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE;
            }

            return cp;
        }
    }

    // The last model this card was told to draw, for tests.
    internal WidgetCardModel Model => _model;

    internal WidgetCardFocus FocusTarget => _focus;

    // The location line OnPaint is about to draw: the live Where reading, or always "Case open" for a
    // notice-mode instance regardless of what the model's own Snapshot.Where says (spec 7.6). For tests.
    internal string WhereLineText => _notice ? WidgetCopy.CaseOpen : WidgetCopy.Where(_model.Snapshot.Where, _model.OtherDeviceLabel);

    // True once DWM accepted the translucent backdrop and DwmExtendFrameIntoClientArea for this window's
    // life; false means the opaque palette paint is used instead.
    internal bool HasTranslucentBackdrop => _dwmBackdropOk;

    // True once DWMWA_WINDOW_CORNER_PREFERENCE succeeded for this window. False either because the OS is
    // below build 22000 (never asked) or because DWM refused it.
    internal bool CornersApplied => _cornersApplied;

    // Sets the palette and dark-mode flag from the taskbar ink, the same reading the tray icon and
    // ConnectCard use, so the card always agrees with them. Call before the first Render after the handle
    // exists so OnHandleCreated's dark-mode attribute is set from the right value on the next paint; a
    // later change is picked up by the next Render, but DWMWA_USE_IMMERSIVE_DARK_MODE itself is only
    // re-applied when the handle is recreated, matching ConnectCard's own border-colour behaviour.
    public void SetTheme(Color taskbarInk, bool highContrast)
    {
        _dark = !highContrast && taskbarInk.GetBrightness() >= 0.5f;
        _palette = CardTheme.For(highContrast, taskbarInk, SystemColors.Window, SystemColors.WindowText);
        if (IsHandleCreated)
        {
            ApplyDarkMode(Handle);
        }
    }

    // Lays the card out for dpi and stores model for the next paint. Does not show, move or size the
    // window: the presenter reads WidgetCardLayout.Compute itself to place the card, then sets Bounds.
    public void Render(WidgetCardModel model, int dpi)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _dpi = dpi > 0 ? dpi : CardPlacement.BaseDpi;
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(_dpi, model.ShowSwitch);
        if (_focus == WidgetCardFocus.Switch && !model.ShowSwitch)
        {
            // The switch just disappeared under the focus; move it back to the button rather than leave
            // focus pointed at a row that no longer draws.
            _focus = WidgetCardFocus.Button;
        }

        ClientSize = new Size(layout.Width, layout.Height);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCorners(Handle);
        ApplyDarkMode(Handle);
        ApplyBackdrop(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (_notice && m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_notice)
        {
            RequestClose(WidgetCardCloseReason.Deactivated);
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Tab:
            case Keys.Tab | Keys.Shift:
            case Keys.Enter:
            case Keys.Escape:
            case Keys.Space:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_notice)
        {
            // Spec 7.6: "No focus, no focus rectangle, keyboard does nothing (nothing has focus)." A
            // notice-mode card is never activated, so it should never receive a key in practice; this
            // guard makes that true even if a key event ever reached it regardless.
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Tab:
                MoveFocus();
                e.Handled = true;
                break;
            case Keys.Escape:
                RequestClose(WidgetCardCloseReason.Escape);
                e.Handled = true;
                break;
            case Keys.Enter:
            case Keys.Space:
                ActivateFocused();
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    // Tracks where a left button-down landed, so OnMouseUp below can require the up to land on the same
    // control before it activates anything: any other button (right, middle) never sets either flag, so
    // its own up can never activate the button or the switch either.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ArgumentNullException.ThrowIfNull(e);
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(_dpi, _model.ShowSwitch);
        _leftButtonDownOnButton = e.Button == MouseButtons.Left && layout.Button.Contains(e.Location);
        _leftButtonDownOnSwitch = e.Button == MouseButtons.Left && layout.ShowSwitch && layout.Switch.Contains(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ArgumentNullException.ThrowIfNull(e);
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(_dpi, _model.ShowSwitch);

        // A left up activates a control only when the matching left down already landed on that same
        // control: a drag that started outside the button or the switch and released inside it, or any
        // up from a button other than left (right, middle), must not count as pressing it, the same rule
        // GaugeWindow's own left-click handling already applies.
        bool activatesButton = e.Button == MouseButtons.Left && _leftButtonDownOnButton && layout.Button.Contains(e.Location);
        bool activatesSwitch = e.Button == MouseButtons.Left && _leftButtonDownOnSwitch && layout.ShowSwitch && layout.Switch.Contains(e.Location);
        _leftButtonDownOnButton = false;
        _leftButtonDownOnSwitch = false;

        if (activatesButton)
        {
            _focus = WidgetCardFocus.Button;
            ActivateFocused();
        }
        else if (activatesSwitch)
        {
            _focus = WidgetCardFocus.Switch;
            ActivateFocused();
        }
        else if (_notice)
        {
            // Spec 7.6: "dismissed by a click outside the two buttons." Only reachable in notice mode: the
            // normal card already closes on deactivation for a click anywhere else.
            RequestClose(WidgetCardCloseReason.ClickOutside);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint fills the whole client area.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(_dwmBackdropOk ? Color.FromArgb(0, 0, 0, 0) : _palette.Background);

        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(_dpi, _model.ShowSwitch);
        DrawEarbudColumn(g, layout.Left, _model.Snapshot.Left, mirror: false);
        DrawEarbudColumn(g, layout.Right, _model.Snapshot.Right, mirror: true);
        DrawCaseColumn(g, layout.Case, _model.Snapshot.Case);

        DrawLine(g, layout.WhereLine, WhereLineText, _palette.Status);
        DrawLine(g, layout.ReadLine, WidgetCopy.BatteryReadLine(_model.Snapshot.BatteryReadAt, _model.Now), _palette.Status);
        DrawButton(g, layout.Button);
        if (layout.ShowSwitch)
        {
            DrawSwitch(g, layout.Switch);
        }
    }

    private void MoveFocus()
    {
        _focus = _model.ShowSwitch && _focus == WidgetCardFocus.Button ? WidgetCardFocus.Switch : WidgetCardFocus.Button;
        Invalidate();
    }

    private void ActivateFocused()
    {
        if (_focus == WidgetCardFocus.Button)
        {
            if (!_model.ButtonEnabled)
            {
                return;
            }

            ToggleRequested?.Invoke(this, EventArgs.Empty);
            RequestClose(WidgetCardCloseReason.Action);
        }
        else if (_model.ShowSwitch)
        {
            AutoPauseChanged?.Invoke(this, !_model.AutoPauseOn);
        }
    }

    private void RequestClose(WidgetCardCloseReason reason)
    {
        if (Visible)
        {
            Hide();
        }

        CloseRequested?.Invoke(this, reason);
    }

    private void ApplyCorners(nint handle)
    {
        _cornersApplied = false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        int preference = Dwm.DWMWCP_ROUND;
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, in preference, sizeof(int));
        _cornersApplied = hr >= 0;
        NoteFrame(hr >= 0 ? null : StepOutcomes.FromHResult("dwm-corner-preference:widget-card", hr), "rounded corners");
    }

    private void ApplyDarkMode(nint handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        int dark = _dark ? 1 : 0;
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, in dark, sizeof(int));
        NoteFrame(hr >= 0 ? null : StepOutcomes.FromHResult("dwm-dark-mode:widget-card", hr), "dark mode");
    }

    // DWMWA_SYSTEMBACKDROP_TYPE plus DwmExtendFrameIntoClientArea with every margin -1: the documented
    // recipe that makes the translucent material fill the window when the client is painted with alpha 0.
    // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea
    // Either call failing, or the OS being below build 22621, falls back to the opaque CardTheme palette
    // for this window's life; each distinct failure is logged once.
    private void ApplyBackdrop(nint handle)
    {
        _dwmBackdropOk = false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        int type = Dwm.DWMSBT_TRANSIENTWINDOW;
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_SYSTEMBACKDROP_TYPE, in type, sizeof(int));
        if (hr < 0)
        {
            NoteFrame(StepOutcomes.FromHResult("dwm-backdrop-type:widget-card", hr), "translucent backdrop");
            return;
        }

        MARGINS margins = MARGINS.Full;
        int extendHr = Dwm.DwmExtendFrameIntoClientArea(handle, in margins);
        if (extendHr < 0)
        {
            NoteFrame(StepOutcomes.FromHResult("dwm-extend-frame:widget-card", extendHr), "translucent backdrop");
            return;
        }

        _dwmBackdropOk = true;
    }

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
        _log.Warn("The widget card " + what + " could not be set, so it keeps the plain frame: " + problem);
    }

    // A single earbud (a round head over a short stem) in bounds, mirrored horizontally for the right ear.
    // Adapted from the head/stem proportions Icon\EarbudGlyph.cs uses for the tray icon's pair; here one bud
    // is filled directly with GDI+ FillPath, matching 7.3's painting model, rather than composited through
    // EarbudGlyph's alpha buffer (built for the layered gauge's UpdateLayeredWindow, not an owner-painted
    // Form).
    private static GraphicsPath BudGlyphPath(Rectangle bounds, bool mirror)
    {
        var path = new GraphicsPath();
        float w = bounds.Width;
        float h = bounds.Height;
        float headSize = w * 0.6f;
        float headOffsetX = mirror ? w - headSize : 0f;
        var head = new RectangleF(bounds.X + headOffsetX, bounds.Y, headSize, headSize);
        path.AddEllipse(head);

        float stemWidth = Math.Max(2f, w * 0.22f);
        float stemHeight = h * 0.45f;
        float stemCentreX = mirror
            ? bounds.X + headOffsetX + (headSize * 0.30f)
            : bounds.X + headOffsetX + (headSize * 0.70f);
        var stem = new RectangleF(stemCentreX - (stemWidth / 2f), bounds.Y + (headSize * 0.55f), stemWidth, stemHeight);
        AddRoundedRect(path, stem, stemWidth / 2f);
        return path;
    }

    // The case as a rounded rectangle with a lid line: drawn as two filled pieces (the lid, then the box)
    // with a thin unpainted gap between them, which reads as the seam without needing a second colour.
    private static (GraphicsPath Lid, GraphicsPath Box) CaseGlyphPaths(Rectangle bounds)
    {
        float w = bounds.Width;
        float h = bounds.Height;
        float gap = Math.Max(1f, h * 0.08f);
        float lidHeight = h * 0.22f;
        var lidRect = new RectangleF(bounds.X, bounds.Y, w, lidHeight);
        var boxRect = new RectangleF(bounds.X, bounds.Y + lidHeight + gap, w, Math.Max(0f, h - lidHeight - gap));

        var lid = new GraphicsPath();
        AddRoundedRect(lid, lidRect, Math.Min(lidRect.Width, lidRect.Height) * 0.4f);
        var box = new GraphicsPath();
        AddRoundedRect(box, boxRect, Math.Min(boxRect.Width, boxRect.Height) * 0.25f);
        return (lid, box);
    }

    private static void AddRoundedRect(GraphicsPath path, RectangleF rect, float radius)
    {
        float r = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
        if (r <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            if (rect.Width > 0 && rect.Height > 0)
            {
                path.AddRectangle(rect);
            }

            return;
        }

        float d = r * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }

    private void DrawEarbudColumn(Graphics g, WidgetCardLayout.ColumnLayout column, PartReading part, bool mirror)
    {
        using GraphicsPath glyph = BudGlyphPath(column.Glyph, mirror);
        using (var brush = new SolidBrush(_palette.Title))
        {
            g.FillPath(brush, glyph);
        }

        if (part.InEar == true)
        {
            DrawInEarMark(g, column.Glyph, mirror);
        }

        DrawBatteryPart(g, column, part);
    }

    private void DrawCaseColumn(Graphics g, WidgetCardLayout.ColumnLayout column, PartReading part)
    {
        (GraphicsPath lid, GraphicsPath box) = CaseGlyphPaths(column.Glyph);
        using (lid)
        using (box)
        using (var brush = new SolidBrush(_palette.Title))
        {
            g.FillPath(brush, lid);
            g.FillPath(brush, box);
        }

        DrawBatteryPart(g, column, part);
    }

    // The bar, percent text and charging bolt: absent entirely (not even the percent text) when Percent is
    // null, so the column shows the glyph only. No dash, no placeholder number ever stands in for a
    // reading that was never taken.
    private void DrawBatteryPart(Graphics g, WidgetCardLayout.ColumnLayout column, PartReading part)
    {
        if (part.Percent is not { } percent)
        {
            return;
        }

        using (var track = new SolidBrush(Color.FromArgb(64, _palette.Title)))
        {
            g.FillRectangle(track, column.Bar);
        }

        int filled = (int)Math.Round(column.Bar.Width * Math.Clamp(percent, 0, 100) / 100.0);
        if (filled > 0)
        {
            using var fill = new SolidBrush(_palette.Title);
            g.FillRectangle(fill, column.Bar.X, column.Bar.Y, filled, column.Bar.Height);
        }

        if (part.Charging == true)
        {
            DrawBolt(g, column.Bar);
        }

        using var font = new Font(_fontFamily, column.Percent.Height * 0.6f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(_palette.Status);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        g.DrawString(WidgetCopy.Percent(percent), font, textBrush, new RectangleF(column.Percent.X, column.Percent.Y, column.Percent.Width, column.Percent.Height), format);
    }

    // A small filled arc beside the bud, on the side away from the mirrored head so it never overlaps it.
    private void DrawInEarMark(Graphics g, Rectangle glyphBounds, bool mirror)
    {
        float d = glyphBounds.Width * 0.22f;
        float x = mirror ? glyphBounds.Left - (d * 0.2f) : glyphBounds.Right - (d * 0.8f);
        float y = glyphBounds.Bottom - d;
        using var brush = new SolidBrush(_palette.Title);
        g.FillEllipse(brush, x, y, d, d);
    }

    // A five-point lightning bolt at the bar's end. Layout choice, matching GaugeRenderer's own bolt shape.
    private void DrawBolt(Graphics g, Rectangle bar)
    {
        float h = bar.Height * 2.2f;
        float w = h * 0.6f;
        float x = bar.Right + 2;
        float y = bar.Y + (bar.Height / 2f) - (h / 2f);
        PointF[] points =
        [
            new PointF(x + (w * 0.55f), y),
            new PointF(x, y + (h * 0.6f)),
            new PointF(x + (w * 0.42f), y + (h * 0.6f)),
            new PointF(x + (w * 0.45f), y + h),
            new PointF(x + w, y + (h * 0.38f)),
            new PointF(x + (w * 0.58f), y + (h * 0.38f)),
        ];
        using var brush = new SolidBrush(_palette.Title);
        g.FillPolygon(brush, points);
    }

    private void DrawLine(Graphics g, Rectangle bounds, string text, Color colour)
    {
        using var font = new Font(_fontFamily, bounds.Height * 0.62f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, bounds, format);
    }

    private void DrawButton(Graphics g, Rectangle rect)
    {
        bool connect = _model.ConnectIntent;
        Color fill = connect
            ? AccentBlue
            : (_dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xE4, 0xE4, 0xE4));
        if (!_model.ButtonEnabled)
        {
            fill = Color.FromArgb(120, fill);
        }

        Color text = connect ? Color.Black : _palette.Title;
        using var path = new GraphicsPath();
        AddRoundedRect(path, rect, rect.Height / 2f);
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        using (var font = new Font(_fontFamily, rect.Height * 0.45f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var textBrush = new SolidBrush(text))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString(connect ? WidgetCopy.Connect : WidgetCopy.Disconnect, font, textBrush, rect, format);
        }

        if (!_notice && _focus == WidgetCardFocus.Button && ContainsFocus)
        {
            DrawFocusRectangle(g, rect);
        }
    }

    private void DrawSwitch(Graphics g, Rectangle rect)
    {
        int trackWidth = (int)(rect.Height * 1.8);
        var labelRect = new Rectangle(rect.X, rect.Y, Math.Max(0, rect.Width - trackWidth - 8), rect.Height);
        DrawLine(g, labelRect, WidgetCopy.AutoPauseSwitch, _palette.Status);

        var track = new Rectangle(rect.Right - trackWidth, rect.Y + ((rect.Height - (rect.Height * 3 / 5)) / 2), trackWidth, rect.Height * 3 / 5);
        using (var trackPath = new GraphicsPath())
        {
            AddRoundedRect(trackPath, track, track.Height / 2f);
            Color trackColour = _model.AutoPauseOn
                ? AccentBlue
                : (_dark ? Color.FromArgb(0x55, 0x55, 0x55) : Color.FromArgb(0xC8, 0xC8, 0xC8));
            using var trackBrush = new SolidBrush(trackColour);
            g.FillPath(trackBrush, trackPath);
        }

        int knobDiameter = Math.Max(1, track.Height - 4);
        int knobX = _model.AutoPauseOn ? track.Right - knobDiameter - 2 : track.X + 2;
        using (var knobBrush = new SolidBrush(Color.White))
        {
            g.FillEllipse(knobBrush, knobX, track.Y + 2, knobDiameter, knobDiameter);
        }

        if (!_notice && _focus == WidgetCardFocus.Switch && ContainsFocus)
        {
            DrawFocusRectangle(g, rect);
        }
    }

    private void DrawFocusRectangle(Graphics g, Rectangle rect)
    {
        using var pen = new Pen(_palette.Title) { DashStyle = DashStyle.Dot };
        Rectangle inset = Rectangle.Inflate(rect, -1, -1);
        g.DrawRectangle(pen, inset);
    }
}
