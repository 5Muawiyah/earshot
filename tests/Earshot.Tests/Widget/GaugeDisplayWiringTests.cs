using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// The gauge's display choice proved wired, through the real TrayContext and the real taskbar reader with only what cannot exist
// in a test replaced: the list of displays, the secondary taskbar windows, the foreground window and the UI Automation read.
// GaugeDisplayTests drive the rules with synthetic layouts; these prove the pieces are connected, so a tray that stops passing the
// chosen display, a reader that ignores it, a fallback that is never taken or a foreground reader that never reaches the
// full-screen rule fails a test. Every tray runs on a private desktop.
[TestClass]
public sealed class GaugeDisplayWiringTests
{
    // Invented identities in the shape Windows gives a monitor's device interface name.
    private const string IdOne = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
    private const string IdTwo = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID104#{monitor-interface}";
    private const string IdGone = @"\\?\DISPLAY#CCC0003#5&0a0a0a0a&0&UID300#{monitor-interface}";

    private static readonly Rectangle Left1080 = new(0, 0, 1920, 1080);
    private static readonly Rectangle Right1080 = new(1920, 0, 1920, 1080);

    private static DisplayInfo Display(string id, string device, Rectangle bounds, bool primary, nint handle) =>
        new(id, device, bounds, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - 48), primary, 96, handle);

    private static readonly DisplayInfo One = Display(IdOne, @"\\.\DISPLAY1", Left1080, true, 11);
    private static readonly DisplayInfo Two = Display(IdTwo, @"\\.\DISPLAY2", Right1080, false, 22);

    // A display list a test changes, as plugging a monitor in or out does.
    private sealed class FakeDisplaySource : IDisplaySource
    {
        private readonly Lock _gate = new();
        private List<DisplayInfo> _displays;

        public FakeDisplaySource(params DisplayInfo[] displays) => _displays = [.. displays];

        public int Reads { get; private set; }

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
                Reads++;
                return new DisplayReading([.. _displays]);
            }
        }
    }

    private static TaskbarLayout BarOn(Rectangle monitor, bool secondary = false, string label = "Display 1")
    {
        int thickness = 48;
        var bar = new Rectangle(monitor.X, monitor.Bottom - thickness, monitor.Width, thickness);
        int step = 44;
        var start = new Rectangle(monitor.X + 762, bar.Top, 45, thickness);
        var buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(monitor.X + 807 + (i * step), bar.Top, step, thickness)).ToList();
        var tray = new Rectangle(monitor.Right - 242, bar.Top, 242, thickness);
        return new TaskbarLayout(
            0, bar, TaskbarEdge.Bottom, false, monitor, [start, .. buttons, tray], start, 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: tray, DisplayCount: 2, ForegroundWindow: null, IsSecondary: secondary, DisplayLabel: label, DisplayFallback: DisplayFallbackReason.None);
    }

    private static ForegroundWindowReading Game(Rectangle monitor, string label) =>
        new(new WindowIdentity("GameWindowClass", false), monitor, monitor, label);

    private static Action<EarshotSettings> WidgetOn(string gaugeDisplay = "") =>
        s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true, GaugeDisplay = gaugeDisplay };

    // ----- the chosen display reaches the reader, through the tray -----

    [TestMethod]
    public void TheChosenDisplayIsWhatTheTaskbarReaderIsAskedForAndFollowsTheSettingWhenItChanges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var displays = new FakeDisplaySource(One, Two);
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), displaySource: displays, taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(IdTwo));
            tray.PumpUntilIdle();
            FakeTaskbarReader reader = tray.LastTaskbarReader!;

            TrayHarness.PumpUntil(() => reader.LastChosenDisplay == IdTwo, "The reader was never asked for the display that is stored. Asked for: " + reader.LastChosenDisplay);

            tray.Context.WidgetCardHostForTest.SetGaugeDisplay(IdOne, CardPlace.NearTray);
            TrayHarness.PumpUntil(() => reader.LastChosenDisplay == IdOne, "A change on the settings page did not reach the reader. Asked for: " + reader.LastChosenDisplay);

            tray.Context.WidgetCardHostForTest.SetGaugeDisplay(GaugeDisplayChoice.MainDisplay, CardPlace.NearTray);
            TrayHarness.PumpUntil(() => reader.LastChosenDisplay == GaugeDisplayChoice.MainDisplay, "The main display was not asked for. Asked for: " + reader.LastChosenDisplay);
        });
    }

    [TestMethod]
    public void WithNoChoiceTheReaderIsAskedForTheMainDisplay()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), displaySource: new FakeDisplaySource(One, Two), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn());
            tray.PumpUntilIdle();
            FakeTaskbarReader reader = tray.LastTaskbarReader!;

            TrayHarness.PumpUntil(() => reader.LastChosenDisplay is not null, "The reader never read.");

            Assert.AreEqual(GaugeDisplayChoice.MainDisplay, reader.LastChosenDisplay);
        });
    }

    // ----- the real reader works out which taskbar, and falls back -----

    private sealed class Recorded
    {
        public TaskbarTarget? Target { get; set; }

        public StepOutcome? TaskbarsProblem { get; set; }

        public DisplayReading? Reading { get; set; }
    }

    // The real reader's Read, with its displays, its taskbar windows and the UI Automation read replaced: what it resolves is what
    // Read hands on.
    private static (UiaTaskbarReader Reader, Recorded Seen) Reader(IDisplaySource displays, Func<SecondaryTaskbarReading> taskbars)
    {
        var seen = new Recorded();
        var reader = new UiaTaskbarReader(displays, taskbars, (target, _, reading, problem) =>
        {
            seen.Target = target;
            seen.Reading = reading;
            seen.TaskbarsProblem = problem;
            return ITaskbarReader.Result.Ok(BarOn(target.Display?.Bounds ?? Left1080, target.Secondary));
        });
        WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.TaskbarReader);
        return (reader, seen);
    }

    private static TaskbarWindowCandidate BarOnTwo(nint handle = 0x5001) => new(handle, new Rectangle(1920, 1032, 1920, 48), true, Two.Handle);

    [TestMethod]
    public void TheReaderReadsTheTaskbarOfTheDisplayItIsAskedFor()
    {
        (UiaTaskbarReader reader, Recorded seen) = Reader(new FakeDisplaySource(One, Two), () => new SecondaryTaskbarReading([BarOnTwo()]));

        reader.Read(null, IdTwo);

        Assert.AreEqual(Two, seen.Target!.Value.Display, "The chosen display's taskbar, not the main display's.");
        Assert.IsTrue(seen.Target.Value.Secondary);
        Assert.AreEqual((nint)0x5001, seen.Target.Value.TrayHandle);
        Assert.AreEqual(new Rectangle(1920, 1032, 1920, 48), seen.Target.Value.SecondaryBounds);
        Assert.AreEqual(DisplayFallbackReason.None, seen.Target.Value.Fallback);
    }

    [TestMethod]
    public void TheMainDisplayIsReadWhenNothingOrItIsChosen()
    {
        (UiaTaskbarReader reader, Recorded seen) = Reader(new FakeDisplaySource(One, Two), () => new SecondaryTaskbarReading([BarOnTwo()]));

        reader.Read(null, GaugeDisplayChoice.MainDisplay);
        Assert.AreEqual(One, seen.Target!.Value.Display);
        Assert.IsFalse(seen.Target.Value.Secondary);

        reader.Read(null, IdOne);
        Assert.AreEqual(One, seen.Target!.Value.Display);
        Assert.IsFalse(seen.Target.Value.Secondary, "The main display's taskbar is the main one, not a secondary.");

        reader.Read(null);
        Assert.AreEqual(One, seen.Target!.Value.Display, "The one-argument read is the main display's.");
    }

    [TestMethod]
    public void ADisplayThatShowsNoTaskbarIsReadAsTheMainDisplayAndTheFallbackIsSaid()
    {
        (UiaTaskbarReader reader, Recorded seen) = Reader(new FakeDisplaySource(One, Two), () => new SecondaryTaskbarReading([]));

        reader.Read(null, IdTwo);

        Assert.AreEqual(One, seen.Target!.Value.Display, "The owner turned the taskbar off on the other displays.");
        Assert.IsFalse(seen.Target.Value.Secondary);
        Assert.AreEqual(DisplayFallbackReason.TaskbarNotShown, seen.Target.Value.Fallback);
    }

    [TestMethod]
    public void ATaskbarThatIsHiddenOrOnAnotherDisplayIsNotTheChosenDisplaysAndADisplayThatIsGoneFallsBackToo()
    {
        TaskbarWindowCandidate hidden = BarOnTwo() with { Visible = false };
        TaskbarWindowCandidate onOne = BarOnTwo() with { Monitor = One.Handle };
        (UiaTaskbarReader reader, Recorded seen) = Reader(new FakeDisplaySource(One, Two), () => new SecondaryTaskbarReading([hidden, onOne]));

        reader.Read(null, IdTwo);
        Assert.AreEqual(DisplayFallbackReason.TaskbarNotShown, seen.Target!.Value.Fallback);

        reader.Read(null, IdGone);
        Assert.AreEqual(One, seen.Target!.Value.Display);
        Assert.AreEqual(DisplayFallbackReason.NotConnected, seen.Target.Value.Fallback);
    }

    // A taskbar window whose rectangle could not be read is not a display with no taskbar: the failure is kept with its raw code.
    [TestMethod]
    public void AFailedReadOfATaskbarWindowIsHandedOnWithItsRawCode()
    {
        StepOutcome failed = StepOutcomes.FromWin32("get-window-rect:Shell_SecondaryTrayWnd", 1400, ok: false);
        (UiaTaskbarReader reader, Recorded seen) = Reader(new FakeDisplaySource(One, Two), () => new SecondaryTaskbarReading([], failed));

        reader.Read(null, IdTwo);

        Assert.AreSame(failed, seen.TaskbarsProblem);
        Assert.AreEqual(1400, seen.TaskbarsProblem!.Code);
    }

    // A taskbar window whose rectangle cannot be read is left out of the list and the failure is kept, with the error code Windows
    // gave, instead of being dropped without a word.
    [TestMethod]
    public void ATaskbarWindowWhoseRectangleCannotBeReadIsRecordedWithItsErrorCodeAndTheOthersAreStillListed()
    {
        nint[] windows = [0x5001, 0x5002, 0x5003];
        SecondaryTaskbarReading reading = SystemDisplaySource.Collect(
            after => after == 0 ? windows[0] : windows.SkipWhile(w => w != after).Skip(1).FirstOrDefault(),
            w => w == 0x5002 ? (false, Rectangle.Empty, 1400u) : (true, new Rectangle(1920, 1032, 1920, 48), 0u),
            _ => true,
            _ => 22);

        Assert.HasCount(2, reading.Taskbars);
        Assert.AreEqual("get-window-rect:Shell_SecondaryTrayWnd", reading.Problem!.Step);
        Assert.AreEqual(1400, reading.Problem.Code, "The raw code is on record.");
        Assert.IsFalse(reading.Problem.Ok);
    }

    // The real enumeration of taskbar windows, run for real: on a desktop with none it finds none and has nothing to report.
    [TestMethod]
    public void TheRealSecondaryTaskbarEnumerationFindsNoneOnAPrivateDesktopAndReportsNoProblem()
    {
        Phase5.CardDesktop.Run(() =>
        {
            SecondaryTaskbarReading reading = SystemDisplaySource.SecondaryTaskbars();

            Assert.IsEmpty(reading.Taskbars);
            Assert.IsNull(reading.Problem);
        });
    }

    // ----- the foreground reader reaches the full-screen rule, through the tray -----

    [TestMethod]
    public void AGameOnAnotherDisplayLeavesTheGaugeShownAndOneOnTheGaugesDisplayHidesItBecauseTheTrayPassedItsReader()
    {
        Phase5.CardDesktop.Run(() =>
        {
            ForegroundWindowReading game = Game(Right1080, "Display 2");
            var displaysSeenByProbe = new List<int>();
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), displaySource: new FakeDisplaySource(One, Two), taskbarWatcherPollIntervalMs: 30,
                gaugeSurfaceFactory: () => new FakeGaugeSurface(),
                foregroundWindowProbe: displays =>
                {
                    lock (displaysSeenByProbe)
                    {
                        displaysSeenByProbe.Add(displays.Count);
                    }

                    return game;
                },
                settings: WidgetOn());
            tray.PumpUntilIdle();
            tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(BarOn(Left1080)));
            TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "The gauge was never shown on the main display.");

            var notice = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 1);
            tray.Context.Window.Dispatch(ref notice);

            Assert.IsInstanceOfType<GaugeState.Shown>(tray.Context.WidgetGaugeStateForTest, "A game on Display 2 must not hide the gauge on Display 1.");
            lock (displaysSeenByProbe)
            {
                Assert.AreEqual(2, displaysSeenByProbe.Distinct().Single(), "The probe is given the displays the tray read.");
            }

            game = Game(Left1080, "Display 1");
            tray.Context.Window.Dispatch(ref notice);

            GaugeState? state = tray.Context.WidgetGaugeStateForTest;
            Assert.IsInstanceOfType<GaugeState.Hidden>(state, "A game on the gauge's own display hides it.");
            Assert.AreEqual(HiddenReason.FullScreenNotified, ((GaugeState.Hidden)state!).Reason);
        });
    }

    // ----- the settings page follows the displays -----

    [TestMethod]
    public void ADisplayChoiceForADisplayThatHasGoneIsNotStoredAndOneThatIsThereIs()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var displays = new FakeDisplaySource(One);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), displaySource: displays, taskbarWatcherPollIntervalMs: 30, settings: WidgetOn());
            tray.PumpUntilIdle();

            tray.Context.WidgetCardHostForTest.SetGaugeDisplay(IdTwo, CardPlace.NearTray);
            tray.PumpUntilIdle();

            Assert.AreEqual("", tray.Settings.Current.Widget.GaugeDisplay, "Display 2 was unplugged after the list was drawn: the choice is not stored.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "no longer connected, so the choice was not stored"));

            displays.Set(One, Two);
            tray.Context.WidgetCardHostForTest.SetGaugeDisplay(IdTwo, CardPlace.NearTray);
            tray.PumpUntilIdle();

            Assert.AreEqual(IdTwo, tray.Settings.Current.Widget.GaugeDisplay, "Plugged in again, it is stored.");
        });
    }

    // WM_DISPLAYCHANGE while the settings page is open redraws it with the displays that are there now.
    [TestMethod]
    public void TheOpenSettingsPageIsRedrawnWithTheNewDisplayListWhenTheDisplaysChange()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var displays = new FakeDisplaySource(One);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), displaySource: displays, taskbarWatcherPollIntervalMs: 30, settings: WidgetOn());
            tray.PumpUntilIdle();
            tray.Context.RequestWidgetCardForTest();
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardIsShownForTest, "The card never opened.");
            tray.Context.OpenWidgetSettingsForTest();
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardSettingsForTest is not null, "The settings page never opened.");
            Assert.HasCount(2, tray.Context.WidgetCardSettingsForTest!.GaugeDisplayOptions, "Main display and Display 1.");

            displays.Set(One, Two);
            var changed = Message.Create(tray.Context.Window.Handle, 0x007E, 0, 0);
            tray.Context.Window.Dispatch(ref changed);

            TrayHarness.PumpUntil(() => tray.Context.WidgetCardSettingsForTest?.GaugeDisplayOptions.Count == 3, "The open page kept the old display list.");
        });
    }
}
