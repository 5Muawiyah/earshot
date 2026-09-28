using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The local probe AppBarWiringTests and WidgetShellSignalTests rely on: ABM_NEW is refused (returns FALSE,
// never a Win32 code SHAppBarMessage documents none of) for a window whose owning thread is bound to a
// CreateDesktopW private desktop. SHAppBarMessage's own internal Shell_TrayWnd lookup is scoped to the
// calling thread's current desktop the same way FindWindow and EnumWindows are documented to be, so it
// never finds the owner's real taskbar from there and the call never reaches it. AppBarRegistration.Register
// never increments RealRegistrationCount unless this succeeds, so this test needs no
// WidgetRealSurfaceGuardTests.AllowRealConstruction(RealWidgetSurface.AppBar) call: a refused ABM_NEW is not a real registration.
// https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations-and-desktops
[TestClass]
public sealed class ProbeAppBarOnPrivateDesktopTests
{
    [TestMethod]
    public void RegisterIsRefusedOnAPrivateDesktop()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var window = new ShellMessageWindow(log);
            var appBar = new AppBarRegistration(window.Handle, log);
            try
            {
                var outcome = appBar.Register();
                Assert.IsFalse(outcome.Ok, "ABM_NEW must be refused on a private desktop, never reaching the real Explorer.");
            }
            finally
            {
                appBar.Dispose();
            }
        });
    }
}
