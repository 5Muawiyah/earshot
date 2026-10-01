using Earshot.Contracts;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A bud leaving the ear pauses the one session playing to the AirPods, and the bud going back resumes that session
// under AutoResume's conditions (AutoResumeTests has each condition; EarSequenceTests runs whole message sequences).
[TestClass]
public sealed class AutoPauseTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static AutoPause NewAutoPause(FakeMediaSessions sessions, bool autoPauseEnabled = true, ILog? log = null, TimeProvider? time = null) =>
        new(sessions, () => autoPauseEnabled, log ?? new CapturingLog(), time);

    private static FakeMediaSessions OnePlayingSession() => new()
    {
        Sessions = { new MediaSessionView("app.exe", MediaPlaybackState.Playing, true, true, "app.exe") },
    };

    [TestMethod]
    public async Task HeldOffWhenTheSettingIsOff()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions, autoPauseEnabled: false);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task PausesWhenABudLeavesTheEarWhileThisPcRendersToTheAirPods()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsTrue(paused);
        string[] expected = { "app.exe" };
        CollectionAssert.AreEqual(expected, sessions.PauseCalls);
    }

    [TestMethod]
    public async Task DoesNotPauseWhenTheDefaultRenderContainerIsNotTheAirPods()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);
        Guid otherRenderContainer = Guid.NewGuid();

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, otherRenderContainer, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, otherRenderContainer, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseWhenRenderIsNotActive()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.Elsewhere, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.Elsewhere, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseWithNoPlayingSession()
    {
        var sessions = new FakeMediaSessions();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
    }

    [TestMethod]
    public async Task DoesNotPauseWithTwoPlayingSessions()
    {
        var sessions = new FakeMediaSessions
        {
            Sessions =
            {
                new MediaSessionView("one.exe", MediaPlaybackState.Playing, true, true, "one.exe"),
                new MediaSessionView("two.exe", MediaPlaybackState.Playing, true, true, "two.exe"),
            },
        };
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseOnAStaleReading()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);
        DateTimeOffset readingAt = At;
        DateTimeOffset now = At + WidgetTiming.EarFreshWindow + TimeSpan.FromSeconds(1);

        await autoPause.ApplyAsync(true, null, readingAt, readingAt, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(
            false, null, readingAt, now, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseWithTheInEarBitUnproved()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(null, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(null, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task AFalseFromTryPauseIsLoggedAndNotRetried()
    {
        var sessions = OnePlayingSession();
        sessions.PauseResult = false;
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(1, sessions.PauseCalls.Count, "Exactly one attempt: a false result is not retried for that edge.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "could not pause"));
    }

    [TestMethod]
    public async Task SafeModeRefusesThePauseAndLogsIt()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var safe = new SafeMediaSessions(sessions, log);
        var autoPause = new AutoPause(safe, () => true, log);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count, "Safe mode never reaches the inner sessions.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode"));
    }

    [TestMethod]
    public async Task DoesNotPauseAgainstAPreviousValueOlderThanTheFreshWindow()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);
        DateTimeOffset later = At + WidgetTiming.EarFreshWindow + TimeSpan.FromSeconds(1);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, null, later, later, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused, "An 11 second old 'in' must not pair with a new 'out'.");
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task RemembersThePauseAndResumesThatSessionWhenTheBudIsBack()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        Assert.IsTrue(autoPause.HasRememberedPause);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };

        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        string[] played = { "app.exe" };
        CollectionAssert.AreEqual(played, sessions.PlayCalls);
        Assert.IsFalse(autoPause.HasRememberedPause);
    }

    [TestMethod]
    public async Task ARefusedPauseRemembersNothingSoNothingIsResumed()
    {
        var sessions = OnePlayingSession();
        sessions.PauseResult = false;
        var autoPause = NewAutoPause(sessions);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(true, null, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(autoPause.HasRememberedPause);
        Assert.AreEqual(0, sessions.PlayCalls.Count);
    }

    [TestMethod]
    public async Task AFalseFromTryPlayIsLoggedAndNotRetried()
    {
        var sessions = OnePlayingSession();
        sessions.PlayResult = false;
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };
        await autoPause.ApplyAsync(true, null, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(true, null, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(1, sessions.PlayCalls.Count);
        Assert.IsTrue(log.Has(LogLevel.Warn, "could not resume"));
    }

    [TestMethod]
    public async Task ASessionThatNoLongerReadsPausedIsNotPlayed()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        // The fake does not change the session's state when it is paused: it still reads Playing, as if the person
        // had pressed play again.
        await autoPause.ApplyAsync(true, null, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(0, sessions.PlayCalls.Count);
        Assert.IsTrue(log.Has(LogLevel.Info, "no longer reads paused"));
    }

    [TestMethod]
    public async Task ASessionThatReadsPausedButCannotBePlayedIsNotPlayed()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused, IsPlayEnabled = false };

        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(0, sessions.PlayCalls.Count, "Windows says the session cannot be played now.");
        Assert.IsTrue(log.Has(LogLevel.Info, "cannot be played"));
        Assert.IsFalse(autoPause.HasRememberedPause, "A refused resume is forgotten, not retried.");
    }

    [TestMethod]
    public async Task AReadingWithNoInEarValueNeverResumes()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };
        DateTimeOffset later = At + TimeSpan.FromSeconds(5);
        await autoPause.ApplyAsync(null, null, later, later, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(0, sessions.PlayCalls.Count, "The bud's own value is still out, and null does not say it came back.");
    }

    // A source that raises no event whatever the person does (the real one before its listening is in place, or after it
    // failed) cannot be told "nothing was touched by hand" from a read alone, so nothing is resumed through it.
    [TestMethod]
    public async Task NothingIsResumedThroughASourceThatCannotReportChanges()
    {
        var sessions = OnePlayingSession();
        sessions.ReportsChanges = false;
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        Assert.IsTrue(paused, "The pause itself does not depend on hearing changes.");
        Assert.IsFalse(autoPause.HasRememberedPause, "No resume is armed for a source that is not listening.");
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };

        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(0, sessions.PlayCalls.Count, "Played by hand and paused by hand look the same to a read.");
        Assert.IsTrue(log.Has(LogLevel.Info, "not armed"));
    }

    [TestMethod]
    public async Task AListeningSourceStillResumesWhenNothingWasTouchedByHand()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };
        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        string[] played = { "app.exe" };
        CollectionAssert.AreEqual(played, sessions.PlayCalls);
    }

    [TestMethod]
    public async Task AReportedChangeAfterTheEchoWindowStopsTheResume()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions, time: new FixedTime(At + TimeSpan.FromSeconds(8)));
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };
        // The person pressed play and then pause again on the same session: it reads Paused, but a change was reported.
        sessions.RaisePlaybackInfoChanged("app.exe");
        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(0, sessions.PlayCalls.Count);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The generation is the status service's count of times the chosen set changed (a first choice, a switch, a set chosen
    // again, a new paired model): in-ear values and a remembered pause belong to one set and never carry to another.
    [TestMethod]
    public async Task ABudInOnOneSetAndOutOnTheNextChosenSetDoesNotPause()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 1);
        bool paused = await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 2);

        Assert.IsFalse(paused, "Set A's 'in' and set B's 'out' are two pairs' values, not one bud leaving.");
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task TheSameSetStillPausesWhenItsGenerationHasNotChanged()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 3);
        bool paused = await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 3);

        Assert.IsTrue(paused);
    }

    [TestMethod]
    public async Task APauseRememberedForOneSetIsForgottenWhenAnotherSetIsChosenAndItsBudIsIn()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 1);
        await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 1);
        Assert.IsTrue(autoPause.HasRememberedPause);
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };

        // A nearer pair is now the chosen set and both its buds are in: that is not the first set's bud coming back.
        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 2);

        Assert.AreEqual(0, sessions.PlayCalls.Count);
        Assert.IsFalse(autoPause.HasRememberedPause);
        Assert.IsTrue(log.Has(LogLevel.Info, "another set"));
    }

    // A pause being made for one set while another set is chosen: the pause is real, but it was made for a set that is no
    // longer the chosen one, so it is not remembered, and nothing of the other set's buds can resume it.
    [TestMethod]
    public async Task APauseInFlightForOneSetIsNotRememberedOnceAnotherSetWasChosen()
    {
        var sessions = OnePlayingSession();
        sessions.PauseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessions.HoldPause = release.Task;
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 1);
        Task<bool> pausing = autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 1);
        await sessions.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Another set is chosen while the pause for the first is still being made.
        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 2);
        release.SetResult();
        bool paused = await pausing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(paused, "The session was paused.");
        Assert.IsFalse(autoPause.HasRememberedPause, "A pause made for the first set is not carried to the set chosen since.");
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };
        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None, selectionGeneration: 2);
        Assert.AreEqual(0, sessions.PlayCalls.Count, "The other set's buds being in is not the paused set's bud coming back.");
        Assert.IsTrue(log.Has(LogLevel.Info, "another set"));
    }

    // The source reported changes when the pause was made and has stopped by the time a bud is back: a play and a pause by
    // hand since would not have been reported, so nothing is resumed through it.
    [TestMethod]
    public async Task NothingIsResumedWhenTheSourceStoppedReportingChangesAfterThePause()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var autoPause = NewAutoPause(sessions, log: log);
        DateTimeOffset back = At + TimeSpan.FromSeconds(10);

        await autoPause.ApplyAsync(true, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(false, true, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        Assert.IsTrue(paused);
        Assert.IsTrue(autoPause.HasRememberedPause, "The source was listening when the pause was made, so a resume was armed.");
        sessions.Sessions[0] = sessions.Sessions[0] with { PlaybackStatus = MediaPlaybackState.Paused };
        sessions.ReportsChanges = false;

        await autoPause.ApplyAsync(true, true, back, back, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.AreEqual(0, sessions.PlayCalls.Count, "Played by hand and paused by hand look the same to a read.");
        Assert.IsTrue(log.Has(LogLevel.Info, "not reporting changes"));
    }

    [TestMethod]
    public async Task DisposeStopsListeningToTheSessions()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);
        await autoPause.ApplyAsync(true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        await autoPause.ApplyAsync(false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        autoPause.Dispose();
        sessions.RaisePlaybackInfoChanged("another.exe");

        Assert.IsTrue(autoPause.HasRememberedPause, "A disposed AutoPause no longer hears the sessions.");
    }
}
