using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// What the Fix offer reads off a finished switch. The connect decides both facts (whose side the failure was on, and whether
// this report made the second PC-side failure in a row); FixOfferFacts.From only carries them to the offer rule.
[TestClass]
public sealed class FixOfferFactsTests
{
    private static ToggleReport Failed(FailureClass failure, bool due) =>
        new(true, OpStatus.Failed, "reason", [], Cancelled: false) { FailureClass = failure, FixOfferDue = due };

    private static bool Offered(ToggleReport report) =>
        FixOffer.ShouldOffer(FixOfferFacts.From(report), report.IsSuccess, report.Cancelled, wasConnect: report.Connect);

    [TestMethod]
    public void APcSideFailureThatMadeTheSecondInARowGivesTheButton()
    {
        ToggleReport report = Failed(FailureClass.PcSide, due: true);

        IFixOfferFacts facts = FixOfferFacts.From(report);

        Assert.IsTrue(facts.FixOfferDue);
        Assert.AreEqual(FixFailureClass.PcSide, facts.FailureClass);
        Assert.IsTrue(Offered(report));
    }

    [TestMethod]
    public void APcSideFailureBeforeTheSecondInARowGivesNone()
    {
        Assert.IsFalse(Offered(Failed(FailureClass.PcSide, due: false)));
    }

    [TestMethod]
    public void OutOfRangeACaseThatIsShutAndNothingHeardNeverGiveTheButtonEvenWhenTheCountSaysDue()
    {
        var timedOut = new ConnectResult(ConnectOutcome.AttemptedTimedOut, "", []);

        foreach (DeviceHint hint in new[] { DeviceHint.NotHeard, DeviceHint.LidClosed, DeviceHint.InUseElsewhere })
        {
            ConnectFailure failure = ConnectFailureClassifier.Classify(timedOut, hint, retried: true);
            ToggleReport report = Failed(failure.Class, due: true);

            Assert.AreEqual(FailureClass.DeviceSide, failure.Class, hint.ToString());
            Assert.AreEqual(FixFailureClass.DeviceSide, FixOfferFacts.From(report).FailureClass, hint.ToString());
            Assert.IsFalse(Offered(report), hint + " is the AirPods' side, so a fix on this PC is not offered.");
        }
    }

    [TestMethod]
    public void ATemporaryFailureGivesNoButton()
    {
        ToggleReport report = Failed(FailureClass.Temporary, due: true);

        Assert.AreEqual(FixFailureClass.Temporary, FixOfferFacts.From(report).FailureClass);
        Assert.IsFalse(Offered(report));
    }

    [TestMethod]
    public void APlainReportGivesNoneOfTheFacts()
    {
        var report = new ToggleReport(true, OpStatus.Failed, "reason", [], Cancelled: false);

        IFixOfferFacts facts = FixOfferFacts.From(report);

        Assert.IsFalse(facts.FixOfferDue);
        Assert.AreEqual(FixFailureClass.None, facts.FailureClass);
        Assert.IsFalse(Offered(report));
    }

    [TestMethod]
    public void ASuccessACancelAndADisconnectNeverGiveTheButton()
    {
        var success = new ToggleReport(true, OpStatus.Success, "done", [], Cancelled: false) { FixOfferDue = true };
        ToggleReport cancelled = new(true, OpStatus.Failed, "cancelled", [], Cancelled: true) { FailureClass = FailureClass.PcSide, FixOfferDue = true };
        ToggleReport disconnect = new(false, OpStatus.Failed, "reason", [], Cancelled: false) { FailureClass = FailureClass.PcSide, FixOfferDue = true };

        Assert.IsFalse(Offered(success));
        Assert.IsFalse(Offered(cancelled));
        Assert.IsFalse(Offered(disconnect));
    }
}
