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

// The colours one card view is painted with. Every one of them is a named token of the design (DesignTokens, the one
// place the values are written) or the accent and the text on it. Under a high-contrast theme every one of them is a
// system colour. The names the card's painters use are kept; each is a token under another name:
//   Footer         the settings row fill (the footer is a surface the way a row is)
//   Track          the progress track
//   ControlFill / ControlStroke / SubtleHover   the same tokens
internal sealed record CardColours(DesignTokens Tokens, Color Accent)
{
    public Color Text => Tokens.TextPrimary;

    public Color TextSecondary => Tokens.TextSecondary;

    // Stale values and read times; a placeholder.
    public Color TextTertiary => Tokens.TextTertiary;

    public Color TextDisabled => Tokens.TextDisabled;

    public Color OnAccent => Tokens.TextOnAccent;

    public Color Caution => Tokens.Caution;

    public Color Footer => Tokens.SettingsRowFill;

    public Color Divider => Tokens.Divider;

    public Color Track => Tokens.ProgressTrack;

    public Color ControlFill => Tokens.ControlFill;

    public Color ControlStroke => Tokens.ControlStroke;

    public Color ControlStrokeBottom => Tokens.ControlStrokeBottom;

    public Color ControlFillDisabled => Tokens.ControlFillDisabled;

    public Color SubtleHover => Tokens.SubtleHover;

    public Color SubtlePressed => Tokens.SubtlePressed;

    public Color RowFill => Tokens.SettingsRowFill;

    public Color RowStroke => Tokens.SettingsRowStroke;

    public Color SurfaceStroke => Tokens.SurfaceStroke;

    public bool HighContrast => Tokens.HighContrast;

    public bool Dark => Tokens.Dark;

    // The focus visual's colours for this theme.
    public FocusPalette Focus => FocusPalette.For(Dark, HighContrast);

    public static CardColours For(bool dark, CardPalette palette, Color accent)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return palette.HighContrast
            ? For(DesignTokens.For(false, true), SystemColors.Highlight)
            : For(DesignTokens.For(dark, false), accent);
    }

    public static CardColours For(DesignTokens tokens, Color accent) => new(tokens, accent);
}
