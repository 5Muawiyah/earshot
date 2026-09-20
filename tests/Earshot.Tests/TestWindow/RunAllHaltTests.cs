using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T14 (test-gui.md section 11/12/13): Run all halts on each non-pass, on leftAtRest no and
// unknown, and at each power-cycle boundary.
[TestClass]
public sealed class RunAllHaltTests
{
    [TestMethod]
    public void WaitingForShutDownAlwaysHalts()
    {
        var state = new DerivedRowState { Kind = RowStateKind.WaitingForShutDown };
        Assert.IsTrue(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void WaitingForRestartAlwaysHalts()
    {
        var state = new DerivedRowState { Kind = RowStateKind.WaitingForRestart };
        Assert.IsTrue(RunAllHalt.ShouldHalt(state));
    }

    // DataRow cannot carry RowStateKind itself (internal, and a [TestMethod] must be public), so
    // each case is named by the enum member's own name and looked up with Enum.Parse.
    [TestMethod]
    [DataRow("Failed")]
    [DataRow("Inconclusive")]
    [DataRow("Unknown")]
    [DataRow("NotRun")]
    [DataRow("StoppedBeforeAnyStep")]
    public void EveryNonPassedKindHaltsWhateverLeftAtRestSays(string kindName)
    {
        var kind = Enum.Parse<RowStateKind>(kindName);
        var state = new DerivedRowState { Kind = kind, LeftAtRest = "yes" };
        Assert.IsTrue(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void APassWithLeftAtRestYesDoesNotHalt()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "yes" };
        Assert.IsFalse(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void APassWithLeftAtRestNotApplicableDoesNotHalt()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "not-applicable" };
        Assert.IsFalse(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void APassWithLeftAtRestNoOnPurposeDoesNotHalt()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "no-on-purpose" };
        Assert.IsFalse(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void APassWithLeftAtRestNoHalts()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "no" };
        Assert.IsTrue(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void APassWithLeftAtRestUnknownHalts()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = "unknown" };
        Assert.IsTrue(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void APassWithNoLeftAtRestFindingAtAllHaltsTheSameAsUnknown()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, LeftAtRest = null };
        Assert.IsTrue(RunAllHalt.ShouldHalt(state));
    }

    [TestMethod]
    public void AQualifiedPassOnAnEarlierBuildStillCountsAsAPassForHaltingPurposes()
    {
        // RunAllHalt only reads Kind and LeftAtRest: the amber qualifier itself is a row-list
        // concern (RowPresenter), not a reason for Run all to stop that this rule owns.
        var state = new DerivedRowState { Kind = RowStateKind.Passed, Qualifier = "on an earlier build", LeftAtRest = "yes" };
        Assert.IsFalse(RunAllHalt.ShouldHalt(state));
    }
}
