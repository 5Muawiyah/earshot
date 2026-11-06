using System.Drawing;
using System.Drawing.Drawing2D;

namespace Earshot.Icons;

// One icon's shape: figures of straight lines and cubic curves in the icon set's 24 unit grid, as the points and the GDI+
// point type of each point (Start 0, Line 1, Bezier3 3, with 0x80 on the last point of a closed figure). The data is constant
// (MingCuteIcons.g.cs holds it as static read-only spans), so reading an outline loads nothing and allocates nothing; a path is
// built from it only when a mask is rasterised. Filled with the winding rule: the set winds every hole against its outline.
// https://learn.microsoft.com/en-us/dotnet/api/system.drawing.drawing2d.graphicspath.-ctor
internal readonly ref struct IconOutline(string name, ReadOnlySpan<float> points, ReadOnlySpan<byte> types)
{
    // The 24 unit grid the data is laid out on.
    public const int Grid = 24;

    // Where the set draws its art: units 2 to 22 of the grid.
    public const float ContentStart = 2f;
    public const float ContentSize = 20f;

    // The file the outline came from, such as "regular/system/close", and ":left" or ":right" for a bud of the AirPods pair.
    public string Name { get; } = name;

    // x, y, x, y ...
    public ReadOnlySpan<float> Points { get; } = points;

    public ReadOnlySpan<byte> Types { get; } = types;

    public int PointCount => Types.Length;

    // The outline as a path, each point moved by (x * scale + offsetX, y * scale + offsetY) and then turned by the angle
    // (degrees, clockwise on screen) about (centreX, centreY) when it is not zero.
    public GraphicsPath ToPath(float scale, float offsetX, float offsetY, float degrees = 0f, float centreX = 0f, float centreY = 0f)
    {
        var points = new PointF[PointCount];
        double radians = degrees * Math.PI / 180.0;
        float cos = (float)Math.Cos(radians);
        float sin = (float)Math.Sin(radians);
        for (int i = 0; i < points.Length; i++)
        {
            float x = (Points[2 * i] * scale) + offsetX;
            float y = (Points[(2 * i) + 1] * scale) + offsetY;
            if (degrees != 0f)
            {
                float dx = x - centreX;
                float dy = y - centreY;
                x = centreX + (dx * cos) - (dy * sin);
                y = centreY + (dx * sin) + (dy * cos);
            }

            points[i] = new PointF(x, y);
        }

        return new GraphicsPath(points, Types.ToArray(), FillMode.Winding);
    }

    // The bounds of the drawn outline in grid units: the points on it, not the controls of its curves (which can lie outside it).
    // Each cubic is sampled 64 times, which is within a ten-thousandth of a unit for the curves of the set.
    public RectangleF Bounds()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Add(float x, float y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        float lastX = 0f, lastY = 0f;
        for (int i = 0; i < Types.Length;)
        {
            int kind = Types[i] & 0x07;
            float x = Points[2 * i];
            float y = Points[(2 * i) + 1];
            if (kind == 3)
            {
                float x1 = x, y1 = y, x2 = Points[(2 * i) + 2], y2 = Points[(2 * i) + 3], x3 = Points[(2 * i) + 4], y3 = Points[(2 * i) + 5];
                for (int s = 1; s <= 64; s++)
                {
                    float t = s / 64f;
                    float u = 1f - t;
                    Add(
                        (u * u * u * lastX) + (3f * u * u * t * x1) + (3f * u * t * t * x2) + (t * t * t * x3),
                        (u * u * u * lastY) + (3f * u * u * t * y1) + (3f * u * t * t * y2) + (t * t * t * y3));
                }

                lastX = x3;
                lastY = y3;
                i += 3;
            }
            else
            {
                Add(x, y);
                lastX = x;
                lastY = y;
                i++;
            }
        }

        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }
}
