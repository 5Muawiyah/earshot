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

    private static readonly string[] SettingsNames =
    [
        "Right end", "Next to apps", "Gauge display", "Other device name", "Pause when a bud comes out", "Pause when AirPods leave this PC",
        "Lower the low battery level", "Raise the low battery level", "Left click connects", "Hand back on shut down, sleep and Exit",
        "Connect shortcut", "Disconnect shortcut", "Clear connect shortcut", "Clear disconnect shortcut", "Check for updates", "Repair Earshot",
        "Check for updates automatically",
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
                CollectionAssert.AreEqual(names, children.Select(c => c.Name).ToArray(), "Children in the keyboard's order.");
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

            AccessibleObject rightEnd = children.Single(c => c.Name == "Right end");
            Assert.AreEqual(AccessibleRole.RadioButton, rightEnd.Role);
            Assert.IsTrue(rightEnd.State.HasFlag(AccessibleStates.Checked));
            Assert.AreEqual(AccessibleRole.CheckButton, children.Single(c => c.Name == "Left click connects").Role);
            Assert.AreEqual(AccessibleRole.Text, children.Single(c => c.Name == "Other device name").Role);
        });
    }

    [TestMethod]
    public void ChoosingAPictureThroughTheAccessibleObjectChoosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = ShownMain(CardKit.SettingsModel(FakeCardHost.Defaults()));
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
            Assert.HasCount(2, controls);
            Assert.AreEqual(WidgetCopy.TipCloseCard, controls[1].Name);
            Assert.IsTrue(controls[1].IconOnly);
            Assert.IsTrue(controls.All(c => !c.Focused));
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
            Assert.AreEqual("Battery read 2 min ago", card.TooltipAt(Centre(layout.ReadLine))!.Value.Text, "The read line is short; its tooltip is the whole sentence.");
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
            Assert.AreEqual(WidgetCopy.ReadingBattery, card.TooltipAt(Centre(card.CurrentMainLayout.ReadLine))!.Value.Text, "The tooltip is the line the refresh set, not the old age.");
            Assert.AreEqual(WidgetCopy.ReadingBattery, card.ReadLineShort);
        });
    }

    [TestMethod]
    public void TheSettingsPageHasATooltipForEveryRowAndEveryIconOnlyControl()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), 96);
            SettingsLayout layout = card.CurrentSettingsLayout!;

            foreach (SettingsItem row in layout.Items.Where(i => i.Kind == SettingsItemKind.Row))
            {
                (string Text, Rectangle Anchor)? tip = card.TooltipAt(Centre(row.LabelRect));
                Assert.IsNotNull(tip, row.Row + ": a tooltip over the label.");
                Assert.AreEqual(row.Tip, tip.Value.Text);
            }

            Assert.AreEqual("Back", card.TooltipAt(Centre(layout.Frame.Back))!.Value.Text);
            Assert.AreEqual("Clear", card.TooltipAt(Centre(CardKit.Row(card, SettingsRowId.Connect).B))!.Value.Text);
            Assert.AreEqual("Ring, bolt, number", card.TooltipAt(Centre(CardKit.Row(card, SettingsRowId.GaugeOrder).Tiles[1]))!.Value.Text, "A picture says what it is a picture of.");
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
