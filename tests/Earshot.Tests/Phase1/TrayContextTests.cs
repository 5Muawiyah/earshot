using System.Diagnostics;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Earshot.Tray;
using Earshot.Tests.Hotkeys;
using Earshot.Tests.Integration.Coordinator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// Drives the real TrayContext on an STA thread with fake controllers, a recording card presenter, an
// in-memory startup registry and a settings file under %TEMP%. Nothing here reaches a device, Bluetooth,
// Task Scheduler or HKCU, and no icon is added to the notification area.
[TestClass]
public sealed class TrayContextTests
{
    private const string ProtectionFailedMessage = "Could not change audio protection.";
    private const string SetUpCall = "setup";
    private static readonly bool[] OnOffOn = [true, false, true];

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    private static ConnectResult Confirmed(string message) => new(ConnectOutcome.Confirmed, message, []);

    [TestMethod]
    public void RightMiddleAndSideButtonsNeverToggle()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            foreach (MouseButtons button in new[] { MouseButtons.Right, MouseButtons.Middle, MouseButtons.XButton1, MouseButtons.XButton2, MouseButtons.None })
            {
                tray.Context.OnIconMouseClick(null, Press(button));
            }

            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Connection.Calls);
            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void ARapidTripleClickConnectsOnce()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            var connect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => connect.Task;

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            Assert.HasCount(1, tray.Connection.Calls);
            Assert.IsTrue(tray.Connection.Calls[0].Connect);
            Assert.AreEqual(AirPodsContainer, tray.Connection.Calls[0].Container);
            Assert.IsTrue(tray.Context.IsBusy);
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "Click ignored: a connect or disconnect is already in flight."));

            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.MenuItem(MenuModel.Connect).Enabled, "The menu toggle is disabled while busy.");

            connect.SetResult(Confirmed("Connected"));
            tray.PumpUntilIdle();

            Assert.HasCount(1, tray.Connection.Calls);
            CollectionAssert.AreEqual(new[] { TrayStatus.CardConnecting, TrayStatus.CardConnected }, tray.Cards.Statuses);
            Assert.IsTrue(tray.Cards.Shown.All(s => s.Anchor == CardAnchor.NearCursor && s.Content.Title == AirPodsName));
        });
    }

    // A click after the double-click time, while the connect still runs, is a newer intent: the connect is
    // cancelled, and the disconnect runs only once the connect's clean-up has finished.
    [TestMethod]
    public void ALaterClickCancelsTheConnectAndDisconnectsOnceItsCleanUpHasFinished()
    {
        StaThread.Run(() =>
        {
            long now = 0;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), tickCount: () => now);
            bool connectEnded = false;
            bool disconnectStartedBeforeTheConnectEnded = false;
            tray.Connection.OnConnect = async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                }

                // Stands in for the clean-up a cancelled connect runs before it returns.
                await Task.Delay(30, CancellationToken.None);
                connectEnded = true;
                return new ConnectResult(ConnectOutcome.Failed, "Cancelled", []);
            };
            tray.Connection.OnDisconnect = _ =>
            {
                disconnectStartedBeforeTheConnectEnded = !connectEnded;
                return Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));
            };

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            now += 200;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            Assert.HasCount(1, tray.Connection.Calls, "A click within the double-click time is the same click.");

            now += 1000;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            now += 100;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { (true, AirPodsContainer), (false, AirPodsContainer) }, tray.Connection.Calls);
            Assert.IsFalse(disconnectStartedBeforeTheConnectEnded, "The disconnect started before the connect's clean-up had finished.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "connect (cancelled because a newer click asked to disconnect)"));
        });
    }

    // The connect in flight is waiting on a change that cannot be cancelled (a protect verb after it connected, say),
    // so the disconnect the later click asked for can start only minutes later. The click is answered at once.
    [TestMethod]
    public void ALaterClickWhileTheConnectWaitsOnAChangeIsAnsweredAtOnce()
    {
        StaThread.Run(() =>
        {
            long now = 0;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), tickCount: () => now);
            var protectVerb = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => protectVerb.Task;

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            now += 1000;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            Application.DoEvents();

            CardShown answer = tray.Cards.Shown[^1];
            Assert.AreEqual(TrayContext.FinishingFirstMessage, answer.Content.Status, "The click had no answer while the connect finished.");
            Assert.AreEqual(TrayHarness.ClickPoint, answer.ClickPoint);
            Assert.HasCount(1, tray.Connection.Calls, "The disconnect started before the connect ended.");

            protectVerb.SetResult(Confirmed("Connected"));
            tray.PumpUntilIdle();

            Assert.HasCount(2, tray.Connection.Calls);
            Assert.IsFalse(tray.Connection.Calls[1].Connect);
        });
    }

    // Open on startup is off and the Run value still starts a copy that is gone (uninstall removed %ProgramFiles%\Earshot,
    // which cannot reach this user's value): the value is removed at start. One that starts a file that is there is left.
    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    public void WithOpenOnStartupOffARunValueWhoseProgramIsGoneIsRemovedAtStart(bool programThere, int deletes)
    {
        StaThread.Run(() =>
        {
            const string Removed = @"C:\Program Files\Earshot\Earshot.exe";
            using var tray = new TrayHarness(
                settings: s => s.OpenOnStartup = false,
                startup: r => r.Run[StartupRegistration.ValueName] = StartupRegistration.CommandFor(Removed),
                fileExists: path => programThere && path == Removed);

            Assert.AreEqual(deletes, tray.Startup.Deletes);
            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.AreEqual(programThere, tray.Startup.Run.ContainsKey(StartupRegistration.ValueName));
        });
    }

    [TestMethod]
    public void ARunValueWhoseProgramIsGoneIsNotRemovedInSafeModeOrWhenTheSettingsCouldNotBeRead()
    {
        string stale = StartupRegistration.CommandFor(@"C:\Program Files\Earshot\Earshot.exe");
        StaThread.Run(() =>
        {
            using var safe = new TrayHarness(safeMode: true, settings: s => s.OpenOnStartup = false, startup: r => r.Run[StartupRegistration.ValueName] = stale);

            Assert.AreEqual(0, safe.Startup.Deletes);
            Assert.IsTrue(safe.Log.Has(LogLevel.Info, "which is no longer there, and was not removed"));
        });

        StaThread.Run(() =>
        {
            using var unread = new TrayHarness(
                settings: s => s.OpenOnStartup = false,
                startup: r => r.Run[StartupRegistration.ValueName] = stale,
                settingsStatus: SettingsLoadStatus.ReadFailed);

            Assert.AreEqual(0, unread.Startup.Deletes);
        });
    }

    // The coordinator cancels a connect itself when the session ends; the log says so rather than blaming a device
    // change.
    [TestMethod]
    public void AConnectCancelledByTheSessionEndIsLoggedWithThatReason()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.Allowed));
            // The connect's wait ends with the cancellation, as the real controller's does.
            tray.Connection.OnConnect = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ConnectResult(ConnectOutcome.Confirmed, "Connected", []);
            };

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "connect (cancelled because the session is ending)"));
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "another device was chosen"));
        });
    }

    [TestMethod]
    public void AConnectedTargetIsDisconnectedWithoutAConnectingCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.HasCount(1, tray.Connection.Calls);
            Assert.IsFalse(tray.Connection.Calls[0].Connect);
            CollectionAssert.AreEqual(new[] { TrayStatus.CardDisconnected }, tray.Cards.Statuses);
        });
    }

    [TestMethod]
    public void AFailedConnectShowsTheControllerMessage()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut,
                "Still connecting. Check your AirPods.",
                [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { TrayStatus.CardConnecting, "Still connecting. Check your AirPods." }, tray.Cards.Statuses);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "connect: Failed. Still connecting. Check your AirPods."));
        });
    }

    [TestMethod]
    public void WithNothingPinnedAndNoDeviceAClickSaysNotFound()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s =>
            {
                s.PinnedContainerId = Guid.Empty;
                s.PinnedAddress = "";
            });

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Connection.Calls);
            CollectionAssert.AreEqual(new[] { TrayStatus.NotFoundMessage(tray.Settings.Current) }, tray.Cards.Statuses);
        });
    }

    [TestMethod]
    public void AClickWhileAMenuActionRunsIsRefusedAndCancelsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false);
            var apply = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelledDuringApply = false;
            tray.Protection.OnApply = async (_, ct) =>
            {
                ControllerResult result = await apply.Task;
                cancelledDuringApply = ct.IsCancellationRequested;
                return result;
            };

            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            Assert.IsTrue(tray.Context.IsBusy);

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            Assert.IsEmpty(tray.Connection.Calls, "No connect starts while the protection change runs.");
            Assert.AreEqual(TrayContext.BusyMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Click ignored: a menu action is still in flight."));

            apply.SetResult(ControllerResult.Ok("Protected"));
            tray.PumpUntilIdle();

            Assert.IsFalse(cancelledDuringApply, "The click cancelled the protection change.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "protect-on: cancelled"));

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.HasCount(1, tray.Connection.Calls, "Once the change has finished, a click connects.");
        });
    }

    // The coordinator's own work here is the idle block: the AirPods stopped being used, the grace window ran out
    // and the block is on the gate. Nothing the tray started is in flight, so only the coordinator is busy.
    [TestMethod]
    public void AClickWhileTheCoordinatorFinishesItsOwnWorkSaysSoAtOnceAndGoesAheadAfterIt()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.Blocked));
            var blocking = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Block.OnBlock = _ => blocking.Task;

            // The nodes turn up enabled with the AirPods not in use, and stay so for the grace window.
            tray.Block.Status = Block(BlockState.Allowed);
            tray.Coordinator.RefreshStatusAsync().GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Coordinator.IdleWaitRunning, "This test needs the idle wait.");
            tray.Time.Advance(BlockCoordinator.IdleGrace);
            TrayHarness.PumpUntil(() => tray.Block.Calls.Contains("block"), "The idle block did not start.");
            Assert.IsTrue(tray.Coordinator.IsBusy);
            Assert.IsFalse(tray.Context.IsBusy);

            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.MenuItem(MenuModel.Connect).Enabled, "The menu toggle is enabled while the coordinator is busy.");

            // Asserted with no pump in between: the answer to the click is synchronous.
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            Assert.AreEqual(TrayContext.FinishingFirstMessage, tray.Cards.Shown[^1].Content.Status, "The click had no answer.");
            Assert.IsEmpty(tray.Connection.Calls, "The connect ran beside the coordinator's own work.");

            blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
            tray.PumpUntilIdle();

            Assert.HasCount(1, tray.Connection.Calls, "Once that work has finished, the click goes ahead.");
            Assert.IsTrue(tray.Connection.Calls[0].Connect);
        });
    }

    private static SessionEndingEventArgs SessionQuery() => new(isQuery: true, ending: true, flags: 0);

    private static SessionEndingEventArgs SessionEndCancelled() => new(isQuery: false, ending: false, flags: 0);

    // The resting state (nodes blocked, AirPods not connected), then WM_QUERYENDSESSION, then a left click. No
    // block is sent for that session end, and the click must still not send the allow that would enable the nodes:
    // if the process is killed by the shutdown the nodes would stay enabled across the power cycle. Every assertion
    // up to the pump is made with no pump in between, since the refusal is synchronous.
    [TestMethod]
    public void AClickWhileTheSessionEndsIsRefusedWithACardAndSendsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());
            Assert.IsEmpty(tray.Block.Calls, "This test needs the session end that sends no block.");

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status, "The refused click had no card.");
            Assert.IsFalse(tray.Context.IsBusy, "The refusal raised the busy guard.");
            Assert.IsFalse(tray.Context.IsWorking, "The refusal left something in flight.");
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Block.Calls, "An allow went out while the session was ending.");
            Assert.IsEmpty(tray.Connection.Calls, "A connect went out while the session was ending.");
        });
    }

    // The same through the menu, for every command that changes the device, a setting the gate holds, or setup.
    [TestMethod]
    public void TheMenuCommandsAreRefusedWhileTheSessionEndsAndNothingIsSavedOrSent()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false,
                arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());

            foreach (string command in new[] { MenuModel.Connect, MenuModel.ProtectAudioQuality, MenuModel.BlockAtBoot })
            {
                tray.Cards.Shown.Clear();
                tray.ClickMenu(command);

                Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status, command + " had no refusal card.");
                Assert.IsFalse(tray.Context.IsWorking, command + " left something in flight.");
            }

            tray.Cards.Shown.Clear();
            tray.Context.ApplyDeviceChoiceAsync(new PickerChoice("Other", "Other", "AABBCCDDEEFF", Guid.NewGuid()), CardPlace.NearTray).GetAwaiter().GetResult();
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status, "Choose device had no refusal card.");

            tray.PumpUntilIdle();
            Assert.IsFalse(tray.Settings.Current.ProtectAudioQuality, "The refused protection change was saved first.");
            Assert.AreEqual(AirPodsAddress, tray.Settings.Current.PinnedAddress, "The refused device change was saved.");
            Assert.IsEmpty(tray.Protection.Calls);
            Assert.IsEmpty(tray.Block.Calls);
            Assert.IsEmpty(tray.Connection.Calls);
        });
    }

    // Before setup the chosen device is only saved: no operation runs, so nothing but the tray's own check stands
    // between a choice made while the session ends and the settings file.
    [TestMethod]
    public void ADeviceChosenBeforeSetupWhileTheSessionEndsIsNotSaved()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.NotSetUp));
            tray.Context.OnSessionEnding(null, SessionQuery());

            tray.Context.ApplyDeviceChoiceAsync(new PickerChoice("Other", "Other", "AABBCCDDEEFF", Guid.NewGuid()), CardPlace.NearTray).GetAwaiter().GetResult();
            tray.PumpUntilIdle();

            Assert.AreEqual(AirPodsAddress, tray.Settings.Current.PinnedAddress, "The refused device change was saved.");
            Assert.AreEqual(AirPodsContainer, tray.Settings.Current.PinnedContainerId);
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // The session-end block is on the gate, so the coordinator is busy, when the click comes. The tray refuses
    // before it answers anything else: one card that says why, not "Finishing another change first." and then a
    // refusal.
    [TestMethod]
    public void AClickWhileTheSessionEndBlockRunsGetsOnlyTheRefusalCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.Blocked));
            var blocking = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Block.OnBlock = _ => blocking.Task;
            tray.Block.Status = Block(BlockState.Allowed);
            tray.Coordinator.RefreshStatusAsync().GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            tray.Context.OnSessionEnding(null, SessionQuery());
            Assert.IsTrue(tray.Coordinator.IsBusy, "This test needs the session-end block in flight.");
            tray.Cards.Shown.Clear();

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            CollectionAssert.AreEqual(OnlyTheRefusal, tray.Cards.Statuses.ToArray());
            Assert.IsFalse(tray.Context.IsBusy, "The refused click raised the busy guard.");
            blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Connection.Calls);
        });
    }

    private static readonly string[] OnlyTheRefusal = [BlockCoordinator.SessionEndingMessage];

    // A protection change was already running when Windows started to end the session, and the coordinator stopped
    // it. That is not an error: the owner is told the change was stopped and why, and the log says the same.
    [TestMethod]
    public void AChangeStoppedByTheSessionEndGetsTheSessionCardNotTheErrorCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false);
            var apply = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Protection.OnApply = (_, _) => apply.Task;
            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            TrayHarness.PumpUntil(() => tray.Protection.Calls.Count == 1, "The protection change did not start.");

            tray.Context.OnSessionEnding(null, SessionQuery());
            apply.SetResult(ControllerResult.Ok("Protected"));
            tray.PumpUntilIdle();

            Assert.AreEqual(BlockCoordinator.SessionEndStoppedMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Cards.Statuses.Contains(TrayContext.SomethingWentWrongMessage), "A change stopped for the session end was shown as an error.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Error, "protect-on"), "A change stopped for the session end was logged as an unexpected error.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "protect-on: stopped because the session is ending."));
        });
    }

    // The same, with the session end cancelled again before the protect verb returns. The card must not depend on
    // how the flag stands at that moment: it is still not an error.
    [TestMethod]
    public void AChangeStoppedByASessionEndThatIsThenCancelledStillGetsNoErrorCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false);
            var apply = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Protection.OnApply = (_, _) => apply.Task;
            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            TrayHarness.PumpUntil(() => tray.Protection.Calls.Count == 1, "The protection change did not start.");

            tray.Context.OnSessionEnding(null, SessionQuery());
            tray.Context.OnSessionEnding(null, SessionEndCancelled());
            apply.SetResult(ControllerResult.Ok("Protected"));
            tray.PumpUntilIdle();

            Assert.AreEqual(BlockCoordinator.SessionEndStoppedMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Cards.Statuses.Contains(TrayContext.SomethingWentWrongMessage));
            Assert.IsFalse(tray.Log.Has(LogLevel.Error, "protect-on"));
        });
    }

    // WM_CLOSE on the hidden window is a request to close Earshot (the Restart Manager sends it after the
    // end-session messages). It is taken as Exit: the block before closing runs and the message loop ends. It is
    // not left to DefWindowProc, which "calls the DestroyWindow function to destroy the window" and would leave
    // Earshot running with no window to hear a shutdown or a cancelled one.
    // https://learn.microsoft.com/windows/win32/winmsg/wm-close
    // https://learn.microsoft.com/windows/win32/rstmgr/guidelines-for-applications
    [TestMethod]
    public void WmCloseOnTheHiddenWindowIsExitNotTheEndOfTheWindow()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), exitWaitLimit: TimeSpan.FromSeconds(5),
                arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Block.Status = Block(BlockState.Allowed);
            tray.Coordinator.RefreshStatusAsync().GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            nint handleAfter = 0;
            bool timedOut = false;
            tray.Ui.Post(_ =>
            {
                var close = Message.Create(tray.Context.Window.Handle, 0x0010, 0, 0);
                tray.Context.Window.Dispatch(ref close);
                handleAfter = tray.Context.Window.Handle;
            }, null);
            using var giveUp = new System.Windows.Forms.Timer { Interval = 5000 };
            giveUp.Tick += (_, _) =>
            {
                timedOut = true;
                Application.ExitThread();
            };
            giveUp.Start();

            Application.Run(tray.Context);

            Assert.AreNotEqual((nint)0, handleAfter, "WM_CLOSE destroyed the hidden window.");
            Assert.IsFalse(timedOut, "WM_CLOSE did not begin Exit: the message loop was still running five seconds later.");
            Assert.IsTrue(tray.Block.Calls.Contains("block"), "The block before closing did not run.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "WM_CLOSE received"));
        });
    }

    // WM_ENDSESSION with wParam FALSE after refusals by click and by menu: the tray is fully usable again with no
    // restart. Nothing is busy, the menu toggle is enabled, and the next click connects.
    [TestMethod]
    public void AfterACancelledSessionEndTheTrayIsUsableAgainAndTheNextClickConnects()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false,
                arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();

            tray.Context.OnSessionEnding(null, SessionEndCancelled());
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Context.IsBusy);
            Assert.IsFalse(tray.Coordinator.IsBusy);
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem(MenuModel.Connect).Enabled, "The menu toggle stayed disabled after the session end was cancelled.");

            tray.Cards.Shown.Clear();
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.HasCount(1, tray.Connection.Calls, "The click after the cancelled session end did not connect.");
            Assert.IsTrue(tray.Connection.Calls[0].Connect);
            Assert.IsFalse(tray.Cards.Statuses.Contains(TrayContext.BusyMessage), "The tray still said another change was running.");
            Assert.IsFalse(tray.Cards.Statuses.Contains(BlockCoordinator.SessionEndingMessage));
        });
    }

    // Exit while the session ends still completes inside the limits it already has: the message loop ends.
    [TestMethod]
    public void ExitWhileTheSessionEndsStillEndsTheMessageLoop()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), exitWaitLimit: TimeSpan.FromSeconds(5),
                arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleAudioProtection));
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);
            var watch = Stopwatch.StartNew();

            Application.Run(tray.Context);

            Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed, "Exit waited out its limit behind something the session end left in flight.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Warn, "still in flight"), "Exit gave up on something left in flight.");
            Assert.IsTrue(tray.Coordinator.WhenIdleAsync().IsCompleted);
        });
    }

    [TestMethod]
    public void TheMicrophoneNoticeIsShownOnceAndRemembered()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), settings: s => s.ProtectAudioQuality = false);

            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Cards.Shown.Count(s => s.Content.Status == TrayStatus.MicrophoneNotice));
            CardShown notice = tray.Cards.Shown.Single(s => s.Content.Status == TrayStatus.MicrophoneNotice);
            Assert.AreEqual(CardAnchor.NearCursor, notice.Anchor);
            Assert.AreEqual(TrayHarness.ClickPoint, notice.ClickPoint, "The notice follows the click that asked for protection.");
            Assert.IsTrue(tray.Settings.Current.ProtectAudioNoticeShown);
            EarshotSettings onDisk = new JsonSettingsStore(tray.SettingsPath, new CapturingLog(), readOnly: true).Current;
            Assert.IsTrue(onDisk.ProtectAudioNoticeShown, "The latch was not saved.");
            Assert.IsTrue(onDisk.ProtectAudioQuality);

            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();
            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(OnOffOn, tray.Protection.Calls);
            Assert.AreEqual(1, tray.Cards.Shown.Count(s => s.Content.Status == TrayStatus.MicrophoneNotice), "The notice was shown again.");
        });
    }

    [TestMethod]
    public void AProtectionChangeThatDoesNotSucceedLeavesTheNoticeForLater()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.ProtectAudioQuality = false);
            tray.Protection.OnApply = (_, _) => Task.FromResult(new ControllerResult(
                OpStatus.Failed,
                ProtectionFailedMessage,
                [StepOutcomes.FromWin32("bluetooth-set-service-state:111E", 5)]));

            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { ProtectionFailedMessage }, tray.Cards.Statuses);
            Assert.IsFalse(tray.Settings.Current.ProtectAudioNoticeShown);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "ERROR_ACCESS_DENIED"));
        });
    }

    [TestMethod]
    public void InSafeModeNoControllerIsReachedAndEveryRefusalIsOnACard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(safeMode: true, snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false);

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();
            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();
            tray.ClickMenu(MenuModel.BlockAtBoot);
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Connection.Calls);
            Assert.IsEmpty(tray.Protection.Calls);
            Assert.IsEmpty(tray.Block.Calls);
            // Four refusals: the start-up check, the click, the protection change and Block at boot. No card
            // promises an action first.
            Assert.IsTrue(tray.Cards.Statuses.All(s => s == SafeDecorators.Message), string.Join(" | ", tray.Cards.Statuses));
            Assert.HasCount(4, tray.Cards.Shown);
            Assert.AreEqual(CardAnchor.NearTray, tray.Cards.Shown[0].Anchor, "The start-up block is not a click.");
            Assert.IsFalse(tray.Settings.Current.ProtectAudioNoticeShown);
            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.AreEqual(0, tray.Startup.Deletes);
        });
    }

    [TestMethod]
    public void AFirstRunInSafeModeWritesNoStartupValue()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(safeMode: true, firstRun: true, startup: _ => { });

            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.AreEqual(0, tray.Startup.Deletes);
            Assert.IsEmpty(tray.Startup.Run);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "First run in safe mode"));
        });
    }

    [TestMethod]
    public void AFirstRunTurnsOpenOnStartupOn()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(firstRun: true, startup: _ => { });

            Assert.AreEqual(1, tray.Startup.Writes);
            Assert.AreEqual(StartupRegistration.CommandFor(TrayHarness.ExePath), tray.Startup.Run[StartupRegistration.ValueName]);
            Assert.IsEmpty(tray.Cards.Shown);
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem(MenuModel.OpenOnStartup).Checked);
        });
    }

    [TestMethod]
    public void AStaleRunValueIsRepairedAtStart()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(startup: r => r.Run[StartupRegistration.ValueName] = "\"D:\\Unzipped\\Earshot\\Earshot.exe\" --startup");

            Assert.AreEqual(1, tray.Startup.Writes);
            Assert.AreEqual(StartupRegistration.CommandFor(TrayHarness.ExePath), tray.Startup.Run[StartupRegistration.ValueName]);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Writing it again."));
        });
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 0)]
    public void AMissingRunValueIsRepairedOnlyWhenTheSettingIsOn(bool openOnStartup, int writes)
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.OpenOnStartup = openOnStartup, startup: _ => { });

            Assert.AreEqual(writes, tray.Startup.Writes);
            Assert.AreEqual(0, tray.Startup.Deletes);
        });
    }

    [TestMethod]
    public void AStaleRunValueIsNotRepairedInSafeMode()
    {
        StaThread.Run(() =>
        {
            const string old = "\"D:\\Unzipped\\Earshot\\Earshot.exe\" --startup";
            using var tray = new TrayHarness(safeMode: true, startup: r => r.Run[StartupRegistration.ValueName] = old);

            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.AreEqual(old, tray.Startup.Run[StartupRegistration.ValueName]);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "was not repaired"));
        });
    }

    [TestMethod]
    public void AnUpToDateRunValueIsLeftAlone()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness();

            Assert.AreEqual(0, tray.Startup.Writes);
        });
    }

    [TestMethod]
    [DataRow(BlockState.NotSetUp, true, "setup")]
    [DataRow(BlockState.Allowed, true, "setboot-off")]
    [DataRow(BlockState.Blocked, false, "setboot-on")]
    public void BlockAtBootDecidesFromTheStatusReadAndNeverFromIsSetUp(BlockState state, bool blockAtBoot, string expected)
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Block(state, blockAtBoot));

            tray.ClickMenu(MenuModel.BlockAtBoot);
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { expected }, tray.Block.Calls);
            Assert.AreEqual(0, tray.Block.IsSetUpReads, "IsSetUp checks a scheduled task and must not be read on the UI thread.");
        });
    }

    // Setup copies Earshot to Program Files, which the scheduled tasks run and only administrators can change: Open
    // on startup starts that copy from then on, so deleting the download folder does not stop it.
    [TestMethod]
    public void SetUpPointsOpenOnStartupAtTheInstalledCopy()
    {
        StaThread.Run(() =>
        {
            const string Installed = @"C:\Program Files\Earshot Installed\Earshot.exe";
            bool installed = false;
            using var tray = new TrayHarness(
                arrange: t => t.Block.Status = Block(BlockState.NotSetUp),
                installedExePath: Installed,
                fileExists: path => installed && path == Installed);
            Assert.AreEqual(0, tray.Startup.Writes, "Before setup the Run value starts this copy.");

            installed = true;
            tray.ClickMenu(MenuModel.SetUpEarshot);
            tray.PumpUntilIdle();

            Assert.AreEqual(StartupRegistration.CommandFor(Installed), tray.Startup.Run[StartupRegistration.ValueName]);
            Assert.AreEqual(1, tray.Startup.Writes);
        });
    }

    [TestMethod]
    public void AtStartTheRunValueIsPointedAtAnInstalledCopy()
    {
        StaThread.Run(() =>
        {
            const string Installed = @"C:\Program Files\Earshot Installed\Earshot.exe";
            using var tray = new TrayHarness(installedExePath: Installed, fileExists: path => path == Installed);

            Assert.AreEqual(StartupRegistration.CommandFor(Installed), tray.Startup.Run[StartupRegistration.ValueName]);
        });
    }

    [TestMethod]
    public void SetUpFromTheMenuRunsSetUp()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Block(BlockState.NotSetUp));

            tray.ClickMenu(MenuModel.SetUpEarshot);
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { SetUpCall }, tray.Block.Calls);
            Assert.AreEqual(0, tray.Block.IsSetUpReads);
        });
    }

    [TestMethod]
    public void AnUnreadableBootBlockStatusIsLoggedWithItsCodeAndShown()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t =>
                t.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002)));

            tray.ClickMenu(MenuModel.BlockAtBoot);
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Block.Calls);
            CollectionAssert.AreEqual(new[] { TrayContext.BlockStatusUnreadableMessage }, tray.Cards.Statuses);
            Assert.IsTrue(tray.Log.Has(LogLevel.Error, "block-status failed ERROR_FILE_NOT_FOUND (0x80070002)"));
        });
    }

    [TestMethod]
    public void ASnapshotChangeReachesTheMenuLabel()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            Assert.IsTrue(tray.MenuItem(MenuModel.Connect).Available);

            tray.Monitor.Raise(Target(ConnectionState.Connected));
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem(MenuModel.Disconnect).Available);

            // Another device connected is not the pinned AirPods, so the click would connect them.
            tray.Monitor.Raise(Target(ConnectionState.Connected, IPhoneContainer, "iPhone"));
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem(MenuModel.Connect).Available);
        });
    }

    [TestMethod]
    public void TheStatusCardGoesThroughTheCardPresenter()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));

            tray.Context.ShowStatusCard();

            Assert.HasCount(1, tray.Cards.Shown);
            Assert.AreEqual(new CardContent(AirPodsName, TrayStatus.CardConnected), tray.Cards.Shown[0].Content);
            Assert.AreEqual(CardAnchor.NearCursor, tray.Cards.Shown[0].Anchor, "Starting a second copy is the user's own action.");
        });
    }

    [TestMethod]
    [DataRow(ConnectionState.Connecting)]
    [DataRow(ConnectionState.Disconnecting)]
    public void AClickIsIgnoredWhileTheLinkIsAlreadyChanging(ConnectionState connection)
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(connection));

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Connection.Calls, "The click acted while the menu item for it was disabled.");
            Assert.IsEmpty(tray.Cards.Shown);
            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.MenuItem(MenuModel.Connect).Enabled, "The label and the click must agree.");
        });
    }

    [TestMethod]
    public void TheCardsOfAConnectAreAnchoredAtTheClick()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Cards.Shown.All(c => c.Anchor == CardAnchor.NearCursor && c.ClickPoint == TrayHarness.ClickPoint),
                "A card that comes after the click must still land at the click.");
        });
    }

    [TestMethod]
    public void TheEndOfTheSessionReachesTheCoordinator()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), arrange: t => t.Block.Status = Block(BlockState.Allowed));
            bool raised = false;
            tray.Context.SessionEnding += (_, _) => raised = true;

            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
            tray.PumpUntilIdle();

            Assert.IsTrue(raised);
            CollectionAssert.Contains(tray.Block.Calls, "block");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Session ending: block queued at "));
        });
    }

    [TestMethod]
    public void TheRunValueIsLeftAloneWhenTheSettingsCouldNotBeRead()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                startup: _ => { },
                settingsStatus: SettingsLoadStatus.ReadFailed);

            Assert.AreEqual(0, tray.Startup.Writes, "Defaults in memory are not the user's choice.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Settings could not be read"));
        });
    }

    [TestMethod]
    public void TheRunValueIsLeftAloneWhenWindowsStartedEarshotFromIt()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                startup: r => r.Run[StartupRegistration.ValueName] = "\"D:\\Unzipped\\Earshot\\Earshot.exe\" --startup",
                startedAtLogon: true);

            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "started by its Run value"));
        });
    }

    [TestMethod]
    public void ARunAgainstATestDataFolderWritesNoStartupValue()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(firstRun: true, startup: _ => { }, dataRootRedirected: true);

            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.AreEqual(0, tray.Startup.Deletes);
            Assert.IsEmpty(tray.Startup.Run);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "First run against a test data folder"));
        });
    }

    [TestMethod]
    public void ExitFromTheMenuEndsTheMessageLoopAndHidesTheCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            Assert.AreEqual(1, tray.Cards.Hides);
            Assert.IsEmpty(tray.Cards.Shown, "Nothing was in flight and the nodes were blocked, so no closing card.");
            Assert.IsEmpty(tray.Block.Calls);
            Assert.AreEqual(0, tray.Startup.Writes);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Exit chosen from the tray menu."));

            tray.Context.ShowStatusCard();
            Assert.IsEmpty(tray.Cards.Shown, "No card after Exit.");
        });
    }

    [TestMethod]
    public void ExitCancelsAConnectInFlightAndWaitsForItToFinish()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            bool sawCancellation = false;
            bool finished = false;
            tray.Connection.OnConnect = async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    sawCancellation = true;
                }

                // Stands in for the clean-up a cancelled connect runs before it returns.
                await Task.Delay(50, CancellationToken.None);
                finished = true;
                return new ConnectResult(ConnectOutcome.Failed, "Cancelled", []);
            };

            tray.Ui.Post(
                _ =>
                {
                    tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                    tray.ClickMenu(MenuModel.Exit);
                },
                null);
            Application.Run(tray.Context);

            Assert.IsTrue(sawCancellation, "Exit did not cancel the connect.");
            Assert.IsTrue(finished, "The message loop ended before the cancelled connect finished.");
            Assert.AreEqual(1, tray.Cards.Hides);
            CardShown closing = tray.Cards.Shown.Single(c => c.Content.Status == TrayContext.ClosingMessage);
            Assert.AreEqual(CardAnchor.NearCursor, closing.Anchor, "The closing card follows the Exit click.");
            Assert.AreEqual(TrayHarness.ClickPoint, closing.ClickPoint);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Waiting for 1 action(s) (toggle)"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "connect (cancelled because Earshot is closing)"));
        });
    }

    // While Exit waits for a change in flight the icon is gone but this process still holds the single-instance lock,
    // so a second copy exits and asks this one to show its card. The card says Earshot is closing rather than leave
    // the start unanswered.
    [TestMethod]
    public void StartingEarshotAgainWhileItClosesSaysItIsClosing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            var release = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => release.Task;

            tray.Ui.Post(
                _ =>
                {
                    tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                    tray.ClickMenu(MenuModel.Exit);
                    tray.Context.ShowStatusCard();
                    release.SetResult(new ConnectResult(ConnectOutcome.Failed, "Cancelled", []));
                },
                null);
            Application.Run(tray.Context);

            CardShown[] closing = tray.Cards.Shown.Where(c => c.Content.Status == TrayContext.ClosingMessage).ToArray();
            Assert.HasCount(2, closing, "The start while closing got no card of its own.");
            Assert.AreEqual(CardAnchor.NearCursor, closing[1].Anchor);
            Assert.AreEqual(TrayStatus.AppName, closing[1].Content.Title);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Earshot was started again while it is closing"));
        });
    }

    [TestMethod]
    public void ExitBlocksEnabledNodesThatAreNotInUseBeforeClosing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Idle(1), arrange: t => t.Block.Status = Block(BlockState.Allowed));

            // The start-up check has blocked once already; this fake leaves the nodes enabled, as a block that
            // did not take would, so Exit is what must block them.
            int before = tray.Block.Calls.Count(c => c == "block");

            // A block that takes a moment, as the gate does, so the card for it is up while it runs.
            tray.Block.OnBlock = async _ =>
            {
                await Task.Delay(20, CancellationToken.None);
                return ControllerResult.Ok("Blocked at boot");
            };
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);
            Application.Run(tray.Context);

            Assert.AreEqual(before + 1, tray.Block.Calls.Count(c => c == "block"), "Exit left the nodes enabled.");
            CardShown card = tray.Cards.Shown.Single(c => c.Content.Status == TrayContext.BlockingBeforeClosingMessage);
            Assert.AreEqual(CardAnchor.NearCursor, card.Anchor);
            Assert.AreEqual(TrayHarness.ClickPoint, card.ClickPoint);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Blocking before Earshot closes"));
        });
    }

    [TestMethod]
    public void ExitWhoseCheckBeforeClosingBlocksNothingNeverSaysItIsBlocking()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Active(1), arrange: t => t.Block.Status = Block(BlockState.Allowed));

            // The check before closing reads the nodes on the system worker, so it is still in flight at the click.
            tray.Block.OnStatus = async _ =>
            {
                await Task.Delay(20, CancellationToken.None);
                return tray.Block.Status;
            };
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            Assert.IsEmpty(tray.Block.Calls, "The AirPods were in use, so nothing was blocked.");
            CollectionAssert.DoesNotContain(tray.Cards.Statuses, TrayContext.BlockingBeforeClosingMessage, "A card said the AirPods were being blocked when nothing was.");
            CollectionAssert.DoesNotContain(tray.Cards.Statuses, TrayContext.ClosingMessage);
            Assert.AreEqual(BlockCoordinator.ClosedWhileInUseMessage, tray.Cards.Shown.Single().Content.Status);
        });
    }

    [TestMethod]
    public void ExitWhileTheAirPodsAreInUseSaysSoAtTheClick()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Active(1), arrange: t => t.Block.Status = Block(BlockState.Allowed));
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Application.Run(tray.Context);

            Assert.IsEmpty(tray.Block.Calls, "The AirPods were in use, so nothing was blocked.");
            CardShown card = tray.Cards.Shown.Single(c => c.Content.Status == BlockCoordinator.ClosedWhileInUseMessage);
            Assert.AreEqual(CardAnchor.NearCursor, card.Anchor);
            Assert.AreEqual(TrayHarness.ClickPoint, card.ClickPoint);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Closing while the AirPods are in use"));
        });
    }

    [TestMethod]
    public void ADeviceTheGateRefusesIsNotPinnedByTheTray()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Block.OnSetDevice = _ => Task.FromResult(ControllerResult.Fail(RefusedDeviceMessage, [StepOutcomes.FromWin32("set-device", 1168)]));
            var choice = new PickerChoice("iPhone", "iPhone", IPhoneAddress, IPhoneContainer);

            RunToEnd(tray, tray.Context.ApplyDeviceChoiceAsync(choice, CardPlace.AtClick(TrayHarness.ClickPoint)));

            CollectionAssert.AreEqual(new[] { "set-device:" + IPhoneAddress }, tray.Block.Calls);
            Assert.AreEqual(AirPodsContainer, tray.Settings.Current.PinnedContainerId, "The tray pinned a device the gate refused.");
            Assert.AreEqual(AirPodsAddress, tray.Settings.Current.PinnedAddress);
            Assert.AreEqual(RefusedDeviceMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Device not changed to iPhone"));
        });
    }

    [TestMethod]
    public void ADeviceTheGateTakesIsPinnedByTheTrayAfterIt()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.Blocked));
            Guid pinnedWhenTheGateWasAsked = Guid.Empty;
            tray.Block.OnSetDevice = _ =>
            {
                pinnedWhenTheGateWasAsked = tray.Settings.Current.PinnedContainerId;
                return Task.FromResult(ControllerResult.Ok("Device chosen"));
            };
            var other = new Guid("0B8E5A51-7F0C-5F5E-9C6B-2D7C1E0F4A11");
            var choice = new PickerChoice("AirPods", "Other AirPods", "A1B2C3D4E5F6", other);

            RunToEnd(tray, tray.Context.ApplyDeviceChoiceAsync(choice, CardPlace.AtClick(TrayHarness.ClickPoint)));

            Assert.AreEqual(AirPodsContainer, pinnedWhenTheGateWasAsked, "The tray moved its pin before the gate took the device.");
            Assert.AreEqual(other, tray.Settings.Current.PinnedContainerId);
            Assert.AreEqual("A1B2C3D4E5F6", tray.Settings.Current.PinnedAddress);
        });
    }

    [TestMethod]
    public void BeforeSetUpADeviceIsPinnedWithoutTheGate()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Block(BlockState.NotSetUp));
            var other = new Guid("0B8E5A51-7F0C-5F5E-9C6B-2D7C1E0F4A11");
            var choice = new PickerChoice("AirPods", "Other AirPods", "A1B2C3D4E5F6", other);

            RunToEnd(tray, tray.Context.ApplyDeviceChoiceAsync(choice, CardPlace.AtClick(TrayHarness.ClickPoint)));

            Assert.IsEmpty(tray.Block.Calls);
            Assert.AreEqual(other, tray.Settings.Current.PinnedContainerId);
        });
    }

    [TestMethod]
    public void ExitStopsWaitingAfterItsLimit()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), exitWaitLimit: TimeSpan.FromMilliseconds(200));
            tray.Connection.OnConnect = _ => new TaskCompletionSource<ConnectResult>().Task;

            tray.Ui.Post(
                _ =>
                {
                    tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                    tray.ClickMenu(MenuModel.Exit);
                },
                null);
            var watch = Stopwatch.StartNew();
            Application.Run(tray.Context);

            Assert.IsLessThan(10.0, watch.Elapsed.TotalSeconds);
            Assert.AreEqual(1, tray.Context.PendingActions);
            Assert.AreEqual(1, tray.Cards.Hides);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "still in flight"));

            // Nothing is left to block the nodes the change in flight may leave enabled, so the card says so.
            CardShown notice = tray.Cards.Shown.Single(c => c.Content.Status == BlockCoordinator.ClosedBeforeChangeEndedMessage);
            Assert.AreEqual(TrayHarness.ClickPoint, notice.ClickPoint);
        });
    }

    // A connect whose allow is still running through the gate when Exit's first wait runs out. The coordinator ends
    // that work within its own limits, and the block its clean-up sends is what keeps the nodes disabled at rest.
    [TestMethod]
    public void ExitWaitsPastItsLimitForTheBlockAfterAGateAllowThatIsStillRunning()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                exitWaitLimit: TimeSpan.FromMilliseconds(100),
                coordinatorExitWaitLimit: TimeSpan.FromSeconds(10),
                arrange: t => t.Block.Status = Block(BlockState.Blocked));
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(ConnectOutcome.NodesBlocked, Earshot.Audio.Connect.ConnectMessages.AllowingFirst, []));
            tray.Block.OnAllow = async _ =>
            {
                await Task.Delay(500, CancellationToken.None);
                tray.Block.Status = Block(BlockState.Allowed);
                return ControllerResult.Ok("Allowed");
            };
            tray.Block.OnBlock = _ =>
            {
                tray.Block.Status = Block(BlockState.Blocked);
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };

            tray.Ui.Post(
                _ =>
                {
                    tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                    tray.ClickMenu(MenuModel.Exit);
                },
                null);
            Application.Run(tray.Context);

            CollectionAssert.AreEqual(AllowThenBlock, tray.Block.Calls, "Earshot closed before the clean-up blocked the nodes the allow enabled.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Still waiting for the block coordinator"));
            Assert.IsFalse(tray.Log.Has(LogLevel.Warn, "still in flight"));
            CollectionAssert.DoesNotContain(tray.Cards.Statuses, BlockCoordinator.ClosedBeforeChangeEndedMessage);
        });
    }

    private static readonly string[] AllowThenBlock = ["allow", "block"];

    private const string RefusedDeviceMessage = "That device cannot be blocked at boot.";

    // Pumps the tray until task is done, then rethrows anything it threw.
    private static void RunToEnd(TrayHarness tray, Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new AssertFailedException("The task did not finish within 10 seconds.");
            }

            Application.DoEvents();
            Thread.Sleep(1);
        }

        tray.PumpUntilIdle();
        task.GetAwaiter().GetResult();
    }
}

// One TrayContext with fakes, on the calling STA thread.
internal sealed class TrayHarness : IDisposable
{
    public const string ExePath = @"C:\Program Files\Earshot\Earshot.exe";

    // Where the tests pretend the pointer was when a click arrived.
    public static readonly System.Drawing.Point ClickPoint = new(1200, 1400);

    private readonly TempFolder _folder = new();

    public TrayHarness(
        bool safeMode = false,
        bool firstRun = false,
        DeviceSnapshot? snapshot = null,
        Action<EarshotSettings>? settings = null,
        Action<FakeStartupRegistry>? startup = null,
        TimeSpan? exitWaitLimit = null,
        Action<TrayHarness>? arrange = null,
        bool startedAtLogon = false,
        bool dataRootRedirected = false,
        SettingsLoadStatus? settingsStatus = null,
        Func<long>? tickCount = null,
        string? installedExePath = null,
        Func<string, bool>? fileExists = null,
        TimeSpan? coordinatorExitWaitLimit = null,
        FakeNativeHotkeys? nativeHotkeys = null,
        Earshot.Tests.Voice.FakeSpeechEngine? voiceEngine = null,
        Earshot.Tests.Streaming.FakeStreamingPlatform? streamingPlatform = null,
        TimeSpan? streamingShutdownWait = null,
        Queue<Earshot.Tests.Streaming.FakeStreamingPlatform>? streamingPlatforms = null,
        TimeSpan? handBackBudget = null,
        TimeSpan? disconnectHandBackWait = null,
        TimeProvider? time = null)
    {
        NativeHotkeys = nativeHotkeys ?? new FakeNativeHotkeys();
        // A fake, never a real SystemSpeechEngine: a TrayContext test must never construct a real
        // SpeechSynthesizer. Reused from tests\Earshot.Tests\Voice\FakeSpeechEngine.cs rather than a
        // second fake, the same way NativeHotkeys is shared with the Hotkeys suite.
        Voice = voiceEngine ?? new Earshot.Tests.Voice.FakeSpeechEngine();
        // A fake, never a real WindowsStreamingPlatform: a TrayContext test must never touch WinRT, read the real
        // device list or open a connection. Shared with the Streaming suite, as the two fakes above are with theirs.
        Streaming = streamingPlatform ?? new Earshot.Tests.Streaming.FakeStreamingPlatform();
        // An exception in a posted callback fails the test instead of opening the WinForms error dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
        Ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(Ui);

        Monitor.Current = snapshot ?? NoDevice();
        if (startup is null)
        {
            Startup.Run[StartupRegistration.ValueName] = StartupRegistration.CommandFor(ExePath);
        }
        else
        {
            startup(Startup);
        }

        SettingsPath = _folder.File("settings.json");
        Settings = new JsonSettingsStore(SettingsPath, Log);
        Settings.Update(s =>
        {
            s.PinnedContainerId = AirPodsContainer;
            s.PinnedAddress = AirPodsAddress;
            settings?.Invoke(s);
        });

        Registry = new ServiceRegistry(Log, Settings, action => Ui.Post(static state => ((Action)state!)(), action), safeMode)
        {
            Monitor = Monitor,
            Connection = Connection,
            Block = Block,
            Protection = Protection,
            Cards = Cards,
        };

        arrange?.Invoke(this);
        var options = new TrayStartOptions(firstRun, settingsStatus ?? Settings.LastLoadStatus, ExePath, Startup)
        {
            ShowIcon = false,
            ExitNoticeTime = TimeSpan.FromMilliseconds(10),
            CursorPosition = () => ClickPoint,
            StartedAtLogon = startedAtLogon,
            DataRootRedirected = dataRootRedirected,
            TickCount = tickCount ?? (() => 0),
            DoubleClickTime = TimeSpan.FromMilliseconds(500),
            InstalledExePath = installedExePath,
            FileExists = fileExists ?? (_ => false),
            // A fake, never the real User32Hotkeys: a TrayContext test must never register a real global
            // hotkey on the machine that runs it.
            NativeHotkeys = NativeHotkeys,
            VoiceEngineFactory = () => Voice,
            // One platform per switch-on when a test supplies a queue of them, so it can tell the first from the second;
            // otherwise the same one every time.
            StreamingPlatformFactory = _ =>
            {
                StreamingPlatformsBuilt++;
                return streamingPlatforms is { Count: > 0 } ? streamingPlatforms.Dequeue() : Streaming;
            },
            StreamingShutdownWait = streamingShutdownWait ?? TrayContext.DefaultStreamingShutdownWait,
            // The same clock as the coordinator's by default, so a hand-back test can drive both with one
            // Advance; a test that wants HoldReply to pump against real time can pass TimeProvider.System.
            Time = time ?? Time,
        };
        if (handBackBudget is { } hb)
        {
            options = options with { HandBackBudget = hb };
        }

        if (disconnectHandBackWait is { } dw)
        {
            options = options with { DisconnectHandBackWait = dw };
        }
        if (exitWaitLimit is { } limit)
        {
            // Unless a test asks for a longer wait for the coordinator, Exit gives up at the same limit.
            options = options with { ExitWaitLimit = limit, CoordinatorExitWaitLimit = coordinatorExitWaitLimit ?? limit };
        }
        else if (coordinatorExitWaitLimit is { } coordinatorLimit)
        {
            options = options with { CoordinatorExitWaitLimit = coordinatorLimit };
        }

        // The registry's controllers, so safe mode wraps what the coordinator calls, exactly as the tray does.
        // The same clock as TrayContext's own (options.Time, just below): production wires both the coordinator
        // and the tray to the one TimeProvider.System (Program.Tray.cs), so a test that asks for the real clock
        // must give it to both, or a deadline one of them computes against real time and the other against the
        // harness's own frozen ManualTime would never agree on when the budget has passed.
        Coordinator = new BlockCoordinator(Registry.Monitor, Registry.Connection, Registry.Block, Registry.Protection,
            Registry.Settings, Registry.Cards, Log, time ?? Time, new CoordinatorOptions(safeMode, StartedAtLogon: false));
        Context = new TrayContext(Registry, Coordinator, options);
        Coordinator.Start();
        PumpUntilIdle();
    }

    public WindowsFormsSynchronizationContext Ui { get; }

    public CapturingLog Log { get; } = new();

    public string SettingsPath { get; }

    public JsonSettingsStore Settings { get; }

    public FakeDeviceMonitor Monitor { get; } = new();

    public FakeConnectionController Connection { get; } = new();

    public FakeBlockController Block { get; } = new();

    public FakeAudioProtectionController Protection { get; } = new();

    public RecordingCards Cards { get; } = new();

    public FakeStartupRegistry Startup { get; } = new();

    public FakeNativeHotkeys NativeHotkeys { get; }

    public Earshot.Tests.Voice.FakeSpeechEngine Voice { get; }

    public Earshot.Tests.Streaming.FakeStreamingPlatform Streaming { get; }

    // How many times the tray asked for a streaming platform. None, while Play from a phone is off.
    public int StreamingPlatformsBuilt { get; private set; }

    public ServiceRegistry Registry { get; }

    public ManualTime Time { get; } = new();

    public BlockCoordinator Coordinator { get; }

    public TrayContext Context { get; }

    public ToolStripMenuItem MenuItem(string text) =>
        Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    // Refreshes the menu as opening it would, then clicks the item.
    public void ClickMenu(string text)
    {
        Context.Menu.Refresh();
        ToolStripMenuItem item = MenuItem(text);
        Assert.IsTrue(item.Available && item.Enabled, text + " cannot be clicked.");
        item.PerformClick();
    }

    // Runs posted callbacks until the tray has nothing in flight.
    public void PumpUntilIdle()
    {
        var watch = Stopwatch.StartNew();
        while (Context.IsWorking)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new AssertFailedException("The tray was still busy after 10 seconds.");
            }

            Application.DoEvents();
            Thread.Sleep(1);
        }

        Application.DoEvents();
    }

    // Runs posted callbacks until the condition holds, for work that leaves the tray busy on purpose (a stalled
    // gate call). Fails by name rather than asserting on a fixed number of pumps.
    public static void PumpUntil(Func<bool> condition, string otherwise)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new AssertFailedException(otherwise);
            }

            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Coordinator.Dispose();
        SynchronizationContext.SetSynchronizationContext(null);
        Ui.Dispose();
        _folder.Dispose();
    }
}

internal sealed class FakeDeviceMonitor : IDeviceMonitor
{
    public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged;

    public DeviceSnapshot Current { get; set; } = Phase1Fixtures.NoDevice();

    public void Start()
    {
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public bool WatchFailed => false;

    // Raised on the calling thread, which in these tests is the UI thread, as the contract requires.
    public void Raise(DeviceSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, new DeviceSnapshotEventArgs(snapshot));
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeConnectionController : IConnectionController
{
    public List<(bool Connect, Guid Container)> Calls { get; } = new();

    public Func<CancellationToken, Task<ConnectResult>> OnConnect { get; set; } =
        _ => Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Connected", []));

    public Func<CancellationToken, Task<ConnectResult>> OnDisconnect { get; set; } =
        _ => Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));

    public Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default)
    {
        Calls.Add((true, containerId));
        return OnConnect(ct);
    }

    public Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default)
    {
        Calls.Add((false, containerId));
        return OnDisconnect(ct);
    }
}

internal sealed class FakeBlockController : IBlockController
{
    public BootBlockStatus Status { get; set; } = Phase1Fixtures.Block(BlockState.Allowed);

    public Exception? StatusFailure { get; set; }

    public int IsSetUpReads { get; private set; }

    public List<string> Calls { get; } = new();

    public bool IsSetUp
    {
        get
        {
            IsSetUpReads++;
            return Status.State != BlockState.NotSetUp;
        }
    }

    // A status read that takes time, as it does on the system worker.
    public Func<CancellationToken, Task<BootBlockStatus>>? OnStatus { get; set; }

    public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default) =>
        StatusFailure is not null ? Task.FromException<BootBlockStatus>(StatusFailure)
        : OnStatus is { } read ? read(ct)
        : Task.FromResult(Status);

    public Func<CancellationToken, Task<ControllerResult>>? OnBlock { get; set; }

    public Task<ControllerResult> BlockAsync(CancellationToken ct = default)
    {
        if (OnBlock is null)
        {
            return Record("block");
        }

        Calls.Add("block");
        return OnBlock(ct);
    }

    public Func<CancellationToken, Task<ControllerResult>>? OnAllow { get; set; }

    public Task<ControllerResult> AllowAsync(CancellationToken ct = default)
    {
        if (OnAllow is null)
        {
            return Record("allow");
        }

        Calls.Add("allow");
        return OnAllow(ct);
    }

    // A stall hook, for a test that needs a genuine async gap (a burst of calls raised while one is still
    // in flight): the other fakes here resolve synchronously, so without this every call in a burst would
    // simply run to completion, one after another, before the next started, proving nothing about the
    // guard that keeps a burst to one call.
    public Func<bool, CancellationToken, Task<ControllerResult>>? OnSetBlockAtBoot { get; set; }

    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default)
    {
        Calls.Add(blockAtBoot ? "setboot-on" : "setboot-off");
        return OnSetBlockAtBoot is { } set ? set(blockAtBoot, ct) : Task.FromResult(ControllerResult.Ok("Block at boot is " + (blockAtBoot ? "on" : "off")));
    }

    public Func<CancellationToken, Task<ControllerResult>>? OnSetDevice { get; set; }

    public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default)
    {
        if (OnSetDevice is null)
        {
            return Record("set-device:" + address12);
        }

        Calls.Add("set-device:" + address12);
        return OnSetDevice(ct);
    }

    public Task<ControllerResult> RunSetupAsync(CancellationToken ct = default) => Record("setup");

    public Task<ControllerResult> UninstallAsync(CancellationToken ct = default) => Record("uninstall");

    private Task<ControllerResult> Record(string call)
    {
        Calls.Add(call);
        return Task.FromResult(ControllerResult.Ok("Done"));
    }
}

internal sealed class FakeAudioProtectionController : IAudioProtectionController
{
    public List<bool> Calls { get; } = new();

    public Func<bool, CancellationToken, Task<ControllerResult>> OnApply { get; set; } =
        (protect, _) => Task.FromResult(ControllerResult.Ok(protect ? "Protected" : "Not protected"));

    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new AudioProtectionSnapshot(AudioProtectionState.Unknown, HandsfreeInstalled: false, HeadsetInstalled: false));

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default)
    {
        Calls.Add(protect);
        return OnApply(protect, ct);
    }
}
