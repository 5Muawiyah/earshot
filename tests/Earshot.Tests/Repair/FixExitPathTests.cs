using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// Every way a Fix run can end, with Earshot installed or not. With Earshot installed and Block at boot on, a run that changed
// anything ends with the normal block, and the block is the last request the gate gets.
[TestClass]
public sealed class FixExitPathTests
{
    private static FixRig Arranged(bool happy = true)
    {
        var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        FixRePairTests.ArrangeHappyPairing(rig, newDeviceIsAirPods: happy);

        // The elevated side runs in this process, over the same fake table.
        rig.Elevator.Result = (path, sha) =>
        {
            FixApplyResult applied = FixApply.Run(new FixApplyRequest(path, sha), new FixApplyDeps
            {
                Nodes = rig.Gate.Nodes,
                Store = rig.Gate.Store,
                Folders = rig.Gate.Folders,
                RunLock = NoGateRunLock.Instance,
                Log = rig.Gate.Log,
                LeftoversReader = rig.Leftovers,
                Wait = _ => true,
                LockWait = TimeSpan.FromSeconds(1),
            });
            return new ElevatedRun((int)applied.Outcome, StepOutcomes.FromWin32("runas", 0));
        };
        return rig;
    }

    private static void SayBothOk(FixRig rig)
    {
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();
    }

    private static void Uninstall(FixRig rig)
    {
        File.Delete(rig.Gate.Store.DeviceFile);
        File.Delete(rig.Gate.Store.ConfigFile);
    }

    private static async Task<FixResult> RunAsync(FixRig rig, CancellationToken ct = default) =>
        await FixConnection.RunAsync([], rig.Host(), ct);

    [TestMethod]
    public async Task ASuccessfulRunAdoptsTheNewPairInOneGateRunWhichBlocksIt()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Done, result.Outcome, string.Join("; ", result.Lines));
        Expect.Same(rig.GateFake.Calls, "adopt:" + FixRig.NewAirPodsAddress);
        Assert.AreEqual(FixRecoveryState.None, rig.Recovery.Read().State);
        // The nodes of the old pair were enabled before the second question.
        Assert.IsNotEmpty(rig.Gate.Nodes.Calls.Where(c => c.Kind is "enable" or "clear-flag").ToList());
        Assert.AreEqual(1, rig.Elevator.Calls.Count);
    }

    [TestMethod]
    public async Task ADeclinedSecondOkEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();

        FixResult result = await RunAsync(rig);

        // The first OK's changes were made, so this is not "nothing was changed": it has its own outcome and exit code, and says
        // what was done and what was not.
        Assert.AreEqual(FixOutcome.DeclinedAfterChanges, result.Outcome);
        Assert.AreEqual(Earshot.ExitCodes.DeclinedAfterChanges, result.ExitCode, "A no on the screen is not reported as a fix that worked.");
        Assert.IsTrue(result.Lines.Any(l => l.Contains("were not removed or paired again", StringComparison.Ordinal)), string.Join("; ", result.Lines));
        Assert.IsFalse(result.Lines.Any(l => l.Contains("Nothing was changed", StringComparison.Ordinal)), string.Join("; ", result.Lines));
        Expect.Same(rig.GateFake.Calls, "block");
        Assert.IsEmpty(rig.Pairing.Calls, "Nothing was unpaired or scanned.");
        Assert.AreEqual(FixRecoveryState.None, rig.Recovery.Read().State);
    }

    [TestMethod]
    public async Task ASecondOkThatTimesOutEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Prompter.SayTimedOut();

        await RunAsync(rig);

        Expect.Same(rig.GateFake.Calls, "block");
        Assert.IsEmpty(rig.Pairing.Calls);
    }

    [TestMethod]
    public async Task ADeclinedFirstOkChangesNothingAndSendsNoBlock()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayCancel();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Declined, result.Outcome);
        Assert.AreEqual(Earshot.ExitCodes.Declined, result.ExitCode);
        Assert.IsEmpty(rig.GateFake.Calls);
        Assert.IsEmpty(rig.Elevator.Calls);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
        Assert.AreEqual(FixRecoveryState.None, rig.Recovery.Read().State);
    }

    // A plan file, a marker or a desktop handle that could not be let go is not an error the person must act on, but it is
    // recorded with its raw code rather than dropped.
    [TestMethod]
    public async Task APlanFileThatCouldNotBeRemovedIsAStepAndALogLine()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();
        Func<string, string, ElevatedRun> applied = rig.Elevator.Result;
        FileStream? held = null;
        rig.Elevator.Result = (path, sha) =>
        {
            ElevatedRun run = applied(path, sha);
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return run;
        };

        try
        {
            FixResult result = await RunAsync(rig);

            StepOutcome deleted = result.Steps.Single(s => s.Step == "fix-plan-delete");
            Assert.IsFalse(deleted.Ok);
            Assert.AreNotEqual(0, deleted.Code);
            Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "fix-plan-delete"), string.Join("; ", rig.Log.Entries.Select(e => e.Message)));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [TestMethod]
    public async Task AMarkerThatCouldNotBeClearedIsSaidAndLogged()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();
        Func<string, string, ElevatedRun> applied = rig.Elevator.Result;
        FileStream? held = null;
        rig.Elevator.Result = (path, sha) =>
        {
            ElevatedRun run = applied(path, sha);
            held = new FileStream(rig.RecoveryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return run;
        };

        try
        {
            FixResult result = await RunAsync(rig);

            Assert.IsFalse(result.Steps.Single(s => s.Step == "fix-marker-clear").Ok);
            Assert.IsTrue(result.Lines.Any(l => l.Contains("could not be removed", StringComparison.Ordinal)), string.Join("; ", result.Lines));
            Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "fix-marker-clear"));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [TestMethod]
    public async Task ADesktopHandleThatDidNotCloseIsLogged()
    {
        using FixRig rig = Arranged();
        rig.Desktop = new FakeDesktop { Reading = FakeDesktop.Interactive().Reading with { CloseError = 6 } };
        rig.Prompter.SayCancel();

        await RunAsync(rig);

        Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "CloseDesktop failed with Win32 error 6"), string.Join("; ", rig.Log.Entries.Select(e => e.Message)));
    }

    // The administrator run may have started and changed the nodes before the wait for it ends in an exception, so the run
    // counts as having touched them and the closing block follows.
    [TestMethod]
    public async Task AnAdministratorRunWhoseWaitEndsInAnExceptionEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Elevator.Result = (_, _) => throw new OperationCanceledException();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Expect.Same(rig.GateFake.Calls, "block");
    }

    [TestMethod]
    public async Task ADeclinedAdministratorPromptChangesNothingAndSendsNoBlock()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Elevator.Result = (_, _) => new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223));

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Assert.IsEmpty(rig.GateFake.Calls);
        Assert.IsEmpty(rig.Pairing.Calls);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("1223", StringComparison.Ordinal) || l.Contains("ERROR_CANCELLED", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnEmptyPlanSkipsElevationAndTheFirstQuestion()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.IsEmpty(rig.Elevator.Calls);
        Assert.HasCount(1, rig.Prompter.Asked, "Only the re-pair question was asked.");
        Assert.IsTrue(rig.Prompter.Asked[0].Heading.Contains("pair", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(FixOutcome.Done, result.Outcome, string.Join("; ", result.Lines));
    }

    [TestMethod]
    public async Task AFailedUnpairEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        rig.Pairing.UnpairResult = new PairingCallResult("Failed", 2, false, null);

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Assert.AreEqual("block", rig.GateFake.Calls[^1]);
        Assert.DoesNotContain("adopt:" + FixRig.NewAirPodsAddress, rig.GateFake.Calls);
    }

    [TestMethod]
    public async Task NoDeviceAppearingEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        rig.Pairing.Events.RemoveAll(e => e.Device?.Id == FixRePairTests.NewId);

        await RunAsync(rig);

        Assert.AreEqual("block", rig.GateFake.Calls[^1]);
        Assert.AreEqual(1, rig.GateFake.Calls.Count);
    }

    [TestMethod]
    public async Task APickTheOwnerCancelsEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        rig.Ui.Pick = -1;

        await RunAsync(rig);

        Assert.AreEqual("block", rig.GateFake.Calls[^1]);
    }

    [TestMethod]
    public async Task AFailedPairingEndsInTheBlock()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        rig.Pairing.PairResult = new PairingCallResult("AuthenticationFailure", 5, false, null);

        FixResult result = await RunAsync(rig);

        Assert.AreEqual("block", rig.GateFake.Calls[^1]);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("AuthenticationFailure", StringComparison.Ordinal) && l.Contains('5', StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnExceptionInThePairingApiEndsInTheBlockAndIsReported()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        rig.Pairing.ThrowOnPair = new InvalidOperationException("the pairing API failed");

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Assert.AreEqual("block", rig.GateFake.Calls[^1]);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("InvalidOperationException", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task APostPairIdentityFailureAdoptsNothingAndEndsInTheBlock()
    {
        using FixRig rig = Arranged(happy: false);
        SayBothOk(rig);

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("adopt:" + FixRig.NewAirPodsAddress, rig.GateFake.Calls);
        Assert.AreEqual("block", rig.GateFake.Calls[^1]);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("stays paired", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task AFailedAdoptionIsFollowedByTheBlock()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        rig.GateFake.AdoptResult = _ => new FixGateResult(false, "Could not adopt", [], 8);

        FixResult result = await RunAsync(rig);

        Expect.Same(rig.GateFake.Calls, "adopt:" + FixRig.NewAirPodsAddress, "block");
        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        AssertTheNewPairIsReportedUnprotected(result);
    }

    // The closing block after a failed adoption goes to the pin Earshot still holds, which is the pair that was just removed,
    // so it cannot protect the new pair. The result must say the new pair is not protected and must never claim the AirPods
    // were blocked.
    private static void AssertTheNewPairIsReportedUnprotected(FixResult result)
    {
        string all = string.Join(" | ", result.Lines);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("new pair is not protected", StringComparison.OrdinalIgnoreCase)), all);
        Assert.IsFalse(result.Lines.Any(l => l.Contains("blocked the AirPods again", StringComparison.OrdinalIgnoreCase)
            || l.Contains("blocking the AirPods again", StringComparison.OrdinalIgnoreCase)), all);
    }

    [TestMethod]
    public async Task ACancelledAdoptionIsFollowedByTheBlockThatIsNotCancelled()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        using var cts = new CancellationTokenSource();
        rig.GateFake.OnCall = call =>
        {
            if (call == "adopt")
            {
                cts.Cancel();
            }
        };

        FixResult result = await RunAsync(rig, cts.Token);

        Expect.Same(rig.GateFake.Calls, "adopt:" + FixRig.NewAirPodsAddress, "block");
        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        AssertTheNewPairIsReportedUnprotected(result);
    }

    [TestMethod]
    public async Task WithEarshotNotInstalledNothingIsAdoptedAndNoBlockIsSent()
    {
        using FixRig rig = Arranged();
        Uninstall(rig);
        SayBothOk(rig);

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Done, result.Outcome, string.Join("; ", result.Lines));
        Assert.IsEmpty(rig.GateFake.Calls);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("not installed", StringComparison.OrdinalIgnoreCase) && l.Contains("adopt", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task WithEarshotNotInstalledADeclinedSecondOkSendsNoBlock()
    {
        using FixRig rig = Arranged();
        Uninstall(rig);

        // With no record of Earshot there is nothing to undo, so the only question is the second one.
        rig.Prompter.SayCancel();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Declined, result.Outcome);
        Assert.HasCount(1, rig.Prompter.Asked);
        Assert.IsEmpty(rig.Pairing.Calls);
        Assert.IsEmpty(rig.GateFake.Calls);
    }

    [TestMethod]
    public async Task WithBlockAtBootOffNoBlockIsSent()
    {
        using FixRig rig = Arranged();
        Assert.IsTrue(rig.Gate.Store.WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();

        await RunAsync(rig);

        Assert.IsEmpty(rig.GateFake.Calls);
    }

    [TestMethod]
    public async Task ABlockThatFailsLeavesTheMarkerForTheNextStart()
    {
        using FixRig rig = Arranged();
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();
        rig.GateFake.BlockResult = () => new FixGateResult(false, "Could not block", [], 3);

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Assert.AreNotEqual(FixRecoveryState.None, rig.Recovery.Read().State);
    }

    [TestMethod]
    public async Task TheMarkerIsWrittenBeforeTheFirstChangeNotAfter()
    {
        using FixRig rig = Arranged();
        SayBothOk(rig);
        FixRecoveryState? atElevation = null;
        FakeElevator elevator = rig.Elevator;
        Func<string, string, ElevatedRun> inner = elevator.Result;
        elevator.Result = (p, s) =>
        {
            atElevation = rig.Recovery.Read().State;
            return inner(p, s);
        };

        await RunAsync(rig);

        Assert.AreEqual(FixRecoveryState.WindowOpen, atElevation);
    }
}
