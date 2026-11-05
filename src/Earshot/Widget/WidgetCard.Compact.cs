using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.Tray;

namespace Earshot.Widget;

// The compact form of the card: the same window folded to one row (a glyph, the words, and a button when the state has an action).
// Only an unasked card (an unasked card, never activated) shows it. What it draws, and how it grows into the full card and folds back:
//   - grow and fold move the window to its new height point to point with the bottom edge kept (the card's own height motion), the
//     old content fading out as the new fades in (CompactMotion has the numbers); a row replaced in place crossfades in 83 ms;
//   - the row holds its time once its entrance has finished; the pointer over it stops the clock and leaving restarts it with what
//     remained (CompactHold); when it runs out the card folds away;
//   - the button's glyph crossfades to a ring while the work runs and to a tick when it is done.
// With animation effects off nothing moves: every change lands on its end frame in one step, and the ring is a still 90 degree arc at
// 12 o'clock.
internal sealed partial class WidgetCard
{
    private CardForm _shownForm = CardForm.Full;
    private WidgetCardLayout.CompactLayout? _compactLayout;
    private bool _leftButtonDownOnCompactAction;
    private bool _leftButtonDownOnCompactBody;
    private string _baseAccessibleName = string.Empty;

    // The compact row's button was pressed, genuinely: a left down and up on it, or a screen reader's default action. Never raised
    // while its work is running.
    public event EventHandler? CompactActionRequested;

    // The body of the compact row (anywhere but its button) was pressed: the presenter grows the card to the full one.
    public event EventHandler? CompactBodyClicked;

    // Whether the card is showing its compact form now.
    internal bool IsCompactShown => _unasked && _shownForm == CardForm.Compact && _compactLayout is not null;

    internal CardForm ShownForm => _shownForm;

    internal WidgetCardLayout.CompactLayout? CurrentCompactLayout => _compactLayout;

    // ---- Render

    private bool WantsCompact(WidgetCardModel model) => _unasked && model.Form == CardForm.Compact && model.Compact is not null;

    private void RenderCompact(WidgetCardModel model, CompactContent content)
    {
        _shownForm = CardForm.Compact;
        _shownView = WidgetCardView.Main;
        _settingsLayout = null;
        _setupLayout = null;
        using (var probe = new Bitmap(1, 1))
        using (Graphics measure = Graphics.FromImage(probe))
        {
            _compactLayout = ComputeCompactLayout(measure, content);
        }

        AccessibleName = TrayStatus.AppName + ": " + content.Text;
        NoteActionState(model.CompactState);
        SizeTo(new Size(_compactLayout.Width, _compactLayout.Height));
        RepaintIfChanged();
    }

    private WidgetCardLayout.CompactLayout ComputeCompactLayout(Graphics measure, CompactContent content)
    {
        bool hasGlyph = content.Glyph != '\0';
        WidgetCardLayout.CompactButton button = content.Action is null
            ? WidgetCardLayout.CompactButton.None
            : content.Action.IsIcon ? WidgetCardLayout.CompactButton.Icon : WidgetCardLayout.CompactButton.Text;
        int actionWidth = 0;
        if (button == WidgetCardLayout.CompactButton.Text)
        {
            actionWidth = CardPaint.LineWidth(measure, content.Action!.Label, _type, CardPlacement.Scale(14, _dpi), bold: false) + (2 * CardPlacement.Scale(8, _dpi));
        }

        int width = WidgetCardLayout.CompactTextWidth(_dpi, _look.TextScale, hasGlyph, button, actionWidth);
        int lines = CardPaint.Lines(measure, content.Text, width, _type, CardPlacement.Scale(14, _dpi), bold: false);
        return WidgetCardLayout.ForCompact(_dpi, _look.TextScale, lines, hasGlyph, button, actionWidth);
    }

    // ---- Drawing

    private static Color CompactInk(InkRole ink, CardColours colours) => colours.HighContrast
        ? colours.Text
        : ink switch
        {
            InkRole.Accent => colours.Accent,
            InkRole.Caution => colours.Caution,
            _ => colours.Text,
        };

    private void DrawCompact(Graphics g)
    {
        if (_compactLayout is not { } layout || _model.Compact is not { } content)
        {
            return;
        }

        CardColours colours = Colours;
        if (!layout.Glyph.IsEmpty)
        {
            _ = CardPaint.TryGlyph(g, content.Glyph, layout.Glyph, CompactInk(content.Ink, colours), _dpi, WidgetCardLayout.CompactGlyphAt96, _look.TextScale);
        }

        DrawText(g, layout.Text, content.Text, colours.Text, TypeRole.Body, StringAlignment.Near, wrap: layout.Lines > 1);
        if (content.Action is { } action && !layout.Action.IsEmpty)
        {
            DrawCompactAction(g, layout.Action, action, colours);
        }
    }

    private void DrawCompactAction(Graphics g, Rectangle bounds, CompactAction action, CardColours colours)
    {
        if (!action.IsIcon)
        {
            DrawText(g, bounds, action.Label, colours.HighContrast ? colours.Text : colours.Accent, TypeRole.Body, StringAlignment.Far);
            return;
        }

        if (_actionFadeP >= 1)
        {
            DrawActionFace(g, bounds, action, _actionShown, 1, colours);
            return;
        }

        DrawActionFace(g, bounds, action, _actionFrom, 1 - _actionFadeP, colours);
        DrawActionFace(g, bounds, action, _actionShown, _actionFadeP, colours);
    }

    private static Color WithOpacity(Color colour, double opacity) =>
        Color.FromArgb((int)Math.Round(colour.A * Math.Clamp(opacity, 0, 1)), colour.R, colour.G, colour.B);

    // One face of the icon button: the glyph, the ring, or the tick, at an opacity.
    private void DrawActionFace(Graphics g, Rectangle bounds, CompactAction action, CompactActionState state, double opacity, CardColours colours)
    {
        if (opacity <= 0)
        {
            return;
        }

        switch (state)
        {
            case CompactActionState.Progress:
                DrawRing(g, bounds, WithOpacity(colours.Accent, opacity));
                break;
            case CompactActionState.Done:
                _ = CardPaint.TryGlyph(g, FluentGlyphs.CheckMark, bounds, WithOpacity(colours.HighContrast ? colours.Text : colours.Accent, opacity), _dpi, WidgetCardLayout.CompactGlyphAt96, _look.TextScale);
                break;
            default:
                _ = CardPaint.TryGlyph(g, action.Glyph, bounds, WithOpacity(colours.Text, opacity), _dpi, WidgetCardLayout.CompactGlyphAt96, _look.TextScale);
                break;
        }
    }

    // The 16 px ring: a 2 px accent arc of 90 degrees, centred at 12 o'clock when still, turning once every 1000 ms while it runs.
    private void DrawRing(Graphics g, Rectangle bounds, Color colour)
    {
        int box = TextFit.Grow(WidgetCardLayout.CompactGlyphAt96, _dpi, _look.TextScale);
        float stroke = Math.Max(1f, 2f * _dpi / 96f);
        float inset = stroke / 2f;
        var ring = new RectangleF(
            bounds.X + ((bounds.Width - box) / 2f) + inset, bounds.Y + ((bounds.Height - box) / 2f) + inset, box - stroke, box - stroke);
        using var pen = new Pen(colour, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, ring, -135f + _spinAngle, 90f);
    }

    // ---- The button's state: glyph, ring, tick

    private CompactActionState _actionShown = CompactActionState.Idle;
    private CompactActionState _actionFrom = CompactActionState.Idle;
    private readonly AnimatedValue _actionFade = new(1);
    private double _actionFadeP = 1;
    private readonly object _spinKey = new();
    private TimeSpan _spinStart;
    private float _spinAngle;

    // The state the compact row's button is in, for tests.
    internal CompactActionState ActionStateShown => _actionShown;

    internal double ActionFadeProgress => _actionFadeP;

    internal float RingAngleForTest => _spinAngle;

    private void NoteActionState(CompactActionState state)
    {
        if (state == _actionShown)
        {
            return;
        }

        _actionFrom = _actionShown;
        _actionShown = state;
        _actionFade.Jump(0);
        _actionFadeP = 0;
        _actionFade.AnimateTo(1, CanAnimateContent ? _driver : null, CompactMotion.ActionFadeDuration, FluentMotion.Linear, p =>
        {
            _actionFadeP = p;
            if (_compactLayout is { } layout)
            {
                MarkDirty(layout.Action);
            }
        });

        if (state == CompactActionState.Progress)
        {
            StartSpin();
        }
        else
        {
            StopSpin();
        }

        if (state == CompactActionState.Done)
        {
            // The tick holds 800 ms, then the card folds.
            StartHold(CompactMotion.TickHold);
        }
    }

    private void StartSpin()
    {
        _spinAngle = 0;
        if (!CanAnimateContent || _driver is null)
        {
            return;
        }

        _spinStart = _driver.Now;
        _driver.Run(_spinKey, at =>
        {
            double turn = (at - _spinStart).TotalMilliseconds % CompactMotion.TurnDuration.TotalMilliseconds / CompactMotion.TurnDuration.TotalMilliseconds;
            _spinAngle = (float)(turn * 360.0);
            if (_compactLayout is { } layout)
            {
                MarkDirty(layout.Action);
            }

            return _actionShown == CompactActionState.Progress;
        });
    }

    private void StopSpin()
    {
        _driver?.Stop(_spinKey);
        _spinAngle = 0;
    }

    // ---- Changing form and replacing the row: the old content is kept as a picture and crossfaded out

    private Bitmap? _fadeOld;
    private FormFadeKind _fadeKind;
    private readonly AnimatedValue _formFade = new(1);
    private double _formFadeP = 1;
    private TimeSpan? _nextHeightDuration;

    // Whether a crossfade is on its way, for tests.
    internal bool FormFading => _fadeOld is not null;

    internal (double Old, double New) FormFadeAlphasForTest => _fadeOld is null ? (0, 1) : CompactMotion.Alphas(_fadeKind, _formFadeP);

    private readonly record struct FormChange(Bitmap? Old, FormFadeKind Kind, TimeSpan Duration);

    private static bool SameWords(CompactContent? a, CompactContent? b) => (a is null && b is null) || (a is not null && b is not null && a with { Hold = null } == b with { Hold = null });

    // Called before a new model is laid out: when the card is on screen and its form is about to change, or its compact row is
    // about to say something else, the old content is kept as a picture.
    private FormChange BeginFormChange(WidgetCardModel model)
    {
        CardForm to = WantsCompact(model) ? CardForm.Compact : CardForm.Full;
        if (!CanAnimateContent || (_animator?.Running ?? false))
        {
            FinishFormFade();
            return default;
        }

        bool formChanges = _shownForm != to;
        bool replaces = !formChanges && to == CardForm.Compact && !SameWords(_model.Compact, model.Compact);
        if (!formChanges && !replaces)
        {
            return default;
        }

        FinishFormFade();
        if (formChanges)
        {
            _nextHeightDuration = to == CardForm.Compact ? CompactMotion.FoldDuration : CompactMotion.GrowDuration;
        }

        return new FormChange(CaptureLayer(), formChanges ? FormFadeKind.Sequential : FormFadeKind.Cross, formChanges ? _nextHeightDuration!.Value : CompactMotion.ReplaceDuration);
    }

    private void EndFormChange(FormChange change)
    {
        _nextHeightDuration = null;
        if (change.Old is null)
        {
            return;
        }

        _fadeOld = change.Old;
        _fadeKind = change.Kind;
        _formFade.Jump(0);
        _formFadeP = 0;
        _formFade.AnimateTo(1, _driver, change.Duration, FluentMotion.Linear, p =>
        {
            _formFadeP = p;
            if (p >= 1)
            {
                FinishFormFade();
            }

            MarkDirty(ClientRectangle);
        });
        MarkDirty(ClientRectangle);
    }

    private void FinishFormFade()
    {
        _driver?.Stop(_formFade);
        _formFade.Jump(1);
        _formFadeP = 1;
        _fadeOld?.Dispose();
        _fadeOld = null;
    }

    // The height of the page on show.
    private int ShownContentHeight => IsCompactShown ? _compactLayout!.Height : _mainLayout.Height;

    // The page on show drawn into a clear bitmap of its own height, so it can be faded and moved as one.
    private Bitmap CaptureLayer()
    {
        int height = Math.Max(1, ShownContentHeight);
        var layer = new Bitmap(Math.Max(1, ClientSize.Width), height, PixelFormat.Format32bppPArgb);
        using Graphics lg = Graphics.FromImage(layer);
        lg.SmoothingMode = SmoothingMode.AntiAlias;
        lg.PixelOffsetMode = PixelOffsetMode.Half;
        lg.TextRenderingHint = CardPaint.CardTextHint;
        lg.Clear(Color.Transparent);
        RenderPage(lg);
        return layer;
    }

    // The page drawn as the crossfade has it: the old picture and the new content, each at its opacity, both with their bottom edge
    // on the window's (the window grows and folds with that edge kept).
    private void RenderPageFaded(Graphics g)
    {
        if (_fadeOld is not { } old)
        {
            RenderPage(g);
            return;
        }

        (double oldAlpha, double newAlpha) = CompactMotion.Alphas(_fadeKind, _formFadeP);
        int bottom = ClientSize.Height;
        DrawFaded(g, old, new Point(0, bottom - old.Height), oldAlpha);
        if (newAlpha <= 0)
        {
            return;
        }

        using Bitmap fresh = CaptureLayer();
        DrawFaded(g, fresh, new Point(0, bottom - fresh.Height), newAlpha);
    }

    private static void DrawFaded(Graphics g, Bitmap layer, Point at, double opacity)
    {
        if (opacity <= 0)
        {
            return;
        }

        var matrix = new ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0, 1) };
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        g.DrawImage(layer, new Rectangle(at, layer.Size), 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, attributes);
    }

    // ---- The hold

    private readonly CompactHold _hold = new();
    private System.Windows.Forms.Timer? _holdTimer;
    private bool _pointerOver;
    private bool _holdArmed;

    // The clock the hold counts on, in milliseconds. A test hands in its own.
    internal Func<long> HoldClock = static () => Environment.TickCount64;

    // Whether the compact row is holding, and whether the pointer has paused it, for tests.
    internal bool HoldActive => _hold.Active;

    internal bool HoldPaused => _hold.Paused;

    internal TimeSpan? HoldRemaining => _hold.RemainingAt(HoldClock());

    // Starts the hold of the row on show (or of hold when given): from now. A row with no hold stays until it is closed.
    internal void StartHold(TimeSpan? hold = null)
    {
        _holdArmed = false;
        TimeSpan? length = hold ?? (IsCompactShown ? _model.Compact?.Hold : null);
        long now = HoldClock();
        _hold.Start(IsCompactShown ? length : null, now);
        if (_pointerOver)
        {
            _hold.Pause(now);
        }

        ScheduleHold();
    }

    // The card has finished coming in: the hold starts now. Called when the entrance ends, or at once when there is none.
    private void ArmHold()
    {
        if (!IsCompactShown)
        {
            return;
        }

        if (_animator is { Running: true })
        {
            _holdArmed = true;
            return;
        }

        StartHold();
    }

    // The row on show is shown again, with the same words or others: its hold starts over, unless the card is still coming in
    // (the hold then starts when it has).
    internal void RestartHold()
    {
        if (Visible && IsCompactShown && _animator is not { Running: true })
        {
            StartHold();
        }
    }

    private void StopHold()
    {
        _holdArmed = false;
        _hold.Stop();
        _holdTimer?.Stop();
    }

    private void ScheduleHold()
    {
        _holdTimer?.Stop();
        if (!_hold.Active || _hold.Paused)
        {
            return;
        }

        if (HoldRemaining is not { } left)
        {
            return;
        }

        _holdTimer ??= new System.Windows.Forms.Timer();
        _holdTimer.Tick -= OnHoldTimer;
        _holdTimer.Tick += OnHoldTimer;
        _holdTimer.Interval = (int)Math.Clamp(Math.Ceiling(left.TotalMilliseconds), 1, int.MaxValue);
        _holdTimer.Start();
    }

    private void OnHoldTimer(object? sender, EventArgs e) => CheckHold();

    // Folds the card away when its hold has run out. The timer calls it; a test calls it after moving its clock.
    internal void CheckHold()
    {
        _holdTimer?.Stop();
        if (_hold.ElapsedAt(HoldClock()))
        {
            _hold.Stop();
            if (Visible && !_exiting)
            {
                RequestClose(WidgetCardCloseReason.HoldEnded);
            }

            return;
        }

        ScheduleHold();
    }

    // The pointer is over the compact row (true) or has left it. Over it, the hold stops; on leaving it goes on with what remained.
    internal void NotePointer(bool over)
    {
        if (_pointerOver == over)
        {
            return;
        }

        _pointerOver = over;
        if (!IsCompactShown)
        {
            return;
        }

        long now = HoldClock();
        if (over)
        {
            _hold.Pause(now);
            _holdTimer?.Stop();
        }
        else
        {
            _hold.Resume(now);
            ScheduleHold();
        }
    }

    // A fold on its way is cancelled and the card comes back in from where it is (a Connect began from the row's own button).
    internal bool CancelFold()
    {
        if (!_exiting || _animator is null)
        {
            return false;
        }

        _exiting = false;
        RaiseOnScreenChanged();
        _animator.Enter(_rest, _travel);
        return true;
    }

    // The card was shown or hidden (called from OnVisibleChanged): hidden, nothing keeps running; shown, the hold and a ring begin.
    private void NoteCompactVisibility()
    {
        if (!Visible)
        {
            StopHold();
            StopSpin();
            FinishFormFade();
            _pointerOver = false;
        }
        else
        {
            if (_actionShown == CompactActionState.Progress)
            {
                StartSpin();
            }

            // Shown: the hold starts once the entrance is over (at once when there is none).
            if (!_hold.Active)
            {
                ArmHold();
            }
        }
    }

    // ---- The pointer

    private void CompactMouseMove(MouseEventArgs e)
    {
        NotePointer(true);
        TrackHot(e.Location, pressed: _hotPressed && (MouseButtons & MouseButtons.Left) != 0);
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

        _tipPending = tip;
        _tipTimer ??= new System.Windows.Forms.Timer();
        _tipTimer.Tick -= OnTipTimer;
        _tipTimer.Tick += OnTipTimer;
        _tipTimer.Interval = Math.Max(1, SystemInformation.MouseHoverTime);
        _tipTimer.Start();
    }

    private void CompactMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _compactLayout is not { } layout)
        {
            _leftButtonDownOnCompactAction = false;
            _leftButtonDownOnCompactBody = false;
            return;
        }

        _leftButtonDownOnCompactAction = !layout.Action.IsEmpty && layout.Action.Contains(e.Location);
        _leftButtonDownOnCompactBody = !_leftButtonDownOnCompactAction;
    }

    private void CompactMouseUp(MouseEventArgs e)
    {
        bool onAction = _leftButtonDownOnCompactAction;
        bool onBody = _leftButtonDownOnCompactBody;
        _leftButtonDownOnCompactAction = false;
        _leftButtonDownOnCompactBody = false;
        if (e.Button != MouseButtons.Left || _compactLayout is not { } layout)
        {
            return;
        }

        bool overAction = !layout.Action.IsEmpty && layout.Action.Contains(e.Location);
        if (onAction && overAction)
        {
            PressCompactAction();
        }
        else if (onBody && !overAction)
        {
            CompactBodyClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    // The button's own press. Ignored while its work runs.
    private void PressCompactAction()
    {
        if (_actionShown != CompactActionState.Idle || _exiting)
        {
            return;
        }

        CompactActionRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- What a screen reader and a tooltip see

    private List<CardControl> CompactControls()
    {
        var list = new List<CardControl>();
        if (_model.Compact is not { } content || _compactLayout is not { } layout)
        {
            return list;
        }

        if (content.Action is { } action && !layout.Action.IsEmpty)
        {
            bool running = _actionShown == CompactActionState.Progress;
            string name = running ? action.RunningLabel ?? action.Label : action.Label;
            list.Add(new CardControl(
                name, CardControlRole.PushButton, layout.Action, action.Label, action.IsIcon, false, false, !running, PressCompactAction)
            {
                Tooltip = action.IsIcon ? action.Label : null,
            });
        }

        list.Add(new CardControl(content.Text, CardControlRole.StaticText, layout.Text, null, false, false, false, true, static () => { }));
        return list;
    }
}
