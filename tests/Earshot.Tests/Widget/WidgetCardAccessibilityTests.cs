using System.Drawing;
using System.Windows.Forms;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What a screen reader and the tooltips see on the card: one child for each control the view has, in the keyboard's
// order, each with a name (an icon says nothing to a screen reader), a role, its place on screen and a default action.
[TestClass]
public sealed class WidgetCardAccessibilityTests
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSELEAVE = 0x02A3;

    private static readonly string[] PlainNames = ["Connect", "Settings", "Refresh battery"];
    private static readonly string[] FullNames = ["Connect", "Pause when a bud comes out", "Update", "Settings", "Refresh battery"];
    private static readonly bool[] OnOff = [false, true];

    // The settings page as the design groups it: Taskbar, Behaviour, Audio, Shortcuts and About, then More opened. The order's
    // pictures are named separately (PictureNames). Repair and Check for updates automatically are on the Updates page.
    private static readonly string[] SettingsNames =
    [
        "Gauge display", "Gauge order", "Case-open card", "More case-open card settings", "Hand back on shut down, sleep and Exit",
        "Microphone off mode", "Connect shortcut", "Clear connect shortcut", "Disconnect shortcut", "Clear disconnect shortcut", "Card shortcut",
        "Updates", "More settings", "Gauge position", "Other device name", "Pause when a bud comes out", "Pause when AirPods leave this PC",
        "Lower the low battery level", "Raise the low battery level", "Fully charged notice", "Left click connects", "Battery history",
        "Copy diagnostics",
    ];

    private static readonly string[] PictureNames =
        ["Ring, number, bolt", "Ring, bolt, number", "Number, ring, bolt", "Number, bolt, ring", "Bolt, ring, number", "Bolt, number, ring"];

    private static WidgetCard ShownMain(WidgetCardModel model, int dpi = 96)
    {
        var card = new WidgetCard(new CapturingLog());
        card.SetTheme(Color.Black, highContrast: false);
        card.Render(model, dpi);
        card.Location = new Point(50, 50);
        card.Show();
        card.Activate();
        Application.DoEvents();
        return card;
    }

    private static List<AccessibleObject> Children(WidgetCard card)
    {
        AccessibleObject root = card.AccessibilityObject;
        var list = new List<AccessibleObject>();
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            list.Add(root.GetChild(i)!);
        }

        return list;
    }

    [TestMethod]
    public void TheMainViewHasOneChildForEachControlAndEachIsNamed()
    {
        Phase5.CardDesktop.Run(() =>
        {
            WidgetCardModel plain = CardKit.MainModel();
            WidgetCardModel full = CardKit.MainModel(CardKit.Snapshot() with { AutoPauseAvailable = true }, updateVersion: "1.3.0") with { ShowSwitch = true, AutoPauseOn = true };
            foreach ((WidgetCardModel model, string[] names) in new (WidgetCardModel, string[])[]
            {
                (plain, PlainNames),
                (full, FullNames),
            })
            {
                using WidgetCard card = ShownMain(model);
                List<AccessibleObject> children = Children(card);
                CollectionAssert.AreEqual(
                    names, children.Where(c => c.Role != AccessibleRole.StaticText).Select(c => c.Name).ToArray(), "Controls in the keyboard's order, the columns' words after them.");
                Assert.IsTrue(children.All(c => !string.IsNullOrWhiteSpace(c.Name)), "Every child has a name.");
                Assert.AreEqual("Earshot: AirPods", card.AccessibilityObject.Name, "The card keeps its own name.");
            }
        });
    }

    [TestMethod]
    public void TheSettingsButtonIsNamedSettingsIsAPushButtonAndPressingItOpensTheSettings()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.MainModel());
            int opened = 0;
            card.SettingsRequested += (_, _) => opened++;
            AccessibleObject gear = Children(card).Single(c => c.Name == "Settings");

            Assert.AreEqual(AccessibleRole.PushButton, gear.Role);
            Assert.AreEqual("Press", gear.DefaultAction);
            Assert.AreEqual(card.RectangleToScreen(card.CurrentMainLayout.Gear), gear.Bounds, "Its place on screen.");
            Assert.IsTrue(gear.State.HasFlag(AccessibleStates.Focusable));

            gear.DoDefaultAction();

            Assert.AreEqual(1, opened);
        });
    }

    [TestMethod]
    public void TheSwitchIsACheckButtonThatSaysWhetherItIsOn()
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach (bool on in OnOff)
            {
                using WidgetCard card = ShownMain(CardKit.MainModel(CardKit.Snapshot() with { AutoPauseAvailable = true }) with { ShowSwitch = true, AutoPauseOn = on });
                AccessibleObject toggle = Children(card).Single(c => c.Name == "Pause when a bud comes out");
                Assert.AreEqual(AccessibleRole.CheckButton, toggle.Role);
                Assert.AreEqual(on, toggle.State.HasFlag(AccessibleStates.Checked));
                Assert.AreEqual("Toggle", toggle.DefaultAction);
            }
        });
    }

    [TestMethod]
    public void TheFocusedControlSaysSoAndMovesWithTab()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.MainModel());
            List<AccessibleObject> children = Children(card);
            Assert.IsTrue(children[0].State.HasFlag(AccessibleStates.Focused), "Connect has the focus first.");
            Assert.IsFalse(children[1].State.HasFlag(AccessibleStates.Focused));

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);

            children = Children(card);
            Assert.IsFalse(children[0].State.HasFlag(AccessibleStates.Focused));
            Assert.IsTrue(children[1].State.HasFlag(AccessibleStates.Focused), "Then the settings button.");
            Assert.AreEqual("Settings", card.AccessibilityObject.GetFocused()!.Name);

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);

            children = Children(card);
            Assert.IsTrue(children[2].State.HasFlag(AccessibleStates.Focused), "Then the battery refresh.");
            Assert.AreEqual("Refresh battery", card.AccessibilityObject.GetFocused()!.Name);
            Assert.AreEqual(WidgetCardFocus.Refresh, card.FocusTarget);
        });
    }

    [TestMethod]
    public void TheSettingsPageNamesEveryIconAndEveryPictureAndSaysWhichChoicesAreOn()
    {
        Phase5.CardDesktop.Run(() =>
        {
            CardSettingsValues values = FakeCardHost.Defaults() with { InstalledVersion = "1.1.0", GaugeOrder = GaugeOrder.NumberRingBolt, InstallExists = true };
            using WidgetCard card = ShownMain(CardKit.SettingsModel(values));

            // The page opens with More and the order's pictures closed; a person opens them, and a test reaches every row by doing the same.
            CardKit.OpenExpanders(card, more: true, order: true);
            List<AccessibleObject> children = Children(card);

            Assert.IsTrue(children.All(c => !string.IsNullOrWhiteSpace(c.Name)), "Every child has a name.");
            Assert.AreEqual("Back", children[0].Name, "The back arrow is an icon, so it is named.");
            AccessibleObject[] pictures = PictureNames.Select(n => children.Single(c => c.Name == n)).ToArray();
            Assert.IsTrue(pictures.All(p => p.Role == AccessibleRole.RadioButton));
            Assert.IsTrue(pictures[2].State.HasFlag(AccessibleStates.Checked), "The chosen order is on.");
            Assert.AreEqual(1, pictures.Count(p => p.State.HasFlag(AccessibleStates.Checked)), "Only one.");

            foreach (string name in SettingsNames)
            {
                Assert.AreEqual(1, children.Count(c => c.Name == name), "One child called \"" + name + "\".");
            }

            AccessibleObject position = children.Single(c => c.Name == "Gauge position");
            Assert.AreEqual(AccessibleRole.PushButton, position.Role, "The position is a choice button, as the display is.");
            Assert.AreEqual(WidgetCopy.PositionRightEnd, position.Value, "Its value is the place the gauge is at, as the old radio's checked segment said.");
            Assert.AreEqual(GaugeDisplayOptions.LabelFor(values.GaugeDisplayOptions, values.GaugeDisplayId), children.Single(c => c.Name == "Gauge display").Value, "And the display's is what its button says.");
            Assert.AreEqual(AccessibleRole.CheckButton, children.Single(c => c.Name == "Left click connects").Role);
            Assert.AreEqual(AccessibleRole.Text, children.Single(c => c.Name == "Other device name").Role);
        });
    }

    // The value is what is chosen, not a fixed word: a position or a display other than the default reads as itself.
    [TestMethod]
    public void TheGaugePositionAndDisplayValuesFollowTheChoiceMade()
    {
        Phase5.CardDesktop.Run(() =>
        {
            const string first = @"\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
            const string second = @"\?\DISPLAY#BBB0002#5&1a2b3c4d&0&UID104#{monitor-interface}";
            CardSettingsValues values = FakeCardHost.Defaults() with
            {
                GaugePosition = GaugePosition.NextToApps,
                GaugeDisplayOptions = [new DisplayOption(first, "Display 1 (1920 x 1080)"), new DisplayOption(second, "Display 2 (2560 x 1440)")],
                GaugeDisplayId = second,
            };
            using WidgetCard card = ShownMain(CardKit.SettingsModel(values));
            CardKit.OpenExpanders(card, more: true, order: false);
            List<AccessibleObject> children = Children(card);

            Assert.AreEqual(WidgetCopy.PositionNextToApps, children.Single(c => c.Name == "Gauge position").Value, "Next to the apps, not the right end.");
            Assert.AreEqual("Display 2 (2560 x 1440)", children.Single(c => c.Name == "Gauge display").Value, "The second display, not the first or the main one.");
        });
    }

    [TestMethod]
    public void ChoosingAPictureThroughTheAccessibleObjectChoosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.SettingsModel(FakeCardHost.Defaults()));
            CardKit.OpenExpanders(card, more: false, order: true);
            SettingChange? seen = null;
            card.SettingChanged += (_, change) => seen = change;

            Children(card).Single(c => c.Name == "Bolt, number, ring").DoDefaultAction();

            Assert.AreEqual(new OrderChange(GaugeOrder.BoltNumberRing), seen);
        });
    }

    [TestMethod]
    public void AnUpdatePageNamesItsBackButtonAndItsFooterButtons()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.UpdateModel(CardKit.Update(Earshot.Update.UpdateStage.Available)));
            List<AccessibleObject> children = Children(card);
            Assert.AreEqual("Back", children[0].Name);
            Assert.IsGreaterThan(1, children.Count, "And the page's own buttons.");
            Assert.IsTrue(children.All(c => !string.IsNullOrWhiteSpace(c.Name)));
        });
    }

    [TestMethod]
    // The case-open card describes its two buttons to a screen reader, which presses them by its own navigation; neither is
    // ever focused, since the card never takes the focus.
    public void ANoticeCardDescribesItsButtonAndItsCloseButtonAndFocusesNeither()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard notice = CardKit.NewCard(dark: false, notice: true);
            notice.Render(CardKit.MainModel(), 96);
            IReadOnlyList<CardControl> controls = notice.CurrentControls();
            CardControl[] pressable = controls.Where(c => c.Role != CardControlRole.StaticText).ToArray();
            Assert.HasCount(2, pressable);
            Assert.AreEqual(WidgetCopy.TipCloseCard, pressable[1].Name);
            Assert.IsTrue(controls.All(c => !string.IsNullOrWhiteSpace(c.Name)), "Every child of the case-open card has a name.");
            Assert.IsTrue(pressable[1].IconOnly);
            Assert.IsTrue(controls.All(c => !c.Focused));
        });
    }

    [TestMethod]
    public void TheBatteryColumnsAreReadAsWordsWithTheirValuesAndAreNotPressable()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            WidgetSnapshot snapshot = CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = now - TimeSpan.FromSeconds(2) }, new PartReading(80, false, null) { ReadAt = now - TimeSpan.FromSeconds(2) });
            using WidgetCard card = ShownMain(CardKit.MainModel(snapshot) with { Now = now });

            List<AccessibleObject> words = Children(card).Where(c => c.Role == AccessibleRole.StaticText).ToList();

            Assert.AreEqual("Left 70%, charging|Right 80%|Case, no reading", string.Join("|", words.Select(c => c.Name)));
            Assert.IsTrue(words.All(c => !c.State.HasFlag(AccessibleStates.Focusable)));
            Assert.IsTrue(words.All(c => c.DefaultAction is null), "Words have nothing to press.");
        });
    }

    [TestMethod]
    public void TheCaseOpenCardsColumnsAreNamedToo()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard notice = CardKit.NewCard(dark: false, notice: true);
            notice.Render(CardKit.MainModel(), 96);

            string[] names = notice.CurrentControls().Where(c => c.Role == CardControlRole.StaticText).Select(c => c.Name).ToArray();

            Assert.HasCount(3, names);
            Assert.IsTrue(names.All(n => n.StartsWith("Left", StringComparison.Ordinal) || n.StartsWith("Right", StringComparison.Ordinal) || n.StartsWith("Case", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void AnExpanderSaysWhetherItIsExpandedOrCollapsed()
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach (bool open in OnOff)
            {
                CardSettingsValues values = FakeCardHost.Defaults() with { MoreExpanded = open };
                using WidgetCard card = ShownMain(CardKit.SettingsModel(values));

                AccessibleObject more = Children(card).Single(c => c.Name == "More settings");

                Assert.AreEqual(open, more.State.HasFlag(AccessibleStates.Expanded));
                Assert.AreEqual(!open, more.State.HasFlag(AccessibleStates.Collapsed));
            }
        });
    }

    [TestMethod]
    public void TheHistoryPageNamesItsStepButtonsTheDayAndDescribesTheChart()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DateTimeOffset now = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
            HistoryView view = HistoryDays.View(
                0, now, TimeZoneInfo.Utc,
                end => HistoryStore.Window([new HistorySample(ChargeComponent.Left, 70, true, end - TimeSpan.FromMinutes(5))], end));
            using WidgetCard card = ShownMain(CardKit.HistoryModel(view));
            List<AccessibleObject> children = Children(card);

            Assert.AreEqual("Back|Previous day|Today|Next day|Battery history, Today", string.Join("|", children.Select(c => c.Name)));
            Assert.IsFalse(children[1].State.HasFlag(AccessibleStates.Unavailable), "Back a day works from today.");
            Assert.IsTrue(children[3].State.HasFlag(AccessibleStates.Unavailable), "Forward is off on today.");
            Assert.AreEqual(AccessibleRole.Chart, children[4].Role);
            StringAssert.Contains(children[4].Description, "Left 70%, charging at 14:55");
        });
    }

    [TestMethod]
    public void TheOldestDayHasItsBackButtonOff()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DateTimeOffset now = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
            using WidgetCard card = ShownMain(CardKit.HistoryModel(HistoryDays.View(HistoryDays.MaxDaysBack, now, TimeZoneInfo.Utc, null)));
            List<AccessibleObject> children = Children(card);

            Assert.IsTrue(children.Single(c => c.Name == "Previous day").State.HasFlag(AccessibleStates.Unavailable));
            Assert.IsFalse(children.Single(c => c.Name == "Next day").State.HasFlag(AccessibleStates.Unavailable));
            Assert.AreEqual("Saturday", children[2].Name);
        });
    }

    [TestMethod]
    public void ThePageStepButtonsAskForTheEarlierAndLaterDay()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DateTimeOffset now = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
            using WidgetCard card = ShownMain(CardKit.HistoryModel(HistoryDays.View(2, now, TimeZoneInfo.Utc, null)));
            var asked = new List<SetupAction>();
            card.SetupActionRequested += (_, action) => asked.Add(action);

            Children(card).Single(c => c.Name == "Previous day").DoDefaultAction();
            Children(card).Single(c => c.Name == "Next day").DoDefaultAction();

            Assert.AreEqual("HistoryEarlier HistoryLater", string.Join(" ", asked));
        });
    }

    // ---- Tooltips

    [TestMethod]
    public void TheTooltipsAreWhatTheWordsLeftOut()
    {
        Phase5.CardSta.Run(() =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            WidgetCardModel model = CardKit.MainModel(CardKit.Snapshot(readAt: now - TimeSpan.FromMinutes(2)), updateVersion: "1.3.0") with { Now = now };
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(model, 96);
            WidgetCardLayout.Layout layout = card.CurrentMainLayout;

            Assert.AreEqual("Settings", card.TooltipAt(Centre(layout.Gear))!.Value.Text, "The gear is an icon and says what it is.");
            Assert.AreEqual("Refresh battery", card.TooltipAt(Centre(layout.Refresh))!.Value.Text, "The refresh icon says what it does.");
            string leftTip = card.TooltipAt(Centre(layout.Left.ReadTime))!.Value.Text;
            Assert.StartsWith("Left ", leftTip, "A column's tooltip is a sentence: what it holds and how old the reading is.");
            Assert.EndsWith("read 2 min ago", leftTip);
            Assert.AreEqual("Version 1.3.0 is available", card.TooltipAt(Centre(layout.UpdateCaption))!.Value.Text);
            Assert.IsNull(card.TooltipAt(Centre(layout.Button)), "The Connect button says it all.");
            Assert.IsNull(card.TooltipAt(new Point(1, 1)), "Nothing under empty space.");
        });
    }

    [TestMethod]
    public void TheReadLineTooltipFollowsARefreshUnderWayAndShortAgeIsDrawnBesideTheClock()
    {
        Phase5.CardSta.Run(() =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.MainModel(CardKit.Snapshot(readAt: now - TimeSpan.FromMinutes(2))) with { Now = now }, 96);
            Assert.AreEqual("2 min ago", card.ReadLineShort, "Beside the clock icon only the age is drawn.");
            Assert.AreEqual("Battery read 2 min ago", card.ReadLineText);

            card.Render(CardKit.MainModel(CardKit.Snapshot(readAt: now - TimeSpan.FromMinutes(2))) with { Now = now, Refresh = BatteryRefreshView.Started }, 96);
            Assert.AreEqual(WidgetCopy.ReadingBattery, card.TooltipAt(Centre(card.CurrentMainLayout.Refresh))!.Value.Text, "The refresh icon's tooltip says what is happening, not its name.");
            Assert.AreEqual(WidgetCopy.ReadingBattery, card.ReadLineShort);
        });
    }

    [TestMethod]
    public void TheSettingsPageHasATooltipForEveryRowAndEveryIconOnlyControl()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), 96, order: true);
            SettingsLayout layout = card.CurrentSettingsLayout!;

            // The order's pictures are the second item of their row and have no label of their own: a picture says what it is below.
            foreach (SettingsItem row in layout.Items.Where(i => i.Kind == SettingsItemKind.Row && i.Tiles.Count == 0))
            {
                (string Text, Rectangle Anchor)? tip = card.TooltipAt(Centre(row.LabelRect));
                Assert.IsNotNull(tip, row.Row + ": a tooltip over the label.");
                Assert.AreEqual(row.Tip, tip.Value.Text);
            }

            Assert.AreEqual("Back", card.TooltipAt(Centre(layout.Frame.Back))!.Value.Text);
            Assert.AreEqual("Clear", card.TooltipAt(Centre(CardKit.Row(card, SettingsRowId.Connect).B))!.Value.Text);
            Assert.AreEqual("Ring, bolt, number", card.TooltipAt(Centre(layout.Items.Single(i => i.Row == SettingsRowId.GaugeOrder && i.Tiles.Count == 6).Tiles[1]))!.Value.Text, "A picture says what it is a picture of.");
            Assert.AreEqual(
                "Pause when a bud comes out, and play again when it goes back",
                card.TooltipAt(Centre(CardKit.Row(card, SettingsRowId.PauseBud).LabelRect))!.Value.Text,
                "What used to be the row's description.");
        });
    }

    [TestMethod]
    public void AnIconOnlyControlShowsItsTooltipWhenTheKeyboardReachesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.MainModel());
            Assert.IsNull(card.TooltipShownForTest);

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);

            Assert.AreEqual(WidgetCardFocus.Gear, card.FocusTarget);
            Assert.AreEqual("Settings", card.TooltipShownForTest, "The gear is an icon, so it says what it is when the keyboard arrives.");

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            Assert.AreEqual(WidgetCardFocus.Refresh, card.FocusTarget);
            Assert.AreEqual("Refresh battery", card.TooltipShownForTest, "The refresh icon is an icon too, so it says what it does.");

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget);
            Assert.IsNull(card.TooltipShownForTest, "Connect has words, so there is nothing to show; the old one goes.");
        });
    }

    [TestMethod]
    public void ARestingMouseWaitsTheHoverTimeThenShowsTheTooltipAndLeavingHidesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.MainModel());
            Point gear = Centre(card.CurrentMainLayout.Gear);
            nint lParam = (nint)(((gear.Y & 0xFFFF) << 16) | (gear.X & 0xFFFF));

            Phase5.TestWindows.Send(card.Handle, WM_MOUSEMOVE, 0, lParam);
            Assert.IsNull(card.TooltipShownForTest, "Not at once.");
            Assert.AreEqual("Settings", card.TooltipPendingForTest, "It is waiting for the hover time.");
            Assert.AreEqual(SystemInformation.MouseHoverTime, card.TooltipDelayForTest, "The time Windows waits before it shows a hover tooltip.");

            card.LetTheHoverTimePassForTest();
            Assert.AreEqual("Settings", card.TooltipShownForTest);

            Phase5.TestWindows.Send(card.Handle, WM_MOUSELEAVE, 0, 0);
            Assert.IsNull(card.TooltipShownForTest, "Leaving hides it.");
        });
    }

    private static Point Centre(Rectangle rect) => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
}
