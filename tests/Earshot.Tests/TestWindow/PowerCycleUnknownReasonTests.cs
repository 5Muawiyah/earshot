using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// PowerCycleGate.NotedWarning used to say, unconditionally, that "the event log could not be
// read" for every Unknown verdict. That is only one of the two genuinely different reasons
// Unknown can mean: the log itself could not be read, or it read fine but named no shut down or
// restart record between the first half and the start. PowerCycle.ReasonForUnknown tells the two
// apart from the same evidence Decide already reads.
[TestClass]
public sealed class PowerCycleUnknownReasonTests
{
    private const string ReadError = "{\"error\":\"access denied\"}";
    private const string NoTransitionFound =
        "{\"kernelGeneral12\":[{\"utc\":\"2026-09-20T08:00:00Z\"}],\"kernelPower109\":[]}";
    private const string UnrecognisedTransitionType =
        "{\"kernelGeneral12\":[{\"utc\":\"2026-09-20T08:00:00Z\"}]," +
        "\"kernelPower109\":[{\"utc\":\"2026-09-20T07:00:00Z\",\"shutdownActionType\":99}]}";

    [TestMethod]
    public void ReasonForUnknownIsLogCouldNotBeReadForAParseFailure()
    {
        Assert.AreEqual(PowerCycleUnknownReason.LogCouldNotBeRead, PowerCycle.ReasonForUnknown("not json"));
    }

    [TestMethod]
    public void ReasonForUnknownIsLogCouldNotBeReadForARecordedReadError()
    {
        Assert.AreEqual(PowerCycleUnknownReason.LogCouldNotBeRead, PowerCycle.ReasonForUnknown(ReadError));
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(ReadError));
    }

    [TestMethod]
    public void ReasonForUnknownIsNoTransitionFoundWhenTheLogReadFineButNamedNoRecord()
    {
        Assert.AreEqual(
            PowerCycleUnknownReason.NoTransitionRecordFoundBetweenTheFirstHalfAndTheStart,
            PowerCycle.ReasonForUnknown(NoTransitionFound));
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(NoTransitionFound));
    }

    [TestMethod]
    public void ReasonForUnknownIsNoTransitionFoundForAnUnrecognisedShutdownActionType()
    {
        Assert.AreEqual(
            PowerCycleUnknownReason.NoTransitionRecordFoundBetweenTheFirstHalfAndTheStart,
            PowerCycle.ReasonForUnknown(UnrecognisedTransitionType));
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(UnrecognisedTransitionType));
    }

    [TestMethod]
    public void ReasonForUnknownIsNullWhenTheVerdictWasNotActuallyUnknown()
    {
        const string powerDown =
            "{\"kernelGeneral12\":[{\"utc\":\"2026-09-20T08:00:00Z\"}]," +
            "\"kernelPower109\":[{\"utc\":\"2026-09-20T07:00:00Z\",\"shutdownActionType\":4}]}";

        Assert.IsNull(PowerCycle.ReasonForUnknown(powerDown));
        Assert.AreEqual(PowerCycleVerdict.PowerDown, PowerCycle.Decide(powerDown));
    }

    [TestMethod]
    public void NotedWarningSaysTheLogCouldNotBeReadOnlyForThatReason()
    {
        string message = PowerCycleGate.NotedWarning(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Unknown, PowerCycleUnknownReason.LogCouldNotBeRead);
        StringAssert.Contains(message, "could not be read");
    }

    [TestMethod]
    public void NotedWarningNamesTheMissingTransitionRatherThanClaimingTheLogCouldNotBeRead()
    {
        string message = PowerCycleGate.NotedWarning(
            PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Unknown, PowerCycleUnknownReason.NoTransitionRecordFoundBetweenTheFirstHalfAndTheStart);

        Assert.IsFalse(message.Contains("could not be read", StringComparison.Ordinal),
            "The log was read fine; the warning must not claim it could not be, when the real reason is that no matching record was found.");
        StringAssert.Contains(message, "named no shut down or restart");
        StringAssert.Contains(message, "Carry on only if you want to try it anyway");
    }
}
