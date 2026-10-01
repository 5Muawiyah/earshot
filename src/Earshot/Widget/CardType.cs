using Earshot.Popup;

namespace Earshot.Widget;

// The fonts one card draws with, for its display scale and the system text size. Callers that already work in
// pixels at the display scale (a 14 px label is Scale(14, dpi)) ask Font(pixelSize, bold) and get the Segoe UI
// Variable instance for that size at the text size; callers that name a style ask Role.
//
// pixelSize is the size at the display scale, before the text size: Font multiplies by the text size itself, so a
// caller never scales twice. The optical instance follows the size in effective pixels (Small up to 12, Text
// above it up to 18, Display from 20), the same rule the type ramp's roles use.
internal sealed class CardType(int dpi, double textScale)
{
    public int Dpi { get; } = dpi > 0 ? dpi : 96;

    public double TextScale { get; } = Math.Clamp(double.IsFinite(textScale) ? textScale : 1.0, TypeRamp.MinTextScale, TypeRamp.MaxTextScale);

    // The same look as before the text size existed: 96 dpi and 100%.
    public static CardType Plain { get; } = new(96, 1.0);

    // A font of pixelSize at the display scale (before the text size), regular or semibold.
    public Font Font(int pixelSize, bool bold)
    {
        OpticalSize optical = TypeRamp.OpticalFor(pixelSize * 96.0 / Dpi);
        int px = Math.Max(1, (int)Math.Round(Math.Max(1, pixelSize) * TextScale, MidpointRounding.AwayFromZero));
        return TypeRamp.FontOfSize(optical, bold, px);
    }

    // The font for a named style.
    public Font Role(TypeRole role) => TypeRamp.Font(role, Dpi, TextScale);

    // The size in pixels a named style draws at here.
    public int SizeOf(TypeRole role) => TypeRamp.SizePx(role, Dpi, TextScale);

    // The line height in pixels of a named style here.
    public int LineOf(TypeRole role) => TypeRamp.LineHeight(role, Dpi, TextScale);
}

// How a height that holds text grows with the text size, so a row never clips what it carries. Widths, paddings,
// icon boxes and the toggle are not text and scale with the display only.
internal static class TextFit
{
    // A height of at96 at the display scale, grown in proportion to the text size. At 100% it is the plain
    // scaled value.
    public static int Grow(int at96, int dpi, double textScale) =>
        Math.Max(
            CardPlacement.Scale(at96, dpi),
            (int)Math.Round(at96 * (dpi > 0 ? dpi : 96) / 96.0 * Math.Clamp(double.IsFinite(textScale) ? textScale : 1.0, TypeRamp.MinTextScale, TypeRamp.MaxTextScale), MidpointRounding.AwayFromZero));

    // A control's height: its design minimum, or one line of role plus padding when that is taller.
    public static int Fit(int minAt96, TypeRole role, int paddingAt96, int dpi, double textScale) =>
        Math.Max(CardPlacement.Scale(minAt96, dpi), TypeRamp.LineHeight(role, dpi, textScale) + CardPlacement.Scale(paddingAt96, dpi));
}
