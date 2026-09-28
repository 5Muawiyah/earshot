using System.Linq;
using Earshot.Contracts;
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
//
// Each successful ABM_NEW here calls WidgetRealSurfaceGuardTests.AllowRealConstruction(RealWidgetSurface.AppBar) right after
// asserting it reached the real Explorer, so the assembly-wide guard can tell this named real execution
// apart from an unnoticed one elsewhere (AppBarWiringTests drives the same wiring on a private desktop
// instead, where ABM_NEW is refused - see ProbeAppBarOnPrivateDesktopTests - precisely so it never needs to
// be on this list, and never touches AllowRealConstruction).
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.AppBar);
        }
        finally
        {
            appBar.Dispose();
        }

        // A second Dispose is a no-op (idempotent), not a second ABM_REMOVE.
        appBar.Dispose();

        // Before the fix, Dispose's own ABM_REMOVE outcome was discarded outright: nothing in the log said
        // whether it had actually succeeded. TrayReport.DescribeStep's own format, "<step> ok|failed <code>
        // (<hex>)", is what LogAppBarOutcome already uses for Register and Reregister's outcomes.
        Assert.IsTrue(log.Has(LogLevel.Debug, "AppBar: sh-app-bar-message:abm-remove"),
            "Dispose's own ABM_REMOVE outcome must reach the log: " +
            string.Join(" | ", log.Entries.Select(e => e.Level + ":" + e.Message)));
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
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.AppBar);

            (Earshot.Contracts.StepOutcome removed, Earshot.Contracts.StepOutcome added) = appBar.Reregister();
            Assert.IsTrue(removed.Ok, "ABM_REMOVE: " + removed.CodeName);
            Assert.IsTrue(added.Ok, "ABM_NEW: " + added.CodeName);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.AppBar);
        }
        finally
        {
            appBar.Dispose();
        }
    }
}
