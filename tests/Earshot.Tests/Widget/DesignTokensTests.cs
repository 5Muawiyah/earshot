using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every colour of the design's Colour table is a named token, in one place, with the table's light and dark values and a
// system colour for each under high contrast. The values here are written from the table, not read from the code; "rgba(r,g,b,a)" is
// a colour whose alpha byte is a * 255 rounded half away from zero. Pure: no drawing.
[TestClass]
public sealed class DesignTokensTests
{
    private static Color Rgba(int r, int g, int b, double a) => Color.FromArgb((int)Math.Round(255 * a, MidpointRounding.AwayFromZero), r, g, b);

    private static readonly HighContrastColours Hc = new(
        Window: Color.FromArgb(0, 0, 0), WindowText: Color.FromArgb(255, 255, 255), GrayText: Color.FromArgb(0x3F, 0xF2, 0x3F),
        Highlight: Color.FromArgb(0x1A, 0xEB, 0xFF), HighlightText: Color.FromArgb(0, 0, 0), ButtonFace: Color.FromArgb(0, 0, 0),
        ButtonText: Color.FromArgb(255, 255, 255), HotTrack: Color.FromArgb(255, 255, 0));

    [TestMethod]
    public void TheLightColumnIsTheTables()
    {
        DesignTokens t = DesignTokens.For(dark: false, highContrast: false);

        Assert.AreEqual(Rgba(0, 0, 0, .896), t.TextPrimary);
        Assert.AreEqual(Rgba(0, 0, 0, .606), t.TextSecondary);
        Assert.AreEqual(Rgba(0, 0, 0, .446), t.TextTertiary);
        Assert.AreEqual(Rgba(0, 0, 0, .361), t.TextDisabled);
        Assert.AreEqual(Rgba(252, 252, 252, .85), t.AcrylicTint);
        Assert.AreEqual(Color.FromArgb(0xF9, 0xF9, 0xF9), t.SolidSurface);
        Assert.AreEqual(Rgba(0, 0, 0, .0578), t.SurfaceStroke);
        Assert.AreEqual(Rgba(0, 0, 0, .14), t.Shadow);
        Assert.AreEqual(Rgba(255, 255, 255, .7), t.SettingsRowFill);
        Assert.AreEqual(Rgba(0, 0, 0, .0578), t.SettingsRowStroke);
        Assert.AreEqual(Rgba(255, 255, 255, .7), t.ControlFill);
        Assert.AreEqual(Rgba(0, 0, 0, .0578), t.ControlStroke);
        Assert.AreEqual(Rgba(0, 0, 0, .1622), t.ControlStrokeBottom);
        Assert.AreEqual(Rgba(249, 249, 249, .3), t.ControlFillDisabled);
        Assert.AreEqual(Rgba(0, 0, 0, .0373), t.SubtleHover);
        Assert.AreEqual(Rgba(0, 0, 0, .0241), t.SubtlePressed);
        Assert.AreEqual(Rgba(0, 0, 0, .446), t.ProgressTrack);
        Assert.AreEqual(Rgba(0, 0, 0, .16), t.GaugeRingTrack);
        Assert.AreEqual(Color.FromArgb(0x9D, 0x5D, 0x00), t.Caution);
        Assert.AreEqual(Rgba(0, 0, 0, .0803), t.Divider);
        Assert.AreEqual(Color.FromArgb(0xFF, 0xFF, 0xFF), t.TextOnAccent, "Text on accent: light #FFFFFF.");
        Assert.AreEqual(Color.FromArgb(0xFF, 0xFF, 0xFF), t.FocusInner, "Focus inner stroke: light #FFFFFF.");
        Assert.AreEqual(30, t.AcrylicBlur);
        Assert.IsFalse(t.HighContrast);
        Assert.IsFalse(t.Dark);
    }

    [TestMethod]
    public void TheDarkColumnIsTheTables()
    {
        DesignTokens t = DesignTokens.For(dark: true, highContrast: false);

        Assert.AreEqual(Color.FromArgb(0xFF, 0xFF, 0xFF), t.TextPrimary);
        Assert.AreEqual(Rgba(255, 255, 255, .786), t.TextSecondary);
        Assert.AreEqual(Rgba(255, 255, 255, .544), t.TextTertiary);
        Assert.AreEqual(Rgba(255, 255, 255, .363), t.TextDisabled);
        Assert.AreEqual(Rgba(44, 44, 44, .82), t.AcrylicTint);
        Assert.AreEqual(Color.FromArgb(0x2C, 0x2C, 0x2C), t.SolidSurface);
        Assert.AreEqual(Rgba(255, 255, 255, .08), t.SurfaceStroke);
        Assert.AreEqual(Rgba(0, 0, 0, .26), t.Shadow);
        Assert.AreEqual(Rgba(255, 255, 255, .0512), t.SettingsRowFill);
        Assert.AreEqual(Rgba(0, 0, 0, .1), t.SettingsRowStroke);
        Assert.AreEqual(Rgba(255, 255, 255, .0605), t.ControlFill);
        Assert.AreEqual(Rgba(255, 255, 255, .093), t.ControlStroke);
        Assert.AreEqual(Rgba(255, 255, 255, .093), t.ControlStrokeBottom, "The dark control stroke has one value.");
        Assert.AreEqual(Rgba(255, 255, 255, .0419), t.ControlFillDisabled);
        Assert.AreEqual(Rgba(255, 255, 255, .0605), t.SubtleHover);
        Assert.AreEqual(Rgba(255, 255, 255, .0419), t.SubtlePressed);
        Assert.AreEqual(Rgba(255, 255, 255, .544), t.ProgressTrack);
        Assert.AreEqual(Rgba(255, 255, 255, .22), t.GaugeRingTrack);
        Assert.AreEqual(Color.FromArgb(0xFC, 0xE1, 0x00), t.Caution);
        Assert.AreEqual(Rgba(255, 255, 255, .0837), t.Divider);
        Assert.AreEqual(Color.FromArgb(0x00, 0x00, 0x00), t.TextOnAccent, "Text on accent: dark #000000.");
        Assert.AreEqual(Rgba(0, 0, 0, .7), t.FocusInner, "Focus inner stroke: dark black at 70%.");
        Assert.AreEqual(30, t.AcrylicBlur);
        Assert.IsTrue(t.Dark);
    }

    // The alpha of each value is the table's figure times 255, half away from zero: 0.896 gives 228, 0.446 gives 114, 0.544 gives 139.
    [TestMethod]
    [DataRow(.896, 228)]
    [DataRow(.606, 155)]
    [DataRow(.446, 114)]
    [DataRow(.361, 92)]
    [DataRow(.544, 139)]
    [DataRow(.85, 217)]
    [DataRow(.7, 179)]
    [DataRow(.0578, 15)]
    [DataRow(.0605, 15)]
    [DataRow(.16, 41)]
    [DataRow(.22, 56)]
    public void AlphaBytesAreRoundedHalfAwayFromZero(double alpha, int expected) =>
        Assert.AreEqual(expected, DesignTokens.AlphaByte(alpha));

    // The stale values and the read times use Text tertiary, the one token the earlier gauge "Tertiary" (#707070, #9D9D9D) and the
    // card's 55% opacity stale style became.
    [TestMethod]
    public void TextTertiaryIsTheOnlyStaleInkTheOldGreysAreGone()
    {
        Assert.AreEqual(114, DesignTokens.For(dark: false, highContrast: false).TextTertiary.A);
        Assert.AreEqual(139, DesignTokens.For(dark: true, highContrast: false).TextTertiary.A);
        Assert.AreNotEqual(Color.FromArgb(0x70, 0x70, 0x70), DesignTokens.For(dark: false, highContrast: false).TextTertiary);
        Assert.AreNotEqual(Color.FromArgb(0x9D, 0x9D, 0x9D), DesignTokens.For(dark: true, highContrast: false).TextTertiary);

        foreach (bool dark in new[] { false, true })
        {
            DesignTokens t = DesignTokens.For(dark, highContrast: false);
            var colours = CardColours.For(t, Color.Red);
            Assert.AreEqual(t.TextTertiary, colours.TextTertiary);
            GaugePalette palette = GaugePalette.Create(lightTheme: !dark, Color.Red, highContrast: false, dark ? Color.White : Color.Black);
            Assert.AreEqual(t.TextTertiary, palette.Tertiary, "The gauge's stale ink is the card's.");
        }
    }

    // Under high contrast every token is a system colour: surface Window, text WindowText, secondary text also WindowText, stale and
    // disabled GrayText, strokes WindowText, buttons ButtonFace and ButtonText, caution WindowText, text on accent HighlightText,
    // links HotTrack, and no shadow and no blur.
    [TestMethod]
    public void HighContrastIsSystemColoursOnly()
    {
        DesignTokens t = DesignTokens.For(dark: false, highContrast: true, Hc);

        Assert.IsTrue(t.HighContrast);
        Assert.AreEqual(Hc.WindowText, t.TextPrimary);
        Assert.AreEqual(Hc.WindowText, t.TextSecondary);
        Assert.AreEqual(Hc.GrayText, t.TextTertiary);
        Assert.AreEqual(Hc.GrayText, t.TextDisabled);
        Assert.AreEqual(Hc.Window, t.AcrylicTint);
        Assert.AreEqual(Hc.Window, t.SolidSurface);
        Assert.AreEqual(Hc.WindowText, t.SurfaceStroke);
        Assert.AreEqual(Color.Transparent, t.Shadow, "No shadow.");
        Assert.AreEqual(0, t.AcrylicBlur, "No backdrop.");
        Assert.AreEqual(Hc.WindowText, t.SettingsRowStroke);
        Assert.AreEqual(Hc.ButtonFace, t.ControlFill);
        Assert.AreEqual(Hc.ButtonText, t.ControlStroke);
        Assert.AreEqual(Hc.WindowText, t.Caution, "Caution becomes WindowText.");
        Assert.AreEqual(Hc.HighlightText, t.TextOnAccent);
        Assert.AreEqual(Hc.Window, t.FocusInner);
        Assert.AreEqual(Hc.HotTrack, t.Link);
        foreach (Color c in new[] { t.TextPrimary, t.TextTertiary, t.SolidSurface, t.SurfaceStroke, t.SettingsRowFill, t.ControlFill, t.ControlStroke, t.ProgressTrack, t.GaugeRingTrack, t.Divider })
        {
            Assert.AreEqual(255, c.A, "No transparency under high contrast: " + c);
        }
    }

    [TestMethod]
    public void TheCardsColoursAreTheTokensAndTheAccentUnderEveryTheme()
    {
        Color accent = Color.FromArgb(0x00, 0x5F, 0xB8);
        foreach ((DesignTokens t, Color expectedAccent) in new[]
        {
            (DesignTokens.For(false, false), accent),
            (DesignTokens.For(true, false), accent),
            (DesignTokens.For(false, true, Hc), Hc.Highlight),
        })
        {
            var c = CardColours.For(t, expectedAccent);
            Assert.AreEqual(t.TextPrimary, c.Text);
            Assert.AreEqual(t.TextSecondary, c.TextSecondary);
            Assert.AreEqual(t.TextTertiary, c.TextTertiary);
            Assert.AreEqual(t.TextDisabled, c.TextDisabled);
            Assert.AreEqual(t.TextOnAccent, c.OnAccent);
            Assert.AreEqual(t.Caution, c.Caution);
            Assert.AreEqual(t.Divider, c.Divider);
            Assert.AreEqual(t.ProgressTrack, c.Track);
            Assert.AreEqual(t.ControlFill, c.ControlFill);
            Assert.AreEqual(t.ControlStroke, c.ControlStroke);
            Assert.AreEqual(t.ControlStrokeBottom, c.ControlStrokeBottom);
            Assert.AreEqual(t.ControlFillDisabled, c.ControlFillDisabled);
            Assert.AreEqual(t.SubtleHover, c.SubtleHover);
            Assert.AreEqual(t.SubtlePressed, c.SubtlePressed);
            Assert.AreEqual(t.SettingsRowFill, c.RowFill);
            Assert.AreEqual(t.SettingsRowStroke, c.RowStroke);
            Assert.AreEqual(t.SurfaceStroke, c.SurfaceStroke);
            Assert.AreEqual(expectedAccent, c.Accent);
            Assert.AreEqual(t.HighContrast, c.HighContrast);
        }
    }

    [TestMethod]
    public void TheGaugePaletteIsTheTokens()
    {
        foreach (bool light in new[] { true, false })
        {
            DesignTokens t = DesignTokens.For(!light, false);
            GaugePalette p = GaugePalette.Create(light, Color.FromArgb(1, 2, 3), highContrast: false, light ? Color.Black : Color.White);
            Assert.AreEqual(t.TextPrimary, p.Ink);
            Assert.AreEqual(t.GaugeRingTrack, p.Track);
            Assert.AreEqual(t.Caution, p.Caution);
            Assert.AreEqual(t.SubtleHover, p.HoverFill);
            Assert.AreEqual(t.TextTertiary, p.Tertiary);
            Assert.AreEqual(t.TextDisabled, p.Disabled);
            Assert.AreEqual(Color.FromArgb(1, 2, 3), p.Accent);
            Assert.AreEqual(GaugePalette.IdleAlpha, p.IdleFill.A);
        }

        GaugePalette hc = GaugePalette.Create(lightTheme: true, Color.Red, highContrast: true, Hc.WindowText, Hc);
        Assert.AreEqual(Hc.WindowText, hc.Ink);
        Assert.AreEqual(Hc.Highlight, hc.Accent, "The ring's arc is Highlight.");
        Assert.AreEqual(Hc.GrayText, hc.Tertiary);
        Assert.AreEqual(Hc.GrayText, hc.Disabled);
        Assert.AreEqual(Hc.WindowText, hc.Caution);
        Assert.AreEqual(Hc.GrayText, hc.Track);
    }

    [TestMethod]
    public void TheFocusVisualsInnerStrokeIsTheTablesAndTheOuterIsTextPrimary()
    {
        FocusPalette light = FocusPalette.For(DesignTokens.For(false, false));
        Assert.AreEqual(Rgba(0, 0, 0, .896), light.OuterStroke);
        Assert.AreEqual(Color.FromArgb(0xFF, 0xFF, 0xFF), light.InnerStroke);
        FocusPalette dark = FocusPalette.For(DesignTokens.For(true, false));
        Assert.AreEqual(Color.FromArgb(0xFF, 0xFF, 0xFF), dark.OuterStroke);
        Assert.AreEqual(Rgba(0, 0, 0, .7), dark.InnerStroke);
        FocusPalette hc = FocusPalette.For(DesignTokens.For(false, true, Hc));
        Assert.AreEqual(Hc.WindowText, hc.OuterStroke);
        Assert.AreEqual(Hc.Window, hc.InnerStroke);
    }
}
