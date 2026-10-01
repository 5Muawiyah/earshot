using System.Drawing.Drawing2D;
using Earshot.Popup;

namespace Earshot.Widget;

// The settings page of the card: its state, its keys and mouse, and its painting. The page is owner-painted like
// the rest of the card, so the text box and the shortcut boxes are drawn here and edited through the card's own
// key handling; there is no child control. Every change is raised as a SettingChange and applied by the presenter,
// which then draws the page again from the saved values.
internal sealed partial class WidgetCard
{
    private SettingsLayout? _settingsLayout;
    private SettingsTarget _settingsFocus = new(SettingsRowId.None, SettingsPart.Back);
    private SettingsTarget? _leftButtonDownOnSettingsTarget;
    private bool _editingText;
    private string _textBuffer = string.Empty;
    private int _caret;
    private SettingsRowId? _capturing;

    // The user changed a row of the settings page.
    public event EventHandler<SettingChange>? SettingChanged;

    // The settings layout the last Render computed, for tests.
    internal SettingsLayout? CurrentSettingsLayout => _settingsLayout;

    internal SettingsTarget SettingsFocusTarget => _settingsFocus;

    // Puts the keyboard focus on a control, for tests.
    internal void FocusSettingsTarget(SettingsTarget target) => _settingsFocus = target;

    internal bool IsEditingText => _editingText;

    internal string TextBufferForTest => _textBuffer;

    // The shortcut row waiting for a key press, or null.
    internal SettingsRowId? CapturingShortcut => _capturing;

    private bool OnSettingsPage => !_notice && _model.View == WidgetCardView.Settings && _settingsLayout is not null && _model.Settings is not null;

    private void RenderSettings(CardSettingsValues values)
    {
        if (_shownView != WidgetCardView.Settings)
        {
            _settingsFocus = new SettingsTarget(SettingsRowId.None, SettingsPart.Back);
            _editingText = false;
            _capturing = null;
        }

        _shownView = WidgetCardView.Settings;
        _setupLayout = null;
        using var probe = new Bitmap(1, 1);
        using Graphics measure = Graphics.FromImage(probe);
        SettingsLayout layout = SettingsPageLayout.Compute(values, _dpi, new GraphicsTextMeasure(measure, _type), _look.TextScale);
        _settingsLayout = layout;

        // The focus must point at a control the page still draws (the Clear button goes when its chord does).
        if (!layout.Targets.Contains(_settingsFocus))
        {
            _settingsFocus = new SettingsTarget(SettingsRowId.None, SettingsPart.Back);
        }

        ClientSize = new Size(layout.Frame.Width, layout.Frame.Height);
        Invalidate();
    }

    // ---- Mouse

    private SettingsTarget? HitSettingsTarget(Point point)
    {
        if (_settingsLayout is not { } layout)
        {
            return null;
        }

        if (layout.Frame.Back.Contains(point))
        {
            return new SettingsTarget(SettingsRowId.None, SettingsPart.Back);
        }

        foreach (SettingsItem item in layout.Items)
        {
            if (item.Kind != SettingsItemKind.Row)
            {
                continue;
            }

            foreach (SettingsTarget target in layout.Targets)
            {
                if (target.Row == item.Row && PartRectangle(item, target.Part).Contains(point))
                {
                    return target;
                }
            }
        }

        return null;
    }

    private static Rectangle PartRectangle(SettingsItem item, SettingsPart part) => part switch
    {
        SettingsPart.Toggle or SettingsPart.SegmentFirst or SettingsPart.Text or SettingsPart.Minus or SettingsPart.Shortcut or SettingsPart.Button or SettingsPart.Choice => item.A,
        SettingsPart.SegmentSecond or SettingsPart.Plus or SettingsPart.Clear => item.B,
        _ => Rectangle.Empty,
    };

    private void SettingsMouseDown(MouseEventArgs e)
    {
        SettingsTarget? hit = e.Button == MouseButtons.Left ? HitSettingsTarget(e.Location) : null;
        _leftButtonDownOnSettingsTarget = hit;

        // A press anywhere else finishes what was being typed, and gives up a shortcut still waiting for keys.
        if (_editingText && hit is not { Part: SettingsPart.Text })
        {
            EndTextEdit(commit: true);
        }

        if (_capturing is { } row && hit != new SettingsTarget(row, SettingsPart.Shortcut))
        {
            _capturing = null;
            Invalidate();
        }
    }

    private void SettingsMouseUp(MouseEventArgs e)
    {
        SettingsTarget? down = _leftButtonDownOnSettingsTarget;
        _leftButtonDownOnSettingsTarget = null;
        if (e.Button == MouseButtons.Left && down is { } pressed && HitSettingsTarget(e.Location) == pressed)
        {
            _settingsFocus = pressed;
            ActivateSettingsTarget(pressed);
        }
    }

    // ---- Activating a control

    internal void ActivateSettingsTarget(SettingsTarget target)
    {
        if (_model.Settings is not { } values)
        {
            return;
        }

        switch (target.Part)
        {
            case SettingsPart.Back:
                EndEdits(commit: true);
                SetupActionRequested?.Invoke(this, SetupAction.Back);
                break;
            case SettingsPart.Toggle:
                Raise(new ToggleChange(target.Row, !ToggleValue(values, target.Row)));
                break;
            case SettingsPart.SegmentFirst:
                Raise(new PositionChange(GaugePosition.RightEnd));
                break;
            case SettingsPart.SegmentSecond:
                Raise(new PositionChange(GaugePosition.NextToApps));
                break;
            case SettingsPart.Choice:
                Raise(new DisplayChange(GaugeDisplayOptions.Next(values.GaugeDisplayOptions, values.GaugeDisplayId)));
                break;
            case SettingsPart.Text:
                BeginTextEdit(values.OtherDeviceLabel);
                break;
            case SettingsPart.Minus:
                StepThreshold(values, -CardSettingsValues.LowBatteryStep);
                break;
            case SettingsPart.Plus:
                StepThreshold(values, CardSettingsValues.LowBatteryStep);
                break;
            case SettingsPart.Shortcut:
                _capturing = target.Row;
                _editingText = false;
                Invalidate();
                break;
            case SettingsPart.Clear:
                if (ShortcutOf(target.Row) is { } shortcut)
                {
                    Raise(new ShortcutClear(shortcut));
                }

                break;
            case SettingsPart.Button:
                Raise(target.Row == SettingsRowId.Repair ? new RepairRequest() : new CheckRequest());
                break;
        }
    }

    private static bool ToggleValue(CardSettingsValues values, SettingsRowId row) => row switch
    {
        SettingsRowId.PauseBud => values.PauseWhenBudComesOut,
        SettingsRowId.PauseLeave => values.PauseWhenAirPodsLeave,
        SettingsRowId.CaseCard => values.CaseOpenCard,
        SettingsRowId.LeftClick => values.LeftClickConnects,
        SettingsRowId.HandBack => values.HandBack,
        SettingsRowId.CheckAutomatically => values.CheckAutomatically,
        _ => false,
    };

    private static CardShortcut? ShortcutOf(SettingsRowId row) => row switch
    {
        SettingsRowId.Connect => CardShortcut.Connect,
        SettingsRowId.Disconnect => CardShortcut.Disconnect,
        _ => null,
    };

    private void StepThreshold(CardSettingsValues values, int delta)
    {
        int next = Math.Clamp(values.LowBatteryPercent + delta, CardSettingsValues.LowBatteryMin, CardSettingsValues.LowBatteryMax);
        if (next != values.LowBatteryPercent)
        {
            Raise(new ThresholdChange(next));
        }
    }

    private void Raise(SettingChange change) => SettingChanged?.Invoke(this, change);

    // ---- Keys

    // The keys of the settings page. Internal so a test can send one without a window handle. A shortcut waiting
    // for keys takes every key; a text box being edited takes the editing keys; otherwise Tab and Shift+Tab move
    // the focus, Enter and Space use what has it, Up and Down step the low battery value, and Escape goes back.
    internal void HandleSettingsKey(Keys keyData)
    {
        NoteKeyForFocusCue(keyData);
        Keys key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0;
        bool alt = (keyData & Keys.Alt) != 0;
        bool shift = (keyData & Keys.Shift) != 0;

        if (_capturing is { } row)
        {
            CaptureShortcutKey(row, key, ctrl, alt, shift);
            return;
        }

        if (_editingText)
        {
            HandleTextKey(key, ctrl);
            return;
        }

        switch (key)
        {
            case Keys.Tab:
                MoveSettingsFocus(shift);
                break;
            case Keys.Escape:
                SetupActionRequested?.Invoke(this, SetupAction.Back);
                break;
            case Keys.Enter:
            case Keys.Space:
                ActivateSettingsTarget(_settingsFocus);
                break;
            case Keys.Up when _settingsFocus.Part is SettingsPart.Minus or SettingsPart.Plus && _model.Settings is { } up:
                StepThreshold(up, CardSettingsValues.LowBatteryStep);
                break;
            case Keys.Down when _settingsFocus.Part is SettingsPart.Minus or SettingsPart.Plus && _model.Settings is { } down:
                StepThreshold(down, -CardSettingsValues.LowBatteryStep);
                break;
        }
    }

    private void MoveSettingsFocus(bool backwards)
    {
        if (_settingsLayout is not { } layout)
        {
            return;
        }

        IReadOnlyList<SettingsTarget> targets = layout.Targets;
        int at = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == _settingsFocus)
            {
                at = i;
            }
        }

        _settingsFocus = targets[(at + (backwards ? targets.Count - 1 : 1)) % targets.Count];
        Invalidate();
    }

    private static bool IsModifierKey(Keys key) =>
        key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    // A shortcut box waiting for keys: Escape gives up, a modifier alone is waited through, and a key pressed
    // without Ctrl or Alt is not a shortcut, so the box keeps waiting. A chord is saved through the presenter,
    // which checks it against Windows and the other shortcut.
    private void CaptureShortcutKey(SettingsRowId row, Keys key, bool ctrl, bool alt, bool shift)
    {
        if (key == Keys.Escape && !ctrl && !alt && !shift)
        {
            _capturing = null;
            Invalidate();
            return;
        }

        if (IsModifierKey(key) || !(ctrl || alt) || ShortcutOf(row) is not { } shortcut)
        {
            return;
        }

        _capturing = null;
        Invalidate();
        Raise(new ShortcutChange(shortcut, key, ctrl, alt, shift));
    }

    // ---- The text box

    private void BeginTextEdit(string current)
    {
        _capturing = null;
        _editingText = true;
        _textBuffer = current;
        _caret = current.Length;
        Invalidate();
    }

    private void EndEdits(bool commit)
    {
        EndTextEdit(commit);
        if (_capturing is not null)
        {
            _capturing = null;
            Invalidate();
        }
    }

    // Enter, leaving the box, going back and closing keep what was typed; Escape puts the saved text back. Only a
    // changed text is raised.
    private void EndTextEdit(bool commit)
    {
        if (!_editingText)
        {
            return;
        }

        _editingText = false;
        string typed = _textBuffer;
        Invalidate();
        if (commit && _model.Settings is { } values && !string.Equals(typed, values.OtherDeviceLabel, StringComparison.Ordinal))
        {
            Raise(new TextChange(typed));
        }
    }

    private void HandleTextKey(Keys key, bool ctrl)
    {
        switch (key)
        {
            case Keys.Enter:
                EndTextEdit(commit: true);
                break;
            case Keys.Escape:
                EndTextEdit(commit: false);
                break;
            case Keys.Tab:
                EndTextEdit(commit: true);
                MoveSettingsFocus(backwards: false);
                break;
            case Keys.Back:
                if (_caret > 0)
                {
                    _textBuffer = _textBuffer.Remove(_caret - 1, 1);
                    _caret--;
                    Invalidate();
                }

                break;
            case Keys.Delete:
                if (_caret < _textBuffer.Length)
                {
                    _textBuffer = _textBuffer.Remove(_caret, 1);
                    Invalidate();
                }

                break;
            case Keys.Left:
                _caret = Math.Max(0, _caret - 1);
                Invalidate();
                break;
            case Keys.Right:
                _caret = Math.Min(_textBuffer.Length, _caret + 1);
                Invalidate();
                break;
            case Keys.Home:
                _caret = 0;
                Invalidate();
                break;
            case Keys.End:
                _caret = _textBuffer.Length;
                Invalidate();
                break;
            case Keys.V when ctrl && Clipboard.ContainsText():
                foreach (char c in Clipboard.GetText())
                {
                    HandleSettingsChar(c);
                }

                break;
        }
    }

    // A typed character, from WM_CHAR. Control characters are the editing keys' business, not text.
    internal void HandleSettingsChar(char c)
    {
        if (!_editingText || char.IsControl(c) || _textBuffer.Length >= WidgetSettings.MaxOtherDeviceLabelLength)
        {
            return;
        }

        _textBuffer = _textBuffer.Insert(_caret, c.ToString());
        _caret++;
        Invalidate();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_notice && _editingText)
        {
            HandleSettingsChar(e.KeyChar);
            e.Handled = true;
        }

        base.OnKeyPress(e);
    }

    // A shortcut waiting for keys sees every key, Alt combinations included, before Windows treats one as a menu
    // or system key.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_notice && _capturing is not null)
        {
            HandleSettingsKey(keyData);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            EndEdits(commit: true);
        }
    }

    // ---- Painting

    private void DrawSettings(Graphics g, CardSettingsValues values, SettingsLayout layout)
    {
        CardColours colours = Colours;
        bool focusVisible = FocusShown;
        SettingsTarget focus = _settingsFocus;
        int fourteen = CardPlacement.Scale(14, _dpi);
        int twelve = CardPlacement.Scale(12, _dpi);

        SubPageFrame.DrawHeader(g, layout.Frame, WidgetCopy.SettingsTitle, null, colours, _type, _dpi, backFocused: focusVisible && focus.Part == SettingsPart.Back);

        bool Focused(SettingsRowId row, SettingsPart part) => focusVisible && focus == new SettingsTarget(row, part);

        foreach (SettingsItem item in layout.Items)
        {
            switch (item.Kind)
            {
                case SettingsItemKind.Divider:
                    CardPaint.Divider(g, item.Bounds.Left, item.Bounds.Right, item.Bounds.Y, colours);
                    continue;
                case SettingsItemKind.Head:
                    CardPaint.Text(g, item.Label, item.Bounds, _type, twelve, bold: true, colours.TextSecondary, StringAlignment.Near, StringAlignment.Far);
                    continue;
            }

            CardPaint.Wrapped(g, item.Label, item.LabelRect, _type, fourteen, bold: false, colours.Text);
            if (item.Sub is not null)
            {
                CardPaint.Wrapped(g, item.Sub, item.SubRect, _type, twelve, bold: false, item.SubIsProblem ? colours.Caution : colours.TextSecondary);
            }

            switch (item.Row)
            {
                case SettingsRowId.GaugePosition:
                    CardPaint.Segment(g, item.A, WidgetCopy.PositionRightEnd, values.GaugePosition == GaugePosition.RightEnd, colours, _type, _dpi, Focused(item.Row, SettingsPart.SegmentFirst));
                    CardPaint.Segment(g, item.B, WidgetCopy.PositionNextToApps, values.GaugePosition == GaugePosition.NextToApps, colours, _type, _dpi, Focused(item.Row, SettingsPart.SegmentSecond));
                    break;
                case SettingsRowId.GaugeDisplay:
                    CardPaint.SmallButton(g, item.A, GaugeDisplayOptions.LabelFor(values.GaugeDisplayOptions, values.GaugeDisplayId), colours, _type, _dpi, Focused(item.Row, SettingsPart.Choice));
                    break;
                case SettingsRowId.OtherDevice:
                    DrawTextBox(g, item.A, colours, values.OtherDeviceLabel, Focused(item.Row, SettingsPart.Text));
                    break;
                case SettingsRowId.LowBattery:
                    bool atMin = values.LowBatteryPercent <= CardSettingsValues.LowBatteryMin;
                    bool atMax = values.LowBatteryPercent >= CardSettingsValues.LowBatteryMax;
                    CardPaint.IconButton(g, item.A, GlyphKind.Minus, enabled: !atMin, colours, _dpi, Focused(item.Row, SettingsPart.Minus));
                    CardPaint.Text(g, values.LowBatteryPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%", item.Value, _type, fourteen, bold: false, colours.Text, StringAlignment.Center, StringAlignment.Center);
                    CardPaint.IconButton(g, item.B, GlyphKind.Plus, enabled: !atMax, colours, _dpi, Focused(item.Row, SettingsPart.Plus));
                    break;
                case SettingsRowId.Connect:
                case SettingsRowId.Disconnect:
                    string chord = item.Row == SettingsRowId.Connect ? values.ConnectChord : values.DisconnectChord;
                    DrawShortcut(g, item, colours, chord, focusVisible, focus);
                    break;
                case SettingsRowId.CheckForUpdates:
                    CardPaint.SmallButton(g, item.A, WidgetCopy.CheckButton, colours, _type, _dpi, Focused(item.Row, SettingsPart.Button));
                    break;
                case SettingsRowId.Repair:
                    CardPaint.SmallButton(g, item.A, WidgetCopy.RepairButton, colours, _type, _dpi, Focused(item.Row, SettingsPart.Button));
                    break;
                default:
                    CardPaint.Toggle(g, item.A, ToggleValue(values, item.Row), colours, _dpi);
                    if (Focused(item.Row, SettingsPart.Toggle))
                    {
                        CardPaint.Focus(g, item.A, item.A.Height / 2, colours, _dpi);
                    }

                    break;
            }
        }
    }

    // 120 by 28: the control fill, a stroke all round and the secondary text colour along the bottom; the accent
    // there while it is being edited, with the caret.
    private void DrawTextBox(Graphics g, Rectangle rect, CardColours colours, string saved, bool focused)
    {
        int radius = CardPlacement.Scale(4, _dpi);
        using (GraphicsPath box = CardPaint.RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius))
        {
            using var fill = new SolidBrush(colours.ControlFill);
            g.FillPath(fill, box);
            using var pen = new Pen(colours.ControlStroke, 1f);
            g.DrawPath(pen, box);
        }

        using (var bottom = new Pen(_editingText ? colours.Accent : colours.TextSecondary, _editingText ? 2f : 1f))
        {
            g.DrawLine(bottom, rect.Left + radius, rect.Bottom - 1, rect.Right - radius, rect.Bottom - 1);
        }

        int padding = CardPlacement.Scale(8, _dpi);
        var textRect = new Rectangle(rect.X + padding, rect.Y, Math.Max(1, rect.Width - (2 * padding)), rect.Height);
        string text = _editingText ? _textBuffer : saved;
        int size = CardPlacement.Scale(14, _dpi);
        CardPaint.Text(g, text, textRect, _type, size, bold: false, colours.Text, StringAlignment.Near, StringAlignment.Center);
        if (_editingText)
        {
            using Font font = _type.Font(size, bold: false);
            using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };
            float advance = _caret == 0 ? 0f : g.MeasureString(_textBuffer[.._caret], font, int.MaxValue, format).Width;
            float x = Math.Min(textRect.Right - 1, textRect.X + advance);
            using var caret = new Pen(colours.Text, 1f);
            g.DrawLine(caret, x, rect.Y + CardPlacement.Scale(6, _dpi), x, rect.Bottom - CardPlacement.Scale(7, _dpi));
        }
        else if (focused)
        {
            CardPaint.Focus(g, rect, radius, colours, _dpi);
        }
    }

    // The words in a shortcut box: "Press keys" while it waits for keys, "Not set" when it holds no chord, else the
    // chord. For the painter and for tests.
    private string ShortcutBoxText(SettingsRowId row, string chord) =>
        _capturing == row ? WidgetCopy.ShortcutPressKeys : string.IsNullOrEmpty(chord) ? WidgetCopy.ShortcutNotSet : chord;

    internal string ShortcutBoxText(SettingsRowId row) => _model.Settings is { } values
        ? ShortcutBoxText(row, row == SettingsRowId.Connect ? values.ConnectChord : values.DisconnectChord)
        : string.Empty;

    // The shortcut box, 136 by 28, and its 28 by 28 clear button. "Not set" in the tertiary colour when empty,
    // "Press keys" in the accent while it waits.
    private void DrawShortcut(Graphics g, SettingsItem item, CardColours colours, string chord, bool focusVisible, SettingsTarget focus)
    {
        bool waiting = _capturing == item.Row;
        bool empty = string.IsNullOrEmpty(chord);
        string text = ShortcutBoxText(item.Row, chord);
        int radius = CardPlacement.Scale(4, _dpi);
        using (GraphicsPath box = CardPaint.RoundedRectangle(new RectangleF(item.A.X + 0.5f, item.A.Y + 0.5f, item.A.Width - 1, item.A.Height - 1), radius))
        {
            using var fill = new SolidBrush(colours.ControlFill);
            g.FillPath(fill, box);
            using var pen = new Pen(waiting ? colours.Accent : colours.ControlStroke, 1f);
            g.DrawPath(pen, box);
        }

        int padding = CardPlacement.Scale(8, _dpi);
        var textRect = new Rectangle(item.A.X + padding, item.A.Y, Math.Max(1, item.A.Width - (2 * padding)), item.A.Height);
        Color ink = waiting ? colours.Accent : empty ? colours.TextTertiary : colours.Text;
        CardPaint.Text(g, text, textRect, _type, CardPlacement.Scale(12, _dpi), bold: false, ink, StringAlignment.Near, StringAlignment.Center);
        if (focusVisible && focus == new SettingsTarget(item.Row, SettingsPart.Shortcut))
        {
            CardPaint.Focus(g, item.A, radius, colours, _dpi);
        }

        CardPaint.IconButton(g, item.B, GlyphKind.Cross, enabled: !empty && !waiting, colours, _dpi, focusVisible && focus == new SettingsTarget(item.Row, SettingsPart.Clear), bordered: false);
    }
}
