using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// Gauge display set to All displays: the rules that are pure, the gauges proved wired through the real tray with only the display
// list, the secondary taskbar windows, the foreground window, the taskbar reads and the gauge windows replaced, and the gauge
// set on its own for what a tray cannot show. Every tray runs on a private desktop.
[TestClass]
public sealed class GaugeAllDisplaysTests
{
    // Invented identities in the shape Windows gives a monitor's device interface name.
    private const string IdOne = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
    private const string IdTwo = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID104#{monitor-interface}";
    private const string IdThree = @"\\?\DISPLAY#BBB0002#5&2b3c4d5e&0&UID108#{monitor-interface}";

    private static readonly Rectangle Left1080 = new(0, 0, 1920, 1080);
    private static readonly Rectangle Right1080 = new(1920, 0, 1920, 1080);
    private static readonly Rectangle Far1080 = new(3840, 0, 1920, 1080);

    private static DisplayInfo Display(string id, string device, Rectangle bounds, bool primary, nint handle, int dpi = 96) =>
        new(id, device, bounds, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - 48), primary, dpi, handle);

    private static readonly DisplayInfo One = Display(IdOne, @"\\.\DISPLAY1", Left1080, true, 11);
    private static readonly DisplayInfo Two = Display(IdTwo, @"\\.\DISPLAY2", Right1080, false, 22);
    private static readonly DisplayInfo Three = Display(IdThree, @"\\.\DISPLAY3", Far1080, false, 33);

    // ----- the choice and its words -----

    [TestMethod]
    public void AllDisplaysIsAReservedValueNoDisplayIdCanBe()
    {
        Assert.IsTrue(GaugeDisplayChoice.IsAll(GaugeDisplayChoice.AllDisplays));
        foreach (string id in new[] { "", IdOne, IdTwo, IdThree, "gdi:\\\\.\\DISPLAY2" })
        {
            Assert.IsFalse(GaugeDisplayChoice.IsAll(id), "A display's identity is never the All displays choice: " + id);
        }

        // The two shapes an identity has, an interface path and "gdi:" plus a device name, both start with a character the
        // reserved value does not.
        Assert.IsFalse(GaugeDisplayChoice.AllDisplays.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.IsFalse(GaugeDisplayChoice.AllDisplays.StartsWith("gdi:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AStoredDisplayKeepsItsValueAndAllDisplaysResolvesToTheMainDisplayForTheMainGauge()
    {
        IReadOnlyList<DisplayInfo> displays = [One, Two];

        Assert.AreEqual((Two, DisplayFallbackReason.None), GaugeDisplayChoice.Resolve(IdTwo, displays), "An old setting still names its display.");
        Assert.AreEqual((One, DisplayFallbackReason.None), GaugeDisplayChoice.Resolve("", displays), "The default stays the main display.");
        Assert.AreEqual((One, DisplayFallbackReason.None), GaugeDisplayChoice.Resolve(GaugeDisplayChoice.AllDisplays, displays));
        Assert.AreEqual(GaugeDisplayChoice.MainDisplay, GaugeDisplayChoice.ReaderChoice(GaugeDisplayChoice.AllDisplays), "The main gauge's reader reads the main display.");
        Assert.AreEqual(IdTwo, GaugeDisplayChoice.ReaderChoice(IdTwo));
        Assert.AreEqual(GaugeDisplayChoice.MainDisplay, GaugeDisplayChoice.ReaderChoice(null));
    }

    [TestMethod]
    public void TheDisplayListOffersAllDisplaysBesideMainDisplayWhenThereIsMoreThanOneDisplay()
    {
        IReadOnlyList<DisplayOption> two = GaugeDisplayOptions.Build([One, Two]);

        CollectionAssert.AreEqual(
            new[] { "", GaugeDisplayChoice.AllDisplays, IdOne, IdTwo }, two.Select(o => o.Id).ToArray());
        Assert.AreEqual("Main display", two[0].Label);
        Assert.AreEqual("All displays", two[1].Label);
        Assert.AreEqual(GaugeDisplayChoice.AllDisplays, GaugeDisplayOptions.Next(two, ""), "The picker goes from Main display to All displays.");
        Assert.AreEqual(IdOne, GaugeDisplayOptions.Next(two, GaugeDisplayChoice.AllDisplays));
        Assert.AreEqual("All displays", GaugeDisplayOptions.LabelFor(two, GaugeDisplayChoice.AllDisplays));

        IReadOnlyList<DisplayOption> one = GaugeDisplayOptions.Build([One]);
        Assert.HasCount(2, one, "With one display there is nothing to show it on besides: Main display and Display 1.");
        Assert.AreEqual("All displays", GaugeDisplayOptions.LabelFor(one, GaugeDisplayChoice.AllDisplays), "A stored choice is still named when the list does not offer it.");
        Assert.AreEqual("", GaugeDisplayOptions.Next(one, GaugeDisplayChoice.AllDisplays), "From a choice the list does not offer the picker starts again at the first.");
    }

    [TestMethod]
    public void AllDisplaysSurvivesTheSettingsClampAndTheTipSaysWhatItDoes()
    {
        var widget = new WidgetSettings { GaugeDisplay = GaugeDisplayChoice.AllDisplays };

        WidgetSettings clamped = widget.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(GaugeDisplayChoice.AllDisplays, clamped.GaugeDisplay);
        Assert.IsEmpty(notes);
        StringAssert.Contains(WidgetCopy.TipGaugeDisplay, "All displays");
        Assert.AreEqual("Gauge display", SettingsRows.NameOf(SettingsRowId.GaugeDisplay, SettingsPart.Choice));
        Assert.AreEqual(WidgetCopy.TipGaugeDisplay, SettingsRows.TipOf(SettingsRowId.GaugeDisplay, SettingsPart.Choice));
    }

    // ----- one tray icon for several gauges -----

    [TestMethod]
    public void TheTrayIconIsVisibleOnlyWhenNoGaugeIsShown()
    {
        var icon = new FakeTrayIcon();
        var votes = new TrayIconVotes(icon);
        using TrayIconVotes.Voter main = votes.NewVoter();
        TrayIconVotes.Voter other = votes.NewVoter();

        main.Visible = false;
        Assert.IsFalse(icon.Visible, "The main gauge is shown, so the icon is out of the way.");

        other.Visible = true;
        Assert.IsFalse(icon.Visible, "The other gauge being hidden does not bring the icon back while the main one is shown.");

        other.Visible = false;
        main.Visible = true;
        Assert.IsFalse(icon.Visible, "One gauge still shown keeps the icon hidden.");

        other.Dispose();
        Assert.IsTrue(icon.Visible, "The gauge that held the icon hidden is gone and the other is not shown: the icon is back.");

        other.Visible = false;
        Assert.IsTrue(icon.Visible, "A late vote from a gauge that is gone counts for nothing.");
    }

    [TestMethod]
    public void OnceTheIconIsHeldHiddenNoVoteOrRemovedGaugeBringsItBack()
    {
        var icon = new FakeTrayIcon();
        var votes = new TrayIconVotes(icon);
        using TrayIconVotes.Voter main = votes.NewVoter();
        TrayIconVotes.Voter other = votes.NewVoter();
        main.Visible = false;
        other.Visible = false;

        votes.HoldHidden();
        int changes = icon.VisibilityChanges.Count;
        main.Visible = true;
        other.Dispose();
        other.Visible = true;

        Assert.IsFalse(icon.Visible, "The icon was brought back after it was held hidden.");
        Assert.AreEqual(changes, icon.VisibilityChanges.Count, "Nothing more was set on the icon.");
    }

    [TestMethod]
    public void WithOneGaugeTheIconIsSetExactlyAsOftenAsTheControllerAsksAndNeverDeduplicated()
    {
        var icon = new FakeTrayIcon();
        using TrayIconVotes.Voter only = new TrayIconVotes(icon).NewVoter();

        only.Visible = true;
        only.Visible = true;
        only.Visible = false;

        Assert.IsTrue(icon.VisibilityChanges.SequenceEqual([true, true, false]), "Every vote reached the icon: " + string.Join(",", icon.VisibilityChanges));
    }

    // ----- the tray, with fake displays and fake gauge windows -----

    private sealed class FakeDisplays : IDisplaySource
    {
        private readonly Lock _gate = new();
        private List<DisplayInfo> _displays;

        public FakeDisplays(params DisplayInfo[] displays) => _displays = [.. displays];

        public void Set(params DisplayInfo[] displays)
        {
            lock (_gate)
            {
                _displays = [.. displays];
            }
        }

        public DisplayReading Read()
        {
            lock (_gate)
            {
                return new DisplayReading([.. _displays]);
            }
        }
    }

    // The secondary taskbar windows there are, as a test sets them.
    private sealed class FakeTaskbars
    {
        private readonly Lock _gate = new();
        private List<TaskbarWindowCandidate> _bars = [];
        private StepOutcome? _problem;

        public void Set(params TaskbarWindowCandidate[] bars)
        {
            lock (_gate)
            {
                _bars = [.. bars];
            }
        }

        public void SetProblem(StepOutcome? problem)
        {
            lock (_gate)
            {
                _problem = problem;
            }
        }

        public SecondaryTaskbarReading Read()
        {
            lock (_gate)
            {
                return new SecondaryTaskbarReading([.. _bars], _problem);
            }
        }
    }

    private static TaskbarWindowCandidate BarOn(DisplayInfo display, nint handle) =>
        new(handle, new Rectangle(display.Bounds.X, display.Bounds.Bottom - 48, display.Bounds.Width, 48), true, display.Handle);

    // A gauge window that shows nothing: what it was last told, and whether it was disposed.
    private sealed class TestSurface : IGaugeSurface
    {
        public bool IsDisposed { get; private set; }

        public nint WindowHandle { get; init; } = 0x4000;

        public Rectangle? Bounds { get; private set; }

        public bool Hidden { get; private set; } = true;

        public int ShowCount { get; private set; }

        public int RenderCount { get; private set; }

        public Rectangle? LastRenderBounds { get; private set; }

        public int LastRenderDpi { get; private set; }

        public event EventHandler? LeftClicked;

        public event EventHandler<Point>? RightClicked;

        public StepOutcome ShowAt(Rectangle bounds)
        {
            Bounds = bounds;
            Hidden = false;
            ShowCount++;
            return new StepOutcome("show-at", true, 0, "S_OK", null);
        }

        public StepOutcome MoveTo(Rectangle bounds)
        {
            Bounds = bounds;
            return new StepOutcome("move-to", true, 0, "S_OK", null);
        }

        public int RaiseCount { get; private set; }

        public StepOutcome Raise()
        {
            RaiseCount++;
            return new StepOutcome("raise", true, 0, "S_OK", null);
        }

        public void Render(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings, int dpi, Rectangle bounds, Color ink, string fontFamily)
        {
            RenderCount++;
            LastRenderBounds = bounds;
            LastRenderDpi = dpi;
            lock (_drawnScales)
            {
                _drawnScales.Add(dpi);
            }
        }

        private readonly List<int> _drawnScales = [];

        // The scale of every draw, in order.
        public IReadOnlyList<int> DrawnScales
        {
            get
            {
                lock (_drawnScales)
                {
                    return [.. _drawnScales];
                }
            }
        }

        public void HideWindow() => Hidden = true;

        public void Dispose() => IsDisposed = true;

        public void Click() => LeftClicked?.Invoke(this, EventArgs.Empty);

        public void RightClick(Point point) => RightClicked?.Invoke(this, point);
    }

    private sealed class Rig : IDisposable
    {
        private readonly List<TestSurface> _surfaces = [];

        public required TrayHarness Tray { get; init; }

        public required FakeDisplays Displays { get; init; }

        public required FakeTaskbars Taskbars { get; init; }

        public ForegroundWindowReading? Foreground { get; set; }

        public IReadOnlyList<TestSurface> Surfaces
        {
            get
            {
                lock (_surfaces)
                {
                    return [.. _surfaces];
                }
            }
        }

        public TestSurface Make()
        {
            var surface = new TestSurface { WindowHandle = 0x4000 + _surfaces.Count };
            lock (_surfaces)
            {
                _surfaces.Add(surface);
            }

            return surface;
        }

        // The surface that was shown on a display, by where it was put.
        public TestSurface? ShownOn(Rectangle monitor) =>
            Surfaces.FirstOrDefault(s => s.Bounds is { } b && monitor.Contains(b));

        public FakeTaskbarReader? ReaderFor(string displayId) => Tray.TaskbarReaders.FirstOrDefault(r => r.LastChosenDisplay == displayId);

        public void Dispose() => Tray.Dispose();
    }

    private static TaskbarLayout BarLayout(DisplayInfo display, bool secondary, DisplayFallbackReason fallback = DisplayFallbackReason.None)
    {
        Rectangle monitor = display.Bounds;
        int thickness = 48 * display.Dpi / 96;
        var bar = new Rectangle(monitor.X, monitor.Bottom - thickness, monitor.Width, thickness);
        int step = 44;
        var start = new Rectangle(monitor.X + 762, bar.Top, 45, thickness);
        var buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(monitor.X + 807 + (i * step), bar.Top, step, thickness)).ToList();
        var tray = new Rectangle(monitor.Right - 242, bar.Top, 242, thickness);
        return new TaskbarLayout(
            0, bar, TaskbarEdge.Bottom, false, monitor, [start, .. buttons, tray], start, display.Dpi, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: tray, DisplayCount: 2, ForegroundWindow: null, IsSecondary: secondary, DisplayLabel: "Display " + display.DeviceName[^1], DisplayFallback: fallback);
    }

    // Runs what the UI thread has been posted for a while, which a sleep does not: a read of the main taskbar reaches the tray, and the
    // other gauges are matched to the displays, only when this thread pumps.
    private static void PumpFor(TimeSpan span)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < span)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    // Pumps until the main taskbar has been read at least this many more times and the tray has handled the reads, each of which
    // matches the other gauges to the displays and taskbars there are. Fails when the reads do not come, so a test that waits for
    // something not to happen has seen the tray look.
    private static void PumpThroughMainReads(Rig rig, int reads)
    {
        int target = rig.Tray.TaskbarReaders[0].ReadCount + reads;
        TrayHarness.PumpUntil(() => rig.Tray.TaskbarReaders[0].ReadCount >= target, "The main taskbar was not read " + reads + " more times.");
        PumpFor(TimeSpan.FromMilliseconds(50));
    }

    private static string Why(Rig rig) => string.Join(" | ", rig.Tray.Log.Entries.TakeLast(25).Select(e => e.Message)) + " readers=" + rig.Tray.TaskbarReaders.Count + " surfaces=" + string.Join(",", rig.Surfaces.Select(s => s.Bounds?.ToString() ?? "none"));

    private static ForegroundWindowReading Game(Rectangle monitor, string label) =>
        new(new WindowIdentity("GameWindowClass", false), monitor, monitor, label);

    private static Action<EarshotSettings> WidgetOn(string gaugeDisplay) =>
        s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true, LeftClickConnects = false, GaugeDisplay = gaugeDisplay };

    // A tray with the widget on, two displays (or the given ones) each with a taskbar, and the main gauge shown. The satellite's
    // reader is given its read when the tray asks for it.
    private static Rig StartRig(string gaugeDisplay, params DisplayInfo[] displays) => StartRigCore(gaugeDisplay, secondaryBars: true, displays);

    private static Rig StartRigCore(string gaugeDisplay, bool secondaryBars, DisplayInfo[] displays, int pollIntervalMs = 30)
    {
        DisplayInfo[] shown = displays.Length > 0 ? displays : [One, Two];
        var fakeDisplays = new FakeDisplays(shown);
        var taskbars = new FakeTaskbars();
        taskbars.Set(secondaryBars ? shown.Where(d => !d.IsPrimary).Select((d, i) => BarOn(d, 0x5000 + i)).ToArray() : []);
        Rig? rig = null;
        var tray = new TrayHarness(
            snapshot: Target(ConnectionState.Disconnected), displaySource: fakeDisplays, taskbarWatcherPollIntervalMs: pollIntervalMs,
            gaugeSurfaceFactory: () => rig!.Make(), foregroundWindowProbe: _ => rig!.Foreground,
            secondaryTaskbars: taskbars.Read, settings: WidgetOn(gaugeDisplay));
        rig = new Rig { Tray = tray, Displays = fakeDisplays, Taskbars = taskbars };
        tray.PumpUntilIdle();
        tray.TaskbarReaders[0].SetNextResult(ITaskbarReader.Result.Ok(BarLayout(One, secondary: false)));
        return rig;
    }

    // Gives every other display's reader the read its display's taskbar would give.
    private static void FeedSecondary(Rig rig, DisplayInfo display)
    {
        TrayHarness.PumpUntil(() => rig.ReaderFor(display.Id) is not null, "No reader was ever asked for " + display.DeviceName + ".");
        rig.ReaderFor(display.Id)!.SetNextResult(ITaskbarReader.Result.Ok(BarLayout(display, secondary: true)));
    }

    [TestMethod]
    public void WithAllDisplaysTwoFakeDisplaysBothGetAGaugeEachOnItsOwnTaskbar()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);

            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "Not every display got a gauge on its own taskbar. " + Why(rig));

            Assert.AreEqual(2, rig.Surfaces.Count(s => !s.IsDisposed), "Exactly one gauge window each.");
            Assert.AreEqual(1, rig.Tray.Context.SecondaryGaugeCountForTest);
            Assert.AreEqual(GaugeDisplayChoice.MainDisplay, rig.Tray.TaskbarReaders[0].LastChosenDisplay, "The main gauge reads the main display.");
            Assert.AreEqual(IdTwo, rig.ReaderFor(IdTwo)!.LastChosenDisplay, "The other gauge reads its own display's taskbar.");
            Assert.IsInstanceOfType<GaugeState.Shown>(rig.Tray.Context.WidgetGaugeStateForTest);
            Assert.IsInstanceOfType<GaugeState.Shown>(rig.Tray.Context.SecondaryGaugesForTest.Single().State);
        });
    }

    [TestMethod]
    public void EveryGaugeIsDrawnWithItsOwnBoundsAndItsOwnDisplaysScale()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DisplayInfo scaled = Two with { Dpi = 144 };
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays, One, scaled);
            FeedSecondary(rig, scaled);

            TrayHarness.PumpUntil(
                () => rig.ShownOn(Left1080) is { RenderCount: > 0 } && rig.ShownOn(Right1080) is { RenderCount: > 0 }, "A gauge was never drawn.");

            TestSurface second = rig.ShownOn(Right1080)!;
            TestSurface first = rig.ShownOn(Left1080)!;
            Assert.AreEqual(second.Bounds, second.LastRenderBounds, "Drawn where it is.");
            Assert.AreEqual(144, second.LastRenderDpi, "The second display's own scale, not the main display's.");
            Assert.AreEqual(96, first.LastRenderDpi);
            Assert.IsTrue(second.Bounds!.Value.Width > first.Bounds!.Value.Width, "A larger scale gives a larger gauge: placed and scaled by the gauge's own display.");
        });
    }

    [TestMethod]
    public void AFullScreenWindowOnOneDisplayHidesOnlyThatDisplaysGaugeAndItReturnsWhenTheWindowCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "The gauges were never shown.");
            rig.Foreground = Game(Right1080, "Display 2");

            var opening = Message.Create(rig.Tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 1);
            rig.Tray.Context.Window.Dispatch(ref opening);

            Assert.IsInstanceOfType<GaugeState.Shown>(rig.Tray.Context.WidgetGaugeStateForTest, "A full-screen window on Display 2 does not hide Display 1's gauge.");
            GaugeState second = rig.Tray.Context.SecondaryGaugesForTest.Single().State;
            Assert.IsInstanceOfType<GaugeState.Hidden>(second, "It hides the gauge on its own display.");
            Assert.AreEqual(HiddenReason.FullScreenNotified, ((GaugeState.Hidden)second).Reason);
            Assert.IsTrue(rig.ShownOn(Right1080)!.Hidden, "The window itself is hidden, not only the state.");
            Assert.IsFalse(rig.ShownOn(Left1080)!.Hidden);

            var closing = Message.Create(rig.Tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 0);
            rig.Tray.Context.Window.Dispatch(ref closing);
            TrayHarness.PumpUntil(() => rig.Tray.Context.SecondaryGaugesForTest.Single().State is GaugeState.Shown, "The gauge never came back when the full-screen window closed.");
        });
    }

    [TestMethod]
    public void AFullScreenWindowOnTheMainDisplayHidesOnlyTheMainGauge()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "The gauges were never shown.");
            rig.Foreground = Game(Left1080, "Display 1");

            var opening = Message.Create(rig.Tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 1);
            rig.Tray.Context.Window.Dispatch(ref opening);

            Assert.IsInstanceOfType<GaugeState.Hidden>(rig.Tray.Context.WidgetGaugeStateForTest);
            Assert.IsInstanceOfType<GaugeState.Shown>(rig.Tray.Context.SecondaryGaugesForTest.Single().State, "Display 2's gauge stays.");
        });
    }

    [TestMethod]
    public void ADisplayThatIsRemovedHasItsGaugeStoppedAndItsWindowDisposed()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Right1080) is not null, "The second gauge was never shown.");
            TestSurface second = rig.ShownOn(Right1080)!;
            FakeTaskbarReader reader = rig.ReaderFor(IdTwo)!;

            rig.Displays.Set(One);
            rig.Taskbars.Set();
            var changed = Message.Create(rig.Tray.Context.Window.Handle, 0x007E, 0, 0);
            rig.Tray.Context.Window.Dispatch(ref changed);

            Assert.IsTrue(second.IsDisposed, "The removed display's gauge window is disposed.");
            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            Assert.IsFalse(rig.ShownOn(Left1080)!.IsDisposed, "The main display's gauge is left alone.");
            PumpFor(TimeSpan.FromMilliseconds(150));
            int reads = reader.ReadCount;
            PumpFor(TimeSpan.FromMilliseconds(250));
            Assert.AreEqual(reads, reader.ReadCount, "Its taskbar is no longer being read.");
            Assert.IsTrue(rig.Tray.Log.Has(LogLevel.Info, "the gauge for Display 2 is removed"));
        });
    }

    [TestMethod]
    public void ATaskbarThatGoesAwayRemovesItsGaugeAndOneThatComesAddsOne()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays, One, Two, Three);
            FeedSecondary(rig, Two);
            FeedSecondary(rig, Three);
            TrayHarness.PumpUntil(() => rig.ShownOn(Right1080) is not null && rig.ShownOn(Far1080) is not null, "The gauges were never shown.");
            Assert.AreEqual(2, rig.Tray.Context.SecondaryGaugeCountForTest);
            TestSurface third = rig.ShownOn(Far1080)!;

            // The owner turns the taskbar off on the other displays' own settings page for Display 3 only.
            rig.Taskbars.Set(BarOn(Two, 0x5000));
            TrayHarness.PumpUntil(() => third.IsDisposed, "The gauge of a display that no longer shows a taskbar stayed.");
            Assert.AreEqual(1, rig.Tray.Context.SecondaryGaugeCountForTest);

            rig.Taskbars.Set(BarOn(Two, 0x5000), BarOn(Three, 0x5001));
            TrayHarness.PumpUntil(() => rig.Tray.Context.SecondaryGaugeCountForTest == 2, "The taskbar came back and no gauge was added.");
        });
    }

    [TestMethod]
    public void WhileAWindowRectangleCannotBeReadNoGaugeIsRemovedForTheTaskbarsItDoesNotSee()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Right1080) is not null, "The second gauge was never shown.");

            rig.Taskbars.SetProblem(StepOutcomes.FromWin32("get-window-rect:Shell_SecondaryTrayWnd", 1400, ok: false));
            rig.Taskbars.Set();
            PumpThroughMainReads(rig, 4);

            Assert.AreEqual(1, rig.Tray.Context.SecondaryGaugeCountForTest, "A taskbar missing because it could not be read is not a taskbar that is gone.");
            Assert.IsFalse(rig.ShownOn(Right1080)!.IsDisposed);
        });
    }

    [TestMethod]
    public void SwitchingFromAllDisplaysToMainDisplayLeavesExactlyOneGaugeAndSwitchingBackAddsTheOtherAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "The gauges were never shown.");
            TestSurface second = rig.ShownOn(Right1080)!;
            FakeTaskbarReader reader = rig.ReaderFor(IdTwo)!;

            rig.Tray.Context.WidgetCardHostForTest.SetGaugeDisplay(GaugeDisplayChoice.MainDisplay, CardPlace.NearTray);

            TrayHarness.PumpUntil(() => second.IsDisposed, "The other display's window was not disposed when the setting changed.");
            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            Assert.AreEqual(1, rig.Surfaces.Count(s => !s.IsDisposed), "Exactly one gauge is left.");
            Assert.IsFalse(rig.ShownOn(Left1080)!.IsDisposed);
            Assert.AreEqual("", rig.Tray.Settings.Current.Widget.GaugeDisplay);
            PumpFor(TimeSpan.FromMilliseconds(150));
            int reads = reader.ReadCount;
            PumpFor(TimeSpan.FromMilliseconds(250));
            Assert.AreEqual(reads, reader.ReadCount, "No gauge is left reading the other taskbar.");

            rig.Tray.Context.WidgetCardHostForTest.SetGaugeDisplay(GaugeDisplayChoice.AllDisplays, CardPlace.NearTray);
            TrayHarness.PumpUntil(() => rig.Tray.Context.SecondaryGaugeCountForTest == 1, "Choosing All displays again did not add the other display's gauge.");
            Assert.AreEqual(GaugeDisplayChoice.AllDisplays, rig.Tray.Settings.Current.Widget.GaugeDisplay, "The choice is stored.");
        });
    }

    // Turning the gauge off stops the main taskbar's reads for good, so nothing but the settings change itself can take the other
    // displays' gauges down. The poll interval is long here for the same reason: a read that came by is not what removes them.
    [TestMethod]
    public void TurningTheGaugeOffTakesTheOtherDisplaysGaugesDownAtOnceWithNoMoreReadsOfTheMainTaskbar()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DisplayInfo scaled = Two with { Dpi = 144 };
            using Rig rig = StartRigCore(GaugeDisplayChoice.AllDisplays, secondaryBars: true, [One, scaled], pollIntervalMs: 600000);
            FeedSecondary(rig, scaled);
            Poke(rig);
            TrayHarness.PumpUntil(() => rig.ShownOn(Right1080) is not null, "The second gauge was never shown. " + Why(rig));
            TestSurface second = rig.ShownOn(Right1080)!;
            FakeTaskbarReader reader = rig.ReaderFor(IdTwo)!;

            rig.Tray.ClickMenu(WidgetCopy.ShowOnTaskbar);

            TrayHarness.PumpUntil(() => !rig.Tray.Settings.Current.Widget.ShowOnTaskbar, "The menu item did not turn the gauge off.");
            TrayHarness.PumpUntil(() => second.IsDisposed, "The other display's gauge window was not disposed when the gauge was turned off.");
            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            PumpFor(TimeSpan.FromMilliseconds(100));
            int reads = reader.ReadCount;
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(reads, reader.ReadCount, "No gauge is left reading the other taskbar.");
        });
    }

    // Closing the tray takes every gauge down with it: each has a watcher thread and a window of its own.
    [TestMethod]
    public void ClosingTheTrayDisposesTheOtherDisplaysGaugesAndStopsReadingTheirTaskbars()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "The gauges were never shown.");
            TestSurface second = rig.ShownOn(Right1080)!;
            FakeTaskbarReader reader = rig.ReaderFor(IdTwo)!;

            rig.Dispose();

            Assert.IsTrue(second.IsDisposed, "The other display's gauge window was left behind when the tray closed.");
            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            int reads = reader.ReadCount;
            Thread.Sleep(200);
            Assert.AreEqual(reads, reader.ReadCount, "The other display's taskbar is still being read after the tray closed.");
        });
    }

    [TestMethod]
    public void SwitchingFromAllDisplaysToANamedDisplayLeavesExactlyOneGaugeToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            FeedSecondary(rig, Two);
            TrayHarness.PumpUntil(() => rig.ShownOn(Right1080) is not null, "The second gauge was never shown.");
            TestSurface second = rig.ShownOn(Right1080)!;

            rig.Tray.Context.WidgetCardHostForTest.SetGaugeDisplay(IdTwo, CardPlace.NearTray);

            TrayHarness.PumpUntil(() => second.IsDisposed, "The other display's window was not disposed when a named display was chosen.");
            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            TrayHarness.PumpUntil(() => rig.Tray.TaskbarReaders[0].LastChosenDisplay == IdTwo, "The one gauge was not pointed at the named display.");
        });
    }

    [TestMethod]
    public void WithAllDisplaysAndNoSecondaryTaskbarItBehavesLikeMainDisplay()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRigCore(GaugeDisplayChoice.AllDisplays, secondaryBars: false, [One, Two]);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null, "The main gauge was never shown.");
            PumpThroughMainReads(rig, 4);

            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            Assert.AreEqual(1, rig.Tray.TaskbarReaders.Count, "No second reader was built.");
            Assert.AreEqual(1, rig.Surfaces.Count);
        });
    }

    [TestMethod]
    public void WithOneDisplayAllDisplaysIsTheMainGaugeOnly()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays, One);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null, "The main gauge was never shown.");
            PumpThroughMainReads(rig, 4);

            Assert.AreEqual(0, rig.Tray.Context.SecondaryGaugeCountForTest);
            Assert.AreEqual(1, rig.Surfaces.Count);
        });
    }

    [TestMethod]
    public void AReadThatFellBackToTheMainTaskbarIsNotDrawnAsTheOtherDisplaysGauge()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            TrayHarness.PumpUntil(() => rig.ReaderFor(IdTwo) is not null, "No reader for Display 2.");

            // The reader's own fallback: the display shows no taskbar, so it read the main one.
            rig.ReaderFor(IdTwo)!.SetNextResult(ITaskbarReader.Result.Ok(BarLayout(One, secondary: false, DisplayFallbackReason.TaskbarNotShown)));
            TrayHarness.PumpUntil(() => rig.ReaderFor(IdTwo)!.ReadCount >= 3, "The reader did not read.");
            rig.Tray.PumpUntilIdle();

            // The main gauge's own reader was given the main taskbar earlier, so one window is shown, and it is the main one.
            Assert.IsInstanceOfType<GaugeState.Hidden>(rig.Tray.Context.SecondaryGaugesForTest.Single().State);
            Assert.AreEqual(1, rig.Surfaces.Count(s => s.ShowCount > 0), "The fallback read was not drawn as a second gauge on the main taskbar.");
            Assert.IsNotNull(rig.ShownOn(Left1080));
        });
    }

    // The card is placed where the clicked gauge is: above it, in the work area of its display, at its display's scale. Checked on the
    // rectangle the card was put at, which an anchor taken from somewhere else (no gauge, the cursor) would not give.
    // The display this gauge is for has become the main one: its reader now reads the main taskbar with no fallback to name, and the
    // main gauge is already on that bar.
    [TestMethod]
    public void AReadOfTheMainTaskbarThatDoesNotSayItFellBackIsNotDrawnAsASecondGaugeThere()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
            TrayHarness.PumpUntil(() => rig.ReaderFor(IdTwo) is not null, "No reader for Display 2.");

            rig.ReaderFor(IdTwo)!.SetNextResult(ITaskbarReader.Result.Ok(BarLayout(One, secondary: false)));
            TrayHarness.PumpUntil(() => rig.ReaderFor(IdTwo)!.ReadCount >= 3, "The reader did not read.");
            rig.Tray.PumpUntilIdle();

            Assert.AreEqual(
                1, rig.Surfaces.Count(s => s.ShowCount > 0 && !s.IsDisposed && !s.Hidden && Left1080.Contains(s.Bounds!.Value)),
                "Two gauges on the main taskbar. " + Why(rig));
            Assert.IsInstanceOfType<GaugeState.Hidden>(rig.Tray.Context.SecondaryGaugesForTest.Single().State);
        });
    }

    [TestMethod]
    public void AClickOnEachGaugeOpensTheCardAboveThatGaugeOnItsOwnDisplayAtThatDisplaysScale()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DisplayInfo scaled = Two with { Dpi = 144 };
            using Rig rig = StartRig(GaugeDisplayChoice.AllDisplays, One, scaled);
            FeedSecondary(rig, scaled);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "The gauges were never shown.");
            TestSurface second = rig.ShownOn(Right1080)!;
            TestSurface first = rig.ShownOn(Left1080)!;

            second.Click();
            TrayHarness.PumpUntil(() => rig.Tray.Context.WidgetCardIsShownForTest, "The card never opened from the second gauge. " + Why(rig));

            Rectangle onSecond = rig.Tray.Context.WidgetCardRestBoundsForTest!.Value;
            GaugePosition position = rig.Tray.Settings.Current.Widget.GaugePosition;
            Assert.AreEqual(
                WidgetCardPlacement.Above(second.Bounds!.Value, onSecond.Size, scaled.WorkArea, 144, position), onSecond,
                "The card sits above the gauge that was clicked, in its display's work area, at its display's scale.");
            Assert.IsTrue(scaled.WorkArea.Contains(onSecond) && onSecond.Bottom <= second.Bounds.Value.Top, "On the second display and above its gauge.");

            // A second click on the same gauge closes it. The main gauge keeps its own window, which a fake surface is not, so with
            // fakes it has no bounds to anchor on and the card goes by the cursor, which is on the main display.
            second.Click();
            TrayHarness.PumpUntil(() => !rig.Tray.Context.WidgetCardIsShownForTest, "A second click on the gauge did not close the card.");
            first.Click();
            TrayHarness.PumpUntil(() => rig.Tray.Context.WidgetCardIsShownForTest, "The card never opened from the main gauge.");
            Rectangle onMain = rig.Tray.Context.WidgetCardRestBoundsForTest!.Value;
            Assert.IsTrue(One.WorkArea.Contains(onMain), "A click on the main gauge opens the card on the main display: " + onMain);
        });
    }

    // The card's work area is the one of the display its anchor is on, so an anchor on the second display's gauge gives that
    // display's work area, not the main display's.
    [TestMethod]
    public void ACardAnchoredOnTheSecondGaugeTakesTheSecondDisplaysWorkArea()
    {
        List<DisplayArea> areas = new DisplayInfo[] { One, Two }.Select(d => new DisplayArea(d.Bounds, d.WorkArea, d.IsPrimary)).ToList();
        var anchor = new Rectangle(Right1080.Right - 300, Right1080.Bottom - 44, 74, 40);

        Rectangle area = WidgetCardPlacement.WorkAreaFor(anchor, areas, Rectangle.Empty);

        Assert.AreEqual(Two.WorkArea, area);
    }

    // ----- the set, for what a tray cannot show -----

    private sealed class SetRig : IDisposable
    {
        public FakeDisplays Displays { get; } = new(One, Two, Three);

        public FakeTaskbars Taskbars { get; } = new();

        public List<TestSurface> Surfaces { get; } = [];

        public List<FakeTaskbarReader> Readers { get; } = [];

        public Queue<Action> Posted { get; } = new();

        public FakeTrayIcon Icon { get; } = new();

        public CapturingLog Log { get; } = new();

        public SecondaryGaugeSet Set { get; }

        // Called as each reader is built, for a test that has the set asked to reconcile again while it is part way through.
        public Action? OnReaderBuilt { get; set; }

        // The taskbar over a gauge on another display is that display's own, a Shell_SecondaryTrayWnd.
        public SetRig(bool deferred = false)
        {
            Taskbars.Set(BarOn(Two, 0x5000), BarOn(Three, 0x5001));
            var parts = new SecondaryGaugeParts(
                () =>
                {
                    var reader = new FakeTaskbarReader();
                    lock (Readers)
                    {
                        Readers.Add(reader);
                    }

                    OnReaderBuilt?.Invoke();
                    return reader;
                },
                () =>
                {
                    var surface = new TestSurface { WindowHandle = 0x4000 + Surfaces.Count };
                    lock (Surfaces)
                    {
                        Surfaces.Add(surface);
                    }

                    return surface;
                },
                () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false),
                Log,
                TimeProvider.System,
                () => new FakeCoverProbe { Next = new GaugeCover(IsGauge: false, RootClassName: "Shell_SecondaryTrayWnd", BelongsToExplorer: true) },
                action =>
                {
                    if (deferred)
                    {
                        lock (Posted)
                        {
                            Posted.Enqueue(action);
                        }
                    }
                    else
                    {
                        action();
                    }
                },
                () => null,
                600000,
                new TrayIconVotes(Icon));
            Set = new SecondaryGaugeSet(parts, Displays, Taskbars.Read);
        }

        // Gives each gauge's reader the read its own display's taskbar would give, once every reader has said which display it is for.
        public void Feed()
        {
            TrayHarness.PumpUntil(
                () =>
                {
                    lock (Readers)
                    {
                        return Readers.Count == 2 && Readers.All(r => r.LastChosenDisplay is not null);
                    }
                },
                "The readers never read.");
            foreach (FakeTaskbarReader reader in Readers.ToList())
            {
                reader.SetNextResult(ITaskbarReader.Result.Ok(BarLayout(reader.LastChosenDisplay == IdThree ? Three : Two, secondary: true)));
            }

            foreach (SecondaryGauge gauge in Set.Gauges)
            {
                gauge.Poke();
            }
        }

        public void Dispose() => Set.Dispose();
    }

    [TestMethod]
    public void TheSetKeepsOneGaugePerSecondaryDisplayThatHasATaskbarAndNoneForTheMainDisplay()
    {
        using var rig = new SetRig();

        rig.Set.Reconcile(wanted: true);
        rig.Set.Reconcile(wanted: true);

        CollectionAssert.AreEquivalent(new[] { IdTwo, IdThree }, rig.Set.Gauges.Select(g => g.DisplayId).ToArray());
        Assert.AreEqual(2, rig.Readers.Count, "A second Reconcile adds nothing again.");

        rig.Set.Reconcile(wanted: false);
        Assert.AreEqual(0, rig.Set.Count);
    }

    // A reconcile asked for while one is running (a display change arriving during a wait on the UI thread) is run after it, not
    // inside it: run inside, it adds the gauge the outer pass has not stored yet, and the outer pass then stores another over it.
    [TestMethod]
    public void AReconcileAskedForWhileOneIsRunningIsRunAfterItAndLeavesNoGaugeRunningWithNothingToStopIt()
    {
        using var rig = new SetRig();
        bool asked = false;
        rig.OnReaderBuilt = () =>
        {
            if (!asked)
            {
                asked = true;
                rig.Set.Reconcile(wanted: true);
            }
        };

        rig.Set.Reconcile(wanted: true);

        CollectionAssert.AreEquivalent(new[] { IdTwo, IdThree }, rig.Set.Gauges.Select(g => g.DisplayId).ToArray());
        Assert.AreEqual(2, rig.Readers.Count, "Each gauge was built once: a third reader is a gauge that was stored over and never stopped.");

        rig.Set.Reconcile(wanted: false);
        Assert.AreEqual(0, rig.Set.Count);
    }

    [TestMethod]
    public void AReconcileThatTheSettingEndedWhileOneWasRunningEndsWithNoGauges()
    {
        using var rig = new SetRig();
        bool asked = false;
        rig.OnReaderBuilt = () =>
        {
            if (!asked)
            {
                asked = true;
                rig.Set.Reconcile(wanted: false);
            }
        };

        rig.Set.Reconcile(wanted: true);

        Assert.AreEqual(0, rig.Set.Count, "The newest answer was that no gauge is wanted.");
        Assert.IsTrue(rig.Surfaces.All(s => s.IsDisposed), "No window is left behind.");
    }

    [TestMethod]
    public void TheMainDisplaysOwnTaskbarWindowIsNeverTakenForASecondaryGauge()
    {
        using var rig = new SetRig();
        rig.Taskbars.Set(BarOn(One, 0x5000));

        rig.Set.Reconcile(wanted: true);

        Assert.AreEqual(0, rig.Set.Count);
    }

    [TestMethod]
    public void AClickRightClickAndConnectOnEachGaugeAreReportedForThatGauge()
    {
        using var rig = new SetRig();
        rig.Set.Reconcile(wanted: true);
        var cards = new List<string>();
        var menus = new List<(string Id, Point Point)>();
        var toggles = new List<string>();
        rig.Set.CardRequested += (_, g) => cards.Add(g.DisplayId);
        rig.Set.MenuRequested += (_, e) => menus.Add((e.Gauge.DisplayId, e.Point));
        rig.Set.ToggleRequested += (_, g) => toggles.Add(g.DisplayId);
        rig.Feed();

        TrayHarness.PumpUntil(() => rig.Surfaces.Count == 2 && rig.Surfaces.All(s => s.ShowCount > 0), "Both gauges were not shown.");
        TestSurface onThree = rig.Surfaces.Single(s => Far1080.Contains(s.Bounds!.Value));
        TestSurface onTwo = rig.Surfaces.Single(s => Right1080.Contains(s.Bounds!.Value));

        onThree.Click();
        onTwo.RightClick(new Point(3700, 1050));

        CollectionAssert.AreEqual(new[] { IdThree }, cards);
        CollectionAssert.AreEqual(new[] { (IdTwo, new Point(3700, 1050)) }, menus);
        Assert.IsEmpty(toggles);
    }

    // A read that was posted to the UI thread before the gauge was removed must find nothing left to move or draw.
    [TestMethod]
    public void ARemovedGaugeIsNeverShownOrDrawnByAReadThatWasAlreadyPosted()
    {
        using var rig = new SetRig(deferred: true);
        rig.Set.Reconcile(wanted: true);
        TrayHarness.PumpUntil(() => rig.Readers.Count == 2, "The readers were not built.");
        foreach (FakeTaskbarReader reader in rig.Readers)
        {
            reader.SetNextResult(ITaskbarReader.Result.Ok(BarLayout(Two, secondary: true)));
        }

        foreach (SecondaryGauge gauge in rig.Set.Gauges)
        {
            gauge.Poke();
        }

        TrayHarness.PumpUntil(() => { lock (rig.Posted) { return rig.Posted.Count >= 2; } }, "The reads were never posted.");

        rig.Set.Reconcile(wanted: false);
        int laidOutAfterRemoval = 0;
        rig.Set.LaidOut += (_, _) => laidOutAfterRemoval++;
        List<Action> late;
        lock (rig.Posted)
        {
            late = [.. rig.Posted];
            rig.Posted.Clear();
        }

        foreach (Action action in late)
        {
            action();
        }

        Assert.IsEmpty(rig.Surfaces, "A removed gauge made no window from a read that arrived after it was removed.");
        Assert.AreEqual(0, laidOutAfterRemoval, "Nothing was told to draw or place a gauge that was already removed.");
        Assert.AreEqual(0, rig.Set.Count);
    }

    // The sliding raise limit lives in each gauge's own controller, so one gauge's raises never use up another's.
    [TestMethod]
    public void TheRaiseLimitIsCountedPerGaugeSoOneGaugesRaisesNeverUseUpAnothers()
    {
        using var rig = new SetRig();
        rig.Set.Reconcile(wanted: true);
        rig.Feed();

        TrayHarness.PumpUntil(() => rig.Surfaces.Count == 2 && rig.Surfaces.All(s => s.ShowCount > 0), "Both gauges were not shown.");
        SecondaryGauge busy = rig.Set.Gauges.Single(g => g.DisplayId == IdTwo);
        SecondaryGauge quiet = rig.Set.Gauges.Single(g => g.DisplayId == IdThree);
        TestSurface busySurface = rig.Surfaces.Single(s => Right1080.Contains(s.Bounds!.Value));
        TestSurface quietSurface = rig.Surfaces.Single(s => Far1080.Contains(s.Bounds!.Value));

        for (int i = 0; i < GaugeController.RaisesPerWindow + 3; i++)
        {
            busy.OnForegroundChanged("SomeWindow");
        }

        quiet.OnForegroundChanged("SomeWindow");

        Assert.AreEqual(GaugeController.RaisesPerWindow, busySurface.RaiseCount, "The busy gauge is held to the limit.");
        Assert.AreEqual(1, quietSurface.RaiseCount, "The other gauge's raise is not refused for the busy one's.");
    }

    // ----- the icon while the tray closes -----

    // The main gauge goes while another stays shown, so the icon is held hidden by the other's vote alone. Closing the tray takes
    // that gauge down, and the icon must not come back as it goes.
    private static (Rig Rig, FakeTrayIcon Icon) RigWithTheIconHeldHiddenByTheOtherGauge()
    {
        Rig rig = StartRig(GaugeDisplayChoice.AllDisplays);
        FeedSecondary(rig, Two);
        TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is not null && rig.ShownOn(Right1080) is not null, "The gauges were never shown.");
        rig.Tray.TaskbarReaders[0].SetNextResult(
            ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, new StepOutcome("fake", false, 0, "S_OK", null))));
        TrayHarness.PumpUntil(() => rig.Tray.Context.WidgetGaugeStateForTest is GaugeState.Hidden, "The main gauge never hid.");
        rig.Tray.Time.Advance(TimeSpan.FromSeconds(3));
        rig.Tray.PumpUntilIdle();
        FakeTrayIcon icon = rig.Tray.TrayIcons.Single();
        TrayHarness.PumpUntil(() => !icon.Visible, "The other gauge did not hold the icon hidden.");
        return (rig, icon);
    }

    [TestMethod]
    public void TheIconStaysHiddenWhileTheGaugesAreTakenDownAsTheTrayCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (Rig rig, FakeTrayIcon icon) = RigWithTheIconHeldHiddenByTheOtherGauge();
            int mark = icon.VisibilityChanges.Count;

            rig.Dispose();

            Assert.IsFalse(icon.Visible, "The icon was shown again while the tray closed: " + string.Join(",", icon.VisibilityChanges.Skip(mark)));
        });
    }

    // Exit waits for what is in flight with the gauges still being matched to the displays. They are removed as soon as the tray is
    // closing, which is while the icon is already gone.
    [TestMethod]
    public void TheIconStaysHiddenWhileExitWaitsAndTheGaugesAreRemoved()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (Rig rig, FakeTrayIcon icon) = RigWithTheIconHeldHiddenByTheOtherGauge();
            using Rig keep = rig;
            var release = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Tray.Connection.OnConnect = _ => release.Task;
            rig.Tray.ClickMenu(MenuModel.Connect);
            int mark = icon.VisibilityChanges.Count;

            rig.Tray.ClickMenu(MenuModel.Exit);
            TrayHarness.PumpUntil(() => rig.Tray.Context.SecondaryGaugeCountForTest == 0, "The gauges were not removed once Exit began. " + Why(rig));

            Assert.IsFalse(icon.Visible, "The icon was shown again while Exit was waiting: " + string.Join(",", icon.VisibilityChanges.Skip(mark)));
            release.SetResult(new ConnectResult(ConnectOutcome.Failed, "Cancelled", []));
            rig.Tray.PumpUntilIdle();
        });
    }

    // ----- the raise -----

    // Under its own display's taskbar a gauge is raised by the shell events the same as the main one: the cover there is a
    // Shell_SecondaryTrayWnd, not a Shell_TrayWnd.
    [TestMethod]
    public void ASecondaryGaugeCoveredByItsOwnDisplaysTaskbarIsRaisedWhenAWindowTakesTheForeground()
    {
        using var rig = new SetRig();
        rig.Set.Reconcile(wanted: true);
        rig.Feed();
        TrayHarness.PumpUntil(() => rig.Surfaces.Count == 2 && rig.Surfaces.All(s => s.ShowCount > 0), "Both gauges were not shown.");
        SecondaryGauge gauge = rig.Set.Gauges.Single(g => g.DisplayId == IdTwo);
        TestSurface surface = rig.Surfaces.Single(s => Right1080.Contains(s.Bounds!.Value));

        gauge.OnForegroundChanged("Shell_SecondaryTrayWnd");

        Assert.AreEqual(1, surface.RaiseCount, "The gauge under its own taskbar was not raised.");
    }

    // ----- the card's scale -----

    // The card opened from another display's gauge is drawn at that gauge's scale. The main gauge is read from the main taskbar and is
    // drawn at the main display's own scale, with the card open or not: here the main taskbar is read again after the click, which is
    // when the tray learns the main display's scale.
    [TestMethod]
    public void AReadOfTheMainTaskbarWhileTheCardIsOpenFromAnotherDisplaysGaugeDrawsTheMainGaugeAtItsOwnScale()
    {
        Phase5.CardDesktop.Run(() =>
        {
            DisplayInfo scaled = Two with { Dpi = 144 };
            using Rig rig = StartRigCore(GaugeDisplayChoice.AllDisplays, secondaryBars: true, [One, scaled], pollIntervalMs: 600000);
            FeedSecondary(rig, scaled);
            Poke(rig);
            TrayHarness.PumpUntil(() => rig.ShownOn(Left1080) is { RenderCount: > 0 } && rig.ShownOn(Right1080) is { RenderCount: > 0 }, "A gauge was never drawn. " + Why(rig));
            TestSurface second = rig.ShownOn(Right1080)!;
            TestSurface first = rig.ShownOn(Left1080)!;

            second.Click();
            TrayHarness.PumpUntil(() => rig.Tray.Context.WidgetCardIsShownForTest, "The card never opened from the second display's gauge.");
            int mark = first.DrawnScales.Count;
            int reads = rig.Tray.TaskbarReaders[0].ReadCount;
            Poke(rig);
            TrayHarness.PumpUntil(() => rig.Tray.TaskbarReaders[0].ReadCount > reads && first.DrawnScales.Count > mark, "The main taskbar was not read and drawn again.");
            rig.Tray.PumpUntilIdle();

            List<int> after = first.DrawnScales.Skip(mark).ToList();
            Assert.IsTrue(rig.Tray.Context.WidgetCardIsShownForTest, "The card closed before the draws were seen, so they prove nothing.");
            Assert.IsTrue(after.Count > 0 && after.All(dpi => dpi == 96), "The main gauge was drawn at: " + string.Join(",", after));
        });
    }

    // A settings change broadcast, which makes the tray measure the main taskbar again at once.
    private static void Poke(Rig rig)
    {
        var poke = Message.Create(rig.Tray.Context.Window.Handle, 0x001A, 0, 0);
        rig.Tray.Context.Window.Dispatch(ref poke);
    }
}
