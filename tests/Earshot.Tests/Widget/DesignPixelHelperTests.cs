using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The helpers the design pixel tests compare colours with. Color's own equality compares how a colour was made as well as its value,
// so a system colour (Highlight under high contrast, which the tokens give by name) is never equal to a pixel of the same value read
// from a bitmap: a "not the accent" check written with it passes whatever the pixel is.
[TestClass]
public sealed class DesignPixelHelperTests
{
    [TestMethod]
    public void ANotTheAccentCheckFailsOnAPixelThatIsTheAccentEvenWhenTheAccentIsASystemColour()
    {
        Color accent = SystemColors.Highlight;
        Color pixel = Color.FromArgb(accent.ToArgb());

        Assert.AreNotEqual(accent, pixel, "Sanity: Color's own comparison calls a named colour and its pixel different, which is the hole.");
        _ = Assert.ThrowsExactly<AssertFailedException>(() => DesignPixels.AssertNotSameColour(accent, pixel));
        DesignPixels.AssertNotSameColour(accent, Color.FromArgb(accent.ToArgb() ^ 0x010101));
        DesignPixels.AssertSameColour(accent, pixel);
    }
}
