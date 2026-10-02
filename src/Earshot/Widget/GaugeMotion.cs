namespace Earshot.Widget;

// What moves on the taskbar gauge: the ring's arc, which goes to a new value point to point (FluentMotion's ring: Slow on
// PointToPoint, a design choice) while the number beside it is the new value from the first frame, and the hover fill, which
// fades in and out over FluentMotion's hover time. Pure: GaugeWindow draws what this says and pushes a frame when Changed is
// raised.
//
// The ring moves only from one drawn arc to another. An arc appearing (a first reading) or going (the AirPods leave this PC
// and no case value is left) is drawn at once, as is any change while the gauge is not on screen or with animation effects off.
internal sealed class GaugeMotion
{
    private readonly AnimatedValue _ring = new(0);
    private readonly AnimatedValue _hover = new(0);
    private bool _known;
    private bool _hadArc;

    // The frames, or null for none (every change then lands at once).
    public FrameDriver? Driver { get; set; }

    // A frame moved the ring or the fill: the gauge is to be drawn and pushed again.
    public event Action? Changed;

    // Where the arc is drawn, as a percentage.
    public double Ring { get; private set; }

    // How strong the hover fill is, 0 to 1.
    public double Hover { get; private set; }

    // The gauge is about to draw content; onScreen says whether it is shown.
    public void Show(GaugeContent content, bool onScreen)
    {
        ArgumentNullException.ThrowIfNull(content);
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
