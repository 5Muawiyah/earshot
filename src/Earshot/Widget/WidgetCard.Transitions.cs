using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.Popup;

namespace Earshot.Widget;

// Everything on the card that moves besides its entrance and exit, all on the one frame driver AttachMotion made, so they
// share its frames and it stops asking for frames the moment the last of them is still (FluentMotion has the numbers):
//   - a change of height (a page of another size, an expander) moves the window to its new bounds point to point, keeping its
//     bottom edge where it was;
//   - a change of page slides the new page in from the right going deeper (main, then settings, then the Updates or history
//     page) and from the left coming back, fading it in;
//   - an expander's rows open and close as a band that grows or shrinks, the rows under it moving with it;
//   - an icon button's hover and pressed fill fades in and out;
//   - a toggle's knob slides.
// With no motion attached, or Windows' animation effects off, each of these is put at its end in one step, as before.
//
// Each animation marks what it changed; after the frame's steps the card invalidates that and paints it at once, so the frame
// reaches the screen at the refresh it was made for.
internal sealed partial class WidgetCard
{
    private Rectangle _resizeFrom;
    private readonly AnimatedValue _resizeProgress = new(1);
    private Rectangle _dirty;

    // ---- Height

    // True while the window is on its way to a new size.
    private bool Resizing => _driver is not null && _driver.IsRunning(_resizeProgress);

    // The size the card is laid out at: where a height is on its way, the size it is going to.
    internal Size LaidOutSize => Resizing ? _rest.Size : ClientSize;

    // Whether a change of bounds may move rather than jump: the card is on screen, still, and allowed to move.
    private bool CanMoveBounds =>
        _driver is { AnimationsEnabled: true } && !_motionBroken && IsHandleCreated && Visible && !_exiting && !(_animator?.Running ?? false);

    // Sizes the card for its layout. On screen and still, the window moves to the new size point to point with its bottom edge
    // kept; otherwise the client size is set at once, as it always was.
    private void SizeTo(Size size)
    {
        Size current = LaidOutSize;
        if (size == current)
        {
            return;
        }

        Rectangle rest = Resizing ? _rest : Bounds;
        var target = new Rectangle(rest.X, rest.Bottom - size.Height, size.Width, size.Height);
        if (!MoveBoundsTo(target))
        {
            StopResizing();
            SetClientSizeIfChanged(size);
        }
    }

    // Starts the window towards target from wherever it is now. False when it cannot move (it then is not touched here).
    private bool MoveBoundsTo(Rectangle target)
    {
        if (!CanMoveBounds)
        {
            return false;
        }

        if (Resizing)
        {
            if (target == _rest)
            {
                return true;
            }
        }
        else if (target.Size == Bounds.Size)
        {
            // Only a change of size moves; a card put somewhere else at the same size is put there at once.
            return false;
        }

        _resizeFrom = Bounds;
        _rest = target;
        Record(CardWindowCallKind.Place, target, "place at rest, moving there");
        _resizeProgress.Jump(0);
        _resizeProgress.AnimateTo(1, _driver, FluentMotion.HeightDuration, FluentMotion.PointToPoint, ApplyResize);
        return true;
    }

    private void ApplyResize(double progress)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        Rectangle at = Lerp(_resizeFrom, _rest, progress);
        if (at == Bounds)
        {
            return;
        }

        if (!CallPlace(Handle, at))
        {
            // Motion is given up for good and the card is put at its new size at once, as a card without motion is.
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            StopResizing();
            GiveUpMotion("set-window-pos:widget-card-height", error);
            SetClientSizeIfChanged(_rest.Size);
            return;
        }

        MarkDirty(ClientRectangle);
    }

    private void StopResizing()
    {
        _driver?.Stop(_resizeProgress);
        _resizeProgress.Jump(1);
    }

    private static Rectangle Lerp(Rectangle from, Rectangle to, double p) => Rectangle.FromLTRB(
        Mix(from.Left, to.Left, p), Mix(from.Top, to.Top, p), Mix(from.Right, to.Right, p), Mix(from.Bottom, to.Bottom, p));

    private static int Mix(int a, int b, double p) => (int)Math.Round(a + ((b - a) * p), MidpointRounding.AwayFromZero);

    // ---- One repaint per frame

    private void MarkDirty(Rectangle area) => _dirty = _dirty.IsEmpty ? area : Rectangle.Union(_dirty, area);

    private void OnMotionFrame()
    {
        if (_dirty.IsEmpty || IsDisposed || !IsHandleCreated || !Visible)
        {
            _dirty = Rectangle.Empty;
            return;
        }

        Rectangle area = Rectangle.Intersect(_dirty, ClientRectangle);
        _dirty = Rectangle.Empty;
        if (area.IsEmpty)
        {
            return;
        }

        Invalidate(area);
        Update();
    }

    // Whether a change on the card may be animated now.
    private bool CanAnimateContent => _driver is { AnimationsEnabled: true } && IsHandleCreated && Visible && !_exiting;

    // ---- Pages

    private readonly AnimatedValue _pageProgress = new(1);
    private int _pageSign;
    private int _pageOffset;
    private double _pageOpacity = 1;

    // How deep a page is: the main card, then settings, then the pages settings opens.
    private static int Depth(WidgetCardView view) => view switch
    {
        WidgetCardView.Main => 0,
        WidgetCardView.Settings => 1,
        _ => 2,
    };

    // The page shown changed from before to after: the new page slides in, from the right going deeper and from the left
    // coming back (design choice: a page of the same depth counts as deeper).
    private void NotePageChange(WidgetCardView before, WidgetCardView after)
    {
        if (before == after)
        {
            return;
        }

        ForgetMovingParts();
        if (!CanAnimateContent || (_animator?.Running ?? false))
        {
            return;
        }

        _pageSign = Depth(after) >= Depth(before) ? 1 : -1;
        _pageProgress.Jump(0);

        // The whole card shows the new page's first frame now, not only the part the change of page invalidated.
        ApplyPage(0);
        MarkDirtyNow();
        _pageProgress.AnimateTo(1, _driver!, FluentMotion.PageDuration, FluentMotion.Linear, ApplyPage);
    }

    private void ApplyPage(double linear)
    {
        (_pageOffset, _pageOpacity) = PageMotion.At(FluentMotion.PageDuration * linear, CardPlacement.Scale(FluentMotion.PageTravelAt96, _dpi), _pageSign);
        MarkDirty(ClientRectangle);
    }

    // The page's offset and opacity now, for tests.
    internal (int Offset, double Opacity) PageMotionForTest => (_pageOffset, _pageOpacity);

    // Draws the page (everything inside the surface) at its offset and opacity: straight onto g when it is at rest, else into
    // a clear layer that is then drawn faded and moved.
    private void RenderPageMoving(Graphics g, Action<Graphics> page)
    {
        if (_pageOffset == 0 && _pageOpacity >= 1)
        {
            page(g);
            return;
        }

        Size size = ClientSize;
        using var layer = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using (Graphics lg = Graphics.FromImage(layer))
        {
            lg.SmoothingMode = g.SmoothingMode;
            lg.PixelOffsetMode = g.PixelOffsetMode;
            lg.TextRenderingHint = g.TextRenderingHint;
            lg.Clear(Color.Transparent);
            page(lg);
        }

        var matrix = new ColorMatrix { Matrix33 = (float)_pageOpacity };
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        GraphicsState state = g.Save();
        g.SetClip(ClientRectangle);
        g.DrawImage(layer, new Rectangle(_pageOffset, 0, size.Width, size.Height), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, attributes);
        g.Restore(state);
    }

    // A new page starts with nothing half way: no fill fading, no knob sliding, no band opening.
    private void ForgetMovingParts()
    {
        ForgetFills();
        foreach (AnimatedValue knob in _knobs.Values)
        {
            _driver?.Stop(knob);
        }

        _knobs.Clear();
        StopExpander();
    }

    // The fills are where the controls were drawn; once those move (a scroll, a new page) they are dropped at once.
    private void ForgetFills()
    {
        foreach (AnimatedValue fill in _fills.Values)
        {
            _driver?.Stop(fill);
        }

        _fills.Clear();
        _fillLevels.Clear();
        _hot = null;
        _hotPressed = false;
    }

    // ---- Hover and pressed fills (icon buttons, the design's Subtle hover and Subtle pressed)

    private readonly Dictionary<Rectangle, AnimatedValue> _fills = [];
    private readonly Dictionary<Rectangle, double> _fillLevels = [];
    private Rectangle? _hot;
    private bool _hotPressed;

    private const double Hover = 1;
    private const double Pressed = 2;

    // The icon button under point, or null.
    private Rectangle? IconButtonAt(Point point)
    {
        foreach (CardControl control in CurrentControls())
        {
            if (control is { IconOnly: true, Role: CardControlRole.PushButton, Enabled: true, Offscreen: false } && HitsControl(control, point))
            {
                return control.Bounds;
            }
        }

        return null;
    }

    // The pointer moved, went down or up, or left: the fill under it fades to hover or pressed and the one it left fades out.
    private void TrackHot(Point? point, bool pressed)
    {
        Rectangle? hot = point is { } p && !_notice && !_exiting ? IconButtonAt(p) : null;
        if (hot == _hot && pressed == _hotPressed)
        {
            return;
        }

        if (_hot is { } old && old != hot)
        {
            FadeFill(old, 0);
        }

        _hot = hot;
        _hotPressed = pressed && hot is not null;
        if (hot is { } now)
        {
            FadeFill(now, _hotPressed ? Pressed : Hover);
        }
    }

    private void FadeFill(Rectangle bounds, double level)
    {
        if (!_fills.TryGetValue(bounds, out AnimatedValue? fill))
        {
            if (level == 0)
            {
                return;
            }

            fill = new AnimatedValue(0);
            _fills[bounds] = fill;
        }

        fill.AnimateTo(level, CanAnimateContent ? _driver : null, FluentMotion.HoverDuration, FluentMotion.Linear, value =>
        {
            _fillLevels[bounds] = value;
            MarkDirty(Rectangle.Inflate(bounds, 1, 1));
            if (!IsHandleCreated || _driver is null || !_driver.IsRunning(fill))
            {
                // Put there in one step: nothing will repaint it as a frame, so it is asked for now.
                MarkDirtyNow();
            }
        });
    }

    private void MarkDirtyNow()
    {
        if (IsHandleCreated && !_dirty.IsEmpty)
        {
            Invalidate(Rectangle.Intersect(_dirty, ClientRectangle));
        }

        _dirty = Rectangle.Empty;
    }

    // The fills as they are now, under the icons.
    private void DrawFills(Graphics g)
    {
        if (_fillLevels.Count == 0)
        {
            return;
        }

        CardColours colours = Colours;
        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);
        foreach ((Rectangle bounds, double level) in _fillLevels)
        {
            if (level <= 0)
            {
                continue;
            }

            Color colour = FillColour(colours.SubtleHover, colours.SubtlePressed, level);
            using GraphicsPath path = CardPaint.RoundedRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
            using var brush = new SolidBrush(colour);
            g.FillPath(brush, path);
        }
    }

    // Level 0 is no fill, 1 the hover fill, 2 the pressed fill; in between, the colours are mixed (from no fill, the hover
    // fill's own colour at a share of its alpha).
    internal static Color FillColour(Color hover, Color pressed, double level)
    {
        if (level <= 1)
        {
            return Color.FromArgb((int)Math.Round(hover.A * Math.Max(0, level)), hover.R, hover.G, hover.B);
        }

        double p = Math.Min(1, level - 1);
        return Color.FromArgb(
            (int)Math.Round(hover.A + ((pressed.A - hover.A) * p)),
            (int)Math.Round(hover.R + ((pressed.R - hover.R) * p)),
            (int)Math.Round(hover.G + ((pressed.G - hover.G) * p)),
            (int)Math.Round(hover.B + ((pressed.B - hover.B) * p)));
    }

    // For tests: the level of the fill on bounds (0 none, 1 hover, 2 pressed).
    internal double FillLevelForTest(Rectangle bounds) => _fillLevels.TryGetValue(bounds, out double level) ? level : 0;

    // ---- Toggle knobs

    private readonly Dictionary<object, AnimatedValue> _knobs = [];
    private readonly Dictionary<object, Rectangle> _knobBounds = [];

    // Where the knob of the toggle named key is drawn, 0 off to 1 on. A toggle seen for the first time is where it rests; one
    // whose value changed since it was last drawn slides there.
    private double Knob(object key, Rectangle bounds, bool on)
    {
        double target = on ? 1 : 0;
        _knobBounds[key] = bounds;
        if (!_knobs.TryGetValue(key, out AnimatedValue? knob))
        {
            _knobs[key] = new AnimatedValue(target);
            return target;
        }

        if (knob.Target != target)
        {
            knob.AnimateTo(target, CanAnimateContent ? _driver : null, FluentMotion.ToggleDuration, FluentMotion.ToggleCurve, _ =>
            {
                // A knob put there in one step is drawn by the paint under way; only frames ask for more.
                if (_driver?.IsRunning(knob) == true)
                {
                    MarkDirty(Rectangle.Inflate(_knobBounds[key], 1, 1));
                }
            });
        }

        return _driver is null ? knob.Target : knob.ValueAt(_driver.Now);
    }

    // ---- Expanders

    private sealed record ExpanderBand(SettingsRowId Row, SettingsLayout Expanded, int Split, int Delta, AnimatedValue Reveal);

    private ExpanderBand? _band;

    // The expander on row is opening (true) or closing; before is the page as it was drawn, after the page as it now is. The
    // rows under the expander move with the band as it opens or closes, in step with the card's height.
    private void NoteExpander(SettingsRowId row, bool opening, SettingsLayout before, SettingsLayout after)
    {
        SettingsLayout expanded = opening ? after : before;
        SettingsLayout collapsed = opening ? before : after;
        int delta = expanded.Frame.Body.Height - collapsed.Frame.Body.Height;
        SettingsItem? head = expanded.Items.FirstOrDefault(i => i.Row == row && i.Kind == SettingsItemKind.Row);
        if (!CanAnimateContent || delta <= 0 || head is null)
        {
            StopExpander();
            return;
        }

        double from = opening ? 0 : delta;
        if (_band is { } running && running.Row == row && running.Delta == delta)
        {
            from = running.Reveal.ValueAt(_driver!.Now);
        }

        StopExpander();
        var reveal = new AnimatedValue(from);
        _band = new ExpanderBand(row, expanded, head.Bounds.Bottom, delta, reveal);
        reveal.AnimateTo(opening ? delta : 0, _driver, FluentMotion.HeightDuration, FluentMotion.PointToPoint, _ =>
        {
            MarkDirty(ClientRectangle);
            if (!_driver!.IsRunning(reveal) || !reveal.MovingAt(_driver.Now))
            {
                _band = null;
            }
        });
    }

    private void StopExpander()
    {
        if (_band is { } band)
        {
            _driver?.Stop(band.Reveal);
            _band = null;
        }
    }

    // How far the band is open now, for tests, or null when no expander is moving.
    internal double? ExpanderRevealForTest => _band is { } band && _driver is not null ? band.Reveal.ValueAt(_driver.Now) : null;

    // The rows of the settings page, with a moving band where an expander opens or closes: the rows above it as they are, the
    // band cut to how far it is open, and the rows below moved up by what is not yet open.
    private void DrawSettingsRowsMoving(Graphics g, CardSettingsValues values, SettingsLayout layout, CardColours colours, bool focusVisible, SettingsTarget focus)
    {
        if (_band is not { } band || _driver is null || !band.Reveal.MovingAt(_driver.Now))
        {
            DrawSettingsRows(g, values, layout, colours, focusVisible, focus);
            return;
        }

        int open = (int)Math.Round(band.Reveal.ValueAt(_driver.Now), MidpointRounding.AwayFromZero);
        int width = band.Expanded.Frame.Width;
        const int Far = 1 << 20;
        Rectangle[] parts =
        [
            Rectangle.FromLTRB(0, -Far, width, band.Split),
            Rectangle.FromLTRB(0, band.Split, width, band.Split + open),
            Rectangle.FromLTRB(0, band.Split + open, width, Far),
        ];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Height <= 0)
            {
                continue;
            }

            GraphicsState state = g.Save();
            g.SetClip(parts[i], CombineMode.Intersect);
            if (i == 2)
            {
                g.TranslateTransform(0, open - band.Delta);
            }

            DrawSettingsRows(g, values, band.Expanded, colours, focusVisible, focus);
            g.Restore(state);
        }
    }
}

// A new page's place and opacity at a time since it began: it slides from travel pixels to the right (sign 1, going deeper) or
// the left (sign -1, coming back) to rest on the entrance curve over the page duration, and fades in linearly over the page
// fade. Pure, for the card and the tests.
internal static class PageMotion
{
    public static (int Offset, double Opacity) At(TimeSpan elapsed, int travel, int sign)
    {
        if (elapsed >= FluentMotion.PageDuration)
        {
            return (0, 1);
        }

        double t = Math.Max(0, elapsed / FluentMotion.PageDuration);
        double eased = FluentMotion.Enter.Progress(t);
        int offset = (int)Math.Round(sign * travel * (1 - eased), MidpointRounding.AwayFromZero);
        double opacity = Math.Clamp(elapsed / FluentMotion.PageFade, 0, 1);
        return (offset, opacity);
    }
}
