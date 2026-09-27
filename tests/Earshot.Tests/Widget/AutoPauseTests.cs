using Earshot.Contracts;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Stage 1 only (a bud leaving the ear pauses the one session playing to the AirPods). Stage 2 (resuming
// what was paused) is not built; the spec for this allows leaving it out without touching stage 1.
[TestClass]
public sealed class AutoPauseTests
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static AutoPause NewAutoPause(FakeMediaSessions sessions, bool? gate = true, bool autoPauseEnabled = true, ILog? log = null) =>
        new(sessions, () => gate, () => autoPauseEnabled, log ?? new CapturingLog());

    private static FakeMediaSessions OnePlayingSession() => new()
    {
        Sessions = { new MediaSessionView("app.exe", MediaPlaybackState.Playing, true, true, "app.exe") },
    };

    [TestMethod]
    public async Task HeldOffWhileTheGateIsNull()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions, gate: null);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task HeldOffWhenTheSettingIsOff()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions, autoPauseEnabled: false);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task PausesWhenABudLeavesTheEarWhileThisPcRendersToTheAirPods()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

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

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, otherRenderContainer, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, otherRenderContainer, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseWhenRenderIsNotActive()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.Elsewhere, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.Elsewhere, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseWithNoPlayingSession()
    {
        var sessions = new FakeMediaSessions();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

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

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseOnAStrangerReading()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(
            OwnershipVerdict.ModelOrColourMismatch, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

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

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, readingAt, readingAt, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(
            OwnershipVerdict.Owned, false, null, readingAt, now, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task DoesNotPauseWithTheInEarBitUnproved()
    {
        var sessions = OnePlayingSession();
        var autoPause = NewAutoPause(sessions);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, null, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, null, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

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

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(1, sessions.PauseCalls.Count, "Exactly one attempt: a false result is not retried for that edge.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "could not pause"));
    }

    // M4: the public constructor must read WidgetDefaults.BroadcastContinuesWhilePlayingFromThisPc itself,
    // not accept an arbitrary gate, so nothing composing this class can wire up anything but phase 0's own
    // proved value (which ships null, so this stays held off).
    [TestMethod]
    public async Task TheProductionConstructorReadsThePhase0GateItselfAndStaysHeldOff()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var autoPause = new AutoPause(sessions, () => true, log);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused, "WidgetDefaults.BroadcastContinuesWhilePlayingFromThisPc ships null, so the production constructor must stay held off.");
        Assert.AreEqual(0, sessions.PauseCalls.Count);
        Assert.IsTrue(log.Has(LogLevel.Info, "waiting for phase 0"));
    }

    [TestMethod]
    public async Task SafeModeRefusesThePauseAndLogsIt()
    {
        var sessions = OnePlayingSession();
        var log = new CapturingLog();
        var safe = new SafeMediaSessions(sessions, log);
        var autoPause = new AutoPause(safe, () => true, () => true, log);

        await autoPause.ApplyAsync(OwnershipVerdict.Owned, true, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);
        bool paused = await autoPause.ApplyAsync(OwnershipVerdict.Owned, false, null, At, At, AirPodsWhere.ThisPc, Container, Container, CancellationToken.None);

        Assert.IsFalse(paused);
        Assert.AreEqual(0, sessions.PauseCalls.Count, "Safe mode never reaches the inner sessions.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode"));
    }
}
