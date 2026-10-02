using System.Runtime.InteropServices;
using Earshot.Popup;

namespace Earshot.Widget;

// What a screen reader, a tooltip and the keyboard see on the card: the controls the current view has, in the order the
// keyboard walks them. The card is one owner-painted window with no child controls, so each control is described here
// rather than found.
internal enum CardControlRole { PushButton, CheckButton, RadioButton, Text }

// One control: what it is called, what it is, where it is (client pixels, where it is drawn now, scrolled or not), the
// tooltip that explains it when its icon or picture does not, whether it is only an icon or a picture, whether it is on,
// whether it has the focus, and what pressing it does. Offscreen: a page that scrolls has moved it wholly out of the part
// of the card that shows it.
internal sealed record CardControl(
    string Name, CardControlRole Role, Rectangle Bounds, string? Tip, bool IconOnly, bool Checked, bool Focused, bool Enabled, Action Activate,
    bool Offscreen = false);

internal sealed partial class WidgetCard
{
    // ---- The controls of the view on screen

    internal IReadOnlyList<CardControl> CurrentControls()
    {
        if (_notice)
        {
            return NoticeControls();
        }

        if (OnSettingsPage)
        {
            return SettingsControls();
        }

        if (EffectiveView is not (WidgetCardView.Main or WidgetCardView.Settings) && _model.Setup is { } setup && _setupLayout is { } layout)
        {
            return SetupControls(setup, layout);
        }

        return MainControls();
    }

    private List<CardControl> MainControls()
    {
        WidgetCardLayout.Layout layout = _mainLayout;
        bool focus = ContainsFocus;
        var list = new List<CardControl>
        {
            new(
                _model.ConnectIntent ? WidgetCopy.Connect : WidgetCopy.Disconnect, CardControlRole.PushButton, layout.Button, null, false, false,
                focus && _focus == WidgetCardFocus.Button, ButtonUsable, () => Press(WidgetCardFocus.Button)),
        };
        if (_model.ShowSwitch)
        {
            list.Add(new CardControl(
                WidgetCopy.NamePauseBud, CardControlRole.CheckButton, layout.Switch, WidgetCopy.TipPauseBud, false, _model.AutoPauseOn,
                focus && _focus == WidgetCardFocus.Switch, true, () => Press(WidgetCardFocus.Switch)));
        }

        if (layout.ShowUpdateLine)
        {
            list.Add(new CardControl(
                WidgetCopy.UpdateButton, CardControlRole.PushButton, layout.UpdateButton, WidgetCopy.UpdateAvailable(_model.UpdateVersion ?? string.Empty), false, false,
                focus && _focus == WidgetCardFocus.UpdateButton, true, () => Press(WidgetCardFocus.UpdateButton)));
        }

        if (!layout.Gear.IsEmpty)
        {
            list.Add(new CardControl(
                WidgetCopy.TipSettings, CardControlRole.PushButton, layout.Gear, WidgetCopy.TipSettings, true, false,
                focus && _focus == WidgetCardFocus.Gear, true, () => Press(WidgetCardFocus.Gear)));
        }

        if (!layout.Refresh.IsEmpty)
        {
            list.Add(new CardControl(
                WidgetCopy.RefreshBattery, CardControlRole.PushButton, layout.Refresh, RefreshTip, true, false,
                focus && _focus == WidgetCardFocus.Refresh, !BluetoothOff, () => Press(WidgetCardFocus.Refresh)));
        }

        if (BluetoothOff)
        {
            list.Add(new CardControl(
                WidgetCopy.BluetoothOff, CardControlRole.PushButton, layout.WhereLine, WidgetCopy.TipBluetoothSettings, false, false,
                focus && _focus == WidgetCardFocus.Status, true, () => Press(WidgetCardFocus.Status)));
        }

        return list;
    }

    // The case-open card's two buttons, for a screen reader. Never focused: the card is never activated (WS_EX_NOACTIVATE)
    // and takes no keys, so a screen reader reaches them by its own navigation and presses them through DoDefaultAction.
    private List<CardControl> NoticeControls()
    {
        WidgetCardLayout.Layout layout = _mainLayout;
        var list = new List<CardControl>
        {
            new(
                _model.ConnectIntent ? WidgetCopy.Connect : WidgetCopy.Disconnect, CardControlRole.PushButton, layout.Button, null, false, false,
                false, ButtonUsable, () => Press(WidgetCardFocus.Button)),
        };
        if (!layout.Gear.IsEmpty)
        {
            list.Add(new CardControl(
                WidgetCopy.TipCloseCard, CardControlRole.PushButton, layout.Gear, WidgetCopy.TipCloseCard, true, false, false, true,
                () => Press(WidgetCardFocus.Gear)));
        }

        return list;
    }

    private void Press(WidgetCardFocus target)
    {
        _focus = target;
        ActivateFocused();
    }

    private List<CardControl> SetupControls(SetupViewModel setup, WidgetCardLayout.SetupLayout layout)
    {
        bool focus = ContainsFocus;
        var list = new List<CardControl>
        {
            new(
                WidgetCopy.TipBack, CardControlRole.PushButton, layout.Frame.Back, WidgetCopy.TipBack, true, false,
                focus && _setupFocus.Kind == SetupTargetKind.Back, true, () => ActivateSetupTarget(new SetupTarget(SetupTargetKind.Back, 0))),
        };
        for (int i = 0; i < setup.Buttons.Count && i < layout.Frame.Buttons.Count; i++)
        {
            int index = i;
            list.Add(new CardControl(
                setup.Buttons[i].Label, CardControlRole.PushButton, layout.Frame.Buttons[i], null, false, false,
                focus && _setupFocus.Kind == SetupTargetKind.Button && _setupFocus.Index == i, true,
                () => ActivateSetupTarget(new SetupTarget(SetupTargetKind.Button, index))));
        }

        if (!layout.Action.IsEmpty && setup.Buttons.Count == 1)
        {
            list.Add(new CardControl(
                setup.Buttons[0].Label, CardControlRole.PushButton, layout.Action, null, false, false,
                focus && _setupFocus.Kind == SetupTargetKind.Button && _setupFocus.Index == 0, true,
                () => ActivateSetupTarget(new SetupTarget(SetupTargetKind.Button, 0))));
        }

        foreach (WidgetCardLayout.UpdatesRowLayout row in layout.UpdateRows)
        {
            int index = row.Index;
            string name = index switch { 0 => WidgetCopy.NameAutoCheck, 1 => WidgetCopy.SettingsWhatsNew, _ => WidgetCopy.NameRepair };
            CardControlRole role = index == 0 ? CardControlRole.CheckButton : CardControlRole.PushButton;
            list.Add(new CardControl(
                name, role, row.Surface, null, false, index == 0 && setup.Rows is { AutoCheck: true },
                focus && _setupFocus.Kind == SetupTargetKind.Row && _setupFocus.Index == index, true,
                () => ActivateSetupTarget(new SetupTarget(SetupTargetKind.Row, index))));
        }

        return list;
    }

    private List<CardControl> SettingsControls()
    {
        SettingsLayout layout = _settingsLayout!;
        CardSettingsValues values = _model.Settings!;
        bool focus = ContainsFocus;
        var list = new List<CardControl>();

        // Bounds come in the page's own positions; the back button is in the header and stays, every other control is where the
        // scroll has put it. One that is wholly outside the body is off screen.
        Rectangle viewport = SettingsViewport;
        void Add(SettingsTarget stop, Rectangle bounds, CardControlRole role, bool isChecked, bool focused, string? name = null)
        {
            bool iconOnly = stop.Part is SettingsPart.Back or SettingsPart.Clear or SettingsPart.Tile or SettingsPart.Expand;
            Rectangle drawn = stop.Part == SettingsPart.Back ? bounds : Scrolled(bounds);
            bool offscreen = stop.Part != SettingsPart.Back && !drawn.IntersectsWith(viewport);
            list.Add(new CardControl(
                name ?? SettingsRows.NameOf(stop.Row, stop.Part, stop.Index), role, drawn, SettingsRows.TipOf(stop.Row, stop.Part, stop.Index), iconOnly, isChecked,
                focus && focused, true, () => ActivateSettingsTarget(stop), offscreen));
        }

        foreach (SettingsTarget stop in layout.Targets)
        {
            if (stop.Part == SettingsPart.Back)
            {
                Add(stop, layout.Frame.Back, CardControlRole.PushButton, false, _settingsFocus.SameStop(stop));
                continue;
            }

            SettingsItem item = layout.Items.First(i => IsItemOf(i, stop));
            if (stop.Part == SettingsPart.Tile)
            {
                for (int i = 0; i < item.Tiles.Count; i++)
                {
                    var tile = new SettingsTarget(stop.Row, SettingsPart.Tile, i);
                    Add(tile, item.Tiles[i], CardControlRole.RadioButton, i == (int)GaugeOrders.FromStored(values.GaugeOrder), _settingsFocus.SameStop(tile) && _settingsFocus.Index == i);
                }

                continue;
            }

            if (stop.Part == SettingsPart.Check)
            {
                // A display's box says which display it is.
                Add(stop, PartRectangle(item, stop.Part), CardControlRole.CheckButton, values.CaseOpenCardShownOnDisplay(stop.Index),
                    _settingsFocus.SameStop(stop), WidgetCopy.SettingsCaseCard + ": " + item.Label);
                continue;
            }

            CardControlRole role = stop.Part switch
            {
                SettingsPart.Toggle => CardControlRole.CheckButton,
                SettingsPart.Text => CardControlRole.Text,
                _ => CardControlRole.PushButton,
            };
            bool isChecked = stop.Part switch
            {
                SettingsPart.Toggle => ToggleValue(values, stop.Row),
                _ => false,
            };
            Add(stop, PartRectangle(item, stop.Part), role, isChecked, _settingsFocus.SameStop(stop));
        }

        return list;
    }

    // ---- Tooltips

    // The tooltip for a point on the card, and the rectangle it explains, or null. A control with a tooltip shows it;
    // so do the read line and the update line, whose words are shorter than their meaning.
    internal (string Text, Rectangle Anchor)? TooltipAt(Point point)
    {
        foreach (CardControl target in CurrentControls())
        {
            if (target.Tip is { Length: > 0 } tip && HitsControl(target, point))
            {
                return (tip, target.Bounds);
            }
        }

        if (OnSettingsPage && _settingsLayout is { } settings && point.Y >= settings.Frame.Body.Y)
        {
            // Over a row's icon or label: the row's own tooltip. The point is on the page where the scroll has put the row.
            var onPage = new Point(point.X, point.Y + _settingsScroll);
            foreach (SettingsItem item in settings.Items)
            {
                if (item.Kind == SettingsItemKind.Row && item.Tip is { Length: > 0 } rowTip && item.Bounds.Contains(onPage))
                {
                    return (rowTip, Scrolled(item.LabelRect));
                }
            }
        }

        if (!_notice && !OnSettingsPage && _model.View == WidgetCardView.Main)
        {
            WidgetCardLayout.Layout layout = _mainLayout;
            // The status row says more than its words when something is wrong: nothing heard, the case to open near this PC.
            if (_model.Refresh is { IsProblem: true, ReadLine: { Length: > 0 } problem } && layout.WhereLine.Contains(point))
            {
                return (problem, layout.WhereLine);
            }

            // A column says what it holds in a sentence: "Left 70%, charging, read 4 min ago".
            if (layout.ShowColumns)
            {
                ShownBattery shown = _model.ShownParts;
                foreach ((WidgetCardLayout.ColumnLayout column, string label, ShownPart part) in new[]
                {
                    (layout.Left, WidgetCopy.LeftWord, shown.Left), (layout.Right, WidgetCopy.RightWord, shown.Right), (layout.Case, WidgetCopy.CaseLabel, shown.Case),
                })
                {
                    Rectangle whole = Rectangle.Union(column.Label, column.ReadTime);
                    if (whole.Contains(point) && WidgetCopy.ColumnTip(label, part, _model.Now) is { } columnTip)
                    {
                        return (columnTip, whole);
                    }
                }
            }

            if (layout.ShowUpdateLine && layout.UpdateCaption.Contains(point))
            {
                return (WidgetCopy.UpdateAvailable(_model.UpdateVersion ?? string.Empty), layout.UpdateCaption);
            }
        }

        return null;
    }

    // Whether point is on a control as the card shows it: where the control is drawn, and not in the header over a row that
    // has scrolled up under it.
    private bool HitsControl(CardControl control, Point point) =>
        control.Bounds.Contains(point) && !(OnSettingsPage && _settingsLayout is { } layout && point.Y < layout.Frame.Body.Y && control.Bounds.Bottom > layout.Frame.Body.Y);

    private readonly ToolTip _toolTip = new() { ShowAlways = true, UseAnimation = false, UseFading = false };
    private System.Windows.Forms.Timer? _tipTimer;
    private (string Text, Rectangle Anchor)? _tipPending;

    // The text of the tooltip on screen, for tests.
    internal string? TooltipShownForTest { get; private set; }

    // The tooltip waiting for the hover time to pass, and how long that wait is, for tests. A test cannot hold a real
    // cursor over the card (Windows tells a window the cursor is not over that it left), so it lets the time pass itself.
    internal string? TooltipPendingForTest => _tipPending?.Text;

    internal int TooltipDelayForTest => _tipTimer?.Interval ?? 0;

    internal void LetTheHoverTimePassForTest() => OnTipTimer(this, EventArgs.Empty);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);
        if (_notice)
        {
            return;
        }

        // A bar being dragged is the pointer's business, and it widens while the pointer is near it.
        if (ScrollIndicatorMouseMove(e))
        {
            HideTip();
            return;
        }

        (string Text, Rectangle Anchor)? tip = TooltipAt(e.Location);
        if (tip == _tipPending || (tip is null && _tipPending is null))
        {
            return;
        }

        HideTip();
        if (tip is null)
        {
            return;
        }

        // After the time Windows waits before it shows a hover tooltip.
        _tipPending = tip;
        _tipTimer ??= new System.Windows.Forms.Timer();
        _tipTimer.Tick -= OnTipTimer;
        _tipTimer.Tick += OnTipTimer;
        _tipTimer.Interval = Math.Max(1, SystemInformation.MouseHoverTime);
        _tipTimer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        HideTip();
        if (!_scrollDragging)
        {
            SetScrollHot(false);
        }
    }

    private void OnTipTimer(object? sender, EventArgs e)
    {
        _tipTimer?.Stop();
        if (_tipPending is { } tip && Visible)
        {
            ShowTip(tip.Text, tip.Anchor);
        }
    }

    private void ShowTip(string text, Rectangle anchor)
    {
        TooltipShownForTest = text;
        _toolTip.Show(text, this, new Point(anchor.X, anchor.Bottom + CardPlacement.Scale(4, _dpi)), 8000);
    }

    private void HideTip()
    {
        _tipTimer?.Stop();
        _tipPending = null;
        if (TooltipShownForTest is not null)
        {
            TooltipShownForTest = null;
            if (IsHandleCreated)
            {
                _toolTip.Hide(this);
            }
        }
    }

    // The keyboard has moved the focus: an icon with no words shows its tooltip at once, under it, while the focus visual
    // is showing, and a screen reader is told where the focus went.
    private void NoteFocusMoved()
    {
        HideTip();
        IReadOnlyList<CardControl> targets = CurrentControls();
        int focused = -1;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].Focused)
            {
                focused = i;
                break;
            }
        }

        if (focused < 0)
        {
            return;
        }

        if (_cue.Visible && IsHandleCreated && targets[focused] is { IconOnly: true, Tip: { Length: > 0 } tip } target)
        {
            ShowTip(tip, target.Bounds);
        }

        if (IsHandleCreated)
        {
            AccessibilityNotifyClients(AccessibleEvents.Focus, focused);
        }
    }

    private void DisposeTips()
    {
        _tipTimer?.Dispose();
        _tipTimer = null;
        _toolTip.Dispose();
    }

    // ---- Accessibility

    private readonly List<string> _announced = [];

    // What the card has announced, in order, for tests.
    internal IReadOnlyList<string> AnnouncedForTest => _announced;

    // The case-open card's announcement: a UI Automation notification raised from the card's own element, so a screen reader
    // says it once without the card taking the focus (it never takes it). The presenter calls this once per open. False when
    // there is no window yet or UI Automation did not take it; the text is recorded either way.
    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.accessibleobject.raiseautomationnotification
    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiaraisenotificationevent
    internal bool Announce(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _announced.Add(text);
        return IsHandleCreated &&
            AccessibilityObject.RaiseAutomationNotification(
                System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                text);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new CardAccessibleObject(this);

    private sealed class CardAccessibleObject(WidgetCard card) : ControlAccessibleObject(card)
    {
        public override int GetChildCount() => card.CurrentControls().Count;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < card.CurrentControls().Count ? new TargetAccessibleObject(card, this, index) : null;

        public override AccessibleObject? HitTest(int x, int y)
        {
            Point point = card.PointToClient(new Point(x, y));
            IReadOnlyList<CardControl> targets = card.CurrentControls();
            for (int i = 0; i < targets.Count; i++)
            {
                if (card.HitsControl(targets[i], point))
                {
                    return GetChild(i);
                }
            }

            return base.HitTest(x, y);
        }

        public override AccessibleObject? GetFocused()
        {
            IReadOnlyList<CardControl> targets = card.CurrentControls();
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Focused)
                {
                    return GetChild(i);
                }
            }

            return null;
        }
    }

    // One control as a screen reader sees it. It reads the card's current list each time it is asked, so it is never
    // out of date when the view changes under it.
    private sealed class TargetAccessibleObject(WidgetCard card, AccessibleObject parent, int index) : AccessibleObject
    {
        private CardControl? Target
        {
            get
            {
                IReadOnlyList<CardControl> targets = card.CurrentControls();
                return index < targets.Count ? targets[index] : null;
            }
        }

        public override AccessibleObject Parent => parent;

        public override string? Name
        {
            get => Target?.Name;
            set { }
        }

        public override string? Description => Target?.Tip;

        public override AccessibleRole Role => Target?.Role switch
        {
            CardControlRole.CheckButton => AccessibleRole.CheckButton,
            CardControlRole.RadioButton => AccessibleRole.RadioButton,
            CardControlRole.Text => AccessibleRole.Text,
            _ => AccessibleRole.PushButton,
        };

        public override AccessibleStates State
        {
            get
            {
                if (Target is not { } target)
                {
                    return AccessibleStates.Invisible;
                }

                AccessibleStates state = AccessibleStates.Focusable;
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

                if (target.Offscreen)
                {
                    state |= AccessibleStates.Offscreen;
                }

                return state;
            }
        }

        public override Rectangle Bounds => Target is { } target ? card.RectangleToScreen(target.Bounds) : Rectangle.Empty;

        public override string? DefaultAction => Target?.Role switch
        {
            CardControlRole.CheckButton => "Toggle",
            CardControlRole.RadioButton => "Select",
            CardControlRole.Text => "Edit",
            _ => "Press",
        };

        public override void DoDefaultAction()
        {
            if (Target is { Enabled: true } target)
            {
                target.Activate();
            }
        }
    }
}
