namespace Earshot.Widget;

// The mark the story puts in the number's place.
internal enum GaugeSlotGlyph
{
    None,

    // The check icon, 12 px at 100%, in the accent.
    Tick,

    // The close icon, 12 px at 100%, in caution.
    Cross,
}

// One frame of the story as the renderer draws it: where the arc starts and how far it runs, what is in the number's place, and
// how strongly. The motion makes these from time; with animation effects off, or when the renderer is given none, Still gives the
// state the story is in, with nothing part way.
//
// Start and Extent are GDI+ degrees: clockwise from 3 o'clock, so 12 o'clock is -90. Phase says which look the arc has: while
// it is the sweep (Spinning) it is the accent, and otherwise the ring's own colour, caution when the reading is low.
internal readonly record struct GaugeStoryFrame(
    GaugeStoryKind Phase, double Start, double Extent, GaugeSlotGlyph Glyph, double GlyphOpacity, double NumberOpacity)
{
    // 12 o'clock.
    public const double TwelveOClock = -90;

    // The sweep that turns while it connects: a quarter of the ring, 90 degrees (a design choice).
    public const double SweepDegrees = 90;

    // The arc that is the live level: the reading's share of the circle, or none when there is no live reading to sweep to
    // (no figure, or a last reading, which is not the level now).
    public static double LiveExtent(GaugeContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content is { Mode: GaugeMode.Reading, Tertiary: false, Percent: int percent } ? 360.0 * Math.Clamp(percent, 0, 100) / 100.0 : 0;
    }

    // The arc the ring draws at rest: the figure's share of the circle for a reading or the case's value, live or last, and none
    // otherwise.
    public static double RestExtent(GaugeContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content is { Mode: GaugeMode.Reading or GaugeMode.CaseAway, Percent: int percent } ? 360.0 * Math.Clamp(percent, 0, 100) / 100.0 : 0;
    }

    // The frame for a content's story with nothing part way: the sweep still at 12 o'clock, the tick or the cross whole, or the
    // gauge at rest (the arc at the level the ring itself draws, the number whole).
    public static GaugeStoryFrame Still(GaugeContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Story.Kind switch
        {
            GaugeStoryKind.Spinning => new GaugeStoryFrame(GaugeStoryKind.Spinning, TwelveOClock, SweepDegrees, GaugeSlotGlyph.None, 0, 0),
            GaugeStoryKind.Tick => new GaugeStoryFrame(GaugeStoryKind.Tick, TwelveOClock, LiveExtent(content), GaugeSlotGlyph.Tick, 1, 0),
            GaugeStoryKind.Cross => new GaugeStoryFrame(GaugeStoryKind.Cross, TwelveOClock, 0, GaugeSlotGlyph.Cross, 1, 0),
            _ => new GaugeStoryFrame(GaugeStoryKind.None, TwelveOClock, RestExtent(content), GaugeSlotGlyph.None, 0, 1),
        };
    }
}
