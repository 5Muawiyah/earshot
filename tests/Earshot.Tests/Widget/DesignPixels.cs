using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the design pixel tests share: the three themes the design has, the colours the tokens give for each, and small readers of a
// bitmap. The tests that use them draw with GDI+ and run on Windows only.
public enum DesignTheme { Light, Dark, HighContrast }

internal static class DesignPixels
{
    public static readonly DesignTheme[] Themes = [DesignTheme.Light, DesignTheme.Dark, DesignTheme.HighContrast];

    // The scales the design is drawn at: 100% and 150% display scale, each with 100% and 150% text size.
    public static readonly (int Dpi, double TextScale)[] Scales = [(96, 1.0), (96, 1.5), (144, 1.0), (144, 1.5)];

    public static IEnumerable<object[]> ThemesAndScales()
    {
        foreach (DesignTheme theme in Themes)
        {
            foreach ((int dpi, double t) in Scales)
            {
                yield return [theme, dpi, t];
            }
        }
    }

    public static DesignTokens Tokens(DesignTheme theme) => DesignTokens.For(theme == DesignTheme.Dark, theme == DesignTheme.HighContrast);

    // The ink a card is told about: white for a dark card, black for a light one; high contrast is told its own flag.
    public static WidgetCard NewCard(DesignTheme theme, double textScale, bool notice = false)
    {
        var card = new WidgetCard(new CapturingLog(), notice);
        bool hc = theme == DesignTheme.HighContrast;
        var look = new SystemLook(textScale, Transparency: !hc, HighContrast: hc);
        card.AttachLook(() => look);
        card.SetTheme(theme == DesignTheme.Dark ? Color.White : Color.Black, hc);
        return card;
    }

    // The accent the card uses: the design's default for the theme, or Highlight under high contrast.
    public static Color Accent(DesignTheme theme) => theme switch
    {
        DesignTheme.Light => DefaultCardAccent.Light,
        DesignTheme.Dark => DefaultCardAccent.Dark,
        _ => System.Drawing.SystemColors.Highlight,
    };

    // A colour with alpha, composited over another (ARGB, not premultiplied).
    public static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        double b = under.A / 255.0;
        double outA = a + (b * (1 - a));
        if (outA <= 0)
        {
            return Color.Transparent;
        }

        int Channel(int t, int u) => (int)Math.Round(((t * a) + (u * b * (1 - a))) / outA);
        return Color.FromArgb((int)Math.Round(outA * 255), Channel(top.R, under.R), Channel(top.G, under.G), Channel(top.B, under.B));
    }

    // The same colour by its value. Color's own equality also compares how it was made, so a system colour (Highlight under high
    // contrast, which the tokens give by name) never equals the pixel of the same value read from a bitmap.
    public static void AssertSameColour(Color expected, Color actual, string? message = null) =>
        Assert.AreEqual(expected.ToArgb(), actual.ToArgb(), (message ?? "The colour") + ": expected " + expected + " (" + expected.ToArgb().ToString("X8") + "), drawn " + actual);

    public static int Distance(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));

    // The pixel of a rectangle that differs most from the background: the ink of text at its fullest.
    public static Color Extreme(Bitmap bitmap, Rectangle rect, Color background)
    {
        Rectangle bounds = Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        Color best = background;
        int bestDistance = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                int d = Distance(pixel, background);
                if (d > bestDistance)
                {
                    best = pixel;
                    bestDistance = d;
                }
            }
        }

        return best;
    }

    // The columns of a rectangle that hold a pixel different from the background, first and last, or null.
    public static (int First, int Last)? InkColumns(Bitmap bitmap, Rectangle rect, Color background)
    {
        Rectangle bounds = Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int first = -1;
        int last = -1;
        for (int x = bounds.Left; x < bounds.Right; x++)
        {
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                if (bitmap.GetPixel(x, y).ToArgb() != background.ToArgb())
                {
                    first = first < 0 ? x : first;
                    last = x;
                    break;
                }
            }
        }

        return first < 0 ? null : (first, last);
    }
}
