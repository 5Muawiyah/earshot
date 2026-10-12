using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// A connect from every state the nodes can be in, inside its 20 second limit, with one retry for a temporary failure, a plain
// reason when it fails, and exactly the allow it made put back. The clock is the test's: every wait below is Task.Delay on it.
[TestClass]
public sealed class ConnectEveryStateTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly string[] AllowOnly = ["allow"];
    private static readonly string[] ProgressAfterConnecting = ["Allowing", "Waiting for the AirPods", "Connecting"];

    private static readonly int Denied = unchecked((int)0x80070005);
    private static readonly int DeviceNotConnected = unchecked((int)0x8007048F);

    private static ConnectResult Sent(ConnectOutcome outcome, string message, params int[] codes) =>
        new(outcome, message, codes.Select((c, i) => StepOutcomes.FromHResult(KsConnectPath.ReconnectStep + ":src" + i, c, "a2dp: adapter", ok: c == 0)).ToList());

    private static ConnectResult Temporary() => Sent(ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver, DeviceNotConnected);

    private static ConnectResult AccessDenied() => Sent(ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver, Denied);

    // Pumps and moves the clock in quarter seconds until the task ends or the limit passes; the elapsed time is the clock's.
    private static ToggleReport Await(CoordinatorHarness h, Task<ToggleReport> task, out TimeSpan elapsed, TimeSpan? within = null)
    {
        DateTimeOffset start = h.Time.GetUtcNow();
        TimeSpan limit = within ?? TimeSpan.FromSeconds(40);
        h.Pump();
        while (!task.IsCompleted && h.Time.GetUtcNow() - start < limit)
        {
            h.Advance(TimeSpan.FromMilliseconds(250));
        }

        elapsed = h.Time.GetUtcNow() - start;
        Assert.IsTrue(task.IsCompleted, "The connect did not finish within " + limit.TotalSeconds + " s of the clock.");
        return task.GetAwaiter().GetResult();
    }

    private static CoordinatorHarness NodesBlockedAndAway()
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        return h;
    }

    private static void EndpointsComeBackAfter(CoordinatorHarness h, TimeSpan delay)
    {
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = h.Block.Status with { State = BlockState.Allowed };
            h.Time.CreateTimer(_ => h.Monitor.Publish(Devices.Idle(500)), null, delay, Timeout.InfiniteTimeSpan);
            return Task.FromResult(ControllerResult.Ok("Allowed") with { GateNonce = h.Block.LastAllowNonce });
        };
    }

    // ---- the start states ----

    [TestMethod]
    public void NodesPresentAndEnabledConnectAtOnceByTheDirectPath()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsLessThan(Limit, elapsed);
        Assert.HasCount(1, h.Connection.Calls, "The reconnect request is sent.");
        Assert.IsEmpty(h.Block.GateVerbCalls);
        Assert.AreEqual(FailureClass.None, report.FailureClass);
    }

    [TestMethod]
    public void NodesPresentAndDisabledAreAllowedThenConnected()
    {
        using var h = NodesBlockedAndAway();

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsLessThan(Limit, elapsed);
        Assert.HasCount(2, h.Connection.Calls, "One pass found the nodes blocked; the request is sent after the allow.");
        CollectionAssert.AreEqual(AllowOnly, h.Block.GateVerbCalls);
    }

    [TestMethod]
    public void NodesAwayWithTheFlagAreAllowedWaitedForAndConnectedInsideTheLimit()
    {
        using var h = NodesBlockedAndAway();
        EndpointsComeBackAfter(h, TimeSpan.FromSeconds(4));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Success, report.Status, report.UserMessage);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(4), elapsed);
        Assert.IsLessThan(Limit, elapsed);
        Assert.HasCount(2, h.Connection.Calls);
        CollectionAssert.AreEqual(ProgressAfterConnecting, report.Progress.Skip(1).Take(3).ToArray());
    }

    [TestMethod]
    public void AReplacementPairWithNoPairedDeviceIsToldItNeedsPairingAndNothingIsAllowed()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(new ConnectResult(ConnectOutcome.NotFound, ConnectMessages.NotFound, [])));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual("These AirPods need pairing again.", report.UserMessage);
        Assert.AreEqual(FailureClass.PcSide, report.FailureClass);
        Assert.IsTrue(report.OfferConnectInWindows);
        Assert.IsEmpty(h.Block.GateVerbCalls, "A new pairing is adopted only through Fix connection, never by Connect.");
    }

    // ---- the retry ----

    [TestMethod]
    public void ATemporaryCodeThenSuccessRetriesExactlyOnce()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Temporary()));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.HasCount(2, h.Connection.Calls);
        Assert.IsTrue(report.Retried);
        Assert.Contains("Trying again", report.Progress);
        Assert.Contains("Trying again", h.Cards.Statuses);
    }

    [TestMethod]
    public void ATemporaryCodeTwiceFailsAfterOneRetryAsThisPcsSide()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Temporary()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Temporary()));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.HasCount(2, h.Connection.Calls, "Exactly one retry.");
        Assert.AreEqual(FailureClass.PcSide, report.FailureClass);
        Assert.IsLessThan(Limit, elapsed);
        Assert.IsTrue(report.Retried);
    }

    [TestMethod]
    public void ACodeOnThisPcsSideNeverRetries()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.HasCount(1, h.Connection.Calls);
        Assert.AreEqual("Windows refused the change.", report.UserMessage);
        Assert.AreEqual(FailureClass.PcSide, report.FailureClass);
        Assert.IsFalse(report.Retried);
    }

    [TestMethod]
    public void NoRetryIsStartedWithLessThanSixSecondsLeft()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), h.Time, ct);
            return Temporary();
        });

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.HasCount(1, h.Connection.Calls, "Five seconds are left, which is less than a retry needs.");
        Assert.IsFalse(report.Retried);
        Assert.IsLessThanOrEqualTo(Limit + TimeSpan.FromSeconds(1), elapsed);
    }

    [TestMethod]
    public void AConnectThatNeverAnswersEndsAtTheLimitNotLater()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Hints.Hint = DeviceHint.HeardNearby;
        h.Connection.Connects.Enqueue(async ct =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), h.Time, ct);
            return Results.Connected();
        });

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsGreaterThanOrEqualTo(Limit, elapsed);
        Assert.IsLessThanOrEqualTo(Limit + TimeSpan.FromSeconds(1), elapsed);
        Assert.IsFalse(report.Cancelled, "The limit is a failure, not a cancel by the caller.");
    }

    [TestMethod]
    public void EndpointsThatNeverComeBackAreWaitedForTwiceInsideTheLimitThenWindowsDidNotBringThemBack()
    {
        using var h = NodesBlockedAndAway();
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = h.Block.Status with { State = BlockState.Allowed };
            return Task.FromResult(ControllerResult.Ok("Allowed") with { GateNonce = h.Block.LastAllowNonce });
        };

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual("Windows did not bring the AirPods back.", report.UserMessage);
        Assert.AreEqual(FailureClass.PcSide, report.FailureClass);
        Assert.IsTrue(report.Retried);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(19), elapsed);
        Assert.IsLessThanOrEqualTo(Limit + TimeSpan.FromSeconds(1), elapsed);
        Assert.HasCount(1, h.Connection.Calls, "No request is sent to an endpoint that never came.");
    }

    [TestMethod]
    public void AnEndpointWaitThatWasRetriedLeavesNoSecondRetryOfTheRequest()
    {
        using var h = NodesBlockedAndAway();
        EndpointsComeBackAfter(h, BlockCoordinator.EndpointWait + TimeSpan.FromSeconds(1));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Temporary()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Temporary()));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsTrue(report.Retried, "The wait for the endpoint was the one retry.");
        Assert.HasCount(2, h.Connection.Calls, "The pass that found the nodes blocked, and one request after the retried wait; no third.");
        Assert.IsLessThanOrEqualTo(Limit + TimeSpan.FromSeconds(1), elapsed);
    }

    [TestMethod]
    public void AnAllowWhoseGateRunIsSlowIsCutByWhatIsLeftOfTheBudget()
    {
        using var h = NodesBlockedAndAway();
        bool cut = false;
        h.Block.OnAllow = async ct =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), h.Time, ct);
            }
            catch (OperationCanceledException)
            {
                cut = true;
                return ControllerResult.Fail("Stopped waiting for the gate", []);
            }

            return ControllerResult.Ok("Allowed");
        };

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out TimeSpan elapsed, within: TimeSpan.FromMinutes(2));

        Assert.IsTrue(cut, "The gate run was given a token that the budget ends.");
        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsFalse(report.Cancelled, "The budget is a failure, not a cancel by the caller.");
        Assert.IsLessThanOrEqualTo(Limit + TimeSpan.FromSeconds(1), elapsed);
    }

    [TestMethod]
    public void AStatusReadThatStallsIsCutByTheBudgetNotByItsOwnLongerOne()
    {
        using var h = NodesBlockedAndAway();
        h.Block.StatusStalls = true;
        int before = h.Block.StatusReads;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true));
        h.Pump();
        Assert.AreEqual(before + 1, h.Block.StatusReads, "The connect's own read of the nodes is waiting.");
        Assert.IsFalse(toggle.IsCompleted);

        h.Advance(Limit + TimeSpan.FromSeconds(1));

        Assert.IsLessThan(BlockCoordinator.StatusReadBudget, Limit);
        Assert.IsTrue(toggle.IsCompleted, "The budget ended the wait for the read; the read's own budget is longer.");
        Assert.AreEqual(OpStatus.Failed, toggle.GetAwaiter().GetResult().Status);
    }

    [TestMethod]
    public void TheBudgetStartsWhenTheConnectIsAskedForSoTimeSpentQueuedCounts()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Hints.Hint = DeviceHint.LidClosed;
        h.Connection.Connects.Enqueue(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12), h.Time, ct);
            return Results.TimedOut();
        });
        h.Connection.Connects.Enqueue(async ct =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), h.Time, ct);
            return Results.Connected();
        });

        Task<ToggleReport> first = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true));
        Task<ToggleReport> second = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true));
        ToggleReport report = Await(h, second, out TimeSpan elapsed, within: TimeSpan.FromMinutes(2));

        Assert.IsTrue(first.IsCompleted);
        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsLessThanOrEqualTo(Limit + TimeSpan.FromSeconds(1), elapsed, "Twelve seconds of the twenty went to the connect ahead of it.");
    }

    [TestMethod]
    public void WhenTheAirPodsAreNotHeardTheFailureIsTheDevicesAndNeverOffersFix()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Hints.Hint = DeviceHint.NotHeard;

        for (int i = 0; i < 3; i++)
        {
            h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));
            ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

            Assert.AreEqual(FailureClass.DeviceSide, report.FailureClass);
            Assert.AreEqual("Not heard nearby.", report.UserMessage);
            Assert.IsFalse(report.FixOfferDue);
        }
    }

    // ---- the Fix offer ----

    [TestMethod]
    public void ThreeTimeoutsWithNoLinkedPairReachTheOfferAtTheThirdAndNotBefore()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Hints.Hint = DeviceHint.Unknown;

        var reports = new List<ToggleReport>();
        for (int i = 0; i < 3; i++)
        {
            h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));
            reports.Add(Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _));
        }

        Assert.IsTrue(reports.All(r => r.FailureClass == FailureClass.Unknown));
        Assert.AreEqual("Open the case near this PC, or try Fix connection.", reports[0].UserMessage);
        Assert.IsFalse(reports[0].FixOfferDue);
        Assert.IsFalse(reports[1].FixOfferDue);
        Assert.IsTrue(reports[2].FixOfferDue);
    }

    private static CoordinatorHarness AllowedWithNoBlockAtBoot(FixOfferTracker? tracker)
    {
        var h = new CoordinatorHarness(fixOffer: tracker);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        return h;
    }

    private static bool FailsOnThisPc(CoordinatorHarness h)
    {
        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));
        return Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _).FixOfferDue;
    }

    [TestMethod]
    public void TheFixOfferCountSurvivesARestartFromTheConnectHistory()
    {
        using CoordinatorHarness before = AllowedWithNoBlockAtBoot(null);
        Assert.IsFalse(FailsOnThisPc(before), "The first failure on this PC is not yet an offer.");

        // A new process reads the history the first one wrote.
        using CoordinatorHarness after = AllowedWithNoBlockAtBoot(FixOfferTracker.Replay(before.History.Read()));

        Assert.IsTrue(FailsOnThisPc(after), "The second in a row reaches the offer across the restart.");
    }

    [TestMethod]
    public void AnOfferAlreadyRaisedBeforeARestartStartsTheCountAgain()
    {
        using CoordinatorHarness before = AllowedWithNoBlockAtBoot(null);
        Assert.IsFalse(FailsOnThisPc(before));
        Assert.IsTrue(FailsOnThisPc(before));

        using CoordinatorHarness after = AllowedWithNoBlockAtBoot(FixOfferTracker.Replay(before.History.Read()));

        Assert.IsFalse(FailsOnThisPc(after), "The offer was made once; the count starts again.");
        Assert.IsTrue(FailsOnThisPc(after));
    }

    [TestMethod]
    public void ASuccessBeforeARestartResetsTheCountAcrossIt()
    {
        using CoordinatorHarness before = AllowedWithNoBlockAtBoot(null);
        Assert.IsFalse(FailsOnThisPc(before));
        before.Connection.Connects.Enqueue(_ => Task.FromResult(Results.Connected()));
        Await(before, before.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);
        Assert.IsTrue(before.History.Read().Any(e => e.Outcome == ConnectHistoryOutcomes.Connected), "The success is in the history.");

        using CoordinatorHarness after = AllowedWithNoBlockAtBoot(FixOfferTracker.Replay(before.History.Read()));

        Assert.IsFalse(FailsOnThisPc(after), "The success ended the earlier failure's count.");
    }

    [TestMethod]
    public void PcSideDeviceSidePcSideOffersFixOnTheThirdReport()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));
        Assert.IsFalse(Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _).FixOfferDue);
        h.Hints.Hint = DeviceHint.LidClosed;
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));
        Assert.IsFalse(Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _).FixOfferDue);
        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));

        ToggleReport third = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        Assert.IsTrue(third.FixOfferDue);
    }

    [TestMethod]
    public void PcSideSuccessPcSideOffersNothing()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));
        Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);
        Assert.AreEqual(OpStatus.Success, Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _).Status);
        h.Monitor.Publish(Devices.Idle(900));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));

        Assert.IsFalse(Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _).FixOfferDue);
    }

    // ---- what is put back ----

    [TestMethod]
    public void AFailedConnectPutsBackExactlyItsAllowByTheAllowsNonce()
    {
        using var h = NodesBlockedAndAway();
        EndpointsComeBackAfter(h, TimeSpan.FromSeconds(1));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.HasCount(2, h.Block.GateVerbCalls);
        Assert.AreEqual("allow", h.Block.GateVerbCalls[0]);
        Assert.AreEqual("undo-allow:" + h.Block.LastAllowNonce, h.Block.GateVerbCalls[1]);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void TheCleanUpAfterACancelledConnectIsNotCancellable()
    {
        using var h = NodesBlockedAndAway();
        EndpointsComeBackAfter(h, TimeSpan.FromSeconds(1));
        using var cancel = new CancellationTokenSource();
        h.Connection.Connects.Enqueue(async ct =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Results.Connected();
        });

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true), cancel.Token), out _);

        Assert.IsTrue(report.Cancelled);
        Assert.HasCount(1, h.Block.UndoTokens);
        Assert.IsFalse(h.Block.UndoTokens[0].CanBeCanceled, "Clean-up after a cancel runs with CancellationToken.None.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    // ---- the history ----

    [TestMethod]
    public void EveryConnectIsWrittenToTheHistoryWithoutAnIdentifier()
    {
        using var h = NodesBlockedAndAway();
        EndpointsComeBackAfter(h, TimeSpan.FromSeconds(1));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        ConnectHistoryEntry entry = h.History.Read().Single();
        Assert.AreEqual(ConnectHistoryActions.Connect, entry.Action);
        Assert.AreEqual(ConnectHistoryOutcomes.Failed, entry.Outcome);
        Assert.AreEqual("allow-first", entry.Path);
        Assert.IsGreaterThan(0, entry.Steps.Count);
        string text = File.ReadAllText(h.History.FilePath);
        Assert.DoesNotContain(Devices.Address, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Devices.Container.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jonathan", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ASuccessfulConnectAndADisconnectAreRecordedToo()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);
        Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(false)), out _);

        IReadOnlyList<ConnectHistoryEntry> read = h.History.Read();
        Assert.HasCount(2, read);
        Assert.AreEqual(ConnectHistoryOutcomes.Connected, read[0].Outcome);
        Assert.AreEqual(ConnectHistoryActions.Disconnect, read[1].Action);
    }

    [TestMethod]
    public void TheFailurePlainReasonIsWhatTheCardShowsLast()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(AccessDenied()));

        ToggleReport report = Await(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(true)), out _);

        Assert.AreEqual("Windows refused the change.", h.Cards.Statuses[^1]);
        Assert.AreEqual(report.UserMessage, h.Cards.Statuses[^1]);
        Assert.IsTrue(report.OfferConnectInWindows);
    }
}
