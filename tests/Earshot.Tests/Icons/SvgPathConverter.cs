using System.Globalization;
using System.Text.RegularExpressions;

namespace Earshot.Tests.Icons;

internal readonly record struct PathPoint(double X, double Y);

// One figure of a converted path: its points and, for each point, the GDI+ PathPointType code (Start 0, Line 1, Bezier3 3, with
// 0x80 added to the last point of a figure closed with Z). A cubic takes three points: two controls and its end.
internal sealed class ConvertedSubpath
{
    public List<PathPoint> Points { get; } = [];

    public List<byte> Types { get; } = [];

    public bool Closed => Types.Count > 0 && (Types[^1] & SvgPathConverter.CloseFlag) != 0;
}

internal sealed class ConvertedPath
{
    public List<ConvertedSubpath> Subpaths { get; } = [];
}

// SVG path data to the points and point types GDI+ builds a GraphicsPath from. It lives in the test project: the product carries
// only the converted outlines (src\Earshot\Icon\MingCuteIcons.g.cs), written by MingCuteIconGenerator.
//
// All commands of the path grammar are read: M m L l H h V v C c S s Q q T t A a Z z, a number list after a command repeating
// it (after a moveto as a lineto), numbers packed as the files write them (".7.7", "1-.4"), a relative moveto after an unclosed
// figure counted from that figure's last point and after Z from the figure's start. A quadratic becomes the exact cubic. An
// elliptical arc is converted by the endpoint to centre parameterisation of the SVG 1.1 implementation notes, appendix F.6.5,
// with radii that are too small scaled up as F.6.6 says, then cut into pieces of at most 45 degrees, each one cubic whose handles
// are 4/3 tan(delta / 4) of the radius along the tangent; the last end point is the arc's own.
// https://www.w3.org/TR/SVG11/implnote.html#ArcImplementationNotes
// https://www.w3.org/TR/SVG11/paths.html#PathDataBNF
internal static partial class SvgPathConverter
{
    public const byte StartType = 0;
    public const byte LineType = 1;
    public const byte BezierType = 3;
    public const byte CloseFlag = 0x80;

    // The most an arc turns in one cubic. A cubic with the handles below strays from its circle by at most 2.7e-4 of the radius at
    // 90 degrees, and the error falls with the sixth power of the angle: at 45 degrees it is 4.2e-6 of the radius, under a thousandth
    // of a grid unit for any radius the set uses (the largest, 49, strays 0.0002). 90 degrees would stray 0.0027 at radius 10.
    private const double MaxPieceRadians = Math.PI / 4;

    [GeneratedRegex("<path[^>]*?\\sd=\"(?<d>[^\"]*)\"")]
    private static partial Regex PathElement();

    // The d attribute of the one path an icon file holds. A file with another element or a second path is refused.
    public static string PathData(string svg)
    {
        MatchCollection paths = PathElement().Matches(svg);
        if (paths.Count != 1)
        {
            throw new FormatException("An icon file holds exactly one path; this one holds " + paths.Count + ".");
        }

        string withoutRoot = Regex.Replace(svg, "<svg[^>]*>|</svg>|<path[^>]*/>", string.Empty);
        if (withoutRoot.Trim().Length != 0)
        {
            throw new FormatException("An icon file holds only the svg element and its path; this one holds more: " + withoutRoot.Trim());
        }

        return paths[0].Groups["d"].Value;
    }

    public static ConvertedPath Convert(string d)
    {
        var reader = new Reader(d);
        var result = new ConvertedPath();
        ConvertedSubpath? current = null;
        double cx = 0, cy = 0, sx = 0, sy = 0;
        bool seenMove = false;
        double lastCubicX = 0, lastCubicY = 0, lastQuadX = 0, lastQuadY = 0;
        char previous = ' ';
        char command = ' ';

        void Begin()
        {
            if (current is null)
            {
                current = new ConvertedSubpath();
                result.Subpaths.Add(current);
                current.Points.Add(new PathPoint(cx, cy));
                current.Types.Add(StartType);
            }
        }

        void LineTo(double x, double y)
        {
            Begin();
            current!.Points.Add(new PathPoint(x, y));
            current.Types.Add(LineType);
            cx = x;
            cy = y;
        }

        void CubicTo(double x1, double y1, double x2, double y2, double x, double y)
        {
            Begin();
            current!.Points.Add(new PathPoint(x1, y1));
            current.Points.Add(new PathPoint(x2, y2));
            current.Points.Add(new PathPoint(x, y));
            current.Types.Add(BezierType);
            current.Types.Add(BezierType);
            current.Types.Add(BezierType);
            cx = x;
            cy = y;
        }

        while (true)
        {
            reader.SkipSeparators();
            if (reader.AtEnd)
            {
                break;
            }

            if (reader.AtCommand)
            {
                command = reader.ReadCommand();
                if (!seenMove && command is not ('M' or 'm'))
                {
                    throw new FormatException("A path starts with a moveto.");
                }
            }
            else if (command is ' ' or 'Z' or 'z')
            {
                throw new FormatException("Numbers where a command was expected, at " + reader.Position + ".");
            }

            bool relative = char.IsLower(command);
            double ox = relative ? cx : 0;
            double oy = relative ? cy : 0;
            char kind = char.ToUpperInvariant(command);
            switch (kind)
            {
                case 'M':
                {
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    cx = sx = x;
                    cy = sy = y;
                    current = null;
                    seenMove = true;
                    command = relative ? 'l' : 'L';
                    break;
                }

                case 'L':
                {
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    LineTo(x, y);
                    break;
                }

                case 'H':
                {
                    double x = ox + reader.ReadNumber();
                    LineTo(x, cy);
                    break;
                }

                case 'V':
                {
                    double y = oy + reader.ReadNumber();
                    LineTo(cx, y);
                    break;
                }

                case 'C':
                {
                    double x1 = ox + reader.ReadNumber();
                    double y1 = oy + reader.ReadNumber();
                    double x2 = ox + reader.ReadNumber();
                    double y2 = oy + reader.ReadNumber();
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    CubicTo(x1, y1, x2, y2, x, y);
                    lastCubicX = x2;
                    lastCubicY = y2;
                    break;
                }

                case 'S':
                {
                    // The first control is the last one reflected about the current point, when the last command was a cubic.
                    double x1 = previous is 'C' or 'S' ? (2 * cx) - lastCubicX : cx;
                    double y1 = previous is 'C' or 'S' ? (2 * cy) - lastCubicY : cy;
                    double x2 = ox + reader.ReadNumber();
                    double y2 = oy + reader.ReadNumber();
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    CubicTo(x1, y1, x2, y2, x, y);
                    lastCubicX = x2;
                    lastCubicY = y2;
                    break;
                }

                case 'Q':
                {
                    double qx = ox + reader.ReadNumber();
                    double qy = oy + reader.ReadNumber();
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    QuadraticTo(qx, qy, x, y);
                    lastQuadX = qx;
                    lastQuadY = qy;
                    break;
                }

                case 'T':
                {
                    double qx = previous is 'Q' or 'T' ? (2 * cx) - lastQuadX : cx;
                    double qy = previous is 'Q' or 'T' ? (2 * cy) - lastQuadY : cy;
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    QuadraticTo(qx, qy, x, y);
                    lastQuadX = qx;
                    lastQuadY = qy;
                    break;
                }

                case 'A':
                {
                    double rx = reader.ReadNumber();
                    double ry = reader.ReadNumber();
                    double rotation = reader.ReadNumber();
                    bool largeArc = reader.ReadFlag();
                    bool sweep = reader.ReadFlag();
                    double x = ox + reader.ReadNumber();
                    double y = oy + reader.ReadNumber();
                    ArcTo(rx, ry, rotation, largeArc, sweep, x, y);
                    break;
                }

                case 'Z':
                {
                    if (current is not null)
                    {
                        current.Types[^1] |= CloseFlag;
                    }

                    current = null;
                    cx = sx;
                    cy = sy;
                    break;
                }

                default:
                    throw new FormatException("Unknown path command '" + command + "' at " + reader.Position + ".");
            }

            previous = kind;
        }

        return result;

        void QuadraticTo(double qx, double qy, double x, double y)
        {
            // The cubic that traces the same curve: each control two thirds of the way from an end to the quadratic's control.
            CubicTo(
                cx + (2.0 / 3.0 * (qx - cx)), cy + (2.0 / 3.0 * (qy - cy)),
                x + (2.0 / 3.0 * (qx - x)), y + (2.0 / 3.0 * (qy - y)),
                x, y);
        }

        void ArcTo(double rx, double ry, double rotationDegrees, bool largeArc, bool sweep, double x, double y)
        {
            double x1 = cx;
            double y1 = cy;
            if (x1 == x && y1 == y)
            {
                return;
            }

            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx == 0 || ry == 0)
            {
                LineTo(x, y);
                return;
            }

            double phi = rotationDegrees * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi);
            double sinPhi = Math.Sin(phi);

            // F.6.5 step 1: the start point in the ellipse's own axes, centred on the chord's middle.
            double dx = (x1 - x) / 2;
            double dy = (y1 - y) / 2;
            double x1p = (cosPhi * dx) + (sinPhi * dy);
            double y1p = (-sinPhi * dx) + (cosPhi * dy);

            // F.6.6: radii too small to span the chord are scaled up until the ellipse just touches both ends.
            double lambda = (x1p * x1p / (rx * rx)) + (y1p * y1p / (ry * ry));
            if (lambda > 1)
            {
                double scale = Math.Sqrt(lambda);
                rx *= scale;
                ry *= scale;
            }

            // F.6.5 step 2: the centre in the ellipse's axes; the sign picks one of the two ellipses by the large-arc and sweep flags.
            double rx2 = rx * rx;
            double ry2 = ry * ry;
            double numerator = (rx2 * ry2) - (rx2 * y1p * y1p) - (ry2 * x1p * x1p);
            double denominator = (rx2 * y1p * y1p) + (ry2 * x1p * x1p);
            double coefficient = denominator == 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
            if (largeArc == sweep)
            {
                coefficient = -coefficient;
            }

            double cxp = coefficient * (rx * y1p / ry);
            double cyp = coefficient * (-ry * x1p / rx);

            // Step 3: the centre in user space.
            double centreX = (cosPhi * cxp) - (sinPhi * cyp) + ((x1 + x) / 2);
            double centreY = (sinPhi * cxp) + (cosPhi * cyp) + ((y1 + y) / 2);

            // Step 4: the start angle and the sweep.
            double ux = (x1p - cxp) / rx;
            double uy = (y1p - cyp) / ry;
            double vx = (-x1p - cxp) / rx;
            double vy = (-y1p - cyp) / ry;
            double theta1 = Math.Atan2(uy, ux);
            double delta = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
            if (!sweep && delta > 0)
            {
                delta -= 2 * Math.PI;
            }
            else if (sweep && delta < 0)
            {
                delta += 2 * Math.PI;
            }

            int pieces = Math.Max(1, (int)Math.Ceiling((Math.Abs(delta) / MaxPieceRadians) - 1e-9));
            double step = delta / pieces;
            double handle = 4.0 / 3.0 * Math.Tan(step / 4);

            PathPoint Point(double t) => new(
                centreX + (rx * cosPhi * Math.Cos(t)) - (ry * sinPhi * Math.Sin(t)),
                centreY + (rx * sinPhi * Math.Cos(t)) + (ry * cosPhi * Math.Sin(t)));
            PathPoint Tangent(double t) => new(
                (-rx * cosPhi * Math.Sin(t)) - (ry * sinPhi * Math.Cos(t)),
                (-rx * sinPhi * Math.Sin(t)) + (ry * cosPhi * Math.Cos(t)));

            for (int i = 0; i < pieces; i++)
            {
                double t1 = theta1 + (i * step);
                double t2 = theta1 + ((i + 1) * step);
                PathPoint p1 = Point(t1);
                PathPoint p2 = Point(t2);
                PathPoint d1 = Tangent(t1);
                PathPoint d2 = Tangent(t2);
                PathPoint end = i == pieces - 1 ? new PathPoint(x, y) : p2;
                CubicTo(p1.X + (handle * d1.X), p1.Y + (handle * d1.Y), p2.X - (handle * d2.X), p2.Y - (handle * d2.Y), end.X, end.Y);
            }
        }
    }

    // The extremes of the drawn outline: every point of a line, and a cubic sampled finely enough that the error is far below
    // a thousandth of a unit. (Not the control points, which lie outside a curve.)
    public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IEnumerable<ConvertedSubpath> subpaths)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (ConvertedSubpath sub in subpaths)
        {
            PathPoint last = sub.Points[0];
            Add(last.X, last.Y);
            for (int i = 1; i < sub.Points.Count;)
            {
                if ((sub.Types[i] & ~CloseFlag) == LineType)
                {
                    Add(sub.Points[i].X, sub.Points[i].Y);
                    last = sub.Points[i];
                    i++;
                }
                else
                {
                    PathPoint c1 = sub.Points[i];
                    PathPoint c2 = sub.Points[i + 1];
                    PathPoint end = sub.Points[i + 2];
                    for (int s = 0; s <= 1000; s++)
                    {
                        double t = s / 1000.0;
                        double u = 1 - t;
                        Add(
                            (u * u * u * last.X) + (3 * u * u * t * c1.X) + (3 * u * t * t * c2.X) + (t * t * t * end.X),
                            (u * u * u * last.Y) + (3 * u * u * t * c1.Y) + (3 * u * t * t * c2.Y) + (t * t * t * end.Y));
                    }

                    last = end;
                    i += 3;
                }
            }
        }

        return (minX, minY, maxX, maxY);
    }

    // The path grammar's tokens. A number is read as the grammar says: a sign, digits with a point, an exponent; a second point
    // or a sign ends it, which is how ".7.7" and "1-.4" are two numbers. A flag of an arc is one character, 0 or 1.
    private sealed class Reader(string text)
    {
        private int _index;

        public int Position => _index;

        public bool AtEnd => _index >= text.Length;

        public bool AtCommand => !AtEnd && "MmLlHhVvCcSsQqTtAaZz".Contains(text[_index], StringComparison.Ordinal);

        public void SkipSeparators()
        {
            while (!AtEnd && (char.IsWhiteSpace(text[_index]) || text[_index] == ','))
            {
                _index++;
            }
        }

        public char ReadCommand() => text[_index++];

        public bool ReadFlag()
        {
            SkipSeparators();
            if (AtEnd || text[_index] is not ('0' or '1'))
            {
                throw new FormatException("An arc flag is 0 or 1, at " + _index + ".");
            }

            return text[_index++] == '1';
        }

        public double ReadNumber()
        {
            SkipSeparators();
            int start = _index;
            if (!AtEnd && text[_index] is '+' or '-')
            {
                _index++;
            }

            int digits = 0;
            while (!AtEnd && char.IsAsciiDigit(text[_index]))
            {
                _index++;
                digits++;
            }

            if (!AtEnd && text[_index] == '.')
            {
                _index++;
                while (!AtEnd && char.IsAsciiDigit(text[_index]))
                {
                    _index++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                throw new FormatException("A number was expected at " + start + ".");
            }

            if (!AtEnd && text[_index] is 'e' or 'E')
            {
                int exponentStart = _index;
                _index++;
                if (!AtEnd && text[_index] is '+' or '-')
                {
                    _index++;
                }

                int exponentDigits = 0;
                while (!AtEnd && char.IsAsciiDigit(text[_index]))
                {
                    _index++;
                    exponentDigits++;
                }

                if (exponentDigits == 0)
                {
                    _index = exponentStart;
                }
            }

            return double.Parse(text.AsSpan(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
