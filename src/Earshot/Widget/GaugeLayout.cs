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

// Every coordinate of the ring-and-number gauge, in physical pixels, for one display scale s (dpi / 96). Nothing here
// draws or measures anything; it is the table the renderer and the placement read, so a test can check a coordinate
// without a bitmap.
//
// The design's rule, for any scale:
//   width = round(74 s), height = round(40 s)        (round half away from zero)
//   ring box = round(24 s), number slot = round(20 s), bolt slot = round(12 s), gap = round(4 s)
//   left padding = floor((width - content) / 2), content = ring + number + bolt + 2 gaps
// The three pieces sit left to right in the chosen order with one gap between neighbours, vertically centred. The
// design's table (R5 N33 B57 at 100%, R6 N41 B71 at 125%, R7 N49 B85 at 150% for ring, number, bolt) is what the rule gives.
// The ring box holds the ring and, inside it, the earbud pair or the case mark, drawn on the design's 24 unit grid scaled
// to the box. The number slot always fits "100", so the window never changes width; the bolt slot is always reserved,
// whether or not a bolt is drawn; the digits are centred in their slot.
internal readonly record struct GaugeLayout(
    int Dpi,
    int Width,
    int Height,
    Rectangle RingBox,        // the square the ring is drawn in
    float RingRadius,         // of the ring's centre line
    float RingStroke,
    Rectangle Mark,           // the mark's grid: the ring box, the 24 unit grid is scaled to it
    Rectangle NumberSlot,     // where the digits go, centred
    Rectangle ChargingSlot,   // where the bolt goes, centred
    Size Bolt,                // the bolt's own box
    int PhoneSize,            // the phone mark in the number slot
    int TypePixels,           // the number's type size, in pixels
    int CornerRadius)         // the hover fill's corner radius
{
    // The design's figures at 100%.
    public const int WidthAt96 = 74;
    public const int HeightAt96 = 40;
    public const int RingAt96 = 24;
    public const int NumberSlotAt96 = 20;
    public const int BoltSlotAt96 = 12;
    public const int GapAt96 = 4;
    public const float RingStrokeAt96 = 2.5f;

    // The design's earbud pair on its 24 unit grid: the heads 5 by 5 at (6.5, 6) and (12.5, 6), the stems 2 by 8 at (9.5, 9) and
    // (12.5, 9), corner radius 1.
    public const int MarkGrid = 24;

    private const int TypeAt96 = 12;
    private const int PhoneAt96 = 16;
    private const int CornerAt96 = 4;
    private const int LineAt96 = 16;

    // The gauge's size, for a taskbar to be planned around, at a scale.
    public static Size SizeFor(int dpi) => new(Round(WidthAt96, dpi), Round(HeightAt96, dpi));

    // round(at96 * dpi / 96), half away from zero.
    public static int Round(double at96, int dpi) =>
        (int)Math.Round(at96 * (dpi > 0 ? dpi : CardPlacement.BaseDpi) / CardPlacement.BaseDpi, MidpointRounding.AwayFromZero);

    public static GaugeLayout For(int dpi) => For(dpi, GaugeOrder.RingNumberBolt);

    // The layout with the three pieces in the given order (an order that names none is the first).
    public static GaugeLayout For(int dpi, GaugeOrder order)
    {
        int effective = dpi > 0 ? dpi : CardPlacement.BaseDpi;
        int width = Round(WidthAt96, effective);
        int height = Round(HeightAt96, effective);
        int ring = Round(RingAt96, effective);
        int number = Round(NumberSlotAt96, effective);
        int bolt = Round(BoltSlotAt96, effective);
        int gap = Round(GapAt96, effective);
        int line = Round(LineAt96, effective);
        int content = ring + number + bolt + (2 * gap);
        int leftPad = Math.Max(0, (width - content) / 2);

        (GaugePiece first, GaugePiece second, GaugePiece third) = GaugeOrders.Sequence(GaugeOrders.FromStored(order));
        int ringX = 0;
        int numberX = 0;
        int boltX = 0;
        int x = leftPad;
        foreach (GaugePiece piece in new[] { first, second, third })
        {
            switch (piece)
            {
                case GaugePiece.Ring:
                    ringX = x;
                    x += ring + gap;
                    break;
                case GaugePiece.Number:
                    numberX = x;
                    x += number + gap;
                    break;
                default:
                    boltX = x;
                    x += bolt + gap;
                    break;
            }
        }

        double scale = effective / (double)CardPlacement.BaseDpi;
        float stroke = (float)(RingStrokeAt96 * scale);
        var ringBox = new Rectangle(ringX, (height - ring) / 2, ring, ring);
        return new GaugeLayout(
            effective, width, height,
            ringBox, (ring / 2f) - (stroke / 2f), stroke,
            ringBox,
            new Rectangle(numberX, (height - line) / 2, number, line),
            new Rectangle(boltX, (height - bolt) / 2, bolt, bolt),
            new Size(bolt, bolt),
            Round(PhoneAt96, effective),
            Round(TypeAt96, effective),
            Round(CornerAt96, effective));
    }

    // The window's own rectangle centred on a taskbar's short axis, at x.
    public Rectangle BoundsAt(int x, Rectangle taskbar) =>
        new(x, taskbar.Top + ((taskbar.Height - Height) / 2), Width, Height);

    // The earbud pair's four shapes (head, stem, head, stem) as rectangles on the ring box, with the corner radius.
    public (RectangleF[] Shapes, float Radius) EarbudShapes()
    {
        float k = Mark.Width / (float)MarkGrid;
        Rectangle mark = Mark;
        RectangleF R(float gx, float gy, float gw, float gh) => new(mark.X + (gx * k), mark.Y + (gy * k), gw * k, gh * k);
        return ([R(6.5f, 6f, 5f, 5f), R(9.5f, 9f, 2f, 8f), R(12.5f, 6f, 5f, 5f), R(12.5f, 9f, 2f, 8f)], 1f * k);
    }
}
