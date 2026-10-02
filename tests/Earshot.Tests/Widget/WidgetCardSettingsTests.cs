using System.Drawing;
using System.Windows.Forms;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The card's settings page: the rows in the design's order, each painted in light and dark through the card's own
// paint routine into an off-screen bitmap (never shown), and each control changing its setting through the real
// presenter and a real card on a private desktop, with the tray replaced by a fake host.
[TestClass]
public sealed class WidgetCardSettingsTests
{
    private const int WM_KEYDOWN = 0x0100;

    // ---- Layout

    [TestMethod]
    public void TheGroupsAndRowsAreInTheOwnersOrderWithOneMoreExpanderHoldingTheRest()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);

            // Closed: only the groups, with the one More row at the end.
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96, more: false);
            string[] closed = Shape(card);
            string[] expectedClosed =
            [
                "Head:Taskbar", "GaugeDisplay", "GaugeOrder", "Head:Behaviour", "CaseCard", "HandBack", "Head:Audio", "MicrophoneOff",
                "Head:Shortcuts", "Connect", "Disconnect", "OpenCard", "Head:About", "About", "More",
            ];
            CollectionAssert.AreEqual(expectedClosed, closed);

            // Open: the nine rows of More follow it, in the owner's order.
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96, more: true);
            string[] expectedOpen = expectedClosed.Concat(MoreRows).ToArray();
            CollectionAssert.AreEqual(expectedOpen, Shape(card));

            string[] labels = card.CurrentSettingsLayout!.Items.Where(i => i.Kind == SettingsItemKind.Row).Select(i => i.Label).ToArray();
            string[] expectedLabels =
            [
                "Display", "Gauge order", "Case-open card", "Hand back", "Microphone off", "Connect", "Disconnect", "Card", "Updates", "More",
                "Gauge position", "Other device name", "Pause when a bud comes out", "Pause when AirPods leave", "Low battery alerts",
                "Fully charged notice", "Left click connects", "Battery history", "Copy diagnostics",
            ];
            CollectionAssert.AreEqual(expectedLabels, labels);
            Assert.IsFalse(card.CurrentSettingsLayout.Items.Any(i => i.Row is SettingsRowId.Repair or SettingsRowId.CheckAutomatically), "Repair and Check automatically are on the updates page.");
        });
    }

    private static readonly string[] MoreRows =
        ["GaugePosition", "OtherDevice", "PauseBud", "PauseLeave", "LowBattery", "FullyCharged", "LeftClick", "History", "CopyDiagnostics"];

    private static readonly int[] WithoutRepair = [0, 1];

    private static readonly int[] WithRepair = [0, 1, 2];

    private static string[] Shape(WidgetCard card) =>
        card.CurrentSettingsLayout!.Items
            .Where(i => i.Kind == SettingsItemKind.Head || i.Tiles.Count == 0)
            .Select(i => i.Kind == SettingsItemKind.Row ? i.Row.ToString() : i.Kind + (i.Kind == SettingsItemKind.Head ? ":" + i.Label : string.Empty))
            .ToArray();

    // The design's row heights at 100% display scale and 100% text size: a single line is 48 (a 32 control and 8 above and below), a
    // line with a caption 56, the gauge order's header 56; the surfaces are 336 wide, 12 from each side, 4 apart.
    [TestMethod]
    public void RowsAreTheDesignsHeightsAndTheSurfacesAre336WideAnd4Apart()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96, more: true);
            SettingsLayout layout = card.CurrentSettingsLayout!;

            foreach (SettingsItem row in layout.Items.Where(i => i.Kind == SettingsItemKind.Row))
            {
                int expected = row.Row switch
                {
                    SettingsRowId.GaugeOrder => 56,
                    SettingsRowId.HandBack or SettingsRowId.About => 56,
                    _ => 48,
                };
                Assert.AreEqual(expected, row.Bounds.Height, row.Row + " row height.");
                Assert.AreEqual(336, row.Bounds.Width, row.Row + " is 336 wide.");
                Assert.AreEqual(12, row.Bounds.X);
            }

            Assert.AreEqual(360, layout.Frame.Width);
            Assert.AreEqual(360, card.ClientSize.Width, "The settings page is the same width as every other view.");
            Assert.IsEmpty(layout.Frame.Buttons, "The settings page has no footer.");
            SettingsItem head = layout.Items.First(i => i.Kind == SettingsItemKind.Head);
            Assert.AreEqual(20, head.Bounds.Height);
            Assert.AreEqual(layout.Frame.Body.Y + 8 + 4, head.Bounds.Top, "Body padding 8 and the first group's 4.");
            for (int i = 1; i < layout.Surfaces.Count; i++)
            {
                Assert.IsGreaterThanOrEqualTo(layout.Surfaces[i - 1].Bottom, layout.Surfaces[i].Top, "Surfaces never overlap.");
            }
        });
    }

    // With the text size at 150% a single line is 20t + 12 = 42 for its control, so the row is 58; the gauge order header holds the 40 px gauge.
    [TestMethod]
    public void SingleLineRowsGrowWithTheTextSizeToTheControlPlus16()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.AttachLook(() => new SystemLook(1.5, Transparency: true, HighContrast: false));
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96, more: true);
            SettingsItem row = CardKit.Row(card, SettingsRowId.LeftClick);
            Assert.AreEqual(42 + 16, row.Bounds.Height);
        });
    }

    [TestMethod]
    public void NoRowOverlapsAnotherOrLeavesTheCardAtAnyScale()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (int dpi in CardKit.Scales)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                CardSettingsValues values = FakeCardHost.Defaults() with { ConnectFailure = "That shortcut is used by another program.", InEarProofMissing = true };
                CardKit.RenderSettings(card, CardKit.SettingsModel(values), dpi);
                SettingsLayout layout = card.CurrentSettingsLayout!;

                int previousBottom = layout.Frame.Body.Top;
                foreach (SettingsItem item in layout.Items)
                {
                    Assert.IsGreaterThanOrEqualTo(previousBottom, item.Bounds.Top, "At " + dpi + " dpi " + item.Kind + " " + item.Row + " overlaps the one above.");
                    previousBottom = item.Bounds.Bottom;
                    Assert.IsTrue(item.Bounds.Right <= layout.Frame.Width, item.Row + " leaves the card at " + dpi + " dpi.");
                    foreach (Rectangle control in new[] { item.A, item.B, item.Value }.Where(r => !r.IsEmpty))
                    {
                        Assert.IsTrue(item.Bounds.Contains(control), item.Row + " control leaves its row at " + dpi + " dpi.");
                        Assert.IsGreaterThanOrEqualTo(0, control.Left);
                    }
                }

                Assert.IsLessThanOrEqualTo(layout.Frame.Height, previousBottom);
            }
        });
    }

    // ---- Painting: every row, light and dark

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EveryRowRendersItsLabelAndItsControl(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            using Bitmap bitmap = CardKit.Render(card);
            SettingsLayout layout = card.CurrentSettingsLayout!;
            Color background = bitmap.GetPixel(0, 0);
            string theme = dark ? "dark" : "light";

            Assert.IsTrue(CardKit.HasInk(bitmap, layout.Frame.Back, background), theme + ": the back arrow.");
            Assert.IsTrue(CardKit.HasInk(bitmap, layout.Frame.Title, background), theme + ": the title.");

            // A control's own fill is the sample it is compared with, so ink means something drawn on it.
            bool ControlHasContent(Rectangle rect, int inset)
            {
                Rectangle inner = Rectangle.Inflate(rect, -inset, -inset);
                Color fill = bitmap.GetPixel(rect.X + 4, rect.Y + (rect.Height / 2));
                return CardKit.HasInk(bitmap, inner, fill);
            }

            foreach (SettingsItem item in layout.Items)
            {
                if (item.Kind == SettingsItemKind.Head)
                {
                    Assert.IsTrue(CardKit.HasInk(bitmap, item.Bounds, background), theme + ": the head " + item.Label);
                    continue;
                }

                string row = theme + " " + item.Row;
                if (item.Tiles.Count == 0)
                {
                    Assert.IsTrue(CardKit.HasInk(bitmap, item.LabelRect, background), row + ": the label.");
                }

                switch (item.Row)
                {
                    case SettingsRowId.GaugePosition:
                    case SettingsRowId.GaugeDisplay:
                    case SettingsRowId.CopyDiagnostics:
                        Assert.IsTrue(ControlHasContent(item.A, 6), row + ": the button's text.");
                        break;
                    case SettingsRowId.OtherDevice:
                        Assert.IsTrue(ControlHasContent(item.A, 6), row + ": the text box shows its text.");
                        break;
                    case SettingsRowId.LowBattery:
                        Assert.IsTrue(ControlHasContent(item.A, 6), row + ": the minus glyph.");
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.Value, background), row + ": the value.");
                        Assert.IsTrue(ControlHasContent(item.B, 6), row + ": the plus glyph.");
                        break;
                    case SettingsRowId.Connect:
                    case SettingsRowId.Disconnect:
                        Assert.IsTrue(ControlHasContent(item.A, 6), row + ": the shortcut box shows the chord.");
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.B, background), row + ": the clear button's cross.");
                        break;
                    case SettingsRowId.About:
                    case SettingsRowId.History:
                    case SettingsRowId.More:
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.Chevron, bitmap.GetPixel(item.Chevron.X, item.Chevron.Y)), row + ": the chevron.");
                        break;
                    case SettingsRowId.GaugeOrder when item.Tiles.Count == 0:
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.Preview, bitmap.GetPixel(item.Preview.X, item.Preview.Y)), row + ": the gauge as it is now.");
                        break;
                    case SettingsRowId.GaugeOrder:
                        Assert.AreEqual(6, item.Tiles.Count, row + ": six pictures.");
                        foreach (Rectangle tile in item.Tiles)
                        {
                            Assert.IsTrue(CardKit.HasInk(bitmap, Rectangle.Inflate(tile, -4, -4), bitmap.GetPixel(tile.X + 4, tile.Y + 4)), row + ": a picture of the gauge.");
                        }

                        break;
                    default:
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.A, background), row + ": the toggle.");
                        break;
                }
            }
        });
    }

    // Each row sits on a surface: the settings row fill and a 1 px stroke at the radius of 4, drawn under the row.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EveryRowSitsOnASurfaceWithTheDesignsFillAndStroke(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96, more: true);
            using Bitmap bitmap = CardKit.Render(card);
            DesignTokens tokens = DesignTokens.For(dark, highContrast: false);
            Color behind = bitmap.GetPixel(2, card.CurrentSettingsLayout!.Frame.Body.Y + 20);
            foreach (Rectangle surface in card.CurrentSettingsLayout.Surfaces)
            {
                Color inside = bitmap.GetPixel(surface.X + 6, surface.Y + (surface.Height / 2));
                AssertNear(Over(tokens.SettingsRowFill, behind), inside, 2, "The fill of " + surface);
                Color edge = bitmap.GetPixel(surface.X + 40, surface.Y);
                AssertNear(Over(tokens.SettingsRowStroke, Over(tokens.SettingsRowFill, behind)), edge, 3, "The stroke along the top of " + surface);
            }
        });
    }

    private static void AssertNear(Color expected, Color actual, int tolerance, string message)
    {
        Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(expected.R - actual.R), message + " (red " + expected + " vs " + actual + ")");
        Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(expected.G - actual.G), message + " (green " + expected + " vs " + actual + ")");
        Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(expected.B - actual.B), message + " (blue " + expected + " vs " + actual + ")");
    }

    private static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromArgb(255, (int)Math.Round((top.R * a) + (under.R * (1 - a))), (int)Math.Round((top.G * a) + (under.G * (1 - a))), (int)Math.Round((top.B * a) + (under.B * (1 - a))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AToggleIsTheAccentWhenOnAndOnlyAnOutlineWhenOff(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark);
            Color accent = dark ? WidgetCard.AccentDark : WidgetCard.AccentLight;

            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { LeftClickConnects = true }), 96);
            using (Bitmap on = CardKit.Render(card))
            {
                Rectangle track = CardKit.Row(card, SettingsRowId.LeftClick).A;
                Assert.AreEqual(accent, on.GetPixel(track.X + (track.Width / 2), track.Y + (track.Height / 2)), "On: an accent track.");
            }

            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { LeftClickConnects = false }), 96);
            using (Bitmap off = CardKit.Render(card))
            {
                Rectangle track = CardKit.Row(card, SettingsRowId.LeftClick).A;
                SettingsItem row = CardKit.Row(card, SettingsRowId.LeftClick);
                Color surface = off.GetPixel(row.Bounds.X + 6, track.Y + (track.Height / 2));
                Assert.AreEqual(surface, off.GetPixel(track.X + (track.Width / 2), track.Y + (track.Height / 2)), "Off: no fill, the row's own surface shows.");
                Assert.AreNotEqual(surface, off.GetPixel(track.X + (track.Width / 2), track.Y), "Off: an outline along the top of the track.");
            }
        });
    }

    // ---- The row whose feature waits on the in-ear signal

    [TestMethod]
    public void ARowWhoseFeatureWaitsOnTheInEarSignalSaysSoInOneLineAndKeepsItsToggle()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { InEarProofMissing = true }), 96);
            using Bitmap bitmap = CardKit.Render(card);

            SettingsItem row = CardKit.Row(card, SettingsRowId.PauseBud);
            Assert.AreEqual(WidgetCopy.SettingsWaitsOnInEar, row.Sub);
            Assert.IsGreaterThan(48, row.Bounds.Height, "The caption makes the row taller.");
            Assert.IsTrue(CardKit.HasInk(bitmap, row.SubRect, bitmap.GetPixel(0, 0)), "The caption is drawn.");
            Assert.IsFalse(row.A.IsEmpty, "The toggle stays.");
            StringAssert.Contains(WidgetCopy.SettingsWaitsOnInEar, "cannot yet tell");

            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            Assert.IsNull(CardKit.Row(card, SettingsRowId.PauseBud).Sub, "Known: no caption.");
        });
    }

    [TestMethod]
    public void TheToggleOfAWaitingRowStillWritesItsSetting()
    {
        RunSettings(
            page =>
            {
                CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.PauseBud, SettingsPart.Toggle));

                CardKit.AssertCalls(page.Host, "pauseBud:False");
            },
            host => host.Values = host.Values with { InEarProofMissing = true });
    }

    // ---- Opening and leaving the page

    [TestMethod]
    public void TheGearOpensTheSettingsPageAndTheBackArrowReturnsToTheMainView()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Page page = Page.Open(new FakeCardHost());
            Assert.AreEqual(WidgetCardView.Main, page.Presenter.ViewForTest);

            CardKit.Click(page.Card, page.Card.CurrentMainLayout.Gear);
            Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest);
            Assert.AreEqual(WidgetCardView.Settings, page.Card.EffectiveView);
            Assert.AreEqual(360, page.Card.ClientSize.Width);

            CardKit.Click(page.Card, page.Card.CurrentSettingsLayout!.Frame.Back);
            Assert.AreEqual(WidgetCardView.Main, page.Presenter.ViewForTest);
            Assert.IsTrue(page.Presenter.IsShown, "Back goes to the main view; it does not close the card.");
        });
    }

    // The work area is given, not read from the screen the tests run on: the page is taller than a small screen has room for.
    [TestMethod]
    public void TheCardKeepsItsBottomEdgeWhenThePageIsTallerThanTheMainView()
    {
        AssertTheCardKeepsItsBottomEdge(new Rectangle(0, 0, 1920, 1040));
    }

    [TestMethod]
    public void TheCardKeepsItsBottomEdgeWhenThePageIsTallerThanTheMainViewOnASmallWorkArea()
    {
        AssertTheCardKeepsItsBottomEdge(new Rectangle(0, 0, 1280, 600));
    }

    private static void AssertTheCardKeepsItsBottomEdge(Rectangle workArea)
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Page page = Page.Open(new FakeCardHost(), workArea: workArea);
            int bottom = page.Card.Bounds.Bottom;

            CardKit.Click(page.Card, page.Card.CurrentMainLayout.Gear);

            Assert.AreEqual(bottom, page.Card.Bounds.Bottom, "It grows upward from the gauge, not down into the taskbar.");
            Assert.IsGreaterThan(page.Card.CurrentMainLayout.Height, page.Card.Height);
        });
    }

    [TestMethod]
    public void ASecondGaugeClickClosesTheSettingsPage()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Page page = Page.Open(new FakeCardHost());
            CardKit.Click(page.Card, page.Card.CurrentMainLayout.Gear);

            page.Presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();

            Assert.IsFalse(page.Presenter.IsShown);
            Assert.AreEqual(WidgetCardView.Main, page.Presenter.ViewForTest, "The next open starts at the main view.");
        });
    }

    // Tab is given to the page as a Tab with no modifier. A key message sent to the window has Shift added to it from the real
    // keyboard when a Shift key is held or stuck, and the page would then go backwards; Escape reads the same either way, so it is
    // still sent as a message.
    [TestMethod]
    public void TabAndEscapeWorkOnTheSettingsPageFromTheKeyboard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Page page = Page.Open(new FakeCardHost());
            CardKit.Click(page.Card, page.Card.CurrentMainLayout.Gear);
            page.Card.Activate();
            Application.DoEvents();

            Assert.AreEqual(new SettingsTarget(SettingsRowId.None, SettingsPart.Back), page.Card.SettingsFocusTarget);
            page.Card.HandleSettingsKey(Keys.Tab);
            Assert.AreEqual(new SettingsTarget(SettingsRowId.GaugeDisplay, SettingsPart.Choice), page.Card.SettingsFocusTarget, "The first stop after Back is the Taskbar group's Display.");

            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Escape, 0);
            Assert.AreEqual(WidgetCardView.Main, page.Presenter.ViewForTest, "Escape goes back.");
        });
    }

    // ---- Each control changes its setting

    [TestMethod]
    public void TheGaugePositionComboNamesThePlaceAndAPressMovesToTheOther()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.GaugePosition, SettingsPart.Choice));
            Assert.AreEqual(GaugePosition.NextToApps, page.Host.Values.GaugePosition);

            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.GaugePosition, SettingsPart.Choice));
            Assert.AreEqual(GaugePosition.RightEnd, page.Host.Values.GaugePosition);
            CardKit.AssertCalls(page.Host, "gauge:NextToApps", "gauge:RightEnd");
        });
    }

    [TestMethod]
    [DataRow("PauseBud", "pauseBud:False")]
    [DataRow("PauseLeave", "pauseLeave:False")]
    [DataRow("LeftClick", "leftClick:True")]
    [DataRow("HandBack", "handBack:False")]
    [DataRow("FullyCharged", "fullyCharged:False")]
    [DataRow("CaseCard", "caseCard:False")]
    public void EachToggleFlipsItsOwnSettingAndNothingElse(string rowName, string expected)
    {
        RunSettings(page =>
        {
            var row = Enum.Parse<SettingsRowId>(rowName);
            CardSettingsValues before = page.Host.Values;

            CardKit.Click(page.Card, CardKit.Part(page.Card, row, SettingsPart.Toggle));

            CollectionAssert.AreEqual(new[] { expected }, page.Host.Calls);
            Assert.AreNotEqual(before, page.Host.Values);

            CardKit.Click(page.Card, CardKit.Part(page.Card, row, SettingsPart.Toggle));
            Assert.AreEqual(before, page.Host.Values, "A second click puts it back.");
        });
    }

    // The case-open card's row: its chevron opens the expander, whose close choice steps through the five times, whose displays
    // choice steps between where the gauge is and all displays, and whose boxes (one per display, with more than one) tick a
    // display in or out. Each press saves through the host; the chevron only changes the page.
    [TestMethod]
    public void TheCaseOpenCardRowExpandsToItsCloseAndDisplayChoices()
    {
        const string one = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
        const string two = @"\\?\DISPLAY#BBB0002#5&1a2b3c4d&0&UID104#{monitor-interface}";
        RunSettings(
            page =>
            {
                Assert.IsFalse(page.Card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.CaseCardClose), "Collapsed at first.");

                CardKit.ClickPart(page.Card, SettingsRowId.CaseCard, SettingsPart.Expand);
                Assert.IsTrue(page.Card.CaseCardExpanded);
                Assert.IsEmpty(page.Host.Calls, "Opening the expander saves nothing.");

                CardKit.ClickPart(page.Card, SettingsRowId.CaseCardClose, SettingsPart.Choice);
                CardKit.ClickPart(page.Card, SettingsRowId.CaseCardDisplays, SettingsPart.Choice);
                CardKit.AssertCalls(page.Host, "caseCardClose:5", "caseCardDisplays:" + CaseOpenCardDisplayChoice.All);

                page.Host.Calls.Clear();
                SettingsItem box = page.Card.CurrentSettingsLayout!.Items.Single(i => i.Row == SettingsRowId.CaseCardDisplay && i.Index == 1);
                page.Card.ScrollSettingsToForTest(box.Bounds.Top - page.Card.CurrentSettingsLayout.Frame.Body.Y);
                CardKit.Click(page.Card, new Rectangle(box.A.X, box.A.Y - page.Card.SettingsScrollOffset, box.A.Width, box.A.Height));
                CardKit.AssertCalls(page.Host, "caseCardDisplay:" + two + ":False");
            },
            host => host.Values = host.Values with
            {
                CaseOpenCardDisplayOptions = [new DisplayOption(one, "Display 1 (1920 x 1080)"), new DisplayOption(two, "Display 2 (1920 x 1080)")],
                CaseOpenCardShownOn = [one, two],
            });
    }

    [TestMethod]
    public void TheLowBatteryStepperMovesInTensAndStopsAtTenAndNinety()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LowBattery, SettingsPart.Plus));
            Assert.AreEqual(30, page.Host.Values.LowBatteryPercent);
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LowBattery, SettingsPart.Minus));
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LowBattery, SettingsPart.Minus));
            Assert.AreEqual(10, page.Host.Values.LowBatteryPercent);

            page.Host.Calls.Clear();
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LowBattery, SettingsPart.Minus));
            Assert.IsEmpty(page.Host.Calls, "Nothing below 10.");

            page.Host.Values = page.Host.Values with { LowBatteryPercent = 90 };
            page.Presenter.Refresh();
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LowBattery, SettingsPart.Plus));
            Assert.IsEmpty(page.Host.Calls, "Nothing above 90.");
        });
    }

    [TestMethod]
    public void TheUpAndDownKeysStepTheLowBatteryValueWhenItsButtonHasTheFocus()
    {
        RunSettings(page =>
        {
            page.Card.FocusSettingsTarget(new SettingsTarget(SettingsRowId.LowBattery, SettingsPart.Plus));
            page.Host.Calls.Clear();

            page.Card.HandleSettingsKey(Keys.Up);
            page.Card.HandleSettingsKey(Keys.Down);
            page.Card.HandleSettingsKey(Keys.Down);

            CardKit.AssertCalls(page.Host, "low:30", "low:20", "low:10");
        });
    }

    [TestMethod]
    public void TheSpaceAndEnterKeysUseTheControlThatHasTheFocus()
    {
        RunSettings(page =>
        {
            page.Card.FocusSettingsTarget(new SettingsTarget(SettingsRowId.LeftClick, SettingsPart.Toggle));

            page.Card.HandleSettingsKey(Keys.Space);
            page.Card.HandleSettingsKey(Keys.Enter);

            CardKit.AssertCalls(page.Host, "leftClick:True", "leftClick:False");
        });
    }

    // ---- The other device's name

    [TestMethod]
    public void TypingInTheTextBoxAndPressingEnterSavesTheNewName()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.OtherDevice, SettingsPart.Text));
            Assert.IsTrue(page.Card.IsEditingText);
            Assert.AreEqual("iPhone", page.Card.TextBufferForTest, "It starts from the saved name.");

            for (int i = 0; i < 6; i++)
            {
                page.Card.HandleSettingsKey(Keys.Back);
            }

            foreach (char c in "Pixel")
            {
                page.Card.HandleSettingsChar(c);
            }

            Assert.IsEmpty(page.Host.Calls, "Nothing is saved while it is being typed.");
            page.Card.HandleSettingsKey(Keys.Enter);

            CardKit.AssertCalls(page.Host, "label:Pixel");
            Assert.AreEqual("Pixel", page.Host.Values.OtherDeviceLabel);
            Assert.IsFalse(page.Card.IsEditingText);
        });
    }

    [TestMethod]
    public void EscapeInTheTextBoxKeepsTheSavedNameAndClickingAwaySavesWhatWasTyped()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.OtherDevice, SettingsPart.Text));
            page.Card.HandleSettingsChar('!');
            page.Card.HandleSettingsKey(Keys.Escape);
            Assert.IsEmpty(page.Host.Calls, "Escape puts the saved name back.");
            Assert.IsFalse(page.Card.IsEditingText);
            Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest, "Escape in the box does not leave the page.");

            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.OtherDevice, SettingsPart.Text));
            page.Card.HandleSettingsChar('!');
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LeftClick, SettingsPart.Toggle));

            CardKit.AssertCalls(page.Host, "label:iPhone!", "leftClick:True");
        });
    }

    [TestMethod]
    public void TheTextBoxEditsAtTheCaretAndStopsAtTheLabelLimit()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.OtherDevice, SettingsPart.Text));
            page.Card.HandleSettingsKey(Keys.Home);
            page.Card.HandleSettingsKey(Keys.Right);
            page.Card.HandleSettingsChar('-');
            page.Card.HandleSettingsKey(Keys.Delete);
            Assert.AreEqual("i-hone", page.Card.TextBufferForTest);

            page.Card.HandleSettingsKey(Keys.End);
            for (int i = 0; i < 60; i++)
            {
                page.Card.HandleSettingsChar('x');
            }

            Assert.AreEqual(WidgetSettings.MaxOtherDeviceLabelLength, page.Card.TextBufferForTest.Length);
            page.Card.HandleSettingsChar('\r');
            Assert.AreEqual(WidgetSettings.MaxOtherDeviceLabelLength, page.Card.TextBufferForTest.Length, "A control character is not text.");
        });
    }

    [TestMethod]
    public void LeavingThePageWhileTypingSavesWhatWasTyped()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.OtherDevice, SettingsPart.Text));
            page.Card.HandleSettingsChar('2');

            CardKit.Click(page.Card, page.Card.CurrentSettingsLayout!.Frame.Back);

            CardKit.AssertCalls(page.Host, "label:iPhone2");
        });
    }

    // ---- Shortcuts

    [TestMethod]
    public void AShortcutBoxShowsItsChordOrNotSetAndWaitsForKeysWhenClicked()
    {
        RunSettings(
            page =>
            {
                Assert.AreEqual("Ctrl+Alt+Shift+A", page.Card.ShortcutBoxText(SettingsRowId.Connect));
                Assert.AreEqual(WidgetCopy.ShortcutNotSet, page.Card.ShortcutBoxText(SettingsRowId.Disconnect));

                CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Shortcut));

                Assert.AreEqual(SettingsRowId.Connect, page.Card.CapturingShortcut);
                Assert.AreEqual(WidgetCopy.ShortcutPressKeys, page.Card.ShortcutBoxText(SettingsRowId.Connect));
            },
            host => host.Values = host.Values with { DisconnectChord = string.Empty });
    }

    [TestMethod]
    public void AChordWithCtrlOrAltIsSavedThroughTheHost()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Shortcut));
            page.Card.HandleSettingsKey(Keys.ControlKey | Keys.Control);
            Assert.AreEqual(SettingsRowId.Connect, page.Card.CapturingShortcut, "A modifier alone is waited through.");

            page.Card.HandleSettingsKey(Keys.P | Keys.Control | Keys.Alt);

            CardKit.AssertCalls(page.Host, "shortcut:Connect:Ctrl+Alt+P");
            Assert.IsNull(page.Card.CapturingShortcut);
            Assert.AreEqual("Ctrl+Alt+P", page.Card.ShortcutBoxText(SettingsRowId.Connect));

            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Disconnect, SettingsPart.Shortcut));
            page.Card.HandleSettingsKey(Keys.F5 | Keys.Alt | Keys.Shift);
            Assert.AreEqual("shortcut:Disconnect:Alt+Shift+F5", page.Host.Calls[^1]);
        });
    }

    [TestMethod]
    public void AKeyWithNeitherCtrlNorAltIsNotAShortcutAndEscapeGivesUp()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Shortcut));

            page.Card.HandleSettingsKey(Keys.P);
            page.Card.HandleSettingsKey(Keys.P | Keys.Shift);
            page.Card.HandleSettingsKey(Keys.F5);
            Assert.IsEmpty(page.Host.Calls);
            Assert.AreEqual(SettingsRowId.Connect, page.Card.CapturingShortcut, "It keeps waiting.");

            page.Card.HandleSettingsKey(Keys.Escape);

            Assert.IsNull(page.Card.CapturingShortcut);
            Assert.IsEmpty(page.Host.Calls);
            Assert.AreEqual("Ctrl+Alt+Shift+A", page.Card.ShortcutBoxText(SettingsRowId.Connect), "Escape leaves the chord as it was.");
        });
    }

    [TestMethod]
    public void ClickingAwayFromAWaitingShortcutBoxGivesUp()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Shortcut));
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.LeftClick, SettingsPart.Toggle));

            Assert.IsNull(page.Card.CapturingShortcut);
        });
    }

    [TestMethod]
    public void TheClearButtonClearsTheChordAndIsGoneWhenThereIsNone()
    {
        RunSettings(page =>
        {
            Assert.IsTrue(page.Card.CurrentSettingsLayout!.Targets.Contains(new SettingsTarget(SettingsRowId.Connect, SettingsPart.Clear)));

            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Clear));

            CardKit.AssertCalls(page.Host, "clear:Connect");
            Assert.AreEqual(WidgetCopy.ShortcutNotSet, page.Card.ShortcutBoxText(SettingsRowId.Connect));
            Assert.IsFalse(page.Card.CurrentSettingsLayout.Targets.Contains(new SettingsTarget(SettingsRowId.Connect, SettingsPart.Clear)), "An empty box has nothing to clear.");

            page.Host.Calls.Clear();
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Clear));
            Assert.IsEmpty(page.Host.Calls, "A click on a disabled Clear does nothing.");
        });
    }

    [TestMethod]
    public void ARefusedChordSaysWhyOnItsRowAndTheChordStaysAsItWas()
    {
        RunSettings(page =>
        {
            page.Host.ShortcutRefusal = "Ctrl+Alt+P is already set for another command here.";
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Shortcut));
            page.Card.HandleSettingsKey(Keys.P | Keys.Control | Keys.Alt);

            SettingsItem row = CardKit.Row(page.Card, SettingsRowId.Connect);
            Assert.AreEqual("Ctrl+Alt+P is already set for another command here.", row.Sub);
            Assert.IsTrue(row.SubIsProblem);
            Assert.AreEqual("Ctrl+Alt+Shift+A", page.Card.ShortcutBoxText(SettingsRowId.Connect));

            page.Host.ShortcutRefusal = null;
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Connect, SettingsPart.Shortcut));
            page.Card.HandleSettingsKey(Keys.Q | Keys.Control | Keys.Alt);
            Assert.IsNull(CardKit.Row(page.Card, SettingsRowId.Connect).Sub, "A chord that is saved clears the note.");
        });
    }

    [TestMethod]
    public void AChordAnotherProgramHoldsIsSaidOnItsRowInThePlainWordsOfTheRegistration()
    {
        const string held = "Ctrl+Alt+Shift+A is already in use by another program, so it was not set. Windows reported error 1409.";
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { ConnectFailure = held }), 96);
            using Bitmap bitmap = CardKit.Render(card);

            SettingsItem connect = CardKit.Row(card, SettingsRowId.Connect);
            SettingsItem disconnect = CardKit.Row(card, SettingsRowId.Disconnect);
            Assert.AreEqual(held, connect.Sub);
            Assert.IsTrue(connect.SubIsProblem);
            Assert.IsNull(disconnect.Sub, "Only the shortcut that failed says so.");
            Assert.IsTrue(CardKit.HasInk(bitmap, connect.SubRect, bitmap.GetPixel(0, 0)), "The reason is drawn.");
            Assert.IsGreaterThanOrEqualTo(connect.A.Bottom, connect.SubRect.Top, "Under the shortcut box, across the row.");
            Assert.IsGreaterThan(connect.Bounds.Width / 2, connect.SubRect.Width, "It has the row's width, so it is read in a few lines.");
        });
    }

    // ---- About, More and the pages behind them

    [TestMethod]
    public void TheUpdatesRowOpensTheUpdatePageWithoutCheckingOrDownloadingAnything()
    {
        RunSettings(page =>
        {
            CardKit.ClickPart(page.Card, SettingsRowId.About, SettingsPart.Button);

            Assert.AreEqual(0, page.Host.CheckCalls, "Opening the page asks for no check.");
            Assert.AreEqual(0, page.Host.StartUpdateCalls);
            Assert.AreEqual(WidgetCardView.Update, page.Presenter.ViewForTest);
            Assert.IsNotNull(page.Card.Model.Setup!.Rows, "The page has its own rows.");
        });
    }

    [TestMethod]
    public void TheMoreRowOpensAndClosesTheNineRowsAndSavesNothing()
    {
        RunSettings(page =>
        {
            CardKit.OpenExpanders(page.Card, more: false);
            Assert.IsFalse(page.Card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.PauseBud), "Closed at first.");

            CardKit.ClickPart(page.Card, SettingsRowId.More, SettingsPart.Expand);
            Assert.IsTrue(page.Card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.CopyDiagnostics), "Open.");
            Assert.IsEmpty(page.Host.Calls, "Opening More saves nothing.");

            CardKit.ClickPart(page.Card, SettingsRowId.More, SettingsPart.Expand);
            Assert.IsFalse(page.Card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.PauseBud), "Closed again.");
        });
    }

    [TestMethod]
    public void TheGaugeOrderRowOpensToSixPicturesAndAPictureChoosesItsOrder()
    {
        RunSettings(page =>
        {
            CardKit.OpenExpanders(page.Card, more: true, order: false);
            Assert.IsFalse(page.Card.CurrentSettingsLayout!.Items.Any(i => i.Tiles.Count > 0), "Closed at first.");

            CardKit.ClickPart(page.Card, SettingsRowId.GaugeOrder, SettingsPart.Expand);
            SettingsItem grid = page.Card.CurrentSettingsLayout!.Items.Single(i => i.Tiles.Count == 6);
            Assert.IsEmpty(page.Host.Calls);

            page.Card.ScrollSettingsToForTest(grid.Bounds.Top - page.Card.CurrentSettingsLayout.Frame.Body.Y);
            Rectangle tile = grid.Tiles[3];
            CardKit.Click(page.Card, new Rectangle(tile.X, tile.Y - page.Card.SettingsScrollOffset, tile.Width, tile.Height));
            CardKit.AssertCalls(page.Host, "order:" + GaugeOrder.NumberBoltRing);
        });
    }

    [TestMethod]
    public void TheHistoryRowOpensAnEmptyHistoryPageAndBackReturnsToTheSettings()
    {
        RunSettings(page =>
        {
            CardKit.ClickPart(page.Card, SettingsRowId.History, SettingsPart.Button);

            Assert.AreEqual(WidgetCardView.History, page.Presenter.ViewForTest);
            Assert.AreEqual(WidgetCopy.HistoryTitle, page.Card.Model.Setup!.Title);
            Assert.AreEqual("Battery history", page.Card.Model.Setup.Title);
            Assert.IsEmpty(page.Card.Model.Setup.Buttons, "A frame and a back button; nothing else yet.");

            CardKit.Click(page.Card, page.Card.CurrentSetupLayout!.Frame.Back);
            Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest, "Back goes to the settings page.");
        });
    }

    [TestMethod]
    public void TheCopyButtonDoesWhatTheTrayMenusCopyDiagnosticsItemDoes()
    {
        RunSettings(page =>
        {
            CardKit.ClickPart(page.Card, SettingsRowId.CopyDiagnostics, SettingsPart.Button);

            CardKit.AssertCalls(page.Host, "copyDiagnostics");
            Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest, "The page stays.");
        });
    }

    // Repair is on the updates page, a row of its own, and only when an install exists.
    [TestMethod]
    public void TheUpdatesPageHasCheckAutomaticallyWhatsNewAndRepairOnlyWhenAnInstallExistsAndFitsAtEveryScale()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard without = CardKit.NewCard(dark: false);
            without.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.UpToDate), showRepair: false), 96);
            CollectionAssert.AreEqual(WithoutRepair, without.CurrentSetupLayout!.UpdateRows.Select(r => r.Index).ToArray(), "Nothing is installed, so there is nothing to repair.");

            foreach (int dpi in CardKit.Scales)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.UpToDate), showRepair: true), dpi);
                WidgetCardLayout.SetupLayout layout = card.CurrentSetupLayout!;

                CollectionAssert.AreEqual(WithRepair, layout.UpdateRows.Select(r => r.Index).ToArray(), "Check automatically, What's new, Repair at " + dpi + " dpi.");
                int previousBottom = layout.StatusSurface.Bottom;
                foreach (WidgetCardLayout.UpdatesRowLayout row in layout.UpdateRows)
                {
                    Assert.IsGreaterThanOrEqualTo(previousBottom, row.Surface.Top, "Row " + row.Index + " overlaps the one above at " + dpi + " dpi.");
                    previousBottom = row.Surface.Bottom;
                    Assert.IsTrue(row.Surface.Contains(row.Control), "Row " + row.Index + " control stays on its row at " + dpi + " dpi.");
                }

                Assert.IsLessThanOrEqualTo(layout.Frame.Height, previousBottom);
            }
        });
    }

    [TestMethod]
    public void TheRepairRowOfTheUpdatesPageAsksTheTrayToRepairAndNeverAsksForACheck()
    {
        RunSettings(
            page =>
            {
                CardKit.ClickPart(page.Card, SettingsRowId.About, SettingsPart.Button);
                CardKit.Click(page.Card, page.Card.CurrentSetupLayout!.UpdateRows.Single(r => r.Index == 2).Surface);

                Assert.AreEqual(1, page.Host.RepairCalls);
                Assert.AreEqual(0, page.Host.CheckCalls, "It is not the check button.");
                Assert.AreEqual(0, page.Host.StartUpdateCalls);
                Assert.AreEqual(WidgetCardView.Update, page.Presenter.ViewForTest, "The card stays where it was: the result comes on a card of its own.");
            },
            host => host.Values = FakeCardHost.Defaults() with { InstallExists = true });
    }

    [TestMethod]
    public void TheWhatsNewRowOpensTheReleaseNotesAndTheAutomaticRowTogglesTheCheck()
    {
        RunSettings(page =>
        {
            CardKit.ClickPart(page.Card, SettingsRowId.About, SettingsPart.Button);
            CardKit.Click(page.Card, page.Card.CurrentSetupLayout!.UpdateRows.Single(r => r.Index == 1).Surface);
            CardKit.Click(page.Card, page.Card.CurrentSetupLayout!.UpdateRows.Single(r => r.Index == 0).Surface);

            CardKit.AssertCalls(page.Host, "whatsNew", "checkAuto:True");
        });
    }

    [TestMethod]
    public void TheInstalledVersionIsTheUpdatesRowsCaption()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { InstalledVersion = "1.1.0" }), 96);
            Assert.AreEqual("Updates", CardKit.Row(card, SettingsRowId.About).Label);
            Assert.AreEqual("Version 1.1.0", CardKit.Row(card, SettingsRowId.About).Sub);

            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { InstalledVersion = null }), 96);
            Assert.IsNull(CardKit.Row(card, SettingsRowId.About).Sub, "A version that cannot be read is not made up.");
        });
    }

    // ---- Plumbing

    private sealed class Page : IDisposable
    {
        public required FakeCardHost Host { get; init; }

        public required WidgetCardPresenter Presenter { get; init; }

        public required WidgetCard Card { get; init; }

        // The work area the card is placed in: never the screen the tests run on, so no page is taller than the room it has
        // unless a test says so. A work area given also puts the gauge just under it.
        private static readonly Rectangle DefaultWorkArea = new(0, 0, 1920, 1040);

        public static Page Open(FakeCardHost host, GaugePosition? position = null, Rectangle? workArea = null)
        {
            var log = new CapturingLog();
            WidgetCard? card = null;
            Rectangle area = workArea ?? DefaultWorkArea;
            Rectangle gauge = workArea is null ? CardKit.Gauge : new Rectangle(area.Right - 200, area.Bottom, CardKit.Gauge.Width, CardKit.Gauge.Height);
            var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(position), CardKit.Inline, new Streaming.TestTimeProvider(), log, host,
                workAreaFor: _ => area);
            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();
            return new Page { Host = host, Presenter = presenter, Card = card! };
        }

        public void Dispose() => Presenter.Dispose();
    }

    // Opens a real card on a private desktop, on its settings page, over a fake host.
    private static void RunSettings(Action<Page> body, Action<FakeCardHost>? setup = null)
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost();
            setup?.Invoke(host);
            using Page page = Page.Open(host);
            CardKit.Click(page.Card, page.Card.CurrentMainLayout.Gear);
            Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest, "The gear opened the settings page.");
            CardKit.OpenExpanders(page.Card);
            body(page);
        });
    }
}
