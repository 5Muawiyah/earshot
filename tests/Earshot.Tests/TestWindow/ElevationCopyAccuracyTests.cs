using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The rehearsal status copy used to say a passing check "offers 00's uninstall and 07's plan B".
// It does not: rows 00 and 07 never offer either in this build
// (Copy.RestoreUninstallOfferNotAvailable, Copy.PlanBNotAvailable both say plainly that the
// control is not wired and name the console command instead). The copy must never claim an
// offer that does not exist.
[TestClass]
public sealed class ElevationCopyAccuracyTests
{
    [TestMethod]
    public void RehearsalUnlocksRowsNeverClaimsAnOfferThatDoesNotExist()
    {
        Assert.IsFalse(Copy.RehearsalUnlocksRows.Contains("offers", StringComparison.OrdinalIgnoreCase),
            "The copy must not say a passing check \"offers\" 00's uninstall or 07's plan B: neither is wired to a control in this build.");
    }

    [TestMethod]
    public void RehearsalUnlocksRowsNamesTheConsoleRouteInstead()
    {
        StringAssert.Contains(Copy.RehearsalUnlocksRows, "console");
        StringAssert.Contains(Copy.RehearsalUnlocksRows, "00");
        StringAssert.Contains(Copy.RehearsalUnlocksRows, "07");
    }

    [TestMethod]
    public void RehearsalUnlocksRowsStillSaysItUnlocksRow15()
    {
        StringAssert.Contains(Copy.RehearsalUnlocksRows, "15");
    }
}
