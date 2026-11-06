using Earshot.Icons;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Widget;

// The icons the design lists, each by the name the card's code gave it when it drew a Segoe Fluent code point, and the MingCute file
// the icons spec (section 1a) draws in its place. Pure: the catalogue, not the drawing.
[TestClass]
public sealed class DesignGlyphTests
{
    // The design's list: the name, the catalogue icon that replaces it, the MingCute path under assets/svg/core/{regular|filled}
    // and whether the set has a filled drawing of it the card or the menu uses.
    private static readonly (string Name, Icon Icon, string Path, bool Filled)[] Listed =
    [
        ("Refresh", Icon.Refresh, "system/refresh_1", false),
        ("Settings", Icon.Settings, "system/settings_3", false),
        ("Back", Icon.Back, "arrow/arrow_left", false),
        ("Bolt", Icon.Lightning, "weather/lightning", true),
        ("ReadTime", Icon.Time, "system/time", false),
        ("OnThisPc", Icon.Computer, "devices/computer", true),
        ("Warning", Icon.Alert, "system/alert", false),
        ("BluetoothOff", Icon.BluetoothOff, "devices/bluetooth_off", false),
        ("CellPhone", Icon.Cellphone, "devices/cellphone", true),
        ("TvMonitor", Icon.Monitor, "devices/monitor", false),
        ("Sort", Icon.Transfer, "arrow/transfer", false),
        ("Preview", Icon.AirPodsCase, "devices/airpods_2", true),
        ("MicOff", Icon.MicOff, "media/mic_off", true),
        ("Volume", Icon.Volume, "media/volume", false),
        ("Keyboard", Icon.Keyboard, "devices/keyboard", false),
        ("Sync", Icon.Sync, "system/refresh_2", true),
        ("WhatsNew", Icon.Information, "system/information", false),
        ("OpenExternal", Icon.ExternalLink, "files/external_link", false),
        ("ChevronRight", Icon.ChevronRight, "arrow/right", false),
        ("ChevronDown", Icon.ChevronDown, "arrow/down", false),
        ("ChevronUp", Icon.ChevronUp, "arrow/up", false),

        // The rest of the table: icons the design does not list by name but the card and its pages draw.
        ("ChevronLeft", Icon.ChevronLeft, "arrow/left", false),
        ("Bluetooth", Icon.Bluetooth, "devices/bluetooth", false),
        ("History", Icon.History, "system/history", false),
        ("Copy", Icon.Copy, "files/copy", false),
        ("More", Icon.More, "system/more_1", false),
        ("Download", Icon.Download, "files/download_2", false),
        ("DockBottom", Icon.LayoutBottom, "design/layout_bottom", true),
        ("Devices", Icon.Device, "devices/device", false),
        ("Earbud", Icon.AirPods, "media/airpods", true),
        ("Pause", Icon.Pause, "media/pause", true),
        ("Ringer", Icon.Notification, "media/notification", true),
        ("Mouse", Icon.Mouse, "devices/mouse", true),
        ("PowerButton", Icon.Power, "system/power", true),
        ("Cancel", Icon.Close, "system/close", false),
        ("Repair", Icon.Tool, "system/tool", false),
        ("CheckMark", Icon.Check, "system/check", false),
        ("Send", Icon.Send, "communication/send", true),
        ("Error", Icon.Warning, "system/warning", false),
    ];

    [TestMethod]
    public void EveryIconTheDesignListsIsTheMingCuteFileTheSpecNamesForIt()
    {
        foreach ((string name, Icon icon, string path, bool filled) in Listed)
        {
            Assert.AreEqual("regular/" + path, IconCatalogue.Outline(icon, IconStyle.Line).Name, name + ": the line drawing.");
            Assert.AreEqual(filled, IconCatalogue.HasFilled(icon), name + ": whether the set has a filled drawing it uses.");
            Assert.AreEqual(filled ? "filled/" + path : "regular/" + path, IconCatalogue.Outline(icon, IconStyle.Filled).Name, name + ": the filled drawing, or the line one where there is none.");
        }
    }

    [TestMethod]
    public void NoTwoNamesOfTheDesignMeanTheSameIconByMistake()
    {
        // The close and the warning marks are listed once each; the table repeats no icon but where the design reuses one on purpose.
        var seen = new Dictionary<Icon, string>();
        foreach ((string name, Icon icon, _, _) in Listed)
        {
            Assert.IsTrue(seen.TryAdd(icon, name), name + " and " + (seen.TryGetValue(icon, out string? other) ? other : "?") + " are the same icon.");
        }
    }

    [TestMethod]
    public void TheSettingsRowsUseTheDesignsIcons()
    {
        Assert.AreEqual(Icon.Monitor, SettingsRows.For(SettingsRowId.GaugeDisplay).Glyph);
        Assert.AreEqual(Icon.Transfer, SettingsRows.For(SettingsRowId.GaugeOrder).Glyph);
        Assert.AreEqual(Icon.AirPodsCase, SettingsRows.For(SettingsRowId.CaseCard).Glyph);
        Assert.AreEqual(Icon.MicOff, SettingsRows.For(SettingsRowId.MicrophoneOff).Glyph);
        Assert.AreEqual(Icon.Volume, SettingsRows.For(SettingsRowId.SoundSettings).Glyph);
        Assert.AreEqual(Icon.Keyboard, SettingsRows.For(SettingsRowId.Connect).Glyph);
        Assert.AreEqual(Icon.Keyboard, SettingsRows.For(SettingsRowId.Disconnect).Glyph);
        Assert.AreEqual(Icon.Keyboard, SettingsRows.For(SettingsRowId.OpenCard).Glyph);
        Assert.AreEqual(Icon.Sync, SettingsRows.For(SettingsRowId.About).Glyph);
        Assert.AreEqual(Icon.Sync, SettingsRows.For(SettingsRowId.CheckAutomatically).Glyph);
    }

    [TestMethod]
    public void TheRestOfTheSettingsRowsUseTheSpecsIcons()
    {
        (SettingsRowId Row, Icon Icon)[] table =
        [
            (SettingsRowId.GaugePosition, Icon.LayoutBottom), (SettingsRowId.OtherDevice, Icon.Cellphone), (SettingsRowId.PauseBud, Icon.AirPods),
            (SettingsRowId.PauseLeave, Icon.Pause), (SettingsRowId.LowBattery, Icon.Notification), (SettingsRowId.LeftClick, Icon.Mouse),
            (SettingsRowId.HandBack, Icon.Power), (SettingsRowId.SendTo, Icon.Keyboard), (SettingsRowId.SendToCard, Icon.Send),
            (SettingsRowId.SendToGauge, Icon.Send), (SettingsRowId.Repair, Icon.Tool), (SettingsRowId.FullyCharged, Icon.Lightning),
            (SettingsRowId.MovedTo, Icon.Cellphone), (SettingsRowId.History, Icon.History), (SettingsRowId.CopyDiagnostics, Icon.Copy),
            (SettingsRowId.More, Icon.More),
        ];
        foreach ((SettingsRowId row, Icon icon) in table)
        {
            Assert.AreEqual(icon, SettingsRows.For(row).Glyph, row.ToString());
        }

        foreach (SettingsRowId nested in new[] { SettingsRowId.CaseCardClose, SettingsRowId.CaseCardDisplays, SettingsRowId.CaseCardDisplay })
        {
            Assert.IsNull(SettingsRows.For(nested).Glyph, nested + " has no icon of its own.");
        }
    }
}
