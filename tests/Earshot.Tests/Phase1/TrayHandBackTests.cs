using System.Diagnostics;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Interop;
using Earshot.Streaming;
using Earshot.Tests.Streaming;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The pumped hold (HoldReply) on the real TrayContext, the setting-off branch, re-entry, the click and the
// menu item's own wiring. Drives the real TrayContext on an STA thread with fake controllers, exactly
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

    // A coordinator continuation posted to the UI thread runs while HoldReply waits, and HoldReply returns
    // once the task completes: proved by order, not a wall-clock window nothing but this machine's own speed
    // sets. "finished in" (the coordinator noticing the block complete) is logged strictly before "reply
    // returned after" (HoldReply's own pump giving control back), which a pump that returned early (never
    // really waiting for the coordinator's continuations) or one that polled blind to the deadline instead of
    // noticing completion could not produce in that order. The lower bound on elapsed time only rules out
    // returning before the real background delay on the block could possibly have finished; nothing here
    // fails because a machine ran slower, only if it returned suspiciously fast or never in order at all.
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

            int finishedAt = tray.Log.Entries.ToList().FindIndex(e => e.Message.Contains("finished in", StringComparison.Ordinal));
            int returnedAt = tray.Log.Entries.ToList().FindIndex(e => e.Message.Contains("reply returned after", StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, finishedAt);
            Assert.IsGreaterThanOrEqualTo(0, returnedAt);
            Assert.IsLessThan(returnedAt, finishedAt,
                "The coordinator must notice the block finish before HoldReply's own pump gives control back, " +
                "not the other way around.");
        });
    }

    // A block still running at the hand-back's own budget does not hang the window procedure: the hold
    // returns at the deadline, "cut short", and the block's own outcome is still recorded once it arrives later.
    // A zero budget proves nothing about the real race (it faults before any real waiting starts, on either
    // clock), so this drives a real, non-zero budget against the real clock (TimeProvider.System) with a step
    // that genuinely overruns it: a fake disconnect that never completes and never observes its own cancellation
    // token, so nothing but the shared deadline can end the wait. The call to OnSessionEnding is synchronous (it
    // returns only once HoldReply's pump has stopped), so asserting on the log immediately after it returns
    // proves the line was written before WndProc's own return, not by some later continuation that only runs if
    // the process happens to still be pumping messages.
    [TestMethod]
    public void HoldReplyCutsShortAtItsOwnBudgetRatherThanHangingTheCaller()
    {
        StaThread.Run(() =>
        {
            var budget = TimeSpan.FromMilliseconds(200);
            using var tray = new TrayHarness(
                snapshot: TargetRenderActive(),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                time: TimeProvider.System,
                handBackBudget: budget,
                // Larger than the overall budget on purpose: the disconnect's own sub-cap must not be what ends
                // this wait; only the shared deadline the pump also uses may.
                disconnectHandBackWait: TimeSpan.FromSeconds(30));

            // A disconnect that is sent and never comes back, and never reacts to its cancellation token: the
            // most direct proof that only the shared deadline, not a well-behaved callee, ends the wait.
            tray.Connection.OnDisconnect = _ => new TaskCompletionSource<ConnectResult>().Task;

            var clock = Stopwatch.StartNew();
            tray.Context.OnSessionEnding(null, WmEndSession());
            clock.Stop();

            Assert.IsGreaterThanOrEqualTo(budget, clock.Elapsed,
                "The reply returned before the hand-back's own budget passed.");
            Assert.IsLessThan(TimeSpan.FromSeconds(5), clock.Elapsed,
                "Generous upper bound for a slower machine: the reply must still return close to the budget, " +
                "not hang on toward the disconnect's own much longer sub-cap.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "reply returned after"),
                "HoldReply must return the window procedure, not hang it, once its own budget has passed.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "cut short"),
                "The line must already be in the log by the time this synchronous call returns.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "still running: disconnect"),
                "The step actually in flight (the disconnect) is the one named, not an assumed \"block\".");
        });
    }

    // The setting off leaves WM_ENDSESSION exactly as it was before this feature: HoldReply is never
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

            // TrayContext never calls HandBackAsync at all with the setting off, so HandBackText.Off's own
            // line, written from inside that method, never had a chance to fire for a session end; a reader must
            // still be able to tell "off" from "never reached" here the same way the suspend branch already lets
            // them.
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Hand-back: off, so nothing runs for this session end."));
        });
    }

    // A sleep hold has no session-ending flag of its own, and HandBackInProgress has already cleared again
    // by the time OnPowerChanged itself returns (the suspend handler only returns once the hold is over), so the
    // only way to prove a menu action and a hotkey are refused while the hold is actually up is reentrant, from
    // inside the hand-back's own block step: every device action started there is refused with the existing card
    // and starts nothing.
    [TestMethod]
    public void MenuActionsAndHotkeysDuringASleepHoldAreRefusedAndStartNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.HandBackOnShutdownAndSleep = true);

            bool ranReentrant = false;
            tray.Block.OnBlock = _ =>
            {
                Assert.IsTrue(tray.Coordinator.HandBackInProgress, "The reentrant check must run while the hold is still up.");
                int cardsBefore = tray.Cards.Shown.Count;
                int blockCallsBefore = tray.Block.Calls.Count;
                int protectionCallsBefore = tray.Protection.Calls.Count;

                tray.MenuItem(MenuModel.BlockAtBoot).PerformClick();
                tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleAudioProtection));

                Assert.AreEqual(cardsBefore + 2, tray.Cards.Shown.Count,
                    "Both the menu click and the hotkey must show the hand-back refusal card, and nothing else.");
                Assert.AreEqual(TrayContext.HandingBackMessage, tray.Cards.Shown[^1].Content.Status);
                Assert.AreEqual(TrayContext.HandingBackMessage, tray.Cards.Shown[^2].Content.Status);

                // Not just the card: nothing the refused click or hotkey would have sent actually reached the
                // gate. The count is taken before the reentrant calls, not asserted empty outright, because the
                // hand-back's own block call is already in progress (that is what invoked this callback).
                Assert.AreEqual(blockCallsBefore, tray.Block.Calls.Count, "Block at boot must not have sent an allow or a second block.");
                Assert.AreEqual(protectionCallsBefore, tray.Protection.Calls.Count, "The audio protection hotkey must not have sent anything.");

                ranReentrant = true;
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.Suspend));

            Assert.IsTrue(ranReentrant, "The reentrant block step never ran.");
        });
    }

    // With EARSHOT_SAFE_MODE=1 the hand-back logs and sends nothing: ServiceRegistry wraps Connection and
    // Block in the Safe* decorators before TrayContext or the coordinator ever see them, and those
    // decorators refuse every device-changing call without touching the real (here, fake) controller at
    // all, so the fake's own Calls list proves the real device action never ran.
    [TestMethod]
    public void SafeModeMakesTheHandBackLogAndSendNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                safeMode: true,
                snapshot: TargetRenderActive(),
                settings: s => s.HandBackOnShutdownAndSleep = true);
            tray.Connection.Calls.Clear();
            tray.Block.Calls.Clear();

            tray.Context.OnSessionEnding(null, WmEndSession());

            Assert.IsEmpty(tray.Connection.Calls, "Safe mode must never reach the real connection controller.");
            Assert.IsEmpty(tray.Block.Calls, "Safe mode must never reach the real block controller.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Safe mode: no device actions."));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "finished in"));
        });
    }

    // The session-ending guard set before the hand-back runs is still set afterwards (WM_ENDSESSION TRUE
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

    // The menu item toggles the saved setting.
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

    // Refused while the session ends, like every setting change; PerformClick runs the handler
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

    // A held streaming link is let go before the sleep hand-back's own disconnect, the same first step the
    // shut-down hand-back has.
    [TestMethod]
    public void SuspendLetsGoOfAStreamingLinkBeforeTheHandBack()
    {
        StaThread.Run(() =>
        {
            var fake = new FakeStreamingPlatform();
            fake.NextDiscovery(FakeStreamingPlatform.Found(new StreamingDevice("phone-1", "Test Phone", IPhoneContainer)));
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                settings: s =>
                {
                    s.HandBackOnShutdownAndSleep = true;
                    s.Streaming = s.Streaming with { Enabled = true };
                },
                streamingPlatform: fake);
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();
            ToolStripMenuItem play = tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == "Test Phone");
            Assert.IsTrue(play.Enabled, "Test Phone cannot be clicked.");
            play.PerformClick();
            tray.PumpUntilIdle();
            Assert.IsTrue(fake.CallsNamed("Open").Count > 0, "The link was never opened.");

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.Suspend));

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Play from a phone: letting go of any connection, because the machine is sleeping"));
            int letGoAt = tray.Log.Entries.ToList().FindIndex(e => e.Message.Contains("letting go of any connection, because the machine is sleeping", StringComparison.Ordinal));
            int startedAt = tray.Log.Entries.ToList().FindIndex(e => e.Message.StartsWith("Hand-back (sleep): started", StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, letGoAt);
            Assert.IsGreaterThanOrEqualTo(0, startedAt);
            Assert.IsLessThan(startedAt, letGoAt, "The streaming link is let go before the hand-back's own started line, as it is for shut down.");
        });
    }

    // The one execution of the real message path this feature keeps. A real ShellMessageWindow, on a
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
                "measured elapsed for the real WM_ENDSESSION SendMessage call, checked as a lower bound: " +
                "SendMessage must not return before the fake hand-back's own 200 ms delay has actually elapsed.");

            // WM_POWERBROADCAST PBT_APMSUSPEND: answered TRUE (1), again only once its own held fake hand-back
            // (150 ms) has finished.
            Assert.AreEqual(1, suspendResult);
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100), suspendElapsed,
                "measured elapsed for the real WM_POWERBROADCAST SendMessage call, checked as a lower bound: " +
                "SendMessage must not return before the fake hand-back's own 150 ms delay has actually elapsed.");

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

    // PBT_APMRESUMESUSPEND is logged and raised like the other two kinds; an unrecognised wParam falls
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
        Assert.IsTrue(log.Has(LogLevel.Info, "WM_POWERBROADCAST received: PBT_APMRESUMESUSPEND"),
            "The Windows constant name is what a reader (and the live-test harness) parses, not the enum member's own spelling.");

        raised = null;
        const int unknownWParam = 0x0006; // PBT_APMPOWERSTATUSCHANGE: not one of the three this window acts on.
        var unknown = Message.Create(window.Handle, NativeMethods.WM_POWERBROADCAST, (nint)unknownWParam, 0);
        window.Dispatch(ref unknown);

        Assert.IsNull(raised, "An unrecognised wParam must not raise PowerChanged.");
    }

    // Every other real-message test wires a fake handler directly to the window's own events, which proves
    // the pump primitive and the window's own message handling but never that TrayContext installs HoldReply
    // on that path at all. This drives a real WM_ENDSESSION into TrayContext's own real ShellMessageWindow
    // (TrayHarness builds the real TrayContext, so this is TrayContext.Window, not a window built for the
    // test), with the device layer faked, on a private desktop, through the real user32 SendMessageW, which
    // blocks the sender until the window procedure has actually returned.
    [TestMethod]
    public void ARealWmEndSessionToTheRealWindowRunsTheRealHandBackAndHoldsTheReply()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
            using var tray = new TrayHarness(
                snapshot: TargetRenderActive(),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                time: TimeProvider.System,
                handBackBudget: TimeSpan.FromSeconds(2),
                disconnectHandBackWait: TimeSpan.FromMilliseconds(500));
            tray.Block.OnBlock = _ => Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80));
                return ControllerResult.Ok("Blocked at boot");
            });

            nint hwnd = tray.Context.Window.Handle;

            nint endResult = 0;
            TimeSpan endElapsed = default;
            Exception? senderFailure = null;
            var sender = new Thread(() =>
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    endResult = Earshot.Tests.Phase5.TestWindows.Send(hwnd, NativeMethods.WM_ENDSESSION, 1, 0);
                    clock.Stop();
                    endElapsed = clock.Elapsed;
                }
                catch (Exception ex)
                {
                    senderFailure = ex;
                }
                finally
                {
                    tray.Ui.Post(_ => Application.ExitThread(), null);
                }
            })
            { IsBackground = true, Name = "Earshot end-to-end hand-back real message sender" };
            sender.Start();

            Application.Run();
            Assert.IsTrue(sender.Join(TimeSpan.FromSeconds(15)), "The sender thread did not finish.");
            if (senderFailure is not null)
            {
                throw new AssertFailedException("The sender thread threw.", senderFailure);
            }

            // The window procedure only returns once HoldReply's pump has finished, which is only once the
            // fake hand-back's own 80 ms block delay has actually elapsed: proof this ran through TrayContext's
            // real wiring, not a fake attached straight to the window.
            Assert.AreEqual(0, endResult);
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(70), endElapsed,
                "measured elapsed for the real WM_ENDSESSION SendMessage call, checked as a lower bound: " +
                "SendMessage must not return before the fake hand-back's own 80 ms block delay has actually elapsed.");

            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.StartsWith("Hand-back (shutdown): started", StringComparison.Ordinal)),
                "The real hand-back must actually have started.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "finished in"), "The real hand-back must actually have finished.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "reply returned after"), "HoldReply itself must have run.");
        });
    }

    public TestContext? TestContext { get; set; }
}
