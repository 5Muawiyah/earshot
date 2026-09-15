using Earshot.Icons;

namespace Earshot.Popup;

// The colours one card is painted with. HighContrast is true when they are the system colours.
internal sealed record CardPalette(Color Background, Color Title, Color Status, Color Border, bool HighContrast);

// Picks the card colours so the card matches the taskbar it sits on.
//
//   high contrast on       SystemColors.Window background, SystemColors.WindowText for text and border
//   white taskbar ink      dark card (the taskbar is dark)
//   black taskbar ink      light card
//
// The ink comes from ThemeReader, the same reading the tray icon uses, so the card and the icon always
// agree. The two fixed palettes are design choices close to the Windows 11 flyout surfaces, not values
// read from the system; every text colour keeps a contrast ratio of at least 4.5:1 with its background.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.systeminformation.highcontrast
// https://learn.microsoft.com/en-us/dotnet/api/system.drawing.systemcolors
internal static class CardTheme
{
    public static readonly CardPalette Dark = new(
        Background: Color.FromArgb(0x2B, 0x2B, 0x2B),
        Title: Color.FromArgb(0xFF, 0xFF, 0xFF),
        Status: Color.FromArgb(0xCF, 0xCF, 0xCF),
        Border: Color.FromArgb(0x4A, 0x4A, 0x4A),
        HighContrast: false);

    public static readonly CardPalette Light = new(
        Background: Color.FromArgb(0xF9, 0xF9, 0xF9),
        Title: Color.FromArgb(0x1A, 0x1A, 0x1A),
        Status: Color.FromArgb(0x5A, 0x5A, 0x5A),
        Border: Color.FromArgb(0xD0, 0xD0, 0xD0),
        HighContrast: false);

    // The palette for the current theme, read now.
    public static CardPalette Current(ThemeReader theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return For(SystemInformation.HighContrast, theme.Ink(), SystemColors.Window, SystemColors.WindowText);
    }

    internal static CardPalette For(bool highContrast, Color taskbarInk, Color window, Color windowText)
    {
        if (highContrast)
        {
            return new CardPalette(window, windowText, windowText, windowText, HighContrast: true);
        }

        // Light ink is drawn on a dark taskbar.
        return taskbarInk.GetBrightness() >= 0.5f ? Dark : Light;
    }
}
