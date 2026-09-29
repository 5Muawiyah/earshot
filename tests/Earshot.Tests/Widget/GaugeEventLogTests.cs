using System.Drawing;
using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every hide, show, move, cover and raise of the gauge writes one line in a fixed format, so a day of use
// leaves evidence of what made it flicker. The format is pinned here; the controller tests below drive every
// path that hides the gauge and check the reason each one names.
[TestClass]
public sealed class GaugeEventLogTests
{
    private const string StartOfClock = "2026-09-19T12:00:00.000Z";

    private static readonly Rectangle Bar = new(0, 1032, 1920, 48);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);

    private static readonly WindowIdentity Chrome = new("Chrome_WidgetWin_1", BelongsToExplorer: false);
    private static readonly WindowIdentity ShellCore = new("Windows.UI.Core.CoreWindow", BelongsToExplorer: true);

    [TestMethod]
    public void AHideLineHasAFixedShape()
    {
        var e = new GaugeEvent(GaugeEventKind.Hide, GaugeReasons.Covered, new DateTimeOffset(2026, 9, 29, 14, 36, 12, 345, TimeSpan.Zero))
        {
            Bounds = new Rectangle(1183, 1032, 74, 40),
            ShownFor = TimeSpan.FromMilliseconds(4200),
            Window = ShellCore,
            Foreground = Chrome,
            Detail = "quns=3",
        };

        Assert.AreEqual(
            "Gauge: hide reason=covered t=2026-09-29T14:36:12.345Z bounds=1183,1032,74x40 shown_ms=4200 " +
            "window.class=Windows.UI.Core.CoreWindow window.explorer=true fg.class=Chrome_WidgetWin_1 fg.explorer=false detail=quns=3",
            GaugeEventLog.Format(e));
    }

    [TestMethod]
    public void AMoveLineCarriesTheOldRectangleAndAFailureCarriesItsRawCode()
    {
        var move = new GaugeEvent(GaugeEventKind.Move, GaugeReasons.LayoutChanged, new DateTimeOffset(2026, 9, 29, 14, 36, 12, 0, TimeSpan.Zero))
        {
            Bounds = new Rectangle(1200, 1036, 74, 40),
            From = new Rectangle(1183, 1036, 74, 40),
        };
        Assert.AreEqual(
            "Gauge: move reason=layout-changed t=2026-09-29T14:36:12.000Z bounds=1200,1036,74x40 from=1183,1036,74x40",
            GaugeEventLog.Format(move));

        var failed = new GaugeEvent(GaugeEventKind.Show, GaugeReasons.Placed, new DateTimeOffset(2026, 9, 29, 14, 36, 12, 0, TimeSpan.Zero))
        {
            Failure = new StepOutcome("set-window-pos:show-gauge", false, 1400, "ERROR_INVALID_WINDOW_HANDLE", "the handle is gone"),
        };
        Assert.AreEqual(
            "Gauge: show reason=placed t=2026-09-29T14:36:12.000Z code=1400 name=ERROR_INVALID_WINDOW_HANDLE step=set-window-pos:show-gauge",
            GaugeEventLog.Format(failed));
    }

    [TestMethod]
    public void ATitleShapedValueCannotBreakTheLineIntoExtraFields()
    {
        var e = new GaugeEvent(GaugeEventKind.Cover, GaugeReasons.WindowOverGauge, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero))
        {
            Window = new WindowIdentity("Evil class reason=fake\r\nGauge: hide", BelongsToExplorer: false),
        };

        string line = GaugeEventLog.Format(e);

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.AreEqual(1, Regex.Count(line, " reason="), "Only one reason field: " + line);
        Assert.AreEqual(1, Regex.Count(line, " window.explorer="), "Only one window.explorer field: " + line);
        StringAssert.Matches(line, new Regex(@" window\.class=Evil_class_reason_fake__Gauge:_hide window\.explorer=false$"));
    }

    private static TaskbarLayout FreeSpace() =>
        new(0, Bar, TaskbarEdge.Bottom, false, new Rectangle(0, 0, 1920, 1080),
            [Start, new Rectangle(807, 1032, 44, 48), new Rectangle(1678, 1032, 242, 48)], Start,
            96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new Rectangle(1678, 1032, 242, 48));

    private sealed class Rig
    {
        public FakeGaugeSurface Surface { get; } = new();

        public FakeTrayIcon Icon { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public bool Enabled { get; set; } = true;

        public GaugeController Controller { get; }

        public Rig()
        {
            Controller = new GaugeController(() => Surface, Icon, () => new GaugeControllerSettings(Enabled, LeftClickConnects: false), Log, Time);
        }

        public void Layout(TaskbarLayout layout) => Controller.OnLayout(ITaskbarReader.Result.Ok(layout));

        public List<string> Lines() =>
            Log.Entries.Select(e => e.Message).Where(m => m.StartsWith(GaugeEventLog.Prefix, StringComparison.Ordinal)).ToList();

        public string OnlyLine(string kindAndReason)
        {
            List<string> found = Lines().Where(l => l.StartsWith("Gauge: " + kindAndReason + " ", StringComparison.Ordinal)).ToList();
            Assert.HasCount(1, found, "Expected exactly one '" + kindAndReason + "' line. Log: " + string.Join(" | ", Lines()));
            return found[0];
        }
    }

    [TestMethod]
    public void EveryLineStartsWithThePrefixAndCarriesATimestamp()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome });

        Assert.IsNotEmpty(rig.Lines());
        foreach (string line in rig.Lines())
        {
            StringAssert.StartsWith(line, "Gauge: ");
            StringAssert.Matches(line, new Regex(@" t=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z"));
        }
    }

    [TestMethod]
    public void ShowingTheGaugeWritesAShowLineWithItsBounds()
    {
        var rig = new Rig();

        rig.Layout(FreeSpace());

        string line = rig.OnlyLine("show reason=placed");
        StringAssert.Contains(line, " t=" + StartOfClock);
        StringAssert.Matches(line, new Regex(@" bounds=\d+,\d+,\d+x\d+"));
        StringAssert.Contains(line, "was=off");
    }

    [TestMethod]
    public void ACoveredTaskbarWritesOneCoveredLineNamingTheWindowClassAndExplorerOwnership()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Time.Advance(TimeSpan.FromSeconds(3));

        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome, Foreground = ShellCore });
        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome, Foreground = ShellCore });

        string line = rig.OnlyLine("hide reason=covered");
        StringAssert.Contains(line, "shown_ms=3000");
        StringAssert.Contains(line, "window.class=Chrome_WidgetWin_1 window.explorer=false");
        StringAssert.Contains(line, "fg.class=Windows.UI.Core.CoreWindow fg.explorer=true");
    }

    [TestMethod]
    public void AFullScreenStateNamesItsNotificationCode()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { NotificationState = Shell.QUNS_RUNNING_D3D_FULL_SCREEN });

        string line = rig.OnlyLine("hide reason=full-screen-state");
        StringAssert.Contains(line, "quns=" + Shell.QUNS_RUNNING_D3D_FULL_SCREEN);
    }

    [TestMethod]
    public void TheAppBarNotificationFastPathHasItsOwnReason()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.NotifyFullScreenApp(opening: true);

        rig.OnlyLine("hide reason=full-screen-app-notified");
    }

    [TestMethod]
    public void ATaskbarReadFailureNamesTheStepAndCarriesTheRawCode()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        StepOutcome outcome = StepOutcomes.FromHResult("uia:find-all-build-cache", unchecked((int)0x80004005));

        rig.Controller.OnLayout(ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.Occupants, outcome)));
        rig.Controller.OnLayout(ITaskbarReader.Result.Fail(new TaskbarReadFailure(TaskbarReadFailureStep.Occupants, outcome)));

        string line = rig.OnlyLine("hide reason=read-failed");
        StringAssert.Contains(line, "step=uia:find-all-build-cache");
        StringAssert.Contains(line, "detail=step=Occupants");
        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Level == LogLevel.Error), "A repeat of the same failure is not a second Error.");
    }

    [TestMethod]
    public void NoTaskbarIsItsOwnReason()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.OnLayout(ITaskbarReader.Result.Fail(new TaskbarReadFailure(
            TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromWin32("find-window:Shell_TrayWnd", 0, ok: false))));

        rig.OnlyLine("hide reason=no-taskbar");
    }

    [TestMethod]
    public void NoFreeSpaceIsItsOwnReason()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        var tight = FreeSpace() with
        {
            Occupied = [Start, new Rectangle(807, 1032, 800, 48), new Rectangle(1678, 1032, 242, 48)],
        };

        rig.Layout(tight);

        rig.OnlyLine("hide reason=no-free-space");
    }

    [TestMethod]
    public void AFailedShowIsWrittenOnceWithItsCodeAndThenHidesAsWindowFailed()
    {
        var rig = new Rig();
        rig.Surface.NextShowAtResult = StepOutcomes.FromWin32("set-window-pos:show-gauge", 1400);

        rig.Layout(FreeSpace());

        string show = rig.OnlyLine("show reason=placed");
        StringAssert.Contains(show, "code=1400");
        StringAssert.Contains(show, "step=set-window-pos:show-gauge");
        string hide = rig.OnlyLine("hide reason=window-failed");
        Assert.DoesNotContain("code=1400", hide, "The failing step is written once, on the show line.");
    }

    [TestMethod]
    public void TurningTheSettingOffWritesOneLineAndAnIdleOffWritesNone()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Enabled = false;

        rig.Layout(FreeSpace());
        rig.Layout(FreeSpace());

        rig.OnlyLine("hide reason=setting-off");
    }

    [TestMethod]
    public void DisposingWhileShownWritesADisposedLine()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.Dispose();

        rig.OnlyLine("hide reason=disposed");
    }

    [TestMethod]
    public void MovingTheGaugeWritesAMoveLineWithTheOldRectangle()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { Occupied = [Start, new Rectangle(807, 1032, 88, 48), new Rectangle(1678, 1032, 242, 48)] });

        Assert.IsTrue(rig.Surface.Calls.Any(c => c.StartsWith("MoveTo", StringComparison.Ordinal)), "Sanity: the wider button moved the gauge.");
        string line = rig.OnlyLine("move reason=layout-changed");
        StringAssert.Matches(line, new Regex(@" from=\d+,\d+,\d+x\d+"));
    }

    [TestMethod]
    public void AWindowOverTheGaugeWritesACoverLineThenARaiseLineAndAConfirmingReadWritesNothing()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        int before = rig.Lines().Count;

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = true });
        Assert.AreEqual(before, rig.Lines().Count, "A read that confirms the gauge is on top is not an event.");

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = ShellCore, Foreground = Chrome });

        IReadOnlyList<string> lines = rig.Lines();
        Assert.HasCount(before + 2, lines);
        StringAssert.StartsWith(lines[^2], "Gauge: cover reason=window-over-gauge ");
        StringAssert.Contains(lines[^2], "window.class=Windows.UI.Core.CoreWindow window.explorer=true");
        StringAssert.StartsWith(lines[^1], "Gauge: raise reason=covered-at-centre ");
        StringAssert.Contains(lines[^1], "fg.class=Chrome_WidgetWin_1 fg.explorer=false");
    }

    [TestMethod]
    public void ShowingAgainAfterAHideRecordsWhatItWasHiddenFor()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome });

        rig.Layout(FreeSpace());

        List<string> shows = rig.Lines().Where(l => l.StartsWith("Gauge: show ", StringComparison.Ordinal)).ToList();
        Assert.HasCount(2, shows);
        StringAssert.Contains(shows[1], "was=hidden:Covered");
    }

    // The real helper against a real window (a hidden, never-shown STATIC control), not a fake: its class
    // name is read through the real GetClassNameW and its owner through the real process id.
    [TestMethod]
    public void TheRealWindowIdentityReaderReadsAClassNameAndTheOwningProcess()
    {
        var window = new NativeWindow();
        try
        {
            window.CreateHandle(new CreateParams { ClassName = "Static", Style = 0 });

            WindowIdentity? own = GaugeWindowIdentityReader.Read(window.Handle, (uint)Environment.ProcessId);
            WindowIdentity? foreign = GaugeWindowIdentityReader.Read(window.Handle, (uint)Environment.ProcessId + 1);

            Assert.IsNotNull(own);
            // WinForms registers its own class for the control, named after the one asked for.
            StringAssert.Contains(own.Value.ClassName, "Static", StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(own.Value.BelongsToExplorer, "The same process id as the one passed in.");
            Assert.IsNotNull(foreign);
            Assert.IsFalse(foreign.Value.BelongsToExplorer);
            Assert.IsNull(GaugeWindowIdentityReader.Read(0, 1));
            Assert.AreEqual((uint)Environment.ProcessId, GaugeWindowIdentityReader.ProcessOf(window.Handle));
        }
        finally
        {
            window.DestroyHandle();
        }
    }
}
