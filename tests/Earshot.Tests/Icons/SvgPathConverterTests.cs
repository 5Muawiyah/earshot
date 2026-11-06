using System.Globalization;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Icons;

// The converter that turns an icon file's path data into GDI+ points. The other half of its proof is MingCuteIconGeneratorTests
// (the committed file equals what it writes) and IconRasterTests (what is drawn from that file); this half proves the converter
// against values worked out by hand and against the ellipse an arc is meant to follow, because the raster tests draw the
// converter's own output and so cannot catch a converter that is wrong the same way everywhere.
[TestClass]
public sealed class SvgPathConverterTests
{
    private const byte S = SvgPathConverter.StartType;
    private const byte L = SvgPathConverter.LineType;
    private const byte B = SvgPathConverter.BezierType;
    private const byte Z = SvgPathConverter.CloseFlag;

    private static ConvertedSubpath Only(string d)
    {
        ConvertedPath path = SvgPathConverter.Convert(d);
        Assert.HasCount(1, path.Subpaths, "figures in " + d);
        return path.Subpaths[0];
    }

    private static void AssertFigure(ConvertedSubpath figure, byte[] types, double[] xy, double tolerance = 1e-9)
    {
        CollectionAssert.AreEqual(types, figure.Types.ToArray(), "types");
        Assert.HasCount(xy.Length / 2, figure.Points, "points");
        for (int i = 0; i < figure.Points.Count; i++)
        {
            Assert.AreEqual(xy[2 * i], figure.Points[i].X, tolerance, "x of point " + i);
            Assert.AreEqual(xy[(2 * i) + 1], figure.Points[i].Y, tolerance, "y of point " + i);
        }
    }

    // ---- Each command, worked by hand

    [TestMethod]
    public void AbsoluteLineHorizontalAndVerticalCommandsThenCloseGiveAClosedFigure()
    {
        AssertFigure(Only("M10 20L30 40H50V60Z"), [S, L, L, (byte)(L | Z)], [10, 20, 30, 40, 50, 40, 50, 60]);
    }

    [TestMethod]
    public void RelativeLineHorizontalAndVerticalCommandsCountFromTheCurrentPoint()
    {
        AssertFigure(Only("m10 20l5 5h5v5z"), [S, L, L, (byte)(L | Z)], [10, 20, 15, 25, 20, 25, 20, 30]);
    }

    [TestMethod]
    public void NumbersAfterACommandRepeatItAndAfterAMovetoTheyAreLines()
    {
        AssertFigure(Only("M0 0 10 10 20 0"), [S, L, L], [0, 0, 10, 10, 20, 0]);
        AssertFigure(Only("m0 0 10 10 10-10"), [S, L, L], [0, 0, 10, 10, 20, 0]);
        AssertFigure(Only("M0 0L1 1 2 2 3 3"), [S, L, L, L], [0, 0, 1, 1, 2, 2, 3, 3]);
        AssertFigure(Only("M0 0c1 1 2 2 3 3 1 1 2 2 3 3"), [S, B, B, B, B, B, B], [0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6]);
    }

    [TestMethod]
    public void CubicSmoothCubicQuadraticAndSmoothQuadraticCommandsFollowTheirDefinitions()
    {
        AssertFigure(Only("M0 0C0 10 10 10 10 0"), [S, B, B, B], [0, 0, 0, 10, 10, 10, 10, 0]);

        // S after a cubic: the first control is the last one reflected about the current point, (10,10) about (10,0).
        AssertFigure(Only("M0 0C0 10 10 10 10 0S20-10 20 0"), [S, B, B, B, B, B, B], [0, 0, 0, 10, 10, 10, 10, 0, 10, -10, 20, -10, 20, 0]);

        // S after anything else: the first control is the current point.
        AssertFigure(Only("M0 0S5 5 10 0"), [S, B, B, B], [0, 0, 0, 0, 5, 5, 10, 0]);

        // A quadratic is the cubic with its controls two thirds along: (2,4) and (4,4).
        AssertFigure(Only("M0 0Q3 6 6 0"), [S, B, B, B], [0, 0, 2, 4, 4, 4, 6, 0]);

        // T after a quadratic reflects (3,6) about (6,0) to (9,-6): controls (8,-4) and (10,-4).
        AssertFigure(Only("M0 0Q3 6 6 0T12 0"), [S, B, B, B, B, B, B], [0, 0, 2, 4, 4, 4, 6, 0, 8, -4, 10, -4, 12, 0]);

        // T after anything else: the control is the current point, so the cubic's controls are (0,0) and (2,0).
        AssertFigure(Only("M0 0T6 0"), [S, B, B, B], [0, 0, 0, 0, 2, 0, 6, 0]);
    }

    [TestMethod]
    public void ARelativeMovetoAfterAnUnclosedFigureCountsFromItsLastPointAndAfterZFromItsStart()
    {
        ConvertedPath open = SvgPathConverter.Convert("M10 10L20 10L20 20m5 5L30 30");
        Assert.HasCount(2, open.Subpaths);
        AssertFigure(open.Subpaths[0], [S, L, L], [10, 10, 20, 10, 20, 20]);
        AssertFigure(open.Subpaths[1], [S, L], [25, 25, 30, 30]);

        ConvertedPath closed = SvgPathConverter.Convert("M10 10L20 10L20 20Zm5 5L30 30");
        Assert.HasCount(2, closed.Subpaths);
        AssertFigure(closed.Subpaths[0], [S, L, (byte)(L | Z)], [10, 10, 20, 10, 20, 20]);
        AssertFigure(closed.Subpaths[1], [S, L], [15, 15, 30, 30]);
    }

    [TestMethod]
    public void ADrawingCommandAfterZStartsANewFigureAtTheClosedFiguresStart()
    {
        ConvertedPath path = SvgPathConverter.Convert("M10 10L20 10L20 20ZL30 30");
        Assert.HasCount(2, path.Subpaths);
        AssertFigure(path.Subpaths[1], [S, L], [10, 10, 30, 30]);
    }

    [TestMethod]
    public void PackedNumbersAreSeparatedByASecondPointOrASign()
    {
        AssertFigure(Only("M.7.7L1-.4"), [S, L], [0.7, 0.7, 1, -0.4]);
        AssertFigure(Only("M1.5-2.5l-.5.5"), [S, L], [1.5, -2.5, 1, -2]);
        AssertFigure(Only("M1e1 2E-1L+3 4"), [S, L], [10, 0.2, 3, 4]);
    }

    [TestMethod]
    public void AnArcWithPackedRadiiAndSeparateFlagsEndsExactlyAtItsEndPoint()
    {
        ConvertedSubpath figure = Only("M0 0a.7.7 0 0 0 .187.313");
        PathPoint end = figure.Points[^1];
        Assert.AreEqual(0.187, end.X, 1e-12);
        Assert.AreEqual(0.313, end.Y, 1e-12);
        Assert.IsTrue(figure.Points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)));
    }

    [TestMethod]
    public void ArcFlagsMayBePackedWithTheFollowingNumbers()
    {
        // "a10 10 0 011 1": large 0, sweep 1, end (1,1) packed as the grammar allows.
        ConvertedSubpath packed = Only("M0 0a10 10 0 011 1");
        ConvertedSubpath apart = Only("M0 0a10 10 0 0 1 1 1");
        CollectionAssert.AreEqual(apart.Points, packed.Points);
    }

    [TestMethod]
    public void AZeroRadiusArcIsALineAndAnArcToItsOwnStartIsNothing()
    {
        AssertFigure(Only("M0 0A0 5 0 0 1 10 10"), [S, L], [0, 0, 10, 10]);
        AssertFigure(Only("M3 3L4 4A5 5 0 0 1 4 4"), [S, L], [3, 3, 4, 4]);
    }

    [TestMethod]
    public void MalformedPathDataIsRefused()
    {
        Assert.Throws<FormatException>(() => SvgPathConverter.Convert("L10 10"));
        Assert.Throws<FormatException>(() => SvgPathConverter.Convert("M0 0X5 5"));
        Assert.Throws<FormatException>(() => SvgPathConverter.Convert("M0 0L5"));
        Assert.Throws<FormatException>(() => SvgPathConverter.Convert("M0 0Z5 5"));
        Assert.Throws<FormatException>(() => SvgPathConverter.Convert("M0 0A5 5 0 2 1 9 9"));
    }

    [TestMethod]
    public void AFileHoldsExactlyOnePathAndNothingElse()
    {
        Assert.AreEqual("M0 0L1 1", SvgPathConverter.PathData("<svg xmlns=\"x\" width=\"24\"><path d=\"M0 0L1 1\"/></svg>"));
        Assert.Throws<FormatException>(() => SvgPathConverter.PathData("<svg><path d=\"M0 0\"/><path d=\"M1 1\"/></svg>"));
        Assert.Throws<FormatException>(() => SvgPathConverter.PathData("<svg><g/><path d=\"M0 0\"/></svg>"));
        Assert.Throws<FormatException>(() => SvgPathConverter.PathData("<svg></svg>"));
    }

    // ---- Arcs: every cubic piece lies on the ellipse it was cut from

    // The distance from a point to the ellipse (centre, radii, rotation in degrees), measured the way the ellipse is drawn: the
    // point is taken into the ellipse's own unit circle, pushed out to the circle radially and brought back.
    private static double DistanceToEllipse(PathPoint p, double cx, double cy, double rx, double ry, double degrees)
    {
        double phi = degrees * Math.PI / 180.0;
        double dx = p.X - cx;
        double dy = p.Y - cy;
        double u = ((Math.Cos(phi) * dx) + (Math.Sin(phi) * dy)) / rx;
        double v = ((-Math.Sin(phi) * dx) + (Math.Cos(phi) * dy)) / ry;
        double rho = Math.Sqrt((u * u) + (v * v));
        double onU = u / rho * rx;
        double onV = v / rho * ry;
        double qx = cx + (Math.Cos(phi) * onU) - (Math.Sin(phi) * onV);
        double qy = cy + (Math.Sin(phi) * onU) + (Math.Cos(phi) * onV);
        return Math.Sqrt(((p.X - qx) * (p.X - qx)) + ((p.Y - qy) * (p.Y - qy)));
    }

    // The most any of 65 points of any cubic piece of the path strays from the ellipse, and how many pieces there are.
    private static (double Worst, int Pieces) Worst(string d, double cx, double cy, double rx, double ry, double degrees)
    {
        ConvertedSubpath figure = Only(d);
        double worst = 0;
        int pieces = 0;
        PathPoint start = figure.Points[0];
        for (int i = 1; i + 2 < figure.Points.Count; i += 3)
        {
            Assert.AreEqual(SvgPathConverter.BezierType, figure.Types[i] & ~SvgPathConverter.CloseFlag, "an arc is cubic pieces only");
            PathPoint c1 = figure.Points[i];
            PathPoint c2 = figure.Points[i + 1];
            PathPoint end = figure.Points[i + 2];
            for (int s = 0; s <= 64; s++)
            {
                double t = s / 64.0;
                double a = 1 - t;
                var p = new PathPoint(
                    (a * a * a * start.X) + (3 * a * a * t * c1.X) + (3 * a * t * t * c2.X) + (t * t * t * end.X),
                    (a * a * a * start.Y) + (3 * a * a * t * c1.Y) + (3 * a * t * t * c2.Y) + (t * t * t * end.Y));
                worst = Math.Max(worst, DistanceToEllipse(p, cx, cy, rx, ry, degrees));
            }

            start = end;
            pieces++;
        }

        return (worst, pieces);
    }

    // Two circles of radius 10 pass through (10,0) and (0,10): one centred on (0,0), one on (10,10). The flags choose which and
    // which way round. (large, sweep) = (0,1) and (1,0) are the circle on (0,0): a quarter turn forward, three quarters back.
    // (0,0) and (1,1) are the circle on (10,10): a quarter turn back, three quarters forward.
    [TestMethod]
    [DataRow(false, true, 0.0, 0.0, 2)]
    [DataRow(true, false, 0.0, 0.0, 6)]
    [DataRow(false, false, 10.0, 10.0, 2)]
    [DataRow(true, true, 10.0, 10.0, 6)]
    public void EveryFlagCombinationFollowsTheCircleItNames(bool large, bool sweep, double cx, double cy, int pieces)
    {
        string d = "M10 0A10 10 0 " + (large ? "1" : "0") + " " + (sweep ? "1" : "0") + " 0 10";
        (double worst, int count) = Worst(d, cx, cy, 10, 10, 0);
        Assert.AreEqual(pieces, count, "pieces of " + d);
        Assert.IsLessThan(0.001, worst, d + " strays " + worst);
        PathPoint end = Only(d).Points[^1];
        Assert.AreEqual(0, end.X, 1e-12);
        Assert.AreEqual(10, end.Y, 1e-12);
    }

    [TestMethod]
    public void AnEllipseFollowsItsAxes()
    {
        (double worst, int count) = Worst("M10 0A10 5 0 0 1 0 5", 0, 0, 10, 5, 0);
        Assert.AreEqual(2, count);
        Assert.IsLessThan(0.001, worst);
    }

    [TestMethod]
    public void ARotatedEllipseFollowsItsAxes()
    {
        // The ellipse centred on (3,4), radii 10 and 5, turned 30 degrees; the arc runs from parameter 20 to 110 degrees.
        const double phi = 30.0;
        PathPoint At(double degrees)
        {
            double t = degrees * Math.PI / 180.0;
            double p = phi * Math.PI / 180.0;
            return new PathPoint(
                3 + (10 * Math.Cos(p) * Math.Cos(t)) - (5 * Math.Sin(p) * Math.Sin(t)),
                4 + (10 * Math.Sin(p) * Math.Cos(t)) + (5 * Math.Cos(p) * Math.Sin(t)));
        }

        PathPoint a = At(20);
        PathPoint b = At(110);
        string d = string.Create(CultureInfo.InvariantCulture, $"M{a.X:R} {a.Y:R}A10 5 30 0 1 {b.X:R} {b.Y:R}");
        (double worst, int count) = Worst(d, 3, 4, 10, 5, phi);
        Assert.AreEqual(2, count);
        Assert.IsLessThan(0.001, worst, d + " strays " + worst);

        // The long way round the same ellipse (parameter 110 to 20 forward, 270 degrees) is six pieces.
        string longWay = string.Create(CultureInfo.InvariantCulture, $"M{b.X:R} {b.Y:R}A10 5 30 1 1 {a.X:R} {a.Y:R}");
        (double worstLong, int countLong) = Worst(longWay, 3, 4, 10, 5, phi);
        Assert.AreEqual(6, countLong);
        Assert.IsLessThan(0.001, worstLong, longWay + " strays " + worstLong);
    }

    [TestMethod]
    public void RadiiTooSmallForTheChordAreScaledUpUntilTheEllipseTouchesBothEnds()
    {
        // Radius 1 cannot span 10 units: F.6.6 makes it 5, a semicircle about (5,0), forward from (0,0) over the top (y up the page is
        // negative) to (10,0): four pieces of 45 degrees.
        (double worst, int count) = Worst("M0 0A1 1 0 0 1 10 0", 5, 0, 5, 5, 0);
        Assert.AreEqual(4, count);
        Assert.IsLessThan(0.001, worst);

        // The large-arc flag changes nothing once the radii are scaled: the same semicircle.
        (double worstLarge, int countLarge) = Worst("M0 0A1 1 0 1 1 10 0", 5, 0, 5, 5, 0);
        Assert.AreEqual(4, countLarge);
        Assert.IsLessThan(0.001, worstLarge);

        // An ellipse scaled as one: radii 3 and 1 over a chord of 20 need 10 and 10/3, the same ratio, about the middle (10,0).
        ConvertedSubpath figure = Only("M0 0A3 1 0 0 1 20 0");
        (double w, _) = Worst("M0 0A3 1 0 0 1 20 0", 10, 0, 10, 10.0 / 3.0, 0);
        Assert.IsLessThan(0.001, w, "radii 3:1 scaled to 10:3.33 about (10,0)");
        Assert.AreEqual(20, figure.Points[^1].X, 1e-12);
    }

    [TestMethod]
    public void TwoArcsMakeAFullCircle()
    {
        const string d = "M10 0A10 10 0 1 1-10 0A10 10 0 1 1 10 0";
        ConvertedSubpath figure = Only(d);
        Assert.AreEqual(1 + (3 * 8), figure.Points.Count, "start and eight pieces of 45 degrees");
        double worst = 0;
        PathPoint start = figure.Points[0];
        for (int i = 1; i + 2 < figure.Points.Count; i += 3)
        {
            for (int s = 0; s <= 64; s++)
            {
                double t = s / 64.0;
                double a = 1 - t;
                var p = new PathPoint(
                    (a * a * a * start.X) + (3 * a * a * t * figure.Points[i].X) + (3 * a * t * t * figure.Points[i + 1].X) + (t * t * t * figure.Points[i + 2].X),
                    (a * a * a * start.Y) + (3 * a * a * t * figure.Points[i].Y) + (3 * a * t * t * figure.Points[i + 1].Y) + (t * t * t * figure.Points[i + 2].Y));
                worst = Math.Max(worst, DistanceToEllipse(p, 0, 0, 10, 10, 0));
            }

            start = figure.Points[i + 2];
        }

        Assert.IsLessThan(0.001, worst);
        Assert.AreEqual(10, figure.Points[^1].X, 1e-12);
        Assert.AreEqual(0, figure.Points[^1].Y, 1e-12);
    }

    // ---- The files

    // Each vendored file's figure count and bounds in grid units, from the survey of the files made before the converter was
    // written (an independent measurement, to three decimals). Order of the file names is the manifest's.
    private static readonly (string Name, int Figures, double MinX, double MinY, double MaxX, double MaxY)[] Surveyed =
    [
        ("filled/communication/send", 1, 2.38, 3.517, 20.483, 21.621),
        ("filled/design/layout_bottom", 2, 3.0, 3.0, 21.0, 21.0),
        ("filled/devices/airpods_2", 2, 2.0, 4.0, 22.0, 20.0),
        ("filled/devices/cellphone", 2, 5.0, 2.0, 19.0, 22.0),
        ("filled/devices/computer", 2, 3.0, 3.0, 21.0, 21.0),
        ("filled/devices/mouse", 2, 5.0, 1.991, 19.0, 22.0),
        ("filled/media/airpods", 2, 2.004, 3.998, 21.999, 20.013),
        ("filled/media/announcement", 3, 2.002, 2.381, 21.995, 20.993),
        ("filled/media/headphone", 1, 2.002, 2.0, 21.998, 20.482),
        ("filled/media/mic_off", 3, 3.222, 2.011, 20.776, 21.99),
        ("filled/media/notification", 2, 3.074, 2.001, 20.929, 20.987),
        ("filled/media/pause", 2, 6.0, 3.0, 18.0, 21.0),
        ("filled/system/lock", 3, 3.0, 2.014, 21.0, 22.0),
        ("filled/system/power", 2, 1.507, 1.499, 22.497, 22.503),
        ("filled/system/refresh_2", 2, 1.498, 1.502, 22.503, 22.499),
        ("filled/transportation/rocket", 3, 2.47, 2.733, 21.31, 21.505),
        ("filled/weather/lightning", 1, 4.018, 2.0, 19.98, 21.971),
        ("regular/arrow/arrow_left", 1, 3.344, 5.346, 21.0, 18.655),
        ("regular/arrow/down", 1, 5.343, 8.355, 18.655, 16.0),
        ("regular/arrow/left", 1, 8.0, 5.343, 15.645, 18.655),
        ("regular/arrow/right", 1, 8.355, 5.343, 16.0, 18.655),
        ("regular/arrow/transfer", 2, 3.0, 4.001, 21.0, 20.0),
        ("regular/arrow/up", 1, 5.343, 8.0, 18.655, 15.645),
        ("regular/communication/send", 2, 2.386, 3.513, 20.478, 21.618),
        ("regular/design/layout_bottom", 3, 3.0, 3.0, 21.0, 21.0),
        ("regular/devices/airpods_2", 3, 2.0, 4.0, 22.0, 20.0),
        ("regular/devices/battery_1", 3, 2.0, 6.0, 21.0, 18.0),
        ("regular/devices/bluetooth", 3, 5.004, 2.0, 18.999, 22.0),
        ("regular/devices/bluetooth_off", 4, 3.227, 2.0, 20.767, 22.001),
        ("regular/devices/bluetooth_searching", 5, 3.004, 2.0, 21.988, 22.0),
        ("regular/devices/cellphone", 3, 5.0, 2.0, 19.0, 22.0),
        ("regular/devices/computer", 3, 3.0, 3.0, 21.0, 21.0),
        ("regular/devices/device", 3, 2.0, 4.0, 22.0, 21.0),
        ("regular/devices/keyboard", 9, 2.0, 4.0, 22.0, 20.0),
        ("regular/devices/monitor", 2, 2.0, 4.199, 22.0, 20.983),
        ("regular/devices/mouse", 3, 5.0, 1.984, 19.0, 22.017),
        ("regular/files/copy", 5, 3.0, 2.0, 21.0, 22.0),
        ("regular/files/download_2", 2, 3.0, 2.0, 21.0, 22.0),
        ("regular/files/external_link", 2, 3.0, 3.0, 21.0, 21.0),
        ("regular/media/airpods", 4, 2.001, 4.005, 21.999, 20.002),
        ("regular/media/announcement", 5, 2.0, 2.368, 21.989, 20.992),
        ("regular/media/headphone", 3, 1.994, 1.996, 22.0, 20.51),
        ("regular/media/mic_off", 4, 3.232, 2.004, 20.778, 21.988),
        ("regular/media/notification", 3, 3.067, 2.001, 20.934, 20.991),
        ("regular/media/pause", 2, 7.0, 4.0, 17.0, 20.0),
        ("regular/media/play_circle", 4, 2.011, 2.004, 21.989, 21.997),
        ("regular/media/volume", 4, 2.0, 3.003, 21.993, 20.993),
        ("regular/system/add", 1, 4.0, 4.0, 20.0, 20.0),
        ("regular/system/alert", 4, 1.877, 2.401, 22.126, 20.353),
        ("regular/system/check", 1, 2.517, 5.179, 21.478, 18.486),
        ("regular/system/check_circle", 3, 1.998, 2.008, 22.0, 21.993),
        ("regular/system/close", 1, 4.643, 4.644, 19.357, 19.358),
        ("regular/system/exit", 2, 4.0, 3.0, 20.828, 21.0),
        ("regular/system/history", 2, 1.997, 2.003, 22.001, 22.0),
        ("regular/system/information", 4, 2.011, 2.004, 21.989, 21.997),
        ("regular/system/lock", 4, 3.0, 2.0, 21.0, 22.0),
        ("regular/system/minimize", 1, 4.0, 10.999, 20.0, 13.0),
        ("regular/system/more_1", 3, 4.493, 10.487, 19.507, 13.513),
        ("regular/system/power", 2, 2.002, 2.0, 22.006, 22.001),
        ("regular/system/refresh_1", 1, 1.991, 1.999, 21.996, 21.995),
        ("regular/system/refresh_2", 2, 1.999, 2.013, 22.001, 21.987),
        ("regular/system/safe_shield", 4, 3.0, 2.072, 21.0, 21.956),
        ("regular/system/safe_shield_2", 3, 3.0, 2.059, 21.0, 21.956),
        ("regular/system/settings_3", 4, 1.996, 2.518, 22.002, 21.485),
        ("regular/system/time", 3, 2.011, 2.004, 21.989, 21.997),
        ("regular/system/tool", 2, 2.407, 2.387, 21.813, 21.795),
        ("regular/system/warning", 4, 2.007, 2.001, 21.993, 22.0),
        ("regular/transportation/rocket", 4, 2.484, 2.733, 21.312, 21.493),
        ("regular/weather/lightning", 2, 4.014, 2.0, 19.988, 21.994),
    ];

    private static string ReadSvg(string name) =>
        File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "third_party", "MingCute", "svg", "core", name.Replace('/', Path.DirectorySeparatorChar) + ".svg"));

    [TestMethod]
    public void EveryVendoredFileHasTheFigureCountAndBoundsOfTheSurvey()
    {
        Assert.HasCount(69, Surveyed);
        var wrong = new List<string>();
        foreach ((string name, int figures, double minX, double minY, double maxX, double maxY) in Surveyed)
        {
            ConvertedPath path = SvgPathConverter.Convert(SvgPathConverter.PathData(ReadSvg(name)));
            (double x0, double y0, double x1, double y1) = SvgPathConverter.Bounds(path.Subpaths);
            if (path.Subpaths.Count != figures)
            {
                wrong.Add(name + ": " + path.Subpaths.Count + " figures, surveyed " + figures);
            }

            if (Math.Abs(x0 - minX) > 0.01 || Math.Abs(y0 - minY) > 0.01 || Math.Abs(x1 - maxX) > 0.01 || Math.Abs(y1 - maxY) > 0.01)
            {
                wrong.Add(name + ": [" + x0.ToString("0.###", CultureInfo.InvariantCulture) + "," + y0.ToString("0.###", CultureInfo.InvariantCulture) + ".." + x1.ToString("0.###", CultureInfo.InvariantCulture) + "," + y1.ToString("0.###", CultureInfo.InvariantCulture) + "], surveyed [" + minX + "," + minY + ".." + maxX + "," + maxY + "]");
            }
        }

        Assert.IsEmpty(wrong, string.Join("\n", wrong));
    }

    // The set winds each hole against its outline, so GDI+ filling with the winding rule (what the product uses) leaves the same
    // holes as filling with the alternate rule, and an SVG without a fill-rule (nonzero) shows the same icon. Checked by drawing
    // each file's converted path both ways and comparing every sample.
    [TestMethod]
    public void EveryFileFillsTheSameWithTheWindingAndTheAlternateRule()
    {
        var differ = new List<string>();
        foreach ((string name, _, _, _, _, _) in Surveyed)
        {
            ConvertedPath path = SvgPathConverter.Convert(SvgPathConverter.PathData(ReadSvg(name)));
            var points = new List<System.Drawing.PointF>();
            var types = new List<byte>();
            foreach (ConvertedSubpath figure in path.Subpaths)
            {
                points.AddRange(figure.Points.Select(p => new System.Drawing.PointF((float)p.X * 8f, (float)p.Y * 8f)));
                types.AddRange(figure.Types);
            }

            byte[] winding = Fill(points.ToArray(), types.ToArray(), System.Drawing.Drawing2D.FillMode.Winding);
            byte[] alternate = Fill(points.ToArray(), types.ToArray(), System.Drawing.Drawing2D.FillMode.Alternate);
            int unequal = winding.Where((v, i) => v != alternate[i]).Count();
            if (unequal != 0)
            {
                differ.Add(name + ": " + unequal + " samples differ");
            }
        }

        Assert.IsEmpty(differ, string.Join(Environment.NewLine, differ));
    }

    private static byte[] Fill(System.Drawing.PointF[] points, byte[] types, System.Drawing.Drawing2D.FillMode mode)
    {
        const int Side = 24 * 8;
        using var bitmap = new System.Drawing.Bitmap(Side, Side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bitmap))
        using (var path = new System.Drawing.Drawing2D.GraphicsPath(points, types, mode))
        using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.FillPath(brush, path);
        }

        var result = new byte[Side * Side];
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                result[(y * Side) + x] = bitmap.GetPixel(x, y).A;
            }
        }

        return result;
    }
}
