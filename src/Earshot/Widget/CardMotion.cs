namespace Earshot.Widget;

// A cubic Bezier easing curve through (0,0) and (1,1) with the two control points given, as the Windows motion
// page writes them: cubic-bezier(x1, y1, x2, y2). Progress(t) is the share of the way travelled after the share t
// of the time. x(s) is monotonic for the curves used here, so it is inverted by bisection.
// https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion
internal readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
{
    public double Progress(double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        double low = 0;
        double high = 1;
        double s = t;
        for (int i = 0; i < 60; i++)
        {
            s = (low + high) / 2;
            if (Axis(X1, X2, s) < t)
            {
                low = s;
            }
            else
            {
                high = s;
            }
        }

        return Math.Clamp(Axis(Y1, Y2, s), 0, 1);
    }

    // The Bezier through 0, a, b and 1 at parameter s.
    private static double Axis(double a, double b, double s)
    {
        double u = 1 - s;
        return (3 * u * u * s * a) + (3 * u * s * s * b) + (s * s * s);
    }
}

internal enum MotionKind { Enter, Exit }

// What one motion is: its direction, how far the card travels (signed, in pixels, from rest), how long it runs, and
// where it starts, so a motion cut short by the opposite one carries on from where the card is.
internal sealed record CardMotionPlan(
    MotionKind Kind, int Travel, TimeSpan Duration, TimeSpan FadeDuration, int StartOffset, double StartOpacity);

// The card's offset from rest (pixels, signed like the travel) and its opacity (0 to 255) at one moment, and whether
// the motion is over.
internal readonly record struct MotionFrame(int OffsetPx, byte Alpha, bool Done);

// The card's entrance and exit, as pure functions of the time since the motion began.
//
// Taskbar flyouts slide up when invoked and down when dismissed, so a card enters from 40 px (scaled) below its place (above a
// bottom taskbar) and leaves the way it came. The design's motion table: the entrance runs 250 ms on
// cubic-bezier(0, 0, 0, 1) and its opacity rises 0 to 1 linearly over the first 83 ms; the exit runs 167 ms on
// cubic-bezier(1, 0, 1, 1) with its opacity falling 1 to 0 linearly over the whole 167 ms. The curve of the entrance covers
// half the distance in the first eighth of the time, so it reads as immediate yet still visibly slides.
// https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion
internal static class CardMotion
{
    public static readonly CubicBezier Decelerate = new(0, 0, 0, 1);

    // The exit's curve: cubic-bezier(1, 0, 1, 1).
    public static readonly CubicBezier Accelerate = new(1, 0, 1, 1);

    public static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan EnterFade = TimeSpan.FromMilliseconds(83);
    public static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(167);

    // How far the card slides: 40 px at 100%, scaled with the display.
    public const int TravelAt96 = 40;

    // Whether the card fades as well as slides. The fade is a constant window opacity (WS_EX_LAYERED with
    // LWA_ALPHA), and Microsoft does not say whether the system backdrop and the rounded corners survive that style.
    // If the owner finds the backdrop or the corners gone, this is the one switch: false slides the card at full
    // opacity and drops the layered style.
    public static readonly bool UseAlphaFade = true;

    // How far the card travels, signed (down is positive), and from which side: 40 px scaled, away from the taskbar edge it sits above.
    // A bottom taskbar is read from the gauge, which is centred on the taskbar's short side and so lies below the work area; a top
    // one the same above it. With no gauge on a taskbar (hidden, or the taskbar auto-hides and the work area covers it) the travel is
    // downward.
    public static int TravelFor(Rectangle anchor, Rectangle workArea, int dpi)
    {
        int travel = Popup.CardPlacement.Scale(TravelAt96, dpi);
        if (anchor.Width <= 0 || anchor.Height <= 0)
        {
            return travel;
        }

        int centre = anchor.Y + (anchor.Height / 2);
        return centre < workArea.Top ? -travel : travel;
    }

    // Entrance from travel pixels away, invisible. A card already partly in view starts from there.
    public static CardMotionPlan Enter(int travel, int startOffset, double startOpacity) =>
        new(MotionKind.Enter, travel, EnterDuration, EnterFade, startOffset, Math.Clamp(startOpacity, 0, 1));

    public static CardMotionPlan EnterFromBelow(int travel) => Enter(travel, travel, 0);

    // Exit to travel pixels away, fading out; from wherever the card is now.
    public static CardMotionPlan Exit(int travel, int startOffset, double startOpacity) =>
        new(MotionKind.Exit, travel, ExitDuration, ExitDuration, startOffset, Math.Clamp(startOpacity, 0, 1));

    public static CardMotionPlan ExitFromRest(int travel) => Exit(travel, 0, 1);

    // The frame at elapsed since the motion began. With animation effects off the motion is one frame, at rest for an
    // entrance and gone for an exit.
    public static MotionFrame FrameAt(CardMotionPlan plan, TimeSpan elapsed, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (reducedMotion)
        {
            return plan.Kind == MotionKind.Enter ? new MotionFrame(0, 255, Done: true) : new MotionFrame(plan.Travel, 0, Done: true);
        }

        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        double time = elapsed.TotalMilliseconds / plan.Duration.TotalMilliseconds;
        double progress = (plan.Kind == MotionKind.Enter ? Decelerate : Accelerate).Progress(time);
        bool done = elapsed >= plan.Duration;
        if (plan.Kind == MotionKind.Enter)
        {
            double fade = Math.Min(1.0, elapsed.TotalMilliseconds / plan.FadeDuration.TotalMilliseconds);
            double opacity = Math.Min(1.0, plan.StartOpacity + ((1 - plan.StartOpacity) * fade));
            return new MotionFrame(Round(plan.StartOffset * (1 - progress)), ToAlpha(opacity), done);
        }

        double remaining = Math.Max(0, 1 - (elapsed.TotalMilliseconds / plan.FadeDuration.TotalMilliseconds));
        return new MotionFrame(Round(plan.StartOffset + ((plan.Travel - plan.StartOffset) * progress)), ToAlpha(plan.StartOpacity * remaining), done);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static byte ToAlpha(double opacity) => (byte)Math.Clamp((int)Math.Round(255 * opacity, MidpointRounding.AwayFromZero), 0, 255);
}
