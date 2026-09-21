using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The required-transition table, one test method per cell. "An unreadable
// log is unknown, never accepted as a shut down" is pinned
// by FullShutDownNeverStartsOnUnknownAlone below: Unknown gets StartNoted, never plain Start, so a
// row can never read a clean green pass from it (StateDeriver.DeriveFromSecondHalf's own
// "shut down not confirmed" qualifier only fires when the verdict is not literally "power-down").
[TestClass]
public sealed class PowerCycleGateTests
{
    [TestMethod]
    public void FullShutDownStartsOnlyOnPowerDown()
    {
        Assert.AreEqual(PowerCycleGateResult.Start, PowerCycleGate.Evaluate(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.PowerDown));
    }

    [TestMethod]
    public void FullShutDownNotesARestartRatherThanRefusingOrStartingCleanly()
    {
        Assert.AreEqual(PowerCycleGateResult.StartNoted, PowerCycleGate.Evaluate(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Restart));
    }

    [TestMethod]
    public void FullShutDownRefusesNotYet()
    {
        Assert.AreEqual(PowerCycleGateResult.Refuse, PowerCycleGate.Evaluate(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.NotYet));
    }

    [TestMethod]
    public void FullShutDownNeverStartsOnUnknownAlone()
    {
        Assert.AreEqual(PowerCycleGateResult.StartNoted, PowerCycleGate.Evaluate(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Unknown));
    }

    [TestMethod]
    public void AnyStartAcceptsPowerDownOrRestartCleanly()
    {
        Assert.AreEqual(PowerCycleGateResult.Start, PowerCycleGate.Evaluate(PowerCycleRequirement.AnyStart, PowerCycleVerdict.PowerDown));
        Assert.AreEqual(PowerCycleGateResult.Start, PowerCycleGate.Evaluate(PowerCycleRequirement.AnyStart, PowerCycleVerdict.Restart));
    }

    [TestMethod]
    public void AnyStartRefusesNotYetButStartsNotedOnUnknown()
    {
        Assert.AreEqual(PowerCycleGateResult.Refuse, PowerCycleGate.Evaluate(PowerCycleRequirement.AnyStart, PowerCycleVerdict.NotYet));
        Assert.AreEqual(PowerCycleGateResult.StartNoted, PowerCycleGate.Evaluate(PowerCycleRequirement.AnyStart, PowerCycleVerdict.Unknown));
    }

    [TestMethod]
    public void RestartWarnsButStartsOnAPowerDownAndStartsCleanlyOnARestart()
    {
        Assert.AreEqual(PowerCycleGateResult.StartNoted, PowerCycleGate.Evaluate(PowerCycleRequirement.Restart, PowerCycleVerdict.PowerDown));
        Assert.AreEqual(PowerCycleGateResult.Start, PowerCycleGate.Evaluate(PowerCycleRequirement.Restart, PowerCycleVerdict.Restart));
    }

    [TestMethod]
    public void RestartRefusesNotYetAndStartsNotedOnUnknown()
    {
        Assert.AreEqual(PowerCycleGateResult.Refuse, PowerCycleGate.Evaluate(PowerCycleRequirement.Restart, PowerCycleVerdict.NotYet));
        Assert.AreEqual(PowerCycleGateResult.StartNoted, PowerCycleGate.Evaluate(PowerCycleRequirement.Restart, PowerCycleVerdict.Unknown));
    }

    [TestMethod]
    public void NoneStartsOnEveryVerdict()
    {
        foreach (PowerCycleVerdict verdict in Enum.GetValues<PowerCycleVerdict>())
        {
            Assert.AreEqual(PowerCycleGateResult.Start, PowerCycleGate.Evaluate(PowerCycleRequirement.None, verdict));
        }
    }

    [TestMethod]
    public void RefusalMessageForNotYetAsksForTheTransitionThisRowNeeds()
    {
        string message = PowerCycleGate.RefusalMessage(PowerCycleRequirement.AnyStart, PowerCycleVerdict.NotYet);
        StringAssert.Contains(message, "has not recorded a start");
    }

    [TestMethod]
    public void NotedWarningForAFullShutDownAfterARestartNamesTheMistake()
    {
        string message = PowerCycleGate.NotedWarning(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Restart);
        StringAssert.Contains(message, "restart, not a shut down");
        StringAssert.Contains(message, "carry on only if you want to try it anyway");
    }

    [TestMethod]
    public void NotedWarningForAnUnreadableLogSaysSo()
    {
        string message = PowerCycleGate.NotedWarning(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Unknown);
        StringAssert.Contains(message, "could not be read");
    }
}
