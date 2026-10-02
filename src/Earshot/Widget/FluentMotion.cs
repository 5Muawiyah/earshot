namespace Earshot.Widget;

// Every duration and curve Earshot moves anything with, in one place.
//
// From Microsoft's pages (the owner's reading of them, which this code takes as given):
//   durations 83, 167 and 250 ms;
//   entering: cubic-bezier(0, 0, 0, 1); exiting: cubic-bezier(1, 0, 1, 1);
//   https://learn.microsoft.com/windows/apps/design/motion/timing-and-easing
//   point to point (a thing that is already on screen moving to a new place or size): cubic-bezier(0.55, 0.55, 0, 1);
//   taskbar flyouts slide up when invoked and down when dismissed.
//   https://learn.microsoft.com/windows/apps/design/signature-experiences/motion
//
// Which of those each movement uses is the design's motion table where it has a row (flyout entrance and exit, toggle knob,
// scroll bar fade) and otherwise a design choice, named where it is used and listed here:
//   page change (main, settings, Updates, history): Slow on Enter, the new page sliding in from PageTravelAt96 to the right
//     (going deeper) or the left (going back) and fading in over Fast, linearly, as the flyout entrance fades;
//   height change (the card growing or shrinking, an expander opening or closing): Slow on PointToPoint;
//   hover and pressed fills: Fast, linear (a fill is a fade, and the table's fades are linear);
//   gauge ring: Slow on PointToPoint; the number changes at once.
internal static class FluentMotion
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(83);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(250);

    public static readonly CubicBezier Enter = new(0, 0, 0, 1);
    public static readonly CubicBezier Exit = new(1, 0, 1, 1);
    public static readonly CubicBezier PointToPoint = new(0.55, 0.55, 0, 1);

    // A straight line, for fades.
    public static readonly CubicBezier Linear = new(0, 0, 1, 1);

    // Design choice: a new page slides 40 px (scaled), the same travel as the flyout's entrance.
    public const int PageTravelAt96 = 40;

    // The table's toggle knob row.
    public static TimeSpan ToggleDuration => Normal;

    public static CubicBezier ToggleCurve => Enter;

    // Design choices, listed above.
    public static TimeSpan HoverDuration => Fast;

    public static TimeSpan PageDuration => Slow;

    public static TimeSpan PageFade => Fast;

    public static TimeSpan HeightDuration => Slow;

    public static TimeSpan RingDuration => Slow;
}
