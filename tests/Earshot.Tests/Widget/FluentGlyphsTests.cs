using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The icon code points: the installed Segoe Fluent Icons must have a glyph for each one the card uses. A hosted
// image without the font cannot say, and the test then reports inconclusive rather than pass or fail.
[TestClass]
public sealed class FluentGlyphsTests
{
    private static readonly int[] Dpis = [96, 120, 144];

    private static bool FontInstalled(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
    }

    // Every char constant the class declares is a code point the card uses.
    private static List<char> CodePoints() =>
        typeof(FluentGlyphs).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(char) && f.IsLiteral)
            .Select(f => (char)f.GetRawConstantValue()!)
            .ToList();

    [TestMethod]
    public void EveryCodePointTheCardUsesHasAGlyphInTheInstalledFont()
    {
        if (!FontInstalled(FluentGlyphs.FluentFamily))
        {
            Assert.Inconclusive("Segoe Fluent Icons is not installed on this machine.");
        }

        Assert.IsGreaterThan(20, CodePoints().Count, "The constants were found.");
        var missing = CodePoints().Where(c => !FluentGlyphs.HasGlyph(FluentGlyphs.FluentFamily, c)).Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.AreEqual(0, missing.Count, "No glyph for: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void AMissingGlyphIsReportedAsMissing()
    {
        if (!FontInstalled(FluentGlyphs.FluentFamily))
        {
            Assert.Inconclusive("Segoe Fluent Icons is not installed on this machine.");
        }

        Assert.IsFalse(FluentGlyphs.HasGlyph(FluentGlyphs.FluentFamily, '\uFFF0'), "A code point the font does not hold is reported missing, not drawn as a default glyph.");
    }

    [TestMethod]
    public void TheFamilyIsFluentThenMdlThenNone()
    {
        Assert.AreEqual(FluentGlyphs.FluentFamily, FluentGlyphs.ChooseFamily(["Arial", "segoe mdl2 assets", "SEGOE FLUENT ICONS"]));
        Assert.AreEqual(FluentGlyphs.MdlFamily, FluentGlyphs.ChooseFamily(["Arial", "Segoe MDL2 Assets"]));
        Assert.IsNull(FluentGlyphs.ChooseFamily(["Arial", "Segoe UI"]), "No icon font: no icon, the label stays.");
    }

    [TestMethod]
    public void TheEarbudFallsBackToTheHeadphoneWhereTheFontHasNoEarbud()
    {
        Assert.AreEqual(FluentGlyphs.Earbud, FluentGlyphs.EarbudOrHeadphone(_ => true));
        Assert.AreEqual(FluentGlyphs.Headphone, FluentGlyphs.EarbudOrHeadphone(c => c != FluentGlyphs.Earbud));
    }

    [TestMethod]
    public void AGlyphDrawsInkInsideItsBoundsAtEachScale()
    {
        if (FluentGlyphs.Family is null)
        {
            Assert.Inconclusive("No icon font is installed on this machine.");
        }

        foreach (int dpi in Dpis)
        {
            int size = (int)Math.Round(16 * dpi / 96.0, MidpointRounding.AwayFromZero);
            using var bitmap = new Bitmap(size * 2, size * 2, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                Earshot.Widget.CardPaint.Glyph(g, FluentGlyphs.Settings, new Rectangle(size / 2, size / 2, size, size), Color.Black, dpi);
            }

            int ink = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).ToArgb() != Color.White.ToArgb())
                    {
                        ink++;
                    }
                }
            }

            Assert.IsTrue(ink > size, "The glyph draws something at " + dpi + " dpi.");
        }
    }
}
