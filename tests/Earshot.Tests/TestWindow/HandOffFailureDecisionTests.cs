using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A live sandbox run of test 10's variant 1 first surfaced this: MainForm.ShowHandOff used to
// treat any overall other than "pass" as a failed first half. Test 10's own first half never
// records a criterion (only the stateBeforeRestart finding), so EvidenceStore's own
// RecomputeOverall always reads it as "inconclusive" (no criteria at all gives inconclusive),
// which is a normal, correct shape for that half, not a failure; the hand-off screen was wrongly
// telling the owner "the first half did not pass... fix what it names" for every variant, every
// time. Copy.FirstHalfGenuinelyFailed is the fix, pinned here.
[TestClass]
public sealed class HandOffFailureDecisionTests
{
    [TestMethod]
    public void AGenuineFailureIsGenuinelyFailed()
    {
        Assert.IsTrue(Copy.FirstHalfGenuinelyFailed("fail"));
    }

    [TestMethod]
    public void InconclusiveIsNotAFailureTest10sOwnFirstHalfShapeEveryTime()
    {
        Assert.IsFalse(Copy.FirstHalfGenuinelyFailed("inconclusive"));
    }

    [TestMethod]
    public void PassIsNotAFailure()
    {
        Assert.IsFalse(Copy.FirstHalfGenuinelyFailed("pass"));
    }
}
