using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// What happens at the two ends of a session: the check when Earshot starts (did the boot block hold?) and the
// best-effort block when Windows says the session is ending.
[TestClass]
public sealed class StartUpAndSessionTests
{
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] AllowThenBlock = ["allow", "block"];

    private static SessionEndingEventArgs Query() => new(isQuery: true, ending: true, flags: 0);

    [TestMethod]
    public void AtStartUpNodesThatAreEnabledAndNotInUseAreBlockedAtOnce()
    {
        using var h = new CoordinatorHarness(startedAtLogon: true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));

        h.Start();

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The nodes were left enabled at start-up.");
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
        Assert.IsEmpty(h.Cards.Shown);
    }

    [TestMethod]
    public void AtSignInAirPodsThatAreAlreadyInUseAreEvidenceTheBootBlockDidNotHold()
    {
        using var h = new CoordinatorHarness(startedAtLogon: true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));

        h.Start();

        Assert.IsEmpty(h.Block.Calls, "The AirPods are in active use, so the nodes stay enabled.");
        Assert.HasCount(1, h.Cards.Shown);
        Assert.AreEqual(BlockCoordinator.ConnectedAtStartUpMessage, h.Cards.Shown[0].Content.Status);
        Assert.AreEqual(CardAnchor.NearTray, h.Cards.Shown[0].Anchor);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connected at start-up"));

        // The idle rule takes it from there.
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void StartedByHandTheSameStateIsLoggedWithoutACard()
    {
        using var h = new CoordinatorHarness(startedAtLogon: false);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));

        h.Start();

        Assert.IsEmpty(h.Cards.Shown);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connected at start-up"));
    }

    [TestMethod]
    public void TheStartUpCheckWaitsUntilBothTheEndpointsAndTheNodesHaveBeenRead()
    {
        using var h = new CoordinatorHarness();
        h.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002));
        h.Monitor.Set(Devices.NotRead());

        h.Start();
        Assert.IsEmpty(h.Block.Calls);

        // The endpoints are read, but the nodes still are not.
        h.Publish(Devices.Idle(1));
        Assert.IsEmpty(h.Block.Calls);

        h.Block.StatusFailure = null;
        h.Publish(Devices.Idle(2));

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void TheStartUpCheckIsSkippedWhenTheUserActsFirst()
    {
        using var h = new CoordinatorHarness();

        // The nodes are enabled and not in use, which the start-up check would block, but the endpoints have not
        // been read yet, so it is still waiting when the user clicks.
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.NotRead());
        h.Start();
        Assert.IsEmpty(h.Block.Calls);

        h.Toggle(connect: true);

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Start-up check skipped"));
        Assert.IsEmpty(h.Block.Calls, "The start-up check ran after the user had acted.");
    }

    [TestMethod]
    public void TheSessionEndBlockIsIssuedWithoutWaitingForIt()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;

        h.Coordinator.OnSessionEnding(Query());

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The block was not issued as the session ended.");
        Assert.IsTrue(h.Coordinator.IsBusy);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: block issued at "));

        // A second message for the same session end does not start another.
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);

        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "session-end block (issued "));
        Assert.IsFalse(h.Coordinator.IsBusy);
    }

    [TestMethod]
    public void NoSessionEndBlockIsIssuedWithBlockAtBootOffOrTheNodesAlreadyBlocked()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Block at boot is off"));

        h.Block.Status = Statuses.Blocked();
        h.Publish(Devices.NotPresent(2));
        h.Coordinator.OnSessionEnding(Query());
        Assert.IsEmpty(h.Block.Calls);
    }

    [TestMethod]
    public void NoSessionEndBlockIsIssuedBeforeTheNodesHaveEverBeenRead()
    {
        using var h = new CoordinatorHarness();
        h.Block.StatusFailure = new IOException("no status");
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the boot block status was never read"));
    }

    [TestMethod]
    public void ASessionEndThatIsCancelledIsLoggedAndCanBlockAgainLater()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "The session end was cancelled"));
        Assert.HasCount(1, h.Block.Calls);

        // The nodes are enabled again (a driver re-enumeration, say) and the next session end blocks again.
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Active(3));
        h.Coordinator.OnSessionEnding(Query());
        Assert.HasCount(2, h.Block.Calls);
    }

    [TestMethod]
    public void ADeviceChangeWaitsForTheCleanUpOfTheConnectItCancelled()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetCanceled(token));
            return waiting.Task;
        });
        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();

        // The user picked another device: the connect is cancelled and the pin waits for its clean-up.
        cancel.Cancel();
        h.Pump();
        Task<ControllerResult> pin = h.Coordinator.ChangeDeviceAsync(Devices.Address);
        h.Pump();

        Assert.IsFalse(pin.IsCompleted, "The pin moved while the clean-up was still running.");
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);

        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(pin.IsCompleted);
        CollectionAssert.AreEqual(new[] { "allow", "block", "set-device:" + Devices.Address }, h.Block.Calls);
    }
}
