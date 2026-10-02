namespace Earshot.Widget;

// A number that moves, and the figure a paint under way should draw for it: a knob's place, an expander's opening. The figure
// is the one the last frame was given, never one read from the wall clock at paint time, so a frame stamped ahead of now (the
// unpaced fallback stamps an hour ahead so a motion ends in one frame) cannot leave a repaint drawing the value part-way.
// UI thread only.
internal sealed class ShownValue(double value)
{
    private readonly AnimatedValue _value = new(value);
    private double _shown = value;

    public double Target => _value.Target;

    // True while a frame source is running this value.
    public bool IsMoving(FrameDriver? driver) => driver is not null && driver.IsRunning(_value);

    // What to draw now: the last frame's value while it moves, where it rests when it does not.
    public double Level(FrameDriver? driver) => IsMoving(driver) ? _shown : _value.Target;

    // Sends the value to target; onFrame is called with each frame's value, and once at once when nothing is animated.
    public void AnimateTo(double target, FrameDriver? driver, TimeSpan duration, CubicBezier curve, Action<double> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (!IsMoving(driver))
        {
            _shown = _value.Target;
        }

        _value.AnimateTo(target, driver, duration, curve, v =>
        {
            _shown = v;
            onFrame(v);
        });
    }

    public void Stop(FrameDriver? driver) => driver?.Stop(_value);
}
