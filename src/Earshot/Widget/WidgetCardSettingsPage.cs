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

    // Whether the case-open card's row, the gauge order row and the More row are expanded. Collapsed each time the page opens.
    private bool _caseCardExpanded;
    private bool _orderExpanded;
    private bool _moreExpanded;

    // For tests.
    internal bool CaseCardExpanded => _caseCardExpanded;

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

    // The tallest the card may be, or 0 for no limit: the space above the taskbar, which the presenter works out from the
    // work area of the gauge's display. A settings page taller than this is as tall as this and scrolls under its header.
    // Set before Render; the other views never reach it.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int MaxHeight { get; set; }

    // How far the rows have scrolled up past the top of the body, in pixels. Rectangles of the layout are the page's own, in
    // positions that never scroll; what is drawn, hit and told to a screen reader is those moved up by this.
    private int _settingsScroll;
    private bool _scrollHot;
    private bool _scrollDragging;
    private int _scrollDragGrab;

    // The body of the page where the rows show, in client pixels: under the header, to the bottom of the card.
    internal Rectangle SettingsViewport => _settingsLayout is { } layout
        ? new Rectangle(0, layout.Frame.Body.Y, layout.Frame.Width, Math.Max(0, LaidOutSize.Height - layout.Frame.Body.Y))
        : Rectangle.Empty;

    // How far the rows have scrolled, for tests.
    internal int SettingsScrollOffset => _settingsScroll;

    // True when the page is taller than the card and scrolls.
    internal bool SettingsScrolls => _settingsLayout is { } layout && CardScroll.MaxOffset(layout.Frame.Body.Height, SettingsViewport.Height) > 0;

    // The indicator's bar as it is drawn now, and a way to put the rows at an offset, for tests.
    internal Rectangle ScrollIndicatorBounds => ScrollIndicator();

    internal void ScrollSettingsToForTest(int offset) => ScrollSettingsTo(offset);

    // The page's own rectangle as it is drawn now.
    private Rectangle Scrolled(Rectangle content) =>
        content.IsEmpty ? content : new Rectangle(content.X, content.Y - _settingsScroll, content.Width, content.Height);

    private void RenderSettings(CardSettingsValues values)
    {
        if (_shownView != WidgetCardView.Settings)
        {
            _settingsFocus = new SettingsTarget(SettingsRowId.None, SettingsPart.Back);
            _editingText = false;
            _capturing = null;
            _scrollHot = false;
            _scrollDragging = false;
        }

        // Opened from the main card: everything closed and at the top. Coming back from the history or updates page, the page is as it
        // was left.
        if (_shownView == WidgetCardView.Main)
        {
            _settingsScroll = 0;
            _caseCardExpanded = false;
            _orderExpanded = false;
            _moreExpanded = false;
        }

        _shownView = WidgetCardView.Settings;
        _setupLayout = null;
        using var probe = new Bitmap(1, 1);
        using Graphics measure = Graphics.FromImage(probe);
        SettingsLayout layout = SettingsPageLayout.Compute(
            values with { CaseOpenCardExpanded = _caseCardExpanded, GaugeOrderExpanded = _orderExpanded, MoreExpanded = _moreExpanded }, _dpi, new GraphicsTextMeasure(measure, _type), _look.TextScale);
        _settingsLayout = layout;

        // The focus must point at a control the page still draws (the Clear button goes when its chord does).
        if (!layout.Targets.Any(t => t.SameStop(_settingsFocus)))
        {
            _settingsFocus = new SettingsTarget(SettingsRowId.None, SettingsPart.Back);
        }

        // A page taller than the card may be is capped to that height, however little is left of it: the header and a row or two
        // at least. What is cut off is reached by scrolling.
        int height = layout.Frame.Height;
        if (MaxHeight > 0 && height > MaxHeight)
        {
            height = Math.Min(height, Math.Max(MaxHeight, layout.Frame.Body.Y + CardPlacement.Scale(MinViewportAt96, _dpi)));
        }

        SizeTo(new Size(layout.Frame.Width, height));
        _settingsScroll = CardScroll.Clamp(_settingsScroll, layout.Frame.Body.Height, SettingsViewport.Height);
        RepaintIfChanged();
    }

    private const int MinViewportAt96 = 72;

    // ---- Scrolling

    private void ScrollSettingsTo(int offset)
    {
        if (_settingsLayout is not { } layout)
        {
            return;
        }

        int next = CardScroll.Clamp(offset, layout.Frame.Body.Height, SettingsViewport.Height);
        if (next == _settingsScroll)
        {
            return;
        }

        _settingsScroll = next;
        HideTip();
        ForgetFills();
        Invalidate();
    }

    // The wheel: each notch moves the rows by the lines Windows is set to scroll.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_notice && !_exiting && OnSettingsPage && !_scrollDragging && SettingsScrolls)
        {
            ScrollSettingsTo(_settingsScroll + CardScroll.WheelPixels(e.Delta, SystemInformation.MouseWheelScrollLines, _dpi, SettingsViewport.Height));
            if (e is HandledMouseEventArgs handled)
            {
                handled.Handled = true;
            }

            return;
        }

        base.OnMouseWheel(e);
    }

    // Brings the control that has the keyboard focus wholly into view. A row is shown whole (its label and its note with
    // its control) when the viewport has room for that, so moving to the first row shows the top of the page. The back
    // button is in the header, which does not scroll.
    private void ScrollFocusIntoView()
    {
        if (_settingsLayout is not { } layout || _settingsFocus.Part == SettingsPart.Back || !SettingsScrolls)
        {
            return;
        }

        SettingsItem? row = layout.Items.FirstOrDefault(i => IsItemOf(i, _settingsFocus));
        if (row is null)
        {
            return;
        }

        Rectangle control = _settingsFocus.Part == SettingsPart.Tile && _settingsFocus.Index < row.Tiles.Count
            ? row.Tiles[_settingsFocus.Index]
            : PartRectangle(row, _settingsFocus.Part);
        Rectangle target = control;
        if (control.IsEmpty)
        {
            target = row.Bounds;
        }
        else if (row.Bounds.Height <= SettingsViewport.Height)
        {
            target = Rectangle.Union(row.Bounds, control);
        }

        ScrollSettingsTo(CardScroll.EnsureVisible(
            _settingsScroll, target, layout.Frame.Body.Y, SettingsViewport.Height, layout.Frame.Body.Height, CardPlacement.Scale(CardScroll.FocusMarginAt96, _dpi)));
    }

    // The indicator along the right edge as it is drawn: empty when the page does not scroll.
    private Rectangle ScrollIndicator() =>
        _settingsLayout is { } layout && SettingsScrolls
            ? CardScroll.Thumb(SettingsViewport, layout.Frame.Body.Height, _settingsScroll, _dpi, _scrollHot || _scrollDragging)
            : Rectangle.Empty;

    // A press on the strip at the right edge: on the bar, it is picked up to be dragged; beside it, the rows move a page that
    // way. True when the press was the indicator's.
    private bool ScrollIndicatorMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !SettingsScrolls || _settingsLayout is not { } layout
            || !CardScroll.Zone(SettingsViewport, _dpi).Contains(e.Location))
        {
            return false;
        }

        Rectangle bar = ScrollIndicator();
        if (e.Y >= bar.Top && e.Y < bar.Bottom)
        {
            _scrollDragging = true;
            _scrollDragGrab = e.Y - bar.Y;
            Invalidate();
        }
        else
        {
            int page = CardScroll.PagePixels(SettingsViewport.Height, _dpi);
            ScrollSettingsTo(_settingsScroll + (e.Y < bar.Y ? -page : page));
        }

        return true;
    }

    // The pointer moved: a bar being dragged follows it, and the bar widens while the pointer is near it. True while dragging.
    private bool ScrollIndicatorMouseMove(MouseEventArgs e)
    {
        if (_scrollDragging && _settingsLayout is { } layout)
        {
            Rectangle bar = ScrollIndicator();
            ScrollSettingsTo(CardScroll.OffsetForThumbTop(SettingsViewport, layout.Frame.Body.Height, e.Y - _scrollDragGrab, bar.Height, _dpi));
            return true;
        }

        SetScrollHot(OnSettingsPage && SettingsScrolls && CardScroll.Zone(SettingsViewport, _dpi).Contains(e.Location));
        return false;
    }

    private void SetScrollHot(bool hot)
    {
        if (_scrollHot != hot)
        {
            _scrollHot = hot;
            Invalidate();
        }
    }

    private void EndScrollDrag()
    {
        if (_scrollDragging)
        {
            _scrollDragging = false;
            Invalidate();
        }
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

        // Nothing in the header but the back button, and a row that has scrolled up under it is not to be pressed through it.
        if (point.Y < layout.Frame.Body.Y)
        {
            return null;
        }

        point = new Point(point.X, point.Y + _settingsScroll);
        foreach (SettingsItem item in layout.Items)
        {
            if (item.Kind != SettingsItemKind.Row)
            {
                continue;
            }

            for (int i = 0; i < item.Tiles.Count; i++)
            {
                if (item.Tiles[i].Contains(point))
                {
                    return new SettingsTarget(item.Row, SettingsPart.Tile, i);
                }
            }

            foreach (SettingsTarget target in layout.Targets)
            {
                if (target.Row == item.Row && target.Index == item.Index && target.Part != SettingsPart.Tile && PartRectangle(item, target.Part).Contains(point))
                {
                    return target;
                }
            }
        }

        return null;
    }

    private static Rectangle PartRectangle(SettingsItem item, SettingsPart part) => part switch
    {
        SettingsPart.Toggle or SettingsPart.Text or SettingsPart.Minus or SettingsPart.Shortcut or SettingsPart.Button
            or SettingsPart.Choice or SettingsPart.Check => item.A,
        SettingsPart.Plus or SettingsPart.Clear or SettingsPart.Expand => item.B,
        _ => Rectangle.Empty,
    };

    // The row a control is on: the row with its id and, for a box of a list, its index (the pictures of a group share one row).
    private static bool IsItemOf(SettingsItem item, SettingsTarget target) =>
        item.Kind == SettingsItemKind.Row && item.Row == target.Row && (target.Part == SettingsPart.Tile ? item.Tiles.Count > 0 : item.Index == target.Index && item.Tiles.Count == 0);

    private void SettingsMouseDown(MouseEventArgs e)
    {
        if (ScrollIndicatorMouseDown(e))
        {
            _leftButtonDownOnSettingsTarget = null;
            return;
        }

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
        EndScrollDrag();
        SettingsTarget? down = _leftButtonDownOnSettingsTarget;
        _leftButtonDownOnSettingsTarget = null;
        if (e.Button == MouseButtons.Left && down is { } pressed && HitSettingsTarget(e.Location) == pressed)
        {
            // A control that was only partly in view comes wholly into view, as it does when the keyboard moves to it.
            _settingsFocus = pressed;
            ScrollFocusIntoView();
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
            case SettingsPart.Choice when target.Row == SettingsRowId.GaugePosition:
                Raise(new PositionChange(values.GaugePosition == GaugePosition.RightEnd ? GaugePosition.NextToApps : GaugePosition.RightEnd));
                break;
            case SettingsPart.Choice when target.Row == SettingsRowId.CaseCardClose:
                Raise(new CaseCardCloseChange(CaseOpenCardClose.Next(values.CaseOpenCardCloseSeconds)));
                break;
            case SettingsPart.Choice when target.Row == SettingsRowId.CaseCardDisplays:
                Raise(new CaseCardDisplaysChange(CaseOpenCardDisplayChoice.Next(values.CaseOpenCardDisplays)));
                break;
            case SettingsPart.Choice:
                Raise(new DisplayChange(GaugeDisplayOptions.Next(values.GaugeDisplayOptions, values.GaugeDisplayId)));
                break;
            case SettingsPart.Expand:
                // Only the page changes: the row's choices show or go, and the focus stays on the chevron.
                bool opening;
                switch (target.Row)
                {
                    case SettingsRowId.More:
                        opening = _moreExpanded = !_moreExpanded;
                        break;
                    case SettingsRowId.GaugeOrder:
                        opening = _orderExpanded = !_orderExpanded;
                        break;
                    default:
                        opening = _caseCardExpanded = !_caseCardExpanded;
                        break;
                }

                SettingsLayout? before = _settingsLayout;
                RenderSettings(values);
                if (before is not null && _settingsLayout is { } after)
                {
                    NoteExpander(target.Row, opening, before, after);
                }

                Invalidate();
                break;
            case SettingsPart.Check when target.Index >= 0 && target.Index < values.CaseOpenCardDisplayOptions.Count:
                Raise(new CaseCardDisplayChange(values.CaseOpenCardDisplayOptions[target.Index].Id, !values.CaseOpenCardShownOnDisplay(target.Index)));
                break;
            case SettingsPart.Tile:
                Raise(new OrderChange(GaugeOrders.FromStored((GaugeOrder)target.Index)));
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
                Raise(target.Row switch
                {
                    SettingsRowId.SoundSettings => new OpenSoundSettingsRequest(),
                    SettingsRowId.History => new OpenHistoryRequest(),
                    SettingsRowId.About => new OpenUpdatesRequest(),
                    _ => new CopyDiagnosticsRequest(),
                });
                break;
        }
    }

    private static bool ToggleValue(CardSettingsValues values, SettingsRowId row) => row switch
    {
        SettingsRowId.PauseBud => values.PauseWhenBudComesOut,
        SettingsRowId.PauseLeave => values.PauseWhenAirPodsLeave,
        SettingsRowId.LeftClick => values.LeftClickConnects,
        SettingsRowId.HandBack => values.HandBack,
        SettingsRowId.CheckAutomatically => values.CheckAutomatically,
        SettingsRowId.MicrophoneOff => values.HandsFreeMicrophoneOff,
        SettingsRowId.CaseCard => values.CaseOpenCardOn,
        SettingsRowId.FullyCharged => values.FullyChargedNotice,
        _ => false,
    };

    private static CardShortcut? ShortcutOf(SettingsRowId row) => row switch
    {
        SettingsRowId.Connect => CardShortcut.Connect,
        SettingsRowId.Disconnect => CardShortcut.Disconnect,
        SettingsRowId.OpenCard => CardShortcut.OpenCard,
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
    // the focus, Enter and Space use what has it, Up and Down step the low battery value, Page Up, Page Down, Home and End
    // scroll a page that is taller than the card, and Escape goes back.
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
            case Keys.PageDown:
                ScrollSettingsTo(_settingsScroll + CardScroll.PagePixels(SettingsViewport.Height, _dpi));
                break;
            case Keys.PageUp:
                ScrollSettingsTo(_settingsScroll - CardScroll.PagePixels(SettingsViewport.Height, _dpi));
                break;
            case Keys.Home:
                ScrollSettingsTo(0);
                break;
            case Keys.End:
                ScrollSettingsTo(int.MaxValue);
                break;
            case Keys.Left when _settingsFocus.Part == SettingsPart.Tile:
                MoveTile(-1);
                break;
            case Keys.Right when _settingsFocus.Part == SettingsPart.Tile:
                MoveTile(1);
                break;
            case Keys.Up when _settingsFocus.Part == SettingsPart.Tile:
                MoveTile(-3);
                break;
            case Keys.Down when _settingsFocus.Part == SettingsPart.Tile:
                MoveTile(3);
                break;
            case Keys.Up when _settingsFocus.Part is SettingsPart.Minus or SettingsPart.Plus && _model.Settings is { } up:
                StepThreshold(up, CardSettingsValues.LowBatteryStep);
                break;
            case Keys.Down when _settingsFocus.Part is SettingsPart.Minus or SettingsPart.Plus && _model.Settings is { } down:
                StepThreshold(down, -CardSettingsValues.LowBatteryStep);
                break;
        }
    }

    // The focus among the pictures of the gauge orders, in a grid of three across; a key that would leave the grid does
    // nothing. Moving the focus does not choose: Enter or Space does.
    private void MoveTile(int by)
    {
        int next = _settingsFocus.Index + by;
        if (next is >= 0 and <= 5)
        {
            _settingsFocus = _settingsFocus with { Index = next };
            ScrollFocusIntoView();
            Invalidate();
            NoteFocusMoved();
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
            if (targets[i].SameStop(_settingsFocus))
            {
                at = i;
            }
        }

        _settingsFocus = targets[(at + (backwards ? targets.Count - 1 : 1)) % targets.Count];
        ScrollFocusIntoView();
        Invalidate();
        NoteFocusMoved();
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

        // A window that was hidden or shown may not hold the pixels last copied to it.
        ForgetShownFrame();
        if (!Visible)
        {
            HideTip();
            EndEdits(commit: true);
            EndScrollDrag();
            _scrollHot = false;
        }
    }

    // ---- Painting

    private void DrawSettings(Graphics g, CardSettingsValues values, SettingsLayout layout)
    {
        CardColours colours = Colours;
        bool focusVisible = FocusShown;
        SettingsTarget focus = _settingsFocus;

        SubPageFrame.DrawHeader(g, layout.Frame, WidgetCopy.SettingsTitle, null, colours, _type, _dpi, backFocused: focusVisible && focus.Part == SettingsPart.Back);

        // The rows draw in their own positions, moved up by the scroll and cut at the body's top and bottom, so nothing of
        // them shows under the header. A page that fits is drawn as it is.
        bool scrolls = SettingsScrolls;
        GraphicsState state = g.Save();
        if (scrolls)
        {
            g.SetClip(SettingsViewport);
            g.TranslateTransform(0, -_settingsScroll);
        }

        DrawSettingsRowsMoving(g, values, layout, colours, focusVisible, focus);
        g.Restore(state);

        if (scrolls)
        {
            if (_settingsScroll > 0)
            {
                // One whole pixel row under the header, so nothing of it spills into the header.
                using var line = new SolidBrush(colours.Divider);
                g.FillRectangle(line, 0, SettingsViewport.Top, layout.Frame.Width, 1);
            }

            CardScroll.DrawIndicator(g, ScrollIndicator(), colours.TextTertiary);
        }
    }

    private void DrawSettingsRows(Graphics g, CardSettingsValues values, SettingsLayout layout, CardColours colours, bool focusVisible, SettingsTarget focus)
    {
        int fourteen = CardPlacement.Scale(14, _dpi);
        int twelve = CardPlacement.Scale(12, _dpi);
        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);

        bool Focused(SettingsRowId row, SettingsPart part) => focusVisible && focus == new SettingsTarget(row, part);

        // The surfaces first: each a fill and a 1 px stroke at the row radius.
        foreach (Rectangle surface in layout.Surfaces)
        {
            CardPaint.Surface(g, surface, radius, colours.RowFill, colours.RowStroke);
        }

        foreach (SettingsItem item in layout.Items)
        {
            if (item.Kind == SettingsItemKind.Head)
            {
                CardPaint.Text(g, item.Label, item.Bounds, _type, fourteen, bold: true, colours.Text, StringAlignment.Near, StringAlignment.Center);
                continue;
            }

            if (item.DividerAbove)
            {
                // The card draws with PixelOffsetMode.Half, so a whole coordinate is the edge between two pixel rows: a 1 px pen on it
                // would put half its strength on each. Half a pixel down puts the whole stroke on the row the item starts at.
                using var line = new Pen(colours.RowStroke, 1f);
                float strokeY = item.Bounds.Top + 0.5f;
                g.DrawLine(line, item.Bounds.Left + 1, strokeY, item.Bounds.Right - 2, strokeY);
            }

            if (item.Glyph != '\0')
            {
                CardPaint.Glyph(g, FluentGlyphs.Resolve(item.Glyph), item.IconRect, colours.Text, _dpi);
            }

            if (item.Label.Length > 0)
            {
                CardPaint.Wrapped(g, item.Label, item.LabelRect, _type, fourteen, bold: false, colours.Text);
            }

            if (item.Sub is not null)
            {
                CardPaint.Wrapped(g, item.Sub, item.SubRect, _type, twelve, bold: false, item.SubIsProblem ? colours.Caution : colours.TextSecondary);
            }

            if (!item.Chevron.IsEmpty && item.ChevronGlyph != '\0'
                && !CardPaint.TryGlyph(g, item.ChevronGlyph, item.Chevron, colours.TextSecondary, _dpi, FluentGlyphs.ChevronSizeAt96, 1.0))
            {
                CardPaint.Chevron(g, item.Chevron, up: item.ChevronGlyph == FluentGlyphs.ChevronUp, colours.TextSecondary, _dpi);
            }

            switch (item.Row)
            {
                case SettingsRowId.GaugePosition:
                    CardPaint.SmallButton(g, item.A, values.GaugePosition == GaugePosition.RightEnd ? WidgetCopy.PositionRightEnd : WidgetCopy.PositionNextToApps, colours, _type, _dpi, Focused(item.Row, SettingsPart.Choice));
                    break;
                case SettingsRowId.GaugeDisplay:
                    CardPaint.SmallButton(g, item.A, GaugeDisplayOptions.LabelFor(values.GaugeDisplayOptions, values.GaugeDisplayId), colours, _type, _dpi, Focused(item.Row, SettingsPart.Choice));
                    break;
                case SettingsRowId.GaugeOrder:
                    DrawOrderRow(g, item, values, colours, focusVisible, focus);
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
                case SettingsRowId.OpenCard:
                    DrawShortcut(g, item, colours, values.ChordFor(item.Row), focusVisible, focus);
                    break;
                case SettingsRowId.CopyDiagnostics:
                    CardPaint.SmallButton(g, item.A, WidgetCopy.CopyButton, colours, _type, _dpi, Focused(item.Row, SettingsPart.Button));
                    break;
                case SettingsRowId.SoundSettings:
                    CardPaint.SmallButton(g, item.A, WidgetCopy.OpenButton, colours, _type, _dpi, Focused(item.Row, SettingsPart.Button), FluentGlyphs.OpenExternal);
                    break;
                case SettingsRowId.About:
                case SettingsRowId.History:
                    if (Focused(item.Row, SettingsPart.Button))
                    {
                        CardPaint.Focus(g, item.A, radius, colours, _dpi);
                    }

                    break;
                case SettingsRowId.More:
                    if (Focused(item.Row, SettingsPart.Expand))
                    {
                        CardPaint.Focus(g, item.B, radius, colours, _dpi);
                    }

                    break;
                case SettingsRowId.CaseCard:
                    CardPaint.Toggle(g, item.A, values.CaseOpenCardOn, colours, _dpi, Knob(item.Row, item.A, values.CaseOpenCardOn));
                    if (Focused(item.Row, SettingsPart.Toggle))
                    {
                        CardPaint.Focus(g, item.A, item.A.Height / 2, colours, _dpi);
                    }

                    if (!CardPaint.TryGlyph(g, _caseCardExpanded ? FluentGlyphs.ChevronUp : FluentGlyphs.ChevronDown, item.B, colours.TextSecondary, _dpi, FluentGlyphs.ChevronSizeAt96, 1.0))
                    {
                        CardPaint.Chevron(g, item.B, up: _caseCardExpanded, colours.TextSecondary, _dpi);
                    }

                    if (Focused(item.Row, SettingsPart.Expand))
                    {
                        CardPaint.Focus(g, item.B, radius, colours, _dpi);
                    }

                    break;
                case SettingsRowId.CaseCardClose:
                    CardPaint.SmallButton(g, item.A, CaseOpenCardClose.Label(values.CaseOpenCardCloseSeconds), colours, _type, _dpi, Focused(item.Row, SettingsPart.Choice));
                    break;
                case SettingsRowId.CaseCardDisplays:
                    CardPaint.SmallButton(g, item.A, CaseOpenCardDisplayChoice.Label(values.CaseOpenCardDisplays), colours, _type, _dpi, Focused(item.Row, SettingsPart.Choice));
                    break;
                case SettingsRowId.CaseCardDisplay:
                    CardPaint.CheckBox(
                        g, item.A, values.CaseOpenCardShownOnDisplay(item.Index), colours, _dpi,
                        focusVisible && focus == new SettingsTarget(item.Row, SettingsPart.Check, item.Index));
                    break;
                default:
                    CardPaint.Toggle(g, item.A, ToggleValue(values, item.Row), colours, _dpi, Knob(item.Row, item.A, ToggleValue(values, item.Row)));
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
        ? ShortcutBoxText(row, values.ChordFor(row))
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
