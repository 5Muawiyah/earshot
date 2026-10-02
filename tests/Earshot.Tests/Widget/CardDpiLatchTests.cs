using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Cause 4 of the stutter: the card's scale was the host's value, rewritten by the one second taskbar poll, so a value that
// moved back and forth resized and placed the open card again. It is fixed at the show instead.
[TestClass]
public sealed class CardDpiLatchTests
{
    [TestMethod]
    public void TheScaleFixedAtTheShowIgnoresWhatTheHostReportsAfterwards()
    {
        int host = 96;
        var latch = new CardDpiLatch();

        Assert.AreEqual(96, latch.FixAtShow(displayDpi: null, () => host));
        for (int i = 0; i < 20; i++)
        {
            host = i % 2 == 0 ? 144 : 96;
            Assert.AreEqual(96, latch.Current(() => host), "Poll " + i);
        }
    }

    [TestMethod]
    public void ADisplaysOwnScaleIsFixedInPreferenceToTheHosts()
    {
        var latch = new CardDpiLatch();

        Assert.AreEqual(192, latch.FixAtShow(displayDpi: 192, () => 96));
        Assert.AreEqual(192, latch.Current(() => 96));
    }

    [TestMethod]
    public void ADisplayChangeReadsTheScaleAgain()
    {
        int host = 96;
        var latch = new CardDpiLatch();
        latch.FixAtShow(null, () => host);

        host = 144;
        Assert.AreEqual(96, latch.Current(() => host));
        latch.RereadOnDisplayChange(null, () => host);

        Assert.AreEqual(144, latch.Current(() => host));
    }

    [TestMethod]
    public void WithNothingShownTheHostsScaleIsUsed()
    {
        var latch = new CardDpiLatch();
        Assert.AreEqual(120, latch.Current(() => 120));

        latch.FixAtShow(null, () => 96);
        latch.Release();

        Assert.AreEqual(120, latch.Current(() => 120));
        latch.RereadOnDisplayChange(null, () => 144);
        Assert.AreEqual(120, latch.Current(() => 120), "A display change with nothing shown fixes nothing.");
    }
}
