using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Streaming;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// Builds the real ContextMenuStrip on an STA thread without showing it.
[TestClass]
public sealed class TrayMenuTests
{
    // MenuModel.Build's voiceKnownMissing defaults to false (MenuModel.cs), so a caller that leaves it
    // out, exactly as State() below does, sees the ordinary "Speak status" label, never "(no voice)": a
    // caller has to say a voice is known missing, not the other way round.
    private static readonly string[] DesignedOrder =
    [
        "Connect", "-",
        "Block at boot", "Hand back at shut down and sleep", "Protect audio quality", "Turns off the AirPods microphone", "Open on startup",
        "Speak status", "-",
        "Show on the taskbar", "Left click connects straight away", "Card when the case opens", "Low battery alert", "Threshold", "Name your other device...", "-",
        "Choose device...", "Set up Earshot...", "-",
        "Exit",
    ];

    private static readonly string[] CommandOrder = ["toggle", "block", "handback", "protect", "startup", "device", "setup", "exit"];

    private static readonly string[] WidgetCommandOrder = ["taskbar", "leftclick", "caseopen", "lowbattery", "othername"];

    // The Play from a phone submenu as StreamingCoordinator builds it with one device in use.
    private static readonly StreamingMenuModel PlayingFromAPhone = new(
        StreamingLabels.Parent,
        ParentEnabled: true,
        [
            new StreamingMenuItem("Test Phone", Enabled: true, Checked: true, "phone-id", StreamingMenuCommand.Play),
            StreamingMenuModel.Sentence("Looking for devices."),
            new StreamingMenuItem("Stop playing from Test Phone", Enabled: true, Checked: false, "phone-id", StreamingMenuCommand.Stop),
            new StreamingMenuItem(StreamingLabels.Refresh, Enabled: true, Checked: false, null, StreamingMenuCommand.Refresh),
        ]);

    private static readonly string[] Commands = ["Play:phone-id", "Stop:phone-id", "Refresh:-"];

    private static MenuState State(
        DeviceSnapshot? snapshot = null,
        BootBlockStatus? block = null,
        bool busy = false,
        EarshotSettings? settings = null,
        AudioProtectionSnapshot? protection = null,
        bool safeMode = false,
        bool voiceKnownMissing = false) =>
        MenuModel.Build(snapshot ?? NoDevice(), block, protection, settings ?? Settings(), busy, StartupState.Off, safeMode, voiceKnownMissing);

    private static string[] AvailableTexts(TrayMenu menu) =>
        menu.Items.Where(i => i.Available).Select(i => i is ToolStripSeparator ? "-" : i.Text ?? "").ToArray();

    [TestMethod]
    public void TheMenuShowsTheDesignedItemsInOrder()
    {
        StaThread.Run(() =>
        {
            using var menu = new TrayMenu(() => State(block: Block(BlockState.NotSetUp)));

            CollectionAssert.AreEqual(DesignedOrder, AvailableTexts(menu));
        });
    }

    // Play from a phone is off by default, and then it is not in the menu at all: TheMenuShowsTheDesignedItemsInOrder
    // above is the same order it always was. Switched on, it sits under the toggle and changes nothing else.
    [TestMethod]
    public void PlayFromAPhoneIsAbsentByDefaultAndSitsUnderTheToggleWhenOn()
    {
        StaThread.Run(() =>
        {
            MenuState off = MenuModel.Build(NoDevice(), Block(BlockState.NotSetUp), null, Settings(), busy: false, StartupState.Off);
            Assert.IsFalse(off.PlayFromPhone.Visible);
            Assert.AreEqual(0, off.PlayFromPhoneItems.Count);

            MenuState on = MenuModel.Build(NoDevice(), Block(BlockState.NotSetUp), null, Settings(), busy: false, StartupState.Off, streaming: PlayingFromAPhone);
            using var menu = new TrayMenu(() => on);

            var expected = DesignedOrder.ToList();
            expected.Insert(1, "Play from a phone");
            CollectionAssert.AreEqual(expected, AvailableTexts(menu));
        });
    }

    [TestMethod]
    public void ThePlayFromAPhoneSubmenuShowsItsItemsAndRaisesTheOneClicked()
    {
        StaThread.Run(() =>
        {
            MenuState state = MenuModel.Build(NoDevice(), Block(BlockState.NotSetUp), null, Settings(), busy: false, StartupState.Off, streaming: PlayingFromAPhone);
            using var menu = new TrayMenu(() => state);
            var clicked = new List<StreamingMenuItem>();
            menu.PlayFromPhoneItemClicked += (_, e) => clicked.Add(e.Item);

            IReadOnlyList<ToolStripMenuItem> items = menu.PlayFromPhoneItems;
            CollectionAssert.AreEqual(
                PlayingFromAPhone.Items.Select(i => i.Text).ToArray(),
                items.Select(i => i.Text).ToArray());
            Assert.IsTrue(items[0].Checked);
            Assert.IsFalse(items[1].Enabled, "A sentence is never clickable.");
            Assert.IsFalse(items.Any(i => (i.Text ?? "").Contains("phone-id", StringComparison.Ordinal)), "The device id rides on the item, never in its text.");

            foreach (ToolStripMenuItem item in items)
            {
                item.PerformClick();
            }

            CollectionAssert.AreEqual(
                Commands,
                clicked.Select(i => i.Command + ":" + (i.DeviceId ?? "-")).ToArray());

            // Applying the state again rebuilds the submenu rather than adding to it.
            menu.Refresh();
            Assert.AreEqual(4, menu.PlayFromPhoneItems.Count);
        });
    }

    [TestMethod]
    public void TheLowBatteryThresholdSubmenuShowsItsItemsAndRaisesTheOneClicked()
    {
        StaThread.Run(() =>
        {
            EarshotSettings settings = Settings(s => s.Widget = s.Widget with { LowBatteryAlert = true, LowBatteryThresholdPercent = 30 });
            MenuState state = State(settings: settings);
            using var menu = new TrayMenu(() => state);
            var clicked = new List<LowBatteryThresholdMenuItem>();
            menu.LowBatteryThresholdItemClicked += (_, e) => clicked.Add(e.Item);

            IReadOnlyList<ToolStripMenuItem> items = menu.LowBatteryThresholdItems;
            Assert.AreEqual(9, items.Count);
            CollectionAssert.AreEqual(
                state.LowBatteryThresholdItems.Select(i => i.Text).ToArray(),
                items.Select(i => i.Text).ToArray());
            Assert.IsTrue(items[2].Checked, "30% is the third entry (10, 20, 30) and the saved threshold.");
            Assert.IsTrue(items.All(i => i.Enabled), "The alert is on, so every entry is enabled.");

            items[4].PerformClick();

            Assert.AreEqual(1, clicked.Count);
            Assert.AreEqual(50, clicked[0].Percent);

            // Applying the state again rebuilds the submenu rather than adding to it.
            menu.Refresh();
            Assert.AreEqual(9, menu.LowBatteryThresholdItems.Count);
        });
    }

    [TestMethod]
    public void TheLowBatteryThresholdSubmenuIsDisabledWhenTheAlertItselfIsOff()
    {
        StaThread.Run(() =>
        {
            EarshotSettings settings = Settings(s => s.Widget = s.Widget with { LowBatteryAlert = false });
            using var menu = new TrayMenu(() => State(settings: settings));

            Assert.IsTrue(menu.LowBatteryThresholdItems.All(i => !i.Enabled));
            ToolStripMenuItem opener = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Threshold");
            Assert.IsFalse(opener.Enabled, "The submenu's own opener row disables together with its children.");
        });
    }

    // The alert toggle and its threshold submenu are two separate rows, never one item doing both jobs: a
    // click on the toggle's own row must never be mistaken for opening the submenu, and the submenu's own
    // opener carries no checkmark of its own to click.
    [TestMethod]
    public void TheLowBatteryAlertToggleAndItsThresholdSubmenuAreSeparateRows()
    {
        StaThread.Run(() =>
        {
            EarshotSettings settings = Settings(s => s.Widget = s.Widget with { LowBatteryAlert = true });
            using var menu = new TrayMenu(() => State(settings: settings));

            ToolStripMenuItem toggle = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Low battery alert");
            ToolStripMenuItem opener = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Threshold");

            Assert.AreNotSame(toggle, opener);
            Assert.IsEmpty(toggle.DropDownItems.Cast<ToolStripItem>().ToArray(),
                "The toggle itself must carry no dropdown: a checkable item and a submenu parent are never the same row.");
            Assert.HasCount(9, opener.DropDownItems.Cast<ToolStripItem>().ToArray());
        });
    }

    [TestMethod]
    public void ClickingTheWidgetMenuItemsRaisesTheirEvents()
    {
        StaThread.Run(() =>
        {
            EarshotSettings settings = Settings(s => s.Widget = s.Widget with { LowBatteryAlert = true });
            using var menu = new TrayMenu(() => State(block: Block(BlockState.NotSetUp), settings: settings));
            var raised = new List<string>();
            menu.ShowOnTaskbarClicked += (_, _) => raised.Add("taskbar");
            menu.LeftClickConnectsClicked += (_, _) => raised.Add("leftclick");
            menu.CaseOpenCardClicked += (_, _) => raised.Add("caseopen");
            menu.LowBatteryAlertClicked += (_, _) => raised.Add("lowbattery");
            menu.NameOtherDeviceClicked += (_, _) => raised.Add("othername");

            menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Show on the taskbar").PerformClick();
            menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Left click connects straight away").PerformClick();
            menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Card when the case opens").PerformClick();
            menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Low battery alert").PerformClick();
            menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Name your other device...").PerformClick();

            CollectionAssert.AreEqual(WidgetCommandOrder, raised);
        });
    }

    [TestMethod]
    public void AnUnsupportedPlayFromAPhoneIsShownDisabled()
    {
        StaThread.Run(() =>
        {
            var unsupported = new StreamingMenuModel(StreamingLabels.Parent, ParentEnabled: false, [StreamingMenuModel.Sentence("Needs a newer version of Windows.")]);
            MenuState state = MenuModel.Build(NoDevice(), Block(BlockState.NotSetUp), null, Settings(), busy: false, StartupState.Off, streaming: unsupported);
            using var menu = new TrayMenu(() => state);

            ToolStripMenuItem parent = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Play from a phone");
            Assert.IsTrue(parent.Available);
            Assert.IsFalse(parent.Enabled);
        });
    }

    [TestMethod]
    public void ACallerThatOmitsVoiceKnownMissingSeesTheOrdinaryLabelNotNoVoice()
    {
        StaThread.Run(() =>
        {
            // MenuModel.Build(...) with no voiceKnownMissing argument at all, the exact call shape every
            // caller except TrayContext uses.
            MenuState state = MenuModel.Build(NoDevice(), Block(BlockState.NotSetUp), null, Settings(), busy: false, StartupState.Off);

            Assert.AreEqual(MenuModel.SpeakStatusText, state.SpeakStatus.Text);
            Assert.IsTrue(state.SpeakStatus.Enabled);
        });
    }

    [TestMethod]
    public void VoiceKnownMissingTrueDisablesTheItemAndRelabelsIt()
    {
        StaThread.Run(() =>
        {
            using var menu = new TrayMenu(() => State(block: Block(BlockState.NotSetUp), voiceKnownMissing: true));

            ToolStripMenuItem item = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.SpeakStatusNoVoice);
            Assert.IsFalse(item.Enabled);
        });
    }

    [TestMethod]
    public void SafeModeShowsItsCaptionAboveTheDesignedItems()
    {
        StaThread.Run(() =>
        {
            using var menu = new TrayMenu(() => State(block: Block(BlockState.NotSetUp), safeMode: true));

            CollectionAssert.AreEqual(DesignedOrder.Prepend("Safe mode: no device actions").ToArray(), AvailableTexts(menu));
        });
    }

    [TestMethod]
    public void SetUpComesAndGoesWithTheState()
    {
        StaThread.Run(() =>
        {
            BootBlockStatus block = Block(BlockState.Allowed);
            using var menu = new TrayMenu(() => State(block: block));
            CollectionAssert.DoesNotContain(AvailableTexts(menu), "Set up Earshot...");

            block = Block(BlockState.NotSetUp);
            menu.Refresh();

            CollectionAssert.AreEqual(DesignedOrder, AvailableTexts(menu));
        });
    }

    [TestMethod]
    public void CheckedEnabledAndIndeterminateReachTheItems()
    {
        StaThread.Run(() =>
        {
            using var menu = new TrayMenu(() => State());
            menu.Apply(State(
                snapshot: Target(ConnectionState.Connected),
                block: Block(BlockState.Allowed, blockAtBoot: false),
                busy: true,
                protection: Protection(AudioProtectionState.Partial)));

            ToolStripMenuItem Item(string text) =>
                menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

            Assert.IsFalse(Item("Disconnect").Enabled);
            Assert.AreEqual(CheckState.Unchecked, Item("Block at boot").CheckState);
            Assert.AreEqual(CheckState.Checked, Item("Hand back at shut down and sleep").CheckState, "Default on.");
            Assert.AreEqual(CheckState.Indeterminate, Item("Protect audio quality").CheckState);
            Assert.IsFalse(Item("Turns off the AirPods microphone").Enabled);
            Assert.IsTrue(Item("Exit").Enabled);
            Assert.IsFalse(Item("Exit").CheckOnClick);
        });
    }

    [TestMethod]
    public void ClickingAnItemRaisesItsCommand()
    {
        StaThread.Run(() =>
        {
            using var menu = new TrayMenu(() => State(block: Block(BlockState.NotSetUp)));
            var raised = new List<string>();
            menu.ToggleClicked += (_, _) => raised.Add("toggle");
            menu.BlockAtBootClicked += (_, _) => raised.Add("block");
            menu.HandBackClicked += (_, _) => raised.Add("handback");
            menu.ProtectAudioClicked += (_, _) => raised.Add("protect");
            menu.OpenOnStartupClicked += (_, _) => raised.Add("startup");
            menu.ChooseDeviceClicked += (_, _) => raised.Add("device");
            menu.SetUpClicked += (_, _) => raised.Add("setup");
            menu.ExitClicked += (_, _) => raised.Add("exit");

            foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>().Where(i => i.Available && i.Enabled))
            {
                item.PerformClick();
            }

            CollectionAssert.AreEqual(CommandOrder, raised);
        });
    }
}
