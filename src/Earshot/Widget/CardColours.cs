using Earshot.Popup;

namespace Earshot.Widget;

// Where the card's accent colour comes from. The card asks for it every time it paints, so a change of the
// system accent colour shows on the next paint without the card being rebuilt. lightTheme is the card's own
// theme (the Windows app-mode setting picked by the taskbar ink). The member is shaped like the system accent
// service's, so that service can be handed to the card as it is.
internal interface ICardAccentSource
{
    Color AccentFor(bool lightTheme);
}

// The accent the design uses when the system's own is not asked for: the Windows default blue, darker on a
// light card and lighter on a dark one.
internal sealed class DefaultCardAccent : ICardAccentSource
{
    public static readonly Color Light = Color.FromArgb(0x00, 0x5F, 0xB8);
    public static readonly Color Dark = Color.FromArgb(0x60, 0xCD, 0xFF);

    public static DefaultCardAccent Instance { get; } = new();

    public Color AccentFor(bool lightTheme) => lightTheme ? Light : Dark;
}

// Hands the system accent service to the card as its accent source. The card asks for the colour at every paint,
// so a change of the owner's accent shows on the next one.
internal sealed class AccentColoursSource(IAccentColours colours) : ICardAccentSource
{
    public Color AccentFor(bool lightTheme) => colours.AccentFor(lightTheme);
}

// The colours one card view is painted with, each a token of the design: text, secondary text, tertiary text
// (a placeholder, "Not set"), accent and the ink on it, caution, the footer fill, divider, track and the standard control's fill and stroke. Under a
// high-contrast theme every one of them is a system colour.
internal sealed record CardColours(
    Color Text,
    Color TextSecondary,
    Color TextTertiary,
    Color Accent,
    Color OnAccent,
    Color Caution,
    Color Footer,
    Color Divider,
    Color Track,
    Color ControlFill,
    Color ControlStroke,
    Color SubtleHover,
    bool HighContrast)
{
    public static CardColours For(bool dark, CardPalette palette, Color accent)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.HighContrast)
        {
            return new CardColours(
                palette.Title, palette.Status, palette.Status, SystemColors.Highlight, SystemColors.HighlightText, palette.Title, palette.Background,
                palette.Border, palette.Border, palette.Background, palette.Border, palette.Border, HighContrast: true);
        }

        Color onAccent = accent.GetBrightness() > 0.55f ? Color.Black : Color.White;
        return dark
            ? new CardColours(
                Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xC8, 0xC8, 0xC8), Color.FromArgb(0x9D, 0x9D, 0x9D), accent, onAccent, Color.FromArgb(0xFC, 0xE1, 0x00),
                Color.FromArgb(0x20, 0x20, 0x20), Color.FromArgb(15, 255, 255, 255), Color.FromArgb(41, 255, 255, 255),
                Color.FromArgb(15, 255, 255, 255), Color.FromArgb(23, 255, 255, 255), Color.FromArgb(31, 128, 128, 128), HighContrast: false)
            : new CardColours(
                Color.FromArgb(0x1B, 0x1B, 0x1B), Color.FromArgb(0x5D, 0x5D, 0x5D), Color.FromArgb(0x70, 0x70, 0x70), accent, onAccent, Color.FromArgb(0x9D, 0x5D, 0x00),
                Color.FromArgb(0xF3, 0xF3, 0xF3), Color.FromArgb(15, 0, 0, 0), Color.FromArgb(36, 0, 0, 0),
                Color.FromArgb(0xFD, 0xFD, 0xFD), Color.FromArgb(26, 0, 0, 0), Color.FromArgb(31, 128, 128, 128), HighContrast: false);
    }
}
