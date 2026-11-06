using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Earshot.Icons;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Widget;

// Every icon the gauge draws (the icons spec, sections 1b, 2 and 3): the gauge is rendered with the icon log on, and each mode and
// story frame must have drawn the icon the spec maps it to, in the style the spec gives it, in the palette's ink, in the box the spec
// gives it and from GaugeRenderer; and the pixels in that box must be exactly the icon's coverage mask composited in that ink over the
// gauge drawn without its icons. The gauge bitmap is Format32bppPArgb, so the comparison is made on the stored (premultiplied) values,
// within one level of 255 a channel.
[TestClass]
public sealed class GaugeIconSiteTests
{
    private const string FontFamily = "Segoe UI";
    private static readonly int[] Dpis = [96, 120, 144, 192];
    private static readonly Color Accent = Color.FromArgb(0x00, 0x5F, 0xB8);
    private static readonly Color Placeholder = Color.FromArgb(0x80, 0x90, 0xA0);

    private sealed record Scene(Bitmap Bitmap, Bitmap Background, IReadOnlyList<DrawnIcon> Log, GaugePalette Palette, GaugeLayout Layout, int Dpi, DesignTheme Theme);

    private static GaugePalette Palette(DesignTheme theme) =>
        theme == DesignTheme.HighContrast
            ? GaugePalette.Create(lightTheme: true, Accent, highContrast: true, SystemColors.WindowText)
            : GaugePalette.Create(lightTheme: theme == DesignTheme.Light, Accent, highContrast: false, theme == DesignTheme.Light ? Color.Black : Color.White);

    private static Color Fade(Color colour, double opacity) =>
        Color.FromArgb((int)Math.Round(colour.A * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero), colour);

    // Draws what render draws twice, once with every icon left out and once as it is, and hands check each scene.
    private static void Each(Func<GaugePalette, GaugeLayout, Bitmap> render, Action<Scene> check, GaugeOrder order = GaugeOrder.RingNumberBolt)
    {
        foreach (DesignTheme theme in DesignPixels.Themes)
        {
            foreach (int dpi in Dpis)
            {
                GaugePalette palette = Palette(theme);
                GaugeLayout layout = GaugeLayout.For(dpi, order);
                IconPainter.DrawnIconLog = [];
                try
                {
                    IconPainter.PaintNothingForTest = true;
                    using Bitmap background = render(palette, layout);
                    IconPainter.PaintNothingForTest = false;
                    IconPainter.DrawnIconLog.Clear();
                    using Bitmap bitmap = render(palette, layout);
                    check(new Scene(bitmap, background, IconPainter.DrawnIconLog.ToList(), palette, layout, dpi, theme));
                }
                finally
                {
                    IconPainter.PaintNothingForTest = false;
                    IconPainter.DrawnIconLog = null;
                }
            }
        }
    }

    private static Bitmap Draw(GaugeContent content, GaugePalette palette, GaugeLayout layout, GaugeStoryFrame? frame = null) =>
        GaugeRenderer.Render(content, palette, layout, hover: 0, FontFamily, ring: null, frame);

    private static void Each(GaugeContent content, Action<Scene> check, GaugeStoryFrame? frame = null, GaugeOrder order = GaugeOrder.RingNumberBolt) =>
        Each((p, l) => Draw(content, p, l, frame), check, order);

    // The icon must have been drawn once, exactly so, by GaugeRenderer; the pixels in its box are checked.
    private static void Expect(Scene s, Icon icon, IconStyle style, Color ink, int box, Rectangle bounds)
    {
        string where = " (" + s.Theme + ", dpi " + s.Dpi + ")";
        var wanted = new Rectangle(IconPainter.BoxOrigin(bounds, box), new Size(box, box));
        List<DrawnIcon> found = s.Log.Where(d =>
            d.Icon == icon && d.Style == style && d.Ink.ToArgb() == ink.ToArgb() && d.BoxPx == box && d.Rule == IconRule.A && d.RotationStep == 0
            && d.Box == wanted && d.Site.StartsWith("GaugeRenderer.cs:", StringComparison.Ordinal)).ToList();
        if (found.Count != 1)
        {
            var sb = new StringBuilder();
            sb.Append("Expected one ").Append(style).Append(' ').Append(icon).Append(" of ").Append(box).Append(" px in ").Append(ink).Append(" at ").Append(wanted)
                .Append(where).Append(", found ").Append(found.Count).Append(". Drawn:");
            foreach (DrawnIcon d in s.Log)
            {
                sb.Append("\n  ").Append(d.Icon).Append(' ').Append(d.Style).Append(' ').Append(d.BoxPx).Append(" px ").Append(d.Ink).Append(' ').Append(d.Box).Append(' ').Append(d.Site);
            }

            Assert.Fail(sb.ToString());
        }

        AssertPixelExact(s, found[0]);
    }

    private static void ExpectOnly(Scene s, int count) =>
        Assert.HasCount(count, s.Log, "The icons drawn: " + string.Join(", ", s.Log.Select(d => d.Icon + " " + d.Style)) + " (" + s.Theme + ", dpi " + s.Dpi + ")");

    private static void AssertPixelExact(Scene s, DrawnIcon d)
    {
        ReadOnlySpan<byte> mask = IconRaster.Coverage(d.Icon, d.Style, d.BoxPx, d.Rule, d.RotationStep).Span;
        int side = IconRaster.MaskSide(d.BoxPx);
        int left = d.Box.X - IconRaster.Apron;
        int top = d.Box.Y - IconRaster.Apron;
        var all = new Rectangle(0, 0, s.Bitmap.Width, s.Bitmap.Height);
        Assert.IsTrue(all.Contains(new Rectangle(left, top, side, side)), d.Icon + " at " + d.Box + " lies inside the gauge bitmap " + all);

        BitmapData drawn = s.Bitmap.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        BitmapData under = s.Background.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var a = new byte[4];
            var b = new byte[4];
            int inked = 0;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(drawn.Scan0 + ((top + y) * drawn.Stride) + ((left + x) * 4), a, 0, 4);
                    System.Runtime.InteropServices.Marshal.Copy(under.Scan0 + ((top + y) * under.Stride) + ((left + x) * 4), b, 0, 4);
                    int alpha = ((mask[(y * side) + x] * d.Ink.A) + 127) / 255;
                    int[] source = [d.Ink.B, d.Ink.G, d.Ink.R];
                    for (int c = 0; c < 3; c++)
                    {
                        int expected = (((source[c] * alpha) + 127) / 255) + (((b[c] * (255 - alpha)) + 127) / 255);
                        Assert.IsLessThanOrEqualTo(
                            1, Math.Abs(a[c] - expected),
                            d.Icon + " " + d.Style + " at " + d.Box + ", pixel " + (left + x) + "," + (top + y) + " channel " + c + " (" + s.Theme + ", dpi " + s.Dpi + "): drawn " + a[c] + ", expected " + expected);
                    }

                    int expectedAlpha = alpha + (((b[3] * (255 - alpha)) + 127) / 255);
                    Assert.IsLessThanOrEqualTo(
                        1, Math.Abs(a[3] - expectedAlpha),
                        d.Icon + " " + d.Style + " at " + d.Box + ", pixel " + (left + x) + "," + (top + y) + " alpha (" + s.Theme + ", dpi " + s.Dpi + "): drawn " + a[3] + ", expected " + expectedAlpha);
                    inked += mask[(y * side) + x] > 0 ? 1 : 0;
                }
            }

            Assert.IsGreaterThan(0, inked, d.Icon + " at " + d.Box + " has no pixel.");
        }
        finally
        {
            s.Bitmap.UnlockBits(drawn);
            s.Background.UnlockBits(under);
        }
    }

    private static int Box(Scene s) => GaugeLayout.Round(14, s.Dpi);

    // ---- The pair, the case, the bolt and the phone at rest

    [TestMethod]
    public void AReadingIsTheFilledPairInInkAndAFilledBoltWhileCharging() => Each(new GaugeContent(GaugeMode.Reading, 60, false, true, ""), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
        Expect(s, Icon.Lightning, IconStyle.Filled, s.Palette.Ink, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    public void AReadingThatIsNotChargingHasNoBolt() => Each(new GaugeContent(GaugeMode.Reading, 60, false, false, ""), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
        ExpectOnly(s, 1);
    });

    [TestMethod]
    public void ALastReadingKeepsTheInkPairAndDrawsTheBoltInTertiary() => Each(new GaugeContent(GaugeMode.Reading, 62, false, true, "") { Tertiary = true }, s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
        Expect(s, Icon.Lightning, IconStyle.Filled, s.Palette.Tertiary, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    public void NoRecentReadingIsTheFilledPairInTertiary() => Each(new GaugeContent(GaugeMode.MarkOnly, null, false, false, ""), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Tertiary, Box(s), s.Layout.Mark);
        ExpectOnly(s, 1);
    });

    [TestMethod]
    public void NotOnThisPcIsTheLinePairInDisabled() => Each(new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, ""), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Line, s.Palette.Disabled, Box(s), s.Layout.Mark);
        ExpectOnly(s, 1);
    });

    [TestMethod]
    public void OnAnotherDeviceIsTheLinePairAndTheLinePhoneInTertiary() => Each(new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, ""), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Line, s.Palette.Tertiary, Box(s), s.Layout.Mark);
        Expect(s, Icon.Cellphone, IconStyle.Line, s.Palette.Tertiary, s.Layout.PhoneSize, s.Layout.NumberSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    public void TheAwayCaseIsTheLineCaseIconInInkAndAFilledBoltWhileItCharges() => Each(new GaugeContent(GaugeMode.CaseAway, 75, false, true, "") { CaseMark = true }, s =>
    {
        Expect(s, Icon.AirPodsCase, IconStyle.Line, s.Palette.Ink, Box(s), s.Layout.Mark);
        Expect(s, Icon.Lightning, IconStyle.Filled, s.Palette.Ink, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    public void ALastCaseValueIsTheCaseIconAndTheBoltInTertiary() => Each(new GaugeContent(GaugeMode.CaseAway, 80, false, true, "") { CaseMark = true, Tertiary = true }, s =>
    {
        Expect(s, Icon.AirPodsCase, IconStyle.Line, s.Palette.Tertiary, Box(s), s.Layout.Mark);
        Expect(s, Icon.Lightning, IconStyle.Filled, s.Palette.Tertiary, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    [DataRow(GaugeOrder.RingNumberBolt)]
    [DataRow(GaugeOrder.BoltNumberRing)]
    [DataRow(GaugeOrder.NumberBoltRing)]
    public void TheIconsFollowTheirSlotsInEveryOrder(GaugeOrder order) =>
        Each(new GaugeContent(GaugeMode.Reading, 60, false, true, ""), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
            Expect(s, Icon.Lightning, IconStyle.Filled, s.Palette.Ink, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
        }, order: order);

    // ---- The story

    private static GaugeContent Story(GaugeMode mode, GaugeStoryKind kind, bool charging = false) =>
        new(mode, mode == GaugeMode.OnOtherDevice ? null : 70, false, charging, "") { Story = new GaugeStoryView(kind, true) };

    [TestMethod]
    public void TheTickIsTheCheckLineIconInTheTickInkInTheNumbersPlace() => Each(Story(GaugeMode.Reading, GaugeStoryKind.Tick), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
        Expect(s, Icon.Check, IconStyle.Line, s.Palette.Tick, s.Layout.Bolt.Height, s.Layout.NumberSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    public void TheCrossIsTheCloseLineIconInCautionInTheNumbersPlace() => Each(Story(GaugeMode.Reading, GaugeStoryKind.Cross), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
        Expect(s, Icon.Close, IconStyle.Line, s.Palette.Caution, s.Layout.Bolt.Height, s.Layout.NumberSlot);
        ExpectOnly(s, 2);
    });

    [TestMethod]
    public void TheSpinningSweepDrawsOnlyTheMark() => Each(Story(GaugeMode.Reading, GaugeStoryKind.Spinning, charging: true), s =>
    {
        Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
        ExpectOnly(s, 1);
    });

    [TestMethod]
    public void TheGlyphAtHalfStrengthIsTheSameIconAtHalfTheInksAlpha()
    {
        var tick = new GaugeStoryFrame(GaugeStoryKind.Tick, -90, 252, GaugeSlotGlyph.Tick, 0.5, 0);
        Each(Story(GaugeMode.Reading, GaugeStoryKind.Tick), s => Expect(s, Icon.Check, IconStyle.Line, Fade(s.Palette.Tick, 0.5), s.Layout.Bolt.Height, s.Layout.NumberSlot), tick);
        var cross = new GaugeStoryFrame(GaugeStoryKind.Cross, -90, 0, GaugeSlotGlyph.Cross, 0.25, 0);
        Each(Story(GaugeMode.Reading, GaugeStoryKind.Cross), s => Expect(s, Icon.Close, IconStyle.Line, Fade(s.Palette.Caution, 0.25), s.Layout.Bolt.Height, s.Layout.NumberSlot), cross);
    }

    [TestMethod]
    public void WhileTheNumberComesBackTheBoltAndThePhoneFadeInWithIt()
    {
        var leaving = new GaugeStoryFrame(GaugeStoryKind.None, -90, 252, GaugeSlotGlyph.Tick, 0.5, 0.5);
        Each(Story(GaugeMode.Reading, GaugeStoryKind.None, charging: true), s =>
        {
            Expect(s, Icon.Lightning, IconStyle.Filled, Fade(s.Palette.Ink, 0.5), s.Layout.Bolt.Height, s.Layout.ChargingSlot);
            Expect(s, Icon.Check, IconStyle.Line, Fade(s.Palette.Tick, 0.5), s.Layout.Bolt.Height, s.Layout.NumberSlot);
        }, leaving);

        Each(Story(GaugeMode.OnOtherDevice, GaugeStoryKind.None), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Line, s.Palette.Tertiary, Box(s), s.Layout.Mark);
            Expect(s, Icon.Cellphone, IconStyle.Line, Fade(s.Palette.Tertiary, 0.5), s.Layout.PhoneSize, s.Layout.NumberSlot);
        }, new GaugeStoryFrame(GaugeStoryKind.None, -90, 0, GaugeSlotGlyph.None, 0, 0.5));
    }

    [TestMethod]
    public void TheStoryKeepsTheMarkTheStateDrawsAtRest()
    {
        Each(Story(GaugeMode.CaseAway, GaugeStoryKind.Spinning), s =>
        {
            Expect(s, Icon.AirPodsCase, IconStyle.Line, s.Palette.Ink, Box(s), s.Layout.Mark);
            ExpectOnly(s, 1);
        });
        Each(Story(GaugeMode.OnOtherDevice, GaugeStoryKind.Spinning), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Line, s.Palette.Tertiary, Box(s), s.Layout.Mark);
            ExpectOnly(s, 1);
        });
        Each(Story(GaugeMode.MarkOnly, GaugeStoryKind.Spinning), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Tertiary, Box(s), s.Layout.Mark);
            ExpectOnly(s, 1);
        });
        Each(Story(GaugeMode.NotOnThisPc, GaugeStoryKind.Spinning), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Line, s.Palette.Disabled, Box(s), s.Layout.Mark);
            ExpectOnly(s, 1);
        });
    }

    // ---- The picture of an order on the settings page

    [TestMethod]
    public void ThePreviewDrawsTheBoltAsTheLineIconInThePlaceholderInkUntilItCharges()
    {
        var idle = new GaugeContent(GaugeMode.Reading, 60, false, false, "");
        Each((p, l) => GaugeRenderer.RenderPreview(idle, p, l, FontFamily, Placeholder), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Filled, s.Palette.Ink, Box(s), s.Layout.Mark);
            Expect(s, Icon.Lightning, IconStyle.Line, Placeholder, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
            ExpectOnly(s, 2);
        });

        var charging = new GaugeContent(GaugeMode.Reading, 60, false, true, "");
        Each((p, l) => GaugeRenderer.RenderPreview(charging, p, l, FontFamily, Placeholder), s =>
        {
            Expect(s, Icon.Lightning, IconStyle.Filled, s.Palette.Ink, s.Layout.Bolt.Height, s.Layout.ChargingSlot);
            ExpectOnly(s, 2);
        });
    }

    // ---- Clearance: the marks stay inside the ring

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(192)]
    public void NoInkOfThePairOrTheCaseReachesTheRingsInnerEdge(int dpi)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        Rectangle box = GaugeMarks.Box(layout);
        float cx = layout.RingBox.X + (layout.RingBox.Width / 2f);
        float cy = layout.RingBox.Y + (layout.RingBox.Height / 2f);
        double inner = layout.RingRadius - (layout.RingStroke / 2f);

        foreach ((Icon icon, IconStyle style) in new[] { (Icon.AirPods, IconStyle.Filled), (Icon.AirPods, IconStyle.Line), (Icon.AirPodsCase, IconStyle.Line) })
        {
            ReadOnlySpan<byte> mask = IconRaster.Coverage(icon, style, box.Width).Span;
            int side = IconRaster.MaskSide(box.Width);
            double farthest = 0;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    if (mask[(y * side) + x] == 0)
                    {
                        continue;
                    }

                    double dx = (box.X - IconRaster.Apron + x + 0.5) - cx;
                    double dy = (box.Y - IconRaster.Apron + y + 0.5) - cy;
                    farthest = Math.Max(farthest, Math.Sqrt((dx * dx) + (dy * dy)));
                }
            }

            Assert.IsLessThan(inner, farthest, icon + " " + style + " at " + dpi + " dpi reaches " + farthest + " of the ring's inner " + inner);
            Assert.IsGreaterThan(inner / 2, farthest, icon + " " + style + " fills the ring: " + farthest + " of " + inner);
        }
    }
}
