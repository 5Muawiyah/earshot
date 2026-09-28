using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real execution kept for the appbar registration: ABM_NEW and ABM_REMOVE against a real window
// (ShellMessageWindow, already used by the tray and already exercised directly by the existing tray
// tests with no private desktop, since it is never shown: CreateParams carries no WS_VISIBLE). A local
// probe found ABM_NEW itself refused (returns 0) for a window on a CreateDesktopW private desktop, so
// this stays on the test host's own desktop like the existing ShellMessageWindow tests, never showing
// anything. Transient and in-process: both calls run and the registration is removed in the same test.
[TestClass]
public sealed class AppBarRegistrationTests
{
    [TestMethod]
    public void RegisterReturnsOkAndDisposeRemovesIt()
    {
        var log = new CapturingLog();
        using var window = new ShellMessageWindow(log);
        var appBar = new AppBarRegistration(window.Handle, log);

        // try/finally, not a bare sequence of calls: a failed assertion between Register and Dispose must
        // still reach ABM_REMOVE, or the appbar stays registered against this process for the rest of the
        // test run instead of just failing the one test.
        try
        {
            var registered = appBar.Register();
            Assert.IsTrue(registered.Ok, "ABM_NEW: " + registered.CodeName + " " + registered.Detail);
        }
        finally
        {
            appBar.Dispose();
        }

        // A second Dispose is a no-op (idempotent), not a second ABM_REMOVE.
        appBar.Dispose();
    }

    [TestMethod]
    public void ReregisterRemovesThenAddsAgain()
    {
        var log = new CapturingLog();
        using var window = new ShellMessageWindow(log);
        var appBar = new AppBarRegistration(window.Handle, log);

        // Same reasoning as above: Dispose must run even if an assertion in between fails.
        try
        {
            Assert.IsTrue(appBar.Register().Ok);

            (Earshot.Contracts.StepOutcome removed, Earshot.Contracts.StepOutcome added) = appBar.Reregister();
            Assert.IsTrue(removed.Ok, "ABM_REMOVE: " + removed.CodeName);
            Assert.IsTrue(added.Ok, "ABM_NEW: " + added.CodeName);
        }
        finally
        {
            appBar.Dispose();
        }
    }
}
