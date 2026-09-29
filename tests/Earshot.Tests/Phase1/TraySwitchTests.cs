using System.Text.RegularExpressions;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Tests.Hotkeys;
using Earshot.Tests.TestWindow;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The two shortcuts that state a direction, and the measured line every switch leaves. A switch to this PC is the
// connect and a switch to the phone is the disconnect, entering through the same route as the left click, so each
// guard the click has must hold for the shortcut too. The hotkey native calls are the fake ones: nothing here
// registers a real global hotkey, and no key is ever pressed.
[TestClass]
public sealed class TraySwitchTests
{
    private const int ToPcId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.SwitchToPc;
    private const int ToPhoneId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.SwitchToPhone;

    private static readonly bool[] ConnectThenDisconnect = [true, false];

    private static readonly bool[] ConnectTwice = [true, true];

    private static void Press(TrayHarness tray, HotkeyAction action)
    {
        tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(action));
        tray.PumpUntilIdle();
    }

    private static SessionEndingEventArgs SessionQuery() => new(isQuery: true, ending: true, flags: 0);

    // --- the direction is the one asked for, whatever the tray believed ---

    [TestMethod]
    public void SwitchToPcWhileDisconnectedConnects()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            Press(tray, HotkeyAction.SwitchToPc);

            Assert.HasCount(1, tray.Connection.Calls);
            Assert.IsTrue(tray.Connection.Calls[0].Connect);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Switch to-pc: active after 0 ms (trigger shortcut-to-pc,"), "The measured line was not written.");
            Assert.AreEqual(CardAnchor.NearTray, tray.Cards.Shown[^1].Anchor, "A shortcut has no click point, so its card goes near the tray.");
        });
    }

    // The tray thinks the AirPods are here already. The press still asks for the end state, and the controller,
    // which reads the endpoints, is the one that sends nothing: the tray never turns it into a disconnect.
    [TestMethod]
    public void SwitchToPcWhileTheTrayShowsConnectedStillAsksToConnectAndNeverDisconnects()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));

            Press(tray, HotkeyAction.SwitchToPc);

            Assert.HasCount(1, tray.Connection.Calls);
            Assert.IsTrue(tray.Connection.Calls[0].Connect, "A stated direction must not flip like a toggle.");
            Assert.AreEqual("Connected", tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void SwitchToPhoneWhileConnectedDisconnects()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));

            Press(tray, HotkeyAction.SwitchToPhone);

            Assert.HasCount(1, tray.Connection.Calls);
            Assert.IsFalse(tray.Connection.Calls[0].Connect);
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.StartsWith("Switch to-phone: released after 0 ms", StringComparison.Ordinal)), "The measured line was not written.");
            StringAssert.Contains(tray.Log.Entries.Last(e => e.Message.StartsWith("Switch to-phone:", StringComparison.Ordinal)).Message, "trigger shortcut-to-phone");
        });
    }

    // Pressed with the AirPods already elsewhere and the nodes enabled: nothing to release, and the machine is put at
    // rest now rather than after the idle wait.
    [TestMethod]
    public void SwitchToPhoneWithNodesEnabledAndRenderNotActiveBlocksAtOnce()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Allowed));

            Press(tray, HotkeyAction.SwitchToPhone);

            CollectionAssert.Contains(tray.Block.Calls, "block", "The press left the nodes enabled.");
            Assert.IsFalse(tray.Connection.Calls.Any(c => c.Connect), "A switch to the phone must never connect.");
        });
    }

    // --- the trigger each way in is named on the line ---

    [TestMethod]
    public void EachTriggerNamesItselfOnTheSwitchLine()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            tray.Context.OnIconMouseClick(null, new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0));
            tray.PumpUntilIdle();
            tray.Monitor.Raise(Target(ConnectionState.Disconnected));
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleConnection));
            tray.PumpUntilIdle();
            tray.Monitor.Raise(Target(ConnectionState.Disconnected));
            tray.ClickMenu("Connect");
            tray.PumpUntilIdle();

            string[] lines = tray.Log.Entries.Select(e => e.Message).Where(m => m.StartsWith("Switch to-pc:", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(3, lines);
            StringAssert.Contains(lines[0], "(trigger click,");
            StringAssert.Contains(lines[1], "(trigger shortcut-toggle,");
            StringAssert.Contains(lines[2], "(trigger menu,");
        });
    }

    // --- in flight ---

    [TestMethod]
    public void TheSameDirectionPressedTwiceInFlightIsIgnored()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            var connecting = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => connecting.Task;

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            TrayHarness.PumpUntil(() => tray.Connection.Calls.Count == 1, "The first press never reached the controller.");
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            System.Windows.Forms.Application.DoEvents();

            Assert.HasCount(1, tray.Connection.Calls, "A repeat of the direction in flight started another connect.");

            connecting.SetResult(new ConnectResult(ConnectOutcome.Confirmed, "Connected", []));
            tray.PumpUntilIdle();
            Assert.HasCount(1, tray.Connection.Calls);
        });
    }

    // The opposite direction replaces the one in flight even inside the double-click time (the harness clock stands
    // still, so both presses are at the same instant), and starts only once the first has ended and cleaned up.
    [TestMethod]
    public void TheOppositeDirectionSupersedesAndStartsOnlyAfterTheFirstHasEnded()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            bool firstEnded = false;
            bool secondStartedAfterFirstEnded = false;
            tray.Connection.OnConnect = token =>
            {
                var waiting = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() =>
                {
                    firstEnded = true;
                    waiting.TrySetCanceled(token);
                });
                return waiting.Task;
            };
            tray.Connection.OnDisconnect = _ =>
            {
                secondStartedAfterFirstEnded = firstEnded;
                return Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []));
            };

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            TrayHarness.PumpUntil(() => tray.Connection.Calls.Count == 1, "The first press never reached the controller.");
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPhone));
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(ConnectThenDisconnect, tray.Connection.Calls.Select(c => c.Connect).ToArray());
            Assert.IsTrue(secondStartedAfterFirstEnded, "The second direction started before the first had ended.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Switch to-pc: cancelled (trigger shortcut-to-pc,"));
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.StartsWith("Switch to-phone: released after", StringComparison.Ordinal)));
        });
    }

    // To this PC, to the phone, to this PC again while the switch to the phone waits for the connect to end: the last
    // press wins. It used to be dropped with only a debug line, so the PC ended up disconnected.
    [TestMethod]
    public void AThirdPressWhileTheSecondWaitsReplacesItAndTheLastPressWins()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            int connects = 0;
            tray.Connection.OnConnect = token =>
            {
                if (++connects > 1)
                {
                    return Task.FromResult(new ConnectResult(ConnectOutcome.Confirmed, "Connected", []));
                }

                var waiting = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => waiting.TrySetCanceled(token));
                return waiting.Task;
            };

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            TrayHarness.PumpUntil(() => tray.Connection.Calls.Count == 1, "The first press never reached the controller.");
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPhone));
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(ConnectTwice, tray.Connection.Calls.Select(c => c.Connect).ToArray(),
                "The last press was to this PC, so the PC is connected and no disconnect went out.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Shortcut: the switch to this PC replaces the one waiting."), "The replaced press is said in the log.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Switch to-pc: active after"), "The winning switch is measured.");
        });
    }

    // --- every guard the click has ---

    [TestMethod]
    public void SwitchToPcWhileTheSessionEndsIsRefusedWithACardAndSendsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));

            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Context.IsWorking);
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Connection.Calls, "A connect went out while the session was ending.");
            Assert.IsEmpty(tray.Block.Calls, "An allow went out while the session was ending.");
        });
    }

    // The session-end block is on the gate, so the coordinator is busy when the shortcut comes. The tray refuses
    // before it answers anything else: one card that says why, not "Finishing another change first." first.
    [TestMethod]
    public void SwitchToPcWhileTheSessionEndBlockRunsGetsOnlyTheRefusalCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Blocked));
            var blocking = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Block.OnBlock = _ => blocking.Task;
            tray.Block.Status = Phase1Fixtures.Block(BlockState.Allowed);
            tray.Coordinator.RefreshStatusAsync().GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            tray.Context.OnSessionEnding(null, SessionQuery());
            Assert.IsTrue(tray.Coordinator.IsBusy, "This test needs the session-end block in flight.");
            tray.Cards.Shown.Clear();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));

            CollectionAssert.AreEqual(new[] { BlockCoordinator.SessionEndingMessage }, tray.Cards.Statuses.ToArray());
            blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Connection.Calls);
        });
    }

    // A switch to the phone sends no allow, so it still runs while the session ends.
    [TestMethod]
    public void SwitchToPhoneWhileTheSessionEndsStillRunsAndSendsNoAllow()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));
            tray.Context.OnSessionEnding(null, SessionQuery());

            Press(tray, HotkeyAction.SwitchToPhone);

            Assert.IsTrue(tray.Connection.Calls.Any(c => !c.Connect), "The switch to the phone did not run.");
            Assert.IsFalse(tray.Connection.Calls.Any(c => c.Connect));
            CollectionAssert.DoesNotContain(tray.Block.Calls, "allow");
        });
    }

    [TestMethod]
    public void ASwitchWhileAMenuActionIsInFlightAnswersBusyAndIsNotQueued()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false);
            var applying = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Protection.OnApply = (_, _) => applying.Task;
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleAudioProtection));
            TrayHarness.PumpUntil(() => tray.Protection.Calls.Count == 1, "The protection change never started.");

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SwitchToPc));
            System.Windows.Forms.Application.DoEvents();

            Assert.AreEqual(TrayContext.BusyMessage, tray.Cards.Shown[^1].Content.Status);
            applying.SetResult(ControllerResult.Ok("Protected"));
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Connection.Calls, "The refused switch ran later.");
        });
    }

    [TestMethod]
    public void ASwitchWhileTheLinkIsAlreadyChangingIsIgnored()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connecting));

            Press(tray, HotkeyAction.SwitchToPc);
            Press(tray, HotkeyAction.SwitchToPhone);

            Assert.IsEmpty(tray.Connection.Calls);
        });
    }

    [TestMethod]
    public void NothingFiresFromASwitchWhileClosing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.Context.Menu.Strip.Items.Cast<System.Windows.Forms.ToolStripItem>().OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == MenuModel.Exit).PerformClick();
            tray.PumpUntilIdle();

            Press(tray, HotkeyAction.SwitchToPc);
            Press(tray, HotkeyAction.SwitchToPhone);

            Assert.IsEmpty(tray.Connection.Calls, "A switch reached the device after Earshot started closing.");
        });
    }

    [TestMethod]
    public void SafeModeSwitchesEndInNoDeviceAction()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(safeMode: true, snapshot: Target(ConnectionState.Disconnected));

            Press(tray, HotkeyAction.SwitchToPc);
            Press(tray, HotkeyAction.SwitchToPhone);

            Assert.IsEmpty(tray.Connection.Calls, "Safe mode must refuse the device action whatever the trigger.");
            Assert.IsEmpty(tray.Block.Calls);
        });
    }

    [TestMethod]
    public void ASwitchWithNoTargetSaysSoAndSendsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: NoDevice(), settings: s =>
            {
                s.PinnedContainerId = Guid.Empty;
                s.PinnedAddress = string.Empty;
            });

            Press(tray, HotkeyAction.SwitchToPc);

            Assert.IsEmpty(tray.Connection.Calls);
            Assert.AreEqual(TrayStatus.NotFoundMessage(tray.Settings.Current), tray.Cards.Shown[^1].Content.Status);
        });
    }

    // --- registration at start-up and the "taken" card ---

    [TestMethod]
    public void TheTwoDefaultShortcutsAreRegisteredAtStartUp()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness();
            tray.PumpUntilIdle();

            List<NativeCall> registered = tray.NativeHotkeys.Calls.Where(c => c.Method == "RegisterHotKey").ToList();
            Assert.HasCount(2, registered);
            Assert.AreEqual(ToPcId, registered[0].Id);
            Assert.AreEqual(0x41u, registered[0].VirtualKey);
            Assert.AreEqual(0x4007u, registered[0].Modifiers, "Ctrl, Alt and Shift, with no repeat.");
            Assert.AreEqual(ToPhoneId, registered[1].Id);
            Assert.AreEqual(0x44u, registered[1].VirtualKey);
            Assert.IsEmpty(tray.Cards.Shown, "Nothing failed, so no card.");
        });
    }

    // Another program holds Ctrl+Alt+Shift+A: the card says so in the owner's terms, the raw Windows error is in the
    // log, and the other shortcut is still set.
    [TestMethod]
    public void ADefaultShortcutAnotherProgramHoldsIsSaidOnTheCardAndLoggedWithTheRawError()
    {
        StaThread.Run(() =>
        {
            var native = new FakeNativeHotkeys();
            native.SetResult(ToPcId, NativeCallResult.Failure(1409));
            using var tray = new TrayHarness(nativeHotkeys: native);
            tray.PumpUntilIdle();

            Assert.AreEqual(
                "Ctrl+Alt+Shift+A is already in use by another program, so it was not set. Windows reported error 1409.",
                tray.Cards.Shown[^1].Content.Status);
            Assert.HasCount(1, tray.Cards.Shown, "One card, not one per shortcut.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Hotkey SwitchToPc: Ctrl+Alt+Shift+A is already in use by another program"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "RegisterHotKey id=0x4A04 modifiers=0x4007 vk=0x41 result=failed error=1409"), "The raw error was not logged.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "RegisterHotKey id=0x4A05 modifiers=0x4007 vk=0x44 result=ok"), "The other shortcut must still be set.");
        });
    }

    // The settings page reads the registration's outcomes when it refreshes. A save raises Changed before the shortcuts
    // are registered again, so the card has to be told when the result is in, or a taken chord shows as fine for up to
    // 30 s. The handler reads the outcome at the moment it is told, as the page does.
    [TestMethod]
    public void TheCardIsToldWhenTheRegistrationResultIsInSoATakenChordShowsAtOnce()
    {
        StaThread.Run(() =>
        {
            var native = new FakeNativeHotkeys();
            using var tray = new TrayHarness(nativeHotkeys: native);
            tray.PumpUntilIdle();
            var seen = new List<string?>();
            tray.Context.CardUpdateChanged += (_, _) =>
                seen.Add(tray.Context.HotkeyBindings(tray.Settings.Current.Hotkeys).FailureMessage(HotkeyAction.SwitchToPhone));
            native.SetResult(ToPhoneId, NativeCallResult.Failure(1409));

            tray.Settings.Update(s => s.Hotkeys.SetText(HotkeyAction.SwitchToPhone, "Ctrl+Alt+Shift+K"));
            tray.PumpUntilIdle();

            Assert.IsNotEmpty(seen, "The card was never told the registration had run.");
            Assert.IsNotNull(seen[^1], "The card was told before the taken chord was known.");
            StringAssert.Contains(seen[^1], "already in use");
        });
    }

    // The settings page binds to the tray's own registration: a chord another program holds shows as failed, and
    // clearing or changing a box edits the copy the page will save.
    [TestMethod]
    public void TheBindingModelReadsWhatTheRunningRegistrationDidWithEachChord()
    {
        StaThread.Run(() =>
        {
            var native = new FakeNativeHotkeys();
            native.SetResult(ToPcId, NativeCallResult.Failure(1409));
            using var tray = new TrayHarness(nativeHotkeys: native);
            tray.PumpUntilIdle();
            HotkeySettings editing = tray.Settings.Current.Hotkeys;

            HotkeyBindingModel model = tray.Context.HotkeyBindings(editing);

            Assert.AreEqual("Ctrl+Alt+Shift+A", model.Chord(HotkeyAction.SwitchToPc));
            Assert.IsTrue(model.RegistrationFailed(HotkeyAction.SwitchToPc));
            StringAssert.Contains(model.FailureMessage(HotkeyAction.SwitchToPc)!, "1409");
            Assert.IsFalse(model.RegistrationFailed(HotkeyAction.SwitchToPhone));

            model.Clear(HotkeyAction.SwitchToPc);
            Assert.IsNull(model.Set(HotkeyAction.SwitchToPhone, "Ctrl+Alt+Shift+P"));
            tray.Settings.Update(s =>
            {
                s.Hotkeys.SwitchToPc = editing.SwitchToPc;
                s.Hotkeys.SwitchToPhone = editing.SwitchToPhone;
            });
            tray.PumpUntilIdle();

            Assert.IsFalse(model.RegistrationFailed(HotkeyAction.SwitchToPc), "A cleared chord is not a failure once the settings are applied.");
            Assert.IsTrue(tray.NativeHotkeys.Calls.Any(c => c.Method == "RegisterHotKey" && c.Id == ToPhoneId && c.VirtualKey == 0x50));
        });
    }

    [TestMethod]
    public void AnErrorThatIsNotTheHeldOneIsAlsoOnTheCardAndInTheLogWithItsNumber()
    {
        StaThread.Run(() =>
        {
            var native = new FakeNativeHotkeys();
            native.SetResult(ToPhoneId, NativeCallResult.Failure(1400));
            using var tray = new TrayHarness(nativeHotkeys: native);
            tray.PumpUntilIdle();

            StringAssert.Contains(tray.Cards.Shown[^1].Content.Status, "Windows reported error 1400.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "error 1400"));
        });
    }

    // --- one route to the device ---

    [TestMethod]
    public void ThereIsExactlyOneCallOfTheCoordinatorsToggleInTheTray()
    {
        string app = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot", "App");
        int calls = 0;
        foreach (string file in Directory.EnumerateFiles(app, "TrayContext*.cs"))
        {
            calls += Regex.Count(File.ReadAllText(file), @"_coordinator\.ToggleAsync\(", RegexOptions.CultureInvariant);
        }

        Assert.AreEqual(1, calls, "A second route to the device: every shortcut must reach ToggleAsync through RunToggleAsync.");
    }
}
