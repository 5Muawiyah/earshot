using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

[TestClass]
public sealed class MenuModelTests
{
    private static MenuState Build(
        DeviceSnapshot? snapshot = null,
        BootBlockStatus? block = null,
        AudioProtectionSnapshot? protection = null,
        EarshotSettings? settings = null,
        bool busy = false,
        StartupState startup = StartupState.Off,
        bool safeMode = false) =>
        MenuModel.Build(snapshot ?? NoDevice(), block, protection, settings ?? Settings(), busy, startup, safeMode);

    [TestMethod]
    public void TheCopyIsExactlyAsDesigned()
    {
        // Literal strings on purpose: a change to the constants must fail here.
        MenuState state = Build(block: Block(BlockState.NotSetUp));

        Assert.AreEqual("Connect", state.Toggle.Text);
        Assert.AreEqual("Disconnect", Build(snapshot: Target(ConnectionState.Connected)).Toggle.Text);
        Assert.AreEqual("Block at boot", state.BlockAtBoot.Text);
        Assert.AreEqual("Hand back at shut down and sleep", state.HandBack.Text);
        Assert.AreEqual("Protect audio quality", state.ProtectAudio.Text);
        Assert.AreEqual("Turns off the AirPods microphone", state.ProtectCaveat.Text);
        Assert.AreEqual("Open on startup", state.OpenOnStartup.Text);
        Assert.AreEqual("Choose device...", state.ChooseDevice.Text);
        Assert.AreEqual("Set up Earshot...", state.SetUp.Text);
        Assert.AreEqual("Exit", state.Exit.Text);
    }

    // The bound shortcut text sits next to its command in the menu.
    [TestMethod]
    public void MenuLabelsShowTheirBoundShortcut()
    {
        EarshotSettings settings = Settings(s =>
        {
            s.Hotkeys.Enabled = true;
            s.Hotkeys.ToggleConnection = "Ctrl+Alt+C";
            s.Hotkeys.ToggleBlockAtBoot = "Ctrl+Alt+B";
            s.Hotkeys.ToggleAudioProtection = "not a shortcut";
        });

        MenuState state = Build(block: Block(BlockState.NotSetUp), settings: settings);

        Assert.AreEqual("Connect (Ctrl+Alt+C)", state.Toggle.Text);
        Assert.AreEqual("Block at boot (Ctrl+Alt+B)", state.BlockAtBoot.Text);

        // Unparsable text is not echoed into the menu: the label stays exactly as it is with no shortcut.
        Assert.AreEqual("Protect audio quality", state.ProtectAudio.Text);
    }

    [TestMethod]
    public void MenuLabelsAreUnchangedWithHotkeysOff()
    {
        EarshotSettings settings = Settings(s => s.Hotkeys.ToggleConnection = "Ctrl+Alt+C");

        MenuState state = Build(block: Block(BlockState.NotSetUp), settings: settings);

        Assert.AreEqual("Connect", state.Toggle.Text);
    }

    [TestMethod]
    public void NoTextHasAnEmDash()
    {
        MenuState state = Build(block: Block(BlockState.NotSetUp));
        MenuItemState[] items =
            [state.Toggle, state.BlockAtBoot, state.HandBack, state.ProtectAudio, state.ProtectCaveat,
             state.OpenOnStartup, state.ChooseDevice, state.SetUp, state.Exit];

        foreach (MenuItemState item in items)
        {
            Assert.IsFalse(item.Text.Contains((char)0x2014, StringComparison.Ordinal), item.Text);
        }
    }

    [TestMethod]
    public async Task WithTheNullServicesTheMenuOffersConnectAndSetUp()
    {
        BootBlockStatus block = await new NullBlockController().GetStatusAsync();
        AudioProtectionSnapshot protection = await new NullAudioProtectionController().GetStatusAsync();

        MenuState state = Build(new NullDeviceMonitor().Current, block, protection);

        Assert.AreEqual(MenuModel.Connect, state.Toggle.Text);
        Assert.IsTrue(state.Toggle.Enabled);
        Assert.IsFalse(state.BlockAtBoot.Checked, "Before setup nothing blocks at boot, so no check says it does.");
        Assert.IsFalse(state.BlockAtBoot.Indeterminate);
        Assert.IsTrue(state.BlockAtBoot.Enabled);
        Assert.IsTrue(state.ProtectAudio.Checked, "Protect audio quality defaults on.");
        Assert.IsFalse(state.ProtectAudio.Indeterminate, "An unknown read-back never overrides the intent.");
        Assert.IsTrue(state.SetUp.Visible);
        Assert.IsTrue(state.SetUp.Enabled);
        Assert.IsTrue(state.ChooseDevice.Enabled);
        Assert.IsTrue(state.Exit.Enabled);
    }

    [TestMethod]
    public void BeforeTheStatusIsReadBlockAtBootShowsNeitherStateAndSetUpIsHidden()
    {
        MenuState state = Build(block: null);

        Assert.IsFalse(state.BlockAtBoot.Checked);
        Assert.IsTrue(state.BlockAtBoot.Indeterminate, "Not known yet, so neither on nor off.");
        Assert.IsFalse(state.SetUp.Visible);
    }

    [TestMethod]
    [DataRow(ConnectionState.Unknown, "Connect", true)]
    [DataRow(ConnectionState.Disconnected, "Connect", true)]
    [DataRow(ConnectionState.Connecting, "Connect", false)]
    [DataRow(ConnectionState.Connected, "Disconnect", true)]
    [DataRow(ConnectionState.Disconnecting, "Connect", false)]
    public void TheToggleFollowsTheConnection(ConnectionState connection, string text, bool enabled)
    {
        MenuItemState toggle = Build(snapshot: Target(connection)).Toggle;

        Assert.AreEqual(text, toggle.Text);
        Assert.AreEqual(enabled, toggle.Enabled);
        Assert.IsTrue(toggle.Visible);
        Assert.IsFalse(toggle.Checked);
    }

    [TestMethod]
    public void AConnectedTargetThatIsNotThePinnedDeviceOffersConnect()
    {
        // The left click connects the pinned AirPods, so the menu must not offer to disconnect the iPhone.
        EarshotSettings pinned = Settings(s => s.PinnedContainerId = AirPodsContainer);
        DeviceSnapshot otherConnected = Target(ConnectionState.Connected, IPhoneContainer, "iPhone");

        MenuItemState toggle = Build(snapshot: otherConnected, settings: pinned).Toggle;
        ToggleIntent? intent = TrayStatus.Intent(otherConnected, pinned);

        Assert.AreEqual(MenuModel.Connect, toggle.Text);
        Assert.IsTrue(toggle.Enabled);
        Assert.IsNotNull(intent);
        Assert.IsTrue(intent.Connect);
    }

    [TestMethod]
    [DataRow(ConnectionState.Connected)]
    [DataRow(ConnectionState.Disconnected)]
    public void TheToggleLabelAndTheClickAlwaysAgree(ConnectionState connection)
    {
        EarshotSettings pinned = Settings(s => s.PinnedContainerId = AirPodsContainer);
        foreach (Guid container in new[] { AirPodsContainer, IPhoneContainer })
        {
            DeviceSnapshot snapshot = Target(connection, container);

            bool labelSaysConnect = Build(snapshot: snapshot, settings: pinned).Toggle.Text == MenuModel.Connect;

            Assert.AreEqual(TrayStatus.Intent(snapshot, pinned)!.Connect, labelSaysConnect, container + " " + connection);
        }
    }

    [TestMethod]
    public void BusyDisablesEveryActionButChooseDeviceAndExit()
    {
        MenuState state = Build(snapshot: Target(ConnectionState.Connected), block: Block(BlockState.NotSetUp), busy: true);

        Assert.IsFalse(state.Toggle.Enabled);
        Assert.IsFalse(state.BlockAtBoot.Enabled);
        Assert.IsFalse(state.HandBack.Enabled);
        Assert.IsFalse(state.ProtectAudio.Enabled);
        Assert.IsFalse(state.OpenOnStartup.Enabled);
        Assert.IsFalse(state.SetUp.Enabled);
        Assert.IsTrue(state.ChooseDevice.Enabled);
        Assert.IsTrue(state.Exit.Enabled);
    }

    [TestMethod]
    public void TheCaveatIsAlwaysVisibleAndNeverEnabled()
    {
        foreach (bool busy in new[] { false, true })
        {
            foreach (bool protect in new[] { false, true })
            {
                MenuItemState caveat = Build(settings: Settings(s => s.ProtectAudioQuality = protect), busy: busy).ProtectCaveat;
                Assert.IsTrue(caveat.Visible);
                Assert.IsFalse(caveat.Enabled);
                Assert.IsFalse(caveat.Checked);
            }
        }
    }

    [TestMethod]
    public void ABlockAtBootSettingThatCouldNotBeReadShowsNeitherState()
    {
        MenuState state = Build(block: Block(BlockState.Allowed, blockAtBoot: false) with { BlockAtBootKnown = false });

        Assert.IsFalse(state.BlockAtBoot.Checked);
        Assert.IsTrue(state.BlockAtBoot.Indeterminate, "A setting that was not read is shown as neither on nor off.");
        Assert.IsTrue(state.BlockAtBoot.Enabled, "Clicking it writes the setting again.");
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void BlockAtBootReflectsTheGateSetting(bool blockAtBoot, bool expected)
    {
        Assert.AreEqual(expected, Build(block: Block(BlockState.Allowed, blockAtBoot)).BlockAtBoot.Checked);
    }

    [TestMethod]
    [DataRow(BlockState.Allowed, false)]
    [DataRow(BlockState.Blocked, false)]
    [DataRow(BlockState.Mixed, false)]
    [DataRow(BlockState.Unknown, false)]
    [DataRow(BlockState.NotFound, false)]
    [DataRow(BlockState.NotSetUp, true)]
    public void SetUpIsShownOnlyWhenNotSetUp(BlockState blockState, bool visible)
    {
        Assert.AreEqual(visible, Build(block: Block(blockState)).SetUp.Visible);
    }

    [TestMethod]
    [DataRow((int)StartupState.Off, false)]
    [DataRow((int)StartupState.On, true)]
    [DataRow((int)StartupState.DisabledInWindows, false)]
    [DataRow((int)StartupState.Unknown, false)]
    public void OpenOnStartupIsCheckedOnlyWhenWindowsWillStartEarshot(int startup, bool expected)
    {
        MenuItemState item = Build(startup: (StartupState)startup).OpenOnStartup;

        Assert.AreEqual(expected, item.Checked);
        Assert.IsTrue(item.Enabled);
    }

    [TestMethod]
    [DataRow(true, AudioProtectionState.Unknown, false)]
    [DataRow(false, AudioProtectionState.Unknown, false)]
    [DataRow(true, AudioProtectionState.Protected, false)]
    [DataRow(false, AudioProtectionState.Protected, true)]
    [DataRow(true, AudioProtectionState.NotProtected, true)]
    [DataRow(false, AudioProtectionState.NotProtected, false)]
    [DataRow(true, AudioProtectionState.Partial, true)]
    [DataRow(false, AudioProtectionState.Partial, true)]
    public void ProtectAudioShowsTheIntentAndFlagsADisagreement(bool intent, AudioProtectionState read, bool indeterminate)
    {
        MenuItemState item = Build(protection: Protection(read), settings: Settings(s => s.ProtectAudioQuality = intent)).ProtectAudio;

        Assert.AreEqual(intent, item.Checked);
        Assert.AreEqual(indeterminate, item.Indeterminate);
    }

    [TestMethod]
    public void SafeModeAddsOneCaptionAndChangesNothingElse()
    {
        MenuState normal = Build(snapshot: Target(ConnectionState.Disconnected), block: Block(BlockState.NotSetUp));
        MenuState safe = Build(snapshot: Target(ConnectionState.Disconnected), block: Block(BlockState.NotSetUp), safeMode: true);

        Assert.IsFalse(normal.SafeMode.Visible);
        Assert.IsTrue(safe.SafeMode.Visible);
        Assert.AreEqual("Safe mode: no device actions", safe.SafeMode.Text);
        Assert.IsFalse(safe.SafeMode.Enabled, "The caption is a caption, not a command.");

        // Every action stays exactly as it is: each one reports its own refusal on a card.
        Assert.AreEqual(normal with { SafeMode = safe.SafeMode }, safe);
        Assert.IsTrue(safe.Toggle.Enabled);
        Assert.IsTrue(safe.BlockAtBoot.Enabled);
        Assert.IsTrue(safe.ProtectAudio.Enabled);
        Assert.IsTrue(safe.OpenOnStartup.Enabled);
        Assert.IsTrue(safe.SetUp.Enabled);
    }

    // The hand-back menu item: default on, and the check always follows the saved setting.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void HandBackFollowsTheSetting(bool handBack)
    {
        MenuItemState item = Build(settings: Settings(s => s.HandBackOnShutdownAndSleep = handBack)).HandBack;

        Assert.AreEqual(handBack, item.Checked);
        Assert.IsTrue(item.Visible);
    }

    [TestMethod]
    public void HandBackIsCheckedByDefault()
    {
        Assert.IsTrue(Build().HandBack.Checked);
    }

    [TestMethod]
    public void AMissingProtectionReadIsNotADisagreement()
    {
        Assert.IsFalse(MenuModel.ProtectionDisagrees(true, null));
        Assert.IsFalse(MenuModel.ProtectionDisagrees(false, null));
    }
}
