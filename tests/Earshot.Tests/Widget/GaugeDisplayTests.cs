using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The gauge on more than one display: a full-screen application hides the gauge only on the display it covers, the
// owner can choose which display's taskbar holds the gauge, and every rule is driven with synthetic layouts. The
// real read of a secondary taskbar is the last test and is inconclusive where there is none.
[TestClass]
public sealed class GaugeDisplayTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    // Invented identities in the shape Windows gives a monitor's device interface name.
    private const string IdOne = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
    private const string IdTwo = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID104#{monitor-interface}";
    private const string IdThree = @"\\?\DISPLAY#BBB0002#5&9f8e7d6c&0&UID200#{monitor-interface}";

    private static readonly Rectangle Left1080 = new(0, 0, 1920, 1080);
    private static readonly Rectangle Right1080 = new(1920, 0, 1920, 1080);

    private static DisplayInfo Display(string id, string device, Rectangle bounds, bool primary, int dpi = 96, nint handle = 0) =>
        new(id, device, bounds, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - (48 * dpi / 96)), primary, dpi, handle);

    private static readonly string[] ExpectedLabels = ["Main display", "All displays", "Display 1 (1920 x 1080)", "Display 2 (1920 x 1080)"];
    private static readonly int[] Scales = [96, 120, 144];
    private static readonly int[] FullScreenStates = [Shell.QUNS_BUSY, Shell.QUNS_RUNNING_D3D_FULL_SCREEN];

    private static readonly DisplayInfo One = Display(IdOne, @"\\.\DISPLAY1", Left1080, true, 96, 11);
    private static readonly DisplayInfo Two = Display(IdTwo, @"\\.\DISPLAY2", Right1080, false, 96, 22);

    // ---- Which display ----

    [TestMethod]
    public void TheDefaultChoiceIsTheMainDisplayWhateverItsIndex()
    {
        // The primary display listed second: "" still means the primary one.
        (DisplayInfo? display, DisplayFallbackReason fallback) = GaugeDisplayChoice.Resolve("", [Two, One]);

        Assert.AreSame(One, display);
        Assert.AreEqual(DisplayFallbackReason.None, fallback);
    }

    [TestMethod]
    public void AStoredIdentityFindsItsDisplayWhateverOrderTheyAreListedIn()
    {
        Assert.AreSame(Two, GaugeDisplayChoice.Resolve(IdTwo, [One, Two]).Display);
        Assert.AreSame(Two, GaugeDisplayChoice.Resolve(IdTwo, [Two, One]).Display);
    }

    [TestMethod]
    public void TwoMonitorsOfOneModelAreToldApartByTheWholePath()
    {
        // Same model, same instance prefix; only the output's id differs. A match on anything shorter would pick the wrong one.
        Assert.AreSame(One, GaugeDisplayChoice.Resolve(IdOne, [Two, One]).Display);
    }

    [TestMethod]
    public void ADisplayThatIsNotConnectedFallsBackToTheMainDisplayAndSaysWhy()
    {
        (DisplayInfo? display, DisplayFallbackReason fallback) = GaugeDisplayChoice.Resolve(IdThree, [One, Two]);

        Assert.AreSame(One, display);
        Assert.AreEqual(DisplayFallbackReason.NotConnected, fallback);
    }

    [TestMethod]
    public void ADisplayIsNamedPlainlyWithItsResolution()
    {
        DisplayInfo[] all = [One, Two];

        Assert.AreEqual("Display 2", DisplayNames.Short(Two, all));
        Assert.AreEqual("Display 2 (1920 x 1080)", DisplayNames.Long(Two, all));
        Assert.IsFalse(DisplayNames.Long(Two, all).Contains('\\') || DisplayNames.Long(Two, all).Contains("DISPLAY"), "No device name the owner cannot recognise.");
    }

    [TestMethod]
    public void TheListOffersTheMainDisplayThenEachConnectedDisplayAndCyclesThroughThem()
    {
        IReadOnlyList<DisplayOption> options = GaugeDisplayOptions.Build([Two, One]);

        CollectionAssert.AreEqual(ExpectedLabels, options.Select(o => o.Label).ToArray());
        Assert.AreEqual(GaugeDisplayChoice.AllDisplays, GaugeDisplayOptions.Next(options, ""));
        Assert.AreEqual(IdOne, GaugeDisplayOptions.Next(options, GaugeDisplayChoice.AllDisplays));
        Assert.AreEqual(IdTwo, GaugeDisplayOptions.Next(options, IdOne));
        Assert.AreEqual("", GaugeDisplayOptions.Next(options, IdTwo), "It wraps round to the main display.");
        Assert.AreEqual("", GaugeDisplayOptions.Next(options, IdThree), "A display that is gone is not in the list; the next is the first.");
        Assert.AreEqual("Not connected", GaugeDisplayOptions.LabelFor(options, IdThree));
        Assert.AreEqual("Display 2 (1920 x 1080)", GaugeDisplayOptions.LabelFor(options, IdTwo));
    }

    [TestMethod]
    public void TheSecondaryTaskbarIsTheVisibleOneOnThatDisplaysMonitor()
    {
        var onOne = new TaskbarWindowCandidate(100, new Rectangle(0, 1032, 1920, 48), true, 11);
        var onTwo = new TaskbarWindowCandidate(200, new Rectangle(1920, 1032, 1920, 48), true, 22);
        var hiddenOnTwo = new TaskbarWindowCandidate(300, new Rectangle(1920, 1032, 1920, 48), false, 22);

        Assert.AreEqual(200, SecondaryTaskbarPicker.Pick([onOne, onTwo], Two)!.Value.Handle);
        Assert.IsNull(SecondaryTaskbarPicker.Pick([onOne, hiddenOnTwo], Two), "A taskbar turned off on that display is not one to read.");
        Assert.IsNull(SecondaryTaskbarPicker.Pick([], Two));
    }

    // ---- The settings value ----

    [TestMethod]
    public void TheGaugeDisplayDefaultsToTheMainDisplayAndIsKeptAsItsIdentity()
    {
        Assert.AreEqual("", WidgetSettings.Default.GaugeDisplay);
        string path = _temp.File("settings.json");
        var store = new JsonSettingsStore(path, new CapturingLog());

        store.Update(s => s.Widget = s.Widget with { GaugeDisplay = IdTwo });

        var reread = new JsonSettingsStore(path, new CapturingLog());
        Assert.AreEqual(IdTwo, reread.Current.Widget.GaugeDisplay);
    }

    [TestMethod]
    public void AnOlderFileWithNoGaugeDisplayReadsAsTheMainDisplay()
    {
        string path = _temp.File("old.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Widget\": { \"Enabled\": true } }");

        Assert.AreEqual("", new JsonSettingsStore(path, new CapturingLog()).Current.Widget.GaugeDisplay);
    }

    [TestMethod]
    public void ANonsensicalGaugeDisplayIsReadAsTheMainDisplayAndRecorded()
    {
        WidgetSettings clamped = (WidgetSettings.Default with { GaugeDisplay = new string('x', 600) }).Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual("", clamped.GaugeDisplay);
        Assert.IsTrue(notes.Any(n => n.Step == "clamp:GaugeDisplay"));
        Assert.AreEqual(IdTwo, (WidgetSettings.Default with { GaugeDisplay = IdTwo }).Clamped(out _).GaugeDisplay);
    }

    // ---- Full screen, on the gauge's own display only ----

    private static TaskbarLayout BarOn(
        Rectangle monitor, int dpi = 96, int displayCount = 2, ForegroundWindowReading? foreground = null, int quns = Shell.QUNS_ACCEPTS_NOTIFICATIONS,
        bool secondary = false, DisplayFallbackReason fallback = DisplayFallbackReason.None, string label = "Display 1")
    {
        int thickness = 48 * dpi / 96;
        var bar = new Rectangle(monitor.X, monitor.Bottom - thickness, monitor.Width, thickness);
        int step = 44 * dpi / 96;
        var start = new Rectangle(monitor.X + 762, bar.Top, 45 * dpi / 96, thickness);
        var buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(monitor.X + 807 + (i * step), bar.Top, step, thickness)).ToList();
        var tray = new Rectangle(monitor.Right - (242 * dpi / 96), bar.Top, 242 * dpi / 96, thickness);
        return new TaskbarLayout(
            0, bar, TaskbarEdge.Bottom, false, monitor, [start, .. buttons, tray], start, dpi, quns, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: tray, DisplayCount: displayCount, ForegroundWindow: foreground, IsSecondary: secondary,
            DisplayLabel: label, DisplayFallback: fallback);
    }

    private static ForegroundWindowReading Game(Rectangle monitor, string label) =>
        new(new WindowIdentity("GameWindowClass", false), monitor, monitor, label);

    private static (GaugeController Controller, FakeGaugeSurface Surface, CapturingLog Log, Func<ForegroundWindowReading?> SetProbe, int[] ProbeCalls) Rig(
        Func<ForegroundWindowReading?>? probe = null)
    {
        var surface = new FakeGaugeSurface();
        var log = new CapturingLog();
        int[] calls = [0];
        ForegroundWindowReading? current = null;
        Func<ForegroundWindowReading?> reader = probe ?? (() => { calls[0]++; return current; });
        var controller = new GaugeController(
            () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(true, false), log, new Streaming.TestTimeProvider(),
            foregroundProbe: reader);
        return (controller, surface, log, () => current, calls);
    }

    [TestMethod]
    public void AFullScreenGameOnAnotherDisplayLeavesTheGaugeShownWhateverSignalSaysSo()
    {
        foreach (int quns in FullScreenStates)
        {
            (GaugeController controller, _, CapturingLog log, _, _) = Rig();
            controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));
            Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);

            controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, quns: quns, foreground: Game(Right1080, "Display 2"))));

            Assert.IsInstanceOfType<GaugeState.Shown>(controller.State, "QUNS " + quns + ": the game is on Display 2, the gauge on Display 1.");
            Assert.IsTrue(log.Entries.Any(e => e.Message.Contains("Display 2", StringComparison.Ordinal) && e.Message.Contains("Gauge stays shown", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void AFullScreenGameOnTheGaugesDisplayHidesItAndTheLogSaysWhichDisplay()
    {
        (GaugeController controller, FakeGaugeSurface surface, CapturingLog log, _, _) = Rig();
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));

        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, quns: Shell.QUNS_RUNNING_D3D_FULL_SCREEN, foreground: Game(Left1080, "Display 1"))));

        Assert.AreEqual(HiddenReason.NotificationState, ((GaugeState.Hidden)controller.State).Reason);
        CollectionAssert.Contains(surface.Calls, "HideWindow");
        Assert.IsTrue(log.Has(LogLevel.Info, "Gauge hidden (NotificationState QUNS_RUNNING_D3D_FULL_SCREEN, full-screen window on Display 1)."), string.Join(" | ", log.Entries.Select(e => e.Message)));
    }

    [TestMethod]
    public void AMaximisedWindowThatDoesNotCoverTheWholeDisplayIsNotFullScreen()
    {
        (GaugeController controller, _, _, _, _) = Rig();
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));
        var maximised = new ForegroundWindowReading(new WindowIdentity("Chrome_WidgetWin_1", false), new Rectangle(0, 0, 1920, 1032), Left1080, "Display 1");

        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, quns: Shell.QUNS_BUSY, foreground: maximised)));

        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);
    }

    [TestMethod]
    public void WithOneDisplayTheSignalAloneHidesTheGaugeAsItAlwaysDid()
    {
        // No foreground reading, and one with a window that covers nothing: neither changes the old behaviour.
        foreach (ForegroundWindowReading? foreground in new ForegroundWindowReading?[] { null, new(new WindowIdentity("Notepad", false), new Rectangle(10, 10, 300, 200), Left1080, "Display 1") })
        {
            (GaugeController controller, _, _, _, _) = Rig();
            controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1)));

            controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1, quns: Shell.QUNS_BUSY, foreground: foreground)));

            Assert.AreEqual(HiddenReason.NotificationState, ((GaugeState.Hidden)controller.State).Reason);
        }
    }

    [TestMethod]
    public void WithOneDisplayTheLogLineIsTheOldOne()
    {
        (GaugeController controller, _, CapturingLog log, _, _) = Rig();
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1)));

        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1, quns: Shell.QUNS_BUSY, foreground: Game(Left1080, "Display 1"))));

        Assert.IsTrue(log.Has(LogLevel.Info, "Gauge hidden (NotificationState QUNS_BUSY)."));
    }

    [TestMethod]
    public void WhenTheForegroundCannotBeReadWithSeveralDisplaysTheGaugeHidesAsBefore()
    {
        (GaugeController controller, _, _, _, _) = Rig();
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));

        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, quns: Shell.QUNS_BUSY, foreground: null)));

        Assert.AreEqual(HiddenReason.NotificationState, ((GaugeState.Hidden)controller.State).Reason);
    }

    [TestMethod]
    public void PresentationSettingsHideTheGaugeOnEveryDisplay()
    {
        (GaugeController controller, _, _, _, _) = Rig();
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));

        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, quns: Shell.QUNS_PRESENTATION_MODE, foreground: Game(Right1080, "Display 2"))));

        Assert.AreEqual(HiddenReason.NotificationState, ((GaugeState.Hidden)controller.State).Reason);
    }

    [TestMethod]
    public void TheAppbarNoticeIsOnlyATriggerToLookAtTheForegroundWindow()
    {
        ForegroundWindowReading? foreground = Game(Right1080, "Display 2");
        (GaugeController controller, _, CapturingLog log, _, _) = Rig(() => foreground);
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));

        controller.NotifyFullScreenApp(opening: true);
        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State, "The game is on the other display: the notice alone does not hide the gauge.");

        // The foreground then moves to a full-screen window on the gauge's own display.
        foreground = Game(Left1080, "Display 1");
        controller.OnForegroundChanged("GameWindowClass");

        Assert.AreEqual(HiddenReason.FullScreenNotified, ((GaugeState.Hidden)controller.State).Reason);
        Assert.IsTrue(log.Has(LogLevel.Info, "Gauge hidden (FullScreenNotified, full-screen window on Display 1)."));
    }

    [TestMethod]
    public void AClosingNoticeEndsThePendingLookSoALaterForegroundChangeHidesNothing()
    {
        ForegroundWindowReading? foreground = Game(Right1080, "Display 2");
        (GaugeController controller, _, _, _, _) = Rig(() => foreground);
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080)));
        controller.NotifyFullScreenApp(opening: true);
        controller.NotifyFullScreenApp(opening: false);

        foreground = Game(Left1080, "Display 1");
        controller.OnForegroundChanged("GameWindowClass");

        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);
    }

    [TestMethod]
    public void WithOneDisplayTheAppbarNoticeHidesAtOnceAndNeverReadsTheForeground()
    {
        (GaugeController controller, _, _, _, int[] probeCalls) = Rig();
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1)));

        controller.NotifyFullScreenApp(opening: true);

        Assert.AreEqual(HiddenReason.FullScreenNotified, ((GaugeState.Hidden)controller.State).Reason);
        Assert.AreEqual(0, probeCalls[0]);
    }

    [TestMethod]
    public void TheRuleIsTheWindowOnTheDisplayCoveringItsFullBounds()
    {
        Assert.IsTrue(FullScreenRule.Covers(new Rectangle(0, 0, 1920, 1080), Left1080));
        Assert.IsTrue(FullScreenRule.Covers(new Rectangle(-8, -8, 1936, 1096), Left1080), "A window a little larger than the display still covers it.");
        Assert.IsFalse(FullScreenRule.Covers(new Rectangle(0, 0, 1920, 1079), Left1080));
        Assert.IsFalse(FullScreenRule.Covers(new Rectangle(1920, 0, 1920, 1080), Left1080), "The other display's window does not cover this one.");
        Assert.IsFalse(FullScreenRule.CoversGaugeDisplay(2, Game(Right1080, "Display 2"), Left1080));
        Assert.IsTrue(FullScreenRule.CoversGaugeDisplay(2, Game(Left1080, "Display 1"), Left1080));
    }

    // ---- Placement on a secondary display ----

    // What the real secondary taskbar read on a 1920 x 1080 second display (48 px bar): the app buttons from 2660
    // to 3101, the clock's two buttons at 3734 and 3808 and the 8 px beyond it, taken from a read-only probe and
    // kept with invented positions for the test's own numbers.
    private static TaskbarLayout SecondaryBar(bool clock, int dpi = 96, bool autoHide = false, int? topOverride = null, int displayCount = 2)
    {
        int th = 48 * dpi / 96;
        Rectangle monitor = new(1920, 0, 1920, 1080);
        int top = topOverride ?? (monitor.Bottom - th);
        var bar = new Rectangle(monitor.X, top, monitor.Width, th);
        int b = 44 * dpi / 96;
        var start = new Rectangle(2660, top, 45 * dpi / 96, th);
        var occupied = new List<Rectangle> { start };
        for (int i = 0; i < 8; i++)
        {
            occupied.Add(new Rectangle(2705 + (i * b), top, b, th));
        }

        Rectangle? area = null;
        if (clock)
        {
            var left = new Rectangle(3734, top, 70 * dpi / 96, th);
            var right = new Rectangle(3808, top, 24 * dpi / 96, th);
            occupied.Add(left);
            occupied.Add(right);
            area = Rectangle.Union(left, right);
        }

        return new TaskbarLayout(
            0, bar, TaskbarEdge.Bottom, autoHide, monitor, occupied, start, dpi, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false,
            GaugeCentreIsGauge: null, NotificationArea: area, DisplayCount: displayCount, IsSecondary: true, DisplayLabel: "Display 2");
    }

    [TestMethod]
    public void OnASecondaryTaskbarTheRightEndIsEightPixelsLeftOfTheClock()
    {
        TaskbarLayout layout = SecondaryBar(clock: true);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd);

        Assert.AreEqual(PlacementFailure.None, result.Failure);
        Rectangle r = result.Bounds!.Value;
        Assert.AreEqual(3734 - 8, r.Right);
        Assert.AreEqual(layout.Taskbar.Top + 4, r.Top, "Centred on the secondary bar, not the main one.");
        Assert.IsTrue(layout.Taskbar.Contains(r));
        Assert.IsFalse(layout.Occupied.Any(o => o.IntersectsWith(r)));
    }

    [TestMethod]
    public void OnASecondaryTaskbarWithNoClockTheRightEndIsTheBarsOwnEndWithTheSameGap()
    {
        TaskbarLayout layout = SecondaryBar(clock: false);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd);

        Assert.AreEqual(PlacementFailure.None, result.Failure);
        Assert.AreEqual(layout.Taskbar.Right - 8, result.Bounds!.Value.Right);
        Assert.IsFalse(layout.Occupied.Any(o => o.IntersectsWith(result.Bounds.Value)));
    }

    [TestMethod]
    public void TheMainTaskbarWithNoNotificationAreaStillHasNoAnchor()
    {
        TaskbarLayout layout = SecondaryBar(clock: false) with { IsSecondary = false };

        Assert.AreEqual(PlacementFailure.NoAnchor, GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
    }

    [TestMethod]
    public void OnASecondaryTaskbarNextToAppsIsFourPixelsAfterTheLastButton()
    {
        TaskbarLayout layout = SecondaryBar(clock: true);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.NextToApps);

        Assert.AreEqual(PlacementFailure.None, result.Failure);
        int lastButtonRight = layout.Occupied.Where(o => o.Left < 3734).Max(o => o.Right);
        Assert.AreEqual(lastButtonRight + 4, result.Bounds!.Value.Left);
    }

    [TestMethod]
    [DataRow(96, 74, 40)]
    [DataRow(120, 93, 50)]
    [DataRow(144, 111, 60)]
    public void EachDisplaysOwnScaleSizesTheGaugeAndItsBitmap(int dpi, int width, int height)
    {
        // A second display at its own scale beside a main one at 100%.
        TaskbarLayout layout = SecondaryBar(clock: true, dpi: dpi);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(layout.Dpi), GaugePosition.RightEnd);

        Assert.AreEqual(PlacementFailure.None, result.Failure);
        Assert.AreEqual(new Size(width, height), result.Bounds!.Value.Size);
        Assert.AreEqual(3734 - CardPlacement.Scale(8, dpi), result.Bounds.Value.Right);
        Assert.IsFalse(layout.Occupied.Any(o => o.IntersectsWith(result.Bounds.Value)), "Never overlapping.");
        using Bitmap bitmap = GaugeRenderer.Render(
            new GaugeContent(GaugeMode.Reading, 60, false, false, ""), GaugePalette.Create(true, Color.FromArgb(12, 160, 88), highContrast: false, Color.Black),
            GaugeLayout.For(layout.Dpi), hover: false, "Segoe UI");
        Assert.AreEqual(result.Bounds.Value.Size, bitmap.Size, "The bitmap is the placed rectangle's size at that display's scale.");
    }

    [TestMethod]
    public void AutoHideOnTheSecondaryTaskbarFollowsTheSameRuleAsTheMainOne()
    {
        TaskbarLayout slidAway = SecondaryBar(clock: true, autoHide: true, topOverride: 1080);
        TaskbarLayout slidIn = SecondaryBar(clock: true, autoHide: true);

        Assert.AreEqual(PlacementFailure.AutoHiddenAway, GaugePlacement.Place(slidAway, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
        Assert.AreEqual(PlacementFailure.None, GaugePlacement.Place(slidIn, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
    }

    [TestMethod]
    public void NoRoomOnTheSecondaryTaskbarFallsBackAsOnTheMainOne()
    {
        TaskbarLayout layout = SecondaryBar(clock: true);
        layout = layout with { Occupied = [.. layout.Occupied, new Rectangle(3600, layout.Taskbar.Top, 120, 48)] };

        Assert.AreEqual(PlacementFailure.NoRoom, GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
    }

    [TestMethod]
    public void NoPlacementOnEitherDisplayEverOverlapsAnOccupantOrLeavesItsBar()
    {
        var random = new Random(20260930);
        for (int i = 0; i < 300; i++)
        {
            int dpi = Scales[random.Next(3)];
            bool clock = random.Next(2) == 0;
            TaskbarLayout layout = SecondaryBar(clock, dpi);
            layout = layout with { Occupied = [.. layout.Occupied, new Rectangle(2700 + random.Next(1100), layout.Taskbar.Top, random.Next(10, 200), layout.Taskbar.Height)] };
            GaugePosition position = random.Next(2) == 0 ? GaugePosition.RightEnd : GaugePosition.NextToApps;

            PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(dpi), position);

            if (result.Bounds is { } r)
            {
                Assert.IsTrue(layout.Taskbar.Contains(r), "Inside its own bar: " + r);
                Assert.IsFalse(layout.Occupied.Any(o => o.IntersectsWith(r)), "Overlaps an occupant: " + r);
            }
        }
    }

    // ---- The chosen display removed and restored ----

    [TestMethod]
    public void TheGaugeMovesToTheMainTaskbarWhenTheChosenDisplayGoesAndBackWhenItReturns()
    {
        (GaugeController controller, FakeGaugeSurface surface, CapturingLog log, _, _) = Rig();
        TaskbarLayout onSecond = BarOn(Right1080, secondary: true, label: "Display 2");
        TaskbarLayout onMainNotConnected = BarOn(Left1080, fallback: DisplayFallbackReason.NotConnected, label: "Display 1", displayCount: 1);
        TaskbarLayout onMainNoTaskbar = BarOn(Left1080, fallback: DisplayFallbackReason.TaskbarNotShown, label: "Display 1");

        controller.OnLayout(ITaskbarReader.Result.Ok(onSecond));
        Rectangle secondBounds = ((GaugeState.Shown)controller.State).Bounds;
        Assert.IsTrue(Right1080.Contains(secondBounds), "Shown on the second display's bar: " + secondBounds);

        controller.OnLayout(ITaskbarReader.Result.Ok(onMainNotConnected));
        Rectangle mainBounds = ((GaugeState.Shown)controller.State).Bounds;
        Assert.IsTrue(Left1080.Contains(mainBounds), "Moved to the main display's bar: " + mainBounds);
        Assert.AreEqual(DisplayFallbackReason.NotConnected, controller.DisplayFallback);
        Assert.IsTrue(log.Has(LogLevel.Info, "Gauge display: the chosen display is not connected, so the main display's taskbar is used."));

        controller.OnLayout(ITaskbarReader.Result.Ok(onMainNotConnected));
        Assert.AreEqual(1, log.Entries.Count(e => e.Message.Contains("is not connected", StringComparison.Ordinal)), "Said once, not every poll.");

        controller.OnLayout(ITaskbarReader.Result.Ok(onSecond));
        Assert.AreEqual(secondBounds, ((GaugeState.Shown)controller.State).Bounds);
        Assert.AreEqual(DisplayFallbackReason.None, controller.DisplayFallback);
        Assert.IsTrue(log.Has(LogLevel.Info, "Gauge display: the chosen display is back (Display 2)."));

        controller.OnLayout(ITaskbarReader.Result.Ok(onMainNoTaskbar));
        Assert.IsTrue(log.Has(LogLevel.Info, "Gauge display: the chosen display shows no taskbar, so the main display's taskbar is used."));
        CollectionAssert.Contains(surface.Calls, "MoveTo " + mainBounds);
    }

    [TestMethod]
    public void AFailureReadingTheDisplaysIsLoggedOnceWithItsRawCode()
    {
        (GaugeController controller, _, CapturingLog log, _, _) = Rig();
        var problem = StepOutcomes.FromWin32("enum-display-monitors", 0, "EnumDisplayMonitors returned FALSE.", ok: false);

        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1) with { DisplayProblem = problem }));
        controller.OnLayout(ITaskbarReader.Result.Ok(BarOn(Left1080, displayCount: 1) with { DisplayProblem = problem }));

        Assert.AreEqual(1, log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("enum-display-monitors", StringComparison.Ordinal)));
    }

    // ---- The card on the gauge's display ----

    [TestMethod]
    public void TheCardOpensOnTheGaugesDisplayInsideThatDisplaysWorkArea()
    {
        DisplayArea[] displays =
        [
            new(Left1080, new Rectangle(0, 0, 1920, 1032), true),
            new(Right1080, new Rectangle(1920, 0, 1920, 1032), false),
        ];
        var gauge = new Rectangle(3652, 1036, 74, 40);
        var cardSize = new Size(360, 300);

        Rectangle workArea = WidgetCardPlacement.WorkAreaFor(gauge, displays, Rectangle.Empty);
        Rectangle right = WidgetCardPlacement.Above(gauge, cardSize, workArea, 96, GaugePosition.RightEnd);
        Rectangle apps = WidgetCardPlacement.Above(gauge, cardSize, workArea, 96, GaugePosition.NextToApps);

        Assert.AreEqual(displays[1].WorkArea, workArea);
        Assert.AreEqual(3840 - 12, right.Right, "Twelve pixels from the second display's edge, not the first's.");
        Assert.IsTrue(displays[1].WorkArea.Contains(right) && displays[1].WorkArea.Contains(apps));
        Assert.IsLessThanOrEqualTo(gauge.Top - 12, right.Bottom);
    }

    [TestMethod]
    public void AGaugeOnTheMainDisplayStillGetsTheMainWorkArea()
    {
        DisplayArea[] displays = [new(Left1080, new Rectangle(0, 0, 1920, 1032), true), new(Right1080, new Rectangle(1920, 0, 1920, 1032), false)];

        Assert.AreEqual(displays[0].WorkArea, WidgetCardPlacement.WorkAreaFor(new Rectangle(1596, 1036, 74, 40), displays, Rectangle.Empty));
        Assert.AreEqual(displays[1].WorkArea, WidgetCardPlacement.WorkAreaFor(new Rectangle(2500, 600, 0, 0), displays, Rectangle.Empty), "A point anchor is placed by the display holding it.");
    }

    // The real display source, read only: no window, nothing changed.
    [TestMethod]
    public void TheRealDisplaySourceListsEveryDisplayWithOneMainAndADistinctIdentityEach()
    {
        DisplayReading reading = new SystemDisplaySource().Read();

        Assert.IsNull(reading.Problem, reading.Problem is null ? "" : Earshot.Tray.TrayReport.DescribeStep(reading.Problem));
        Assert.IsGreaterThan(0, reading.Displays.Count);
        Assert.AreEqual(1, reading.Displays.Count(d => d.IsPrimary), "Exactly one main display.");
        Assert.AreEqual(reading.Displays.Count, reading.Displays.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Each display has its own identity.");
        Assert.IsTrue(reading.Displays.All(d => d.Bounds.Width > 0 && d.Bounds.Height > 0 && d.Dpi >= 96 && d.Id.Length > 0 && d.Handle != 0));
        Assert.AreSame(reading.Displays.Single(d => d.IsPrimary), GaugeDisplayChoice.Resolve("", reading.Displays).Display);
    }

    // ---- The real secondary taskbar, at the final gate only ----

    [TestMethod]
    public void RealUiaTaskbarReaderReadsARealSecondaryTaskbarReadOnly()
    {
        List<TaskbarWindowCandidate> secondaries = SystemDisplaySource.SecondaryTaskbars().Taskbars.Where(c => c.Visible).ToList();
        if (secondaries.Count == 0)
        {
            Assert.Inconclusive("No Shell_SecondaryTrayWnd on this machine or desktop (one display, a private desktop, or a hosted runner): skipped.");
            return;
        }

        TaskbarWindowCandidate target = secondaries[0];
        bool ok = false;
        List<Rectangle>? occupied = null;
        StepOutcome? failure = null;
        ExceptionDispatchInfo? readerFailure = null;
        var stopwatch = new Stopwatch();
        var thread = new Thread(() =>
        {
            try
            {
                var reader = new UiaTaskbarReader();
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.TaskbarReader);
                stopwatch.Start();
                ok = reader.TryReadOccupants(target.Handle, target.Bounds, Earshot.Audio.ComRelease.Rcw, out occupied, out _, out _, out failure);
                stopwatch.Stop();
            }
            catch (Exception ex)
            {
                readerFailure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Earshot UIA secondary reader test",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The reader thread did not finish in time.");

        readerFailure?.Throw();
        Assert.IsTrue(ok, ok ? "" : "The real UIA read of the secondary taskbar failed: " + failure!.CodeName + " " + failure.Detail);
        Assert.IsGreaterThan(0, occupied!.Count, "A secondary taskbar shows at least its clock or its buttons.");
        Assert.IsTrue(occupied.All(o => target.Bounds.IntersectsWith(o)), "Every occupant read is on that taskbar.");
    }
}
