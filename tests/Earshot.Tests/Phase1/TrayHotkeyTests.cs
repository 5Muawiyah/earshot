using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Tray;
using Earshot.Tests.Hotkeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The tray's hotkey wiring: every action reaches the exact method its menu item or the left click
// already uses, safe mode and closing behave the same as for those, and Close releases what was held.
// TrayHarness injects a FakeNativeHotkeys (TrayStartOptions.NativeHotkeys), so nothing here registers a
// real global hotkey on the machine running the test.
[TestClass]
public sealed class TrayHotkeyTests
{
    [TestMethod]
    public void ToggleConnectionHotkeyUsesTheSameMethodAsTheLeftClick()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleConnection));
            tray.PumpUntilIdle();

            Assert.HasCount(1, tray.Connection.Calls);
            Assert.IsTrue(tray.Connection.Calls[0].Connect);
        });
    }

    [TestMethod]
    public void ToggleAudioProtectionHotkeyUsesTheSameMethodAsTheMenu()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.ProtectAudioQuality = false);

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleAudioProtection));
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.ProtectAudioQuality, "The hotkey did not go through the same protect-on path as the menu.");
        });
    }

    [TestMethod]
    public void NothingFiresFromAHotkeyWhileClosing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.Context.Menu.Strip.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.Exit).PerformClick();
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleConnection));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Connection.Calls, "A hotkey reached the device after Earshot started closing.");
        });
    }

    [TestMethod]
    public void SafeModeHotkeyEndsInNoDeviceAction()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(safeMode: true, snapshot: Target(ConnectionState.Disconnected));

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleConnection));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Connection.Calls, "Safe mode must refuse the device action even when the trigger is a hotkey.");
        });
    }

    [TestMethod]
    public void ABlockAtBootHotkeyBeforeSetupIsRefusedWithoutSetupOrAnElevationPrompt()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.NotSetUp));

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Block.Calls.Contains("setup"), "A hotkey must never reach RunSetupAsync, which shells out to an elevated install.");
            Assert.AreEqual(TrayContext.HotkeySetupNeededMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void ABlockAtBootHotkeyMayTurnItOnButNotOff()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Allowed, blockAtBoot: false));

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Block.Calls.Contains("setboot-on"), "Turning Block at boot on by hotkey must still work.");

            tray.Cards.Shown.Clear();
            tray.Block.Calls.Clear();
            tray.Block.Status = Phase1Fixtures.Block(BlockState.Allowed, blockAtBoot: true);
            tray.Coordinator.RefreshStatusAsync().GetAwaiter().GetResult();
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Block.Calls.Contains("setboot-off"), "Turning Block at boot off by hotkey must be refused.");
            Assert.AreEqual(TrayContext.BlockAtBootHotkeyOffRefusedMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // The busy guard lives in BlockAtBootAsync itself, claimed before any await, so it serves the menu and
    // the hotkey alike: a burst of activations queued back to back on the UI thread (a key held down, or
    // several hotkey messages already queued) must still start exactly one gate change.
    [TestMethod]
    public void ABurstOfBlockAtBootHotkeyActivationsStartsExactlyOneOperation()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Allowed, blockAtBoot: false));
            var settingBoot = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Block.OnSetBlockAtBoot = (_, _) => settingBoot.Task;

            for (int i = 0; i < 5; i++)
            {
                tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));
            }

            Assert.AreEqual(1, tray.Block.Calls.Count(c => c == "setboot-on"), "A burst of hotkey presses must start only one Block at boot change while the first is still in flight.");

            settingBoot.SetResult(ControllerResult.Ok("Block at boot is on"));
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Block.Calls.Count(c => c == "setboot-on"), "A burst of hotkey presses must start only one Block at boot change.");
        });
    }

    [TestMethod]
    public void ABurstOfProtectAudioHotkeyActivationsStartsExactlyOneOperation()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.ProtectAudioQuality = false);
            var applying = new TaskCompletionSource<ControllerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Protection.OnApply = (_, _) => applying.Task;

            for (int i = 0; i < 5; i++)
            {
                tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleAudioProtection));
            }

            Assert.AreEqual(1, tray.Protection.Calls.Count, "A burst of hotkey presses must start only one protection change while the first is still in flight.");

            applying.SetResult(ControllerResult.Ok("Protected"));
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Protection.Calls.Count, "A burst of hotkey presses must start only one protection change.");
        });
    }

    // Success, not only failure: a hotkey has no menu tick to see the result on, so it gets a card naming
    // the state it ended in.
    [TestMethod]
    public void ABlockAtBootHotkeySuccessShowsACardWithTheResultingState()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Allowed, blockAtBoot: false));

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));
            tray.PumpUntilIdle();

            Assert.AreEqual("Block at boot is on", tray.Cards.Shown[^1].Content.Status);
        });
    }

    private static readonly string[] OnlyDisconnected = ["Disconnected"];

    private static SessionEndingEventArgs SessionQuery() => new(isQuery: true, ending: true, flags: 0);

    // The resting state, then WM_QUERYENDSESSION, then the toggle shortcut. No block is sent for that session end
    // (the nodes already read blocked), and the shortcut must still not send the allow. Asserted with no pump
    // between the key press and the card: the refusal is synchronous.
    [TestMethod]
    public void AToggleHotkeyWhileTheSessionEndsIsRefusedWithACardAndSendsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleConnection));

            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status, "The refused shortcut had no card.");
            Assert.AreEqual(CardAnchor.NearTray, tray.Cards.Shown[^1].Anchor);
            Assert.IsFalse(tray.Context.IsWorking, "The refusal left something in flight.");
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Block.Calls, "An allow went out while the session was ending.");
            Assert.IsEmpty(tray.Connection.Calls, "A connect went out while the session was ending.");
        });
    }

    // The protect shortcut used to save the flipped setting before the operation that was then never run. The
    // refusal comes before any write, raises no busy guard, and leaves the next left click working once the
    // session end is cancelled.
    [TestMethod]
    public void AProtectHotkeyWhileTheSessionEndsSavesNothingSendsNothingAndLeavesTheTrayUsable()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.ProtectAudioQuality = false,
                arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Blocked));
            tray.Context.OnSessionEnding(null, SessionQuery());

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleAudioProtection));

            Assert.IsFalse(tray.Settings.Current.ProtectAudioQuality, "The refused change was saved first.");
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Context.IsBusy, "The refusal left the busy guard raised.");
            Assert.IsFalse(tray.Context.IsWorking);
            Assert.IsEmpty(tray.Protection.Calls);

            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
            tray.Context.OnIconMouseClick(null, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Cards.Statuses.Contains(TrayContext.BusyMessage), "The tray still said another change was running.");
            Assert.HasCount(1, tray.Connection.Calls, "The click after the cancelled session end did not connect.");
            Assert.IsFalse(tray.Settings.Current.ProtectAudioQuality);
            Assert.IsEmpty(tray.Protection.Calls.Where(on => on).ToArray(), "The refused protect-on ran later.");
        });
    }

    [TestMethod]
    public void ABlockAtBootHotkeyWhileTheSessionEndsIsRefusedBeforeAnyGateCall()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Allowed, blockAtBoot: false));
            tray.Context.OnSessionEnding(null, SessionQuery());

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));

            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Context.IsWorking);
            tray.PumpUntilIdle();
            Assert.IsEmpty(tray.Block.Calls);
        });
    }

    // With no status cached, Block at boot first reads it. The refusal comes before that: no read is started on the
    // system worker while the session ends, no busy guard is raised, and the owner is told why rather than that
    // the status could not be read. The later check in RunOperationAsync cannot do this; it is never reached.
    [TestMethod]
    public void ABlockAtBootHotkeyWithNoStatusCachedIsRefusedBeforeTheStatusIsRead()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(arrange: t => t.Block.StatusFailure = new IOException("no status"));
            Assert.IsNull(tray.Coordinator.BlockStatus, "This test needs no status cached.");
            int reads = 0;
            tray.Block.StatusFailure = null;
            tray.Block.OnStatus = _ =>
            {
                reads++;
                return Task.FromException<BootBlockStatus>(new IOException("no status"));
            };
            tray.Context.OnSessionEnding(null, SessionQuery());
            int before = reads;

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleBlockAtBoot));

            Assert.IsFalse(tray.Context.IsBusy, "The refused shortcut raised the busy guard.");
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
            tray.PumpUntilIdle();
            Assert.AreEqual(before, reads, "A status read was started for a change that is refused.");
            Assert.IsFalse(tray.Cards.Statuses.Contains(TrayContext.BlockStatusUnreadableMessage), "The owner was told the status could not be read, not why nothing was changed.");
        });
    }

    // The shortcut text comes from the settings file, so a control character in it never reaches the card.
    [TestMethod]
    public void TheRegistrationProblemCardCarriesNoControlCharacters()
    {
        var problem = new HotkeyRegistrationOutcome(HotkeyAction.ToggleConnection, "Ctrl+\r\nQ[31m", HotkeyRegistrationState.TextRejected, 0, "There is no key called \"\r\nQ\".");

        string card = TrayContext.HotkeyProblemCard([problem]);

        Assert.IsFalse(card.Any(char.IsControl), "A control character reached the card: " + string.Join(' ', card.Where(char.IsControl).Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))));
        StringAssert.Contains(card, "is not a shortcut.");
    }

    // A refused connect is neither a success nor a failed connect, so the voice says nothing for it. The status
    // shortcut afterwards is what proves the announcer was listening: its line is the only one spoken.
    [TestMethod]
    public void ARefusedConnectWhileTheSessionEndsIsNotAnnounced()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true },
                arrange: t => t.Block.Status = Phase1Fixtures.Block(BlockState.Blocked));
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Voice.IsOpen, "The announcer must have opened for this test to prove anything.");
            tray.Context.OnSessionEnding(null, SessionQuery());

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.ToggleConnection));
            tray.PumpUntilIdle();
            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            Assert.IsTrue(tray.Voice.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "SpeakCompleted never arrived.");

            CollectionAssert.AreEqual(OnlyDisconnected, tray.Voice.Utterances.ToArray());
        });
    }

    // A registration problem is surfaced as one card summarising it, not one card per shortcut, even when
    // several shortcuts have a problem at once.
    [TestMethod]
    public void ARegistrationProblemShowsOneSummarisingCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s =>
            {
                s.Hotkeys.Enabled = true;
                s.Hotkeys.ToggleConnection = "Ctrl+Alt+C";
                s.Hotkeys.ToggleAudioProtection = "Ctrl+Alt+C";    // duplicates ToggleConnection
                s.Hotkeys.ToggleBlockAtBoot = "not a shortcut";    // text rejected
            });
            tray.PumpUntilIdle();

            // The card names the first shortcut that failed and why, and says there are more.
            const string Expected = "Ctrl+Alt+C is already set for another command here." + TrayContext.HotkeyOthersNotSetSuffix;
            CollectionAssert.AreEqual(new[] { Expected }, tray.Cards.Statuses.ToArray(),
                "A registration problem must show exactly one summarising card, not one per shortcut.");
        });
    }

    // A shortcut that keeps failing is reported once, not again on every settings save; a save that changes
    // what is wrong is reported again.
    [TestMethod]
    public void TheRegistrationProblemCardIsShownAgainOnlyWhenTheProblemsChange()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s =>
            {
                s.Hotkeys.Enabled = true;
                s.Hotkeys.ToggleConnection = "Ctrl+Alt+C";
                s.Hotkeys.ToggleAudioProtection = "Ctrl+Alt+C";
            });
            tray.PumpUntilIdle();
            const string Duplicate = "Ctrl+Alt+C is already set for another command here.";
            CollectionAssert.AreEqual(new[] { Duplicate }, tray.Cards.Statuses.ToArray());

            // An unrelated save: the same shortcut still fails the same way.
            tray.Settings.Update(s => s.ProtectAudioNoticeShown = !s.ProtectAudioNoticeShown);
            tray.PumpUntilIdle();
            CollectionAssert.AreEqual(new[] { Duplicate }, tray.Cards.Statuses.ToArray(), "The same problem was shown again on an unrelated save.");

            // A different problem is news.
            tray.Settings.Update(s => s.Hotkeys.ToggleAudioProtection = "Ctrl+F12");
            tray.PumpUntilIdle();
            Assert.HasCount(2, tray.Cards.Shown);
            Assert.AreEqual("F12 is kept by Windows for the debugger, so it cannot be a shortcut.", tray.Cards.Shown[^1].Content.Status);

            // Fixed, then broken the same way again: that is news too.
            tray.Settings.Update(s => s.Hotkeys.ToggleAudioProtection = "");
            tray.PumpUntilIdle();
            tray.Settings.Update(s => s.Hotkeys.ToggleAudioProtection = "Ctrl+F12");
            tray.PumpUntilIdle();
            Assert.HasCount(3, tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void NoRegistrationProblemShowsNoCard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s =>
            {
                s.Hotkeys.Enabled = true;
                s.Hotkeys.ToggleConnection = "Ctrl+Alt+C";
            });
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void CloseUnregistersEverythingHeld()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s =>
            {
                s.Hotkeys.Enabled = true;
                s.Hotkeys.ToggleConnection = "Ctrl+Alt+C";
            });
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.NativeHotkeys.Calls.Any(c => c.Method == "RegisterHotKey"), "The shortcut was not registered at start-up.");

            tray.Context.Dispose();

            Assert.IsTrue(tray.NativeHotkeys.Calls.Any(c => c.Method == "UnregisterHotKey"), "Close must unregister what was held.");
        });
    }
}
