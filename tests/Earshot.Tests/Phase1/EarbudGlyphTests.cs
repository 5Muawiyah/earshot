using System.Drawing;
using Earshot.Icons;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase1;

// 16, 20, 24 and 32 px are the tray icon sizes at 100, 125, 150 and 200 percent scaling.
[TestClass]
public sealed class EarbudGlyphTests
{
    private static readonly GlyphState[] AllStates = [GlyphState.Connected, GlyphState.Disconnected, GlyphState.Busy, GlyphState.Blocked];

    private static byte At(byte[] alpha, int px, int x, int y) => alpha[(y * px) + x];

    // The pixel that holds the centre of the right head, which the slash never crosses.
    private static (int X, int Y) HeadCentrePixel(EarbudGlyph.Layout layout) =>
        ((int)Math.Floor(layout.RightHead.CentreX), (int)Math.Floor(layout.RightHead.CentreY));

    [TestMethod]
    [DataRow(16, 1)]
    [DataRow(20, 1)]
    [DataRow(24, 2)]
    [DataRow(28, 2)]
    [DataRow(32, 2)]
    [DataRow(40, 3)]
    [DataRow(48, 3)]
    public void TheOutlineWidthScalesWithSize(int px, int stroke)
    {
        Assert.AreEqual(stroke, EarbudGlyph.StrokeFor(px));
        Assert.AreEqual(stroke, EarbudGlyph.LayoutFor(px).Stroke);
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheHeadCentreIsSolidOnlyForConnectedAndBusy(int px)
    {
        (int x, int y) = HeadCentrePixel(EarbudGlyph.LayoutFor(px));

        Assert.AreEqual((byte)255, At(EarbudGlyph.Coverage(px, GlyphState.Connected), px, x, y));
        Assert.AreEqual((byte)102, At(EarbudGlyph.Coverage(px, GlyphState.Busy), px, x, y), "40 percent of 255");
        Assert.AreEqual((byte)0, At(EarbudGlyph.Coverage(px, GlyphState.Disconnected), px, x, y));
        Assert.AreEqual((byte)0, At(EarbudGlyph.Coverage(px, GlyphState.Blocked), px, x, y));
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheOutlinedGlyphKeepsATransparentInteriorAtLeastTwoPixelsWide(int px)
    {
        EarbudGlyph.Layout layout = EarbudGlyph.LayoutFor(px);
        byte[] outlined = EarbudGlyph.Coverage(px, GlyphState.Disconnected);
        byte[] solid = EarbudGlyph.Coverage(px, GlyphState.Connected);
        (int cx, int cy) = HeadCentrePixel(layout);

        for (int y = cy - 1; y <= cy; y++)
        {
            for (int x = cx - 1; x <= cx; x++)
            {
                Assert.AreEqual((byte)0, At(outlined, px, x, y), "interior " + x + "," + y);
                Assert.AreEqual((byte)255, At(solid, px, x, y), "solid " + x + "," + y);
            }
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheStemOutlineSitsOnWholePixelsWithAGapInside(int px)
    {
        EarbudGlyph.Layout layout = EarbudGlyph.LayoutFor(px);
        byte[] outlined = EarbudGlyph.Coverage(px, GlyphState.Disconnected);
        int row = (int)Math.Floor((layout.LeftHead.CentreY + layout.LeftHead.Radius + layout.LeftStem.Bottom) / 2);

        foreach (EarbudGlyph.RoundBox stem in new[] { layout.LeftStem, layout.RightStem })
        {
            int left = (int)stem.Left;
            int right = (int)stem.Right;
            Assert.AreEqual(stem.Left, left, "stem edges are whole pixels");
            Assert.IsGreaterThan((2 * layout.Stroke) + 1 - 1, right - left, "stem wide enough for a gap inside");
            Assert.AreEqual((byte)0, At(outlined, px, left - 1, row), "outside the left edge");
            Assert.AreEqual((byte)0, At(outlined, px, right, row), "outside the right edge");
            for (int i = 0; i < layout.Stroke; i++)
            {
                Assert.AreEqual((byte)255, At(outlined, px, left + i, row), "left outline");
                Assert.AreEqual((byte)255, At(outlined, px, right - 1 - i, row), "right outline");
            }

            Assert.AreEqual((byte)0, At(outlined, px, left + layout.Stroke, row), "inside the stem");
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheBlockedGlyphAddsASlashThroughTheInterior(int px)
    {
        EarbudGlyph.Layout layout = EarbudGlyph.LayoutFor(px);
        byte[] blocked = EarbudGlyph.Coverage(px, GlyphState.Blocked);
        byte[] outlined = EarbudGlyph.Coverage(px, GlyphState.Disconnected);

        // The slash runs through pixel centres (k, k). Pick one well inside the head interior.
        int? k = Enumerable.Range(0, px)
            .Where(i => layout.LeftHead.Distance(i + 0.5, i + 0.5) < -(layout.Stroke + 0.75))
            .Select(i => (int?)i)
            .FirstOrDefault();

        Assert.IsNotNull(k, "The slash crosses the head interior.");
        Assert.IsGreaterThan(199, At(blocked, px, k.Value, k.Value));
        Assert.AreEqual((byte)0, At(outlined, px, k.Value, k.Value));
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheBlockedSlashIsThin(int px)
    {
        // Along one row inside the head interior the slash covers at most stroke + 1 pixels.
        EarbudGlyph.Layout layout = EarbudGlyph.LayoutFor(px);
        byte[] blocked = EarbudGlyph.Coverage(px, GlyphState.Blocked);
        int row = (int)Math.Floor(layout.LeftHead.CentreY);
        int left = (int)Math.Ceiling(layout.LeftHead.CentreX - layout.LeftHead.Radius + layout.Stroke + 1);
        int right = (int)Math.Floor(layout.LeftHead.CentreX + layout.LeftHead.Radius - layout.Stroke - 1);

        int inked = Enumerable.Range(left, right - left).Count(x => At(blocked, px, x, row) > 127);

        Assert.IsGreaterThan(0, inked);
        Assert.IsLessThan(layout.Stroke + 2, inked);
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void BusyIsTheSolidGlyphAtFortyPercent(int px)
    {
        byte[] solid = EarbudGlyph.Coverage(px, GlyphState.Connected);
        byte[] busy = EarbudGlyph.Coverage(px, GlyphState.Busy);

        for (int i = 0; i < solid.Length; i++)
        {
            double expected = solid[i] * EarbudGlyph.BusyOpacity;
            Assert.IsLessThan(1.01, Math.Abs(busy[i] - expected), "pixel " + i);
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void EveryStateLooksDifferent(int px)
    {
        byte[][] images = AllStates.Select(s => EarbudGlyph.Coverage(px, s)).ToArray();

        for (int a = 0; a < images.Length; a++)
        {
            for (int b = a + 1; b < images.Length; b++)
            {
                int differing = images[a].Zip(images[b]).Count(p => Math.Abs(p.First - p.Second) > 32);
                Assert.IsGreaterThan(2, differing, AllStates[a] + " and " + AllStates[b]);
            }
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    [DataRow(48)]
    public void TheOuterRingOfPixelsIsTransparent(int px)
    {
        foreach (GlyphState state in AllStates)
        {
            byte[] alpha = EarbudGlyph.Coverage(px, state);
            for (int i = 0; i < px; i++)
            {
                Assert.AreEqual((byte)0, At(alpha, px, i, 0), state + " top " + i);
                Assert.AreEqual((byte)0, At(alpha, px, i, px - 1), state + " bottom " + i);
                Assert.AreEqual((byte)0, At(alpha, px, 0, i), state + " left " + i);
                Assert.AreEqual((byte)0, At(alpha, px, px - 1, i), state + " right " + i);
            }
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void AWhiteGlyphHasNoDarkenedEdgesInItsPng(int px)
    {
        foreach (GlyphState state in AllStates)
        {
            using var stream = new MemoryStream(EarbudGlyph.RenderPng(px, state, Color.White));
            using var bitmap = new Bitmap(stream);
            int partial = 0;
            for (int y = 0; y < px; y++)
            {
                for (int x = 0; x < px; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.A == 0)
                    {
                        continue;
                    }

                    Assert.AreEqual((255, 255, 255), (pixel.R, pixel.G, pixel.B), state + " at " + x + "," + y + " alpha " + pixel.A);
                    if (pixel.A < 255)
                    {
                        partial++;
                    }
                }
            }

            Assert.IsGreaterThan(0, partial, state + " has antialiased pixels to check.");
        }
    }

    [TestMethod]
    public void TheInkColourIsUsedAsGiven()
    {
        Color ink = Color.FromArgb(255, 10, 200, 30);
        byte[] alpha = EarbudGlyph.Coverage(20, GlyphState.Connected);
        using Bitmap bitmap = EarbudGlyph.ToBitmap(alpha, 20, ink);

        Color centre = bitmap.GetPixel(9, 9);
        Assert.AreEqual((10, 200, 30), (centre.R, centre.G, centre.B));
        Assert.AreEqual(alpha[(9 * 20) + 9], centre.A);
    }

    [TestMethod]
    public void RenderingIsDeterministic()
    {
        foreach (GlyphState state in AllStates)
        {
            CollectionAssert.AreEqual(EarbudGlyph.Coverage(24, state), EarbudGlyph.Coverage(24, state), state.ToString());
        }
    }

    [TestMethod]
    public void SizesOutsideTheIconRangeAreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EarbudGlyph.Coverage(7, GlyphState.Connected));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EarbudGlyph.Coverage(257, GlyphState.Connected));
        Assert.ThrowsExactly<ArgumentException>(() => EarbudGlyph.ToBitmap(new byte[10], 16, Color.White));
    }
}
