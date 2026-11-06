using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Earshot.Icons;
using Earshot.Tests.TestWindow;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Icons;

// What an icon looks like at each size it is drawn at, against a reference: the same outline filled at 32 x 32 samples a pixel and
// averaged, which is the area the icon covers in the limit. The product measures 8 x 8 samples a pixel. Three numbers compare them,
// and the pass marks were set before the product was written, from a survey of the 144 files of the set at 10 to 32 px:
//
//   MAE / ink      sum |P - R| / sum R         <= 0.12   (8 x 8 sampling worst 0.087; GDI+ smoothing 0.13 to 0.27 at 10 to 24 px;
//                                                         a 1.5x render scaled down 0.44 to 1.0; a half pixel shift 0.46 to 1.30)
//   soft IoU       sum min(P, R) / sum max(P, R) >= 0.90 (8 x 8 worst 0.920; GDI+ smoothing 0.75 to 0.87; soft <= 0.64; aliased <= 0.80)
//   gradient       sum |grad P|^2 / sum |grad R|^2 in 0.80 .. 1.25  (8 x 8: 0.86 to 1.18; soft 0.11 to 0.55; aliased 2.0 to 10.4)
//
// so a blurred, resampled, shifted, aliased or wrong-sized icon fails at least one of them at every size. The reference here is
// separate code from IconRaster, and takes the outline from the catalogue the product draws from; that the outline is the file's
// is MingCuteIconGeneratorTests and SvgPathConverterTests.
[TestClass]
public sealed class IconRasterTests
{
    private static readonly int[] Boxes = [10, 12, 13, 14, 15, 16, 18, 20, 21, 24, 25, 28, 30, 32, 40];

    // The icons drawn on the whole 24 grid (rule B): the sub-page status icons at 20 px and the tray menu's icons at the menu's size.
    private static readonly Icon[] RuleBIcons =
    [
        Icon.CheckCircle, Icon.Alert, Icon.Download, Icon.SafeShield2,
        Icon.SafeShield, Icon.AirPods, Icon.PlayCircle, Icon.Refresh, Icon.Send, Icon.Lock, Icon.Power, Icon.Headphone, Icon.MicOff,
        Icon.Rocket, Icon.Announcement, Icon.LayoutBottom, Icon.Mouse, Icon.AirPodsCase, Icon.Notification, Icon.Battery, Icon.Cellphone,
        Icon.Device, Icon.Settings, Icon.Tool, Icon.Bluetooth, Icon.BluetoothSearching, Icon.Download, Icon.Sync, Icon.Copy, Icon.Exit,
    ];

    private static readonly int[] RuleBBoxes = [16, 20, 24, 25, 30, 32, 40];

    private static IEnumerable<IconStyle> Styles(Icon icon) =>
        IconCatalogue.HasFilled(icon) ? [IconStyle.Line, IconStyle.Filled] : [IconStyle.Line];

    // ---- The reference

    private const int Reference = 32;

    // The outline filled without smoothing into a bitmap 32 times the mask's size and averaged to pixels. The mapping from the
    // grid to the box is the rule's, written out here.
    private static float[] ReferenceCoverage(Icon icon, IconStyle style, int box, IconRule rule)
    {
        int side = box + 2;
        int big = side * Reference;
        float scale = rule == IconRule.A ? box / 20f : box / 24f;
        float origin = rule == IconRule.A ? 2f : 0f;
        IconOutline outline = IconCatalogue.Outline(icon, style);
        float shift = 0f;
        if (icon is Icon.AirPodsLeft or Icon.AirPodsRight)
        {
            RectangleF b = outline.Bounds();
            shift = 12f - (b.X + (b.Width / 2f));
        }

        float offset = (((shift - origin) * scale) + 1f) * Reference;
        var mask = new float[side * side];
        using var bitmap = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        using (GraphicsPath path = outline.ToPath(scale * Reference, offset, offset))
        using (var brush = new SolidBrush(Color.Black))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.FillPath(brush, path);
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, big, big), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[big * 4];
            for (int y = 0; y < big; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                int target = (y / Reference) * side;
                for (int x = 0; x < big; x++)
                {
                    mask[target + (x / Reference)] += row[(x * 4) + 3] / 255f;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] /= Reference * Reference;
        }

        return mask;
    }

    private readonly record struct Marks(double MaeOverInk, double Iou, double Gradient);

    private static Marks Compare(ReadOnlySpan<byte> product, float[] reference, int side)
    {
        double ink = 0, error = 0, inter = 0, union = 0, gradP = 0, gradR = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            float p = product[i] / 255f;
            float r = reference[i];
            ink += r;
            error += Math.Abs(p - r);
            inter += Math.Min(p, r);
            union += Math.Max(p, r);
        }

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = (y * side) + x;
                double px = x + 1 < side ? (product[i + 1] - product[i]) / 255.0 : 0;
                double py = y + 1 < side ? (product[i + side] - product[i]) / 255.0 : 0;
                double rx = x + 1 < side ? reference[i + 1] - reference[i] : 0;
                double ry = y + 1 < side ? reference[i + side] - reference[i] : 0;
                gradP += (px * px) + (py * py);
                gradR += (rx * rx) + (ry * ry);
            }
        }

        return new Marks(error / Math.Max(ink, 1e-9), inter / Math.Max(union, 1e-9), gradP / Math.Max(gradR, 1e-9));
    }

    private static void Check(List<Icon> icons, int[] boxes, IconRule rule, ConcurrentBag<string> failures, ConcurrentBag<double> worst)
    {
        Parallel.ForEach(icons, icon =>
        {
            foreach (IconStyle style in Styles(icon))
            {
                foreach (int box in boxes)
                {
                    Marks m = Compare(IconRaster.Coverage(icon, style, box, rule).Span, ReferenceCoverage(icon, style, box, rule), box + 2);
                    worst.Add(m.MaeOverInk);
                    if (m.MaeOverInk > 0.12 || m.Iou < 0.90 || m.Gradient is < 0.80 or > 1.25)
                    {
                        failures.Add(icon + " " + style + " " + box + "px rule " + rule + ": MAE/ink " + m.MaeOverInk.ToString("0.000") + ", IoU " + m.Iou.ToString("0.000") + ", gradient " + m.Gradient.ToString("0.00"));
                    }
                }
            }
        });
    }

    [TestMethod]
    public void EveryOutlineAtEveryListedBoxMeetsThePassMarksOnTheArtArea()
    {
        var failures = new ConcurrentBag<string>();
        var worst = new ConcurrentBag<double>();
        Check(IconCatalogue.All.ToList(), Boxes, IconRule.A, failures, worst);
        Assert.IsGreaterThan(1000, worst.Count, "Sanity: every icon, style and box was compared.");
        Assert.IsEmpty(failures, failures.Count + " icon renderings miss the pass marks:\n" + string.Join("\n", failures.Order().Take(40)));
    }

    [TestMethod]
    public void TheIconsDrawnOnTheWholeGridMeetThePassMarksAtTheirBoxes()
    {
        var failures = new ConcurrentBag<string>();
        var worst = new ConcurrentBag<double>();
        Check(RuleBIcons.Distinct().ToList(), RuleBBoxes, IconRule.B, failures, worst);
        Assert.IsGreaterThan(200, worst.Count, "Sanity: the icons were compared.");
        Assert.IsEmpty(failures, failures.Count + " icon renderings miss the pass marks:\n" + string.Join("\n", failures.Order().Take(40)));
    }

    // The pass marks catch what they are for: a copy of the reference moved half a pixel, drawn without smoothing at the box's size
    // or scaled down from a render half as large again each fail at least one of them.
    [TestMethod]
    public void TheMarksRejectAShiftedAnAliasedAndAResampledIcon()
    {
        int rejected = 0;
        foreach (Icon icon in new[] { Icon.Settings, Icon.Close, Icon.Computer, Icon.Lightning })
        {
            foreach (int box in new[] { 12, 16, 24 })
            {
                float[] reference = ReferenceCoverage(icon, IconStyle.Line, box, IconRule.A);
                int side = box + 2;

                // Half a pixel to the right.
                var shifted = new byte[reference.Length];
                for (int y = 0; y < side; y++)
                {
                    for (int x = 0; x < side; x++)
                    {
                        float left = x > 0 ? reference[(y * side) + x - 1] : 0f;
                        float here = reference[(y * side) + x];
                        shifted[(y * side) + x] = (byte)Math.Round(((left + here) / 2f) * 255f);
                    }
                }

                // Hard edges: every sample either wholly in or wholly out.
                var aliased = reference.Select(r => (byte)(r >= 0.5f ? 255 : 0)).ToArray();

                foreach (byte[] bad in new[] { shifted, aliased })
                {
                    Marks m = Compare(bad, reference, side);
                    if (m.MaeOverInk > 0.12 || m.Iou < 0.90 || m.Gradient is < 0.80 or > 1.25)
                    {
                        rejected++;
                    }
                }
            }
        }

        Assert.AreEqual(4 * 3 * 2, rejected, "Every shifted and every aliased copy fails a mark.");
    }

    // ---- The mask

    [TestMethod]
    public void TheMaskIsTheBoxAndAnApronAndHoldsInkOnlyWhereTheIconIs()
    {
        ReadOnlyMemory<byte> mask = IconRaster.Coverage(Icon.Close, IconStyle.Line, 20);
        Assert.AreEqual(22 * 22, mask.Length);
        Assert.AreEqual(22, IconRaster.MaskSide(20));
        Assert.IsTrue(mask.Span.ToArray().Any(b => b > 0));
        Assert.IsTrue(mask.Span.ToArray().Any(b => b is > 0 and < 255), "Edges are partly covered.");
        Assert.AreEqual(0, mask.Span[0], "A corner of the box is empty for a cross.");
    }

    [TestMethod]
    public void TheSameRequestGivesTheSameMaskEveryTime()
    {
        byte[] a = IconRaster.Coverage(Icon.Settings, IconStyle.Line, 16).ToArray();
        byte[] b = IconRaster.Coverage(Icon.Settings, IconStyle.Line, 16).ToArray();
        CollectionAssert.AreEqual(a, b);
    }

    [TestMethod]
    public void AFilledIconIsDrawnWhereTheLineOneIsWhenThereIsNoFilledDrawing()
    {
        Assert.IsFalse(IconCatalogue.HasFilled(Icon.Settings));
        CollectionAssert.AreEqual(
            IconRaster.Coverage(Icon.Settings, IconStyle.Line, 16).ToArray(),
            IconRaster.Coverage(Icon.Settings, IconStyle.Filled, 16).ToArray());
        Assert.IsTrue(IconCatalogue.HasFilled(Icon.Computer));
        long line = IconRaster.Coverage(Icon.Computer, IconStyle.Line, 16).ToArray().Sum(b => (long)b);
        long filled = IconRaster.Coverage(Icon.Computer, IconStyle.Filled, 16).ToArray().Sum(b => (long)b);
        Assert.IsGreaterThan(line, filled, "The filled drawing covers more than the line drawing.");
    }

    [TestMethod]
    public void TheSetsArtAreaFillsTheBoxOnRuleAAndKeepsItsMarginOnRuleB()
    {
        // close.svg's art runs 4.5 to 19.5 of the grid. Rule A: units 2 to 22 are the box, so the art sits 2.5/20 of the box in from
        // each edge. Rule B: the whole grid is the box, so 4.5/24.
        static (int First, int Last) Extent(ReadOnlySpan<byte> mask, int side)
        {
            int first = int.MaxValue, last = -1;
            for (int x = 0; x < side; x++)
            {
                for (int y = 0; y < side; y++)
                {
                    if (mask[(y * side) + x] > 127)
                    {
                        first = Math.Min(first, x);
                        last = Math.Max(last, x);
                    }
                }
            }

            return (first - 1, last - 1);
        }

        (int firstA, int lastA) = Extent(IconRaster.Coverage(Icon.Close, IconStyle.Line, 40, IconRule.A).Span, 42);
        (int firstB, int lastB) = Extent(IconRaster.Coverage(Icon.Close, IconStyle.Line, 40, IconRule.B).Span, 42);
        Assert.IsLessThanOrEqualTo(6, firstA, "rule A: the cross starts about 5 px in of 40");
        Assert.IsGreaterThanOrEqualTo(33, lastA, "rule A: and ends about 34 px in of 40");
        Assert.IsGreaterThanOrEqualTo(6, firstB, "rule B: the cross starts about 7.5 px in");
        Assert.IsLessThanOrEqualTo(33, lastB, "rule B: and ends about 32.5 px in");
        Assert.IsLessThan(firstB, firstA);
    }

    [TestMethod]
    public void ABudIsCentredOnItsOwnBoundsInTheBox()
    {
        foreach (Icon bud in new[] { Icon.AirPodsLeft, Icon.AirPodsRight })
        {
            foreach (IconStyle style in new[] { IconStyle.Line, IconStyle.Filled })
            {
                ReadOnlySpan<byte> mask = IconRaster.Coverage(bud, style, 32).Span;
                int first = int.MaxValue, last = -1;
                for (int y = 0; y < 34; y++)
                {
                    for (int x = 0; x < 34; x++)
                    {
                        if (mask[(y * 34) + x] > 127)
                        {
                            first = Math.Min(first, x);
                            last = Math.Max(last, x);
                        }
                    }
                }

                double middle = (first + last + 1) / 2.0;
                Assert.AreEqual(17.0, middle, 0.6, bud + " " + style + ": the bud's middle is the box's middle");
            }
        }
    }

    // ---- The turn

    [TestMethod]
    public void AStepOfTheTurnIsATenthOfARevolutionClockwiseAndTenStepsAreOne()
    {
        byte[] none = IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, 0).ToArray();
        CollectionAssert.AreEqual(none, IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, IconRaster.RotationSteps).ToArray(), "ten steps is a whole turn");
        CollectionAssert.AreEqual(none, IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, BatteryRefreshView.SpinFrames).ToArray(), "the card's spinner has as many frames as a turn has steps");
        CollectionAssert.AreNotEqual(none, IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, BatteryRefreshView.SpinFrames - 1).ToArray(), "and its last frame is not the first");
        CollectionAssert.AreEqual(none, IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, -10).ToArray(), "steps wrap");
        CollectionAssert.AreNotEqual(none, IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, 1).ToArray());

        // Half a turn is the mask turned over about its middle (the box and its apron are symmetric about it).
        byte[] half = IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, 5).ToArray();
        byte[] flipped = none.Reverse().ToArray();
        long difference = half.Zip(flipped, (a, b) => (long)Math.Abs(a - b)).Sum();
        long ink = none.Sum(b => (long)b);
        Assert.IsLessThan(0.03, difference / (double)ink, "half a turn equals the mask turned about its middle");

        // A quarter-turn-like step (2.5 steps does not exist): step 1 differs from step 0 by a turn of 36 degrees, clockwise: the
        // end of the arrow head that was at the top moves right. Checked by the centre of the ink in the top half.
        double Cx(byte[] mask)
        {
            double sum = 0, weight = 0;
            for (int y = 0; y < 13; y++)
            {
                for (int x = 0; x < 26; x++)
                {
                    sum += x * mask[(y * 26) + x];
                    weight += mask[(y * 26) + x];
                }
            }

            return sum / weight;
        }

        byte[] one = IconRaster.Coverage(Icon.Refresh, IconStyle.Line, 24, IconRule.A, 1).ToArray();
        Assert.AreNotEqual(Cx(none), Cx(one), 0.05, "the ink of the top half moves when the icon turns");
    }

    [TestMethod]
    public void TheCacheKeepsAtMostItsLimitOfMasks()
    {
        for (int box = IconRaster.MinBox; box < IconRaster.MinBox + 40; box++)
        {
            foreach (int step in new[] { 0, 3, 6 })
            {
                _ = IconRaster.Coverage(Icon.Check, IconStyle.Line, box, IconRule.A, step);
                _ = IconRaster.Coverage(Icon.Check, IconStyle.Line, box, IconRule.B, step);
            }
        }

        Assert.IsLessThanOrEqualTo(IconRaster.CacheLimit, IconRaster.CachedMasks);
        Assert.Throws<ArgumentOutOfRangeException>(() => IconRaster.Coverage(Icon.Check, IconStyle.Line, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => IconRaster.Coverage(Icon.Check, IconStyle.Line, 257));
    }

    // ---- The catalogue

    [TestMethod]
    public void TheCatalogueDrawsEveryManifestFileOnceAndNothingElse()
    {
        var drawn = new List<string>();
        foreach (Icon icon in IconCatalogue.All)
        {
            drawn.Add(IconCatalogue.Outline(icon, IconStyle.Line).Name);
            if (IconCatalogue.HasFilled(icon))
            {
                drawn.Add(IconCatalogue.Outline(icon, IconStyle.Filled).Name);
            }
        }

        using System.Text.Json.JsonDocument manifest = VendoredFilesTests.Manifest();
        var expected = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString()!["assets/svg/core/".Length..^".svg".Length])
            .ToList();
        expected.AddRange(["regular/media/airpods:left", "regular/media/airpods:right", "filled/media/airpods:left", "filled/media/airpods:right"]);
        CollectionAssert.AreEquivalent(expected, drawn);
        CollectionAssert.AllItemsAreUnique(drawn);
    }

    [TestMethod]
    public void TheOutlinesAreWellFormedGdiPlusPaths()
    {
        foreach (Icon icon in IconCatalogue.All)
        {
            foreach (IconStyle style in Styles(icon))
            {
                IconOutline outline = IconCatalogue.Outline(icon, style);
                Assert.AreEqual(outline.Types.Length * 2, outline.Points.Length, outline.Name);
                Assert.AreEqual(0, outline.Types[0], outline.Name + ": starts a figure");
                int run = 0;
                for (int i = 0; i < outline.Types.Length; i++)
                {
                    int kind = outline.Types[i] & 0x07;
                    Assert.IsTrue(kind is 0 or 1 or 3, outline.Name + ": point type " + outline.Types[i]);
                    if (kind == 3)
                    {
                        run++;
                    }
                    else
                    {
                        Assert.AreEqual(0, run % 3, outline.Name + ": a curve takes three points");
                        run = 0;
                    }
                }

                Assert.AreEqual(0, run % 3, outline.Name + ": a curve takes three points");

                using GraphicsPath path = outline.ToPath(1f, 0f, 0f);
                Assert.AreEqual(outline.PointCount, path.PointCount, outline.Name);
                RectangleF bounds = path.GetBounds();
                Assert.IsGreaterThanOrEqualTo(-1f, bounds.Left, outline.Name);
                Assert.IsLessThanOrEqualTo(25f, bounds.Right, outline.Name);
                Assert.IsGreaterThanOrEqualTo(-1f, bounds.Top, outline.Name);
                Assert.IsLessThanOrEqualTo(25f, bounds.Bottom, outline.Name);
            }
        }
    }

    [TestMethod]
    public void NoIconFontIsNeededByTheCatalogue()
    {
        // The product source for the catalogue and the painter names no font.
        string folder = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot", "Icon");
        foreach (string file in new[] { "IconCatalogue.cs", "IconOutline.cs", "IconRaster.cs", "IconPainter.cs" })
        {
            string text = File.ReadAllText(Path.Combine(folder, file));
            Assert.DoesNotContain("Segoe", text, StringComparison.Ordinal, file);
            Assert.DoesNotContain("new Font(", text, StringComparison.Ordinal, file);
            Assert.DoesNotContain("DrawString", text, StringComparison.Ordinal, file);
        }
    }

    // ---- The painter

    private static Bitmap Canvas(int size, Color background, float dpi = 96f)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(background);
        return bitmap;
    }

    // The mask composited in the ink over the background, per channel as straight alpha over an opaque pixel.
    private static Color Expected(byte coverage, Color ink, Color background)
    {
        double a = coverage * ink.A / 255.0 / 255.0;
        int Mix(int i, int b) => (int)Math.Round((i * a) + (b * (1 - a)));
        return Color.FromArgb(background.A, Mix(ink.R, background.R), Mix(ink.G, background.G), Mix(ink.B, background.B));
    }

    // The painter draws exactly the mask in the ink, whatever the Graphics' own settings or resolution, at a whole-pixel place.
    [TestMethod]
    [DataRow(96f, false)]
    [DataRow(144f, false)]
    [DataRow(192f, true)]
    public void ThePainterCompositesTheMaskInTheInkPixelForPixel(float dpi, bool hostile)
    {
        var background = Color.FromArgb(255, 32, 32, 36);
        var ink = Color.FromArgb(255, 240, 200, 40);
        using Bitmap canvas = Canvas(60, background, dpi);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            if (hostile)
            {
                // Settings a card's Graphics might carry: linear-light blending, a bicubic filter, smoothing, half-pixel offsets.
                g.CompositingQuality = CompositingQuality.GammaCorrected;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }

            IconPainter.Draw(g, Icon.Settings, IconStyle.Line, new Rectangle(10, 12, 40, 36), 16, ink);
            Assert.AreEqual(hostile ? CompositingQuality.GammaCorrected : CompositingQuality.Default, g.CompositingQuality, "the Graphics' state is put back");
        }

        // The box is centred in the 40 x 36 rectangle: x 10 + (40-16)/2 = 22, y 12 + (36-16)/2 = 22.
        ReadOnlySpan<byte> mask = IconRaster.Coverage(Icon.Settings, IconStyle.Line, 16).Span;
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                int mx = x - 22 + 1;
                int my = y - 22 + 1;
                byte coverage = mx is >= 0 and < 18 && my is >= 0 and < 18 ? mask[(my * 18) + mx] : (byte)0;
                Color want = Expected(coverage, ink, background);
                Color got = canvas.GetPixel(x, y);
                Assert.IsTrue(
                    Math.Abs(want.R - got.R) <= 1 && Math.Abs(want.G - got.G) <= 1 && Math.Abs(want.B - got.B) <= 1,
                    "pixel " + x + "," + y + " at " + dpi + " dpi: wanted " + want + ", drawn " + got);
            }
        }
    }

    [TestMethod]
    public void TheInksAlphaIsTheIconsOpacity()
    {
        var background = Color.FromArgb(255, 20, 20, 20);
        var ink = Color.FromArgb(128, 255, 255, 255);
        using Bitmap canvas = Canvas(30, background);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            IconPainter.Draw(g, Icon.Computer, IconStyle.Filled, new Rectangle(5, 5, 20, 20), 20, ink);
        }

        ReadOnlySpan<byte> mask = IconRaster.Coverage(Icon.Computer, IconStyle.Filled, 20).Span;
        int solid = Array.FindIndex(mask.ToArray(), b => b == 255);
        Assert.IsGreaterThanOrEqualTo(0, solid);
        int mx = solid % 22;
        int my = solid / 22;
        Color got = canvas.GetPixel(5 + mx - 1, 5 + my - 1);
        Color want = Expected(255, ink, background);
        Assert.AreEqual((int)want.R, (int)got.R, 1, "a fully covered pixel of half-opaque white is the background lifted halfway");
    }

    [TestMethod]
    public void ThePainterRecordsWhatItDrewAndWhereToATestsLog()
    {
        using Bitmap canvas = Canvas(40, Color.Black);
        using Graphics g = Graphics.FromImage(canvas);
        IconPainter.DrawnIconLog = [];
        try
        {
            IconPainter.Draw(g, Icon.Lightning, IconStyle.Filled, new Rectangle(0, 0, 40, 40), 12, Color.Red, IconRule.A, 3);
            Assert.HasCount(1, IconPainter.DrawnIconLog);
            DrawnIcon logged = IconPainter.DrawnIconLog[0];
            Assert.AreEqual(Icon.Lightning, logged.Icon);
            Assert.AreEqual(IconStyle.Filled, logged.Style);
            Assert.AreEqual(12, logged.BoxPx);
            Assert.AreEqual(Color.Red.ToArgb(), logged.Ink.ToArgb());
            Assert.AreEqual(new Rectangle(14, 14, 12, 12), logged.Box);
            Assert.AreEqual(3, logged.RotationStep);
            StringAssert.StartsWith(logged.Site, "IconRasterTests.cs:");
        }
        finally
        {
            IconPainter.DrawnIconLog = null;
        }

        IconPainter.Draw(g, Icon.Lightning, IconStyle.Filled, new Rectangle(0, 0, 40, 40), 12, Color.Red);
        Assert.IsNull(IconPainter.DrawnIconLog, "Outside a test nothing is kept.");
    }

    [TestMethod]
    public void AClearInkDrawsNothing()
    {
        var background = Color.FromArgb(255, 9, 9, 9);
        using Bitmap canvas = Canvas(30, background);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            IconPainter.Draw(g, Icon.Check, IconStyle.Line, new Rectangle(0, 0, 30, 30), 16, Color.Transparent);
        }

        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                Assert.AreEqual(background.ToArgb(), canvas.GetPixel(x, y).ToArgb());
            }
        }
    }

    [TestMethod]
    public void TheBoxIsCentredByIntegerDivisionOnTheRectangleItIsGiven()
    {
        Assert.AreEqual(new Point(22, 22), IconPainter.BoxOrigin(new Rectangle(10, 12, 40, 36), 16));
        Assert.AreEqual(new Point(11, 11), IconPainter.BoxOrigin(new Rectangle(10, 10, 5, 5), 3), "(5 - 3) / 2 = 1");
        Assert.AreEqual(new Point(10, 10), IconPainter.BoxOrigin(new Rectangle(10, 10, 4, 4), 3), "(4 - 3) / 2 = 0");
        Assert.AreEqual(new Point(10, 10), IconPainter.BoxOrigin(new Rectangle(10, 10, 4, 4), 5), "(4 - 5) / 2 truncates to 0");
    }
}
