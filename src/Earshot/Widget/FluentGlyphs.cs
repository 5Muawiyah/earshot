using System.Drawing.Text;
using Earshot.Interop;

namespace Earshot.Widget;

// The Segoe Fluent Icons code points the card and its pages use, and the one place that draws them.
//
// The font is a system font that ships with Windows 11 and is never bundled: its licence allows it on Windows
// only. Windows 10 and some server images have Segoe MDL2 Assets instead, which carries the same code points
// for the older glyphs; when neither is installed no icon is drawn and the label stays.
// https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font
//
// A glyph is a decoration. Where it stands alone (the gear, the back arrow, the clear cross) the target has a
// tooltip and an accessible name, so the icon is never the only carrier of meaning.
internal static class FluentGlyphs
{
    public const string FluentFamily = "Segoe Fluent Icons";
    public const string MdlFamily = "Segoe MDL2 Assets";

    public const char Settings = '\uE713';
    public const char Refresh = '\uE72C';
    public const char Back = '\uE72B';
    public const char OnThisPc = '\uE977';
    public const char ReadTime = '\uE823';
    public const char Bolt = '\uE945';
    public const char BluetoothOff = '\uE702';
    public const char WhatsNew = '\uE946';
    public const char OpenExternal = '\uE8A7';
    public const char ChevronRight = '\uE76C';

    // The chevrons are 12 epx glyphs.
    public const int ChevronSizeAt96 = 12;

    public const char Volume = '\uE767';
    public const char History = '\uE81C';
    public const char Copy = '\uE8C8';
    public const char More = '\uE712';
    public const char Download = '\uE896';
    public const char DockBottom = '\uE90E';
    public const char TvMonitor = '\uE7F4';
    public const char Sort = '\uE8CB';
    public const char CellPhone = '\uE8EA';
    public const char Earbud = '\uF4C0';
    public const char Headphone = '\uE7F6';
    public const char Pause = '\uE769';
    public const char Tiles = '\uECA5';
    public const char Ringer = '\uEA8F';
    public const char Mouse = '\uE962';
    public const char PowerButton = '\uE7E8';
    public const char MicOff = '\uEC54';
    public const char Keyboard = '\uE765';
    public const char Cancel = '\uE711';
    public const char Sync = '\uE895';
    public const char Repair = '\uE90F';
    public const char Warning = '\uE7BA';
    public const char Preview = '\uE8A0';
    public const char ChevronDown = '\uE70D';
    public const char ChevronUp = '\uE70E';
    public const char CheckMark = '\uE73E';

    // The family to draw icons with, from the names the system lists: Segoe Fluent Icons, else Segoe MDL2 Assets,
    // else null (no icon). Names are compared whole and ignoring case; GDI+ cuts long family names at 31
    // characters, and neither name here is that long.
    public static string? ChooseFamily(IEnumerable<string> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var names = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        if (names.Contains(FluentFamily))
        {
            return FluentFamily;
        }

        return names.Contains(MdlFamily) ? MdlFamily : null;
    }

    private static readonly object Gate = new();
    private static string? _family;
    private static bool _resolved;

    // The family on this PC, found once. Null when neither font is installed.
    public static string? Family
    {
        get
        {
            lock (Gate)
            {
                if (!_resolved)
                {
                    using var fonts = new InstalledFontCollection();
                    _family = ChooseFamily(fonts.Families.Select(f => f.Name));
                    _resolved = true;
                }

                return _family;
            }
        }
    }

    // True when family has a glyph for codePoint (GDI answers 0xFFFF for a missing one).
    public static bool HasGlyph(string family, char codePoint)
    {
        ArgumentNullException.ThrowIfNull(family);
        using var font = new Font(family, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        nint hfont = font.ToHfont();
        nint hdc = LayeredWindow.CreateCompatibleDC(0);
        if (hdc == 0)
        {
            _ = LayeredWindow.DeleteObject(hfont);
            throw new InvalidOperationException("CreateCompatibleDC failed with Win32 error " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        nint previous = LayeredWindow.SelectObject(hdc, hfont);
        try
        {
            uint result = NativeMethods.GetGlyphIndices(hdc, codePoint.ToString(), 1, out ushort index, NativeMethods.GGI_MARK_NONEXISTING_GLYPHS);
            return result == 1 && index != 0xFFFF;
        }
        finally
        {
            _ = LayeredWindow.SelectObject(hdc, previous);
            _ = LayeredWindow.DeleteDC(hdc);
            _ = LayeredWindow.DeleteObject(hfont);
        }
    }

    // The earbud glyph is newer than the rest; where the installed font lacks it the headphone stands in.
    public static char EarbudOrHeadphone(Func<char, bool> hasGlyph)
    {
        ArgumentNullException.ThrowIfNull(hasGlyph);
        return hasGlyph(Earbud) ? Earbud : Headphone;
    }

    // The glyph to draw for a code point a row names: the earbud is the headphone where the font has no earbud, which only the
    // painter asks the font about, so a layout never needs the font.
    public static char Resolve(char codePoint) => codePoint == Earbud ? EarbudForThisPc() : codePoint;

    private static char? _earbud;

    // The earbud, or the headphone when this PC's font has no earbud.
    public static char EarbudForThisPc()
    {
        if (_earbud is { } known)
        {
            return known;
        }

        string? family = Family;
        char chosen = family is null ? Headphone : EarbudOrHeadphone(c => HasGlyph(family, c));
        _earbud = chosen;
        return chosen;
    }
}

internal static partial class CardPaint
{
    // The 16 epx glyph size, which is on the icon font's list of optimal sizes (16, 20, 24 at 100, 125, 150%).
    public const int GlyphSizeAt96 = 16;

    // Draws one icon centred in bounds, 16 epx at 96 dpi and scaled with dpi. Draws nothing when no icon font is
    // installed. Text rendering is GDI+ like the rest of the card, so the glyph keeps its alpha on the translucent
    // backdrop.
    public static void Glyph(Graphics g, char codePoint, Rectangle bounds, Color colour, int dpi) =>
        _ = TryGlyph(g, codePoint, bounds, colour, dpi);

    // The same, answering false when no icon font is installed, so a caller can draw its own shape instead.
    public static bool TryGlyph(Graphics g, char codePoint, Rectangle bounds, Color colour, int dpi) =>
        TryGlyph(g, codePoint, bounds, colour, dpi, GlyphSizeAt96, 1.0);

    // A glyph of sizeAt96 epx at the display scale, grown by the text size (the card's icons follow it).
    public static bool TryGlyph(Graphics g, char codePoint, Rectangle bounds, Color colour, int dpi, int sizeAt96, double textScale)
    {
        ArgumentNullException.ThrowIfNull(g);
        string? family = FluentGlyphs.Family;
        if (family is null)
        {
            return false;
        }

        float size = Math.Max(1, TextFit.Grow(sizeAt96, dpi, textScale));
        using var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(codePoint.ToString(), font, brush, bounds, format);
        return true;
    }
}
