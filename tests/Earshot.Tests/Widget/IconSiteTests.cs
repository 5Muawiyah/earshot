using System.Drawing;
using System.Text;
using Earshot.Icons;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Widget;

// Every place the card, its pages, the compact row and the send-to picker draw an icon (the icons spec, sections 1 and 2): the page is
// rendered into a bitmap with the icon log on, and each site must have drawn the icon the spec maps it to, in the style the spec
// gives it, in the ink, in the box and from the file that draws it; and the rendered pixels in that box must be exactly the icon's
// coverage mask composited in that ink over the pixels that were there, within one level of 255 a channel. The sites that are filled
// only under a condition are each tried with the condition on and off.
[TestClass]
public sealed class IconSiteTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    // The card is tried light at 100%, dark at 150% display scale and 150% text size, light at 200%, and in high contrast at 125%.
    private static readonly (DesignTheme Theme, int Dpi, double Text)[] Variants =
    [
        (DesignTheme.Light, 96, 1.0), (DesignTheme.Dark, 144, 1.5), (DesignTheme.Light, 192, 1.0), (DesignTheme.HighContrast, 120, 1.0),
    ];

    // Offset is where the page has been scrolled to, Visible the part of the bitmap the page shows in (an icon drawn outside it is
    // clipped, and is not looked at for pixels).
    private sealed record Scene(Bitmap Bitmap, Bitmap Background, IReadOnlyList<DrawnIcon> Log, CardColours Colours, WidgetCard? Card, int Dpi, double Text, Size Offset, Rectangle Visible);

    private static WidgetSnapshot Heard(TimeSpan age, AirPodsWhere where = AirPodsWhere.ThisPc)
    {
        DateTimeOffset at = Fixed - age;
        return CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = at }, new PartReading(60, false, null) { ReadAt = at }, at) with
        {
            Case = new PartReading(90, false, null) { ReadAt = at },
            Where = where,
        };
    }

    private static WidgetCardModel MainModel(WidgetSnapshot? snapshot = null) => CardKit.MainModel(snapshot ?? Heard(TimeSpan.FromSeconds(5))) with { Now = Fixed };

    // Renders what show lays out, with the icon log on and an opaque surface, and hands check the scene.
    private static void Run(
        (DesignTheme Theme, int Dpi, double Text) variant, bool unasked, Action<WidgetCard, int> show, Action<Scene> check, bool scrolled = false)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(variant.Theme, variant.Text, unasked);
            CardColours colours = CardColours.For(DesignPixels.Tokens(variant.Theme), DesignPixels.Accent(variant.Theme));
            card.OverrideBackgroundForCaptureOnly = Color.FromArgb(255, colours.Tokens.SolidSurface);
            show(card, variant.Dpi);
            IconPainter.DrawnIconLog = [];
            try
            {
                // The same page twice: once with every icon left out, for what is under them, once as it is drawn.
                IconPainter.PaintNothingForTest = true;
                using Bitmap background = CardKit.Render(card);
                IconPainter.PaintNothingForTest = false;
                IconPainter.DrawnIconLog.Clear();
                using Bitmap bitmap = CardKit.Render(card);
                var all = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                check(new Scene(
                    bitmap, background, IconPainter.DrawnIconLog, colours, card, variant.Dpi, variant.Text,
                    scrolled ? new Size(0, -card.SettingsScrollOffset) : Size.Empty, scrolled ? Rectangle.Intersect(all, card.SettingsViewport) : all));
            }
            finally
            {
                IconPainter.PaintNothingForTest = false;
                IconPainter.DrawnIconLog = null;
            }
        });
    }

    private static void Each(bool unasked, Action<WidgetCard, int> show, Action<Scene> check, bool scrolled = false)
    {
        foreach (var variant in Variants)
        {
            Run(variant, unasked, show, check, scrolled);
        }
    }

    // The tick of a finished compact row and of a notice is in the accent, and in the text colour in high contrast (WidgetCard.Compact.cs).
    private static Color TickInk(Scene s) => s.Colours.HighContrast ? s.Colours.Text : s.Colours.Accent;

    private static int Box(Scene s, int at96, bool textScaled = true) => TextFit.Grow(at96, s.Dpi, textScaled ? s.Text : 1.0);

    // The icon must have been drawn at least once with exactly these particulars, and every such draw must be pixel exact.
    private static DrawnIcon Expect(Scene s, Icon icon, IconStyle style, Color ink, int box, string file, Rectangle? bounds = null, IconRule rule = IconRule.A, int? step = null)
    {
        List<DrawnIcon> found = s.Log.Where(d =>
            d.Icon == icon && d.Style == style && d.Ink.ToArgb() == ink.ToArgb() && d.BoxPx == box && d.Rule == rule
            && d.Site.StartsWith(file + ":", StringComparison.Ordinal)
            && (step is null || d.RotationStep == step)
            && (bounds is null || d.Box == new Rectangle(IconPainter.BoxOrigin(bounds.Value, box), new Size(box, box)))).ToList();
        if (found.Count == 0)
        {
            var sb = new StringBuilder();
            sb.Append("No ").Append(style).Append(' ').Append(icon).Append(" of ").Append(box).Append(" px in ").Append(ink).Append(" from ").Append(file)
                .Append(bounds is { } b ? " in " + b : string.Empty).Append(". Drawn:");
            foreach (DrawnIcon d in s.Log)
            {
                sb.Append("\n  ").Append(d.Icon).Append(' ').Append(d.Style).Append(' ').Append(d.BoxPx).Append(" px ").Append(d.Ink).Append(' ').Append(d.Box).Append(' ').Append(d.Rule).Append(' ').Append(d.Site);
            }

            Assert.Fail(sb.ToString());
        }

        int verified = 0;
        foreach (DrawnIcon d in found)
        {
            var onScreen = new Rectangle(d.Box.X + s.Offset.Width, d.Box.Y + s.Offset.Height, d.Box.Width, d.Box.Height);
            if (s.Visible.Contains(Rectangle.Inflate(onScreen, IconRaster.Apron, IconRaster.Apron)))
            {
                AssertPixelExact(s, d);
                verified++;
            }
        }

        Assert.IsGreaterThan(0, verified, icon + " " + style + " was drawn but none of it is in view to be looked at.");
        return found[0];
    }

    private static void NotDrawn(Scene s, Icon icon, string why) =>
        Assert.IsFalse(s.Log.Any(d => d.Icon == icon), why);

    // The mask in ink over the page rendered without its icons, in the box and its one pixel apron: every pixel, those the mask leaves
    // alone included.
    private static void AssertPixelExact(Scene s, DrawnIcon d)
    {
        ReadOnlySpan<byte> mask = IconRaster.Coverage(d.Icon, d.Style, d.BoxPx, d.Rule, d.RotationStep).Span;
        int side = IconRaster.MaskSide(d.BoxPx);
        int left = d.Box.X - IconRaster.Apron + s.Offset.Width;
        int top = d.Box.Y - IconRaster.Apron + s.Offset.Height;
        int inked = 0;
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int alpha = ((mask[(y * side) + x] * d.Ink.A) + 127) / 255;
                Color px = s.Bitmap.GetPixel(left + x, top + y);
                Color bg = s.Background.GetPixel(left + x, top + y);
                int Over(int src, int dst) => ((src * alpha) + (dst * (255 - alpha)) + 127) / 255;
                int worst = Math.Max(Math.Abs(px.R - Over(d.Ink.R, bg.R)), Math.Max(Math.Abs(px.G - Over(d.Ink.G, bg.G)), Math.Abs(px.B - Over(d.Ink.B, bg.B))));
                Assert.IsLessThanOrEqualTo(
                    1, worst,
                    d.Icon + " " + d.Style + " at " + d.Box + ", pixel " + (left + x) + "," + (top + y) + ": drawn " + px + ", the mask in " + d.Ink + " over " + bg + " is "
                    + Color.FromArgb(Over(d.Ink.R, bg.R), Over(d.Ink.G, bg.G), Over(d.Ink.B, bg.B)));
                inked += mask[(y * side) + x] > 0 ? 1 : 0;
            }
        }

        Assert.IsGreaterThan(0, inked, d.Icon + " at " + d.Box + " has no pixel.");
    }

    // What is on the page, not what the mask says: the rows and columns in the box and its apron where the page differs from the same
    // page without its icons by more than a quarter in some channel. A bud has to be whole and in its place there: its top and bottom
    // are the pair's art rows (the grid's 2 to 22 is the box's height, spec section 4) to within a pixel, and none of it is cut by the
    // edge of the mask, which is the apron's outer pixel.
    private static void AssertBudIsWholeAndInPlace(Scene s, DrawnIcon d)
    {
        RectangleF art = IconCatalogue.Outline(Icon.AirPods, d.Style).Bounds();
        double wantTop = d.Box.Y + s.Offset.Height + ((art.Y - 2.0) / 20.0 * d.BoxPx);
        double wantBottom = d.Box.Y + s.Offset.Height + ((art.Bottom - 2.0) / 20.0 * d.BoxPx);
        int left = d.Box.X + s.Offset.Width - IconRaster.Apron;
        int top = d.Box.Y + s.Offset.Height - IconRaster.Apron;
        int side = IconRaster.MaskSide(d.BoxPx);
        int first = int.MaxValue, last = -1;
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                Color px = s.Bitmap.GetPixel(left + x, top + y);
                Color bg = s.Background.GetPixel(left + x, top + y);
                if (Math.Max(Math.Abs(px.R - bg.R), Math.Max(Math.Abs(px.G - bg.G), Math.Abs(px.B - bg.B))) > 64)
                {
                    first = Math.Min(first, y);
                    last = Math.Max(last, y);
                    Assert.IsTrue(x is > 0 && x < side - 1 && y is > 0 && y < side - 1, d.Icon + " " + d.Style + " at " + d.Box + ": ink on the edge of its mask at " + x + "," + y);
                }
            }
        }

        Assert.IsGreaterThan(-1, last, d.Icon + " " + d.Style + " at " + d.Box + ": nothing on the page");
        Assert.AreEqual(wantTop, top + first, 1.0, d.Icon + " " + d.Style + " at " + d.Box + ": the bud starts on the pair's top row");
        Assert.AreEqual(wantBottom, top + last + 1, 1.0, d.Icon + " " + d.Style + " at " + d.Box + ": the bud ends on the pair's bottom row");
    }

    // ---- The main page

    [TestMethod]
    public void TheTitleRowGearIsTheSettingsLineIcon() => Each(false, (c, dpi) => c.Render(MainModel(), dpi), s =>
        Expect(s, Icon.Settings, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs", s.Card!.CurrentMainLayout.Gear));

    [TestMethod]
    public void TheRefreshIconIsTheRefreshLineIconInTheTextInk() => Each(false, (c, dpi) => c.Render(MainModel(), dpi), s =>
        Expect(s, Icon.Refresh, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.Battery.cs", s.Card!.CurrentMainLayout.Refresh, step: 0));

    [TestMethod]
    public void TheRefreshIconSpinsByBakingItsTurnIntoTheMaskInTheAccent() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Refresh = new BatteryRefreshView(Reading: true, SpinFrame: 3) }, dpi),
        s => Expect(s, Icon.Refresh, IconStyle.Line, s.Colours.Accent, Box(s, 16), "WidgetCard.Battery.cs", s.Card!.CurrentMainLayout.Refresh, step: 3));

    [TestMethod]
    public void TheRefreshIconIsDimWhileBluetoothIsOff() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.BluetoothOff) }, dpi),
        s => Expect(s, Icon.Refresh, IconStyle.Line, s.Colours.TextDisabled, Box(s, 16), "WidgetCard.Battery.cs", s.Card!.CurrentMainLayout.Refresh));

    [TestMethod]
    public void TheStatusRowComputerIsFilledOnlyWhileTheAirPodsAreOnThisPc()
    {
        Each(false, (c, dpi) => c.Render(MainModel(Heard(TimeSpan.FromSeconds(5), AirPodsWhere.ThisPc)), dpi), s =>
            Expect(s, Icon.Computer, IconStyle.Filled, s.Colours.Text, Box(s, 16), "WidgetCard.cs"));
        Each(false, (c, dpi) => c.Render(MainModel(Heard(TimeSpan.FromSeconds(5), AirPodsWhere.NotInUse)), dpi), s =>
        {
            Expect(s, Icon.Computer, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs");
            Assert.IsFalse(s.Log.Any(d => d.Icon == Icon.Computer && d.Style == IconStyle.Filled), "Not on this PC: the line computer.");
        });
    }

    [TestMethod]
    public void TheStatusRowPhoneIsTheCellphoneLineIconWhenTheAirPodsAreOnTheOtherDevice() => Each(false,
        (c, dpi) => c.Render(MainModel(Heard(TimeSpan.FromSeconds(5), AirPodsWhere.Elsewhere)), dpi), s =>
        {
            Expect(s, Icon.Cellphone, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs");
            NotDrawn(s, Icon.Computer, "On the other device: no computer.");
        });

    [TestMethod]
    public void BluetoothOffHasItsOwnIconAndAChevronToTheSettings() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Radio = CardRadio.Off }, dpi), s =>
        {
            Expect(s, Icon.BluetoothOff, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs");
            Expect(s, Icon.ChevronRight, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12), "WidgetCard.cs");
        });

    [TestMethod]
    public void ABluetoothThatIsNotWorkingIsTheCircledMarkInCaution() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Radio = CardRadio.NotWorking }, dpi),
        s => Expect(s, Icon.Warning, IconStyle.Line, s.Colours.Caution, Box(s, 16), "WidgetCard.cs"));

    [TestMethod]
    public void NoAirPodsPairedIsTheBluetoothLineIcon() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Radio = CardRadio.NoAirPodsPaired }, dpi),
        s => Expect(s, Icon.Bluetooth, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs"));

    [TestMethod]
    public void ARefreshThatHeardNothingIsTheAlertTriangleInCaution() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.NothingHeard) }, dpi),
        s => Expect(s, Icon.Alert, IconStyle.Line, s.Colours.Caution, Box(s, 16), "WidgetCard.cs"));

    [TestMethod]
    public void TheSendToRowIsTheSendLineIconWithAChevron() => Each(false,
        (c, dpi) => c.Render(MainModel() with { ShowSendTo = true }, dpi), s =>
        {
            Expect(s, Icon.Send, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs");
            Expect(s, Icon.ChevronRight, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12), "WidgetCard.cs");
        });

    [TestMethod]
    public void TheSwitchRowAirPodsAreFilledOnlyWhilePauseIsOn()
    {
        Each(false, (c, dpi) => c.Render(MainModel() with { ShowSwitch = true, AutoPauseOn = true }, dpi), s =>
            Expect(s, Icon.AirPods, IconStyle.Filled, s.Colours.Text, Box(s, 16), "WidgetCard.cs"));
        Each(false, (c, dpi) => c.Render(MainModel() with { ShowSwitch = true, AutoPauseOn = false }, dpi), s =>
        {
            Expect(s, Icon.AirPods, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.cs");
            Assert.IsFalse(s.Log.Any(d => d.Icon == Icon.AirPods && d.Style == IconStyle.Filled));
        });
    }

    [TestMethod]
    public void TheUpdateLineHasTheDownloadLineIconAtTheFixedSize() => Each(false,
        (c, dpi) => c.Render(MainModel() with { UpdateVersion = "1.8.0" }, dpi),
        s => Expect(s, Icon.Download, IconStyle.Line, s.Colours.TextSecondary, Box(s, 16, textScaled: false), "WidgetCard.cs"));

    [TestMethod]
    public void ALastReadingHasTheClockInTheTertiaryInk() => Each(false,
        (c, dpi) => c.Render(MainModel(Heard(TimeSpan.FromMinutes(4))), dpi),
        s => Expect(s, Icon.Time, IconStyle.Line, s.Colours.TextTertiary, Box(s, 12), "WidgetCard.cs"));

    [TestMethod]
    public void AChargingLiveFigureHasTheFilledBoltAndALowOneHasItInCaution()
    {
        Each(false, (c, dpi) => c.Render(MainModel(), dpi), s =>
            Expect(s, Icon.Lightning, IconStyle.Filled, s.Colours.Text, Box(s, 12), "WidgetCard.cs"));
        Each(false, (c, dpi) => c.Render(MainModel() with { LowBatteryThresholdPercent = 80 }, dpi), s =>
            Expect(s, Icon.Lightning, IconStyle.Filled, s.Colours.Caution, Box(s, 12), "WidgetCard.cs"));
    }

    [TestMethod]
    public void TheBudsAndTheCaseAreFilledOnlyForALiveReadingOnThisPc()
    {
        Each(false, (c, dpi) => c.Render(MainModel(), dpi), s =>
        {
            WidgetCardLayout.Layout layout = s.Card!.CurrentMainLayout;
            AssertBudIsWholeAndInPlace(s, Expect(s, Icon.AirPodsLeft, IconStyle.Filled, s.Colours.Text, Math.Min(layout.Left.Glyph.Width, layout.Left.Glyph.Height), "WidgetCard.cs", layout.Left.Glyph));
            AssertBudIsWholeAndInPlace(s, Expect(s, Icon.AirPodsRight, IconStyle.Filled, s.Colours.Text, Math.Min(layout.Right.Glyph.Width, layout.Right.Glyph.Height), "WidgetCard.cs", layout.Right.Glyph));
            Expect(s, Icon.AirPodsCase, IconStyle.Filled, s.Colours.Text, Math.Min(layout.Case.Glyph.Width, layout.Case.Glyph.Height), "WidgetCard.cs", layout.Case.Glyph);
        });
        Each(false, (c, dpi) => c.Render(MainModel(Heard(TimeSpan.FromMinutes(4))), dpi), s =>
        {
            WidgetCardLayout.Layout layout = s.Card!.CurrentMainLayout;
            AssertBudIsWholeAndInPlace(s, Expect(s, Icon.AirPodsLeft, IconStyle.Line, s.Colours.Text, Math.Min(layout.Left.Glyph.Width, layout.Left.Glyph.Height), "WidgetCard.cs", layout.Left.Glyph));
            AssertBudIsWholeAndInPlace(s, Expect(s, Icon.AirPodsRight, IconStyle.Line, s.Colours.Text, Math.Min(layout.Right.Glyph.Width, layout.Right.Glyph.Height), "WidgetCard.cs", layout.Right.Glyph));
            Expect(s, Icon.AirPodsCase, IconStyle.Line, s.Colours.Text, Math.Min(layout.Case.Glyph.Width, layout.Case.Glyph.Height), "WidgetCard.cs", layout.Case.Glyph);
        });
        Each(false, (c, dpi) => c.Render(MainModel(Heard(TimeSpan.FromSeconds(5), AirPodsWhere.Elsewhere)), dpi), s =>
        {
            WidgetCardLayout.Layout layout = s.Card!.CurrentMainLayout;
            Expect(s, Icon.AirPodsLeft, IconStyle.Line, s.Colours.Text, Math.Min(layout.Left.Glyph.Width, layout.Left.Glyph.Height), "WidgetCard.cs", layout.Left.Glyph);
            Expect(s, Icon.AirPodsCase, IconStyle.Line, s.Colours.Text, Math.Min(layout.Case.Glyph.Width, layout.Case.Glyph.Height), "WidgetCard.cs", layout.Case.Glyph);
        });
    }

    [TestMethod]
    public void APartWithNoValueHasItsMarkInTheDisabledInk() => Each(false,
        (c, dpi) => c.Render(CardKit.MainModel(CardKit.Snapshot()) with { Now = Fixed }, dpi), s =>
        {
            WidgetCardLayout.Layout layout = s.Card!.CurrentMainLayout;
            Expect(s, Icon.AirPodsRight, IconStyle.Line, s.Colours.TextDisabled, Math.Min(layout.Right.Glyph.Width, layout.Right.Glyph.Height), "WidgetCard.cs", layout.Right.Glyph);
        });

    [TestMethod]
    public void TheCardNobodyAskedForHasAClosePlaceOfTheGear() => Each(true, (c, dpi) => c.Render(MainModel(), dpi), s =>
    {
        Expect(s, Icon.Close, IconStyle.Line, s.Colours.Text, Box(s, 10), "WidgetCard.cs", s.Card!.CurrentMainLayout.Gear);
        NotDrawn(s, Icon.Settings, "An unasked card has no gear.");
    });

    // ---- The updates page, the sub-page frame and the history page

    private static WidgetCardModel UpdatesPage(bool autoCheck) =>
        CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.Idle), autoCheck: autoCheck, showRepair: true, version: "1.7.0");

    [TestMethod]
    public void TheUpdatesPageRowsHaveTheirIcons() => Each(false, (c, dpi) => c.Render(UpdatesPage(false), dpi), s =>
    {
        Expect(s, Icon.Sync, IconStyle.Line, s.Colours.Text, Box(s, 16, textScaled: false), "WidgetCard.cs");
        Expect(s, Icon.Information, IconStyle.Line, s.Colours.Text, Box(s, 16, textScaled: false), "WidgetCard.cs");
        Expect(s, Icon.Tool, IconStyle.Line, s.Colours.Text, Box(s, 16, textScaled: false), "WidgetCard.cs");
        Expect(s, Icon.Bluetooth, IconStyle.Line, s.Colours.Text, Box(s, 16, textScaled: false), "WidgetCard.cs");
        Expect(s, Icon.ExternalLink, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12, textScaled: false), "WidgetCard.cs");
    });

    [TestMethod]
    public void TheCheckAutomaticallyRowOnTheUpdatesPageIsFilledOnlyWhileItIsOn()
    {
        Each(false, (c, dpi) => c.Render(UpdatesPage(true), dpi), s =>
            Expect(s, Icon.Sync, IconStyle.Filled, s.Colours.Text, Box(s, 16, textScaled: false), "WidgetCard.cs"));
        Each(false, (c, dpi) => c.Render(UpdatesPage(false), dpi), s =>
            Assert.IsFalse(s.Log.Any(d => d.Icon == Icon.Sync && d.Style == IconStyle.Filled)));
    }

    [TestMethod]
    public void TheUpdatesPageTileHasTheAirPodsLineIconOnTheAccent() => Each(false, (c, dpi) => c.Render(UpdatesPage(false), dpi), s =>
        Expect(s, Icon.AirPods, IconStyle.Line, s.Colours.OnAccent, TextFit.Grow(20, s.Dpi, 1.0), "WidgetCard.cs", s.Card!.CurrentSetupLayout!.Tile));

    [TestMethod]
    public void TheBackButtonIsTheBackLineIcon() => Each(false, (c, dpi) => c.Render(UpdatesPage(false), dpi), s =>
        Expect(s, Icon.Back, IconStyle.Line, s.Colours.Text, Box(s, 16, textScaled: false), "SubPageFrame.cs", s.Card!.CurrentSetupLayout!.Frame.Back));

    private static WidgetCardModel StatusPage(SetupIcon icon) => new(
        CardKit.Snapshot(), false, false, true, true, "iPhone", Fixed, WidgetCardView.Update,
        new SetupViewModel("Page", null, null, null, icon, "Status", null, Array.Empty<SetupButton>(), 0));

    [TestMethod]
    public void TheSubPageStatusIconsAreTheirMappedIconsAtTheWholeGridBox() => Each(false, (c, dpi) => c.Render(StatusPage(SetupIcon.Check), dpi), s =>
        Expect(s, Icon.CheckCircle, IconStyle.Line, s.Colours.Accent, TextFit.Grow(20, s.Dpi, 1.0), "SubPageFrame.cs", s.Card!.CurrentSetupLayout!.StatusIcon, IconRule.B));

    [TestMethod]
    public void TheSubPageCautionIconIsTheAlertTriangleInCaution() => Each(false, (c, dpi) => c.Render(StatusPage(SetupIcon.Caution), dpi), s =>
        Expect(s, Icon.Alert, IconStyle.Line, s.Colours.Caution, TextFit.Grow(20, s.Dpi, 1.0), "SubPageFrame.cs", s.Card!.CurrentSetupLayout!.StatusIcon, IconRule.B));

    [TestMethod]
    public void TheSubPageDownloadIconIsTheDownloadLineIcon() => Each(false, (c, dpi) => c.Render(StatusPage(SetupIcon.Down), dpi), s =>
        Expect(s, Icon.Download, IconStyle.Line, s.Colours.Text, TextFit.Grow(20, s.Dpi, 1.0), "SubPageFrame.cs", s.Card!.CurrentSetupLayout!.StatusIcon, IconRule.B));

    [TestMethod]
    public void TheSubPageShieldIsTheSafeShieldLineIcon() => Each(false, (c, dpi) => c.Render(StatusPage(SetupIcon.Shield), dpi), s =>
        Expect(s, Icon.SafeShield2, IconStyle.Line, s.Colours.Text, TextFit.Grow(20, s.Dpi, 1.0), "SubPageFrame.cs", s.Card!.CurrentSetupLayout!.StatusIcon, IconRule.B));

    [TestMethod]
    public void TheHistoryStepButtonsAreTheArrowLineIconsAndDimWhenTheyCannotStep() => Each(false,
        (c, dpi) => c.Render(CardKit.HistoryModel(HistoryDays.View(0, Fixed, TimeZoneInfo.Utc, null)), dpi), s =>
        {
            HistoryPageLayout page = s.Card!.CurrentSetupLayout!.History!;
            Expect(s, Icon.ChevronLeft, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.History.cs", page.DayBack);
            Expect(s, Icon.ChevronRight, IconStyle.Line, s.Colours.TextDisabled, Box(s, 16), "WidgetCard.History.cs", page.DayForward);
        });

    // ---- The compact row and a carried notice

    [TestMethod]
    public void TheMovedToRowHasTheCellphoneAndTheTakeBackComputer() => Each(true,
        (c, dpi) => c.Render(CardKit.MainModel() with { Form = CardForm.Compact, Compact = CompactContents.MovedTo("iPhone"), Now = Fixed }, dpi), s =>
        {
            CompactLayoutRects r = CompactLayoutRects.Of(s);
            Expect(s, Icon.Cellphone, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.Compact.cs", r.Glyph);
            Expect(s, Icon.Computer, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.Compact.cs", r.Action);
        });

    [TestMethod]
    public void TheMovedToRowWithNoNameHasTheDeviceIcon() => Each(true,
        (c, dpi) => c.Render(CardKit.MainModel() with { Form = CardForm.Compact, Compact = CompactContents.MovedTo(string.Empty), Now = Fixed }, dpi),
        s => Expect(s, Icon.Device, IconStyle.Line, s.Colours.Text, Box(s, 16), "WidgetCard.Compact.cs", CompactLayoutRects.Of(s).Glyph));

    [TestMethod]
    public void TheLowBatteryRowHasTheAlertTriangleInCautionAndTheChargedRowATickInTheAccent()
    {
        Each(true, (c, dpi) => c.Render(CardKit.MainModel() with { Form = CardForm.Compact, Compact = CompactContents.LowBattery("Left", 20), Now = Fixed }, dpi), s =>
            Expect(s, Icon.Alert, IconStyle.Line, s.Colours.Caution, Box(s, 16), "WidgetCard.Compact.cs", CompactLayoutRects.Of(s).Glyph));
        Each(true, (c, dpi) => c.Render(CardKit.MainModel() with { Form = CardForm.Compact, Compact = CompactContents.FullyCharged(), Now = Fixed }, dpi), s =>
            Expect(s, Icon.Check, IconStyle.Line, TickInk(s), Box(s, 16), "WidgetCard.Compact.cs", CompactLayoutRects.Of(s).Glyph));
    }

    [TestMethod]
    public void ABuiltCardRowTickIsTheCheckLineIconInTheAccent() => Each(true,
        (c, dpi) => c.Render(CardKit.MainModel() with { Form = CardForm.Compact, Compact = CompactContents.MovedTo("iPhone"), CompactState = CompactActionState.Done, Now = Fixed }, dpi),
        s => Expect(s, Icon.Check, IconStyle.Line, TickInk(s), Box(s, 16), "WidgetCard.Compact.cs", CompactLayoutRects.Of(s).Action));

    [TestMethod]
    public void ANoticeOnTheStatusRowOfTheAskedCardHasItsIcon() => Each(false,
        (c, dpi) => c.Render(MainModel() with { Notice = CompactContents.LowBattery("Left", 20) }, dpi),
        s => Expect(s, Icon.Alert, IconStyle.Line, s.Colours.Caution, Box(s, 16), "WidgetCard.Notice.cs"));

    [TestMethod]
    public void ANoticeOnTheStripUnderASubPageHasItsIcon() => Each(false,
        (c, dpi) => c.Render(UpdatesPage(false) with { Notice = CompactContents.FullyCharged() }, dpi),
        s => Expect(s, Icon.Check, IconStyle.Line, TickInk(s), Box(s, 16), "WidgetCard.Notice.cs"));

    // ---- The settings page

    private static CardSettingsValues Toggles(bool on) => FakeCardHost.Defaults() with
    {
        PauseWhenBudComesOut = on, PauseWhenAirPodsLeave = on, LeftClickConnects = on, HandBack = on, CheckAutomatically = on,
        HandsFreeMicrophoneOff = on, CaseOpenCardOn = on, FullyChargedNotice = on, MovedToOn = on, SendToOnCard = on, SendToOnGauge = on,
        InstallExists = true,
    };

    // Each row of the settings page with a switch and its icon (Check automatically and Repair are rows of the updates page, tried there).
    public static IEnumerable<object[]> ToggleRows()
    {
        foreach (bool on in new[] { true, false })
        {
            yield return ["PauseBud", "AirPods", on];
            yield return ["PauseLeave", "Pause", on];
            yield return ["LeftClick", "Mouse", on];
            yield return ["HandBack", "Power", on];
            yield return ["MicrophoneOff", "MicOff", on];
            yield return ["CaseCard", "AirPodsCase", on];
            yield return ["FullyCharged", "Lightning", on];
            yield return ["MovedTo", "Cellphone", on];
            yield return ["SendToCard", "Send", on];
            yield return ["SendToGauge", "Send", on];
        }
    }

    public static IEnumerable<object[]> PlainRows()
    {
        yield return ["GaugePosition", "LayoutBottom"];
        yield return ["GaugeDisplay", "Monitor"];
        yield return ["GaugeOrder", "Transfer"];
        yield return ["OtherDevice", "Cellphone"];
        yield return ["LowBattery", "Notification"];
        yield return ["Connect", "Keyboard"];
        yield return ["Disconnect", "Keyboard"];
        yield return ["OpenCard", "Keyboard"];
        yield return ["SendTo", "Keyboard"];
        yield return ["SoundSettings", "Volume"];
        yield return ["History", "History"];
        yield return ["CopyDiagnostics", "Copy"];
        yield return ["More", "More"];
        yield return ["About", "Sync"];
    }

    // The settings page rendered with the row at the top of the body, so that it is in view whatever the card's height.
    private static void ShowRow(WidgetCard card, int dpi, CardSettingsValues values, SettingsRowId row, bool more = true, bool order = false)
    {
        CardKit.RenderSettings(card, CardKit.SettingsModel(values), dpi, more, order);
        SettingsItem item = CardKit.Row(card, row);
        card.ScrollSettingsToForTest(item.Bounds.Top - card.CurrentSettingsLayout!.Frame.Body.Y);
    }

    [TestMethod]
    [DynamicData(nameof(ToggleRows))]
    public void ASettingsRowWithASwitchIsFilledExactlyWhileItIsOn(string rowName, string iconName, bool on)
    {
        var row = Enum.Parse<SettingsRowId>(rowName);
        var icon = Enum.Parse<Icon>(iconName);
        Each(
            false,
            (c, dpi) => ShowRow(c, dpi, Toggles(on), row),
            s =>
            {
                SettingsItem item = CardKit.Row(s.Card!, row);
                int box = Box(s, 16, textScaled: false);
                Expect(s, icon, on ? IconStyle.Filled : IconStyle.Line, s.Colours.Text, box, "WidgetCardSettingsPage.cs", item.IconRect);
                Assert.IsFalse(
                    s.Log.Any(d => d.Icon == icon && d.Style == (on ? IconStyle.Line : IconStyle.Filled) && d.Box == new Rectangle(IconPainter.BoxOrigin(item.IconRect, box), new Size(box, box))),
                    row + ": the other style was drawn in the row's box.");
            },
            scrolled: true);
    }

    [TestMethod]
    [DynamicData(nameof(PlainRows))]
    public void ASettingsRowWithNoSwitchIsAlwaysItsLineIcon(string rowName, string iconName)
    {
        var row = Enum.Parse<SettingsRowId>(rowName);
        var icon = Enum.Parse<Icon>(iconName);
        Each(
            false,
            (c, dpi) => ShowRow(c, dpi, Toggles(true), row),
            s => Expect(s, icon, IconStyle.Line, s.Colours.Text, Box(s, 16, textScaled: false), "WidgetCardSettingsPage.cs", CardKit.Row(s.Card!, row).IconRect),
            scrolled: true);
    }

    [TestMethod]
    public void TheNavigationRowsOfTheSettingsPageEndInAChevronRight()
    {
        foreach (SettingsRowId row in new[] { SettingsRowId.History, SettingsRowId.About })
        {
            Each(false, (c, dpi) => ShowRow(c, dpi, Toggles(true), row), s =>
                Expect(s, Icon.ChevronRight, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12, textScaled: false), "WidgetCardSettingsPage.cs", CardKit.Row(s.Card!, row).Chevron), scrolled: true);
        }
    }

    [TestMethod]
    public void TheExpandingRowsOfTheSettingsPageHaveADownChevronClosedAndAnUpChevronOpen()
    {
        foreach (SettingsRowId row in new[] { SettingsRowId.More, SettingsRowId.GaugeOrder })
        {
            Each(false, (c, dpi) => ShowRow(c, dpi, Toggles(true), row, more: false, order: false), s =>
                Expect(s, Icon.ChevronDown, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12, textScaled: false), "WidgetCardSettingsPage.cs", CardKit.Row(s.Card!, row).Chevron), scrolled: true);
            Each(false, (c, dpi) => ShowRow(c, dpi, Toggles(true), row, more: true, order: true), s =>
                Expect(s, Icon.ChevronUp, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12, textScaled: false), "WidgetCardSettingsPage.cs", CardKit.Row(s.Card!, row).Chevron), scrolled: true);
        }
    }

    [TestMethod]
    public void TheCaseCardRowHasADownChevronClosedAndAnUpChevronOpen()
    {
        Each(false, (c, dpi) => ShowRow(c, dpi, Toggles(true), SettingsRowId.CaseCard), s =>
            Expect(s, Icon.ChevronDown, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12, textScaled: false), "WidgetCardSettingsPage.cs", CardKit.Row(s.Card!, SettingsRowId.CaseCard).B), scrolled: true);
        Each(
            false,
            (c, dpi) =>
            {
                CardKit.RenderSettings(c, CardKit.SettingsModel(Toggles(true) with { CaseOpenCardExpanded = true }), dpi, more: true, order: false);
                c.ExpandSettingsRowsForCapture(more: true, gaugeOrder: false, caseOpenCard: true);
                c.ScrollSettingsToForTest(CardKit.Row(c, SettingsRowId.CaseCard).Bounds.Top - c.CurrentSettingsLayout!.Frame.Body.Y);
            },
            s => Expect(s, Icon.ChevronUp, IconStyle.Line, s.Colours.TextSecondary, Box(s, 12, textScaled: false), "WidgetCardSettingsPage.cs", CardKit.Row(s.Card!, SettingsRowId.CaseCard).B),
            scrolled: true);
    }

    [TestMethod]
    public void TheOpenButtonOfTheSoundRowHasTheExternalLinkIconInTheTextInk() => Each(false,
        (c, dpi) => ShowRow(c, dpi, Toggles(true), SettingsRowId.SoundSettings),
        s => Expect(s, Icon.ExternalLink, IconStyle.Line, s.Colours.Text, Box(s, 12, textScaled: false), "SubPageFrame.cs"), scrolled: true);

    [TestMethod]
    public void TheLowBatteryStepperHasTheMinusAndPlusLineIcons() => Each(false,
        (c, dpi) => ShowRow(c, dpi, Toggles(true), SettingsRowId.LowBattery),
        s =>
        {
            Expect(s, Icon.Minimize, IconStyle.Line, s.Colours.Text, Box(s, 10, textScaled: false), "SubPageFrame.cs");
            Expect(s, Icon.Add, IconStyle.Line, s.Colours.Text, Box(s, 10, textScaled: false), "SubPageFrame.cs");
        }, scrolled: true);

    [TestMethod]
    public void AClearButtonHasTheCloseLineIconInTheSecondaryInk() => Each(false,
        (c, dpi) => ShowRow(c, dpi, Toggles(true), SettingsRowId.Connect),
        s => Expect(s, Icon.Close, IconStyle.Line, s.Colours.TextSecondary, Box(s, 16, textScaled: false), "SubPageFrame.cs", CardKit.Row(s.Card!, SettingsRowId.Connect).B), scrolled: true);

    [TestMethod]
    public void AClearButtonWithNothingToClearIsInTheDisabledInk() => Each(false,
        (c, dpi) => ShowRow(c, dpi, Toggles(true), SettingsRowId.OpenCard),
        s => Expect(s, Icon.Close, IconStyle.Line, s.Colours.TextDisabled, Box(s, 16, textScaled: false), "SubPageFrame.cs", CardKit.Row(s.Card!, SettingsRowId.OpenCard).B), scrolled: true);

    [TestMethod]
    public void ATickedDisplayBoxHasTheCheckLineIconOnTheAccent() => Each(false,
        (c, dpi) =>
        {
            CardSettingsValues values = Toggles(true) with
            {
                CaseOpenCardExpanded = true,
                CaseOpenCardDisplayOptions = [new DisplayOption("a", "Display 1"), new DisplayOption("b", "Display 2")],
                CaseOpenCardShownOn = ["a"],
            };
            CardKit.RenderSettings(c, CardKit.SettingsModel(values), dpi, more: true, order: false);
            c.ExpandSettingsRowsForCapture(more: true, gaugeOrder: false, caseOpenCard: true);
            SettingsItem box = c.CurrentSettingsLayout!.Items.First(i => i.Row == SettingsRowId.CaseCardDisplay);
            c.ScrollSettingsToForTest(box.Bounds.Top - c.CurrentSettingsLayout.Frame.Body.Y);
        },
        s => Expect(s, Icon.Check, IconStyle.Line, s.Colours.OnAccent, Box(s, 16, textScaled: false), "SubPageFrame.cs"), scrolled: true);

    // ---- The send-to picker

    private static void Picker(AirPodsWhere where, Action<Scene, SendToLayout.Layout> check)
    {
        Phase5.CardSta.Run(() =>
        {
            var accent = new FakeAccent();
            using var picker = new SendToPicker(new CapturingLog(), accent);
            picker.SetTheme(Color.Black, highContrast: false);
            Size size = picker.Prepare(SendToModel.From(where, "iPhone", busy: false), 96, openedByKeyboard: false);
            CardColours colours = CardColours.For(DesignTokens.For(false, false), accent.Light);
            IconPainter.DrawnIconLog = [];
            try
            {
                using var background = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                IconPainter.PaintNothingForTest = true;
                using (Graphics g = Graphics.FromImage(background))
                {
                    picker.RenderContent(g);
                }

                IconPainter.PaintNothingForTest = false;
                IconPainter.DrawnIconLog.Clear();
                using var bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    picker.RenderContent(g);
                }

                check(new Scene(bitmap, background, IconPainter.DrawnIconLog, colours, null, 96, 1.0, Size.Empty, new Rectangle(0, 0, bitmap.Width, bitmap.Height)), picker.CurrentLayout);
            }
            finally
            {
                IconPainter.PaintNothingForTest = false;
                IconPainter.DrawnIconLog = null;
            }
        });
    }

    [TestMethod]
    public void ThePickerHeaderAndRowsHaveTheirIconsAndTheCurrentRowIsFilledWithATick() => Picker(AirPodsWhere.ThisPc, (s, layout) =>
    {
        Expect(s, Icon.Computer, IconStyle.Line, s.Colours.TextSecondary, 16, "SendToPicker.cs", layout.HeaderIcon);
        Expect(s, Icon.Computer, IconStyle.Filled, s.Colours.Text, 16, "SendToPicker.cs", layout.Rows[0].Icon);
        Expect(s, Icon.Cellphone, IconStyle.Line, s.Colours.Text, 16, "SendToPicker.cs", layout.Rows[1].Icon);
        Expect(s, Icon.Volume, IconStyle.Line, s.Colours.Text, 16, "SendToPicker.cs", layout.Rows[2].Icon);
        Expect(s, Icon.Check, IconStyle.Line, s.Colours.Accent, 16, "SendToPicker.cs", layout.Rows[0].Tick);
    });

    [TestMethod]
    public void ThePickerRowForTheOtherDeviceIsFilledWhileTheAirPodsAreThere() => Picker(AirPodsWhere.Elsewhere, (s, layout) =>
    {
        Expect(s, Icon.Cellphone, IconStyle.Filled, s.Colours.Text, 16, "SendToPicker.cs", layout.Rows[1].Icon);
        Expect(s, Icon.Cellphone, IconStyle.Line, s.Colours.TextSecondary, 16, "SendToPicker.cs", layout.HeaderIcon);
        Expect(s, Icon.Computer, IconStyle.Line, s.Colours.Text, 16, "SendToPicker.cs", layout.Rows[0].Icon);
    });

    [TestMethod]
    public void ThePickerHeaderIsTheAirPodsLineIconWhenTheyAreNowhereWeKnow() => Picker(AirPodsWhere.NotInUse, (s, layout) =>
        Expect(s, Icon.AirPods, IconStyle.Line, s.Colours.TextSecondary, 16, "SendToPicker.cs", layout.HeaderIcon));

    // The compact row's rectangles, in the card's own coordinates.
    private readonly record struct CompactLayoutRects(Rectangle Glyph, Rectangle Action)
    {
        public static CompactLayoutRects Of(Scene s)
        {
            WidgetCardLayout.CompactLayout layout = s.Card!.CurrentCompactLayout!;
            return new CompactLayoutRects(layout.Glyph, layout.Action);
        }
    }
}
