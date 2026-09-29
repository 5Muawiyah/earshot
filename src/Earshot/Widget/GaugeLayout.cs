using Earshot.Popup;

namespace Earshot.Widget;

// Where the gauge sits on the taskbar. Stored in the settings as its number; a number that names neither is
// read as RightEnd.
public enum GaugePosition
{
    // The right-hand end of the taskbar, a little to the left of the notification area.
    RightEnd = 0,

    // Just after the last app button.
    NextToApps = 1,
}

// Every coordinate of the ring-and-number gauge, in physical pixels, for one display scale. Nothing here
// draws or measures anything; it is the table the renderer and the placement read, so a test can check a
// coordinate without a bitmap.
//
// 100%, 125% and 150% are written out (the design gives each figure at each scale, and simple rounding of
// the 100% figure does not reproduce them: 7 at 150% is 10, not 11, on the left and 11 on the right). Any
// other scale scales the 100% figures and rounds half away from zero.
//
// Left to right, all x measured from the window's left edge:
//   | left pad | ring box | gap | number slot | charging slot | right pad |
// The ring box holds the ring and, inside it, the earbud mark. The number slot always fits "100", so the
// window never changes width. The charging slot is always reserved, whether or not a bolt is drawn.
internal readonly record struct GaugeLayout(
    int Dpi,
    int Width,
    int Height,
    Rectangle RingBox,        // the square the ring is drawn in
    float RingRadius,         // of the ring's centre line
    float RingStroke,
    Rectangle Mark,           // the earbud mark
    Rectangle NumberSlot,     // where the digits go, left aligned
    Rectangle ChargingSlot,   // where the bolt goes, centred
    Size Bolt,                // the bolt's own box
    int PhoneSize,            // the phone mark in the number slot
    int TypePixels,           // the number's type size, in pixels
    int CornerRadius)         // the hover fill's corner radius
{
    // The type is 12 px at 100%; a line box is 16 px.
    private const int TypeAt96 = 12;
    private const int PhoneAt96 = 16;
    private const int CornerAt96 = 4;

    public static GaugeLayout For(int dpi) => dpi switch
    {
        96 => Build(96, 74, 40, 7, 24, 10.5f, 2f, (13, 14, 12), (35, 22), (57, 10), (8, 12)),
        120 => Build(120, 93, 50, 9, 30, 13f, 2.5f, (16, 17, 15), (44, 28), (72, 12), (10, 15)),
        144 => Build(144, 111, 60, 10, 36, 15.75f, 3f, (19, 21, 18), (52, 33), (85, 15), (12, 18)),
        _ => Scaled(dpi),
    };

    // The window's own rectangle centred on a taskbar's short axis, at x.
    public Rectangle BoundsAt(int x, Rectangle taskbar) =>
        new(x, taskbar.Top + ((taskbar.Height - Height) / 2), Width, Height);

    private static GaugeLayout Build(
        int dpi, int width, int height, int leftPad, int ringSize, float radius, float stroke,
        (int X, int Y, int Size) mark, (int X, int Width) number, (int X, int Width) charging, (int W, int H) bolt)
    {
        int ringTop = (height - ringSize) / 2;
        int line = (int)Math.Round(16 * dpi / 96.0, MidpointRounding.AwayFromZero);
        return new GaugeLayout(
            dpi, width, height,
            new Rectangle(leftPad, ringTop, ringSize, ringSize), radius, stroke,
            new Rectangle(mark.X, mark.Y, mark.Size, mark.Size),
            new Rectangle(number.X, (height - line) / 2, number.Width, line),
            new Rectangle(charging.X, (height - bolt.H) / 2, charging.Width, bolt.H),
            new Size(bolt.W, bolt.H),
            (int)Math.Round(PhoneAt96 * dpi / 96.0, MidpointRounding.AwayFromZero),
            (int)Math.Round(TypeAt96 * dpi / 96.0, MidpointRounding.AwayFromZero),
            (int)Math.Round(CornerAt96 * dpi / 96.0, MidpointRounding.AwayFromZero));
    }

    private static GaugeLayout Scaled(int dpi)
    {
        int effective = dpi > 0 ? dpi : CardPlacement.BaseDpi;
        int S(int at96) => CardPlacement.Scale(at96, effective);
        int leftPad = S(7);
        int ring = S(24);
        int gap = S(4);
        int numberWidth = S(22);
        int chargeWidth = S(10);
        int rightPad = S(7);
        int width = leftPad + ring + gap + numberWidth + chargeWidth + rightPad;
        float stroke = Math.Max(1f, (float)(Math.Round(2.0 * effective / CardPlacement.BaseDpi * 2, MidpointRounding.AwayFromZero) / 2));
        return Build(
            effective, width, S(40), leftPad, ring, 10.5f * effective / CardPlacement.BaseDpi, stroke,
            (leftPad + S(6), ((S(40) - ring) / 2) + S(6), S(12)),
            (leftPad + ring + gap, numberWidth), (leftPad + ring + gap + numberWidth, chargeWidth),
            (S(8), S(12)));
    }
}
