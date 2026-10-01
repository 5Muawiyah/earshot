using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// AutoResume on its own: no clock, no media session. Every fact is passed in, so each condition of a resume is one
// test.
[TestClass]
public sealed class AutoResumeTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly DateTimeOffset PausedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static RememberedPause Pause(bool leftWasIn = true, bool rightWasIn = true) =>
        new("player.exe", "player.exe", leftWasIn, rightWasIn, Container, PausedAt, PausedAt + WidgetTiming.OwnPauseEchoWindow);

    private static AutoResume Remembering(RememberedPause? pause = null)
    {
        var resume = new AutoResume();
        resume.Remember(pause ?? Pause());
        return resume;
    }

    private static InEarState In(DateTimeOffset at) => new(true, at);

    private static InEarState Out(DateTimeOffset at) => new(false, at);

    private static ResumeDecision Evaluate(
        AutoResume resume, DateTimeOffset now, InEarState left, InEarState right, bool settingOn = true, bool renders = true, Guid? container = null) =>
        resume.Evaluate(now, now, settingOn, renders, container ?? Container, left, right);

    [TestMethod]
    public void NothingRememberedNothingToDo()
    {
        var resume = new AutoResume();

        ResumeDecision decision = Evaluate(resume, PausedAt, In(PausedAt), In(PausedAt));

        Assert.AreEqual(ResumeVerdict.NothingRemembered, decision.Verdict);
        Assert.IsFalse(resume.HasPending);
    }

    [TestMethod]
    public void ResumesWhenEveryBudThatWasInIsBackInTime()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(20);

        ResumeDecision decision = Evaluate(resume, now, In(now), In(now));

        Assert.AreEqual(ResumeVerdict.Resume, decision.Verdict);
        Assert.AreEqual("player.exe", decision.Pause!.SessionId);
    }

    [TestMethod]
    public void WaitsWhileABudThatWasInIsStillOut()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        Assert.AreEqual(ResumeVerdict.Wait, Evaluate(resume, now, Out(now), In(now)).Verdict);
        Assert.AreEqual(ResumeVerdict.Wait, Evaluate(resume, now, In(now), Out(now)).Verdict);
        Assert.IsTrue(resume.HasPending, "Waiting keeps the pause.");
    }

    [TestMethod]
    public void AnUnknownBudValueIsNotBackIn()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        ResumeDecision decision = Evaluate(resume, now, In(now), new InEarState(null, null));

        Assert.AreEqual(ResumeVerdict.Wait, decision.Verdict);
    }

    [TestMethod]
    public void ABudThatWasNotInTheEarBeforeThePauseIsNotWaitedFor()
    {
        AutoResume resume = Remembering(Pause(leftWasIn: true, rightWasIn: false));
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        ResumeDecision decision = Evaluate(resume, now, In(now), Out(now));

        Assert.AreEqual(ResumeVerdict.Resume, decision.Verdict);
    }

    [TestMethod]
    public void AResumeIsTakenOnce()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        Evaluate(resume, now, In(now), In(now));
        ResumeDecision second = Evaluate(resume, now, In(now), In(now));

        Assert.AreEqual(ResumeVerdict.NothingRemembered, second.Verdict);
    }

    [TestMethod]
    public void ResumesAtTheEdgeOfTheWindowAndCancelsAMomentAfter()
    {
        AutoResume atEdge = Remembering();
        DateTimeOffset edge = PausedAt + WidgetTiming.ResumeWindow;
        Assert.AreEqual(ResumeVerdict.Resume, Evaluate(atEdge, edge, In(edge), In(edge)).Verdict);

        AutoResume late = Remembering();
        DateTimeOffset after = edge + TimeSpan.FromMilliseconds(1);
        ResumeDecision decision = Evaluate(late, after, In(after), In(after));

        Assert.AreEqual(ResumeVerdict.Cancel, decision.Verdict);
        Assert.IsFalse(late.HasPending, "Past the window the pause is gone for good.");
        Assert.AreEqual(ResumeVerdict.NothingRemembered, Evaluate(late, after, In(after), In(after)).Verdict);
    }

    [TestMethod]
    public void AReadingOlderThanTheFreshWindowIsNotActedOnButDoesNotCancel()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(30);
        DateTimeOffset oldReading = now - WidgetTiming.EarFreshWindow - TimeSpan.FromSeconds(1);

        ResumeDecision decision = resume.Evaluate(now, oldReading, true, true, Container, In(now), In(now));

        Assert.AreEqual(ResumeVerdict.Wait, decision.Verdict);
        Assert.IsTrue(resume.HasPending);
    }

    [TestMethod]
    public void ABudValueOlderThanTheFreshWindowIsNotBackIn()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(30);
        DateTimeOffset oldBud = now - WidgetTiming.EarFreshWindow - TimeSpan.FromSeconds(1);

        ResumeDecision decision = resume.Evaluate(now, now, true, true, Container, In(now), In(oldBud));

        Assert.AreEqual(ResumeVerdict.Wait, decision.Verdict);
    }

    [TestMethod]
    public void TheSettingBeingOffCancels()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        ResumeDecision decision = Evaluate(resume, now, In(now), In(now), settingOn: false);

        Assert.AreEqual(ResumeVerdict.Cancel, decision.Verdict);
        Assert.IsFalse(resume.HasPending);
    }

    [TestMethod]
    public void NoLongerRenderingToTheAirPodsCancels()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        Assert.AreEqual(ResumeVerdict.Cancel, Evaluate(resume, now, In(now), In(now), renders: false).Verdict);
        Assert.IsFalse(resume.HasPending);
    }

    [TestMethod]
    public void AChangedWatchedContainerCancels()
    {
        AutoResume resume = Remembering();
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        Assert.AreEqual(ResumeVerdict.Cancel, Evaluate(resume, now, In(now), In(now), container: Guid.NewGuid()).Verdict);
    }

    [TestMethod]
    public void AChangeToAnotherSessionForgetsThePause()
    {
        AutoResume resume = Remembering();

        string? reason = resume.NoteSessionChanged("other.exe", PausedAt + TimeSpan.FromSeconds(1));

        Assert.IsNotNull(reason);
        Assert.IsFalse(resume.HasPending, "Even inside the echo window: only the paused session's own change is an echo.");
    }

    [TestMethod]
    public void AChangeToThePausedSessionInsideTheEchoWindowIsTheEchoOfThePause()
    {
        AutoResume resume = Remembering();

        string? reason = resume.NoteSessionChanged("player.exe", PausedAt + WidgetTiming.OwnPauseEchoWindow);

        Assert.IsNull(reason);
        Assert.IsTrue(resume.HasPending);
    }

    [TestMethod]
    public void AChangeToThePausedSessionAfterTheEchoWindowForgetsThePause()
    {
        AutoResume resume = Remembering();

        string? reason = resume.NoteSessionChanged("player.exe", PausedAt + WidgetTiming.OwnPauseEchoWindow + TimeSpan.FromMilliseconds(1));

        Assert.IsNotNull(reason);
        Assert.IsFalse(resume.HasPending);
    }

    [TestMethod]
    public void ASessionChangeWithNothingRememberedIsIgnored()
    {
        var resume = new AutoResume();

        Assert.IsNull(resume.NoteSessionChanged("player.exe", PausedAt));
    }

    [TestMethod]
    public void TheOutputGoingForgetsThePause()
    {
        AutoResume resume = Remembering();

        Assert.IsNotNull(resume.NoteOutputGone());
        Assert.IsFalse(resume.HasPending);
        Assert.IsNull(resume.NoteOutputGone());
    }

    [TestMethod]
    public void ANewerPauseReplacesTheOlderOne()
    {
        AutoResume resume = Remembering();
        resume.Remember(new RememberedPause("second.exe", "second.exe", true, true, Container, PausedAt, PausedAt));
        DateTimeOffset now = PausedAt + TimeSpan.FromSeconds(5);

        ResumeDecision decision = Evaluate(resume, now, In(now), In(now));

        Assert.AreEqual("second.exe", decision.Pause!.SessionId);
    }
}
