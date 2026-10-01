using System.Drawing.Text;

namespace Earshot.Widget;

// The card's text styles: the Windows 11 type ramp in Segoe UI Variable, sized in effective pixels and scaled by
// the display's DPI and by Settings > Accessibility > Text size.
//
// Sizes and line heights are the type ramp's: Caption 12/16, Body 14/20, Body Strong (semibold) 14/20. Weights are
// Regular (400) and Semibold (600); Microsoft's minimums are 14 px semibold and 12 px regular.
// https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography
//
// Segoe UI Variable is one font with weight and optical-size axes; XAML picks the optical size by itself. GDI+ has
// no such switch, so each style names one of the font's own instances (Small, Text, Display, each with a Semibold
// sibling). The page gives the axis range but no thresholds, so the choice is Earshot's own: Small at 12 px, Text
// at 14 and up to 18, Display at 20 and above. A role table edit changes it.
//
// GDI+ cuts family names at 31 characters ("Segoe UI Variable Small Semibol"), so a family is found by name from the
// installed list rather than spelled out. Where the instance is missing the fallback is Segoe UI, with bold for
// semibold (the only weight it has), and then the system message font. FontStyle.Bold is never applied to a
// Segoe UI Variable family: its Semibold instance is already the 600 weight.
internal enum TypeRole { Caption, CaptionStrong, Body, BodyStrong, Number, Gauge }

internal enum OpticalSize { Small, Text, Display }

internal readonly record struct TypeFace(string Family, FontStyle Style);

internal static class TypeRamp
{
    internal readonly record struct RoleSpec(int SizeEpx, int LineEpx, bool Semibold, OpticalSize Optical);

    public const double MinTextScale = 1.0;
    public const double MaxTextScale = 2.25;

    public static RoleSpec Spec(TypeRole role) => role switch
    {
        TypeRole.Caption => new RoleSpec(12, 16, false, OpticalSize.Small),
        TypeRole.CaptionStrong => new RoleSpec(12, 16, true, OpticalSize.Small),
        TypeRole.Body => new RoleSpec(14, 20, false, OpticalSize.Text),
        TypeRole.BodyStrong => new RoleSpec(14, 20, true, OpticalSize.Text),
        TypeRole.Number => new RoleSpec(20, 24, true, OpticalSize.Display),
        TypeRole.Gauge => new RoleSpec(12, 16, false, OpticalSize.Small),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    // The gauge has a fixed width and a 22 px number slot, so its type does not follow the text size; its
    // tooltip is a system tooltip and does.
    public static double ScaleFor(TypeRole role, double textScale) =>
        role == TypeRole.Gauge ? 1.0 : Math.Clamp(double.IsFinite(textScale) ? textScale : 1.0, MinTextScale, MaxTextScale);

    public static int SizePx(TypeRole role, int dpi, double textScale) =>
        Math.Max(1, (int)Math.Round(Spec(role).SizeEpx * Effective(dpi) / 96.0 * ScaleFor(role, textScale), MidpointRounding.AwayFromZero));

    public static int LineHeight(TypeRole role, int dpi, double textScale) =>
        Math.Max(1, (int)Math.Round(Spec(role).LineEpx * Effective(dpi) / 96.0 * ScaleFor(role, textScale), MidpointRounding.AwayFromZero));

    private static int Effective(int dpi) => dpi > 0 ? dpi : 96;

    // The family and style for role among the installed family names.
    public static TypeFace Resolve(TypeRole role, IReadOnlyList<string> installed, string messageFontFamily)
    {
        RoleSpec spec = Spec(role);
        return Resolve(spec.Optical, spec.Semibold, installed, messageFontFamily);
    }

    // The family and style for one optical instance, regular or semibold.
    public static TypeFace Resolve(OpticalSize optical, bool semibold, IReadOnlyList<string> installed, string messageFontFamily)
    {
        ArgumentNullException.ThrowIfNull(installed);
        string wanted = "Segoe UI Variable " + optical + (semibold ? " Semibold" : string.Empty);
        string cut = wanted.Length > 31 ? wanted[..31] : wanted;
        foreach (string name in installed)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(name, cut, StringComparison.OrdinalIgnoreCase))
            {
                return new TypeFace(name, FontStyle.Regular);
            }
        }

        foreach (string name in installed)
        {
            if (string.Equals(name, "Segoe UI", StringComparison.OrdinalIgnoreCase))
            {
                return new TypeFace(name, semibold ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        return new TypeFace(messageFontFamily, semibold ? FontStyle.Bold : FontStyle.Regular);
    }

    private static readonly Lock Gate = new();
    private static IReadOnlyList<string>? _installed;
    private static string? _message;

    // The installed families, read once.
    private static IReadOnlyList<string> Installed(out string messageFontFamily)
    {
        lock (Gate)
        {
            if (_installed is null)
            {
                using var fonts = new InstalledFontCollection();
                _installed = fonts.Families.Select(f => f.Name).ToList();
                using Font? message = SystemFonts.MessageBoxFont;
                _message = message?.Name ?? FontFamily.GenericSansSerif.Name;
            }

            messageFontFamily = _message!;
            return _installed;
        }
    }

    // A pixel-unit font for role at dpi and text size. The caller disposes it.
    public static Font Font(TypeRole role, int dpi, double textScale)
    {
        TypeFace face = Resolve(role, Installed(out string message), message);
        return new Font(face.Family, SizePx(role, dpi, textScale), face.Style, GraphicsUnit.Pixel);
    }

    // The family a style draws with here, for a caller that takes a family name (the gauge's renderer). Regular-weight
    // styles only: a semibold style needs its style flag as well, which Font(role, ...) carries.
    public static string FamilyFor(TypeRole role)
    {
        TypeFace face = Resolve(role, Installed(out string message), message);
        return face.Family;
    }

    // A pixel-unit font of an explicit size in one optical instance. The caller disposes it.
    public static Font FontOfSize(OpticalSize optical, bool semibold, int pixels)
    {
        TypeFace face = Resolve(optical, semibold, Installed(out string message), message);
        return new Font(face.Family, Math.Max(1, pixels), face.Style, GraphicsUnit.Pixel);
    }

    // The optical instance that serves a size in effective pixels: Small up to 12, Text above it up to 18, Display
    // from 20.
    public static OpticalSize OpticalFor(double epx) => epx switch
    {
        <= 12.5 => OpticalSize.Small,
        < 19.5 => OpticalSize.Text,
        _ => OpticalSize.Display,
    };
}
