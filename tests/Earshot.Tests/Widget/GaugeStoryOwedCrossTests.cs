using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A cross that has to be seen (a disconnect whose block did not take) has no end of its own until a gauge on screen has drawn it.
[TestClass]
public sealed class GaugeStoryOwedCrossTests
{
    private static readonly DateTimeOffset Begin = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static (GaugeStoryHost Host, TestTimeProvider Time) Make()
    {
        var time = new TestTimeProvider();
        var host = new GaugeStoryHost(time, action => action());
        return (host, time);
    }

    [TestMethod]
    public void AnOwedCrossHasNoEndUntilItIsSeenThenHoldsItsFullTime()
    {
        var story = new GaugeStory();
        story.Started(false, ToggleOrigin.Elsewhere, Begin);
        story.Ended(ToggleOutcome.Failed, Begin, mustBeSeen: true);

        Assert.IsTrue(story.AwaitingSight);
        Assert.IsNull(story.NextChange, "There is nothing to time yet.");
        Assert.AreEqual(GaugeStoryKind.Cross, story.View(Begin + TimeSpan.FromDays(1)).Kind);

        DateTimeOffset seen = Begin + TimeSpan.FromHours(2);
        Assert.IsTrue(story.Seen(seen));
        Assert.AreEqual(seen + GaugeStory.CrossHold, story.NextChange);
        Assert.AreEqual(GaugeStoryKind.Cross, story.View(seen + GaugeStory.CrossHold - TimeSpan.FromMilliseconds(1)).Kind);
        Assert.AreEqual(GaugeStoryKind.None, story.View(seen + GaugeStory.CrossHold).Kind);
        Assert.IsFalse(story.Seen(seen), "Seen once.");
    }

    [TestMethod]
    public void AnOrdinaryCrossIsTimedFromTheEndAsBefore()
    {
        var story = new GaugeStory();
        story.Started(true, ToggleOrigin.Elsewhere, Begin);
        story.Ended(ToggleOutcome.Failed, Begin);

        Assert.IsFalse(story.AwaitingSight);
        Assert.AreEqual(Begin + GaugeStory.CrossHold, story.NextChange);
        Assert.IsFalse(story.Seen(Begin));
    }

    [TestMethod]
    public void ANewToggleOrADismissEndsTheOwedCross()
    {
        var story = new GaugeStory();
        story.Started(false, ToggleOrigin.Elsewhere, Begin);
        story.Ended(ToggleOutcome.Failed, Begin, mustBeSeen: true);
        Assert.IsTrue(story.Dismiss());
        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin).Kind);
        Assert.IsFalse(story.Dismiss());

        story.Started(false, ToggleOrigin.Elsewhere, Begin);
        story.Ended(ToggleOutcome.Failed, Begin, mustBeSeen: true);
        story.Started(true, ToggleOrigin.Elsewhere, Begin);
        Assert.IsFalse(story.AwaitingSight);
        Assert.AreEqual(GaugeStoryKind.Spinning, story.View(Begin).Kind);
    }

    [TestMethod]
    public void TheHostKeepsAnOwedCrossPastItsHoldAndStartsTheHoldWhenAGaugeDrawsIt()
    {
        (GaugeStoryHost host, TestTimeProvider time) = Make();
        int seen = 0;
        host.CrossSeen += () => seen++;
        host.ToggleStarted(false, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Failed, mustBeSeen: true);
        Assert.IsTrue(host.CrossIsOwed);

        time.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(GaugeStoryKind.Cross, host.Current.Kind, "Nothing drew it, so it is still owed.");
        Assert.AreEqual(0, time.LiveTimers);

        host.GaugeDrawn();
        Assert.AreEqual(1, seen);
        Assert.IsFalse(host.CrossIsOwed);
        time.Advance(GaugeStory.CrossHold - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(GaugeStoryKind.Cross, host.Current.Kind);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind);

        host.GaugeDrawn();
        Assert.AreEqual(1, seen, "It is said once.");
    }

    [TestMethod]
    public void AGaugeThatDrawsWhileACardIsOnScreenHasNotSeenTheCross()
    {
        (GaugeStoryHost host, _) = Make();
        host.ToggleStarted(false, ToggleOrigin.Elsewhere);
        host.SetCardOnScreen(true);
        host.ToggleEnded(ToggleOutcome.Failed, mustBeSeen: true);

        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind, "The card tells, not the gauge.");
        host.GaugeDrawn();
        Assert.IsTrue(host.CrossIsOwed, "A draw with no cross in it has seen nothing.");

        host.SetCardOnScreen(false);
        Assert.AreEqual(GaugeStoryKind.Cross, host.Current.Kind, "The moment the card goes, the cross is owed to the gauge.");
    }

    [TestMethod]
    public void ADismissedOwedCrossGoesWithoutBeingDrawn()
    {
        (GaugeStoryHost host, _) = Make();
        int changes = 0;
        host.Changed += () => changes++;
        host.ToggleStarted(false, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Failed, mustBeSeen: true);
        int before = changes;

        host.DismissOwedCross();

        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind);
        Assert.AreEqual(before + 1, changes, "The gauge is told to draw again.");
        Assert.IsFalse(host.CrossIsOwed);
    }
}
