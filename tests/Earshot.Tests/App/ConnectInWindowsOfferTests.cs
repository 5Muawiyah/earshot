using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// After a Connect that failed, the failure card carries Connect in Windows, so the person has one thing to press when Earshot
// could not connect: the connect said so (OfferConnectInWindows). Fix connection takes the card's one button when it is due.
[TestClass]
public sealed class ConnectInWindowsOfferTests
{
    private static ToggleReport Failed(bool offer) =>
        new(true, OpStatus.Failed, "reason", [], Cancelled: false) { FailureClass = FailureClass.Unknown, OfferConnectInWindows = offer };

    [TestMethod]
    public void AFailedConnectThatSaysSoGivesTheButton()
    {
        Assert.IsTrue(ConnectInWindowsOffer.ShouldOffer(Failed(offer: true), cancelled: false, wasConnect: true, fixOffered: false));
    }

    [TestMethod]
    public void NoButtonWhenTheConnectDidNotSaySoOrWasCancelledOrWasADisconnect()
    {
        Assert.IsFalse(ConnectInWindowsOffer.ShouldOffer(Failed(offer: false), cancelled: false, wasConnect: true, fixOffered: false));
        Assert.IsFalse(ConnectInWindowsOffer.ShouldOffer(Failed(offer: true), cancelled: true, wasConnect: true, fixOffered: false));
        Assert.IsFalse(ConnectInWindowsOffer.ShouldOffer(Failed(offer: true), cancelled: false, wasConnect: false, fixOffered: false));
    }

    [TestMethod]
    public void NoButtonAfterASuccessOrWhenFixConnectionHasTheCard()
    {
        var success = new ToggleReport(true, OpStatus.Success, "done", [], Cancelled: false) { OfferConnectInWindows = true };

        Assert.IsFalse(ConnectInWindowsOffer.ShouldOffer(success, cancelled: false, wasConnect: true, fixOffered: false));
        Assert.IsFalse(ConnectInWindowsOffer.ShouldOffer(Failed(offer: true), cancelled: false, wasConnect: true, fixOffered: true));
    }
}
