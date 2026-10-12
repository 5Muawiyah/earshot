using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// The three ways in to the send-to picker, each with its own switch: the card's row, the item in the menu a right-click on the
// gauge opens, and the shortcut. Turning one off leaves the others on.
[TestClass]
public sealed class SendToAccessTests
{
    private static MenuState Menu(EarshotSettings settings, bool openedFromGauge) =>
        MenuModel.Build(NoDevice(), null, null, settings, busy: false, StartupState.Off, openedFromGauge: openedFromGauge);

    [TestMethod]
    public void EveryWayInIsOnByDefaultForANewInstallAndAnOldFile()
    {
        var fresh = new EarshotSettings();
        Assert.IsTrue(fresh.Widget.SendToOnCard);
        Assert.IsTrue(fresh.Widget.SendToOnGauge);
        Assert.IsTrue(SendToShortcut.IsOn(fresh.Hotkeys));

        var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Widget\": { \"ShowOnTaskbar\": true } }");
        EarshotSettings old = new JsonSettingsStore(path, new CapturingLog()).Current;

        Assert.IsTrue(old.Widget.SendToOnCard, "A file that never held the switch reads as on.");
        Assert.IsTrue(old.Widget.SendToOnGauge);
        Assert.IsTrue(SendToShortcut.IsOn(old.Hotkeys));
    }

    [TestMethod]
    public void TheSwitchesAreSavedAndReadBack()
    {
        var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("settings.json");
        var store = new JsonSettingsStore(path, log);
        store.Update(s => s.Widget = s.Widget with { SendToOnCard = false });
        Assert.IsFalse(new JsonSettingsStore(path, log).Current.Widget.SendToOnCard);
        Assert.IsTrue(new JsonSettingsStore(path, log).Current.Widget.SendToOnGauge);

        store.Update(s => s.Widget = s.Widget with { SendToOnGauge = false });
        EarshotSettings read = new JsonSettingsStore(path, log).Current;
        Assert.IsFalse(read.Widget.SendToOnCard);
        Assert.IsFalse(read.Widget.SendToOnGauge);
    }

    [TestMethod]
    public void TheCardRowGoesOffOnItsOwn()
    {
        WidgetSettings on = new();
        WidgetSettings off = on with { SendToOnCard = false };

        Assert.IsTrue(SendToAccess.CardRow(on, noticeCard: false));
        Assert.IsFalse(SendToAccess.CardRow(off, noticeCard: false));
        Assert.IsTrue(SendToAccess.GaugeMenuItem(off, openedFromGauge: true), "The gauge menu is untouched.");
        Assert.IsTrue(SendToShortcut.IsOn(new HotkeySettings()), "The shortcut is untouched.");
    }

    [TestMethod]
    public void TheCaseOpenCardNeverHasTheRow()
    {
        Assert.IsFalse(SendToAccess.CardRow(new WidgetSettings(), noticeCard: true));
    }

    [TestMethod]
    public void TheGaugeMenuItemGoesOffOnItsOwn()
    {
        EarshotSettings on = Settings();
        EarshotSettings off = Settings();
        off.Widget = off.Widget with { SendToOnGauge = false };

        Assert.IsTrue(Menu(on, openedFromGauge: true).SendTo.Visible);
        Assert.IsFalse(Menu(off, openedFromGauge: true).SendTo.Visible);
        Assert.IsTrue(SendToAccess.CardRow(off.Widget, noticeCard: false), "The card row is untouched.");
        Assert.IsTrue(SendToShortcut.IsOn(off.Hotkeys), "The shortcut is untouched.");
    }

    [TestMethod]
    public void TheTrayIconsOwnMenuNeverHasTheItem()
    {
        MenuState state = Menu(Settings(), openedFromGauge: false);

        Assert.IsFalse(state.SendTo.Visible);
    }

    [TestMethod]
    public void TheMenuItemSaysSendToWithTheShortcutAndIsNeverHeldByASwitchInFlight()
    {
        EarshotSettings settings = Settings();
        MenuState idle = MenuModel.Build(NoDevice(), null, null, settings, busy: false, StartupState.Off, openedFromGauge: true);
        MenuState busy = MenuModel.Build(NoDevice(), null, null, settings, busy: true, StartupState.Off, openedFromGauge: true);

        Assert.AreEqual("Send to... (Ctrl+Alt+Shift+S)", idle.SendTo.Text);
        Assert.IsTrue(busy.SendTo.Enabled, "Opening the picker touches nothing; its own rows are held while a switch runs.");
    }

    [TestMethod]
    public void TheShortcutGoesOffOnItsOwnWhenCleared()
    {
        var hotkeys = new HotkeySettings { SendTo = string.Empty };
        var settings = Settings();

        Assert.IsFalse(SendToShortcut.IsOn(hotkeys));
        Assert.IsFalse(SendToShortcut.IsOn(new HotkeySettings { Enabled = false }));
        Assert.IsTrue(SendToAccess.CardRow(settings.Widget, noticeCard: false), "The card row is untouched.");
        Assert.IsTrue(SendToAccess.GaugeMenuItem(settings.Widget, openedFromGauge: true), "The gauge menu is untouched.");
    }

    [TestMethod]
    public void AClearedShortcutLeavesNoChordOnTheMenuItem()
    {
        EarshotSettings settings = Settings();
        settings.Hotkeys.SendTo = string.Empty;

        Assert.AreEqual("Send to...", Menu(settings, openedFromGauge: true).SendTo.Text);
    }

    [TestMethod]
    public void TheConnectionOfThisPcIsHereWhateverTheBroadcastLastSaid()
    {
        Assert.AreEqual(AirPodsWhere.ThisPc, SendToAccess.Where(AirPodsWhere.Elsewhere, connectedHere: true));
        Assert.AreEqual(AirPodsWhere.ThisPc, SendToAccess.Where(AirPodsWhere.Unknown, connectedHere: true));
        Assert.AreEqual(AirPodsWhere.Elsewhere, SendToAccess.Where(AirPodsWhere.Elsewhere, connectedHere: false));
        Assert.AreEqual(AirPodsWhere.NotInUse, SendToAccess.Where(AirPodsWhere.NotInUse, connectedHere: false));
    }
}
