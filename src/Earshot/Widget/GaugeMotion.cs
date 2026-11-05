namespace Earshot.Widget;

// What moves on the taskbar gauge: the ring's arc, which goes to a new value point to point (FluentMotion's ring: Slow on
// PointToPoint, a design choice) while the number beside it is the new value from the first frame, and the hover fill, which
// fades in and out over FluentMotion's hover time. Pure: GaugeWindow draws what this says and pushes a frame when Changed is
// raised.
//
// The ring moves only from one drawn arc to another. An arc appearing (a first reading) or going (the AirPods leave this PC
// and no case value is left) is drawn at once, as is any change while the gauge is not on screen or with animation effects off.
//
// The connect story (GaugeContent.Story) is drawn through Frame, from the content each Show is given:
//   connecting   the number fades out over 83 ms, the arc becomes a 90 degree sweep over 167 ms (point to point) and turns once
//                per 1000 ms, linear, clockwise, off the frame clock
//   connected    the sweep stops and comes round to 12 o'clock while the arc goes to the live level over 250 ms; the tick fades in
//                over 83 ms
//   failed       the arc empties to the track over 167 ms; the cross fades in over 83 ms
//   leaving      the glyph fades out and the number fades back in over 83 ms, the arc to what the state draws at rest
// Frames are asked of the display only while something moves: a turning sweep and the short moves between looks. The holds, the
// gauge at rest and a gauge nobody can see ask for none. With animation effects off, or no driver, the sweep is a still quarter
// at 12 o'clock, and the tick and the cross appear and leave with no fade. The durations are FluentMotion's; the 1000 ms turn,
// the 90 degree sweep and the 250, 167 and 83 ms moves are design choices.
internal sealed class GaugeMotion
{
    // The sweep's turn.
    private static readonly TimeSpan Turn = TimeSpan.FromMilliseconds(1000);

    private readonly AnimatedValue _ring = new(0);
    private readonly AnimatedValue _hover = new(0);
    private bool _known;
    private bool _hadArc;

    private readonly object _storyKey = new();
    private readonly AnimatedValue _start = new(GaugeStoryFrame.TwelveOClock);
    private readonly AnimatedValue _extent = new(0);
    private readonly AnimatedValue _glyphOpacity = new(0);
    private readonly AnimatedValue _numberOpacity = new(1);
    private double _vStart = GaugeStoryFrame.TwelveOClock;
    private double _vExtent;
    private double _vGlyph;
    private double _vNumber = 1;
    private GaugeSlotGlyph _glyph;
    private GaugeStoryKind _kind;
    private bool _framed;
    private bool _spinning;
    private TimeSpan _spinStart;
    private GaugeContent? _content;

    // The frames, or null for none (every change then lands at once).
    public FrameDriver? Driver { get; set; }

    // A frame moved the ring, the fill or the story: the gauge is to be drawn and pushed again.
    public event Action? Changed;

    // Where the arc is drawn, as a percentage.
    public double Ring { get; private set; }

    // How strong the hover fill is, 0 to 1.
    public double Hover { get; private set; }

    // Where the story is now, or null when it is not drawn: the gauge at rest.
    public GaugeStoryFrame? Frame { get; private set; }

    // The gauge is about to draw content; onScreen says whether it is shown.
    public void Show(GaugeContent content, bool onScreen)
    {
        ArgumentNullException.ThrowIfNull(content);
        ShowRing(content, onScreen);
        ShowStory(content, onScreen);
    }

    // The gauge is no longer on screen (hidden for a full-screen application, or the window gone): nothing turns for a window that
    // draws nothing, and the story, kept, is the still state it is in.
    public void Suspend()
    {
        if (_content is { } content)
        {
            SettleStill(content);
        }
    }

    private void ShowRing(GaugeContent content, bool onScreen)
    {
        bool arc = content.Mode is GaugeMode.Reading or GaugeMode.CaseAway;
        double target = arc ? content.Percent ?? 0 : 0;
        bool move = _known && _hadArc && arc && onScreen;
        _known = true;
        _hadArc = arc;
        if (!move)
        {
            Driver?.Stop(_ring);
            _ring.Jump(target);
            Ring = target;
            return;
        }

        if (target == _ring.Target)
        {
            return;
        }

        _ring.AnimateTo(target, Driver, FluentMotion.RingDuration, FluentMotion.PointToPoint, value =>
        {
            Ring = value;
            if (Driver?.IsRunning(_ring) == true)
            {
                Changed?.Invoke();
            }
        });
    }

    private void ShowStory(GaugeContent content, bool onScreen)
    {
        _content = content;
        GaugeStoryKind kind = content.Story.Kind;
        if (!onScreen || Driver is not { AnimationsEnabled: true } driver)
        {
            SettleStill(content);
            return;
        }

        if (kind == _kind)
        {
            // The same story: a sweep that was stopped (the gauge was hidden) turns again; nothing else moves.
            if (kind == GaugeStoryKind.Spinning && !driver.IsRunning(_storyKey))
            {
                SettleStill(content);
                BeginSpin(driver);
                driver.Run(_storyKey, Step);
            }

            return;
        }

        Begin(content, kind, driver);
    }

    // A story that moves, from where the gauge is now.
    private void Begin(GaugeContent content, GaugeStoryKind kind, FrameDriver driver)
    {
        TimeSpan now = driver.Now;
        if (!_framed)
        {
            // From the gauge at rest: the arc as the ring draws it, the number whole, no glyph.
            _framed = true;
            Put(GaugeStoryFrame.TwelveOClock, GaugeStoryFrame.RestExtent(content), 0, 1);
        }

        // Leaving the sweep, the arc comes round to 12 o'clock from where it has turned to; from any other look it stays where it is.
        double home = _spinning ? RoundTwelve : _vStart;
        _start.Jump(_vStart);
        _spinning = false;
        _kind = kind;
        switch (kind)
        {
            case GaugeStoryKind.Spinning:
                _glyphOpacity.MoveTo(0, now, FluentMotion.Fast, FluentMotion.Linear);
                _numberOpacity.MoveTo(0, now, FluentMotion.Fast, FluentMotion.Linear);
                _extent.MoveTo(GaugeStoryFrame.SweepDegrees, now, FluentMotion.Normal, FluentMotion.PointToPoint);
                BeginSpin(driver);
                break;

            case GaugeStoryKind.Tick:
                _glyph = GaugeSlotGlyph.Tick;
                _glyphOpacity.MoveTo(1, now, FluentMotion.Fast, FluentMotion.Linear);
                _numberOpacity.MoveTo(0, now, FluentMotion.Fast, FluentMotion.Linear);
                _extent.MoveTo(GaugeStoryFrame.LiveExtent(content), now, FluentMotion.Slow, FluentMotion.PointToPoint);
                _start.MoveTo(home, now, FluentMotion.Slow, FluentMotion.PointToPoint);
                break;

            case GaugeStoryKind.Cross:
                _glyph = GaugeSlotGlyph.Cross;
                _glyphOpacity.MoveTo(1, now, FluentMotion.Fast, FluentMotion.Linear);
                _numberOpacity.MoveTo(0, now, FluentMotion.Fast, FluentMotion.Linear);
                _extent.MoveTo(0, now, FluentMotion.Normal, FluentMotion.PointToPoint);
                _start.MoveTo(home, now, FluentMotion.Normal, FluentMotion.PointToPoint);
                break;

            default:
                _glyphOpacity.MoveTo(0, now, FluentMotion.Fast, FluentMotion.Linear);
                _numberOpacity.MoveTo(1, now, FluentMotion.Fast, FluentMotion.Linear);
                _extent.MoveTo(GaugeStoryFrame.RestExtent(content), now, FluentMotion.Slow, FluentMotion.PointToPoint);
                _start.MoveTo(home, now, FluentMotion.Slow, FluentMotion.PointToPoint);
                break;
        }

        // Frame 0 is what is on screen now, so the push that follows this call changes nothing the owner can see; the frames
        // that follow move it.
        Frame = Compose();
        driver.Run(_storyKey, Step);
    }

    // 12 o'clock again after a turn: the sweep has turned from -90 up to just short of 270, so it comes round to 270.
    private static double RoundTwelve => GaugeStoryFrame.TwelveOClock + 360;

    private void BeginSpin(FrameDriver driver)
    {
        _spinning = true;
        _spinStart = driver.Now;
    }

    // One frame of the story at the time of a refresh of the display.
    private bool Step(TimeSpan at)
    {
        _vStart = _spinning ? SpinAngle(at) : _start.ValueAt(at);
        _vExtent = _extent.ValueAt(at);
        _vGlyph = _glyphOpacity.ValueAt(at);
        _vNumber = _numberOpacity.ValueAt(at);
        bool moving = _spinning || _start.MovingAt(at) || _extent.MovingAt(at) || _glyphOpacity.MovingAt(at) || _numberOpacity.MovingAt(at);
        if (!moving && _kind == GaugeStoryKind.None)
        {
            // The glyph has gone and the number is back: the gauge is at rest.
            _framed = false;
            Frame = null;
        }
        else
        {
            Frame = Compose();
        }

        Changed?.Invoke();
        return moving;
    }

    private double SpinAngle(TimeSpan at)
    {
        double turns = Math.Max(0, (at - _spinStart) / Turn);
        return GaugeStoryFrame.TwelveOClock + (360 * (turns - Math.Floor(turns)));
    }

    private GaugeStoryFrame Compose() => new(_kind, _vStart, _vExtent, _glyph, _vGlyph, _vNumber);

    // Nothing moves: the story is its still state (the sweep at 12 o'clock, the tick or the cross whole, the gauge at rest). Used
    // with animation effects off, with no driver, for a gauge nobody sees, and when something that was moving is stopped.
    private void SettleStill(GaugeContent content)
    {
        Driver?.Stop(_storyKey);
        _spinning = false;
        _kind = content.Story.Kind;
        GaugeStoryFrame still = GaugeStoryFrame.Still(content);
        Put(still.Start, still.Extent, still.GlyphOpacity, still.NumberOpacity);
        _glyph = still.Glyph;
        _framed = _kind != GaugeStoryKind.None;
        Frame = _framed ? still : null;
    }

    private void Put(double start, double extent, double glyph, double number)
    {
        _start.Jump(start);
        _extent.Jump(extent);
        _glyphOpacity.Jump(glyph);
        _numberOpacity.Jump(number);
        _vStart = start;
        _vExtent = extent;
        _vGlyph = glyph;
        _vNumber = number;
    }

    // The pointer came onto the gauge or left it. The caller draws again afterwards.
    public void SetHover(bool hover, bool onScreen)
    {
        double target = hover ? 1 : 0;
        _hover.AnimateTo(target, onScreen ? Driver : null, FluentMotion.HoverDuration, FluentMotion.Linear, value =>
        {
            Hover = value;
            if (Driver?.IsRunning(_hover) == true)
            {
                Changed?.Invoke();
            }
        });
    }
}
