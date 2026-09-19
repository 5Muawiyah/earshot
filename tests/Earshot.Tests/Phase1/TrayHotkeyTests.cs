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

    // Section 8 of the follow-up review: a registration problem is surfaced as one card summarising it,
    // not one card per shortcut, even when several shortcuts have a problem at once.
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

            Assert.AreEqual(1, tray.Cards.Shown.Count(c => c.Content.Status == TrayContext.HotkeyRegistrationProblemMessage),
                "A registration problem must show exactly one summarising card, not one per shortcut.");
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

            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == TrayContext.HotkeyRegistrationProblemMessage));
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
