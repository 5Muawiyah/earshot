using System.Drawing;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The window-free decisions of a card nobody asked for (UnaskedGate, OpenGeneration). The presenter tests that put real cards on
// a private desktop are in WidgetCardUnaskedTests and run on Windows only.
[TestClass]
public sealed class UnaskedGateTests
{
    private static ForegroundWindowReading Game(Rectangle monitor) =>
        new(new WindowIdentity("GameWindowClass", false), monitor, monitor, "Display");

    [TestMethod]
    public void ANewRequestMakesEveryEarlierOneStale()
    {
        var generation = new OpenGeneration();
        int first = generation.Next();
        Assert.IsTrue(generation.IsCurrent(first));

        int second = generation.Next();

        Assert.IsFalse(generation.IsCurrent(first), "Work queued for the first open is stale once a second was asked for.");
        Assert.IsTrue(generation.IsCurrent(second));
        Assert.AreEqual(second, generation.Current);
    }

    [TestMethod]
    public void OnlyTheQuietStatesAcceptACardNobodyAskedFor()
    {
        Assert.IsTrue(UnaskedGate.AcceptsUnrequestedCard(Shell.QUNS_ACCEPTS_NOTIFICATIONS));
        Assert.IsTrue(UnaskedGate.AcceptsUnrequestedCard(Shell.QUNS_APP));
        Assert.IsFalse(UnaskedGate.AcceptsUnrequestedCard(Shell.QUNS_BUSY));
        Assert.IsFalse(UnaskedGate.AcceptsUnrequestedCard(Shell.QUNS_NOT_PRESENT));
        Assert.AreEqual("QUNS_BUSY", UnaskedGate.NotificationStateName(Shell.QUNS_BUSY));
        Assert.AreEqual("QUNS 99", UnaskedGate.NotificationStateName(99));
    }

    [TestMethod]
    public void TheFallbackCardShowsOnlyWhereAnyCardNobodyClickedForMay()
    {
        Assert.IsTrue(UnaskedGate.FallbackMayShow(Shell.QUNS_ACCEPTS_NOTIFICATIONS));
        Assert.IsTrue(UnaskedGate.FallbackMayShow(Shell.QUNS_APP));
        Assert.IsFalse(UnaskedGate.FallbackMayShow(Shell.QUNS_BUSY));
        Assert.IsFalse(UnaskedGate.FallbackMayShow(Shell.QUNS_RUNNING_D3D_FULL_SCREEN));
        Assert.IsFalse(UnaskedGate.FallbackMayShow(Shell.QUNS_PRESENTATION_MODE));
        Assert.IsFalse(UnaskedGate.FallbackMayShow(Shell.QUNS_QUIET_TIME));
    }

    [TestMethod]
    public void AFailedStateReadClosesAnOpenCardAtTheRecheck()
    {
        DisplayInfo one = CaseOpenCardChoiceTests.One;

        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(unchecked((int)0x80004005), Shell.QUNS_ACCEPTS_NOTIFICATIONS, 1, null, one), "A card on a display.");
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(unchecked((int)0x80004005), Shell.QUNS_ACCEPTS_NOTIFICATIONS, 0, null, null), "A card with no display.");
    }

    [TestMethod]
    public void ACardOnADisplayClosesOnlyWhenAFullScreenApplicationIsOnIt()
    {
        DisplayInfo one = CaseOpenCardChoiceTests.One;
        DisplayInfo two = CaseOpenCardChoiceTests.Two;
        ForegroundWindowReading onTwo = Game(two.Bounds);

        Assert.IsFalse(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS, 2, null, one));
        Assert.IsFalse(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_BUSY, 2, onTwo, one), "The game is on the other display.");
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_BUSY, 2, onTwo, two));
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_PRESENTATION_MODE, 2, null, one), "No display takes it in presentation mode.");
    }

    // A card placed without a display list answers to the global state: it was only allowed when cards may show anywhere.
    [TestMethod]
    public void ACardWithNoDisplayClosesWhenTheGlobalStateNoLongerAllowsIt()
    {
        Assert.IsFalse(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS, 0, null, null));
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_BUSY, 0, null, null));
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_RUNNING_D3D_FULL_SCREEN, 0, null, null));
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_QUIET_TIME, 0, null, null));
    }

    // The result of his own click is shown without asking the notification state, so a recheck does not close it either, whatever
    // it reads (a failed read included).
    [TestMethod]
    public void ARecheckNeverClosesACardThatFollowsHisClick()
    {
        DisplayInfo one = CaseOpenCardChoiceTests.One;
        ForegroundWindowReading game = Game(one.Bounds);

        Assert.IsFalse(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_RUNNING_D3D_FULL_SCREEN, 1, game, one, afterClick: true));
        Assert.IsFalse(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_BUSY, 0, null, null, afterClick: true));
        Assert.IsFalse(UnaskedGate.ClosesOnRecheck(unchecked((int)0x80004005), Shell.QUNS_ACCEPTS_NOTIFICATIONS, 1, null, one, afterClick: true));
        Assert.IsTrue(UnaskedGate.ClosesOnRecheck(0, Shell.QUNS_RUNNING_D3D_FULL_SCREEN, 1, game, one, afterClick: false), "Any other card closes.");
    }
}
