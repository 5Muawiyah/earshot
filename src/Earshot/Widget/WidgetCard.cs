using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// Which of the card's focusable items has the keyboard focus. There are no child controls, so focus is
// tracked here and painted as the system focus rectangle.
internal enum WidgetCardFocus { Button, Switch, Gear, UpdateButton, Refresh, Status }

// Which control of a sub-page has the keyboard focus: the back button, or a footer button (Index is the button).
internal enum SetupTargetKind { Back, Button, Row, Day }

internal readonly record struct SetupTarget(SetupTargetKind Kind, int Index);

// Why the card asked to be hidden. WidgetCardPresenter uses Deactivated to run the toggle-close rule: a
// second gauge click within SystemInformation.DoubleClickTime of a deactivate-close does not reopen it, the
// same gesture as a second click on the volume flyout's own icon. The other reasons it simply hides for.
//
// ClickOutside is notice mode only: a click that landed on the card but missed both the button and the
// switch. A notice-mode card is never activated (WS_EX_NOACTIVATE), so it never deactivates either; this
// is its only way to notice "the owner clicked past it".
//
// CloseButton is notice mode only too: the close button that stands where the gear is on the gauge's card.
internal enum WidgetCardCloseReason { Deactivated, Escape, Action, ClickOutside, CloseButton }

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
    WidgetCardView View = WidgetCardView.Main,
    SetupViewModel? Setup = null,
    string? UpdateVersion = null,     // a check found this newer version: the update line shows
    CardSettingsValues? Settings = null)
{
    public static WidgetCardModel Empty { get; } = new(
        WidgetSnapshot.Empty(WidgetWatcherState.NotStarted),
        AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true, ButtonEnabled: true,
        OtherDeviceLabel: "", Now: DateTimeOffset.UtcNow);

    // What each battery part shows and whether it is fresh, worked out by the presenter from the snapshot and the
    // time so the card only picks a colour from it. A model built without it gets the same answer worked out here.
    public ShownBattery? Parts { get; init; }

    // Where a battery refresh stands, or null when none has been asked for: the icon turns while it reads, and a
    // refresh that heard nothing says so in the read line.
    public BatteryRefreshView? Refresh { get; init; }

    public ShownBattery ShownParts => Parts ?? BatteryFreshness.Shown(Snapshot, Now);
}

// The dedicated three-column card: Left, Right, Case battery (each greyed when its value is not fresh), where
// the AirPods are, when the battery was last read, and a Connect/Disconnect button, plus the auto-pause switch
// when the snapshot says it is available. Built for the gauge's left click with LeftClickConnects off (the default): ConnectCard/
// CardPresenter, the tray's existing one-line card, has no room for three battery columns, a where-line and
// an auto-pause switch together, so the gauge gets its own card rather than reusing that one.
//
// Owner-painted, no child controls, one Form: OnPaintBackground is empty, RenderContent starts with
// Graphics.Clear and draws everything else with GDI+ FillPath/DrawString, never TextRenderer: GDI text
// writes alpha 0, invisible on a window composited through alpha the way this card's translucent backdrop
// is (https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows), the same
// fact ConnectCard and GaugeRenderer already build on. The framework's OptimizedDoubleBuffer stays off;
// the card buffers itself (WidgetCard.Paint.cs), with the finished pixels copied to the window as they are,
// alpha included, which is what the DWM recipe needs.
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
// (OnKeyDown), no focus rectangle is ever painted (DrawButton/DrawSwitch), a close button (the Cancel glyph) stands
// where the gear is and closes it, and a click that misses the controls closes it too (OnMouseUp), since a
// notice-mode card is never activated and so never deactivates either.
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
    private bool _leftButtonDownOnGear;
    private bool _leftButtonDownOnUpdate;
    private bool _leftButtonDownOnRefresh;
    private bool _leftButtonDownOnStatus;

    // The status row's chevron was pressed while Bluetooth is off: the presenter opens Windows' Bluetooth settings.
    public event EventHandler? BluetoothSettingsRequested;
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

    // The user activated the gear in the title row (Enter, Space or a mouse click on it).
    public event EventHandler? SettingsRequested;

    // The user activated the battery refresh icon beside the gear (Enter, Space or a mouse click on it).
    public event EventHandler? RefreshRequested;

    // The user activated the "Update" button on the update line (Enter, Space or a mouse click on it).
    public event EventHandler? UpdateRequested;

    // The user pressed a button of a sub-page, or its back button, or Escape.
    public event EventHandler<SetupAction>? SetupActionRequested;

    // The card wants to be hidden. WidgetCardPresenter hides it (it may already be hidden by the time this
    // is observed: the card hides itself first, see RequestClose) and does its own bookkeeping.
    public event EventHandler<WidgetCardCloseReason>? CloseRequested;

    // A notice-mode card is shown without being activated: the property says whether a form is activated when it is shown,
    // so even the show itself takes no focus (and WS_EX_NOACTIVATE and MA_NOACTIVATE keep it so afterwards).
    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.showwithoutactivation
    protected override bool ShowWithoutActivation => _notice;

    // For tests.
    internal bool ShowsWithoutActivationForTest => ShowWithoutActivation;

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

    // Which control of the sub-page has the focus, for tests.
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
        RepaintIfChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachAccent();
            StopMotion();
            DisposeTips();
            DisposeFrames();
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
        WidgetCardView before = _shownView;
        RenderView(model, dpi);
        NotePageChange(before, _shownView);
    }

    private void RenderView(WidgetCardModel model, int dpi)
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

                _setupFocus = setup.Buttons.Count == 0 ? new SetupTarget(SetupTargetKind.Back, 0) : new SetupTarget(SetupTargetKind.Button, primary);
            }

            if (_setupFocus.Kind == SetupTargetKind.Day && !SetupTargets().Contains(_setupFocus))
            {
                // The step button the focus was on cannot step now (the oldest day, or today): the focus goes back to Back.
                _setupFocus = new SetupTarget(SetupTargetKind.Back, 0);
            }

            _shownView = model.View;
            using var probe = new Bitmap(1, 1);
            using Graphics measure = Graphics.FromImage(probe);
            _setupLayout = ComputeSetupLayout(measure, setup);
            SizeTo(new Size(_setupLayout.Frame.Width, _setupLayout.Frame.Height));
            RepaintIfChanged();
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

        if (_focus == WidgetCardFocus.UpdateButton && !_mainLayout.ShowUpdateLine)
        {
            _focus = WidgetCardFocus.Button;
        }

        if (_focus == WidgetCardFocus.Gear && _mainLayout.Gear.IsEmpty)
        {
            _focus = WidgetCardFocus.Button;
        }

        if (_focus == WidgetCardFocus.Refresh && _mainLayout.Refresh.IsEmpty)
        {
            _focus = WidgetCardFocus.Button;
        }

        if (_focus == WidgetCardFocus.Status && !BluetoothOff)
        {
            _focus = WidgetCardFocus.Button;
        }

        SizeTo(new Size(layout.Width, layout.Height));
        RepaintIfChanged();
    }

    private WidgetCardLayout.Layout ComputeMainLayout(Graphics measure, WidgetCardModel model)
    {
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
            _dpi, model.ShowSwitch, showGear: true, showUpdateLine: showUpdate, updateButtonWidth: updateButtonWidth, showRefresh: !_notice,
            textScale: _look.TextScale);
    }

    private WidgetCardLayout.SetupLayout ComputeSetupLayout(Graphics measure, SetupViewModel setup)
    {
        int side = CardPlacement.Scale(WidgetCardLayout.BodySideAt96, _dpi);
        int contentWidth = CardPlacement.Scale(SubPageFrame.WidthAt96, _dpi) - (2 * side);
        int textWidth = contentWidth - CardPlacement.Scale(WidgetCardLayout.StatusIconAt96 + WidgetCardLayout.StatusIconGapAt96, _dpi);
        int promptLines = setup.Prompt is null ? 1 : CardPaint.Lines(measure, setup.Prompt, contentWidth, _type, CardPlacement.Scale(14, _dpi), bold: true);
        int captionLines = setup.Caption is null ? 1 : CardPaint.Lines(measure, setup.Caption, contentWidth, _type, CardPlacement.Scale(12, _dpi), bold: false);
        int subLines = setup.StatusSub is null ? 1 : CardPaint.Lines(measure, setup.StatusSub, textWidth, _type, CardPlacement.Scale(12, _dpi), bold: false);
        int actionWidth = 0;
        if (setup.Rows is not null)
        {
            // The updates page: the status caption wraps beside the tile and the action, so it is measured at the width left there.
            int padding = CardPlacement.Scale(SettingsPageLayout.RowPadLeftAt96 + SettingsPageLayout.RowPadRightAt96, _dpi);
            int gap = CardPlacement.Scale(WidgetCardLayout.StatusIconGapAt96, _dpi);
            int surfaceWidth = CardPlacement.Scale(SubPageFrame.WidthAt96, _dpi) - (2 * CardPlacement.Scale(SettingsPageLayout.BodySideAt96, _dpi));
            if (setup.Buttons.Count == 1)
            {
                actionWidth = new GraphicsTextMeasure(measure, _type).Width(setup.Buttons[0].Label, CardPlacement.Scale(14, _dpi)) + (2 * CardPlacement.Scale(SettingsPageLayout.ButtonPaddingAt96, _dpi));
            }

            int captionWidth = Math.Max(1, surfaceWidth - padding - CardPlacement.Scale(WidgetCardLayout.UpdatesTileAt96, _dpi) - (3 * gap) - actionWidth);
            subLines = CardPaint.Lines(measure, UpdatesCaptionText(setup), captionWidth, _type, CardPlacement.Scale(12, _dpi), bold: false);
        }

        return WidgetCardLayout.Setup(setup, _dpi, promptLines, captionLines, subLines, _look.TextScale, actionWidth);
    }

    // The updates page's status caption: the state in words, and its second line when it has one.
    internal static string UpdatesCaptionText(SetupViewModel setup) =>
        (setup.Status ?? string.Empty) + (setup.StatusSub is null ? string.Empty : "\n" + setup.StatusSub);

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
        _darkApplied = null;
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
        _leftButtonDownOnGear = false;
        _leftButtonDownOnUpdate = false;
        _leftButtonDownOnRefresh = false;
        _leftButtonDownOnStatus = false;
        _leftButtonDownOnSetupTarget = null;
        _leftButtonDownOnSettingsTarget = null;
        EndScrollDrag();
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
        if (e.Button == MouseButtons.Left)
        {
            TrackHot(e.Location, pressed: true);
        }

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
        _leftButtonDownOnGear = e.Button == MouseButtons.Left && !layout.Gear.IsEmpty && layout.Gear.Contains(e.Location);
        _leftButtonDownOnUpdate = e.Button == MouseButtons.Left && layout.ShowUpdateLine && layout.UpdateButton.Contains(e.Location);
        _leftButtonDownOnRefresh = e.Button == MouseButtons.Left && !layout.Refresh.IsEmpty && layout.Refresh.Contains(e.Location);
        _leftButtonDownOnStatus = e.Button == MouseButtons.Left && !_notice && BluetoothOff && layout.WhereLine.Contains(e.Location);
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

        TrackHot(e.Location, pressed: false);
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
        // control: a drag that started outside the button or the switch and released inside it, or any up from a button other than left (right, middle), must not count as pressing
        // it, the same rule GaugeWindow's own left-click handling already applies.
        bool activatesButton = e.Button == MouseButtons.Left && _leftButtonDownOnButton && layout.Button.Contains(e.Location);
        bool activatesSwitch = e.Button == MouseButtons.Left && _leftButtonDownOnSwitch && layout.ShowSwitch && layout.Switch.Contains(e.Location);
        bool activatesGear = e.Button == MouseButtons.Left && _leftButtonDownOnGear && !layout.Gear.IsEmpty && layout.Gear.Contains(e.Location);
        bool activatesUpdate = e.Button == MouseButtons.Left && _leftButtonDownOnUpdate && layout.ShowUpdateLine && layout.UpdateButton.Contains(e.Location);
        bool activatesRefresh = e.Button == MouseButtons.Left && _leftButtonDownOnRefresh && !layout.Refresh.IsEmpty && layout.Refresh.Contains(e.Location);
        bool activatesStatus = e.Button == MouseButtons.Left && _leftButtonDownOnStatus && BluetoothOff && layout.WhereLine.Contains(e.Location);
        ClearPressedFlags();

        if (activatesGear)
        {
            _focus = WidgetCardFocus.Gear;
            ActivateFocused();
        }
        else if (activatesRefresh)
        {
            _focus = WidgetCardFocus.Refresh;
            ActivateFocused();
        }
        else if (activatesStatus)
        {
            _focus = WidgetCardFocus.Status;
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
        else if (_notice)
        {
            // A notice-mode card is dismissed by a click outside the button and the switch: only reachable
            // in notice mode, since the normal card already closes on deactivation
            // for a click anywhere else.
            RequestClose(WidgetCardCloseReason.ClickOutside);
        }
    }

    // Every invalidation, with its rectangle, to the recording seam. A full invalidation reports the whole client.
    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnInvalidated(e);
        Record(CardWindowCallKind.Invalidate, e.InvalidRect, e.InvalidRect == ClientRectangle ? "invalidate whole" : "invalidate");
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint fills the whole client area.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Never RenderContent(e.Graphics): drawing on the window's own surface shows the cleared backdrop until it is done.
        PaintBuffered(e.Graphics, e.ClipRectangle);
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
        g.TextRenderingHint = CardPaint.CardTextHint;
        // The surface is the design's: the acrylic tint over the backdrop DWM blurs, or the solid surface when there is no backdrop
        // (transparency effects off, or high contrast, where it is the window colour).
        g.Clear(OverrideBackgroundForCaptureOnly ?? (PaintsOpaqueBackground ? Colours.Tokens.SolidSurface : Colours.Tokens.AcrylicTint));
        DrawSurfaceStroke(g);
        RenderPageMoving(g, RenderPage);
    }

    // What is inside the surface: the page on show, with any hover fill under its icons.
    private void RenderPage(Graphics g)
    {
        DrawFills(g);
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
            ShownBattery shown = _model.ShownParts;
            DrawEarbudColumn(g, layout.Left, WidgetCopy.LeftLabel, _model.Snapshot.Left, shown.Left, mirror: false);
            DrawEarbudColumn(g, layout.Right, WidgetCopy.RightLabel, _model.Snapshot.Right, shown.Right, mirror: true);
            DrawCaseColumn(g, layout.Case, WidgetCopy.CaseLabel, shown.Case);
        }

        DrawStatusRow(g, layout.WhereLine);

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

        DrawRefreshIcon(g, layout);
        if (_notice)
        {
            // The case-open card's close button: the Cancel glyph, or the clear button's own cross without the font.
            if (!CardPaint.TryGlyph(g, FluentGlyphs.Cancel, layout.Gear, colours.Text, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale))
            {
                CardPaint.IconButton(g, layout.Gear, GlyphKind.Cross, enabled: true, colours, _dpi, focused: false, bordered: false);
            }

            return;
        }

        if (!CardPaint.TryGlyph(g, FluentGlyphs.Settings, layout.Gear, colours.Text, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale))
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

    // Cycles Button, Switch (when shown), UpdateButton (when shown), Gear and Refresh (when the view has them), back to
    // Button.
    private void MoveFocus()
    {
        var order = new List<WidgetCardFocus> { WidgetCardFocus.Button };
        if (_model.ShowSwitch)
        {
            order.Add(WidgetCardFocus.Switch);
        }

        if (_mainLayout.ShowUpdateLine)
        {
            order.Add(WidgetCardFocus.UpdateButton);
        }

        if (!_mainLayout.Gear.IsEmpty)
        {
            order.Add(WidgetCardFocus.Gear);
        }

        if (!_mainLayout.Refresh.IsEmpty)
        {
            order.Add(WidgetCardFocus.Refresh);
        }

        if (!_notice && BluetoothOff)
        {
            order.Add(WidgetCardFocus.Status);
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
            if (!ButtonUsable)
            {
                return;
            }

            ToggleRequested?.Invoke(this, EventArgs.Empty);
            if (CaseOpenCardRules.ConnectClosesCard(_notice))
            {
                RequestClose(WidgetCardCloseReason.Action);
            }
        }
        else if (_focus == WidgetCardFocus.Gear)
        {
            if (_notice)
            {
                // The notice's close button, in the gear's place.
                RequestClose(WidgetCardCloseReason.CloseButton);
            }
            else if (!_mainLayout.Gear.IsEmpty)
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_focus == WidgetCardFocus.Refresh)
        {
            if (!_mainLayout.Refresh.IsEmpty && !BluetoothOff)
            {
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_focus == WidgetCardFocus.Status)
        {
            if (BluetoothOff)
            {
                BluetoothSettingsRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_focus == WidgetCardFocus.UpdateButton)
        {
            if (_mainLayout.ShowUpdateLine)
            {
                UpdateRequested?.Invoke(this, EventArgs.Empty);
            }
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
        Record(CardWindowCallKind.DwmAttribute, Rectangle.Empty, "corner preference");
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, in preference, sizeof(int));
        _cornersApplied = hr >= 0;
        NoteFrame(hr >= 0 ? null : StepOutcomes.FromHResult("dwm-corner-preference:widget-card", hr), "rounded corners");
    }

    // The dark-mode attribute is set when the window is made and when the theme really changes, not at every show: setting it
    // again to the value it has still makes the compositor draw the frame again.
    private bool? _darkApplied;
    private int _darkModeApplications;
    private int _backdropApplications;

    // How many times the window's dark-mode attribute and its backdrop were set, for tests.
    internal int DarkModeApplications => _darkModeApplications;

    internal int BackdropApplications => _backdropApplications;

    private void ApplyDarkMode(nint handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || _darkApplied == _dark)
        {
            return;
        }

        _darkApplied = _dark;
        _darkModeApplications++;
        int dark = _dark ? 1 : 0;
        Record(CardWindowCallKind.DwmAttribute, Rectangle.Empty, "dark mode");
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

        _backdropApplications++;

        int type = Dwm.DWMSBT_TRANSIENTWINDOW;
        Record(CardWindowCallKind.DwmAttribute, Rectangle.Empty, "backdrop type");
        int hr = Dwm.DwmSetWindowAttribute(handle, Dwm.DWMWA_SYSTEMBACKDROP_TYPE, in type, sizeof(int));
        if (hr < 0)
        {
            NoteFrame(StepOutcomes.FromHResult("dwm-backdrop-type:widget-card", hr), "translucent backdrop");
            return;
        }

        MARGINS margins = MARGINS.Full;
        Record(CardWindowCallKind.DwmAttribute, Rectangle.Empty, "extend frame");
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

    // The card's 1 px surface stroke, inside the edge, rounded to the window's 8 px corners.
    private void DrawSurfaceStroke(Graphics g)
    {
        if (OverrideBackgroundForCaptureOnly is null && !_cornersApplied)
        {
            return;
        }

        CardColours colours = Colours;
        using GraphicsPath path = CardPaint.RoundedRectangle(
            new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1), CardPlacement.Scale(SurfaceRadiusAt96, _dpi));
        using var pen = new Pen(colours.SurfaceStroke, 1f);
        g.DrawPath(pen, path);
    }

    // The surface's corner radius, from the design.
    internal const int SurfaceRadiusAt96 = 8;

    // "L", "R" or "Case" above the mark, centred, Caption in the secondary text colour.
    private void DrawColumnLabel(Graphics g, Rectangle bounds, string text)
    {
        using Font font = _type.Role(TypeRole.Caption);
        using var brush = new SolidBrush(Colours.TextSecondary);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, bounds, format);
    }

    // The mark is a filled shape in the primary text colour, never the accent: the earbud in a square of the mark's height.
    private void DrawEarbudColumn(Graphics g, WidgetCardLayout.ColumnLayout column, string label, PartReading part, ShownPart shown, bool mirror)
    {
        DrawColumnLabel(g, column.Label, label);
        int side = Math.Min(column.Glyph.Width, column.Glyph.Height);
        var square = new Rectangle(column.Glyph.X + ((column.Glyph.Width - side) / 2), column.Glyph.Y, side, side);
        using GraphicsPath glyph = BudGlyphPath(square, mirror);
        using (var brush = new SolidBrush(Colours.Text))
        {
            g.FillPath(brush, glyph);
        }

        // Like the figure, the mark is of the part's shown reading, but only a live one while the AirPods are on this PC: a last
        // reading or an estimate (shown at any age, and for AirPods that are away) says nothing of where a bud is now. A raw value of
        // the snapshot that is not shown must not be drawn either, and nothing decodes an in-ear bit today, so this is only a guard.
        if (shown.Fresh && _model.Snapshot.Where == AirPodsWhere.ThisPc && part.InEar == true)
        {
            DrawInEarMark(g, square, mirror);
        }

        DrawBatteryPart(g, column, shown);
    }

    private void DrawCaseColumn(Graphics g, WidgetCardLayout.ColumnLayout column, string label, ShownPart shown)
    {
        DrawColumnLabel(g, column.Label, label);
        (GraphicsPath lid, GraphicsPath box) = CaseGlyphPaths(column.Glyph);
        using (lid)
        using (box)
        using (var brush = new SolidBrush(Colours.Text))
        {
            g.FillPath(brush, lid);
            g.FillPath(brush, box);
        }

        DrawBatteryPart(g, column, shown);
    }

    // The ink a value is drawn in: primary while it is fresh, the tertiary token when it is a last reading or an estimate
    // (the stale style, which replaced a 55% opacity of the primary ink).
    internal Color ValueInk(ShownPart shown) => shown.Fresh ? Colours.Text : Colours.TextTertiary;

    // A live value is drawn in the primary ink and the accent; a last reading or an estimate in the tertiary token, its
    // read-time line saying its age, and an estimate with "≈" before it. The bar and the charging bolt are absent entirely
    // when Percent is null: there is nothing to show a bar or a bolt for. The words "No reading" take the value's own place
    // instead: never a number, never a dash standing in for a reading that was never taken. The read-time line is always
    // reserved and empty while the value is fresh (no ticking seconds).
    private void DrawBatteryPart(Graphics g, WidgetCardLayout.ColumnLayout column, ShownPart shown)
    {
        CardColours colours = Colours;
        if (shown.Percent is not { } percent)
        {
            DrawText(g, column.Percent, WidgetCopy.Percent(null), colours.TextTertiary, TypeRole.Number, StringAlignment.Center);
            return;
        }

        // A 1 px track centred on the bar's 3 px, and the fill the full 3.
        int trackHeight = Math.Max(1, CardPlacement.Scale(1, _dpi));
        using (var track = new SolidBrush(colours.Track))
        {
            g.FillRectangle(track, column.Bar.X, column.Bar.Y + ((column.Bar.Height - trackHeight) / 2), column.Bar.Width, trackHeight);
        }

        int filled = (int)Math.Round(column.Bar.Width * Math.Clamp(percent, 0, 100) / 100.0);
        if (filled > 0)
        {
            using var fill = new SolidBrush(shown.Fresh ? colours.Accent : colours.TextTertiary);
            g.FillRectangle(fill, column.Bar.X, column.Bar.Y, filled, column.Bar.Height);
        }

        Color ink = ValueInk(shown);
        DrawText(g, column.Percent, WidgetCopy.PercentText(percent, shown.Estimated), ink, TypeRole.Number, StringAlignment.Center);
        if (shown.Charging == true)
        {
            DrawBolt(g, column.BoltSlot, ink);
        }

        if (!shown.Fresh && shown.ReadAt is { } at)
        {
            DrawReadTime(g, column.ReadTime, WidgetCopy.StaleAgeAmount(_model.Now - at), colours.TextTertiary);
        }
    }

    // The clock (12, Read time E823) and the age, centred as one run, in the tertiary token.
    private void DrawReadTime(Graphics g, Rectangle bounds, string age, Color ink)
    {
        using Font font = _type.Role(TypeRole.Caption);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        float textWidth = g.MeasureString(age, font, int.MaxValue, format).Width;
        int box = TextFit.Grow(ReadTimeGlyphAt96, _dpi, _look.TextScale);
        int gap = CardPlacement.Scale(4, _dpi);
        float run = box + gap + textWidth;
        int left = bounds.X + (int)Math.Max(0, (bounds.Width - run) / 2f);
        var iconRect = new Rectangle(left, bounds.Y + ((bounds.Height - box) / 2), box, box);
        _ = CardPaint.TryGlyph(g, FluentGlyphs.ReadTime, iconRect, ink, _dpi, ReadTimeGlyphAt96, _look.TextScale);
        DrawText(g, new Rectangle(iconRect.Right + gap, bounds.Y, Math.Max(1, bounds.Right - iconRect.Right - gap), bounds.Height), age, ink, TypeRole.Caption, StringAlignment.Near);
    }

    // The clock and the charging bolt are 12 epx glyphs.
    internal const int ReadTimeGlyphAt96 = 12;

    // A small filled arc beside the bud, on the side away from the mirrored head so it never overlaps it.
    private void DrawInEarMark(Graphics g, Rectangle glyphBounds, bool mirror)
    {
        float d = glyphBounds.Width * 0.22f;
        float x = mirror ? glyphBounds.Left - (d * 0.2f) : glyphBounds.Right - (d * 0.8f);
        float y = glyphBounds.Bottom - d;
        using var brush = new SolidBrush(Colours.Text);
        g.FillEllipse(brush, x, y, d, d);
    }

    // The charging bolt, Fluent E945 at 12, in the reserved slot right of the value; five points drawn where no icon font is installed.
    private void DrawBolt(Graphics g, Rectangle slot, Color ink)
    {
        if (CardPaint.TryGlyph(g, FluentGlyphs.Bolt, slot, ink, _dpi, ReadTimeGlyphAt96, _look.TextScale))
        {
            return;
        }

        float h = slot.Height;
        float w = h * 0.6f;
        float x = slot.X + ((slot.Width - w) / 2f);
        float y = slot.Y;
        PointF[] points =
        [
            new PointF(x + (w * 0.55f), y),
            new PointF(x, y + (h * 0.6f)),
            new PointF(x + (w * 0.42f), y + (h * 0.6f)),
            new PointF(x + (w * 0.45f), y + h),
            new PointF(x + w, y + (h * 0.38f)),
            new PointF(x + (w * 0.58f), y + (h * 0.38f)),
        ];
        using var brush = new SolidBrush(ink);
        g.FillPolygon(brush, points);
    }

    // The status row: its icon (16), then the words (Body) and, with Bluetooth off, a chevron to Bluetooth settings. Nothing
    // heard (and the case not yet opened) is the caution ink with the Open the case glyph; Bluetooth off has its own glyph;
    // otherwise On this PC, or the iPhone, in the primary ink.
    private void DrawStatusRow(Graphics g, Rectangle bounds)
    {
        CardColours colours = Colours;
        (char glyph, string text, Color ink) = StatusRow;
        int box = TextFit.Grow(CardPaint.GlyphSizeAt96, _dpi, _look.TextScale);
        var iconRect = new Rectangle(bounds.X, bounds.Y + ((bounds.Height - box) / 2), box, box);
        int gap = CardPlacement.Scale(8, _dpi);
        int chevron = BluetoothOff ? TextFit.Grow(ChevronGlyphAt96, _dpi, _look.TextScale) : 0;
        if (!CardPaint.TryGlyph(g, glyph, iconRect, ink, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale))
        {
            DrawText(g, bounds, text, ink, TypeRole.Body, StringAlignment.Near);
        }
        else
        {
            int textX = iconRect.Right + gap;
            DrawText(g, new Rectangle(textX, bounds.Y, Math.Max(1, bounds.Right - chevron - textX), bounds.Height), text, ink, TypeRole.Body, StringAlignment.Near);
        }

        if (BluetoothOff)
        {
            if (!_notice && _focus == WidgetCardFocus.Status && FocusShown)
            {
                CardPaint.Focus(g, bounds, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
            }

            var chevronRect = new Rectangle(bounds.Right - chevron, bounds.Y + ((bounds.Height - chevron) / 2), chevron, chevron);
            _ = CardPaint.TryGlyph(g, FluentGlyphs.ChevronRight, chevronRect, colours.TextSecondary, _dpi, ChevronGlyphAt96, _look.TextScale);
        }
    }

    // The chevrons are 12 epx glyphs.
    internal const int ChevronGlyphAt96 = 12;

    // What the status row holds now, for the painter, the tooltip and tests.
    internal (char Glyph, string Text, Color Ink) StatusRow
    {
        get
        {
            CardColours colours = Colours;
            if (BluetoothOff)
            {
                return (FluentGlyphs.BluetoothOff, WidgetCopy.BluetoothOff, colours.Text);
            }

            if (_model.Refresh is { IsProblem: true } problem && problem.Outcome == BatteryRefreshOutcome.NothingHeard)
            {
                return (FluentGlyphs.Warning, WidgetCopy.OpenTheCase, colours.Caution);
            }

            if (_model.Refresh is { IsProblem: true } other)
            {
                return (FluentGlyphs.Warning, other.ReadLine ?? string.Empty, colours.Caution);
            }

            if (AsksToOpenTheCase)
            {
                return (FluentGlyphs.Warning, WidgetCopy.OpenTheCaseToShowBattery, colours.Caution);
            }

            char glyph = !_notice && _model.Snapshot.Where == AirPodsWhere.Elsewhere ? FluentGlyphs.CellPhone : FluentGlyphs.OnThisPc;
            return (glyph, WhereLineText, colours.Text);
        }
    }

    // Bluetooth is off: the refresh outcome says so, or the watcher stopped for the radio's absence. The refresh icon and
    // Connect are disabled while it is.
    internal bool BluetoothOff =>
        !_notice
        && (_model.Refresh?.Outcome == BatteryRefreshOutcome.BluetoothOff
            || (_model.Snapshot.Watcher == WidgetWatcherState.Stopped && _model.Snapshot.WatcherErrorCode == AdvertisementSourceCodes.RadioNotAvailableCode));

    private void DrawText(Graphics g, Rectangle bounds, string text, Color colour, TypeRole role, StringAlignment horizontal)
    {
        using Font font = _type.Role(role);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = horizontal, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, bounds, format);
    }

    // The button: 4 px corners, the accent when it connects (the text on it the token for text on accent) and the standard control
    // otherwise; the disabled control fill and the disabled text colour while it cannot be used.
    private void DrawButton(Graphics g, Rectangle rect)
    {
        CardColours colours = Colours;
        bool connect = _model.ConnectIntent;
        bool enabled = ButtonUsable;
        Color fill = !enabled ? colours.ControlFillDisabled : connect ? colours.Accent : colours.ControlFill;
        Color stroke = !enabled ? colours.ControlStroke : connect ? colours.Accent : colours.ControlStroke;
        Color text = !enabled ? colours.TextDisabled : connect ? colours.OnAccent : colours.Text;
        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);
        using GraphicsPath path = CardPaint.RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        using (var pen = new Pen(stroke, 1f))
        {
            g.DrawPath(pen, path);
        }

        if (enabled && !connect)
        {
            using var bottom = new Pen(colours.ControlStrokeBottom, 1f);
            g.DrawLine(bottom, rect.Left + radius, rect.Bottom - 1, rect.Right - radius - 1, rect.Bottom - 1);
        }

        DrawText(g, rect, connect ? WidgetCopy.Connect : WidgetCopy.Disconnect, text, TypeRole.Body, StringAlignment.Center);
        if (!_notice && _focus == WidgetCardFocus.Button && FocusShown)
        {
            CardPaint.Focus(g, rect, radius, colours, _dpi);
        }
    }

    // Whether Connect and Disconnect can be pressed: the model says so, and Bluetooth is not off.
    internal bool ButtonUsable => _model.ButtonEnabled && !BluetoothOff;

    // A line of text with its icon at the left, in the primary ink; with no icon font the words alone.
    private void DrawIconLine(Graphics g, Rectangle bounds, char glyph, string text, TypeRole role = TypeRole.Body)
    {
        CardColours colours = Colours;
        int box = TextFit.Grow(CardPaint.GlyphSizeAt96, _dpi, _look.TextScale);
        var iconRect = new Rectangle(bounds.X, bounds.Y + ((bounds.Height - box) / 2), box, box);
        if (!CardPaint.TryGlyph(g, glyph, iconRect, colours.Text, _dpi, CardPaint.GlyphSizeAt96, _look.TextScale))
        {
            DrawText(g, bounds, text, colours.Text, role, StringAlignment.Near);
            return;
        }

        int indent = box + CardPlacement.Scale(8, _dpi);
        DrawText(g, new Rectangle(bounds.X + indent, bounds.Y, Math.Max(1, bounds.Width - indent), bounds.Height), text, colours.Text, role, StringAlignment.Near);
    }

    // The switch row: the label, and the design's toggle (accent when on, no fill and a secondary-text outline when
    // off, so the off track shows on the dark card as well).
    private void DrawSwitch(Graphics g, Rectangle rect)
    {
        CardColours colours = Colours;
        int trackWidth = CardPlacement.Scale(WidgetCardLayout.ToggleWidthAt96, _dpi);
        int trackHeight = CardPlacement.Scale(WidgetCardLayout.ToggleHeightAt96, _dpi);
        var labelRect = new Rectangle(rect.X, rect.Y, Math.Max(0, rect.Width - trackWidth - 8), rect.Height);
        DrawIconLine(g, labelRect, FluentGlyphs.EarbudForThisPc(), WidgetCopy.AutoPauseSwitch);

        var track = new Rectangle(rect.Right - trackWidth, rect.Y + ((rect.Height - trackHeight) / 2), trackWidth, trackHeight);
        CardPaint.Toggle(g, track, _model.AutoPauseOn, colours, _dpi, Knob(SettingsRowId.PauseBud, track, _model.AutoPauseOn));

        if (!_notice && _focus == WidgetCardFocus.Switch && FocusShown)
        {
            CardPaint.Focus(g, rect, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi), colours, _dpi);
        }
    }

    // ---- The sub-pages

    // The keyboard order of a sub-page: the back button, then the footer buttons.
    private List<SetupTarget> SetupTargets()
    {
        var targets = new List<SetupTarget> { new(SetupTargetKind.Back, 0) };
        if (_model.Setup is { } setup)
        {
            for (int i = 0; i < setup.Buttons.Count; i++)
            {
                targets.Add(new SetupTarget(SetupTargetKind.Button, i));
            }

            if (_setupLayout is { } layout)
            {
                foreach (WidgetCardLayout.UpdatesRowLayout row in layout.UpdateRows)
                {
                    targets.Add(new SetupTarget(SetupTargetKind.Row, row.Index));
                }
            }

            // A step button that cannot step is not a stop.
            if (setup.History is { } history)
            {
                if (history.CanBack)
                {
                    targets.Add(new SetupTarget(SetupTargetKind.Day, 0));
                }

                if (history.CanForward)
                {
                    targets.Add(new SetupTarget(SetupTargetKind.Day, 1));
                }
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

        if (!layout.Action.IsEmpty && layout.Action.Contains(point))
        {
            return new SetupTarget(SetupTargetKind.Button, 0);
        }

        if (layout.History is { } page && setup.History is { } view)
        {
            if (view.CanBack && page.DayBack.Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Day, 0);
            }

            if (view.CanForward && page.DayForward.Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Day, 1);
            }
        }

        foreach (WidgetCardLayout.UpdatesRowLayout row in layout.UpdateRows)
        {
            if (row.Surface.Contains(point))
            {
                return new SetupTarget(SetupTargetKind.Row, row.Index);
            }
        }

        return null;
    }

    // The keys of a sub-page. Internal so a test can send one without a window handle. Tab and Shift+Tab move the
    // focus through SetupTargets; Space activates what has the focus; Enter is the page's primary button (or the
    // focused footer button); Escape is Back.
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
                if (_setupFocus.Kind != SetupTargetKind.Back)
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
            case SetupTargetKind.Button:
                if (target.Index < setup.Buttons.Count)
                {
                    SetupActionRequested?.Invoke(this, setup.Buttons[target.Index].Action);
                }

                break;
            case SetupTargetKind.Day:
                if (setup.History is { } view && (target.Index == 0 ? view.CanBack : view.CanForward))
                {
                    SetupActionRequested?.Invoke(this, target.Index == 0 ? SetupAction.HistoryEarlier : SetupAction.HistoryLater);
                }

                break;
            case SetupTargetKind.Row:
                SetupActionRequested?.Invoke(this, target.Index switch
                {
                    0 => SetupAction.ToggleAutoCheck,
                    1 => SetupAction.WhatsNew,
                    _ => SetupAction.Repair,
                });
                break;
        }
    }

    private void DrawSetup(Graphics g, SetupViewModel setup, WidgetCardLayout.SetupLayout layout)
    {
        CardColours colours = Colours;
        bool focusVisible = FocusShown;
        SetupTarget focus = _setupFocus;

        SubPageFrame.DrawHeader(g, layout.Frame, setup.Title, setup.Step, colours, _type, _dpi, backFocused: focusVisible && focus.Kind == SetupTargetKind.Back);

        if (setup.Rows is { } updateRows)
        {
            DrawUpdatesPage(g, setup, updateRows, layout, colours, focusVisible, focus);
            return;
        }

        if (setup.History is { } history && layout.History is { } historyLayout)
        {
            DrawHistoryPage(g, history, historyLayout, colours, focusVisible, focus);
            return;
        }

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

        var buttons = setup.Buttons.Select(b => (b.Label, b.Primary)).ToList();
        int focusedButton = focusVisible && focus.Kind == SetupTargetKind.Button ? focus.Index : -1;
        SubPageFrame.DrawFooter(g, layout.Frame, buttons, colours, _type, _dpi, focusedButton);
    }

    // The updates page: the status row (the accent tile with the earbud mark in the text on accent, the title, the caption and the action),
    // then Check automatically, What's new and Repair, each on a surface of the settings page's own fill and stroke.
    private void DrawUpdatesPage(Graphics g, SetupViewModel setup, UpdatesRows rows, WidgetCardLayout.SetupLayout layout, CardColours colours, bool focusVisible, SetupTarget focus)
    {
        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);
        int twelve = CardPlacement.Scale(12, _dpi);
        int fourteen = CardPlacement.Scale(14, _dpi);

        void Surface(Rectangle rect)
        {
            CardPaint.Surface(g, rect, radius, colours.RowFill, colours.RowStroke);
        }

        Surface(layout.StatusSurface);
        using (GraphicsPath tilePath = CardPaint.RoundedRectangle(layout.Tile, radius))
        using (var tileFill = new SolidBrush(colours.Accent))
        {
            g.FillPath(tileFill, tilePath);
        }

        DrawTileMark(g, layout.Tile, colours.OnAccent);
        string title = rows.Version is { } version ? WidgetCopy.CardApp + " " + version : WidgetCopy.CardApp;
        CardPaint.Text(g, title, layout.StatusTitle, _type, fourteen, bold: true, colours.Text, StringAlignment.Near, StringAlignment.Center);
        CardPaint.Wrapped(g, UpdatesCaptionText(setup), layout.StatusCaption, _type, twelve, bold: false, setup.Icon == SetupIcon.Caution ? colours.Caution : colours.TextSecondary);
        if (setup.Icon == SetupIcon.Spinner && layout.Action.IsEmpty)
        {
            CardPaint.Spinner(g, layout.StatusIcon, colours.Accent, setup.SpinnerFrame, _dpi);
        }

        if (!layout.Action.IsEmpty && setup.Buttons.Count == 1)
        {
            // The action is the accent when it is the page's main one (Install, Restart), the standard button otherwise (Check).
            CardPaint.Button(g, layout.Action, setup.Buttons[0].Label, setup.Buttons[0].Primary, colours, _type, _dpi, focusVisible && focus == new SetupTarget(SetupTargetKind.Button, 0));
        }

        if (setup.ShowProgress && !layout.Progress.IsEmpty)
        {
            DrawProgress(g, setup, layout.Progress, colours);
        }

        if (setup.Caption is not null && !layout.Caption.IsEmpty)
        {
            CardPaint.Wrapped(g, setup.Caption, layout.Caption, _type, twelve, bold: false, colours.TextSecondary);
        }

        foreach (WidgetCardLayout.UpdatesRowLayout row in layout.UpdateRows)
        {
            Surface(row.Surface);
            char glyph = row.Index switch { 0 => FluentGlyphs.Sync, 1 => FluentGlyphs.WhatsNew, _ => FluentGlyphs.Repair };
            string label = row.Index switch { 0 => WidgetCopy.CheckAutomatically, 1 => WidgetCopy.SettingsWhatsNew, _ => WidgetCopy.RepairEarshot };
            CardPaint.Glyph(g, glyph, row.Icon, colours.Text, _dpi);
            CardPaint.Text(g, label, row.Label, _type, fourteen, bold: false, colours.Text, StringAlignment.Near, StringAlignment.Center);
            bool focused = focusVisible && focus == new SetupTarget(SetupTargetKind.Row, row.Index);
            switch (row.Index)
            {
                case 0:
                    CardPaint.Toggle(g, row.Control, rows.AutoCheck, colours, _dpi, Knob(SettingsRowId.CheckAutomatically, row.Control, rows.AutoCheck));
                    if (focused)
                    {
                        CardPaint.Focus(g, row.Control, row.Control.Height / 2, colours, _dpi);
                    }

                    break;
                case 1:
                    if (!CardPaint.TryGlyph(g, FluentGlyphs.OpenExternal, row.Control, colours.TextSecondary, _dpi, FluentGlyphs.ChevronSizeAt96, 1.0))
                    {
                        CardPaint.Chevron(g, row.Control, up: false, colours.TextSecondary, _dpi);
                    }

                    if (focused)
                    {
                        CardPaint.Focus(g, row.Surface, radius, colours, _dpi);
                    }

                    break;
                default:
                    CardPaint.SmallButton(g, row.Control, WidgetCopy.RepairButton, colours, _type, _dpi, focused);
                    break;
            }
        }

        var buttons = setup.Buttons.Select(b => (b.Label, b.Primary)).ToList();
        int focusedButton = focusVisible && focus.Kind == SetupTargetKind.Button ? focus.Index : -1;
        if (layout.Action.IsEmpty)
        {
            SubPageFrame.DrawFooter(g, layout.Frame, buttons, colours, _type, _dpi, focusedButton);
        }
    }

    // The earbud pair in the status tile, in the colour for text on the accent: the gauge's own pair scaled to the tile.
    private static void DrawTileMark(Graphics g, Rectangle tile, Color ink)
    {
        var grid = new GaugeLayout(
            96, 0, 0, tile, 0, 0, tile, Rectangle.Empty, Rectangle.Empty, Size.Empty, 0, 0, 0);
        (RectangleF[] shapes, float radius) = grid.EarbudShapes();
        using var path = new GraphicsPath { FillMode = FillMode.Winding };
        foreach (RectangleF shape in shapes)
        {
            using GraphicsPath one = CardPaint.RoundedRectangle(shape, radius);
            path.AddPath(one, connect: false);
        }

        using var brush = new SolidBrush(ink);
        g.FillPath(brush, path);
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
}
