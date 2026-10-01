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

    private static AutoPause NewAutoPause(FakeMediaSessions sessions, bool autoPauseEnabled = true, ILog? log = null) =>
        new(sessions, () => autoPauseEnabled, log ?? new CapturingLog());

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
