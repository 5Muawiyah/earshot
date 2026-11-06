using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Tests.Phase1;
using Earshot.Tests.Phase5;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Icons;

// The tray menu's icons. Every top-level command has its icon in the image column, in the menu's text colour, filled exactly when the
// item is checked (the Connect and Disconnect item: when it says Disconnect) and line when it is unchecked or indeterminate. The menu is
// shown and drawn on a private desktop only; the icon log records what the renderer drew, and a second drawing with nothing painted
// gives the background to compare the painted icon against.
[TestClass]
public sealed class TrayMenuIconTests
{
    // Index in TrayMenu.Items -> the icon of the item there. The separators (5, 12, 19, 25, 28) have none.
    private static readonly (int Index, Icon Icon)[] Table =
    [
        (0, Icon.SafeShield),
        (1, Icon.AirPods),
        (2, Icon.PlayCircle),
        (3, Icon.Refresh),
        (4, Icon.Send),
        (6, Icon.Lock),
        (7, Icon.Power),
        (8, Icon.Headphone),
        (9, Icon.MicOff),
        (10, Icon.Rocket),
        (11, Icon.Announcement),
        (13, Icon.LayoutBottom),
        (14, Icon.Mouse),
        (15, Icon.AirPodsCase),
        (16, Icon.Notification),
        (17, Icon.Battery),
        (18, Icon.Cellphone),
        (20, Icon.Device),
        (21, Icon.Settings),
        (22, Icon.Tool),
        (23, Icon.Bluetooth),
        (24, Icon.BluetoothSearching),
        (26, Icon.Download),
        (27, Icon.Sync),
        (29, Icon.Copy),
        (30, Icon.Exit),
    ];

    private sealed record ItemInfo(int Index, string Text, bool Separator, bool Available, bool Enabled, CheckState Check, Rectangle Bounds, Color ForeColor);

    private sealed record Shown(IReadOnlyList<ItemInfo> Items, IReadOnlyList<DrawnIcon> Log, Bitmap Painted, Bitmap Bare, Size ImageSize, IReadOnlyList<Size> ImageSizes, bool CheckMargin, bool ImageMargin, uint IconDpi, Rectangle Display);

    private static MenuState Base(DeviceSnapshot snapshot, bool block = true)
    {
        BootBlockStatus status = Phase1Fixtures.Block(BlockState.Blocked, block);
        MenuState state = MenuModel.Build(snapshot, status, null, Phase1Fixtures.Settings(), false, StartupState.Off);
        return AllVisible(state);
    }

    private static MenuItemState Show(MenuItemState item) => item with { Visible = true };

    // Every command shown, so one drawing carries all 26 icons.
    private static MenuState AllVisible(MenuState s) => s with
    {
        SafeMode = Show(s.SafeMode),
        Toggle = Show(s.Toggle),
        PlayFromPhone = Show(s.PlayFromPhone),
        RefreshBattery = Show(s.RefreshBattery),
        SendTo = Show(s.SendTo),
        BlockAtBoot = Show(s.BlockAtBoot),
        HandBack = Show(s.HandBack),
        ProtectAudio = Show(s.ProtectAudio),
        ProtectCaveat = Show(s.ProtectCaveat),
        OpenOnStartup = Show(s.OpenOnStartup),
        SpeakStatus = Show(s.SpeakStatus),
        ShowOnTaskbar = Show(s.ShowOnTaskbar),
        LeftClickConnectsItem = Show(s.LeftClickConnectsItem),
        CaseOpenCardItem = Show(s.CaseOpenCardItem),
        LowBatteryAlert = Show(s.LowBatteryAlert),
        LowBatteryThreshold = Show(s.LowBatteryThreshold),
        NameOtherDeviceItem = Show(s.NameOtherDeviceItem),
        ChooseDevice = Show(s.ChooseDevice),
        SetUp = Show(s.SetUp),
        Repair = Show(s.Repair),
        FixConnection = Show(s.FixConnection),
        PairAirPods = Show(s.PairAirPods),
        CheckForUpdates = Show(s.CheckForUpdates),
        CheckAutomatically = Show(s.CheckAutomatically),
        CopyDiagnostics = Show(s.CopyDiagnostics),
        Exit = Show(s.Exit),
    };

    // Every command checked, none the Connect and Disconnect item (the model never checks it).
    private static MenuState AllChecked(MenuState s) => s with
    {
        SafeMode = Check(s.SafeMode),
        PlayFromPhone = Check(s.PlayFromPhone),
        RefreshBattery = Check(s.RefreshBattery),
        SendTo = Check(s.SendTo),
        BlockAtBoot = Check(s.BlockAtBoot),
        HandBack = Check(s.HandBack),
        ProtectAudio = Check(s.ProtectAudio),
        ProtectCaveat = Check(s.ProtectCaveat),
        OpenOnStartup = Check(s.OpenOnStartup),
        SpeakStatus = Check(s.SpeakStatus),
        ShowOnTaskbar = Check(s.ShowOnTaskbar),
        LeftClickConnectsItem = Check(s.LeftClickConnectsItem),
        CaseOpenCardItem = Check(s.CaseOpenCardItem),
        LowBatteryAlert = Check(s.LowBatteryAlert),
        LowBatteryThreshold = Check(s.LowBatteryThreshold),
        NameOtherDeviceItem = Check(s.NameOtherDeviceItem),
        ChooseDevice = Check(s.ChooseDevice),
        SetUp = Check(s.SetUp),
        Repair = Check(s.Repair),
        FixConnection = Check(s.FixConnection),
        PairAirPods = Check(s.PairAirPods),
        CheckForUpdates = Check(s.CheckForUpdates),
        CheckAutomatically = Check(s.CheckAutomatically),
        CopyDiagnostics = Check(s.CopyDiagnostics),
        Exit = Check(s.Exit),
    };

    private static MenuItemState Check(MenuItemState item) => item with { Checked = true, Indeterminate = false };

    private static MenuItemState Indeterminate(MenuItemState item) => item with { Checked = false, Indeterminate = true };

    private static MenuState AllIndeterminate(MenuState s) => s with
    {
        BlockAtBoot = Indeterminate(s.BlockAtBoot),
        HandBack = Indeterminate(s.HandBack),
        ProtectAudio = Indeterminate(s.ProtectAudio),
        OpenOnStartup = Indeterminate(s.OpenOnStartup),
        SpeakStatus = Indeterminate(s.SpeakStatus),
    };

    private static MenuState AllUnchecked(MenuState s) => AllWith(s, item => item with { Checked = false, Indeterminate = false });

    private static MenuState AllDisabled(MenuState s) => AllWith(s, item => item with { Enabled = false });

    private static MenuState AllWith(MenuState s, Func<MenuItemState, MenuItemState> change) => s with
    {
        SafeMode = change(s.SafeMode),
        Toggle = change(s.Toggle),
        PlayFromPhone = change(s.PlayFromPhone),
        RefreshBattery = change(s.RefreshBattery),
        SendTo = change(s.SendTo),
        BlockAtBoot = change(s.BlockAtBoot),
        HandBack = change(s.HandBack),
        ProtectAudio = change(s.ProtectAudio),
        ProtectCaveat = change(s.ProtectCaveat),
        OpenOnStartup = change(s.OpenOnStartup),
        SpeakStatus = change(s.SpeakStatus),
        ShowOnTaskbar = change(s.ShowOnTaskbar),
        LeftClickConnectsItem = change(s.LeftClickConnectsItem),
        CaseOpenCardItem = change(s.CaseOpenCardItem),
        LowBatteryAlert = change(s.LowBatteryAlert),
        LowBatteryThreshold = change(s.LowBatteryThreshold),
        NameOtherDeviceItem = change(s.NameOtherDeviceItem),
        ChooseDevice = change(s.ChooseDevice),
        SetUp = change(s.SetUp),
        Repair = change(s.Repair),
        FixConnection = change(s.FixConnection),
        PairAirPods = change(s.PairAirPods),
        CheckForUpdates = change(s.CheckForUpdates),
        CheckAutomatically = change(s.CheckAutomatically),
        CopyDiagnostics = change(s.CopyDiagnostics),
        Exit = change(s.Exit),
    };

    private static Bitmap Draw(ContextMenuStrip strip)
    {
        var bitmap = new Bitmap(strip.Width, strip.Height, PixelFormat.Format24bppRgb);
        strip.DrawToBitmap(bitmap, new Rectangle(Point.Empty, strip.Size));
        return bitmap;
    }

    // Opens the menu for state on a private desktop and draws it twice: once with the icons painted, once with nothing painted.
    private static Shown Open(MenuState state, Action<TrayMenu>? configure = null)
    {
        Shown? shown = null;
        CardDesktop.Run(() =>
        {
            using var menu = new TrayMenu(() => state);
            configure?.Invoke(menu);
            ContextMenuStrip strip = menu.Strip;
            strip.Show(new Point(0, 0));
            CardSta.PumpUntil(() => strip.Visible && strip.Width > 0, TimeSpan.FromSeconds(10));
            Assert.IsTrue(strip.Visible, "The menu never opened on the private desktop.");

            List<ItemInfo> items = [];
            int index = 0;
            foreach (ToolStripItem item in menu.Items)
            {
                CheckState check = item is ToolStripMenuItem m ? m.CheckState : CheckState.Unchecked;
                items.Add(new ItemInfo(index++, item.Text ?? string.Empty, item is ToolStripSeparator, item.Available, item.Enabled, check, item.Bounds, item.ForeColor));
            }

            IconPainter.DrawnIconLog = [];
            IconPainter.PaintNothingForTest = true;
            Bitmap bare;
            try
            {
                bare = Draw(strip);
            }
            finally
            {
                IconPainter.PaintNothingForTest = false;
            }

            IconPainter.DrawnIconLog = [];
            Bitmap painted = Draw(strip);
            List<DrawnIcon> log = IconPainter.DrawnIconLog;
            IconPainter.DrawnIconLog = null;
            List<Size> imageSizes = menu.Items.Where(i => i is ToolStripMenuItem { Image: not null } && Table.Any(t => ReferenceEquals(menu.Items[t.Index], i)))
                .Select(i => i.Image!.Size).ToList();
            shown = new Shown(items, log, painted, bare, strip.ImageScalingSize, imageSizes, strip.ShowCheckMargin, strip.ShowImageMargin, menu.IconDpi, strip.DisplayRectangle);
            strip.Close();
        });
        Assert.IsNotNull(shown);
        return shown;
    }

    // The renderer draws in each item's own coordinates, so a logged box is relative to its item's top-left corner. Items are painted
    // top to bottom, so the log holds the icons in the order of Table; this pairs them and puts each box in the strip's coordinates.
    private static List<(ItemInfo Item, DrawnIcon Drawn, Rectangle InStrip)> Pair(Shown shown)
    {
        // A menu taller than the screen's working area scrolls, and a command outside the shown part is never painted, so its icon
        // cannot be counted. A small screen (a hosted runner's) at a high dpi does that; the count is then not a finding either way.
        List<ItemInfo> hidden = Table.Select(t => shown.Items[t.Index]).Where(i => i.Bounds.Top < shown.Display.Top || i.Bounds.Bottom > shown.Display.Bottom).ToList();
        if (hidden.Count > 0)
        {
            Assert.Inconclusive(hidden.Count + " of the " + Table.Length + " commands lie outside the shown part of the menu " + shown.Display +
                " on this screen, so their icons are not painted and cannot be counted.");
        }

        Assert.AreEqual(Table.Length, shown.Log.Count, "The menu should draw one icon for each of its " + Table.Length + " commands and none else (a submenu entry or a separator drew one, or a command drew none).");
        var pairs = new List<(ItemInfo, DrawnIcon, Rectangle)>();
        for (int i = 0; i < Table.Length; i++)
        {
            ItemInfo item = shown.Items[Table[i].Index];
            DrawnIcon drawn = shown.Log[i];
            var inStrip = new Rectangle(item.Bounds.X + drawn.Box.X, item.Bounds.Y + drawn.Box.Y, drawn.Box.Width, drawn.Box.Height);
            Assert.IsTrue(item.Bounds.Contains(inStrip), "Item " + item.Index + " (" + item.Text + ") drew its icon outside its own row: " + inStrip + " against " + item.Bounds);
            pairs.Add((item, drawn, inStrip));
        }

        return pairs;
    }

    private static void AssertEveryItemDrew(Shown shown, Func<ItemInfo, IconStyle> expectedStyle, Func<ItemInfo, Color> expectedInk, string state)
    {
        foreach (((int index, Icon icon), (ItemInfo item, DrawnIcon drawn, _)) in Table.Zip(Pair(shown)))
        {
            string who = state + ": item " + index + " (" + item.Text + ")";
            Assert.IsTrue(item.Available, who + " is not shown, so its icon cannot be checked.");
            Assert.AreEqual(icon, drawn.Icon, who + " draws the wrong icon.");
            Assert.AreEqual(expectedStyle(item), drawn.Style, who + " draws the wrong style.");
            Assert.AreEqual(IconRule.B, drawn.Rule, who + " uses rule B, the whole 24 grid in the box.");
            Assert.AreEqual(expectedInk(item).ToArgb(), drawn.Ink.ToArgb(), who + " draws in the wrong ink.");
            Assert.AreEqual(shown.ImageSize.Width, drawn.BoxPx, who + ": the icon box is the strip's image size.");
        }
    }

    [TestMethod]
    public void EveryCommandDrawsItsIconInTheLineStyleWhenUnchecked()
    {
        Shown shown = Open(AllUnchecked(Base(Phase1Fixtures.NoDevice())));
        foreach (ItemInfo item in shown.Items.Where(i => !i.Separator))
        {
            Assert.AreEqual(CheckState.Unchecked, item.Check, item.Text + " should be unchecked in this state.");
        }

        AssertEveryItemDrew(shown, item => IconStyle.Line, item => item.Enabled ? item.ForeColor : SystemColors.GrayText, "unchecked");
        Assert.AreEqual("Connect", shown.Items[1].Text.Split('\t')[0]);
    }

    [TestMethod]
    public void ACheckedCommandDrawsTheFilledStyleAndTheDisconnectItemToo()
    {
        Shown shown = Open(AllChecked(Base(Phase1Fixtures.Target(ConnectionState.Connected))));
        Assert.IsTrue(shown.Items[1].Text.StartsWith("Disconnect", StringComparison.Ordinal), "The fixture should be connected: " + shown.Items[1].Text);
        AssertEveryItemDrew(
            shown,
            item => item.Index == 1 || item.Check == CheckState.Checked ? IconStyle.Filled : IconStyle.Line,
            item => item.Enabled ? item.ForeColor : SystemColors.GrayText,
            "checked");
        Assert.AreEqual(CheckState.Unchecked, shown.Items[1].Check, "The Disconnect item is never checked; it is filled because of its text.");
    }

    [TestMethod]
    public void AnIndeterminateCommandStaysInTheLineStyle()
    {
        Shown shown = Open(AllIndeterminate(AllUnchecked(Base(Phase1Fixtures.NoDevice()))));
        Assert.AreEqual(CheckState.Indeterminate, shown.Items[6].Check, "The fixture should leave Block at boot indeterminate.");
        AssertEveryItemDrew(shown, item => IconStyle.Line, item => item.Enabled ? item.ForeColor : SystemColors.GrayText, "indeterminate");
    }

    [TestMethod]
    public void ADisabledCommandDrawsInTheGreyTextColour()
    {
        Shown shown = Open(AllDisabled(AllChecked(Base(Phase1Fixtures.Target(ConnectionState.Connected)))));
        Assert.IsFalse(shown.Items[21].Enabled);
        AssertEveryItemDrew(
            shown,
            item => item.Index == 1 || item.Check == CheckState.Checked ? IconStyle.Filled : IconStyle.Line,
            item => SystemColors.GrayText,
            "disabled");
    }

    [TestMethod]
    public void ThePaintedIconIsTheMaskInTheInkOverTheBackgroundAtWholePixels()
    {
        Shown shown = Open(AllChecked(Base(Phase1Fixtures.Target(ConnectionState.Connected))));
        Assert.IsTrue(shown.Log.Count > 0, "Nothing was drawn.");
        int compared = 0;
        foreach ((_, DrawnIcon icon, Rectangle at) in Pair(shown))
        {
            ReadOnlyMemory<byte> mask = IconRaster.Coverage(icon.Icon, icon.Style, icon.BoxPx, icon.Rule, icon.RotationStep);
            int side = IconRaster.MaskSide(icon.BoxPx);
            int[] expected = IconPainter.Argb(mask.Span, icon.Ink);
            int originX = at.X - IconRaster.Apron;
            int originY = at.Y - IconRaster.Apron;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    Color back = shown.Bare.GetPixel(originX + x, originY + y);
                    Color painted = shown.Painted.GetPixel(originX + x, originY + y);
                    int a = (expected[(y * side) + x] >> 24) & 0xFF;
                    int r = Over(icon.Ink.R, back.R, a);
                    int g = Over(icon.Ink.G, back.G, a);
                    int b = Over(icon.Ink.B, back.B, a);
                    Assert.IsTrue(
                        Math.Abs(painted.R - r) <= 1 && Math.Abs(painted.G - g) <= 1 && Math.Abs(painted.B - b) <= 1,
                        icon.Icon + " " + icon.Style + " pixel (" + x + "," + y + "): painted " + painted + " expected " + r + "," + g + "," + b + " over " + back + " at alpha " + a);
                    compared++;
                }
            }
        }

        Assert.IsTrue(compared > 1000, "Too few pixels compared: " + compared);
    }

    private static int Over(int ink, int back, int alpha) => ((ink * alpha) + (back * (255 - alpha)) + 127) / 255;

    [TestMethod]
    public void TheTextTheCheckStateAndTheEnabledStateAreAsTheStateSaid()
    {
        MenuState state = AllChecked(Base(Phase1Fixtures.Target(ConnectionState.Connected)));
        Shown shown = Open(state);
        Assert.AreEqual(31, shown.Items.Count);
        Assert.AreEqual(state.SafeMode.Text, shown.Items[0].Text);
        Assert.AreEqual(state.Toggle.Text, shown.Items[1].Text);
        Assert.AreEqual(state.BlockAtBoot.Text, shown.Items[6].Text);
        Assert.AreEqual(CheckState.Checked, shown.Items[6].Check);
        Assert.AreEqual(state.Exit.Text, shown.Items[30].Text);
        Assert.IsTrue(shown.Items[30].Enabled);
    }

    [TestMethod]
    public void TheInkIsTheTextColourGreyWhenDisabledAndHighlightTextOnASelectedItemInHighContrast()
    {
        Color text = Color.FromArgb(10, 20, 30);
        Assert.AreEqual(text.ToArgb(), TrayMenuRenderer.Ink(enabled: true, selected: false, highContrast: false, text).ToArgb());
        Assert.AreEqual(text.ToArgb(), TrayMenuRenderer.Ink(enabled: true, selected: true, highContrast: false, text).ToArgb(), "Selected, not high contrast: the text colour.");
        Assert.AreEqual(text.ToArgb(), TrayMenuRenderer.Ink(enabled: true, selected: false, highContrast: true, text).ToArgb(), "High contrast, not selected: the text colour.");
        Assert.AreEqual(SystemColors.HighlightText.ToArgb(), TrayMenuRenderer.Ink(enabled: true, selected: true, highContrast: true, text).ToArgb());
        Assert.AreEqual(SystemColors.GrayText.ToArgb(), TrayMenuRenderer.Ink(enabled: false, selected: false, highContrast: false, text).ToArgb());
        Assert.AreEqual(SystemColors.GrayText.ToArgb(), TrayMenuRenderer.Ink(enabled: false, selected: true, highContrast: true, text).ToArgb(), "A disabled item stays grey.");
    }

    [TestMethod]
    [DataRow(96u, 16)]
    [DataRow(120u, 20)]
    [DataRow(144u, 24)]
    [DataRow(192u, 32)]
    public void TheIconBoxFollowsTheMenusDpi(uint dpi, int px)
    {
        Assert.AreEqual(px, TrayMenuScale.IconPx(dpi), "SM_CXSMICON at " + dpi + " dpi.");
        Shown shown = Open(AllChecked(Base(Phase1Fixtures.Target(ConnectionState.Connected))), menu =>
        {
            menu.CursorDpi = () => dpi;
            menu.WindowDpi = _ => dpi;
        });
        Assert.AreEqual(dpi, shown.IconDpi);
        Assert.AreEqual(new Size(px, px), shown.ImageSize, "The strip's image size.");
        Assert.AreEqual(Table.Length, shown.ImageSizes.Count, "Every command carries the placeholder image.");
        Assert.IsTrue(shown.ImageSizes.All(s => s == new Size(px, px)), "Every placeholder is " + px + " px square.");
        foreach ((_, DrawnIcon drawn, _) in Pair(shown))
        {
            Assert.AreEqual(px, drawn.BoxPx, "The icon is drawn in a " + px + " px box at " + dpi + " dpi.");
        }
    }

    [TestMethod]
    public void TheIconsFollowTheWindowsOwnDpiOnceTheMenuIsUp()
    {
        Shown shown = Open(Base(Phase1Fixtures.NoDevice()), menu =>
        {
            menu.CursorDpi = () => 96;
            menu.WindowDpi = _ => 192;
        });
        Assert.AreEqual(192u, shown.IconDpi, "The cursor said 96 and the window said 192: the window wins.");
        Assert.AreEqual(new Size(32, 32), shown.ImageSize);
        Assert.IsTrue(Pair(shown).All(p => p.Drawn.BoxPx == 32), "The icons were redrawn at 32 px.");
    }

    [TestMethod]
    public void TheCheckColumnStaysAndTheImageColumnIsAdded()
    {
        Shown shown = Open(Base(Phase1Fixtures.NoDevice()));
        Assert.IsTrue(shown.CheckMargin, "The tick and the indeterminate mark keep their own column.");
        Assert.IsTrue(shown.ImageMargin, "The icons have a column of their own.");
    }
}
