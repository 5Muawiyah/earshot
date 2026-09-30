using System.Drawing;
using System.Windows.Forms;
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

    private static readonly SettingsRowId[] RowOrder =
    [
        SettingsRowId.GaugePosition, SettingsRowId.OtherDevice, SettingsRowId.PauseBud, SettingsRowId.PauseLeave, SettingsRowId.CaseCard,
        SettingsRowId.LowBattery, SettingsRowId.LeftClick, SettingsRowId.HandBack, SettingsRowId.Connect, SettingsRowId.Disconnect,
        SettingsRowId.CheckForUpdates, SettingsRowId.CheckAutomatically,
    ];

    // ---- Layout

    [TestMethod]
    public void TheRowsAreInTheDesignsOrderWithTwoDividersAndTwoSectionHeads()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), 96);

            string[] shape = card.CurrentSettingsLayout!.Items
                .Select(i => i.Kind == SettingsItemKind.Row ? i.Row.ToString() : i.Kind + (i.Kind == SettingsItemKind.Head ? ":" + i.Label : string.Empty))
                .ToArray();

            string[] expectedShape =
            [
                "GaugePosition", "OtherDevice", "PauseBud", "PauseLeave", "CaseCard", "LowBattery", "LeftClick", "HandBack",
                "Divider", "Head:Shortcuts", "Connect", "Disconnect", "Divider", "Head:Updates", "CheckForUpdates", "CheckAutomatically",
            ];
            CollectionAssert.AreEqual(expectedShape, shape);
            string[] labels = card.CurrentSettingsLayout.Items.Where(i => i.Kind == SettingsItemKind.Row).Select(i => i.Label).ToArray();
            string[] expectedLabels =
            [
                "Gauge position", "Other device", "Pause when a bud comes out", "Pause when AirPods leave this PC", "Case-open card",
                "Low battery alert", "Left click connects", "Hand back on shut down, sleep and Exit", "Connect", "Disconnect",
                "Check for updates", "Check automatically",
            ];
            CollectionAssert.AreEqual(expectedLabels, labels);
        });
    }

    [TestMethod]
    public void RowsAre36HighTheCheckRowIs44AndTheCardIs360Wide()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            SettingsLayout layout = card.CurrentSettingsLayout!;

            foreach (SettingsItem row in layout.Items.Where(i => i.Kind == SettingsItemKind.Row))
            {
                int expected = row.Row == SettingsRowId.CheckForUpdates ? 44 : 36;
                Assert.AreEqual(expected, row.Bounds.Height, row.Row + " row height.");
            }

            Assert.AreEqual(360, layout.Frame.Width);
            Assert.AreEqual(360, card.ClientSize.Width, "The settings page is the same width as every other view.");
            Assert.IsEmpty(layout.Frame.Buttons, "The settings page has no footer.");
            SettingsItem head = layout.Items.First(i => i.Kind == SettingsItemKind.Head);
            Assert.AreEqual(32, head.Bounds.Height);
            SettingsItem divider = layout.Items.First(i => i.Kind == SettingsItemKind.Divider);
            Assert.AreEqual(1, divider.Bounds.Height);
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
                CardSettingsValues values = FakeCardHost.Defaults() with { ConnectFailure = "That shortcut is used by another program.", InEarProofMissing = true, LidProofMissing = true };
                card.Render(CardKit.SettingsModel(values), dpi);
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
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
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
                switch (item.Kind)
                {
                    case SettingsItemKind.Divider:
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.Bounds, background), theme + ": a divider.");
                        continue;
                    case SettingsItemKind.Head:
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.Bounds, background), theme + ": the head " + item.Label);
                        continue;
                }

                string row = theme + " " + item.Row;
                Assert.IsTrue(CardKit.HasInk(bitmap, item.LabelRect, background), row + ": the label.");
                switch (item.Row)
                {
                    case SettingsRowId.GaugePosition:
                        Assert.IsTrue(ControlHasContent(item.A, 6), row + ": the first segment.");
                        Assert.IsTrue(ControlHasContent(item.B, 6), row + ": the second segment.");
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
                    case SettingsRowId.CheckForUpdates:
                        Assert.IsTrue(ControlHasContent(item.A, 6), row + ": the Check button's text.");
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.SubRect, background), row + ": the version sub-line.");
                        break;
                    default:
                        Assert.IsTrue(CardKit.HasInk(bitmap, item.A, background), row + ": the toggle.");
                        break;
                }
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TheSelectedPositionIsInTheAccentAndTheOtherIsNot(bool dark)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark);
            Color accent = dark ? WidgetCard.AccentDark : WidgetCard.AccentLight;
            foreach (GaugePosition position in new[] { GaugePosition.RightEnd, GaugePosition.NextToApps })
            {
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { GaugePosition = position }), 96);
                using Bitmap bitmap = CardKit.Render(card);
                SettingsItem row = CardKit.Row(card, SettingsRowId.GaugePosition);

                Color first = bitmap.GetPixel(row.A.X + 4, row.A.Y + (row.A.Height / 2));
                Color second = bitmap.GetPixel(row.B.X + 4, row.B.Y + (row.B.Height / 2));
                Assert.AreEqual(position == GaugePosition.RightEnd, first == accent, position + ": the first segment.");
                Assert.AreEqual(position == GaugePosition.NextToApps, second == accent, position + ": the second segment.");
            }
        });
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

            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { LeftClickConnects = true }), 96);
            using (Bitmap on = CardKit.Render(card))
            {
                Rectangle track = CardKit.Row(card, SettingsRowId.LeftClick).A;
                Assert.AreEqual(accent, on.GetPixel(track.X + (track.Width / 2), track.Y + (track.Height / 2)), "On: an accent track.");
            }

            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { LeftClickConnects = false }), 96);
            using (Bitmap off = CardKit.Render(card))
            {
                Rectangle track = CardKit.Row(card, SettingsRowId.LeftClick).A;
                Color background = off.GetPixel(0, 0);
                Assert.AreEqual(background, off.GetPixel(track.X + (track.Width / 2), track.Y + (track.Height / 2)), "Off: no fill.");
                Assert.AreNotEqual(background, off.GetPixel(track.X + (track.Width / 2), track.Y), "Off: an outline along the top of the track.");
            }
        });
    }

    // ---- The rows whose feature waits on a proved field

    [TestMethod]
    public void ARowWhoseFeatureWaitsOnAProvedFieldSaysSoInOneLineAndKeepsItsToggle()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InEarProofMissing = true, LidProofMissing = true }), 96);
            using Bitmap bitmap = CardKit.Render(card);

            foreach ((SettingsRowId id, string caption) in new[]
            {
                (SettingsRowId.PauseBud, WidgetCopy.SettingsWaitsOnInEar),
                (SettingsRowId.CaseCard, WidgetCopy.SettingsWaitsOnLid),
            })
            {
                SettingsItem row = CardKit.Row(card, id);
                Assert.AreEqual(caption, row.Sub);
                Assert.AreEqual(44, row.Bounds.Height, id + ": the caption is one line, so the row is a two-line row.");
                Assert.IsTrue(CardKit.HasInk(bitmap, row.SubRect, bitmap.GetPixel(0, 0)), id + ": the caption is drawn.");
                Assert.IsFalse(row.A.IsEmpty, id + ": the toggle stays.");
                StringAssert.Contains(caption, "cannot yet tell");
                Assert.IsFalse(caption.Contains("set-up", StringComparison.OrdinalIgnoreCase), id + ": set-up cannot prove this field, so the caption must not promise it.");
            }

            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            Assert.IsNull(CardKit.Row(card, SettingsRowId.PauseBud).Sub, "Proved: no caption.");
            Assert.IsNull(CardKit.Row(card, SettingsRowId.CaseCard).Sub, "Proved: no caption.");
        });
    }

    [TestMethod]
    public void TheToggleOfAWaitingRowStillWritesItsSetting()
    {
        RunSettings(
            page =>
            {
                CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.PauseBud, SettingsPart.Toggle));
                CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.CaseCard, SettingsPart.Toggle));

                CardKit.AssertCalls(page.Host, "pauseBud:False", "caseCard:False");
            },
            host => host.Values = host.Values with { InEarProofMissing = true, LidProofMissing = true });
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

    [TestMethod]
    public void TheCardKeepsItsBottomEdgeWhenThePageIsTallerThanTheMainView()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Page page = Page.Open(new FakeCardHost());
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
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            Assert.AreEqual(new SettingsTarget(SettingsRowId.GaugePosition, SettingsPart.SegmentFirst), page.Card.SettingsFocusTarget);

            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Escape, 0);
            Assert.AreEqual(WidgetCardView.Main, page.Presenter.ViewForTest, "Escape goes back.");
        });
    }

    // ---- Each control changes its setting

    [TestMethod]
    public void TheGaugePositionButtonsChangeThePosition()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.GaugePosition, SettingsPart.SegmentSecond));
            Assert.AreEqual(GaugePosition.NextToApps, page.Host.Values.GaugePosition);

            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.GaugePosition, SettingsPart.SegmentFirst));
            Assert.AreEqual(GaugePosition.RightEnd, page.Host.Values.GaugePosition);
            CardKit.AssertCalls(page.Host, "gauge:NextToApps", "gauge:RightEnd");
        });
    }

    [TestMethod]
    [DataRow("PauseBud", "pauseBud:False")]
    [DataRow("PauseLeave", "pauseLeave:False")]
    [DataRow("CaseCard", "caseCard:False")]
    [DataRow("LeftClick", "leftClick:True")]
    [DataRow("HandBack", "handBack:False")]
    [DataRow("CheckAutomatically", "checkAuto:True")]
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
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { ConnectFailure = held }), 96);
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

    // ---- Updates rows

    [TestMethod]
    public void TheCheckButtonAsksForACheckAndOpensTheUpdatePageWithoutDownloadingAnything()
    {
        RunSettings(page =>
        {
            CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.CheckForUpdates, SettingsPart.Button));

            Assert.AreEqual(1, page.Host.CheckCalls);
            Assert.AreEqual(0, page.Host.StartUpdateCalls);
            Assert.AreEqual(WidgetCardView.Update, page.Presenter.ViewForTest);
        });
    }

    // Repair sits under the check, and is there only when an install exists, in whatever state.
    [TestMethod]
    public void TheRepairRowIsBesideTheCheckRowOnlyWhenAnInstallExistsAndFitsAtEveryScale()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard without = CardKit.NewCard(dark: false);
            without.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), 96);
            Assert.IsFalse(without.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.Repair), "Nothing is installed, so there is nothing to repair.");

            foreach (int dpi in CardKit.Scales)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), dpi);
                SettingsLayout layout = card.CurrentSettingsLayout!;

                string[] rows = layout.Items.Where(i => i.Kind == SettingsItemKind.Row).Select(i => i.Row.ToString()).ToArray();
                int check = Array.IndexOf(rows, "CheckForUpdates");
                Assert.AreEqual("Repair", rows[check + 1], "Repair is the row next to Check for updates, at " + dpi + " dpi.");
                SettingsItem repair = CardKit.Row(card, SettingsRowId.Repair);
                Assert.AreEqual("Repair Earshot", repair.Label);
                Assert.AreEqual("Checks the installed files and sets Earshot up again.", repair.Sub);
                Assert.IsFalse(repair.A.IsEmpty, "It has a button.");
                Assert.IsTrue(repair.Bounds.Contains(repair.A));
                Assert.IsTrue(layout.Targets.Contains(new SettingsTarget(SettingsRowId.Repair, SettingsPart.Button)), "The keyboard reaches it.");
                int previousBottom = layout.Frame.Body.Top;
                foreach (SettingsItem item in layout.Items)
                {
                    Assert.IsGreaterThanOrEqualTo(previousBottom, item.Bounds.Top, item.Row + " overlaps the one above at " + dpi + " dpi.");
                    previousBottom = item.Bounds.Bottom;
                }
            }
        });
    }

    [TestMethod]
    public void TheRepairButtonAsksTheTrayToRepairAndNeverAsksForACheck()
    {
        RunSettings(
            page =>
            {
                CardKit.Click(page.Card, CardKit.Part(page.Card, SettingsRowId.Repair, SettingsPart.Button));

                Assert.AreEqual(1, page.Host.RepairCalls);
                Assert.AreEqual(0, page.Host.CheckCalls, "It is not the check row's button.");
                Assert.AreEqual(0, page.Host.StartUpdateCalls);
                Assert.AreEqual(WidgetCardView.Settings, page.Presenter.ViewForTest, "The card stays where it was: the result comes on a card of its own.");
            },
            host => host.Values = FakeCardHost.Defaults() with { InstallExists = true });
    }

    [TestMethod]
    public void TheInstalledVersionIsTheCheckRowsSubLine()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InstalledVersion = "1.1.0" }), 96);
            Assert.AreEqual("Version 1.1.0", CardKit.Row(card, SettingsRowId.CheckForUpdates).Sub);

            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InstalledVersion = null }), 96);
            Assert.IsNull(CardKit.Row(card, SettingsRowId.CheckForUpdates).Sub, "A version that cannot be read is not made up.");
        });
    }

    // ---- Plumbing

    private sealed class Page : IDisposable
    {
        public required FakeCardHost Host { get; init; }

        public required WidgetCardPresenter Presenter { get; init; }

        public required WidgetCard Card { get; init; }

        public static Page Open(FakeCardHost host, GaugePosition? position = null)
        {
            var log = new CapturingLog();
            WidgetCard? card = null;
            var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(position), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
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
            body(page);
        });
    }
}
