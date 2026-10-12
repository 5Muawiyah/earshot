using Earshot.App;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Connect in Windows, a connection made in Windows' own Bluetooth settings, and the races between such a connection and the
// coordinator's own blocks. The rule: a link that forms while Earshot has the nodes enabled (its own connect, its idle grace or
// a Connect in Windows window) in an unlocked session is the owner's and is never cut; a link that forms outside all of those is
// blocked and reported; a block that finds a live link at the gate changes nothing.
[TestClass]
public sealed class WindowsConnectRaceTests
{
    private static readonly TimeSpan Grace = BlockCoordinator.IdleGrace;
    private static readonly string[] AllowOnly = ["allow"];
    private static readonly string[] BlockIdleOnly = ["block-idle"];
    private static readonly string[] BlockOnly = ["block"];

    private static CoordinatorHarness BlockedAtRest()
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Start();
        return h;
    }

    private static CoordinatorHarness AllowedAndIdle()
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Start();

        // In use at start-up leaves the nodes alone; the AirPods then go idle, which starts the countdown.
        h.Publish(Devices.Idle(2));
        return h;
    }

    private static ToggleReport Done(CoordinatorHarness h, Task<ToggleReport> task)
    {
        h.Pump();
        for (int i = 0; i < 400 && !task.IsCompleted; i++)
        {
            h.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.IsTrue(task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static IEnumerable<string> Blocks(CoordinatorHarness h) =>
        h.Block.GateVerbCalls.Where(c => c is "block" or "block-idle" || c.StartsWith("undo-allow", StringComparison.Ordinal));

    // ---- Connect in Windows ----

    [TestMethod]
    public void ConnectInWindowsAllowsFirstThenOpensTheBluetoothSettingsPageOnce()
    {
        using CoordinatorHarness h = BlockedAtRest();

        ToggleReport report = Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        Assert.IsTrue(report.IsSuccess, report.UserMessage);
        CollectionAssert.AreEqual(AllowOnly, h.Block.GateVerbCalls);
        Assert.AreEqual(1, h.Launcher.Opened);
        Assert.IsTrue(h.Coordinator.WindowsConnectOpen);
        Assert.AreEqual(ConnectHistoryActions.ConnectInWindows, h.History.Read().Single().Action);
    }

    [TestMethod]
    public void NoIdleBlockIsSentWhileTheWindowIsOpenAndOneFollowsAFreshGraceAfterIt()
    {
        using CoordinatorHarness h = BlockedAtRest();
        Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        h.Advance(TimeSpan.FromSeconds(119));
        Assert.IsEmpty(Blocks(h), "Nothing is blocked inside the two minutes.");
        Assert.IsTrue(h.Coordinator.WindowsConnectOpen);

        h.Advance(TimeSpan.FromSeconds(2));
        Assert.IsFalse(h.Coordinator.WindowsConnectOpen);
        h.Advance(Grace - TimeSpan.FromSeconds(2));
        Assert.IsEmpty(Blocks(h), "The idle wait starts again when the window ends.");

        h.Advance(TimeSpan.FromSeconds(3));
        Assert.HasCount(1, Blocks(h));
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ALinkThatFormsInsideTheWindowIsTheOwnersEvenWhenTheSessionLocksLater()
    {
        using CoordinatorHarness h = BlockedAtRest();
        Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));
        h.Advance(TimeSpan.FromSeconds(40));
        h.Publish(Devices.Active(700));
        h.Link.State = LinkState.Connected;
        h.Advance(TimeSpan.FromSeconds(20));
        h.Session.State = SessionLockState.Locked;

        h.Advance(TimeSpan.FromMinutes(5));

        Assert.IsEmpty(Blocks(h), "It was made while unlocked and the window was open, so it is kept.");
        Assert.IsTrue(h.History.Read().Any(e => e.Action == ConnectHistoryActions.WindowsConnection), "The connection made in Windows is recorded.");

        // When the link drops the idle rule starts again from a fresh grace.
        h.Link.State = LinkState.Disconnected;
        h.Publish(Devices.Idle(701));
        h.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.IsEmpty(Blocks(h));
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, Blocks(h));
    }

    // ---- a link outside any window ----

    [TestMethod]
    public void ALinkThatFirstAppearsWhileLockedOutsideAnyWindowIsBlockedAndReported()
    {
        using CoordinatorHarness h = AllowedAndIdle();
        h.Advance(TimeSpan.FromSeconds(5));
        h.Session.State = SessionLockState.Locked;

        h.Link.State = LinkState.Connected;
        h.Publish(Devices.Active(800));
        h.Advance(TimeSpan.FromSeconds(10));

        CollectionAssert.Contains(h.Block.GateVerbCalls, "block", "A plain block: it cuts the link on purpose.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.Contains(BlockCoordinator.ForeignLinkMessage, h.Cards.Statuses);
    }

    [TestMethod]
    public void AnUnknownLockStateCountsAsLocked()
    {
        using CoordinatorHarness h = AllowedAndIdle();
        h.Session.State = SessionLockState.Unknown;

        h.Link.State = LinkState.Connected;
        h.Publish(Devices.Active(800));
        h.Advance(TimeSpan.FromSeconds(10));

        CollectionAssert.Contains(h.Block.GateVerbCalls, "block");
    }

    [TestMethod]
    public void ALinkWhileUnlockedAndRenderingKeepsTheNodesAsToday()
    {
        using CoordinatorHarness h = AllowedAndIdle();

        h.Link.State = LinkState.Connected;
        h.Publish(Devices.Active(800));
        h.Advance(TimeSpan.FromMinutes(10));

        Assert.IsEmpty(Blocks(h));
        Assert.AreEqual(BlockState.Allowed, h.Block.Status.State);
    }

    // ---- re-reading before an automatic block ----

    [TestMethod]
    public void TheIdleBlockUsesTheVerbThatRefusesALiveLink()
    {
        using CoordinatorHarness h = AllowedAndIdle();

        h.Advance(Grace + TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(BlockIdleOnly, h.Block.GateVerbCalls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsGreaterThan(0, h.Link.Reads, "The link is read again just before the block.");
    }

    [TestMethod]
    public void ALinkBitThatReadsConnectedWhileTheEndpointIsUnpluggedIsOnlyCalledStuckAfterTheSettle()
    {
        using CoordinatorHarness h = AllowedAndIdle();
        h.Link.State = LinkState.Connected;

        h.Advance(Grace + BlockCoordinator.LinkSettle - TimeSpan.FromSeconds(1));
        Assert.IsEmpty(Blocks(h), "The bit may be a few seconds ahead of the endpoint, so it is waited for.");

        h.Advance(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(BlockOnly, h.Block.GateVerbCalls, "It still disagrees with the endpoint after the settle, so the bit is stuck: a plain block.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ALinkThatReadsConnectedWithNoEndpointIsDeferredForTwoMinutesThenBlockedAndReported()
    {
        using CoordinatorHarness h = AllowedAndIdle();
        h.Publish(Devices.NotPresent(900));
        h.Link.State = LinkState.Connected;

        h.Advance(Grace + TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(60));
        Assert.IsEmpty(Blocks(h), "An unconfirmed 'connected' is not cut.");

        h.Advance(TimeSpan.FromMinutes(2));

        CollectionAssert.AreEqual(BlockOnly, h.Block.GateVerbCalls);
        Assert.Contains(BlockCoordinator.UnconfirmedLinkMessage, h.Cards.Statuses);
    }

    [TestMethod]
    public void AGateThatFindsALiveLinkLeavesTheNodesEnabledWithoutCountingAFailure()
    {
        using CoordinatorHarness h = AllowedAndIdle();
        h.Link.State = LinkState.Disconnected;
        h.Link.GateSees = LinkState.Connected;

        h.Advance(Grace + TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(BlockIdleOnly, h.Block.GateVerbCalls);
        Assert.AreEqual(BlockState.Allowed, h.Block.Status.State, "The gate changed nothing.");
        Assert.AreEqual(0, h.Coordinator.IdleFailures);
        Assert.IsEmpty(h.Cards.Statuses);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning || h.Coordinator.RecheckRunning, "The idle rule is not left without anything in hand.");
    }

    // ---- races ----

    [TestMethod]
    public void AWindowsConnectWhoseLinkBitLeadsTheEndpointAtSecondTwentyNineOfTheIdleCountdownIsNotCut()
    {
        using CoordinatorHarness h = AllowedAndIdle();
        h.Advance(TimeSpan.FromSeconds(29));

        h.Link.State = LinkState.Connected;
        h.Advance(TimeSpan.FromSeconds(4));
        h.Publish(Devices.Active(810));
        h.Advance(BlockCoordinator.LinkSettle + TimeSpan.FromSeconds(2));
        Assert.IsEmpty(Blocks(h), "Second 30 passes with the bit up and the endpoint not yet playing, and the settle ends with it playing.");

        h.Advance(TimeSpan.FromMinutes(1));
        h.Link.State = LinkState.Disconnected;
        h.Publish(Devices.Idle(811));
        h.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.IsEmpty(Blocks(h), "The countdown starts from the drop, not from before.");
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, Blocks(h));
    }

    [TestMethod]
    public void AWindowsConnectThatLandsDuringTheCleanUpIsNotCutAndTheIdleRuleStartsAgain()
    {
        using CoordinatorHarness h = BlockedAtRest();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ =>
        {
            // The clean-up reads the link as down, and by the time the gate reads it the link is up.
            h.Link.State = LinkState.Disconnected;
            h.Link.GateSees = LinkState.Connected;
            return Task.FromResult(Results.TimedOut());
        });

        ToggleReport report = Done(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)));

        Assert.IsNotNull(report);
        Assert.AreEqual(BlockState.Allowed, h.Block.Status.State, "The gate refused and the nodes stay enabled.");
        CollectionAssert.Contains(h.Block.Calls, "block-refused");

        // The link is real: render goes ACTIVE, then drops. A fresh grace follows the drop.
        h.Link.State = LinkState.Connected;
        h.Publish(Devices.Active(820));
        h.Advance(TimeSpan.FromMinutes(1));
        h.Link.State = LinkState.Disconnected;
        h.Link.GateSees = null;
        h.Publish(Devices.Idle(821));
        h.Advance(Grace + TimeSpan.FromSeconds(1));
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ALinkBitThatLeadsTheEndpointDuringTheCleanUpIsNotCut()
    {
        using CoordinatorHarness h = BlockedAtRest();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        bool risen = false;
        h.Connection.Connects.Enqueue(_ =>
        {
            // The connect timed out, and just after it the bit rises with the endpoint still not playing.
            h.Link.State = LinkState.Connected;
            risen = true;
            return Task.FromResult(Results.TimedOut());
        });

        Task<ToggleReport> task = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true));
        h.Pump();
        int risenAt = -1;
        for (int i = 0; i < 400 && !task.IsCompleted; i++)
        {
            h.Advance(TimeSpan.FromMilliseconds(250));
            if (risenAt < 0 && risen)
            {
                risenAt = i;
            }

            if (risenAt >= 0 && i == risenAt + 12)
            {
                h.Publish(Devices.Active(830));
            }
        }

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(risen, "The bit rose.");
        Assert.IsEmpty(Blocks(h), "The link is the owner's, three seconds from ACTIVE: nothing is sent to cut it.");
        Assert.AreEqual(BlockState.Allowed, h.Block.Status.State);
    }

    [TestMethod]
    public void AStuckBitAtTheCleanUpAsksTheGateToUndoItsAllowAndNeverSendsTheFullBlock()
    {
        using CoordinatorHarness h = BlockedAtRest();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Link.State = LinkState.Connected;
            return Task.FromResult(Results.TimedOut());
        });

        ToggleReport report = Done(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)));

        string calls = string.Join(", ", h.Block.Calls) + " / " + report.UserMessage + " / " + string.Join(", ", h.Block.GateVerbCalls);
        CollectionAssert.DoesNotContain(h.Block.GateVerbCalls, "block", "The connect's clean-up never sends the full block: " + calls);
        Assert.IsTrue(h.Block.GateVerbCalls.Any(c => c.StartsWith("undo-allow", StringComparison.Ordinal)), "It asks the gate to undo its own allow: " + calls);
    }
}
