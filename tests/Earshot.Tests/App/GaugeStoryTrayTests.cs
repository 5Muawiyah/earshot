using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tests.Streaming;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.App;

// The tray telling the gauge's story from the one place every connect and disconnect goes through: a sweep while it runs, then a
// tick for a connect that worked, a cross for an attempt that failed, and nothing for a disconnect that worked or a toggle that
// was cancelled. Its end always comes, whatever the toggle did.
[TestClass]
public sealed class GaugeStoryTrayTests
{
    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    private static ConnectResult Confirmed(string message) => new(ConnectOutcome.Confirmed, message, []);

    private static ConnectResult TimedOut() =>
        new(ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]);

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private sealed class Seen
    {
        public List<GaugeStoryView> Views { get; } = [];

        public GaugeStoryKind[] Kinds => Views.Select(v => v.Kind).ToArray();
    }

    private static Seen Watch(TrayHarness tray)
    {
        var seen = new Seen();
        GaugeStoryHost story = tray.Context.GaugeStoryForTest;
        story.Changed += () => seen.Views.Add(story.Current);
        return seen;
    }

    [TestMethod]
    public void AConnectSweepsWhileItRunsThenTicksFor800MillisecondsAndSettles()
    {
        StaThread.Run(() =>
        {
            var time = new TestTimeProvider();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: time);
            Seen seen = Watch(tray);
            var connect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => connect.Task;

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            GaugeStoryView running = tray.Context.GaugeStoryForTest.Current;
            Assert.AreEqual(GaugeStoryKind.Spinning, running.Kind);
            Assert.IsTrue(running.Connect);

            connect.SetResult(Confirmed("Connected"));
            tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.Tick, tray.Context.GaugeStoryForTest.Current.Kind);

            time.Advance(Ms(799));
            tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.Tick, tray.Context.GaugeStoryForTest.Current.Kind);

            time.Advance(Ms(1));
            tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.None, tray.Context.GaugeStoryForTest.Current.Kind);
            CollectionAssert.AreEqual(new[] { GaugeStoryKind.Spinning, GaugeStoryKind.Tick, GaugeStoryKind.None }, seen.Kinds);
        });
    }

    [TestMethod]
    public void AConnectThatFailsShowsACrossFor1500Milliseconds()
    {
        StaThread.Run(() =>
        {
            var time = new TestTimeProvider();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: time);
            tray.Connection.OnConnect = _ => Task.FromResult(TimedOut());

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            GaugeStoryView cross = tray.Context.GaugeStoryForTest.Current;
            Assert.AreEqual(GaugeStoryKind.Cross, cross.Kind);
            Assert.IsTrue(cross.Connect);
            time.Advance(Ms(1499));
            tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.Cross, tray.Context.GaugeStoryForTest.Current.Kind);
            time.Advance(Ms(1));
            tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.None, tray.Context.GaugeStoryForTest.Current.Kind);
        });
    }

    [TestMethod]
    public void TheSweepEndsWhenTheReportComesAndNotWhenTheLooksAfterAFailureHaveFinished()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: new TestTimeProvider());
            tray.Connection.OnConnect = _ => Task.FromResult(TimedOut());
            Seen seen = Watch(tray);

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { GaugeStoryKind.Spinning, GaugeStoryKind.Cross }, seen.Kinds, "One start, one end.");
        });
    }

    [TestMethod]
    public void ADisconnectThatWorksGoesStraightToWhatIsTrueWithNoTick()
    {
        StaThread.Run(() =>
        {
            var time = new TestTimeProvider();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), time: time);
            Seen seen = Watch(tray);
            int timersBefore = time.LiveTimers;

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.AreEqual(GaugeStoryKind.None, tray.Context.GaugeStoryForTest.Current.Kind);
            CollectionAssert.AreEqual(new[] { GaugeStoryKind.Spinning, GaugeStoryKind.None }, seen.Kinds);
            Assert.IsFalse(seen.Views[0].Connect, "A disconnect says so.");
            Assert.AreEqual(timersBefore, time.LiveTimers, "No hold to time.");
        });
    }

    [TestMethod]
    public void AToggleCancelledByANewerClickShowsNoCrossAndTheNewOneSweeps()
    {
        StaThread.Run(() =>
        {
            long now = 0;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), tickCount: () => now, time: new TestTimeProvider());
            Seen seen = Watch(tray);
            tray.Connection.OnConnect = async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                }

                return new ConnectResult(ConnectOutcome.Failed, "Cancelled", []);
            };
            var disconnect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnDisconnect = _ => disconnect.Task;

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            now += 1200;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            TrayHarness.PumpUntil(() => tray.Context.GaugeStoryForTest.Current is { Kind: GaugeStoryKind.Spinning, Connect: false }, "the disconnect sweeps");

            Assert.IsFalse(seen.Kinds.Contains(GaugeStoryKind.Cross), "A cancelled connect says nothing of itself.");
            Assert.IsFalse(seen.Kinds.Contains(GaugeStoryKind.Tick));

            disconnect.SetResult(Confirmed("Disconnected"));
            tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.None, tray.Context.GaugeStoryForTest.Current.Kind);
        });
    }

    [TestMethod]
    public void AToggleThatThrowsStillEndsTheStoryAndTheGaugeIsNotLeftTurning()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: new TestTimeProvider());
            tray.Connection.OnConnect = _ => throw new InvalidOperationException("a failure the toggle did not plan for");

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.AreNotEqual(GaugeStoryKind.Spinning, tray.Context.GaugeStoryForTest.Current.Kind, "ToggleEnded comes from the finally.");
        });
    }

    [TestMethod]
    public void TheButtonOnTheCardIsToldAsFromTheCardAndAnythingElseAsFromElsewhere()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: new TestTimeProvider());
            var connect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => connect.Task;

            tray.Context.StartToggleFromWidget(CardPlace.NearTray);
            Assert.AreEqual(ToggleOrigin.Card, tray.Context.GaugeStoryForTest.Origin);
            connect.SetResult(Confirmed("Connected"));
            tray.PumpUntilIdle();

            var again = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnDisconnect = _ => again.Task;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            Assert.AreEqual(ToggleOrigin.Elsewhere, tray.Context.GaugeStoryForTest.Origin);
            again.SetResult(Confirmed("Disconnected"));
            tray.PumpUntilIdle();
        });
    }

    // A connect that took but whose protection did not (BlockCoordinator.ConnectedNotProtectedMessage) is told as Partial, not as a
    // failure: the gauge shows no cross and the card does not grow to the failed card.
    [TestMethod]
    [DataRow(OpStatus.Success, false, "Succeeded")]
    [DataRow(OpStatus.AlreadyInState, false, "Succeeded")]
    [DataRow(OpStatus.Partial, false, "Partial")]
    [DataRow(OpStatus.Failed, false, "Failed")]
    [DataRow(OpStatus.NotAttempted, false, "Failed")]
    [DataRow(OpStatus.Partial, true, "Cancelled")]
    [DataRow(OpStatus.Success, true, "Cancelled")]
    public void AFinishedToggleIsToldAsItsStatusSaysAndPartialIsItsOwnOutcome(OpStatus status, bool cancelled, string expected)
    {
        var report = new ToggleReport(true, status, "Connected, but audio quality protection did not apply.", [], cancelled);

        Assert.AreEqual(expected, TrayContext.OutcomeOf(report, cancelled).ToString());
    }
}
