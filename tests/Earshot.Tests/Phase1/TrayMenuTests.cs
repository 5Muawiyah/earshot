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
    private static readonly string[] DesignedOrder =
    [
        "Connect", "-",
        "Block at boot", "Protect audio quality", "Turns off the AirPods microphone", "Open on startup", "-",
        "Choose device...", "Set up Earshot...", "-",
        "Exit",
    ];

    private static readonly string[] CommandOrder = ["toggle", "block", "protect", "startup", "device", "setup", "exit"];

    private static MenuState State(
        DeviceSnapshot? snapshot = null,
        BootBlockStatus? block = null,
        bool busy = false,
        bool safeMode = false,
        EarshotSettings? settings = null,
        AudioProtectionSnapshot? protection = null) =>
        MenuModel.Build(snapshot ?? NoDevice(), block, protection, settings ?? Settings(), busy, safeMode, StartupState.Off);

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
    public void SetUpAndTheSafeModeCaptionComeAndGoWithTheState()
    {
        StaThread.Run(() =>
        {
            using var menu = new TrayMenu(() => State(block: Block(BlockState.Allowed)));
            CollectionAssert.DoesNotContain(AvailableTexts(menu), "Set up Earshot...");
            CollectionAssert.DoesNotContain(AvailableTexts(menu), "Safe mode: no device actions");

            menu.Apply(State(block: Block(BlockState.NotSetUp), safeMode: true));
            string[] texts = AvailableTexts(menu);

            Assert.AreEqual("Safe mode: no device actions", texts[0]);
            Assert.AreEqual("-", texts[1]);
            CollectionAssert.Contains(texts, "Set up Earshot...");
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
