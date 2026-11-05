using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The leave Earshot did not start is told to whoever listens (the moved-to rule), whether or not pause on leave is on, and an
// own leave, a change in flight or a leave already decided is not. Pause on leave itself is exercised in PauseOnLeaveTests.
[TestClass]
public sealed class PauseOnLeaveLeftByOtherTests
{
    private static readonly Guid Container = Devices.Container;
    private static readonly string[] EventThenPause = ["left-by-other", "pause"];
    private static readonly string[] PlayerOnly = ["player.exe"];
    private static readonly bool[] ToldPlaying = [true];
    private static readonly bool[] ToldNotPlaying = [false];

    private sealed class Rig : IDisposable
    {
        public ManualTime Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public List<string> Trace { get; } = new();

        public FakeRenderActivity Activity { get; } = new();

        public TracingMediaSessions Sessions { get; }

        public bool Enabled { get; set; } = true;

        public List<DateTimeOffset> Raised { get; } = new();

        public List<bool> RaisedPlaying { get; } = new();

        public PauseOnLeave Pause { get; }

        public Rig()
        {
            Sessions = new TracingMediaSessions { Sessions = { TracingMediaSessions.Playing("player.exe") }, Trace = Trace };
            Pause = new PauseOnLeave(Sessions, Activity, () => Enabled, Time, Log);
            Pause.LeftByOther += (at, playing) =>
            {
                Raised.Add(at);
                RaisedPlaying.Add(playing);
                Trace.Add("left-by-other");
            };
        }

        public Task Active() => Pause.OnRender(RenderState.Active, Container, Time.GetUtcNow(), changeInFlight: null);

        public Task Left(string? changeInFlight = null) => Pause.OnRender(RenderState.NotActive, Container, Time.GetUtcNow(), changeInFlight);

        public void Dispose() => Pause.Dispose();
    }

    [TestMethod]
    public async Task AForeignLeaveIsToldWithTheTimeItWasSeen()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        DateTimeOffset seen = rig.Time.GetUtcNow();

        await rig.Left();

        CollectionAssert.AreEqual(new[] { seen }, rig.Raised);
    }

    // Turning the pause off leaves Moved to alone.
    [TestMethod]
    public async Task AForeignLeaveIsToldWithPauseOnLeaveOff()
    {
        using var rig = new Rig { Enabled = false };
        await rig.Active();

        await rig.Left();

        Assert.HasCount(1, rig.Raised);
        Assert.IsEmpty(rig.Sessions.PauseCalls, "Pause on leave was off but something was paused.");
    }

    // The event comes just before the decision, so a slow pause cannot delay it.
    [TestMethod]
    public async Task TheEventComesBeforeThePause()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        CollectionAssert.AreEqual(EventThenPause, rig.Trace.Where(t => t != "read").ToArray());
    }

    [TestMethod]
    public async Task AnOwnLeaveIsNotTold()
    {
        using var rig = new Rig();
        await rig.Active();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        rig.Pause.AfterOwnLeave(left: true);
        await rig.Left();

        Assert.IsEmpty(rig.Raised);
    }

    // The own leave did not take them off: the next leave of the same stretch is the phone's, and is told.
    [TestMethod]
    public async Task AForeignLeaveAfterAnOwnLeaveThatFailedIsTold()
    {
        using var rig = new Rig();
        await rig.Active();
        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        rig.Pause.AfterOwnLeave(left: false);

        await rig.Left();

        Assert.HasCount(1, rig.Raised);
    }

    [TestMethod]
    public async Task ALeaveDuringAnEarshotChangeIsNotTold()
    {
        using var rig = new Rig();
        await rig.Active();

        await rig.Left(changeInFlight: "protect");

        Assert.IsEmpty(rig.Raised);
    }

    [TestMethod]
    public async Task OneLeaveIsToldOnce()
    {
        using var rig = new Rig();
        await rig.Active();
        await rig.Left();

        await rig.Left();

        Assert.HasCount(1, rig.Raised);
    }

    [TestMethod]
    public async Task AReadThatFailedIsNotALeave()
    {
        using var rig = new Rig();
        await rig.Active();

        await rig.Pause.OnRender(RenderState.Unknown, Container, rig.Time.GetUtcNow(), changeInFlight: null);

        Assert.IsEmpty(rig.Raised);
    }

    // A listener that throws must not take pause on leave down with it.
    [TestMethod]
    public async Task AListenerThatThrowsDoesNotStopThePause()
    {
        using var rig = new Rig();
        rig.Pause.LeftByOther += (_, _) => throw new InvalidOperationException("listener failed");
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        CollectionAssert.AreEqual(PlayerOnly, rig.Sessions.PauseCalls);
        Assert.IsTrue(rig.Log.Has(LogLevel.Error, "Pause on leave: telling the leave to its listener failed"));
    }

    // ---- Whether this PC was playing to them, which is what Moved to rests on

    [TestMethod]
    public async Task AForeignLeaveIsToldPlayingWhenTheLastReadingSaysPlaying()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Playing;
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        CollectionAssert.AreEqual(ToldPlaying, rig.RaisedPlaying);
    }

    [TestMethod]
    public async Task AForeignLeaveIsToldNotPlayingWhenTheLastReadingSaysSilent()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Silent;
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        CollectionAssert.AreEqual(ToldNotPlaying, rig.RaisedPlaying);
    }

    // A reading too old to say is not "playing".
    [TestMethod]
    public async Task AForeignLeaveIsToldNotPlayingWhenTheReadingIsTooOld()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Playing;
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));
        rig.Activity.OnRead = (_, _) => throw new InvalidOperationException("no reading");
        rig.Time.Advance(PauseOnLeave.SampleFreshness + TimeSpan.FromSeconds(30));

        await rig.Left();

        CollectionAssert.AreEqual(ToldNotPlaying, rig.RaisedPlaying);
    }

    // The pause being off must not blind Moved to: with someone listening the readings are still taken, and nothing is paused.
    [TestMethod]
    public async Task WithPauseOnLeaveOffTheReadingsAreStillTakenForWhoeverListens()
    {
        using var rig = new Rig { Enabled = false };
        rig.Activity.State = RenderActivityState.Playing;
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        CollectionAssert.AreEqual(ToldPlaying, rig.RaisedPlaying);
        Assert.IsEmpty(rig.Sessions.PauseCalls, "Pause on leave was off but something was paused.");
    }

    // ---- The lines the morning reads

    [TestMethod]
    public async Task AForeignLeaveSaysWhatThisPcWasPlaying()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Silent;
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromMilliseconds(600));

        await rig.Left();

        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Move: leave seen, not started by Earshot, playing=Silent."));
    }

    [TestMethod]
    public async Task AForeignLeaveWithNoRecentReadingSaysUnknown()
    {
        using var rig = new Rig();
        await rig.Active();
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        rig.Activity.OnRead = (_, _) => throw new InvalidOperationException("no reading");
        rig.Time.Advance(TimeSpan.FromSeconds(5));

        await rig.Left();

        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Move: leave seen, not started by Earshot, playing=Unknown."));
    }

    [TestMethod]
    public async Task AnOwnLeaveSaysItWasOwnAndShowsNothing()
    {
        using var rig = new Rig();
        await rig.Active();

        await rig.Pause.BeforeOwnLeaveAsync("Disconnect", Container, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        await rig.Left();

        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Move: own leave (Disconnect), nothing shown."));
        Assert.IsFalse(rig.Log.Has(LogLevel.Info, "Move: leave seen"));
    }
}
