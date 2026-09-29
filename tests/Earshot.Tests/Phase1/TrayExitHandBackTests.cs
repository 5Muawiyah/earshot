using System.Diagnostics;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// Exit from the menu while the AirPods are connected to this PC hands them back, the way a shut down does: the
// real TrayContext on an STA thread with fake controllers, exactly as TrayContextTests drives Exit. Nothing here
// reaches a device, a window message or Task Scheduler.
[TestClass]
public sealed class TrayExitHandBackTests
{
    private static readonly string[] DisconnectThenBlock = ["disconnect", "block"];

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    // The AirPods are in use and their nodes enabled: what a normal listening session looks like to Exit.
    private static TrayHarness InUse(Action<EarshotSettings>? settings = null, TimeProvider? time = null, TimeSpan? budget = null, TimeSpan? disconnectWait = null) =>
        new(snapshot: Devices.Active(1), settings: settings, time: time, handBackBudget: budget, disconnectHandBackWait: disconnectWait,
            arrange: t => t.Block.Status = Block(BlockState.Allowed));

    // Disconnect, then block, in that order and no other: the order the fakes were called in.
    [TestMethod]
    public void ExitWhileConnectedDisconnectsThenBlocksAndSaysNothingIsWrong()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = InUse();
            var order = new List<string>();
            tray.Connection.OnDisconnect = _ =>
            {
                order.Add("disconnect");
                return Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));
            };
            tray.Block.OnBlock = _ =>
            {
                order.Add("block");
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            CollectionAssert.AreEqual(DisconnectThenBlock, order, "Exit did not release the AirPods before it blocked them.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): started at"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): disconnect S_OK, confirmed after"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): block sent at"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): finished in"));

            // The card that says what is happening follows the Exit click; nothing says anything went wrong.
            CardShown card = tray.Cards.Shown.Single(c => c.Content.Status == TrayContext.ExitHandBackMessage);
            Assert.AreEqual(TrayHarness.ClickPoint, card.ClickPoint);
            Assert.AreEqual(1, tray.Cards.Shown.Count, "A clean hand-back shows only the card that says it is running.");
            CollectionAssert.DoesNotContain(tray.Cards.Statuses, BlockCoordinator.ClosedWhileInUseMessage);
        });
    }

    // Off is what Exit was before: nothing is disconnected, nothing blocked, and the card says it closed in use.
    [TestMethod]
    public void ExitWhileConnectedWithHandBackOffIsAsItWasBefore()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = InUse(s => s.HandBackOnShutdownAndSleep = false);
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            Assert.IsEmpty(tray.Connection.Calls, "Exit disconnected the AirPods with Hand back off.");
            Assert.IsEmpty(tray.Block.Calls);
            Assert.AreEqual(BlockCoordinator.ClosedWhileInUseMessage, tray.Cards.Shown.Single().Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Closing while the AirPods are in use"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back: off, so nothing runs for this Exit."));
        });
    }

    // Not connected: Exit is exactly today's, with no hand-back line and no card that says one is running.
    [TestMethod]
    public void ExitWhileNotConnectedHandsNothingBack()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Idle(1), arrange: t => t.Block.Status = Block(BlockState.Allowed));
            int blocksBefore = tray.Block.Calls.Count(c => c == "block");
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            Assert.IsEmpty(tray.Connection.Calls, "Exit disconnected AirPods that were not connected.");
            Assert.AreEqual(blocksBefore + 1, tray.Block.Calls.Count(c => c == "block"), "Exit no longer blocks the nodes it used to.");
            CollectionAssert.DoesNotContain(tray.Cards.Statuses, TrayContext.ExitHandBackMessage);
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Hand-back (exit)"), "A hand-back line was written for an Exit with nothing to hand back.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Hand-back: off"));
        });
    }

    // A block that did not take is said at the click, before Exit ends the loop, with the raw code in the log.
    [TestMethod]
    public void ExitWhoseBlockFailsSaysSoBeforeExitingAndLogsTheRawCode()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = InUse();
            tray.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail("Could not block", [StepOutcomes.FromWin32("cm-disable:BTHENUM", 5)]));
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            CardShown notice = tray.Cards.Shown.Single(c => c.Content.Status == BlockCoordinator.ExitNotBlockedMessage);
            Assert.AreEqual(TrayHarness.ClickPoint, notice.ClickPoint);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): finished in"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "block Failed: cm-disable:BTHENUM ERROR_ACCESS_DENIED"),
                "The raw code of the step that failed is not in the log line.");
        });
    }

    // A hand-back that runs out of its cap leaves Exit in bounded time, says the change did not finish, and never sends
    // the block it never got to.
    [TestMethod]
    public void ExitWhoseDisconnectNeverReturnsIsCutShortAndSaysSo()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = InUse(time: TimeProvider.System, budget: TimeSpan.FromMilliseconds(200), disconnectWait: TimeSpan.FromSeconds(30));
            tray.Connection.OnDisconnect = _ => new TaskCompletionSource<ConnectResult>().Task;
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            var watch = Stopwatch.StartNew();
            Application.Run(tray.Context);

            Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, "Exit waited on a disconnect that never came back.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Hand-back (exit): cut short at"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "still running: disconnect"));
            Assert.IsEmpty(tray.Block.Calls.Where(c => c == "block"), "The block was sent after a disconnect that never confirmed within the cap.");
            CollectionAssert.Contains(tray.Cards.Statuses, BlockCoordinator.ClosedBeforeChangeEndedMessage);
        });
    }

    // Exit clicked while a connect is in flight: the connect is cancelled as it always was. If it had reached ACTIVE
    // by then, the AirPods are connected when Exit's own turn comes, so they are handed back; the wait for the connect
    // is unchanged.
    [TestMethod]
    public void ExitDuringAConnectThatReachesActiveHandsTheAirPodsBack()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Idle(1), arrange: t => t.Block.Status = Block(BlockState.Allowed));
            var order = new List<string>();
            tray.Connection.OnConnect = async ct =>
            {
                order.Add("connect");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    order.Add("connect cancelled");
                }

                // The link came up in the instant the cancel landed: what a connect does not undo.
                tray.Monitor.Raise(Devices.Active(2));
                return new ConnectResult(ConnectOutcome.Confirmed, "Connected", []);
            };
            tray.Connection.OnDisconnect = _ =>
            {
                order.Add("disconnect");
                return Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));
            };
            tray.Block.OnBlock = _ =>
            {
                order.Add("block");
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };

            tray.Ui.Post(
                _ =>
                {
                    tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                    tray.ClickMenu(MenuModel.Exit);
                },
                null);
            Application.Run(tray.Context);

            Assert.AreEqual("connect", order[0]);
            Assert.AreEqual("connect cancelled", order[1]);
            CollectionAssert.AreEqual(DisconnectThenBlock, order.Skip(2).Where(o => o is "disconnect" or "block").ToArray(),
                "The AirPods a cancelled connect left connected were not handed back before Exit ended.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back (exit): finished in"));
        });
    }

    // Exit clicked while a disconnect is in flight: the disconnect finishes as it always did and is not sent again,
    // because the AirPods are no longer connected by the time Exit's own turn comes.
    [TestMethod]
    public void ExitDuringADisconnectDoesNotSendASecondDisconnect()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = InUse();
            var release = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnDisconnect = _ => release.Task;
            tray.Ui.Post(
                _ =>
                {
                    // A left click on connected AirPods is a disconnect.
                    tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                    tray.ClickMenu(MenuModel.Exit);
                    tray.Monitor.Raise(Devices.Idle(2));
                    release.SetResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));
                },
                null);

            Application.Run(tray.Context);

            Assert.AreEqual(1, tray.Connection.Calls.Count(c => !c.Connect), "Exit sent a second disconnect on top of the one in flight.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Hand-back (exit): started at"), "A hand-back ran for AirPods that had already been let go.");
        });
    }
}
