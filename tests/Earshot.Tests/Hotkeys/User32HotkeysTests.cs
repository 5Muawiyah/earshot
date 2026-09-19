using Earshot.Hotkeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

[TestClass]
public sealed class User32HotkeysTests
{
    // The only test in this feature that calls into user32.dll. It cannot disturb another program,
    // because a hot key belongs to the thread or window that registered it and this test thread
    // registered none: it asks to unregister an id (0x4AFF) this process never took.
    //
    // A version of this test that only checked "if it failed, the code is non-zero" stayed green even
    // with User32Hotkeys.Unregister's body replaced by a fabricated success: that proves nothing about
    // the P/Invoke binding. This asserts the actual, observed outcome instead. A standalone probe run on
    // this machine (net10.0-windows, .NET SDK as configured for this repository), calling UnregisterHotKey
    // directly with the same arguments, printed "ok=False err=1419": ERROR_HOTKEY_NOT_REGISTERED, "Hot
    // key is not registered", which is exactly what
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey says happens
    // for an id nothing has registered. The Win32 API docs do not promise this code for every Windows
    // version, so if a future OS observably returns something else, that probe result is what should
    // change, not this assertion into a tautology.
    [TestMethod]
    public void User32HotkeysUnregisterOfAnIdWeNeverTookIsReported()
    {
        var native = new User32Hotkeys();

        NativeCallResult result = native.Unregister(nint.Zero, 0x4AFF);

        Assert.IsFalse(result.Succeeded, "Unregistering an id this thread never registered was observed to fail on this machine.");
        Assert.AreEqual(1419, result.ErrorCode, "Observed as ERROR_HOTKEY_NOT_REGISTERED by a direct probe on this machine, not recalled from documentation.");
    }
}
