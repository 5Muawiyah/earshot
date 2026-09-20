using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// section 10.3: "declined-prompt copy derived from a fixture." Each case below is the exact
// criteria-outcome shape a real result.json would carry for that situation.
[TestClass]
public sealed class ElevatedDeclineCopyTests
{
    [TestMethod]
    public void UninstallPassedButInstallAgainDidNotNeedsTheNotInstalledWarning()
    {
        Assert.IsTrue(Copy.NeedsNotInstalledWarning(uninstallPassed: true, installAgainPassed: false));
    }

    [TestMethod]
    public void BothPassingNeedsNoWarning()
    {
        Assert.IsFalse(Copy.NeedsNotInstalledWarning(uninstallPassed: true, installAgainPassed: true));
    }

    [TestMethod]
    public void UninstallItselfNotPassingNeedsNoWarningNothingWasRemovedYet()
    {
        Assert.IsFalse(Copy.NeedsNotInstalledWarning(uninstallPassed: false, installAgainPassed: false));
    }
}
