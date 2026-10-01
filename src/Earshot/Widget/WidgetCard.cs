using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// Which of the card's focusable items has the keyboard focus. There are no child controls, so focus is
// tracked here and painted as the system focus rectangle.
internal enum WidgetCardFocus { Button, Switch, SetupButton, Gear, UpdateButton }

// Which control of a set-up page has the keyboard focus: the back button, one of a picker's two chevrons or
// its Charging toggle (Index is the picker, 0 to 2), or a footer button (Index is the button).
internal enum SetupTargetKind { Back, Up, Down, Charging, Button }

internal readonly record struct SetupTarget(SetupTargetKind Kind, int Index);

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
    DateTimeOffset Now,
    bool ShowSetupButton,   // true while no part has a percent: the grid gives way to "Set up battery"
    WidgetCardView View = WidgetCardView.Main,
    SetupViewModel? Setup = null,
    string? UpdateVersion = null,     // a check found this newer version: the update line shows
    CardSettingsValues? Settings = null)
{
    public static WidgetCardModel Empty { get; } = new(
        WidgetSnapshot.Empty(WidgetWatcherState.NotStarted, claimExists: false),
        AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true, ButtonEnabled: true,
        OtherDeviceLabel: "", Now: DateTimeOffset.UtcNow, ShowSetupButton: false);
}

// The dedicated three-column card: Left, Right, Case battery, where the AirPods are, when the battery was
// last read, and a Connect/Disconnect button, plus the auto-pause switch when the snapshot says it is
// available. Built for the gauge's left click with LeftClickConnects off (the default): ConnectCard/
// CardPresenter, the tray's existing one-line card, has no room for three battery columns, a where-line and
// an auto-pause switch together, so the gauge gets its own card rather than reusing that one.
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
internal sealed partial class WidgetCard : Form
{
    // The accent used until something supplies the system's own (AccentSource): the Windows default blue,
    // darker in light mode and lighter in dark mode, since the same light-mode blue reads as too pale against
    // a light background.
    internal static readonly Color AccentLight = DefaultCardAccent.Light;
    internal static readonly Color AccentDark = DefaultCardAccent.Dark;

    private readonly ILog _log;
    private readonly bool _notice;
    private WidgetCardModel _model = WidgetCardModel.Empty;
    private int _dpi = CardPlacement.BaseDpi;
    private CardPalette _palette = CardTheme.Light;
    private bool _dark;
    private bool _dwmBackdropOk;
    private bool _cornersApplied;
    private WidgetCardFocus _focus = WidgetCardFocus.Button;
    private SetupTarget _setupFocus = new(SetupTargetKind.Button, 0);
    private WidgetCardView _shownView = WidgetCardView.Main;
    private WidgetCardLayout.Layout _mainLayout = WidgetCardLayout.Compute(CardPlacement.BaseDpi, showSwitch: false);
    private WidgetCardLayout.SetupLayout? _setupLayout;
    private string? _lastFrameProblem;
    private bool _leftButtonDownOnButton;
    private bool _leftButtonDownOnSwitch;
    private bool _leftButtonDownOnSetupButton;
    private bool _leftButtonDownOnGear;
    private bool _leftButtonDownOnUpdate;
    private SetupTarget? _leftButtonDownOnSetupTarget;

    public WidgetCard(ILog log, bool notice = false)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _notice = notice;

        Text = TrayStatus.AppName;
        // AccessibleName was never set (null, confirmed by reading it directly rather than assuming a
        // fallback to Text): a screen reader had nothing here to tell this card apart from the gauge, the
        // tray icon or any other Earshot window. GaugeWindow already sets its own explicit name for the
        // same reason; this card gets one that also tells the ordinary card and the case-open notice apart.
        AccessibleName = TrayStatus.AppName + ": " + (notice ? WidgetCopy.CaseOpen : "AirPods");
        AccessibleRole = AccessibleRole.Window;
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

    // The user activated the "Set up battery" button on the main view (Enter, Space or a mouse click on it).
    public event EventHandler? SetupRequested;

    // The user activated the gear in the title row (Enter, Space or a mouse click on it).
    public event EventHandler? SettingsRequested;

    // The user activated the "Update" button on the update line (Enter, Space or a mouse click on it).
    public event EventHandler? UpdateRequested;

    // The user pressed a button of a set-up page, or its back button, or Escape.
    public event EventHandler<SetupAction>? SetupActionRequested;

    // The user changed a picker or a Charging toggle on the Pick page, to the new picks.
    public event EventHandler<BatterySetupPicks>? SetupPicksChanged;

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
            if (WantsLayeredStyle)
            {
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED;
            }

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

    // Which control of the set-up page has the focus, for tests.
    internal SetupTarget SetupFocusTarget => _setupFocus;

    // The view this card draws: notice mode always draws the main view, whatever the model says.
    internal WidgetCardView EffectiveView => _notice ? WidgetCardView.Main : _model.View;

    // The layouts the last Render computed, for tests.
    internal WidgetCardLayout.Layout CurrentMainLayout => _mainLayout;

    internal WidgetCardLayout.SetupLayout? CurrentSetupLayout => _setupLayout;

    // Where the card's accent colour comes from (ICardAccentSource). Asked at every paint, so the system's own colour, once
    // supplied, shows on the next one. Until then the design's default blue.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ICardAccentSource AccentSource { get; set; } = DefaultCardAccent.Instance;

    private CardColours Colours => CardColours.For(_dark, _palette, AccentSource.AccentFor(lightTheme: !_dark));

    private IAccentColours? _accent;
    private int _accentRepaints;

    // How many times an accent change asked this card to repaint, for tests.
    internal int AccentRepaintsForTest => _accentRepaints;

    // Takes the card's accent from the system accent service and repaints the card, open or not, whenever the owner
    // changes it. The service raises Changed on the UI thread.
    internal void AttachAccent(IAccentColours accent)
    {
        ArgumentNullException.ThrowIfNull(accent);
        DetachAccent();
        _accent = accent;
        AccentSource = new AccentColoursSource(accent);
        accent.Changed += OnAccentChanged;
    }

    private void DetachAccent()
    {
        if (_accent is { } accent)
        {
            accent.Changed -= OnAccentChanged;
            _accent = null;
        }
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _accentRepaints++;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachAccent();
            StopMotion();
            DisposeTips();
        }

        base.Dispose(disposing);
    }

    // The location line OnPaint is about to draw: the live Where reading, or always "Case open" for a
    // notice-mode instance regardless of what the model's own Snapshot.Where says. For tests.
    internal string WhereLineText => _notice ? WidgetCopy.CaseOpen : WidgetCopy.Where(_model.Snapshot.Where, _model.OtherDeviceLabel);

    // True once DWM accepted the translucent backdrop and DwmExtendFrameIntoClientArea for this window's
    // life; false means the opaque palette paint is used instead.
    internal bool HasTranslucentBackdrop => _dwmBackdropOk;

    // Set only by the probe widget capture path: DWM's own translucent backdrop is never actually
    // composited for a Form that is never shown, and Control.DrawToBitmap cannot recover or preserve real
    // per-pixel alpha from OnPaint's own transparent clear (it bakes straight to opaque black, GDI's own
    // text and fill calls forcing alpha to 255 as they go). Overriding the clear colour outright, before
    // any capture, is the only way a probe image ends up with a deliberate, readable background instead.
    // Never set in the running tray: HasTranslucentBackdrop still reports what DWM itself did.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Color? OverrideBackgroundForCaptureOnly { get; set; }

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
        ReadLook();

        if (EffectiveView == WidgetCardView.Settings && model.Settings is { } settingsValues)
        {
            RenderSettings(settingsValues);
            return;
        }

        _settingsLayout = null;
        if (EffectiveView is not (WidgetCardView.Main or WidgetCardView.Settings) && model.Setup is { } setup)
        {
            if (_shownView != model.View)
            {
                // A new page: focus starts on its primary button, so Enter does what the page's main button does.
                int primary = 0;
                for (int i = 0; i < setup.Buttons.Count; i++)
                {
                    if (setup.Buttons[i].Primary)
                    {
                        primary = i;
                    }
                }

                _setupFocus = new SetupTarget(SetupTargetKind.Button, primary);
            }

            _shownView = model.View;
            using var probe = new Bitmap(1, 1);
            using Graphics measure = Graphics.FromImage(probe);
            _setupLayout = ComputeSetupLayout(measure, setup);
            ClientSize = new Size(_setupLayout.Frame.Width, _setupLayout.Frame.Height);
            Invalidate();
            return;
        }

        _shownView = WidgetCardView.Main;
        _setupLayout = null;
        using (var probe = new Bitmap(1, 1))
        using (Graphics measure = Graphics.FromImage(probe))
        {
            _mainLayout = ComputeMainLayout(measure, model);
        }

        WidgetCardLayout.Layout layout = _mainLayout;
        if (_focus == WidgetCardFocus.Switch && !model.ShowSwitch)
        {
            // The switch just disappeared under the focus; move it back to the button rather than leave
            // focus pointed at a row that no longer draws.
            _focus = WidgetCardFocus.Button;
        }

        if (_focus == WidgetCardFocus.SetupButton && !_mainLayout.ShowSetupButton)
        {
            // Same rule for the set-up button: a reading arriving between two renders removes it, so focus
            // must not stay pointed at it.
            _focus = WidgetCardFocus.Button;
        }

        if (_focus == WidgetCardFocus.UpdateButton && !_mainLayout.ShowUpdateLine)
        {
            _focus = WidgetCardFocus.Button;
        }

        if (_focus == WidgetCardFocus.Gear && _mainLayout.Gear.IsEmpty)
        {
            _focus = WidgetCardFocus.Button;
        }

        ClientSize = new Size(layout.Width, layout.Height);
        Invalidate();
    }

    // The label the main view's set-up button carries: "Try again" once the newest set-up saw only forms
    // that cannot be read.
    private string SetupButtonLabel => _model.Snapshot.SetupCouldNotRead ? WidgetCopy.TryAgain : WidgetCopy.SetUpBattery;

    private WidgetCardLayout.Layout ComputeMainLayout(Graphics measure, WidgetCardModel model)
    {
        bool showSetup = model.ShowSetupButton && !_notice;
        int buttonWidth = 0;
        if (showSetup)
        {
            using Font font = _type.Font(CardPlacement.Scale(14, _dpi), bold: false);
            SizeF size = measure.MeasureString(SetupButtonLabel, font, int.MaxValue, StringFormat.GenericTypographic);
            buttonWidth = (int)Math.Ceiling(size.Width) + (2 * CardPlacement.Scale(12, _dpi));
        }

        bool showUpdate = model.UpdateVersion is not null && !_notice;
        int updateButtonWidth = 0;
        if (showUpdate)
        {
            using Font font = _type.Font(CardPlacement.Scale(12, _dpi), bold: false);
            SizeF size = measure.MeasureString(WidgetCopy.UpdateButton, font, int.MaxValue, StringFormat.GenericTypographic);
            updateButtonWidth = Math.Max(
                CardPlacement.Scale(WidgetCardLayout.UpdateButtonMinWidthAt96, _dpi),
                (int)Math.Ceiling(size.Width) + (2 * CardPlacement.Scale(WidgetCardLayout.UpdateButtonPaddingAt96, _dpi)));
        }

        return WidgetCardLayout.Compute(
            _dpi, model.ShowSwitch, showSetup, showSetup && model.Snapshot.SetupCouldNotRead, buttonWidth,
            showGear: !_notice, showUpdateLine: showUpdate, updateButtonWidth: updateButtonWidth, textScale: _look.TextScale);
    }

    private WidgetCardLayout.SetupLayout ComputeSetupLayout(Graphics measure, SetupViewModel setup)
    {
        int side = CardPlacement.Scale(WidgetCardLayout.BodySideAt96, _dpi);
        int contentWidth = CardPlacement.Scale(SubPageFrame.WidthAt96, _dpi) - (2 * side);
        int textWidth = contentWidth - CardPlacement.Scale(WidgetCardLayout.StatusIconAt96 + WidgetCardLayout.StatusIconGapAt96, _dpi);
        int promptLines = setup.Prompt is null ? 1 : CardPaint.Lines(measure, setup.Prompt, contentWidth, _type, CardPlacement.Scale(14, _dpi), bold: true, CardPlacement.Scale(WidgetCardLayout.PromptLineAt96, _dpi));
        int captionLines = setup.Caption is null ? 1 : CardPaint.Lines(measure, setup.Caption, contentWidth, _type, CardPlacement.Scale(12, _dpi), bold: false, CardPlacement.Scale(WidgetCardLayout.CaptionLineAt96, _dpi));
        int subLines = setup.StatusSub is null ? 1 : CardPaint.Lines(measure, setup.StatusSub, textWidth, _type, CardPlacement.Scale(12, _dpi), bold: false, CardPlacement.Scale(WidgetCardLayout.CaptionLineAt96, _dpi));
        return WidgetCardLayout.Setup(setup, _dpi, promptLines, captionLines, subLines, _look.TextScale);
    }

    // Advances the spinner to frame and repaints only its icon. The presenter's 100 ms timer calls this; a
    // whole Render (a new layout, a new size) is not needed for a turning arc.
    internal void SetSpinnerFrame(int frame)
    {
        if (_model.Setup is not { } setup || _setupLayout is not { } layout)
        {
            return;
        }

        _model = _model with { Setup = setup with { SpinnerFrame = frame } };
        if (IsHandleCreated)
        {
            Invalidate(layout.StatusIcon);
        }
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

        // Capture has moved on (another window took it, or a modal loop or Alt+Tab cancelled the press): a
        // press that started here is over, so a later, unrelated button-up must not answer as a click. Neither
        // message asks the window to release capture itself.
        // https://learn.microsoft.com/en-us/windows/win32/inputmsg/wm-capturechanged
        // https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-cancelmode
        if (m.Msg is NativeMethods.WM_CAPTURECHANGED or WmCancelMode)
        {
            ClearPressedFlags();
        }

        base.WndProc(ref m);
    }

    private const int WmCancelMode = 0x001F;

    private void ClearPressedFlags()
    {
        _leftButtonDownOnButton = false;
        _leftButtonDownOnSwitch = false;
        _leftButtonDownOnSetupButton = false;
        _leftButtonDownOnGear = false;
        _leftButtonDownOnUpdate = false;
        _leftButtonDownOnSetupTarget = null;
        _leftButtonDownOnSettingsTarget = null;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        // A set-up page stays open when the card loses focus: the owner reaches for his case and his phone
        // in the middle of it. Escape, Cancel, Back and Done are the ways out.
        if (!_notice && EffectiveView is WidgetCardView.Main or WidgetCardView.Settings)
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
            case Keys.Up:
            case Keys.Down:
                return true;
            case Keys.Left:
            case Keys.Right:
            case Keys.Home:
            case Keys.End:
                return _editingText || (OnSettingsPage && _settingsFocus.Part == SettingsPart.Tile) || base.IsInputKey(keyData);
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_notice)
        {
            // No focus, no focus visual, keyboard does nothing: a notice-mode card is never activated
            // (WS_EX_NOACTIVATE), so it should never receive a key in practice; this guard makes that true
            // even if a key event ever reached it regardless.
            return;
        }

        if (_exiting)
        {
            // The card has been closed and is only fading out: a key must not act on a card that is going away.
            e.Handled = true;
            return;
        }

        NoteKeyForFocusCue(e.KeyData);
        if (OnSettingsPage)
        {
            HandleSettingsKey(e.KeyData);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        if (EffectiveView is not (WidgetCardView.Main or WidgetCardView.Settings) && _model.Setup is not null)
        {
            HandleSetupKey(e.KeyCode, e.Shift);
            e.Handled = true;
            base.OnKeyDown(e);
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
    // control before it activates anything: any other button (right, middle) never sets any of the flags,
    // so its own up can never activate the button, the switch or the set-up button either.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ArgumentNullException.ThrowIfNull(e);
        if (_exiting)
        {
            // Nothing pressed on a card that is fading out counts, so there is nothing for a later up to complete.
            ClearPressedFlags();
            return;
        }

        NoteMouseForFocusCue();
        HideTip();
        if (OnSettingsPage)
        {
            SettingsMouseDown(e);
            return;
        }

        if (EffectiveView is not (WidgetCardView.Main or WidgetCardView.Settings) && _setupLayout is not null)
        {
            _leftButtonDownOnSetupTarget = e.Button == MouseButtons.Left ? HitSetupTarget(e.Location) : null;
            return;
        }

        WidgetCardLayout.Layout layout = _mainLayout;
        _leftButtonDownOnButton = e.Button == MouseButtons.Left && layout.Button.Contains(e.Location);
        _leftButtonDownOnSwitch = e.Button == MouseButtons.Left && layout.ShowSwitch && layout.Switch.Contains(e.Location);
        _leftButtonDownOnSetupButton = e.Button == MouseButtons.Left && layout.ShowSetupButton && layout.SetupButton.Contains(e.Location);
        _leftButtonDownOnGear = e.Button == MouseButtons.Left && !layout.Gear.IsEmpty && layout.Gear.Contains(e.Location);
        _leftButtonDownOnUpdate = e.Button == MouseButtons.Left && layout.ShowUpdateLine && layout.UpdateButton.Contains(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ArgumentNullException.ThrowIfNull(e);
        if (_exiting)
        {
            ClearPressedFlags();
            return;
        }

        if (OnSettingsPage)
        {
            SettingsMouseUp(e);
            return;
        }

        if (EffectiveView is not (WidgetCardView.Main or WidgetCardView.Settings) && _setupLayout is not null)
        {
            SetupTarget? down = _leftButtonDownOnSetupTarget;
            _leftButtonDownOnSetupTarget = null;
            if (e.Button == MouseButtons.Left && down is { } pressed && HitSetupTarget(e.Location) == pressed)
            {
                _setupFocus = pressed;
                ActivateSetupTarget(pressed);
            }

            return;
        }

        WidgetCardLayout.Layout layout = _mainLayout;

        // A left up activates a control only when the matching left down already landed on that same
        // control: a drag that started outside the button, the switch or the set-up button and released
        // inside it, or any up from a button other than left (right, middle), must not count as pressing
        // it, the same rule GaugeWindow's own left-click handling already applies.
        bool activatesButton = e.Button == MouseButtons.Left && _leftButtonDownOnButton && layout.Button.Contains(e.Location);
        bool activatesSwitch = e.Button == MouseButtons.Left && _leftButtonDownOnSwitch && layout.ShowSwitch && layout.Switch.Contains(e.Location);
        bool activatesSetupButton = e.Button == MouseButtons.Left && _leftButtonDownOnSetupButton && layout.ShowSetupButton && layout.SetupButton.Contains(e.Location);
        bool activatesGear = e.Button == MouseButtons.Left && _leftButtonDownOnGear && !layout.Gear.IsEmpty && layout.Gear.Contains(e.Location);
        bool activatesUpdate = e.Button == MouseButtons.Left && _leftButtonDownOnUpdate && layout.ShowUpdateLine && layout.UpdateButton.Contains(e.Location);
        ClearPressedFlags();

        if (activatesGear)
        {
            _focus = WidgetCardFocus.Gear;
            ActivateFocused();
        }
        else if (activatesUpdate)
        {
            _focus = WidgetCardFocus.UpdateButton;
            ActivateFocused();
        }
        else if (activatesButton)
        {
            _focus = WidgetCardFocus.Button;
            ActivateFocused();
        }
        else if (activatesSwitch)
        {
            _focus = WidgetCardFocus.Switch;
            ActivateFocused();
        }
        else if (activatesSetupButton)
        {
            _focus = WidgetCardFocus.SetupButton;
            ActivateFocused();
        }
        else if (_notice)
        {
            // A notice-mode card is dismissed by a click outside the button, the switch and the set-up
            // button: only reachable in notice mode, since the normal card already closes on deactivation
            // for a click anywhere else.
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
        RenderContent(e.Graphics);
    }

    // The card's whole content, factored out of OnPaint so a capture path can paint it straight into an
    // off-screen Bitmap's Graphics with no Form, no window handle and no Show/DrawToBitmap call ever
    // involved. Control.DrawToBitmap on a top-level, activatable Form like this one (normal mode has no
    // WS_EX_NOACTIVATE, since it takes real keyboard focus) was found to make the window briefly visible,
    // and normal mode briefly take the foreground, on whatever desktop the calling thread is attached to,
    // to do its own internal layout - the owner's real desktop for a thread never given a private one. This
    // never touches Handle, so nothing here can do that: WidgetCardTests' own pure checks and Earshot.exe
    // probe widget (ProbeWidget.cs) both call this directly instead.
    internal void RenderContent(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Half: a coordinate names a pixel edge, not a pixel centre. The card sets its strokes on the half pixel
        // (x + 0.5) so a one pixel outline is one crisp row; under the default offset the same stroke fell across
        // two rows at half strength, which is what made a thin outline look faint on a dark card.
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(OverrideBackgroundForCaptureOnly ?? (PaintsOpaqueBackground ? _palette.Background : Color.FromArgb(0, 0, 0, 0)));

        if (OnSettingsPage && _model.Settings is { } settingsValues && _settingsLayout is { } settingsLayout)
        {
            DrawSettings(g, settingsValues, settingsLayout);
            return;
        }

        if (EffectiveView is not (WidgetCardView.Main or WidgetCardView.Settings) && _model.Setup is { } setup && _setupLayout is { } setupLayout)
        {
            DrawSetup(g, setup, setupLayout);
            return;
        }

        WidgetCardLayout.Layout layout = _mainLayout;
        DrawTitleRow(g, layout);
        if (layout.ShowColumns)
        {
            DrawColumnLabel(g, layout.Left.Label, WidgetCopy.LeftLabel);
            DrawColumnLabel(g, layout.Right.Label, WidgetCopy.RightLabel);
            DrawColumnLabel(g, layout.Case.Label, WidgetCopy.CaseLabel);
            DrawEarbudColumn(g, layout.Left, _model.Snapshot.Left, mirror: false);
            DrawEarbudColumn(g, layout.Right, _model.Snapshot.Right, mirror: true);
            DrawCaseColumn(g, layout.Case, _model.Snapshot.Case);
        }

        DrawIconLine(g, layout.WhereLine, FluentGlyphs.Location, WhereLineText, WhereLineText);
        DrawIconLine(
            g, layout.ReadLine, FluentGlyphs.Clock, WidgetCopy.ReadAge(_model.Snapshot.BatteryReadAt, _model.Now),
            WidgetCopy.BatteryReadLine(_model.Snapshot.BatteryReadAt, _model.Now));
        if (layout.ShowSetupButton)
        {
            DrawSetupButton(g, layout);
        }

        if (layout.ShowUpdateLine)
        {
            DrawUpdateLine(g, layout);
        }

        DrawButton(g, layout.Button);
        if (layout.ShowSwitch)
        {
            DrawSwitch(g, layout.Switch);
        }
    }

    // "AirPods" at 14 px semibold, and the gear when the view has one.
    private void DrawTitleRow(Graphics g, WidgetCardLayout.Layout layout)
    {
        CardColours colours = Colours;
        CardPaint.Text(g, WidgetCopy.CardTitle, layout.Title, _type, CardPlacement.Scale(14, _dpi), bold: true, colours.Text, StringAlignment.Near, StringAlignment.Center);
        if (layout.Gear.IsEmpty)
        {
            return;
        }

        if (!CardPaint.TryGlyph(g, FluentGlyphs.Settings, layout.Gear, colours.Text, _dpi))
        {
            CardPaint.Gear(g, layout.Gear, colours.Text, _dpi);
        }

        if (_focus == WidgetCardFocus.Gear && FocusShown)
        {
            CardPaint.Focus(g, layout.Gear, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
        }
    }

    // The line that says a newer version is out, with a 24 px Update button: a divider above it, the caption
    // left. Nothing here downloads; the button only asks to open the update page.
    private void DrawUpdateLine(Graphics g, WidgetCardLayout.Layout layout)
    {
        CardColours colours = Colours;
        CardPaint.Divider(g, layout.UpdateLine.Left, layout.UpdateLine.Right, layout.UpdateLine.Top, colours);
        string version = _model.UpdateVersion ?? string.Empty;
        int box = CardPlacement.Scale(CardPaint.GlyphSizeAt96, _dpi);
        var iconRect = new Rectangle(layout.UpdateCaption.X, layout.UpdateCaption.Y + ((layout.UpdateCaption.Height - box) / 2), box, box);
        bool hasIcon = CardPaint.TryGlyph(g, FluentGlyphs.Download, iconRect, colours.TextSecondary, _dpi);
        int indent = hasIcon ? box + CardPlacement.Scale(8, _dpi) : 0;
        CardPaint.Text(
            g, hasIcon ? WidgetCopy.UpdateAvailableShort(version) : WidgetCopy.UpdateAvailable(version),
            new Rectangle(layout.UpdateCaption.X + indent, layout.UpdateCaption.Y, Math.Max(1, layout.UpdateCaption.Width - indent), layout.UpdateCaption.Height), _type,
            CardPlacement.Scale(12, _dpi), bold: false, colours.TextSecondary, StringAlignment.Near, StringAlignment.Center);
        CardPaint.SmallButton(g, layout.UpdateButton, WidgetCopy.UpdateButton, colours, _type, _dpi, focused: _focus == WidgetCardFocus.UpdateButton && FocusShown);
    }

    // Cycles Button, Switch (when shown), SetupButton (when shown), UpdateButton (when shown), Gear (when the view
    // has one), back to Button. The set-up button sits above the Connect button on the card, but is the least
    // central of the three, so Tab reaches it after them; the update line and the gear come last.
    private void MoveFocus()
    {
        var order = new List<WidgetCardFocus> { WidgetCardFocus.Button };
        if (_model.ShowSwitch)
        {
            order.Add(WidgetCardFocus.Switch);
        }

        if (_mainLayout.ShowSetupButton)
        {
            order.Add(WidgetCardFocus.SetupButton);
        }

        if (_mainLayout.ShowUpdateLine)
        {
            order.Add(WidgetCardFocus.UpdateButton);
        }

        if (!_mainLayout.Gear.IsEmpty)
        {
            order.Add(WidgetCardFocus.Gear);
        }

        int at = order.IndexOf(_focus);
        _focus = order[(at + 1) % order.Count];
        Invalidate();
        NoteFocusMoved();
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
        else if (_focus == WidgetCardFocus.Gear)
        {
            if (!_mainLayout.Gear.IsEmpty)
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_focus == WidgetCardFocus.UpdateButton)
        {
            if (_mainLayout.ShowUpdateLine)
            {
                UpdateRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_focus == WidgetCardFocus.SetupButton)
        {
            if (!_mainLayout.ShowSetupButton)
            {
                return;
            }

            // The card stays open: the set-up runs on it, in its own pages.
            SetupRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (_model.ShowSwitch)
        {
            AutoPauseChanged?.Invoke(this, !_model.AutoPauseOn);
        }
    }

    private void RequestClose(WidgetCardCloseReason reason)
    {
        EndEdits(commit: true);
        if (Visible)
        {
            HideAnimated();
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
    // is filled directly with GDI+ FillPath, matching this card's own owner-painted model (OnPaint fills
    // the whole client area itself), rather than composited through EarbudGlyph's alpha buffer (built for
    // the layered gauge's UpdateLayeredWindow, not an owner-painted Form).
    private static GraphicsPath BudGlyphPath(Rectangle bounds, bool mirror)
    {
        // Winding, not the default Alternate: the head (an ellipse) and the stem (a rounded rectangle)
        // overlap where the stem meets the head, and Alternate XORs that overlap into a hole instead of
        // filling it solid.
        var path = new GraphicsPath { FillMode = FillMode.Winding };
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

    // "L", "R" or "Case" above the glyph, centred, in the same status ink the where/read lines use.
    private void DrawColumnLabel(Graphics g, Rectangle bounds, string text)
    {
        using Font font = _type.Role(TypeRole.CaptionStrong);
        using var brush = new SolidBrush(_palette.Status);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        g.DrawString(text, font, brush, bounds, format);
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

    // The bar and the charging bolt are absent entirely when Percent is null: there is nothing to show a
    // bar or a bolt for. The words "No reading" take the percent line's own place instead (the owner's own
    // instruction): never a number, never a dash standing in for a reading that was never taken.
    private void DrawBatteryPart(Graphics g, WidgetCardLayout.ColumnLayout column, PartReading part)
    {
        if (part.Percent is not { } percent)
        {
            DrawLine(g, column.Percent, WidgetCopy.Percent(null), _palette.Status);
            return;
        }

        using (var track = new SolidBrush(Color.FromArgb(64, _palette.Title)))
        {
            g.FillRectangle(track, column.Bar);
        }

        int filled = (int)Math.Round(column.Bar.Width * Math.Clamp(percent, 0, 100) / 100.0);
        if (filled > 0)
        {
            // The bar is filled with the accent colour, never a fixed blue.
            using var fill = new SolidBrush(Colours.Accent);
            g.FillRectangle(fill, column.Bar.X, column.Bar.Y, filled, column.Bar.Height);
        }

        if (part.Charging == true)
        {
            DrawBolt(g, column.Bar);
        }

        using Font font = _type.Role(TypeRole.Number);
        using var textBrush = new SolidBrush(_palette.Status);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
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

    // A line of text with its icon: the icon at the left, then the words. With no icon font the line says it in full
    // words instead, since the icon is what made the short ones clear.
    private void DrawIconLine(Graphics g, Rectangle bounds, char glyph, string text, string fullText, TypeRole role = TypeRole.Caption)
    {
        int box = CardPlacement.Scale(CardPaint.GlyphSizeAt96, _dpi);
        var iconRect = new Rectangle(bounds.X, bounds.Y + ((bounds.Height - box) / 2), box, box);
        if (!CardPaint.TryGlyph(g, glyph, iconRect, _palette.Status, _dpi))
        {
            DrawLine(g, bounds, fullText, _palette.Status, role);
            return;
        }

        int indent = box + CardPlacement.Scale(8, _dpi);
        DrawLine(g, new Rectangle(bounds.X + indent, bounds.Y, Math.Max(1, bounds.Width - indent), bounds.Height), text, _palette.Status, role);
    }

    private void DrawLine(Graphics g, Rectangle bounds, string text, Color colour, TypeRole role = TypeRole.Caption)
    {
        using Font font = _type.Role(role);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, bounds, format);
    }

    private void DrawButton(Graphics g, Rectangle rect)
    {
        bool connect = _model.ConnectIntent;
        Color fill = connect
            ? Colours.Accent
            : (_dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xE4, 0xE4, 0xE4));
        if (!_model.ButtonEnabled)
        {
            fill = Color.FromArgb(120, fill);
        }

        Color text = connect ? Colours.OnAccent : _palette.Title;
        using var path = new GraphicsPath();
        AddRoundedRect(path, rect, rect.Height / 2f);
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        using (Font font = _type.Role(TypeRole.Body))
        using (var textBrush = new SolidBrush(text))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString(connect ? WidgetCopy.Connect : WidgetCopy.Disconnect, font, textBrush, rect, format);
        }

        if (!_notice && _focus == WidgetCardFocus.Button && FocusShown)
        {
            CardPaint.Focus(g, rect, rect.Height / 2, Colours, _dpi);
        }
    }

    // The switch row: the label, and the design's toggle (accent when on, no fill and a secondary-text outline when
    // off, so the off track shows on the dark card as well).
    private void DrawSwitch(Graphics g, Rectangle rect)
    {
        CardColours colours = Colours;
        int trackWidth = CardPlacement.Scale(WidgetCardLayout.ToggleWidthAt96, _dpi);
        int trackHeight = CardPlacement.Scale(WidgetCardLayout.ToggleHeightAt96, _dpi);
        var labelRect = new Rectangle(rect.X, rect.Y, Math.Max(0, rect.Width - trackWidth - 8), rect.Height);
        DrawIconLine(g, labelRect, FluentGlyphs.EarbudForThisPc(), WidgetCopy.AutoPauseSwitch, WidgetCopy.NamePauseBud, TypeRole.Body);

        var track = new Rectangle(rect.Right - trackWidth, rect.Y + ((rect.Height - trackHeight) / 2), trackWidth, trackHeight);
        CardPaint.Toggle(g, track, _model.AutoPauseOn, colours, _dpi);

        if (!_notice && _focus == WidgetCardFocus.Switch && FocusShown)
        {
            CardPaint.Focus(g, rect, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
        }
    }

    // The main view's one primary button when no part has a reading: the same accent fill as Connect, with
    // the focus rectangle when it has the keyboard focus.
    private void DrawSetupButton(Graphics g, WidgetCardLayout.Layout layout)
    {
        if (layout.SetupCaption != Rectangle.Empty)
        {
            DrawLine(g, layout.SetupCaption, WidgetCopy.SetupCannotReadYet, _palette.Status);
        }

        bool focused = !_notice && _focus == WidgetCardFocus.SetupButton && FocusShown;
        CardPaint.Button(g, layout.SetupButton, SetupButtonLabel, primary: true, Colours, _type, _dpi, focused);
    }

    // ---- The set-up pages

    // The keyboard order of a set-up page: the back button, then for each picker its up chevron, its down
    // chevron and its Charging toggle, then the footer buttons.
    private List<SetupTarget> SetupTargets()
    {
        var targets = new List<SetupTarget> { new(SetupTargetKind.Back, 0) };
        if (_model.Setup is { } setup)
        {
            if (setup.Picks is not null)
            {
                for (int i = 0; i < 3; i++)
                {
                    targets.Add(new SetupTarget(SetupTargetKind.Up, i));
                    targets.Add(new SetupTarget(SetupTargetKind.Down, i));
                    targets.Add(new SetupTarget(SetupTargetKind.Charging, i));
                }
            }

            for (int i = 0; i < setup.Buttons.Count; i++)
            {
                targets.Add(new SetupTarget(SetupTargetKind.Button, i));
            }
        }

        return targets;
    }

    private SetupTarget? HitSetupTarget(Point point)
    {
        if (_setupLayout is not { } layout || _model.Setup is not { } setup)
        {
            return null;
        }

        if (layout.Frame.Back.Contains(point))
        {
            return new SetupTarget(SetupTargetKind.Back, 0);
        }

        for (int i = 0; i < layout.Frame.Buttons.Count && i < setup.Buttons.Count; i++)
        {
            if (layout.Frame.Buttons[i].Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Button, i);
            }
        }

        for (int i = 0; i < layout.Pickers.Count; i++)
        {
            WidgetCardLayout.PickerLayout picker = layout.Pickers[i];
            if (picker.Up.Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Up, i);
            }

            if (picker.Down.Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Down, i);
            }

            if (picker.Toggle.Contains(point) || picker.ChargingLabel.Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Charging, i);
            }
        }

        return null;
    }

    // The keys of a set-up page. Internal so a test can send one without a window handle. Tab and Shift+Tab
    // move the focus through SetupTargets; the Up and Down arrows step the picker the focus is in; Space
    // activates what has the focus; Enter is the page's primary button (or the focused footer button);
    // Escape is Back.
    internal void HandleSetupKey(Keys key, bool shift = false)
    {
        NoteKeyForFocusCue(key);
        List<SetupTarget> targets = SetupTargets();
        int index = -1;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == _setupFocus)
            {
                index = i;
            }
        }

        switch (key)
        {
            case Keys.Tab:
                index = index < 0 ? 0 : (index + (shift ? targets.Count - 1 : 1)) % targets.Count;
                _setupFocus = targets[index];
                Invalidate();
                NoteFocusMoved();
                break;
            case Keys.Escape:
                SetupActionRequested?.Invoke(this, SetupAction.Back);
                break;
            case Keys.Enter:
                if (_setupFocus.Kind == SetupTargetKind.Button)
                {
                    ActivateSetupTarget(_setupFocus);
                }
                else if (_model.Setup is { } setup)
                {
                    SetupButton? primary = setup.Buttons.FirstOrDefault(b => b.Primary) ?? (setup.Buttons.Count > 0 ? setup.Buttons[0] : null);
                    if (primary is not null)
                    {
                        SetupActionRequested?.Invoke(this, primary.Action);
                    }
                }

                break;
            case Keys.Space:
                ActivateSetupTarget(_setupFocus);
                break;
            case Keys.Up:
                if (_setupFocus.Kind is SetupTargetKind.Up or SetupTargetKind.Down or SetupTargetKind.Charging)
                {
                    StepPicker(_setupFocus.Index, +10);
                }

                break;
            case Keys.Down:
                if (_setupFocus.Kind is SetupTargetKind.Up or SetupTargetKind.Down or SetupTargetKind.Charging)
                {
                    StepPicker(_setupFocus.Index, -10);
                }

                break;
        }
    }

    private void ActivateSetupTarget(SetupTarget target)
    {
        if (_model.Setup is not { } setup)
        {
            return;
        }

        switch (target.Kind)
        {
            case SetupTargetKind.Back:
                SetupActionRequested?.Invoke(this, SetupAction.Back);
                break;
            case SetupTargetKind.Up:
                StepPicker(target.Index, +10);
                break;
            case SetupTargetKind.Down:
                StepPicker(target.Index, -10);
                break;
            case SetupTargetKind.Charging:
                if (setup.Picks is { } picks)
                {
                    ChangePicks(target.Index switch
                    {
                        0 => picks with { LeftCharging = !picks.LeftCharging },
                        1 => picks with { RightCharging = !picks.RightCharging },
                        _ => picks with { CaseCharging = !picks.CaseCharging },
                    });
                }

                break;
            case SetupTargetKind.Button:
                if (target.Index < setup.Buttons.Count)
                {
                    SetupActionRequested?.Invoke(this, setup.Buttons[target.Index].Action);
                }

                break;
        }
    }

    // Steps one picker by 10 either way, clamped to 0 to 100.
    private void StepPicker(int picker, int delta)
    {
        if (_model.Setup?.Picks is not { } picks)
        {
            return;
        }

        int Step(int value) => Math.Clamp(value + delta, 0, 100);
        ChangePicks(picker switch
        {
            0 => picks with { Left = Step(picks.Left) },
            1 => picks with { Right = Step(picks.Right) },
            _ => picks with { Case = Step(picks.Case) },
        });
    }

    private void ChangePicks(BatterySetupPicks picks)
    {
        if (_model.Setup is not { } setup || picks == setup.Picks)
        {
            return;
        }

        _model = _model with { Setup = setup with { Picks = picks } };
        SetupPicksChanged?.Invoke(this, picks);
        Invalidate();
    }

    private void DrawSetup(Graphics g, SetupViewModel setup, WidgetCardLayout.SetupLayout layout)
    {
        CardColours colours = Colours;
        bool focusVisible = FocusShown;
        SetupTarget focus = _setupFocus;

        SubPageFrame.DrawHeader(g, layout.Frame, setup.Title, setup.Step, colours, _type, _dpi, backFocused: focusVisible && focus.Kind == SetupTargetKind.Back);

        if (setup.Prompt is not null)
        {
            CardPaint.Wrapped(g, setup.Prompt, layout.Prompt, _type, CardPlacement.Scale(14, _dpi), bold: true, colours.Text);
        }

        if (setup.Caption is not null)
        {
            CardPaint.Wrapped(g, setup.Caption, layout.Caption, _type, CardPlacement.Scale(12, _dpi), bold: false, colours.TextSecondary);
        }

        if (setup.Status is not null)
        {
            switch (setup.Icon)
            {
                case SetupIcon.Spinner:
                    CardPaint.Spinner(g, layout.StatusIcon, colours.Accent, setup.SpinnerFrame, _dpi);
                    break;
                case SetupIcon.Check:
                    CardPaint.CheckIcon(g, layout.StatusIcon, colours.Accent, _dpi);
                    break;
                case SetupIcon.Caution:
                    CardPaint.CautionIcon(g, layout.StatusIcon, colours.Caution, _dpi);
                    break;
                case SetupIcon.Down:
                    CardPaint.DownIcon(g, layout.StatusIcon, colours.Text, _dpi);
                    break;
                case SetupIcon.Shield:
                    CardPaint.ShieldIcon(g, layout.StatusIcon, colours.Text, _dpi);
                    break;
            }

            CardPaint.Text(g, setup.Status, layout.StatusText, _type, CardPlacement.Scale(14, _dpi), bold: false, colours.Text, StringAlignment.Near, StringAlignment.Center);
            if (setup.StatusSub is not null)
            {
                CardPaint.Wrapped(g, setup.StatusSub, layout.StatusSub, _type, CardPlacement.Scale(12, _dpi), bold: false, colours.TextSecondary);
            }
        }

        if (setup.ShowProgress && !layout.Progress.IsEmpty)
        {
            DrawProgress(g, setup, layout.Progress, colours);
        }

        if (setup.Picks is { } picks)
        {
            string[] labels = [WidgetCopy.LeftLabel, WidgetCopy.RightLabel, WidgetCopy.CaseLabel];
            int[] values = [picks.Left, picks.Right, picks.Case];
            bool[] charging = [picks.LeftCharging, picks.RightCharging, picks.CaseCharging];
            for (int i = 0; i < layout.Pickers.Count; i++)
            {
                DrawPicker(g, layout.Pickers[i], labels[i], values[i], charging[i], colours, i, focusVisible ? focus : null);
            }
        }

        var buttons = setup.Buttons.Select(b => (b.Label, b.Primary)).ToList();
        int focusedButton = focusVisible && focus.Kind == SetupTargetKind.Button ? focus.Index : -1;
        SubPageFrame.DrawFooter(g, layout.Frame, buttons, colours, _type, _dpi, focusedButton);
    }

    // The download's bar, 4 px, with its percentage 36 wide at the right. While the size is not known the bar has
    // no fill and there is no figure: a percentage is only ever the download's own.
    private void DrawProgress(Graphics g, SetupViewModel setup, Rectangle row, CardColours colours)
    {
        int textWidth = CardPlacement.Scale(WidgetCardLayout.ProgressTextWidthAt96, _dpi);
        int gap = CardPlacement.Scale(WidgetCardLayout.ProgressGapAt96, _dpi);
        int barHeight = CardPlacement.Scale(WidgetCardLayout.ProgressBarHeightAt96, _dpi);
        var bar = new Rectangle(row.X, row.Y + ((row.Height - barHeight) / 2), Math.Max(1, row.Width - textWidth - gap), barHeight);
        CardPaint.ProgressBar(g, bar, setup.ProgressPercent ?? 0, colours);
        if (setup.ProgressPercent is { } percent)
        {
            var textRect = new Rectangle(row.Right - textWidth, row.Y, textWidth, row.Height);
            CardPaint.Text(
                g, percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%", textRect, _type, CardPlacement.Scale(12, _dpi),
                bold: false, colours.TextSecondary, StringAlignment.Far, StringAlignment.Center);
        }
    }

    private void DrawPicker(Graphics g, WidgetCardLayout.PickerLayout picker, string label, int value, bool charging, CardColours colours, int index, SetupTarget? focus)
    {
        int radius = CardPlacement.Scale(4, _dpi);
        using (GraphicsPath box = CardPaint.RoundedRectangle(new RectangleF(picker.Box.X + 0.5f, picker.Box.Y + 0.5f, picker.Box.Width - 1, picker.Box.Height - 1), radius))
        {
            using var fill = new SolidBrush(colours.ControlFill);
            g.FillPath(fill, box);
            using var pen = new Pen(colours.ControlStroke, 1f);
            g.DrawPath(pen, box);
        }

        CardPaint.Text(g, label, picker.Label, _type, CardPlacement.Scale(12, _dpi), bold: false, colours.TextSecondary, StringAlignment.Center, StringAlignment.Center);
        CardPaint.Chevron(g, picker.Up, up: true, colours.Text, _dpi);
        CardPaint.Chevron(g, picker.Down, up: false, colours.Text, _dpi);
        DrawPickerValue(g, picker.Value, value, colours);
        CardPaint.Text(g, WidgetCopy.Charging, picker.ChargingLabel, _type, CardPlacement.Scale(12, _dpi), bold: false, colours.TextSecondary, StringAlignment.Near, StringAlignment.Center);
        CardPaint.Toggle(g, picker.Toggle, charging, colours, _dpi);

        if (focus is { } f && f.Index == index)
        {
            switch (f.Kind)
            {
                case SetupTargetKind.Up:
                    CardPaint.Focus(g, picker.Up, radius, colours, _dpi);
                    break;
                case SetupTargetKind.Down:
                    CardPaint.Focus(g, picker.Down, radius, colours, _dpi);
                    break;
                case SetupTargetKind.Charging:
                    CardPaint.Focus(g, Rectangle.Union(picker.ChargingLabel, picker.Toggle), radius, colours, _dpi);
                    break;
            }
        }
    }

    // The picker's value at 20 px semibold with the percent sign at 12 px regular. Every digit sits in a cell
    // as wide as a "0", so the value does not shift as it changes.
    private void DrawPickerValue(Graphics g, Rectangle bounds, int value, CardColours colours)
    {
        string digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using Font big = _type.Font(CardPlacement.Scale(20, _dpi), bold: true);
        using Font small = _type.Font(CardPlacement.Scale(12, _dpi), bold: false);
        using var brush = new SolidBrush(colours.Text);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        float cell = g.MeasureString("0", big, int.MaxValue, StringFormat.GenericTypographic).Width;
        float percentWidth = g.MeasureString("%", small, int.MaxValue, StringFormat.GenericTypographic).Width;
        float total = (cell * digits.Length) + 1 + percentWidth;
        float x = bounds.X + ((bounds.Width - total) / 2f);
        foreach (char digit in digits)
        {
            g.DrawString(digit.ToString(), big, brush, new RectangleF(x, bounds.Y, cell, bounds.Height), format);
            x += cell;
        }

        using var percentBrush = new SolidBrush(colours.Text);
        g.DrawString("%", small, percentBrush, new RectangleF(x + 1, bounds.Y + (bounds.Height * 0.12f), percentWidth + 2, bounds.Height), format);
    }
}
