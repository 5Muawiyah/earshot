using System.Diagnostics;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// T5, T6 and the menu item's own wiring: the pumped hold (HoldReply) on the real TrayContext, the setting-off
// branch, re-entry, and the click. Drives the real TrayContext on an STA thread with fake controllers, exactly
// as TrayContextTests does. Nothing here reaches a device, a window message or Task Scheduler.
[TestClass]
public sealed class TrayHandBackTests
{
    private static readonly string[] ExpectedRealMessageOrder =
    [
        "session-ending hand-back started", "session-ending hand-back finished",
        "suspend hand-back started", "suspend hand-back finished",
    ];

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    private static SessionEndingEventArgs WmEndSession() => new(isQuery: false, ending: true, flags: 0);

    // T5: a coordinator continuation posted to the UI thread runs while HoldReply waits, and HoldReply returns
    // once the task completes -- proved by the wall-clock time actually taken: a real background delay on the
    // block, a generous budget and TimeProvider.System for the deadline, so a hold that returned early (the pump
    // not actually running the coordinator's own continuations) or hung to the deadline would both show up in
    // the elapsed time, not just in whether the test finished at all.
    [TestMethod]
    public void HoldReplyWaitsForTheRealHandBackTaskThenReturns()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                time: TimeProvider.System,
                handBackBudget: TimeSpan.FromSeconds(2),
                disconnectHandBackWait: TimeSpan.FromMilliseconds(500));
            tray.Block.OnBlock = _ => Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(60));
                return ControllerResult.Ok("Blocked at boot");
            });

            var clock = Stopwatch.StartNew();
            tray.Context.OnSessionEnding(null, WmEndSession());
            clock.Stop();

            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40), clock.Elapsed,
                "The reply returned before the hand-back's own block finished: the pump is not really waiting for it.");
            Assert.IsLessThan(TimeSpan.FromSeconds(1), clock.Elapsed,
                "The reply waited close to the whole budget: HoldReply is polling blindly to the deadline rather than " +
                "noticing the task finish through the pumped continuations.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "reply returned after"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "finished in"));
        });
    }

    // T4 / T5: a block still running at the hand-back's own budget does not hang the window procedure: the hold
    // returns at the deadline, "cut short", and the block's own outcome is still recorded once it arrives later.
    [TestMethod]
    public void HoldReplyCutsShortAtItsOwnBudgetRatherThanHangingTheCaller()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                handBackBudget: TimeSpan.Zero);

            // The harness's own start-up check may already have blocked the nodes through the default fake
            // behaviour; put them back to enabled for this test's own scenario, read fresh.
            tray.Block.Status = Block(BlockState.Allowed);
            tray.Coordinator.RefreshStatusAsync();
            tray.PumpUntilIdle();

            var pending = new TaskCompletionSource<ControllerResult>();
            tray.Block.OnBlock = _ => pending.Task;

            tray.Context.OnSessionEnding(null, WmEndSession());

            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "reply returned after"),
                "HoldReply must return the window procedure, not hang it, once its own budget has passed.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "cut short"));

            // Not abandoned: the block is a gate request already sent, so it still finishes and is still logged.
            pending.SetResult(ControllerResult.Ok("Blocked at boot"));
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "the block that was still running finished"));
        });
    }

    // T2: the setting off leaves WM_ENDSESSION exactly as it was before this feature: HoldReply is never
    // entered, and the process answers at once.
    [TestMethod]
    public void TheSettingOffNeverEntersTheHold()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.HandBackOnShutdownAndSleep = false);

            tray.Context.OnSessionEnding(null, WmEndSession());

            Assert.IsFalse(tray.Log.Has(LogLevel.Debug, "reply returned after"));
            Assert.IsFalse(tray.Log.Entries.Any(e => e.Message.StartsWith("Hand-back (shutdown): started", StringComparison.Ordinal)));
        });
    }

    // T6: the session-ending guard set before the hand-back runs is still set afterwards (WM_ENDSESSION TRUE
    // never clears it -- only a later cancellation does), so a click that would connect (the only kind the
    // coordinator itself refuses at a session end; a disconnect is still let through even now) is still refused
    // with the existing card once the hold has returned.
    [TestMethod]
    public void AClickAfterTheHoldReturnsIsStillRefusedBecauseTheSessionIsStillEnding()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                handBackBudget: TimeSpan.FromMilliseconds(50));

            tray.Context.OnSessionEnding(null, WmEndSession());

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // Section 7: the menu item toggles the saved setting.
    [TestMethod]
    public void ClickingTheMenuItemTogglesTheSetting()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.HandBackOnShutdownAndSleep = true);

            tray.ClickMenu(MenuModel.HandBackOnShutdownAndSleep);
            tray.PumpUntilIdle();
            Assert.IsFalse(tray.Registry.Settings.Current.HandBackOnShutdownAndSleep);

            tray.ClickMenu(MenuModel.HandBackOnShutdownAndSleep);
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Registry.Settings.Current.HandBackOnShutdownAndSleep);
        });
    }

    // Section 7: refused while the session ends, like every setting change; PerformClick runs the handler
    // regardless of the item's own Enabled state, so this proves the handler's own guard, not just the menu's.
    [TestMethod]
    public void TheMenuItemIsRefusedWhileTheSessionIsEnding()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.HandBackOnShutdownAndSleep = true);
            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));

            tray.MenuItem(MenuModel.HandBackOnShutdownAndSleep).PerformClick();
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Registry.Settings.Current.HandBackOnShutdownAndSleep, "Refused: nothing was saved.");
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // T11, the one execution of the real message path this feature keeps. A real ShellMessageWindow, on a
    // thread of its own attached to a private desktop, pumping real messages (Application.Run); a second,
    // ordinary thread sends WM_QUERYENDSESSION, a real WM_ENDSESSION (wParam TRUE), and a real WM_POWERBROADCAST
    // with PBT_APMSUSPEND, to it with the real user32 SendMessageW, which blocks the sender until the window
    // procedure has returned. Each handler is wired to a fake hand-back (never the real coordinator: this proves
    // the message plumbing and the hold, not the device logic already proved in HandBackTests) that finishes
    // only after a real delay, held through TrayContext's own pump primitive, so a regression that stopped
    // holding the reply, or moved the hold off the real window procedure, shows up as SendMessage returning early.
    [TestMethod]
    public void ARealMessagePathOnAPrivateDesktopHoldsTheReplyForTheFakeHandBack()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
            var sync = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(sync);
            var log = new CapturingLog();
            using var window = new ShellMessageWindow(log);
            nint hwnd = window.Handle;

            var order = new List<string>();
            window.SessionEnding += (_, e) =>
            {
                if (!e.IsQuery && e.Ending)
                {
                    order.Add("session-ending hand-back started");
                    // The fake hand-back: a real 200 ms delay on a pool thread, held through the exact primitive
                    // TrayContext.HoldReply uses, with a generous 5 s cap that this delay never approaches.
                    Task fake = Task.Run(async () => await Task.Delay(TimeSpan.FromMilliseconds(200)));
                    TrayContext.PumpUntilTaskOrDeadline(fake, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5), TimeProvider.System);
                    order.Add("session-ending hand-back finished");
                }
            };
            window.PowerChanged += (_, e) =>
            {
                if (e.Kind == PowerEventKind.Suspend)
                {
                    order.Add("suspend hand-back started");
                    Task fake = Task.Run(async () => await Task.Delay(TimeSpan.FromMilliseconds(150)));
                    TrayContext.PumpUntilTaskOrDeadline(fake, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5), TimeProvider.System);
                    order.Add("suspend hand-back finished");
                }
            };

            nint queryResult = 0, endResult = 0, suspendResult = 0;
            TimeSpan queryElapsed = default, endElapsed = default, suspendElapsed = default;
            Exception? senderFailure = null;
            var sender = new Thread(() =>
            {
                try
                {
                    var queryClock = Stopwatch.StartNew();
                    queryResult = Earshot.Tests.Phase5.TestWindows.Send(hwnd, NativeMethods.WM_QUERYENDSESSION, 0, 0);
                    queryClock.Stop();
                    queryElapsed = queryClock.Elapsed;

                    var endClock = Stopwatch.StartNew();
                    endResult = Earshot.Tests.Phase5.TestWindows.Send(hwnd, NativeMethods.WM_ENDSESSION, 1, 0);
                    endClock.Stop();
                    endElapsed = endClock.Elapsed;

                    var suspendClock = Stopwatch.StartNew();
                    suspendResult = Earshot.Tests.Phase5.TestWindows.Send(hwnd, NativeMethods.WM_POWERBROADCAST, (nint)NativeMethods.PBT_APMSUSPEND, 0);
                    suspendClock.Stop();
                    suspendElapsed = suspendClock.Elapsed;
                }
                catch (Exception ex)
                {
                    senderFailure = ex;
                }
                finally
                {
                    sync.Post(_ => Application.ExitThread(), null);
                }
            })
            { IsBackground = true, Name = "Earshot hand-back real message sender" };
            sender.Start();

            Application.Run();
            Assert.IsTrue(sender.Join(TimeSpan.FromSeconds(15)), "The sender thread did not finish.");
            if (senderFailure is not null)
            {
                throw new AssertFailedException("The sender thread threw.", senderFailure);
            }

            // WM_QUERYENDSESSION: answered TRUE (1) at once, no hold.
            Assert.AreEqual(1, queryResult);

            // WM_ENDSESSION TRUE: answered 0 (its return value carries no meaning; ShellMessageWindow always sets
            // it to 0), but only once the window procedure itself has returned, which happens only once the
            // pumped hold for the fake hand-back has finished.
            Assert.AreEqual(0, endResult);
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), endElapsed,
                "measured elapsed for the real WM_ENDSESSION SendMessage call, as an upper-bound check only: " +
                "SendMessage must not return before the fake hand-back's own 200 ms delay has actually elapsed.");

            // WM_POWERBROADCAST PBT_APMSUSPEND: answered TRUE (1), again only once its own held fake hand-back
            // (150 ms) has finished.
            Assert.AreEqual(1, suspendResult);
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100), suspendElapsed,
                "measured elapsed for the real WM_POWERBROADCAST SendMessage call, as an upper-bound check only.");

            CollectionAssert.AreEqual(
                ExpectedRealMessageOrder,
                order,
                "The handler ran in order and each hold finished before its SendMessage returned.");

            Assert.IsTrue(log.Has(LogLevel.Info, "WM_QUERYENDSESSION received"));
            Assert.IsTrue(log.Has(LogLevel.Info, "WM_ENDSESSION received"));
            Assert.IsTrue(log.Has(LogLevel.Info, "WM_POWERBROADCAST received"));

            TestContext?.WriteLine("WM_QUERYENDSESSION SendMessage elapsed: " + queryElapsed.TotalMilliseconds + " ms.");
            TestContext?.WriteLine("WM_ENDSESSION SendMessage elapsed: " + endElapsed.TotalMilliseconds + " ms.");
            TestContext?.WriteLine("WM_POWERBROADCAST SendMessage elapsed: " + suspendElapsed.TotalMilliseconds + " ms.");
        });
    }

    // T13: PBT_APMRESUMESUSPEND is logged and raised like the other two kinds; an unrecognised wParam falls
    // through to the base window procedure untouched (m.Result is never set by ShellMessageWindow for it).
    [TestMethod]
    public void ResumeSuspendIsRaisedAndAnUnknownWParamFallsThroughToTheBaseHandler()
    {
        var log = new CapturingLog();
        using var window = new ShellMessageWindow(log);
        PowerEventKind? raised = null;
        window.PowerChanged += (_, e) => raised = e.Kind;

        var resumeSuspend = Message.Create(window.Handle, NativeMethods.WM_POWERBROADCAST, (nint)NativeMethods.PBT_APMRESUMESUSPEND, 0);
        window.Dispatch(ref resumeSuspend);

        Assert.AreEqual(PowerEventKind.ResumeSuspend, raised);
        Assert.AreEqual(1, resumeSuspend.Result);
        Assert.IsTrue(log.Has(LogLevel.Info, "WM_POWERBROADCAST received: ResumeSuspend"));

        raised = null;
        const int unknownWParam = 0x0006; // PBT_APMPOWERSTATUSCHANGE: not one of the three this window acts on.
        var unknown = Message.Create(window.Handle, NativeMethods.WM_POWERBROADCAST, (nint)unknownWParam, 0);
        window.Dispatch(ref unknown);

        Assert.IsNull(raised, "An unrecognised wParam must not raise PowerChanged.");
    }

    public TestContext? TestContext { get; set; }
}
