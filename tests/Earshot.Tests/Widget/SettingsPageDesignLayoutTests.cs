using System.Drawing;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The design's settings figures, from the pure layout (no window, no drawing, a fixed-width text measure): the groups and
// their order, row heights at 100% and 150% text size, the surfaces, the expanders and the controls' sizes. "Row" figures are at
// 100% display scale; a single-line row is 20t + 12 for its control plus 8 above and below.
[TestClass]
public sealed class SettingsPageDesignLayoutTests
{
    // About half an em a character (0.47, near Segoe UI's average), and one line unless the text is wider than the room.
    private sealed class FixedMeasure : ICardTextMeasure
    {
        public int Width(string text, int pixelSize) => (int)Math.Ceiling(text.Length * pixelSize * 0.47);

        public int Lines(string text, int width, int pixelSize, int lineHeight) =>
            Math.Max(1, (int)Math.Ceiling(Width(text, pixelSize) / (double)Math.Max(1, width)));
    }

    private static SettingsLayout Layout(CardSettingsValues? values = null, int dpi = 96, double t = 1.0, bool more = true, bool order = false, bool caseCard = false) =>
        SettingsPageLayout.Compute(
            (values ?? FakeCardHost.Defaults()) with { MoreExpanded = more, GaugeOrderExpanded = order, CaseOpenCardExpanded = caseCard }, dpi, new FixedMeasure(), t);

    private static SettingsItem Row(SettingsLayout l, SettingsRowId id) => l.Items.First(i => i.Kind == SettingsItemKind.Row && i.Row == id && i.Tiles.Count == 0);

    private static readonly string[] Heads = ["Taskbar", "Behaviour", "Audio", "Shortcuts", "About"];

    private static readonly string[] ClosedRows =
        ["GaugeDisplay", "GaugeOrder", "CaseCard", "HandBack", "MicrophoneOff", "Connect", "Disconnect", "OpenCard", "About", "More"];

    private static readonly string[] MoreRows =
        ["GaugePosition", "OtherDevice", "PauseBud", "PauseLeave", "LowBattery", "FullyCharged", "LeftClick", "History", "CopyDiagnostics"];

    [TestMethod]
    public void TheGroupsAreTheOwnersAndMoreIsOneRowAfterThem()
    {
        SettingsLayout closed = Layout(more: false);
        string[] heads = closed.Items.Where(i => i.Kind == SettingsItemKind.Head).Select(i => i.Label).ToArray();
        CollectionAssert.AreEqual(Heads, heads);

        string[] rows = closed.Items.Where(i => i.Kind == SettingsItemKind.Row).Select(i => i.Row.ToString()).ToArray();
        CollectionAssert.AreEqual(ClosedRows, rows);

        string[] more = Layout(more: true).Items.Where(i => i.Kind == SettingsItemKind.Row).Skip(rows.Length).Select(i => i.Row.ToString()).ToArray();
        CollectionAssert.AreEqual(MoreRows, more);
    }

    [TestMethod]
    public void TheSoundSwitchHasNoRowAndRepairAndCheckAutomaticallyAreNotOnThePage()
    {
        SettingsLayout l = Layout(FakeCardHost.Defaults() with { HandsFreeMicrophoneOff = true, InstallExists = true });
        Assert.IsFalse(l.Items.Any(i => i.Row is SettingsRowId.Repair or SettingsRowId.CheckAutomatically), "They are on the updates page.");
        Assert.IsFalse(l.Items.Any(i => i.Label.Contains("Sound switch", StringComparison.OrdinalIgnoreCase)), "No documented API, so no row.");
    }

    [TestMethod]
    public void TheFullyChargedNoticeRowIsASwitchBoundToItsOwnRow()
    {
        SettingsLayout l = Layout();
        SettingsItem row = Row(l, SettingsRowId.FullyCharged);
        Assert.AreEqual("Fully charged notice", row.Label);
        Assert.IsTrue(l.Targets.Contains(new SettingsTarget(SettingsRowId.FullyCharged, SettingsPart.Toggle)));
        Assert.AreEqual(new Size(40, 20), row.A.Size, "A toggle is 40 by 20.");
    }

    // 100% display, 100% text: a single line 48, a line with a caption 56, the gauge order header 56.
    [TestMethod]
    public void RowHeightsAt100PercentTextAreTheTables()
    {
        SettingsLayout l = Layout(FakeCardHost.Defaults() with { InstalledVersion = "1.4.0" });
        foreach (SettingsItem row in l.Items.Where(i => i.Kind == SettingsItemKind.Row))
        {
            int expected = row.Row is SettingsRowId.GaugeOrder or SettingsRowId.HandBack or SettingsRowId.About ? 56 : 48;
            Assert.AreEqual(expected, row.Bounds.Height, row.Row.ToString());
        }
    }

    // 150% text: a single line is 20t + 12 = 42 for its control, so 58; a line with a caption 36t + 20 = 74.
    [TestMethod]
    public void RowHeightsAt150PercentTextGrowWithTheText()
    {
        SettingsLayout l = Layout(FakeCardHost.Defaults() with { InstalledVersion = "1.4.0" }, t: 1.5);
        Assert.AreEqual(58, Row(l, SettingsRowId.LeftClick).Bounds.Height);
        Assert.AreEqual(58, Row(l, SettingsRowId.GaugeDisplay).Bounds.Height);
        Assert.AreEqual(74, Row(l, SettingsRowId.HandBack).Bounds.Height, "A caption row: 24 + 24 + 20 ... label 30 and caption 24 plus 20.");
        Assert.AreEqual(42, Row(l, SettingsRowId.GaugeDisplay).A.Height, "A combo is 20t + 12 high.");
        Assert.AreEqual(56, Row(l, SettingsRowId.GaugeOrder).Bounds.Height, "The header holds the 40 px gauge: 56, whatever the text size.");
    }

    [TestMethod]
    public void SurfacesAre336WideTwelveFromTheSidesAndFourApartAndHoldTheirRows()
    {
        SettingsLayout l = Layout();
        Assert.AreEqual(360, l.Frame.Width);
        foreach (Rectangle s in l.Surfaces)
        {
            Assert.AreEqual(12, s.X);
            Assert.AreEqual(336, s.Width);
        }

        for (int i = 1; i < l.Surfaces.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(l.Surfaces[i - 1].Bottom + 4, l.Surfaces[i].Top, "Surfaces are at least 4 apart.");
        }

        foreach (SettingsItem row in l.Items.Where(i => i.Kind == SettingsItemKind.Row))
        {
            Assert.IsTrue(l.Surfaces.Any(s => s.Contains(row.Bounds)), row.Row + " sits on a surface.");
        }

        Assert.AreEqual(l.Frame.Body.Y + 8 + 4, l.Items.First(i => i.Kind == SettingsItemKind.Head).Bounds.Top, "Body padding 8, the first group's top 4.");
    }

    // Row padding 8 / 12 / 8 / 14: the icon is 14 into the surface and 16 wide, the label 12 after it, the controls end 12 short of the
    // surface's right edge.
    [TestMethod]
    public void RowPaddingIconAndGapAreTheDesigns()
    {
        SettingsLayout l = Layout();
        SettingsItem row = Row(l, SettingsRowId.LeftClick);
        Assert.AreEqual(12 + 14, row.IconRect.X);
        Assert.AreEqual(new Size(16, 16), row.IconRect.Size);
        Assert.AreEqual(row.IconRect.Right + 12, row.LabelRect.X);
        Assert.AreEqual(348 - 12, row.A.Right);
        Assert.AreEqual(row.Bounds.Top + 8, row.A.Y - ((32 - row.A.Height) / 2), "8 above a 32 control (the toggle is centred in it).");
    }

    [TestMethod]
    public void ComboAndButtonAre32HighAndAtLeast128WideAndPad12()
    {
        SettingsLayout l = Layout();
        SettingsItem display = Row(l, SettingsRowId.GaugeDisplay);
        Assert.AreEqual(32, display.A.Height);
        Assert.IsGreaterThanOrEqualTo(128, display.A.Width, "Combo min width 128.");

        SettingsItem copy = Row(l, SettingsRowId.CopyDiagnostics);
        Assert.AreEqual(32, copy.A.Height, "Button 32 high.");
        Assert.AreEqual(new FixedMeasure().Width("Copy", 14) + 24, copy.A.Width, "Padding 12 each side.");
    }

    [TestMethod]
    public void TheMoreRowIsOneSurfaceWithItsRowsSeparatedByARowStroke()
    {
        SettingsLayout l = Layout();
        SettingsItem more = Row(l, SettingsRowId.More);
        Rectangle surface = l.Surfaces.Single(s => s.Contains(more.Bounds));
        Assert.AreEqual(more.Bounds.Top, surface.Top, "The header row opens the surface.");
        SettingsItem last = Row(l, SettingsRowId.CopyDiagnostics);
        Assert.AreEqual(last.Bounds.Bottom, surface.Bottom, "And the last row closes it.");
        Assert.IsFalse(more.DividerAbove);
        foreach (string name in MoreRows)
        {
            var id = Enum.Parse<SettingsRowId>(name);
            Assert.IsTrue(Row(l, id).DividerAbove, id + " has a 1 px stroke above it.");
            Assert.IsTrue(surface.Contains(Row(l, id).Bounds));
        }
    }

    [TestMethod]
    public void TheGaugeOrderRowIsAnExpanderAThreeByTwoGridOfTilesWhenOpen()
    {
        SettingsLayout closed = Layout(order: false);
        Assert.IsFalse(closed.Items.Any(i => i.Tiles.Count > 0));
        SettingsItem header = Row(closed, SettingsRowId.GaugeOrder);
        Assert.AreEqual(56, header.Bounds.Height);
        Assert.AreEqual(new Size(74, 40), header.Preview.Size, "It shows the gauge as it is.");
        Assert.IsTrue(closed.Targets.Contains(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Expand)));

        SettingsLayout open = Layout(order: true);
        SettingsItem grid = open.Items.Single(i => i.Tiles.Count == 6);
        Assert.IsTrue(grid.DividerAbove);
        IReadOnlyList<Rectangle> tiles = grid.Tiles;
        Assert.AreEqual(52, tiles[0].Height, "Tiles are 52 high.");
        Assert.AreEqual(8, tiles[1].Left - tiles[0].Right, "8 between tiles across.");
        Assert.AreEqual(8, tiles[3].Top - tiles[0].Bottom, "And down.");
        Assert.AreEqual(tiles[0].Top, tiles[1].Top);
        Assert.AreEqual(tiles[0].Left, tiles[3].Left);
        Assert.AreEqual(grid.Bounds.Left + 12, tiles[0].Left, "Padding 12.");
        Assert.AreEqual(grid.Bounds.Right - 12, tiles[2].Right, "Padding 12.");
        Assert.AreEqual(grid.Bounds.Top + 12, tiles[0].Top);
        Assert.AreEqual(grid.Bounds.Bottom - 12, tiles[5].Bottom);
    }

    // The microphone guidance line is 16 high with padding 0 / 12 / 10 / 42: it starts at the label's x, ends 12 short of the surface
    // and leaves 10 under it.
    [TestMethod]
    public void TheMicrophoneGuidanceLineHasItsPaddingAndTheSoundSettingsRowIsInTheSameSurface()
    {
        SettingsLayout l = Layout(FakeCardHost.Defaults() with { HandsFreeMicrophoneOff = true, MicrophoneState = MicrophoneRowState.ConnectFirst });
        SettingsItem mic = Row(l, SettingsRowId.MicrophoneOff);
        Assert.AreEqual(WidgetCopy.MicConnectFirst, mic.Sub);
        Assert.AreEqual(12 + 42, mic.SubRect.X, "Left padding 42 from the surface.");
        Assert.AreEqual(348 - 12, mic.SubRect.Right, "Right padding 12.");
        Assert.AreEqual(16, mic.SubRect.Height, "A 16 high line.");
        Assert.AreEqual(8 + 32 + 16 + 10, mic.Bounds.Height, "Under the control row: 8 above, the 32 row, the line and 10 below.");

        SettingsItem sound = Row(l, SettingsRowId.SoundSettings);
        Assert.IsTrue(sound.DividerAbove);
        Assert.AreEqual(32, sound.A.Height, "The Open button is 32 high.");
        Assert.IsTrue(l.Surfaces.Any(s => s.Contains(mic.Bounds) && s.Contains(sound.Bounds)), "One surface.");
    }

    [TestMethod]
    public void TheCaseOpenCardRowHasItsOwnCloseAndDisplayChoicesInsideItsSurface()
    {
        SettingsLayout l = Layout(caseCard: true);
        SettingsItem card = Row(l, SettingsRowId.CaseCard);
        SettingsItem close = Row(l, SettingsRowId.CaseCardClose);
        SettingsItem displays = Row(l, SettingsRowId.CaseCardDisplays);
        Assert.IsTrue(close.DividerAbove && displays.DividerAbove);
        Assert.IsTrue(close.Nested && displays.Nested, "No icon of their own.");
        Assert.IsTrue(l.Surfaces.Any(s => s.Contains(card.Bounds) && s.Contains(close.Bounds) && s.Contains(displays.Bounds)));
        Assert.IsFalse(Layout(caseCard: false).Items.Any(i => i.Row == SettingsRowId.CaseCardClose), "Closed until opened.");
    }

    [TestMethod]
    public void NavigationRowsAreOneTargetWithAChevron()
    {
        SettingsLayout l = Layout(FakeCardHost.Defaults() with { InstalledVersion = "1.4.0" });
        foreach (SettingsRowId id in new[] { SettingsRowId.About, SettingsRowId.History })
        {
            SettingsItem row = Row(l, id);
            Assert.AreEqual(row.Bounds, row.A, "The whole row presses.");
            Assert.AreEqual(FluentGlyphs.ChevronRight, row.ChevronGlyph);
            Assert.IsFalse(row.Chevron.IsEmpty);
            Assert.IsTrue(l.Targets.Contains(new SettingsTarget(id, SettingsPart.Button)));
        }

        Assert.AreEqual("Version 1.4.0", Row(l, SettingsRowId.About).Sub, "The Updates row's caption is the version.");
        Assert.AreEqual("Updates", Row(l, SettingsRowId.About).Label);
    }

    [TestMethod]
    public void TheHandBackRowSaysWhenInItsCaption()
    {
        Assert.AreEqual("Shut down, sleep, Exit", Row(Layout(), SettingsRowId.HandBack).Sub);
    }

    [TestMethod]
    [DataRow(96, 1.0)]
    [DataRow(96, 1.5)]
    [DataRow(144, 1.0)]
    [DataRow(144, 1.5)]
    [DataRow(192, 2.25)]
    public void NothingOverlapsAndEveryControlStaysOnItsRowAtEveryScaleAndTextSize(int dpi, double t)
    {
        SettingsLayout l = Layout(FakeCardHost.Defaults() with { HandsFreeMicrophoneOff = true, InEarProofMissing = true, InstalledVersion = "1.4.0", ConnectFailure = "That shortcut is used by another program." }, dpi, t, order: true, caseCard: true);
        int previousBottom = l.Frame.Body.Top;
        foreach (SettingsItem item in l.Items)
        {
            Assert.IsGreaterThanOrEqualTo(previousBottom, item.Bounds.Top, item.Row + " overlaps the one above at " + dpi + " dpi, text " + t);
            previousBottom = item.Bounds.Bottom;
            Assert.IsLessThanOrEqualTo(l.Frame.Width, item.Bounds.Right);
            foreach (Rectangle control in new[] { item.A, item.B, item.Value }.Where(r => !r.IsEmpty))
            {
                Assert.IsTrue(item.Bounds.Contains(control), item.Row + " control leaves its row at " + dpi + " dpi, text " + t);
            }
        }

        Assert.IsLessThanOrEqualTo(l.Frame.Height, previousBottom);
        Assert.AreEqual(CardPlacement.Scale(360, dpi), l.Frame.Width, "The width does not grow with the text.");
    }
}
