namespace Earshot.Widget;

// The colours the gauge draws with, for one theme, all named tokens of the design (DesignTokens): ink is Text primary,
// Tertiary is Text tertiary, Disabled is Text disabled, Track is Gauge ring track, Caution is Caution and
// the hover fill is Subtle hover. The ring's fill is the owner's Windows accent colour, not the text colour.
//
//   token        light                 dark
//   ink          rgba(0,0,0,.896)      #FFFFFF
//   tertiary     rgba(0,0,0,.446)      rgba(255,255,255,.544)
//   disabled     rgba(0,0,0,.361)      rgba(255,255,255,.363)
//   track        rgba(0,0,0,.16)       rgba(255,255,255,.22)
//   caution      #9D5D00               #FCE100
//   hover        rgba(0,0,0,.0373)     rgba(255,255,255,.0605)
//
// A last reading or an estimate is drawn in the card's own tertiary token, so it looks the same on the gauge as on the card.
//
// In a high contrast theme the system's own text colour draws the ink, caution and the arc is Highlight, stale and
// disabled are GrayText and the track is GrayText (see DesignTokens).
internal readonly record struct GaugePalette(
    Color Ink, Color Track, Color Caution, Color Accent, Color HoverFill, Color IdleFill, Color Tertiary, Color Disabled)
{
    // The hover fill of a window nobody is pointing at: alpha 1 of 255, invisible on any background but not
    // 0, so the whole gauge is one clickable target (a layered window passes a click through where its alpha
    // is 0).
    public const byte IdleAlpha = 1;

    public static GaugePalette Create(bool lightTheme, Color accent, bool highContrast, Color highContrastInk) =>
        Create(lightTheme, accent, highContrast, highContrastInk, highContrast ? HighContrastColours.FromSystem() : default);

    // highContrastInk is the taskbar's own ink under a high-contrast theme; system says the rest.
    public static GaugePalette Create(bool lightTheme, Color accent, bool highContrast, Color highContrastInk, HighContrastColours system)
    {
        DesignTokens t = DesignTokens.For(!lightTheme, highContrast, system);
        if (highContrast)
        {
            return new GaugePalette(
                highContrastInk, t.GaugeRingTrack, highContrastInk, system.Highlight,
                t.SubtleHover, Color.FromArgb(IdleAlpha, highContrastInk), t.TextTertiary, t.TextDisabled);
        }

        Color baseInk = lightTheme ? Color.Black : Color.White;
        return new GaugePalette(
            t.TextPrimary, t.GaugeRingTrack, t.Caution, accent, t.SubtleHover, Color.FromArgb(IdleAlpha, baseInk), t.TextTertiary, t.TextDisabled);
    }
}
