using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// B2 (review round 1): this is the caller RunAllHaltTests.cs never actually exercised.
// AdvanceRunAll's own "freshEnough" check used to treat StoppedBeforeAnyStep the same as NotRun,
// skipping RunAllHalt.ShouldHalt (which already, correctly, says halt for it) and restarting a
// declined test forever. Every case here is named for what AdvanceRunAll must now do.
[TestClass]
public sealed class RunAllAdvanceTests
{
    [TestMethod]
    public void NeverAttemptedStartsFresh()
    {
        var state = new DerivedRowState { Kind = RowStateKind.NotRun };
        Assert.AreEqual(RunAllAdvanceDecision.StartFresh, RunAllAdvance.Decide(state));
    }

    [TestMethod]
    public void ADeclinedStartStoppedBeforeAnyStepHaltsRatherThanRestartingForever()
    {
        var state = new DerivedRowState { Kind = RowStateKind.StoppedBeforeAnyStep };
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(state));
    }

    [TestMethod]
    public void ACleanPassAdvances()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "yes" };
        Assert.AreEqual(RunAllAdvanceDecision.Advance, RunAllAdvance.Decide(state));
    }

    [TestMethod]
    public void APassWithABadLeftAtRestHaltsRatherThanAdvancing()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "no" };
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(state));
    }

    [TestMethod]
    public void WaitingForShutDownHaltsAtThePowerCycleBoundary()
    {
        var state = new DerivedRowState { Kind = RowStateKind.WaitingForShutDown };
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(state));
    }

    [TestMethod]
    public void WaitingForRestartHaltsAtThePowerCycleBoundary()
    {
        var state = new DerivedRowState { Kind = RowStateKind.WaitingForRestart };
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(state));
    }

    [TestMethod]
    public void FailedInconclusiveAndUnknownAllHalt()
    {
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(new DerivedRowState { Kind = RowStateKind.Failed }));
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(new DerivedRowState { Kind = RowStateKind.Inconclusive }));
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(new DerivedRowState { Kind = RowStateKind.Unknown }));
    }

    [TestMethod]
    public void LockedHalts()
    {
        Assert.AreEqual(RunAllAdvanceDecision.Halt, RunAllAdvance.Decide(new DerivedRowState { Kind = RowStateKind.Locked }));
    }
}
