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

    // A chord no keyboard has and no program asks for: one of F13 to F24, which no keyboard carries, with a random
    // non-empty set of Alt, Ctrl, Shift and Win, chosen once for the run. Nothing is ever pressed. Two gates running
    // at the same time on one machine would collide on any one fixed chord (the second registration would be reported
    // taken, and fail), so each run draws its own from the 180 there are. The two tests below register it for the
    // calling thread only (a null window handle makes the hot key the thread's own, and its WM_HOTKEY would go to the
    // thread's queue) and release it before they end, so they run the real RegisterHotKey and UnregisterHotKey and
    // touch nothing another program holds. They are meant for the private desktop the test runs use.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
    //
    // A chord drawn at random can still be one another program on this machine holds (keyboard software does use
    // these keys), and Windows says so with 1409 when it is registered. So a draw is tried, and released, before the
    // test relies on it, and another is drawn while the answer is 1409, so a test fails only for a reason of its own.
    private static (uint Modifiers, uint Key, string Text) FreeChord(User32Hotkeys native)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            (uint Modifiers, uint Key, string Text) chord = DrawUnusedChord();
            NativeCallResult tried = native.Register(nint.Zero, 0x4AFE, chord.Modifiers, chord.Key);
            if (tried.Succeeded)
            {
                native.Unregister(nint.Zero, 0x4AFE);
                return chord;
            }

            Assert.AreEqual(1409, tried.ErrorCode, "RegisterHotKey for " + chord.Text + " failed with something other than 'already registered'.");
        }

        Assert.Fail("Sixty chords drawn from F13 to F24 were all held by other programs.");
        return default;
    }

    private static (uint Modifiers, uint Key, string Text) DrawUnusedChord()
    {
        (string Name, uint Flag)[] all = [("Ctrl", 0x0002), ("Alt", 0x0001), ("Shift", 0x0004), ("Win", 0x0008)];
        int mask = Random.Shared.Next(1, 16);
        int function = Random.Shared.Next(13, 25);
        uint modifiers = 0x4000;
        var text = new List<string>();
        for (int i = 0; i < all.Length; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                modifiers |= all[i].Flag;
                text.Add(all[i].Name);
            }
        }

        text.Add("F" + function.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (modifiers, 0x7Cu + (uint)(function - 13), string.Join('+', text));
    }

    [TestMethod]
    public void ARealChordNoOneUsesRegistersAndUnregisters()
    {
        var native = new User32Hotkeys();
        (uint modifiers, uint key, string text) = FreeChord(native);
        NativeCallResult registered = native.Register(nint.Zero, 0x4AF0, modifiers, key);
        try
        {
            Assert.IsTrue(registered.Succeeded, "RegisterHotKey for the chord " + text + " failed with error " + registered.ErrorCode + ".");
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
        var settings = new HotkeySettings { Enabled = true, SwitchToPc = FreeChord(native).Text, SwitchToPhone = string.Empty };
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
