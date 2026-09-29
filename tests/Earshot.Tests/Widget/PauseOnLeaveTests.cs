using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Pause on leave against fakes and a clock the test moves: when it pauses, and every reason it does not. Nothing here
// reaches Core Audio or Windows Media Controls.
[TestClass]
public sealed class PauseOnLeaveTests
{
    private static readonly Guid Container = Devices.Container;
    private static readonly string[] PlayerOnly = ["player.exe"];

    private sealed class Rig : IDisposable
    {
        public ManualTime Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public FakeRenderActivity Activity { get; } = new();

        public TracingMediaSessions Sessions { get; } = new() { Sessions = { TracingMediaSessions.Playing("player.exe") } };

        public bool Enabled { get; set; } = true;

        public PauseOnLeave Pause { get; }

        public Rig() => Pause = new PauseOnLeave(Sessions, Activity, () => Enabled, Time, Log);

        // The AirPods become this PC's output: a snapshot with render ACTIVE, seen now.
        public Task Active() => Pause.OnRender(RenderState.Active, Container, Time.GetUtcNow(), changeInFlight: null);

        // They go: render no longer ACTIVE, seen now.
        public Task Left(string? changeInFlight = null) => Pause.OnRender(RenderState.NotActive, Container, Time.GetUtcNow(), changeInFlight);

        // One tick of the sampler (the timer fires at once and every second after).
        public void Tick(TimeSpan? by = null) => Time.Advance(by ?? TimeSpan.Zero);

        public void Dispose() => Pause.Dispose();
    }

    // The rule that matters most: it was playing to the AirPods, one session is playing, they go, that session is
    // paused, and the line says how long after the change was seen.
    [TestMethod]
    public async Task APhoneTakingTheAirPodsWhilePlayingPausesTheOnePlayingSession()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();
        rig.Tick(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        CollectionAssert.AreEqual(PlayerOnly, rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Pause on leave: the AirPods left this PC (change seen at "));
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Paused player.exe 0 ms after the change was seen; this PC was playing to them at the last reading, 600 ms before."));
    }

    // Never resumes: no play call is made when they go, when they come back, or at any time after.
    [TestMethod]
    public async Task ItNeverResumes()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();
        await rig.Left();

        await rig.Active();
        rig.Tick(TimeSpan.FromSeconds(30));

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count);
        Assert.IsEmpty(rig.Sessions.PlayCalls, "Something was played again after the AirPods left or came back.");
    }

    // Not playing to them: the reading said silent, so nothing is paused even though another session is playing (a
    // video to the speakers).
    [TestMethod]
    public async Task ItDoesNotPauseWhenThisPcWasNotPlayingToTheAirPods()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Silent;
        await rig.Active();
        rig.Tick();

        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: this PC was not playing to them (last reading: silent)."));
    }

    // No reading at all before they went (they left the instant they appeared): nothing to go on, so nothing is paused.
    [TestMethod]
    public async Task ItDoesNotPauseWithNoReadingBeforeTheLeave()
    {
        using var rig = new Rig();
        await rig.Active();

        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(0, rig.Activity.Reads);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: no reading of the AirPods audio was taken before they left."));
    }

    // A reading older than SampleFreshness says nothing about now. A read that never comes back keeps the sampler from
    // taking a newer one, so the first reading goes stale.
    [TestMethod]
    public async Task ItDoesNotPauseOnAReadingThatHasGoneStale()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();
        rig.Activity.OnRead = (_, _) => new TaskCompletionSource<RenderActivityReading>().Task;
        rig.Tick(TimeSpan.FromSeconds(10));

        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: the last reading of the AirPods audio was 10000 ms old."));
    }

    [TestMethod]
    public async Task ItDoesNotPauseWhenTheSettingIsOffAndTakesNoReadings()
    {
        using var rig = new Rig();
        rig.Enabled = false;
        await rig.Active();
        rig.Tick(TimeSpan.FromSeconds(5));

        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(0, rig.Activity.Reads, "It read the AirPods' audio with the setting off.");
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: Pause when AirPods leave this PC is off."));
    }

    // The setting is read at each use: turned on while the AirPods are in use, the next reading is taken.
    [TestMethod]
    public async Task TurningTheSettingOnTakesEffectWithoutARestart()
    {
        using var rig = new Rig();
        rig.Enabled = false;
        await rig.Active();
        rig.Tick(TimeSpan.FromSeconds(2));

        rig.Enabled = true;
        rig.Tick(TimeSpan.FromSeconds(1));
        await rig.Left();

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task TwoSessionsPlayingPauseNeitherAndTheLineSaysWhy()
    {
        using var rig = new Rig();
        rig.Sessions.Sessions.Add(TracingMediaSessions.Playing("video.exe"));
        await rig.Active();
        rig.Tick();

        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: 2 media sessions are playing, so none was paused."));
    }

    [TestMethod]
    public async Task NoPlayingSessionMeansNothingToPause()
    {
        using var rig = new Rig();
        rig.Sessions.Sessions.Clear();
        await rig.Active();
        rig.Tick();

        await rig.Left();

        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: no media session is playing."));
    }

    [TestMethod]
    public async Task APauseWindowsRefusesIsALoggedWarningNotAnException()
    {
        using var rig = new Rig();
        rig.Sessions.PauseResult = false;
        await rig.Active();
        rig.Tick();

        await rig.Left();

        Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "Not paused: Windows did not take the pause for player.exe."));
    }

    // A protection change drops the link and brings it back: Earshot's own doing, not a leave.
    [TestMethod]
    public async Task ALeaveDuringAnEarshotChangeIsNotALeave()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();

        await rig.Left(changeInFlight: "protect");

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: Earshot was changing the AirPods when they went away (protect)."));
    }

    // A snapshot that could not be read is not an observation: it neither ends an episode nor starts one.
    [TestMethod]
    public async Task AnUnreadableSnapshotChangesNothing()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();

        await rig.Pause.OnRender(RenderState.Unknown, Container, rig.Time.GetUtcNow(), null);
        await rig.Active();
        Assert.IsEmpty(rig.Sessions.PauseCalls, "An unreadable snapshot was taken for the AirPods leaving.");

        await rig.Left();
        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count);
    }

    // Not this PC's output to begin with: a NotActive snapshot while they were never Active is not a leave.
    [TestMethod]
    public async Task ANotActiveSnapshotWithNoEpisodeIsNotALeave()
    {
        using var rig = new Rig();

        await rig.Left();
        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsEmpty(rig.Log.Entries.Where(e => e.Message.StartsWith(PauseOnLeaveText.Prefix, StringComparison.Ordinal)));
    }

    // Another device is chosen while the old one is playing: the watched container changes, so the old device showing as
    // not active is not the AirPods leaving, and pauses nothing. The new device then gets a stretch of its own.
    [TestMethod]
    public async Task AChangeOfTheWatchedDeviceIsNotALeave()
    {
        using var rig = new Rig();
        Guid other = Guid.NewGuid();
        await rig.Active();
        rig.Tick();

        await rig.Pause.OnRender(RenderState.NotActive, other, rig.Time.GetUtcNow(), changeInFlight: null);

        Assert.IsEmpty(rig.Sessions.PauseCalls, "A change of the watched device was taken for the AirPods leaving.");
        Assert.IsEmpty(rig.Log.Entries.Where(e => e.Message.StartsWith(PauseOnLeaveText.Prefix, StringComparison.Ordinal)));

        await rig.Pause.OnRender(RenderState.Active, other, rig.Time.GetUtcNow(), changeInFlight: null);
        rig.Tick();
        await rig.Pause.OnRender(RenderState.NotActive, other, rig.Time.GetUtcNow(), changeInFlight: null);

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count, "The new device's own leave was not decided.");
    }

    // One leave, one decision: a second NotActive snapshot for the same stretch does not decide again.
    [TestMethod]
    public async Task ARepeatedNotActiveSnapshotDecidesOnce()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();

        await rig.Left();
        await rig.Left();

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count);
    }

    // The sampler stops when they leave, and starts again with the next stretch.
    [TestMethod]
    public async Task SamplingRunsOnlyWhileTheAirPodsAreThisPcsOutput()
    {
        using var rig = new Rig();
        Assert.AreEqual(0, rig.Time.ActiveTimers);

        await rig.Active();
        Assert.AreEqual(1, rig.Time.ActiveTimers);

        await rig.Left();
        Assert.AreEqual(0, rig.Time.ActiveTimers, "The sampler kept reading after the AirPods left.");

        await rig.Active();
        Assert.AreEqual(1, rig.Time.ActiveTimers);
    }

    [TestMethod]
    public void DisposingStopsTheSampler()
    {
        var rig = new Rig();
        rig.Active().GetAwaiter().GetResult();

        rig.Dispose();

        Assert.AreEqual(0, rig.Time.ActiveTimers);
    }

    // An unreadable session state is logged once with its raw code, and is neither "playing" nor "silent".
    [TestMethod]
    public async Task AnUnreadableReadingIsLoggedOnceWithItsCodeAndPausesNothing()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Unknown;
        rig.Activity.Steps = [StepOutcomes.FromHResult("render-activity-session-manager", unchecked((int)0x88890004))];
        await rig.Active();
        rig.Tick();
        rig.Tick(TimeSpan.FromSeconds(3));

        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("could not be read, so a leave cannot be judged", StringComparison.Ordinal)));
        Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "AUDCLNT_E_DEVICE_INVALIDATED"));
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: this PC was not playing to them (last reading: unknown)."));
    }

    // A read that throws is a reading that could not be taken: never an escaped exception.
    [TestMethod]
    public async Task AReadThatThrowsIsRecordedAsUnknown()
    {
        using var rig = new Rig();
        rig.Activity.OnRead = (_, _) => throw new InvalidOperationException("worker stopped");
        await rig.Active();

        rig.Tick();
        await rig.Left();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "could not be read"));
    }

    // ---- Earshot's own leave ----

    // Decided beforehand from a fresh read: paused first, then the disconnect goes.
    [TestMethod]
    public async Task BeforeAnOwnLeaveItPausesFromAFreshReadWithNoEarlierSample()
    {
        using var rig = new Rig();
        await rig.Active();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        CollectionAssert.AreEqual(PlayerOnly, rig.Sessions.PauseCalls);
        Assert.AreEqual(1, rig.Activity.Reads);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Pause on leave: before Earshot lets the AirPods go (Disconnect), paused player.exe in 0 ms; this PC was playing to them."));
    }

    [TestMethod]
    public async Task BeforeAnOwnLeaveItDoesNotPauseWhenSilent()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Silent;
        await rig.Active();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "(Disconnect). Not paused: this PC was not playing to them (last reading: silent)."));
    }

    [TestMethod]
    public async Task AnOwnLeaveWithTheSettingOffPausesNothing()
    {
        using var rig = new Rig();
        rig.Enabled = false;
        await rig.Active();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(0, rig.Activity.Reads);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: Pause when AirPods leave this PC is off."));
    }

    // Not this PC's output: nothing to pause and nothing read.
    [TestMethod]
    public async Task AnOwnLeaveWhenTheAirPodsAreNotThisPcsOutputDoesNothing()
    {
        using var rig = new Rig();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(0, rig.Activity.Reads);
    }

    // The pause made before the disconnect is not made again when the endpoint change follows.
    [TestMethod]
    public async Task TheLeaveThatFollowsAnOwnPauseIsNotDecidedTwice()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();
        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        rig.Pause.AfterOwnLeave(left: true);

        await rig.Left();

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count, "The pause was made twice for one leave.");
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "already decided before Earshot let them go"));
    }

    // A disconnect that did not take the AirPods away gives the decision back, so the phone taking them later in the same
    // stretch is decided afresh.
    [TestMethod]
    public async Task ADisconnectThatDidNotLeaveReArmsTheDecision()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();
        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        rig.Pause.AfterOwnLeave(left: false);
        rig.Sessions.PauseCalls.Clear();
        rig.Tick(TimeSpan.FromSeconds(1));

        await rig.Left();

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count, "A later leave in the same stretch was never decided.");
    }

    // A read that fails falls back on the last reading while it is still recent.
    [TestMethod]
    public async Task AFailedFreshReadFallsBackOnARecentSample()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Tick();
        rig.Activity.OnRead = (_, _) => Task.FromResult(new RenderActivityReading(RenderActivityState.Unknown, 0, [StepOutcomes.FromHResult("render-activity-session-manager", unchecked((int)0x88890004))]));

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count);
    }

    [TestMethod]
    public async Task AFailedFreshReadWithNoRecentSamplePausesNothingAndSaysWhy()
    {
        using var rig = new Rig();
        rig.Activity.OnRead = (_, _) => Task.FromResult(new RenderActivityReading(RenderActivityState.Unknown, 0, [StepOutcomes.FromHResult("render-activity-session-manager", unchecked((int)0x88890004))]));
        await rig.Active();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Not paused: the AirPods audio could not be read (render-activity-session-manager failed AUDCLNT_E_DEVICE_INVALIDATED"));
    }

    // The disconnect never waits longer than the cap for a media session: a read that never comes back ends the wait at
    // the cap, and the line says the disconnect goes ahead.
    [TestMethod]
    public async Task ASlowReadNeverHoldsUpTheDisconnectBeyondTheCap()
    {
        using var rig = new Rig();
        rig.Activity.OnRead = (_, _) => new TaskCompletionSource<RenderActivityReading>().Task;
        await rig.Active();

        Task before = rig.Pause.BeforeOwnLeaveAsync("hand-back at shut down", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        Assert.IsFalse(before.IsCompleted, "The wait ended before the cap.");
        rig.Time.Advance(TimeSpan.FromMilliseconds(400));

        // Bounded in real time, so a wait that ignored its cap fails here instead of hanging the run.
        await before.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "(hand-back at shut down), reading and pausing took longer than 400 ms, so the disconnect goes ahead and the pause carries on."));
    }

    // A pause that runs past the cap is not lost: it carries on and writes its own line when it ends.
    [TestMethod]
    public async Task APauseThatRunsPastTheCapStillFinishesAndIsLogged()
    {
        using var rig = new Rig();
        var release = new TaskCompletionSource<bool>();
        rig.Sessions.OnPause = () => release.Task;
        await rig.Active();

        Task before = rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMilliseconds(400));
        await before.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(rig.Log.Has(LogLevel.Info, "paused player.exe"));

        release.SetResult(true);

        // The pause finishes on its own thread of continuation, so the line is waited for, bounded, not slept for.
        for (int waited = 0; waited < 500 && !rig.Log.Has(LogLevel.Info, "paused player.exe in 400 ms"); waited++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "paused player.exe in 400 ms"));
    }

    // The cap is a share of the disconnect's own wait, never more than the default.
    [TestMethod]
    public void TheCapIsAQuarterOfTheDisconnectWaitAndNeverMoreThanTheDefault()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(187.5), PauseOnLeave.CapFor(TimeSpan.FromMilliseconds(750)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(375), PauseOnLeave.CapFor(TimeSpan.FromMilliseconds(1500)));
        Assert.AreEqual(PauseOnLeave.DefaultOwnLeaveCap, PauseOnLeave.CapFor(TimeSpan.FromSeconds(12)));
    }

    // Pause on leave never acts on anything but the audio: no battery or in-ear data reaches it. Its constructor takes
    // exactly the two seams and the setting, so there is no way to hand it a reading.
    [TestMethod]
    public void ItIsBuiltFromNothingButAudioMediaSessionsTheSettingAndTheClock()
    {
        var parameters = typeof(PauseOnLeave).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();

        CollectionAssert.AreEqual(
            new[] { typeof(IMediaSessions), typeof(IRenderActivity), typeof(Func<bool>), typeof(TimeProvider), typeof(ILog) },
            parameters);
    }
}
