using System.Drawing.Drawing2D;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// Where the picker's parts are, in client pixels, for a display scale and the system text size. Pure: no window, no drawing.
// A header line that says where the AirPods are, a divider, then one row for each choice. A row is as high as a single-line
// settings row plus 8 (20t + 12 + 8), with 12 inside on the left for its icon and 12 between the icon and the words; the
// picker is 280 wide with 8 round the rows. Design choices in the card's own style, not measurements.
internal static class SendToLayout
{
    public const int WidthAt96 = 280;
    public const int PaddingAt96 = 8;
    public const int RowGapAt96 = 2;
    public const int RowExtraAt96 = 8;
    public const int InsideAt96 = 12;
    public const int IconGapAt96 = 12;
    public const int HeaderGapAt96 = 4;

    internal sealed record Layout(int Width, int Height, Rectangle Header, Rectangle HeaderIcon, Rectangle HeaderText, int DividerY, IReadOnlyList<RowLayout> Rows);

    internal sealed record RowLayout(Rectangle Bounds, Rectangle Icon, Rectangle Label, Rectangle Tick);

    public static Layout Compute(int dpi, int rowCount, double textScale)
    {
        int width = CardPlacement.Scale(WidthAt96, dpi);
        int pad = CardPlacement.Scale(PaddingAt96, dpi);
        int gap = CardPlacement.Scale(RowGapAt96, dpi);
        int inside = CardPlacement.Scale(InsideAt96, dpi);
        int iconGap = CardPlacement.Scale(IconGapAt96, dpi);
        int line = WidgetCardLayout.RowHeight(dpi, textScale);
        int rowHeight = line + CardPlacement.Scale(RowExtraAt96, dpi);
        int box = TextFit.Grow(CardPaint.GlyphSizeAt96, dpi, textScale);

        var header = new Rectangle(pad, pad, width - (2 * pad), line);
        var headerIcon = new Rectangle(header.X + inside, header.Y + ((header.Height - box) / 2), box, box);
        int headerTextX = headerIcon.Right + iconGap;
        var headerText = new Rectangle(headerTextX, header.Y, Math.Max(1, header.Right - inside - headerTextX), header.Height);
        int dividerY = header.Bottom + CardPlacement.Scale(HeaderGapAt96, dpi);

        var rows = new List<RowLayout>(rowCount);
        int y = dividerY + CardPlacement.Scale(HeaderGapAt96, dpi);
        for (int i = 0; i < rowCount; i++)
        {
            var bounds = new Rectangle(pad, y, width - (2 * pad), rowHeight);
            var icon = new Rectangle(bounds.X + inside, bounds.Y + ((bounds.Height - box) / 2), box, box);
            var tick = new Rectangle(bounds.Right - inside - box, icon.Y, box, box);
            int labelX = icon.Right + iconGap;
            var label = new Rectangle(labelX, bounds.Y, Math.Max(1, tick.X - iconGap - labelX), bounds.Height);
            rows.Add(new RowLayout(bounds, icon, label, tick));
            y = bounds.Bottom + gap;
        }

        int height = (rowCount > 0 ? rows[^1].Bounds.Bottom : dividerY) + pad;
        return new Layout(width, height, header, headerIcon, headerText, dividerY, rows);
    }
}

// The send-to picker: where the AirPods are now, and a row for each place to send them. A small borderless window in the
// card's own style (the design tokens, the accent, the type ramp and the icon font), shown above the gauge and closed by a
// choice, Escape or a click anywhere else. It takes focus, so the keyboard works: Up and Down move among the rows, Enter and
// Space press the one with the focus, Escape closes it.
//
// It only reports which row was pressed (Chosen). What a row does is the tray's (SendToActions): a press here never reaches
// the device itself.
internal sealed class SendToPicker : Form
{
    private readonly ILog _log;
    private readonly Func<SystemLook> _readLook;
    private IAccentColours? _accent;
    private SendToModel _model = SendToModel.From(AirPodsWhere.Unknown, "", busy: false);
    private SendToLayout.Layout _layout = SendToLayout.Compute(CardPlacement.BaseDpi, 3, 1.0);
    private int _dpi = CardPlacement.BaseDpi;
    private CardPalette _palette = CardTheme.Light;
    private bool _dark;
    private CardType _type = CardType.Plain;
    private SystemLook _look = SystemLook.Default;
    private int _focus;
    private int _hot = -1;
    private int _pressed = -1;
    private bool _focusCue;
    private bool _dwmFrame;
    private bool _finished;
    private string? _lastFrameProblem;

    public SendToPicker(ILog log, IAccentColours accent, Func<SystemLook>? readLook = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(accent);
        _log = log;
        _readLook = readLook ?? (static () => SystemLook.Default);
        _accent = accent;
        Text = SendToCopy.WindowName;
        AccessibleName = SendToCopy.WindowName;
        AccessibleRole = AccessibleRole.Window;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _palette.Background;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        accent.Changed += OnAccentChanged;
    }

    // A row was pressed. Raised once, then the picker closes itself.
    public event EventHandler<SendToChoice>? Chosen;

    // The picker went away with no choice: Escape, or another window took the focus.
    public event EventHandler? Dismissed;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    internal SendToModel Model => _model;

    internal SendToLayout.Layout CurrentLayout => _layout;

    internal int FocusIndex => _focus;

    internal int HotIndex => _hot;

    internal bool FocusCueShown => _focusCue;

    // Takes the theme from the taskbar ink, as the card does, so the picker agrees with it.
    public void SetTheme(Color taskbarInk, bool highContrast)
    {
        _dark = !highContrast && taskbarInk.GetBrightness() >= 0.5f;
        _palette = CardTheme.For(highContrast, taskbarInk, SystemColors.Window, SystemColors.WindowText);
    }

    // Lays the picker out for model at dpi and returns its size in pixels. Nothing is shown.
    public Size Prepare(SendToModel model, int dpi, bool openedByKeyboard)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _dpi = dpi > 0 ? dpi : CardPlacement.BaseDpi;
        _look = _readLook().Clamped();
        _type = new CardType(_dpi, _look.TextScale);
        _layout = SendToLayout.Compute(_dpi, model.Rows.Count, _look.TextScale);
        _focus = FirstActing(0, +1) ?? 0;
        _hot = -1;
        _pressed = -1;
        _focusCue = openedByKeyboard;
        BackColor = Colours.Tokens.SolidSurface;
        ClientSize = new Size(_layout.Width, _layout.Height);
        Invalidate();
        return ClientSize;
    }

    // Shows the picker at bounds and gives it the focus.
    public void PresentAt(Rectangle bounds)
    {
        _ = Handle;
        Bounds = bounds;
        Show();
        Activate();
    }

    private CardColours Colours => CardColours.For(_dark, _palette, _accent?.AccentFor(lightTheme: !_dark) ?? DefaultCardAccent.Instance.AccentFor(!_dark));

    // Where the AirPods are now and each row, as a screen reader and the keyboard see them.
    internal IReadOnlyList<CardControl> CurrentControls()
    {
        var list = new List<CardControl>
        {
            new(SendToCopy.WindowName + ": " + _model.Where, CardControlRole.StaticText, _layout.Header, null, false, false, false, true, static () => { }),
        };
        for (int i = 0; i < _model.Rows.Count && i < _layout.Rows.Count; i++)
        {
            int index = i;
            SendToRow row = _model.Rows[i];
            list.Add(new CardControl(
                SendToCopy.RowName(row.Label, row.Current), CardControlRole.PushButton, _layout.Rows[i].Bounds, null, false, row.Current,
                ContainsFocus && _focus == i, row.Enabled, () => Choose(index)));
        }

        return list;
    }

    private void Choose(int index)
    {
        if (_finished || index < 0 || index >= _model.Rows.Count || !_model.Rows[index].Acts)
        {
            return;
        }

        _finished = true;
        SendToChoice choice = _model.Rows[index].Choice;
        Hide();
        Chosen?.Invoke(this, choice);
    }

    private void Dismiss()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    // The next row, in the direction given, that a press would act on; null when there is none.
    private int? FirstActing(int from, int step)
    {
        int count = _model.Rows.Count;
        for (int i = 0; i < count; i++)
        {
            int index = (((from + (i * step)) % count) + count) % count;
            if (_model.Rows[index].Acts)
            {
                return index;
            }
        }

        return null;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape or Keys.Space or Keys.Tab or (Keys.Tab | Keys.Shift) or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _focusCue = true;
        int count = _model.Rows.Count;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Dismiss();
                e.Handled = true;
                return;
            case Keys.Down:
            case Keys.Tab when !e.Shift:
                _focus = FirstActing(_focus + 1, +1) ?? _focus;
                break;
            case Keys.Up:
            case Keys.Tab when e.Shift:
                _focus = FirstActing(_focus - 1, -1) ?? _focus;
                break;
            case Keys.Home:
                _focus = FirstActing(0, +1) ?? _focus;
                break;
            case Keys.End:
                _focus = FirstActing(count - 1, -1) ?? _focus;
                break;
            case Keys.Enter:
            case Keys.Space:
                Choose(_focus);
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
        Invalidate();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Location);
        if (row != _hot)
        {
            _hot = row;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != -1)
        {
            _hot = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left ? RowAt(e.Location) : -1;
        _focusCue = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int down = _pressed;
        _pressed = -1;
        if (e.Button == MouseButtons.Left && down >= 0 && RowAt(e.Location) == down)
        {
            _focus = down;
            Choose(down);
        }
        else
        {
            Invalidate();
        }
    }

    private int RowAt(Point point)
    {
        for (int i = 0; i < _layout.Rows.Count; i++)
        {
            if (_layout.Rows[i].Bounds.Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Dismiss();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Prepare already laid the picker out for the display it is going to.
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCorners(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint fills the whole client area.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        RenderContent(e.Graphics);
    }

    // The picker's whole content, apart from the window, so a capture can paint it into a bitmap with no window shown.
    internal void RenderContent(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = CardPaint.CardTextHint;
        CardColours colours = Colours;
        g.Clear(colours.Tokens.SolidSurface);
        if (!_dwmFrame)
        {
            using var pen = new Pen(colours.SurfaceStroke);
            g.DrawRectangle(pen, 0, 0, _layout.Width - 1, _layout.Height - 1);
        }

        // Where the AirPods are now.
        char headerGlyph = _model.WhereGlyph == FluentGlyphs.Earbud ? FluentGlyphs.EarbudForThisPc() : _model.WhereGlyph;
        _ = CardPaint.TryGlyph(g, headerGlyph, _layout.HeaderIcon, colours.TextSecondary, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale);
        CardPaint.Text(g, _model.Where, _layout.HeaderText, _type, CardPlacement.Scale(12, _dpi), bold: false, colours.TextSecondary, StringAlignment.Near, StringAlignment.Center);
        CardPaint.Divider(g, _layout.Header.Left, _layout.Header.Right, _layout.DividerY, colours);

        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);
        for (int i = 0; i < _model.Rows.Count && i < _layout.Rows.Count; i++)
        {
            SendToRow row = _model.Rows[i];
            SendToLayout.RowLayout at = _layout.Rows[i];
            bool pressed = i == _pressed && i == _hot;
            if (row.Acts && (i == _hot || pressed))
            {
                using GraphicsPath path = CardPaint.RoundedRectangle(
                    new RectangleF(at.Bounds.X + 0.5f, at.Bounds.Y + 0.5f, at.Bounds.Width - 1, at.Bounds.Height - 1), radius);
                using var fill = new SolidBrush(pressed ? colours.SubtlePressed : colours.SubtleHover);
                g.FillPath(fill, path);
            }

            Color ink = row.Enabled ? colours.Text : colours.TextDisabled;
            _ = CardPaint.TryGlyph(g, row.Glyph, at.Icon, ink, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale);
            CardPaint.Text(g, row.Label, at.Label, _type, CardPlacement.Scale(14, _dpi), bold: false, ink, StringAlignment.Near, StringAlignment.Center);
            if (row.Current)
            {
                _ = CardPaint.TryGlyph(g, FluentGlyphs.CheckMark, at.Tick, colours.Accent, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale);
            }

            if (_focusCue && i == _focus && ContainsFocus)
            {
                CardPaint.Focus(g, at.Bounds, radius, colours, _dpi);
            }
        }
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        if (Visible)
        {
            Invalidate();
        }
    }

    // Windows 11 rounds the corners and draws the border when asked; where it will not, the picker paints a plain one.
    // https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners
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
        if (hr < 0)
        {
            string problem = TrayReport.DescribeStep(StepOutcomes.FromHResult("dwm-corner-preference:send-to", hr));
            if (problem != _lastFrameProblem)
            {
                _lastFrameProblem = problem;
                _log.Warn("The send-to picker's rounded corners could not be set, so it keeps the plain frame: " + problem);
            }
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PickerAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _accent is { } accent)
        {
            accent.Changed -= OnAccentChanged;
            _accent = null;
        }

        base.Dispose(disposing);
    }

    private sealed class PickerAccessibleObject(SendToPicker picker) : ControlAccessibleObject(picker)
    {
        public override int GetChildCount() => picker.CurrentControls().Count;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < picker.CurrentControls().Count ? new RowAccessibleObject(picker, this, index) : null;
    }

    private sealed class RowAccessibleObject(SendToPicker picker, AccessibleObject parent, int index) : AccessibleObject
    {
        private CardControl? Target
        {
            get
            {
                IReadOnlyList<CardControl> targets = picker.CurrentControls();
                return index < targets.Count ? targets[index] : null;
            }
        }

        public override AccessibleObject Parent => parent;

        public override string? Name
        {
            get => Target?.Name;
            set { }
        }

        public override AccessibleRole Role => Target?.Role == CardControlRole.StaticText ? AccessibleRole.StaticText : AccessibleRole.PushButton;

        public override AccessibleStates State
        {
            get
            {
                if (Target is not { } target)
                {
                    return AccessibleStates.Invisible;
                }

                AccessibleStates state = target.Role == CardControlRole.StaticText ? AccessibleStates.ReadOnly : AccessibleStates.Focusable;
                if (target.Focused)
                {
                    state |= AccessibleStates.Focused;
                }

                if (target.Checked)
                {
                    state |= AccessibleStates.Checked;
                }

                if (!target.Enabled)
                {
                    state |= AccessibleStates.Unavailable;
                }

                return state;
            }
        }

        public override Rectangle Bounds => Target is { } target ? picker.RectangleToScreen(target.Bounds) : Rectangle.Empty;

        public override string? DefaultAction => Target?.Role == CardControlRole.StaticText ? null : "Press";

        public override void DoDefaultAction()
        {
            if (Target is { Enabled: true } target)
            {
                target.Activate();
            }
        }
    }
}
