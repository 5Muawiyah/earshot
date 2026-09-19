using System.Windows.Forms;
using Earshot.Contracts;
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
        "Block at boot", "Protect audio quality", "Turns off the AirPods microphone", "Open on startup",
        "Speak status", "-",
        "Choose device...", "Set up Earshot...", "-",
        "Exit",
    ];

    private static readonly string[] CommandOrder = ["toggle", "block", "protect", "startup", "device", "setup", "exit"];

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
