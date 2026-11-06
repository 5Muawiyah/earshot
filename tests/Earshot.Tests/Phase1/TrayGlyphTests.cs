using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Earshot.Icons;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = System.Drawing.Icon;
using MingCute = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Phase1;

// The tray icon is MingCute's AirPods icon (the icons spec, sections 1b and 8): Disconnected the line drawing, Connected the filled
// one, Busy the filled one at 0.4 of its strength, Blocked the line drawing with Earshot's own slash. 16, 20, 24 and 32 px are the
// tray icon sizes at 100, 125, 150 and 200 percent scaling.
[TestClass]
public sealed class TrayGlyphTests
{
    private static readonly GlyphState[] AllStates = [GlyphState.Connected, GlyphState.Disconnected, GlyphState.Busy, GlyphState.Blocked];

    // The mask of the icon in the tray box: the box's px x px, without the mask's apron.
    private static byte[] Cropped(IconStyle style, int px)
    {
        ReadOnlySpan<byte> mask = IconRaster.Coverage(MingCute.AirPods, style, px, IconRule.A).Span;
        int side = IconRaster.MaskSide(px);
        var cropped = new byte[px * px];
        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                cropped[(y * px) + x] = mask[((y + IconRaster.Apron) * side) + x + IconRaster.Apron];
            }
        }

        return cropped;
    }

    private static byte At(byte[] alpha, int px, int x, int y) => alpha[(y * px) + x];

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void DisconnectedIsTheLineIconAndConnectedTheFilledIconAsTheRasterMakesThem(int px)
    {
        CollectionAssert.AreEqual(Cropped(IconStyle.Line, px), TrayGlyph.Coverage(px, GlyphState.Disconnected));
        CollectionAssert.AreEqual(Cropped(IconStyle.Filled, px), TrayGlyph.Coverage(px, GlyphState.Connected));
        Assert.IsGreaterThan(0, TrayGlyph.Coverage(px, GlyphState.Connected).Count(a => a == 255), "The filled icon has solid pixels.");
        Assert.AreNotEqual(Cropped(IconStyle.Line, px).Sum(a => a), Cropped(IconStyle.Filled, px).Sum(a => a), "Line and filled differ.");
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void BusyIsTheFilledIconAtFortyPercentRounded(int px)
    {
        byte[] filled = Cropped(IconStyle.Filled, px);
        byte[] busy = TrayGlyph.Coverage(px, GlyphState.Busy);

        Assert.HasCount(filled.Length, busy);
        for (int i = 0; i < filled.Length; i++)
        {
            Assert.AreEqual((byte)Math.Round(filled[i] * 0.4, MidpointRounding.AwayFromZero), busy[i], "pixel " + (i % px) + "," + (i / px));
        }

        Assert.AreEqual((byte)102, busy.Max(), "A solid pixel is 40 percent of 255.");
    }

    // The slash runs from (2, 2) to (14, 14) of the 16 unit grid, snapped to whole pixels, as it always has: half the outline width
    // either side of the line, and a clear gap of max(1, width - 1) outside that where it crosses the drawing.
    private static (double Start, double End, double Half, double Gap) Slash(int px)
    {
        double unit = px / 16.0;
        int stroke = Math.Max(1, (int)Math.Round(px / 16.0, MidpointRounding.AwayFromZero));
        return (Math.Round(2 * unit, MidpointRounding.AwayFromZero), Math.Round(14 * unit, MidpointRounding.AwayFromZero), stroke / 2.0, Math.Max(1, stroke - 1));
    }

    private static double DistanceToSlash(int px, double x, double y)
    {
        (double start, double end, _, _) = Slash(px);
        double v = end - start;
        double t = Math.Clamp((((x - start) * v) + ((y - start) * v)) / (2 * v * v), 0, 1);
        double dx = x - (start + (t * v));
        double dy = y - (start + (t * v));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void BlockedIsTheLineIconWithTheSlashThroughItAndAGapEitherSide(int px)
    {
        byte[] blocked = TrayGlyph.Coverage(px, GlyphState.Blocked);
        byte[] line = Cropped(IconStyle.Line, px);
        (_, _, double half, double gap) = Slash(px);

        int onTheSlash = 0;
        int untouched = 0;
        int cut = 0;
        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                double d = DistanceToSlash(px, x + 0.5, y + 0.5);
                if (d > half + gap + 1.5)
                {
                    Assert.AreEqual(line[(y * px) + x], blocked[(y * px) + x], "clear of the slash: the line icon as it is at " + x + "," + y);
                    untouched++;
                }
                else if (d < half - 0.4 && line[(y * px) + x] == 0)
                {
                    Assert.IsGreaterThan(199, blocked[(y * px) + x], "the slash through the empty inside at " + x + "," + y);
                    onTheSlash++;
                }
                else if (line[(y * px) + x] > 127 && d <= half + gap && blocked[(y * px) + x] < line[(y * px) + x])
                {
                    cut++;
                }
            }
        }

        Assert.IsGreaterThan(px, untouched, "Most of the icon lies clear of the slash.");
        Assert.IsGreaterThan(0, onTheSlash, "The slash crosses the empty inside of the icon.");
        Assert.IsGreaterThan(0, cut, "The gap cuts the outline where the slash crosses it.");
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheSlashIsThin(int px)
    {
        // Along each row, the pixels more than half covered and within the slash's own half width and a pixel of it never exceed the
        // outline width plus one.
        byte[] blocked = TrayGlyph.Coverage(px, GlyphState.Blocked);
        byte[] line = Cropped(IconStyle.Line, px);
        (_, _, double half, _) = Slash(px);
        int stroke = Math.Max(1, (int)Math.Round(px / 16.0, MidpointRounding.AwayFromZero));

        for (int y = 0; y < px; y++)
        {
            int inked = 0;
            for (int x = 0; x < px; x++)
            {
                if (blocked[(y * px) + x] > 127 && line[(y * px) + x] == 0 && DistanceToSlash(px, x + 0.5, y + 0.5) <= half + 1)
                {
                    inked++;
                }
            }

            Assert.IsLessThan(stroke + 2, inked, "row " + y);
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void EveryPngFrameIsStraightAlphaWithTheInkInEveryDrawnPixel(int px)
    {
        Color ink = Color.FromArgb(0x20, 0xA0, 0xE0);
        foreach (GlyphState state in AllStates)
        {
            byte[] coverage = TrayGlyph.Coverage(px, state);
            byte[] png = TrayGlyph.RenderPng(px, state, ink);
            using var stream = new MemoryStream(png);
            using var decoded = new Bitmap(stream);
            Assert.AreEqual(px, decoded.Width, state.ToString());
            BitmapData data = decoded.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[px * 4];
                int drawn = 0;
                for (int y = 0; y < px; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < px; x++)
                    {
                        byte a = row[(x * 4) + 3];
                        Assert.AreEqual(coverage[(y * px) + x], a, state + " alpha at " + x + "," + y);
                        if (a > 0)
                        {
                            Assert.AreEqual(ink.B, row[x * 4], state + " B at " + x + "," + y);
                            Assert.AreEqual(ink.G, row[(x * 4) + 1], state + " G at " + x + "," + y);
                            Assert.AreEqual(ink.R, row[(x * 4) + 2], state + " R at " + x + "," + y);
                            drawn++;
                        }
                    }
                }

                Assert.IsGreaterThan(px, drawn, state + " draws something.");
            }
            finally
            {
                decoded.UnlockBits(data);
            }
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheIcoFrameLoadsAtTheTraySizeForEveryState(int px)
    {
        foreach (GlyphState state in AllStates)
        {
            byte[] ico = IcoWriter.SingleFramePng(TrayGlyph.RenderPng(px, state, Color.White), px);
            using Icon icon = IcoWriter.Load(ico, px);
            Assert.AreEqual(px, icon.Width, state.ToString());
            Assert.AreEqual(px, icon.Height, state.ToString());
            using Icon made = TrayIconFactory.Create(px, state, Color.White);
            Assert.AreEqual(px, made.Width, state.ToString());
        }
    }

    [TestMethod]
    public void EachStateDiffersFromEveryOtherAtEverySize()
    {
        foreach (int px in new[] { 16, 20, 24, 32 })
        {
            for (int a = 0; a < AllStates.Length; a++)
            {
                for (int b = a + 1; b < AllStates.Length; b++)
                {
                    CollectionAssert.AreNotEqual(TrayGlyph.Coverage(px, AllStates[a]), TrayGlyph.Coverage(px, AllStates[b]), AllStates[a] + " and " + AllStates[b] + " at " + px);
                }
            }
        }
    }

    [TestMethod]
    public void SizesOutsideTheIconRangeAreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TrayGlyph.Coverage(7, GlyphState.Connected));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TrayGlyph.Coverage(257, GlyphState.Connected));
        Assert.ThrowsExactly<ArgumentException>(() => TrayGlyph.ToBitmap(new byte[10], 16, Color.White));
    }

    [TestMethod]
    public void TheTraySizeIsClampedToTheGlyphsRange()
    {
        Assert.AreEqual(TrayGlyph.MinSize, TrayIconFactory.SizeFor(1, 96));
        Assert.AreEqual(TrayGlyph.MaxSize, TrayIconFactory.SizeFor(900, 96));
        Assert.AreEqual(32, TrayIconFactory.SizeFor(0, 192), "Without the metric, 16 px at 200 percent.");
    }
}
