using Earshot.Contracts;
using Earshot.Hotkeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

[TestClass]
public sealed class HotkeyManagerTests
{
    private const int ConnectionId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.ToggleConnection;
    private const int ProtectionId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.ToggleAudioProtection;
    private const int BlockId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.ToggleBlockAtBoot;
    private const int SpeakId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.SpeakStatus;

    private static (HotkeyManager Manager, FakeMessageWindow Window, FakeNativeHotkeys Native, CapturingLog Log) Build()
    {
        var window = new FakeMessageWindow();
        var native = new FakeNativeHotkeys();
        var log = new CapturingLog();
        return (new HotkeyManager(window, native, log), window, native, log);
    }

    private static HotkeySettings EnabledWith(string connect = "", string protect = "", string block = "", string speak = "") =>
        new() { Enabled = true, ToggleConnection = connect, ToggleAudioProtection = protect, ToggleBlockAtBoot = block, SpeakStatus = speak };

    [TestMethod]
    public void ApplyWithDefaultsRegistersNothing()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(HotkeySettings.Defaults);

        Assert.AreEqual(4, outcomes.Count);
        foreach (HotkeyRegistrationOutcome outcome in outcomes)
        {
            Assert.AreEqual(HotkeyRegistrationState.NotSet, outcome.State);
            Assert.AreEqual("Shortcuts are switched off.", outcome.Message);
        }

        Assert.AreEqual(0, native.Calls.Count);
    }

    [TestMethod]
    public void ApplyWithTextsButDisabledRegistersNothing()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        var settings = new HotkeySettings
        {
            Enabled = false,
            ToggleConnection = "Ctrl+Alt+C",
            ToggleAudioProtection = "Ctrl+Alt+A",
            ToggleBlockAtBoot = "Ctrl+Alt+B",
            SpeakStatus = "Ctrl+Alt+S",
        };

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        Assert.AreEqual(0, native.Calls.Count);
        Assert.IsTrue(outcomes.All(o => o.State == HotkeyRegistrationState.NotSet));
    }

    [TestMethod]
    public void ApplyRegistersEachSetActionWithNoRepeat()
    {
        (HotkeyManager manager, FakeMessageWindow window, FakeNativeHotkeys native, _) = Build();
        HotkeySettings settings = EnabledWith(connect: "Ctrl+Alt+C", protect: "Ctrl+Alt+A", block: "Ctrl+Alt+B", speak: "");

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        Assert.AreEqual(3, native.Calls.Count);
        AssertRegistered(native.Calls[0], window.Handle, ConnectionId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);
        AssertRegistered(native.Calls[1], window.Handle, ProtectionId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);
        AssertRegistered(native.Calls[2], window.Handle, BlockId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42);

        HotkeyRegistrationOutcome speak = outcomes.Single(o => o.Action == HotkeyAction.SpeakStatus);
        Assert.AreEqual(HotkeyRegistrationState.NotSet, speak.State);
        Assert.AreEqual("No shortcut set.", speak.Message);
        Assert.IsFalse(native.Calls.Any(c => c.Id == SpeakId));
    }

    private static void AssertRegistered(NativeCall call, nint handle, int id, HotkeyModifiers modifiers, ushort virtualKey)
    {
        Assert.AreEqual("RegisterHotKey", call.Method);
        Assert.AreEqual(handle, call.Handle);
        Assert.AreEqual(id, call.Id);
        Assert.AreEqual(virtualKey, call.VirtualKey);
        Assert.AreEqual(0x4000u, call.Modifiers & 0x4000u, "MOD_NOREPEAT must always be set.");
        Assert.AreEqual((uint)modifiers | 0x4000u, call.Modifiers);
    }

    [TestMethod]
    public void ApplyReportsAlreadyHeldFor1409()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        native.SetResult(ProtectionId, NativeCallResult.Failure(1409));
        HotkeySettings settings = EnabledWith(connect: "Ctrl+Alt+C", protect: "Ctrl+Alt+A", block: "Ctrl+Alt+B");

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        HotkeyRegistrationOutcome protection = outcomes.Single(o => o.Action == HotkeyAction.ToggleAudioProtection);
        Assert.AreEqual(HotkeyRegistrationState.AlreadyHeld, protection.State);
        Assert.AreEqual(1409, protection.ErrorCode);
        Assert.AreEqual("Ctrl+Alt+A is already in use by another program, so it was not set. Windows reported error 1409.", protection.Message);

        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.ToggleConnection).State);
        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.ToggleBlockAtBoot).State);
    }

    [TestMethod]
    public void ApplyReportsTheCodeWindowsGave()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        native.SetResult(ConnectionId, NativeCallResult.Failure(1400));
        HotkeySettings settings = EnabledWith(connect: "Ctrl+Alt+C");

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        HotkeyRegistrationOutcome outcome = outcomes.Single(o => o.Action == HotkeyAction.ToggleConnection);
        Assert.AreEqual(HotkeyRegistrationState.Failed, outcome.State);
        Assert.AreEqual(1400, outcome.ErrorCode);
        Assert.IsTrue(outcome.Message.EndsWith("Windows reported error 1400.", StringComparison.Ordinal));
        Assert.IsFalse(outcome.Message.Contains("1409", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApplyRefusesF12WithoutCallingWindows()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        HotkeySettings settings = EnabledWith(connect: "Ctrl+F12");

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        HotkeyRegistrationOutcome outcome = outcomes.Single(o => o.Action == HotkeyAction.ToggleConnection);
        Assert.AreEqual(HotkeyRegistrationState.Refused, outcome.State);
        Assert.AreEqual("F12 is kept by Windows for the debugger, so it cannot be a shortcut.", outcome.Message);
        Assert.IsFalse(native.Calls.Any(c => c.Id == ConnectionId));
    }

    [TestMethod]
    public void ApplyRejectsDuplicateCombinationAcrossActions()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        HotkeySettings settings = EnabledWith(connect: "Ctrl+Alt+P", protect: "Ctrl+Alt+P");

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.ToggleConnection).State);
        HotkeyRegistrationOutcome duplicate = outcomes.Single(o => o.Action == HotkeyAction.ToggleAudioProtection);
        Assert.AreEqual(HotkeyRegistrationState.DuplicateInSettings, duplicate.State);
        Assert.AreEqual("Ctrl+Alt+P is already set for another command here.", duplicate.Message);
        Assert.AreEqual(1, native.Calls.Count(c => c.Method == "RegisterHotKey"));
    }

    [TestMethod]
    public void ApplyUnregistersHeldIdsBeforeRegisteringAgain()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C", protect: "Ctrl+Alt+A"));
        int afterFirst = native.Calls.Count;

        manager.Apply(EnabledWith(connect: "Ctrl+Shift+X", protect: "Ctrl+Shift+Y"));

        List<NativeCall> secondRound = native.Calls.Skip(afterFirst).ToList();
        int lastUnregisterIndex = secondRound.FindLastIndex(c => c.Method == "UnregisterHotKey");
        int firstRegisterIndex = secondRound.FindIndex(c => c.Method == "RegisterHotKey");
        Assert.AreEqual(2, secondRound.Count(c => c.Method == "UnregisterHotKey"));
        Assert.IsTrue(firstRegisterIndex > lastUnregisterIndex, "Every unregister of the held ids must come before any register of the new round.");
    }

    [TestMethod]
    public void ActivatedRaisedOnlyForHeldIds()
    {
        (HotkeyManager manager, FakeMessageWindow window, _, CapturingLog log) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));

        var raised = new List<HotkeyAction>();
        manager.Activated += (_, e) => raised.Add(e.Action);

        window.Raise(0x0312, ConnectionId, 0);
        Assert.AreEqual(1, raised.Count);
        Assert.AreEqual(HotkeyAction.ToggleConnection, raised[0]);

        window.Raise(0x0312, 999, 0);
        window.Raise(0x0312, -1, 0);
        Assert.AreEqual(1, raised.Count);
        Assert.IsTrue(log.Has(LogLevel.Debug, "WM_HOTKEY ignored id=999"));
        Assert.IsTrue(log.Has(LogLevel.Debug, "WM_HOTKEY ignored id=-1"));

        int entriesBefore = log.Entries.Count;
        window.Raise(0x0100, ConnectionId, 0);
        Assert.AreEqual(1, raised.Count);
        Assert.AreEqual(entriesBefore, log.Entries.Count);
    }

    [TestMethod]
    public void ReleaseAllUnregistersAndStopsEvents()
    {
        (HotkeyManager manager, FakeMessageWindow window, FakeNativeHotkeys native, _) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C", protect: "Ctrl+Alt+A"));
        int afterApply = native.Calls.Count;

        manager.ReleaseAll();

        List<NativeCall> released = native.Calls.Skip(afterApply).ToList();
        Assert.AreEqual(2, released.Count);
        Assert.IsTrue(released.All(c => c.Method == "UnregisterHotKey"));
        Assert.IsTrue(released.Any(c => c.Id == ConnectionId));
        Assert.IsTrue(released.Any(c => c.Id == ProtectionId));

        var raised = new List<HotkeyAction>();
        manager.Activated += (_, e) => raised.Add(e.Action);
        window.Raise(0x0312, ConnectionId, 0);
        Assert.AreEqual(0, raised.Count);

        int afterRelease = native.Calls.Count;
        manager.ReleaseAll();
        Assert.AreEqual(afterRelease, native.Calls.Count);
    }

    [TestMethod]
    public void DisposeIsIdempotentAndBlocksApply()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, CapturingLog log) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));
        int afterApply = native.Calls.Count;

        manager.Dispose();
        Assert.AreEqual(afterApply + 1, native.Calls.Count);
        Assert.IsTrue(native.Calls[^1].Method == "UnregisterHotKey");

        int callsAfterFirstDispose = native.Calls.Count;
        int entriesAfterFirstDispose = log.Entries.Count;
        manager.Dispose();
        Assert.AreEqual(callsAfterFirstDispose, native.Calls.Count);
        Assert.AreEqual(entriesAfterFirstDispose, log.Entries.Count);

        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.Apply(HotkeySettings.Defaults));
    }

    [TestMethod]
    public void ApplyFromAnotherThreadThrows()
    {
        (HotkeyManager manager, FakeMessageWindow window, FakeNativeHotkeys native, _) = Build();
        window.IsOwnedByCurrentThread = false;

        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(() => manager.Apply(HotkeySettings.Defaults));

        Assert.AreEqual("Hotkeys must be applied on the thread that owns the window.", ex.Message);
        Assert.AreEqual(0, native.Calls.Count);
    }

    [TestMethod]
    public void LogHasOneLinePerNativeCall()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, CapturingLog log) = Build();
        native.SetResult(BlockId, NativeCallResult.Failure(1409));

        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C", protect: "Ctrl+Alt+A", block: "Ctrl+Alt+B"));

        List<LogEntry> entries = log.Entries.ToList();
        Assert.AreEqual(3, entries.Count);
        Assert.IsTrue(entries.All(e => e.Level == LogLevel.Debug));
        Assert.AreEqual(2, entries.Count(e => e.Message.StartsWith("RegisterHotKey", StringComparison.Ordinal) && e.Message.EndsWith("result=ok", StringComparison.Ordinal)));
        Assert.AreEqual(1, entries.Count(e => e.Message.Contains("result=failed error=1409", StringComparison.Ordinal)));
        Assert.IsTrue(entries[2].Message.Contains("id=0x" + BlockId.ToString("X"), StringComparison.Ordinal));
    }

    // Spec section 5: "Apply, ReleaseAll and Dispose must be called on the thread that owns the window."
    // Apply was already checked (ApplyFromAnotherThreadThrows); this covers the other two, which an
    // off-thread call would otherwise silently fail to release (UnregisterHotKey frees a hot key only for
    // the thread that registered it).
    [TestMethod]
    public void ReleaseAllFromAnotherThreadThrows()
    {
        (HotkeyManager manager, FakeMessageWindow window, FakeNativeHotkeys native, _) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));
        int afterApply = native.Calls.Count;
        window.IsOwnedByCurrentThread = false;

        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(manager.ReleaseAll);

        Assert.AreEqual("Hotkeys must be applied on the thread that owns the window.", ex.Message);
        Assert.AreEqual(afterApply, native.Calls.Count, "Nothing must be unregistered off-thread.");
    }

    [TestMethod]
    public void DisposeFromAnotherThreadThrowsAndReleasesNothing()
    {
        (HotkeyManager manager, FakeMessageWindow window, FakeNativeHotkeys native, _) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));
        int afterApply = native.Calls.Count;
        window.IsOwnedByCurrentThread = false;

        Assert.ThrowsExactly<InvalidOperationException>(manager.Dispose);

        Assert.AreEqual(afterApply, native.Calls.Count, "Nothing must be unregistered off-thread.");

        // A second Dispose call, still off-thread, is a no-op: the first call never marked the instance
        // disposed (it threw before that), but there is nothing new to guard against here either.
        window.IsOwnedByCurrentThread = true;
        manager.Dispose();
        Assert.AreEqual(afterApply + 1, native.Calls.Count, "The on-thread Dispose still releases what was held.");
    }

    // IMessageWindow can tell the two apart: a destroyed window has no handle (NativeWindow.Handle is zero once
    // the handle is gone), while a window owned by another thread still has one. Against a destroyed window there
    // is nothing to pass to UnregisterHotKey, so Dispose logs that and finishes instead of throwing out of Close.
    [TestMethod]
    public void DisposeAfterTheWindowIsDestroyedLogsAndFinishesWithoutCallingWindows()
    {
        (HotkeyManager manager, FakeMessageWindow window, FakeNativeHotkeys native, CapturingLog log) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));
        int afterApply = native.Calls.Count;
        int raised = 0;
        manager.Activated += (_, _) => raised++;
        window.Handle = 0;
        window.IsOwnedByCurrentThread = false;

        manager.Dispose();

        Assert.AreEqual(afterApply, native.Calls.Count, "There is no window to unregister against.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "the window was already destroyed"), "The skipped release was not logged.");
        window.Raise(0x0312, ConnectionId, 0);
        Assert.AreEqual(0, raised, "Dispose did not unsubscribe from the window.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.Apply(EnabledWith()), "Dispose did not mark the instance disposed.");
        manager.Dispose();
    }

    [TestMethod]
    public void CurrentOutcomesIsEmptyBeforeApplyAndHoldsTheLastResult()
    {
        (HotkeyManager manager, _, _, _) = Build();

        Assert.IsEmpty(manager.CurrentOutcomes);

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));

        Assert.AreSame(outcomes, manager.CurrentOutcomes);
        Assert.AreEqual(HotkeyRegistrationState.Registered, manager.CurrentOutcomes.Single(o => o.Action == HotkeyAction.ToggleConnection).State);
    }

    [TestMethod]
    public void ApplyReportsTextRejectedWithoutCallingWindows()
    {
        (HotkeyManager manager, _, FakeNativeHotkeys native, _) = Build();
        HotkeySettings settings = EnabledWith(connect: "Ctrl");

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        HotkeyRegistrationOutcome outcome = outcomes.Single(o => o.Action == HotkeyAction.ToggleConnection);
        Assert.AreEqual(HotkeyRegistrationState.TextRejected, outcome.State);
        Assert.AreEqual(0, outcome.ErrorCode);
        Assert.AreEqual(HotkeyText.NoKeyMessage, outcome.Message);
        Assert.IsFalse(native.Calls.Any(c => c.Id == ConnectionId));
    }

    [TestMethod]
    public void NoEventAfterDispose()
    {
        (HotkeyManager manager, FakeMessageWindow window, _, _) = Build();
        manager.Apply(EnabledWith(connect: "Ctrl+Alt+C"));
        var raised = new List<HotkeyAction>();
        manager.Activated += (_, e) => raised.Add(e.Action);

        manager.Dispose();

        // The id is no longer held, and Dispose unsubscribed from MessageReceived first, so this proves
        // both: the fake still lets the test raise the message the window would no longer deliver.
        window.Raise(0x0312, ConnectionId, 0);
        Assert.IsEmpty(raised);
    }
}
