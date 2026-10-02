namespace Earshot.Widget;

// The colours the gauge draws with, for one theme: text, track, caution and hover. The ring's fill is the
// owner's Windows accent colour, not the text colour.
//
//   token        light                dark
//   ink          #1B1B1B              #FFFFFF
//   track        black at 24%         white at 30%
//   caution      #9D5D00              #FCE100
//   hover        black at 5%          white at 7%
//   tertiary     #707070              #9D9D9D
//
// Tertiary is the card's own tertiary text token (CardColours), so a last reading or an estimate is drawn in the same
// ink on the gauge as on the card.
//
// In a high contrast theme the system's own text colour draws everything, so nothing is left to a theme the
// owner did not choose.
internal readonly record struct GaugePalette(Color Ink, Color Track, Color Caution, Color Accent, Color HoverFill, Color IdleFill, Color Tertiary)
{
    // The hover fill of a window nobody is pointing at: alpha 1 of 255, invisible on any background but not
    // 0, so the whole gauge is one clickable target (a layered window passes a click through where its alpha
    // is 0).
    public const byte IdleAlpha = 1;

    public static GaugePalette Create(bool lightTheme, Color accent, bool highContrast, Color highContrastInk)
    {
        if (highContrast)
        {
            return new GaugePalette(
                highContrastInk, WithAlpha(highContrastInk, 0.30), highContrastInk, highContrastInk,
                WithAlpha(highContrastInk, 0.12), Color.FromArgb(IdleAlpha, highContrastInk), highContrastInk);
        }

        Color ink = lightTheme ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
        Color baseInk = lightTheme ? Color.Black : Color.White;
        return new GaugePalette(
            ink,
            WithAlpha(baseInk, lightTheme ? 0.24 : 0.30),
            lightTheme ? Color.FromArgb(0x9D, 0x5D, 0x00) : Color.FromArgb(0xFC, 0xE1, 0x00),
            accent,
            WithAlpha(baseInk, lightTheme ? 0.05 : 0.07),
            Color.FromArgb(IdleAlpha, baseInk),
            lightTheme ? Color.FromArgb(0x70, 0x70, 0x70) : Color.FromArgb(0x9D, 0x9D, 0x9D));
    }

    private static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((int)Math.Round(255 * alpha, MidpointRounding.AwayFromZero), c);
}
