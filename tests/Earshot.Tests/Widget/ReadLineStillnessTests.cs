using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The card's read-time line must not tick. Every message changes the newest read time (the radio's own timestamp, a little
// behind the clock by a varying latency), the presenter redraws, and a line that counted seconds ("0 s ago", "1 s ago",
// "2 s ago") flickered at message cadence. Design: while a reading is fresh (BatteryFreshness.FreshWindow) the line is
// hidden but its place kept; a reading that is not fresh says its age at the level of minutes, never seconds.
[TestClass]
public sealed class ReadLineStillnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    // Cause 1 of the stutter: the text for a fresh value changed as its age went 0, 1, 2 s.
    [TestMethod]
    public void TheReadLineForAFreshReadingIsTheSameTextAtEveryAgeInTheFreshWindow()
    {
        string shortLine = WidgetCopy.ReadAge(Now, Now);
        string fullLine = WidgetCopy.BatteryReadLine(Now, Now);

        for (int ms = 0; ms <= (int)BatteryFreshness.FreshWindow.TotalMilliseconds; ms += 100)
        {
            DateTimeOffset readAt = Now - TimeSpan.FromMilliseconds(ms);
            Assert.AreEqual(shortLine, WidgetCopy.ReadAge(readAt, Now), "Short line at age " + ms + " ms.");
            Assert.AreEqual(fullLine, WidgetCopy.BatteryReadLine(readAt, Now), "Full line at age " + ms + " ms.");
        }
    }

    [TestMethod]
    public void TheFreshReadLineIsHiddenButItsPlaceIsKept()
    {
        Assert.AreEqual("", WidgetCopy.ReadAge(Now - TimeSpan.FromSeconds(2), Now));
        Assert.AreEqual("", WidgetCopy.BatteryReadLine(Now - TimeSpan.FromSeconds(2), Now));
        Assert.AreEqual("", WidgetCopy.ReadAge(Now + TimeSpan.FromMinutes(5), Now), "A reading from the future is fresh, not a negative age.");
    }

    [TestMethod]
    public void AReadingThatIsNotFreshSaysItsAgeAtTheLevelOfMinutesAndNeverCountsSeconds()
    {
        string first = WidgetCopy.ReadAge(Now - BatteryFreshness.FreshWindow - TimeSpan.FromSeconds(1), Now);
        Assert.AreEqual("under 1 min ago", first);
        for (int s = 31; s < 60; s++)
        {
            Assert.AreEqual(first, WidgetCopy.ReadAge(Now - TimeSpan.FromSeconds(s), Now), "Age " + s + " s.");
            Assert.DoesNotContain(" s ", WidgetCopy.BatteryReadLine(Now - TimeSpan.FromSeconds(s), Now) + " ");
        }

        Assert.AreEqual("2 min ago", WidgetCopy.ReadAge(Now - TimeSpan.FromMinutes(2), Now));
        Assert.AreEqual("3 h ago", WidgetCopy.ReadAge(Now - TimeSpan.FromHours(3), Now));
        Assert.AreEqual("Battery read 5 min ago", WidgetCopy.BatteryReadLine(Now - TimeSpan.FromMinutes(5), Now));
    }

    [TestMethod]
    public void NoReadingStillSaysSo()
    {
        Assert.AreEqual("Not read yet", WidgetCopy.ReadAge(null, Now));
        Assert.AreEqual("Battery not read yet", WidgetCopy.BatteryReadLine(null, Now));
    }

    // A column's figure that is not live keeps its marking and its age (an estimate its sign), at the level of minutes.
    [TestMethod]
    public void AColumnThatIsNotLiveKeepsItsMarkingAndItsAgeWithoutCountingSeconds()
    {
        ShownPart Part(bool estimated, int seconds) => estimated
            ? new(80, true, ReadingKind.Estimated, Now - TimeSpan.FromSeconds(seconds)) { ReadPercent = 60 }
            : new(80, false, ReadingKind.Last, Now - TimeSpan.FromSeconds(seconds));

        string last35 = WidgetCopy.PartLine(Part(false, 35), Now);
        Assert.AreEqual(last35, WidgetCopy.PartLine(Part(false, 55), Now));
        Assert.AreEqual("80% · under 1 min", last35);
        Assert.AreEqual("≈80% · under 1 min", WidgetCopy.PartLine(Part(true, 40), Now));
        Assert.AreEqual("Last read under 1 min ago", WidgetCopy.PartTip(Part(false, 40), Now));
        Assert.AreEqual("Estimated, read under 1 min ago", WidgetCopy.PartTip(Part(true, 40), Now));
    }
}
