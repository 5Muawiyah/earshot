using System.Drawing;
using System.Windows.Forms;
using Earshot.Icons;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

[TestClass]
public sealed class CardThemeTests
{
    [TestMethod]
    public void HighContrastUsesTheSystemColours()
    {
        Color window = Color.FromArgb(0, 0, 0);
        Color windowText = Color.FromArgb(255, 255, 0);

        CardPalette palette = CardTheme.For(highContrast: true, Color.White, window, windowText);

        Assert.IsTrue(palette.HighContrast);
        Assert.AreEqual(window, palette.Background);
        Assert.AreEqual(windowText, palette.Title);
        Assert.AreEqual(windowText, palette.Status);
        Assert.AreEqual(windowText, palette.Border);
    }

    [TestMethod]
    public void TheCardFollowsTheTaskbarInk()
    {
        Assert.AreEqual(CardTheme.Dark, CardTheme.For(false, Color.White, SystemColors.Window, SystemColors.WindowText), "White ink means a dark taskbar.");
        Assert.AreEqual(CardTheme.Dark, CardTheme.For(false, Color.FromArgb(255, 255, 255), SystemColors.Window, SystemColors.WindowText));
        Assert.AreEqual(CardTheme.Light, CardTheme.For(false, Color.Black, SystemColors.Window, SystemColors.WindowText));
        Assert.AreEqual(CardTheme.Light, CardTheme.For(false, Color.FromArgb(0, 0, 0), SystemColors.Window, SystemColors.WindowText));
    }

    [TestMethod]
    public void TheCurrentPaletteUsesTheTrayIconThemeReading()
    {
        var reader = new ThemeReader(new CapturingLog());

        CardPalette current = CardTheme.Current(reader);

        Assert.AreEqual(CardTheme.For(SystemInformation.HighContrast, reader.Ink(), SystemColors.Window, SystemColors.WindowText), current);
    }

    [TestMethod]
    public void TextIsReadableOnBothFixedPalettes()
    {
        foreach (CardPalette palette in new[] { CardTheme.Dark, CardTheme.Light })
        {
            Assert.IsFalse(palette.HighContrast);
            Assert.IsGreaterThanOrEqualTo(4.5, Contrast(palette.Title, palette.Background), "Title contrast.");
            Assert.IsGreaterThanOrEqualTo(4.5, Contrast(palette.Status, palette.Background), "Status contrast.");
            Assert.AreNotEqual(palette.Background, palette.Border);
        }
    }

    // WCAG 2 contrast ratio.
    // https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        double v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
