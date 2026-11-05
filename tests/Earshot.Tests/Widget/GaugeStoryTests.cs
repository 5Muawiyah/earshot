using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the gauge tells while a connect or disconnect runs: idle, spinning, a tick or a cross, all worked out from the times it
// is handed. The story reads no clock and owns no timer; the host that keeps it owns the one timer there is, for the end of a
// hold, and none for the end of a spin.
[TestClass]
public sealed class GaugeStoryTests
{
    private static readonly DateTimeOffset Begin = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [TestMethod]
    public void ANewStoryIsIdleAndNothingIsDueFromIt()
    {
        var story = new GaugeStory();

        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin).Kind);
        Assert.IsNull(story.NextChange);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AToggleThatStartsSpinsInTheDirectionItWants(bool connect)
    {
        var story = new GaugeStory();

        story.Started(connect, ToggleOrigin.Elsewhere, Begin);

        GaugeStoryView view = story.View(Begin);
        Assert.AreEqual(GaugeStoryKind.Spinning, view.Kind);
        Assert.AreEqual(connect, view.Connect);
    }

    [TestMethod]
    public void ASpinHasNoEndOfItsOwnAndOnlyEndedStopsIt()
    {
        var story = new GaugeStory();
        story.Started(true, ToggleOrigin.Elsewhere, Begin);

        Assert.IsNull(story.NextChange, "Nothing is due while it spins: the toggle's own end is the only end.");
        Assert.AreEqual(GaugeStoryKind.Spinning, story.View(Begin + TimeSpan.FromHours(3)).Kind, "No cap of the story's own, however long.");

        story.Ended(ToggleOutcome.Cancelled, Begin + Ms(21000));
        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin + Ms(21000)).Kind);
    }

    [TestMethod]
    public void AConnectThatSucceedsShowsATickFor800MillisecondsThenIdle()
    {
        var story = new GaugeStory();
        story.Started(true, ToggleOrigin.Elsewhere, Begin);
        DateTimeOffset end = Begin + Ms(3200);

        story.Ended(ToggleOutcome.Succeeded, end);

        Assert.AreEqual(GaugeStoryKind.Tick, story.View(end).Kind);
        Assert.AreEqual(GaugeStoryKind.Tick, story.View(end + Ms(799)).Kind);
        Assert.AreEqual(GaugeStoryKind.None, story.View(end + Ms(800)).Kind, "The hold is 800 ms.");
        Assert.AreEqual(end + Ms(800), story.NextChange);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AToggleThatFailsShowsACrossFor1500MillisecondsThenIdle(bool connect)
    {
        var story = new GaugeStory();
        story.Started(connect, ToggleOrigin.Elsewhere, Begin);
        DateTimeOffset end = Begin + Ms(900);

        story.Ended(ToggleOutcome.Failed, end);

        GaugeStoryView view = story.View(end);
        Assert.AreEqual(GaugeStoryKind.Cross, view.Kind);
        Assert.AreEqual(connect, view.Connect, "A failed disconnect keeps its direction, for the words it says.");
        Assert.AreEqual(GaugeStoryKind.Cross, story.View(end + Ms(1499)).Kind);
        Assert.AreEqual(GaugeStoryKind.None, story.View(end + Ms(1500)).Kind, "A failure earns the longer look: 1500 ms.");
        Assert.AreEqual(end + Ms(1500), story.NextChange);
    }

    [TestMethod]
    public void ADisconnectThatSucceedsGoesStraightToIdleWithNoTick()
    {
        var story = new GaugeStory();
        story.Started(false, ToggleOrigin.Elsewhere, Begin);

        story.Ended(ToggleOutcome.Succeeded, Begin + Ms(700));

        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin + Ms(700)).Kind);
        Assert.IsNull(story.NextChange);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ACancelledToggleGoesStraightToIdleWithNoMark(bool connect)
    {
        var story = new GaugeStory();
        story.Started(connect, ToggleOrigin.Elsewhere, Begin);

        story.Ended(ToggleOutcome.Cancelled, Begin + Ms(400));

        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin + Ms(400)).Kind);
        Assert.IsNull(story.NextChange);
    }

    // Connected but not protected, or disconnected but not blocked: the gauge claims neither a tick nor a cross, it shows what is true.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void APartialToggleShowsNeitherATickNorACross(bool connect)
    {
        var story = new GaugeStory();
        story.Started(connect, ToggleOrigin.Elsewhere, Begin);

        story.Ended(ToggleOutcome.Partial, Begin + Ms(900));

        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin + Ms(900)).Kind);
        Assert.IsNull(story.NextChange, "Nothing is held.");
    }

    [TestMethod]
    public void AnEndWithNoStartChangesNothing()
    {
        var story = new GaugeStory();

        story.Ended(ToggleOutcome.Failed, Begin);

        Assert.AreEqual(GaugeStoryKind.None, story.View(Begin).Kind, "A cross is never shown for a toggle that was not shown starting.");
        Assert.IsNull(story.NextChange);
    }

    [TestMethod]
    public void ANewToggleDuringAHoldTakesTheGaugeOverAtOnce()
    {
        var story = new GaugeStory();
        story.Started(true, ToggleOrigin.Elsewhere, Begin);
        story.Ended(ToggleOutcome.Succeeded, Begin + Ms(1000));

        story.Started(false, ToggleOrigin.Elsewhere, Begin + Ms(1300));

        GaugeStoryView view = story.View(Begin + Ms(1300));
        Assert.AreEqual(GaugeStoryKind.Spinning, view.Kind);
        Assert.IsFalse(view.Connect);
        Assert.IsNull(story.NextChange, "The hold that was running is over.");
    }

    [TestMethod]
    public void TheOriginOfTheToggleIsKept()
    {
        var story = new GaugeStory();

        story.Started(true, ToggleOrigin.Card, Begin);

        Assert.AreEqual(ToggleOrigin.Card, story.Origin);
    }

    [TestMethod]
    public void IdleIsTheSameValueWhateverCameBefore()
    {
        var story = new GaugeStory();
        story.Started(true, ToggleOrigin.Elsewhere, Begin);
        story.Ended(ToggleOutcome.Failed, Begin + Ms(100));

        Assert.AreEqual(default(GaugeStoryView), story.View(Begin + Ms(5000)), "Idle never carries a direction, so a key built from it is stable.");
    }
}

// The host keeps the story, says when a card is on screen (the gauge then shows nothing of it), and owns the one timer: the end
// of a tick's or a cross's hold.
[TestClass]
public sealed class GaugeStoryHostTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static (GaugeStoryHost Host, TestTimeProvider Time, List<GaugeStoryView> Changes) Make()
    {
        var time = new TestTimeProvider();
        var host = new GaugeStoryHost(time, action => action());
        var changes = new List<GaugeStoryView>();
        host.Changed += () => changes.Add(host.Current);
        return (host, time, changes);
    }

    [TestMethod]
    public void AToggleThatStartsIsToldAtOnceAndNoTimerIsRunningForIt()
    {
        (GaugeStoryHost host, TestTimeProvider time, List<GaugeStoryView> changes) = Make();

        host.ToggleStarted(true, ToggleOrigin.Elsewhere);

        Assert.AreEqual(GaugeStoryKind.Spinning, host.Current.Kind);
        Assert.HasCount(1, changes);
        Assert.AreEqual(0, time.TimersCreated, "The story has no timer of its own for the end of a spin.");
    }

    [TestMethod]
    public void ATickIsHeldFor800MillisecondsByTheHostsOwnTimer()
    {
        (GaugeStoryHost host, TestTimeProvider time, List<GaugeStoryView> changes) = Make();
        host.ToggleStarted(true, ToggleOrigin.Elsewhere);
        time.Advance(Ms(2000));

        host.ToggleEnded(ToggleOutcome.Succeeded);
        Assert.AreEqual(GaugeStoryKind.Tick, host.Current.Kind);
        int before = changes.Count;

        time.Advance(Ms(799));
        Assert.AreEqual(GaugeStoryKind.Tick, host.Current.Kind);
        Assert.AreEqual(before, changes.Count, "Nothing is told before the hold ends.");

        time.Advance(Ms(1));
        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind);
        Assert.AreEqual(before + 1, changes.Count, "The end of the hold is told once, so the gauge settles.");
        Assert.AreEqual(0, time.LiveTimers, "The timer is let go.");
    }

    [TestMethod]
    public void ACrossIsHeldFor1500Milliseconds()
    {
        (GaugeStoryHost host, TestTimeProvider time, _) = Make();
        host.ToggleStarted(true, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Failed);

        time.Advance(Ms(1499));
        Assert.AreEqual(GaugeStoryKind.Cross, host.Current.Kind);

        time.Advance(Ms(1));
        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind);
    }

    [TestMethod]
    public void ADisconnectThatSucceedsAndACancelStartNoTimerAndShowNothing()
    {
        (GaugeStoryHost host, TestTimeProvider time, _) = Make();
        host.ToggleStarted(false, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Succeeded);
        host.ToggleStarted(true, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Cancelled);

        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind);
        Assert.AreEqual(0, time.LiveTimers);
    }

    [TestMethod]
    public void WhileACardIsOnScreenTheGaugeShowsNothingAndItTakesOverTheMomentTheCardCloses()
    {
        (GaugeStoryHost host, _, List<GaugeStoryView> changes) = Make();
        host.SetCardOnScreen(true);
        host.ToggleStarted(true, ToggleOrigin.Card);

        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind, "The card tells its own story.");

        changes.Clear();
        host.SetCardOnScreen(false);
        Assert.AreEqual(GaugeStoryKind.Spinning, host.Current.Kind, "Closing the card mid-attempt hands over at once.");
        Assert.HasCount(1, changes);

        host.SetCardOnScreen(true);
        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind, "Opening a card takes it back.");
        Assert.HasCount(2, changes);
    }

    [TestMethod]
    public void ACardOpeningOrClosingThatChangesNothingTellsNobody()
    {
        (GaugeStoryHost host, _, List<GaugeStoryView> changes) = Make();

        host.SetCardOnScreen(true);
        host.SetCardOnScreen(false);

        Assert.HasCount(0, changes, "No story, so nothing on the gauge moves.");
    }

    [TestMethod]
    public void AResultThatArrivesWhileACardIsOnScreenIsNotReplayedOnTheGaugeWhenTheCardCloses()
    {
        (GaugeStoryHost host, TestTimeProvider time, _) = Make();
        host.ToggleStarted(true, ToggleOrigin.Elsewhere);
        host.SetCardOnScreen(true);
        host.ToggleEnded(ToggleOutcome.Failed);
        time.Advance(Ms(200));

        host.SetCardOnScreen(false);

        Assert.AreEqual(GaugeStoryKind.None, host.Current.Kind, "The card showed the failure; the gauge does not show it again after it.");
    }

    [TestMethod]
    public void AToggleThatStartsDuringAHoldReplacesItAndTheOldTimerDoesNotEndTheNewSpin()
    {
        (GaugeStoryHost host, TestTimeProvider time, _) = Make();
        host.ToggleStarted(true, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Succeeded);
        time.Advance(Ms(300));

        host.ToggleStarted(false, ToggleOrigin.Elsewhere);
        time.Advance(Ms(2000));

        Assert.AreEqual(GaugeStoryKind.Spinning, host.Current.Kind);
        Assert.IsFalse(host.Current.Connect);
    }

    [TestMethod]
    public void DisposingLetsTheHoldTimerGoAndTellsNobodyAfterwards()
    {
        (GaugeStoryHost host, TestTimeProvider time, List<GaugeStoryView> changes) = Make();
        host.ToggleStarted(true, ToggleOrigin.Elsewhere);
        host.ToggleEnded(ToggleOutcome.Failed);
        int before = changes.Count;

        host.Dispose();
        time.Advance(Ms(5000));

        Assert.AreEqual(0, time.LiveTimers);
        Assert.AreEqual(before, changes.Count);
    }
}
