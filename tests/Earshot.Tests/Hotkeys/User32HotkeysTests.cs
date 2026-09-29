using Earshot.Contracts;
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
    // this machine on 2026-09-19 (net10.0-windows, .NET SDK as configured for this repository at the
    // time; the target gained an explicit platform version later that day, net10.0-windows10.0.19041.0),
    // calling UnregisterHotKey directly with the same arguments, printed "ok=False err=1419". The
    // system error codes page names that code: ERROR_HOTKEY_NOT_REGISTERED, 1419 (0x58B), "Hot key
    // is not registered."
    // https://learn.microsoft.com/windows/win32/debug/system-error-codes--1300-1699-
    // The UnregisterHotKey page itself names no error code at all, so nothing documented promises this one
    // for an id nothing has registered, on this or any Windows version: it is the observed probe result. If a
    // future OS observably returns something else, that probe result is what should change, not this
    // assertion into a tautology.
    [TestMethod]
    public void User32HotkeysUnregisterOfAnIdWeNeverTookIsReported()
    {
        var native = new User32Hotkeys();

        NativeCallResult result = native.Unregister(nint.Zero, 0x4AFF);

        Assert.IsFalse(result.Succeeded, "Unregistering an id this thread never registered was observed to fail on this machine.");
        Assert.AreEqual(1419, result.ErrorCode, "Observed as ERROR_HOTKEY_NOT_REGISTERED by a direct probe on this machine, not recalled from documentation.");
    }

    // Ctrl+Alt+Shift+Win+F24: five keys no keyboard has and no program asks for. Nothing is ever pressed. The two
    // tests below register it for the calling thread only (a null window handle makes the hot key the thread's own,
    // and its WM_HOTKEY would go to the thread's queue) and release it before they end, so they run the real
    // RegisterHotKey and UnregisterHotKey and touch nothing another program holds. They are meant for the private
    // desktop the test runs use.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
    private const uint UnusedChordModifiers = 0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x4000;

    private const uint UnusedChordKey = 0x87;

    [TestMethod]
    public void ARealChordNoOneUsesRegistersAndUnregisters()
    {
        var native = new User32Hotkeys();
        NativeCallResult registered = native.Register(nint.Zero, 0x4AF0, UnusedChordModifiers, UnusedChordKey);
        try
        {
            Assert.IsTrue(registered.Succeeded, "RegisterHotKey for a chord no one uses failed with error " + registered.ErrorCode + ".");
        }
        finally
        {
            NativeCallResult released = native.Unregister(nint.Zero, 0x4AF0);
            if (registered.Succeeded)
            {
                Assert.IsTrue(released.Succeeded, "UnregisterHotKey failed with error " + released.ErrorCode + ".");
            }
        }

        NativeCallResult again = native.Unregister(nint.Zero, 0x4AF0);
        Assert.IsFalse(again.Succeeded, "The chord was still registered after it was released.");
        Assert.AreEqual(1419, again.ErrorCode);
    }

    // The "taken" path with the real call: this test process registers a chord and then asks for it again under
    // another id, exactly what a second program holding it looks like to the caller, and the real error must come
    // back through HotkeyManager as AlreadyHeld with the raw code. 1409 is ERROR_HOTKEY_ALREADY_REGISTERED,
    // https://learn.microsoft.com/windows/win32/debug/system-error-codes--1300-1699- ; the assertion is the code
    // this machine returns, not one recalled.
    [TestMethod]
    public void ARealChordRegisteredTwiceIsReportedTakenWithTheRealCode()
    {
        var native = new User32Hotkeys();
        var settings = new HotkeySettings { Enabled = true, SwitchToPc = "Ctrl+Alt+Shift+Win+F24", SwitchToPhone = string.Empty };
        var holderWindow = new FakeMessageWindow { Handle = 0 };
        var pressedWindow = new FakeMessageWindow { Handle = 0 };
        using var holder = new HotkeyManager(holderWindow, native, new CapturingLog());
        var log = new CapturingLog();
        using var second = new HotkeyManager(pressedWindow, native, log);

        IReadOnlyList<HotkeyRegistrationOutcome> first = holder.Apply(settings);
        IReadOnlyList<HotkeyRegistrationOutcome> taken = second.Apply(settings);

        HotkeyRegistrationOutcome heldOutcome = first.Single(o => o.Action == HotkeyAction.SwitchToPc);
        Assert.AreEqual(HotkeyRegistrationState.Registered, heldOutcome.State, "The first registration of a chord no one uses must succeed.");
        HotkeyRegistrationOutcome outcome = taken.Single(o => o.Action == HotkeyAction.SwitchToPc);
        Assert.AreEqual(HotkeyRegistrationState.AlreadyHeld, outcome.State);
        Assert.AreEqual(1409, outcome.ErrorCode);
        StringAssert.Contains(outcome.Message, "already in use by another program");
        Assert.IsTrue(log.Has(LogLevel.Debug, "result=failed error=1409"), "The raw Win32 error was not logged.");
    }
}
