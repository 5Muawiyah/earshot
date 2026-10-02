namespace Earshot.Widget;

// The system colours a high-contrast theme paints with, handed in so the token table is pure (SystemColors is
// not available off Windows, and a test can give each theme its own).
// https://learn.microsoft.com/en-us/dotnet/api/system.drawing.systemcolors
internal readonly record struct HighContrastColours(
    Color Window, Color WindowText, Color GrayText, Color Highlight, Color HighlightText, Color ButtonFace, Color ButtonText, Color HotTrack)
{
    public static HighContrastColours FromSystem() => new(
        SystemColors.Window, SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight, SystemColors.HighlightText,
        SystemColors.ButtonFace, SystemColors.ControlText, SystemColors.HotTrack);
}

// Every colour of the design's Colour table, by name, for one theme. This is the one place the values are
// written; the card (CardColours) and the gauge (GaugePalette) read them from here. A value written "rgba(r,g,b,a)"
// in the table is a colour with that alpha, the alpha rounded half away from zero to a byte.
//
// Light and dark are the table's two columns. Under a high-contrast theme every token is a system colour
// (HighContrastColours): surface Window, text WindowText, secondary text also WindowText, stale and disabled text
// GrayText, strokes WindowText, buttons ButtonFace / ButtonText, caution WindowText, no shadow and no
// transparency. The table gives no high-contrast value for the hover and pressed fills, the progress and gauge tracks
// or the divider; those are design choices: a hover or pressed fill is the button face, and the tracks and the divider are
// GrayText, so nothing is left to a transparency the theme turned off.
internal readonly record struct DesignTokens(
    Color TextPrimary,
    Color TextSecondary,
    Color TextTertiary,
    Color TextDisabled,
    Color AcrylicTint,
    Color SolidSurface,
    Color SurfaceStroke,
    Color Shadow,
    Color SettingsRowFill,
    Color SettingsRowStroke,
    Color ControlFill,
    Color ControlStroke,
    Color ControlStrokeBottom,
    Color ControlFillDisabled,
    Color SubtleHover,
    Color SubtlePressed,
    Color ProgressTrack,
    Color GaugeRingTrack,
    Color Caution,
    Color Divider,
    Color TextOnAccent,
    Color FocusInner,
    Color Link,
    int AcrylicBlur,
    bool HighContrast,
    bool Dark)
{
    // The alpha of an "rgba(r,g,b,a)" value as a byte.
    public static int AlphaByte(double alpha) => (int)Math.Round(255 * alpha, MidpointRounding.AwayFromZero);

    private static Color Rgba(int r, int g, int b, double alpha) => Color.FromArgb(AlphaByte(alpha), r, g, b);

    // The blur radius the acrylic tint is painted over, from the table ("blur 30").
    public const int BlurRadius = 30;

    public static DesignTokens Light { get; } = new(
        TextPrimary: Rgba(0, 0, 0, .896),
        TextSecondary: Rgba(0, 0, 0, .606),
        TextTertiary: Rgba(0, 0, 0, .446),
        TextDisabled: Rgba(0, 0, 0, .361),
        AcrylicTint: Rgba(252, 252, 252, .85),
        SolidSurface: Color.FromArgb(0xF9, 0xF9, 0xF9),
        SurfaceStroke: Rgba(0, 0, 0, .0578),
        Shadow: Rgba(0, 0, 0, .14),
        SettingsRowFill: Rgba(255, 255, 255, .7),
        SettingsRowStroke: Rgba(0, 0, 0, .0578),
        ControlFill: Rgba(255, 255, 255, .7),
        ControlStroke: Rgba(0, 0, 0, .0578),
        ControlStrokeBottom: Rgba(0, 0, 0, .1622),
        ControlFillDisabled: Rgba(249, 249, 249, .3),
        SubtleHover: Rgba(0, 0, 0, .0373),
        SubtlePressed: Rgba(0, 0, 0, .0241),
        ProgressTrack: Rgba(0, 0, 0, .446),
        GaugeRingTrack: Rgba(0, 0, 0, .16),
        Caution: Color.FromArgb(0x9D, 0x5D, 0x00),
        Divider: Rgba(0, 0, 0, .0803),
        TextOnAccent: Color.FromArgb(0xFF, 0xFF, 0xFF),
        FocusInner: Color.FromArgb(0xFF, 0xFF, 0xFF),
        Link: Color.FromArgb(0x00, 0x5F, 0xB8),
        AcrylicBlur: BlurRadius,
        HighContrast: false,
        Dark: false);

    public static DesignTokens DarkTheme { get; } = new(
        TextPrimary: Color.FromArgb(0xFF, 0xFF, 0xFF),
        TextSecondary: Rgba(255, 255, 255, .786),
        TextTertiary: Rgba(255, 255, 255, .544),
        TextDisabled: Rgba(255, 255, 255, .363),
        AcrylicTint: Rgba(44, 44, 44, .82),
        SolidSurface: Color.FromArgb(0x2C, 0x2C, 0x2C),
        SurfaceStroke: Rgba(255, 255, 255, .08),
        Shadow: Rgba(0, 0, 0, .26),
        SettingsRowFill: Rgba(255, 255, 255, .0512),
        SettingsRowStroke: Rgba(0, 0, 0, .1),
        ControlFill: Rgba(255, 255, 255, .0605),
        ControlStroke: Rgba(255, 255, 255, .093),
        ControlStrokeBottom: Rgba(255, 255, 255, .093),
        ControlFillDisabled: Rgba(255, 255, 255, .0419),
        SubtleHover: Rgba(255, 255, 255, .0605),
        SubtlePressed: Rgba(255, 255, 255, .0419),
        ProgressTrack: Rgba(255, 255, 255, .544),
        GaugeRingTrack: Rgba(255, 255, 255, .22),
        Caution: Color.FromArgb(0xFC, 0xE1, 0x00),
        Divider: Rgba(255, 255, 255, .0837),
        TextOnAccent: Color.FromArgb(0x00, 0x00, 0x00),
        FocusInner: Rgba(0, 0, 0, .7),
        Link: Color.FromArgb(0x60, 0xCD, 0xFF),
        AcrylicBlur: BlurRadius,
        HighContrast: false,
        Dark: true);

    // The table's column for a theme. The table has one control stroke for dark (the bottom edge is the same colour).
    public static DesignTokens For(bool dark, bool highContrast, HighContrastColours system)
    {
        if (!highContrast)
        {
            return dark ? DarkTheme : Light;
        }

        return new DesignTokens(
            TextPrimary: system.WindowText,
            TextSecondary: system.WindowText,
            TextTertiary: system.GrayText,
            TextDisabled: system.GrayText,
            AcrylicTint: system.Window,
            SolidSurface: system.Window,
            SurfaceStroke: system.WindowText,
            Shadow: Color.Transparent,
            SettingsRowFill: system.Window,
            SettingsRowStroke: system.WindowText,
            ControlFill: system.ButtonFace,
            ControlStroke: system.ButtonText,
            ControlStrokeBottom: system.ButtonText,
            ControlFillDisabled: system.ButtonFace,
            SubtleHover: system.ButtonFace,
            SubtlePressed: system.ButtonFace,
            ProgressTrack: system.GrayText,
            GaugeRingTrack: system.GrayText,
            Caution: system.WindowText,
            Divider: system.GrayText,
            TextOnAccent: system.HighlightText,
            FocusInner: system.Window,
            Link: system.HotTrack,
            AcrylicBlur: 0,
            HighContrast: true,
            Dark: false);
    }

    public static DesignTokens For(bool dark, bool highContrast) =>
        For(dark, highContrast, highContrast ? HighContrastColours.FromSystem() : default);
}
