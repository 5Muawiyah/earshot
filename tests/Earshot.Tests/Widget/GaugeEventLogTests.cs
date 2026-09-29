using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every hide, show, move and raise of the gauge writes one line with its reason, so a day of use leaves the
// cause of any flicker in the log. The wording is pinned here; the controller tests drive every path that
// hides the gauge and check the reason each one names, and that nothing but a class ever names a window.
[TestClass]
public sealed class GaugeEventLogTests
{
    private static readonly Rectangle Bar = new(0, 1032, 1920, 48);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);

    private static readonly WindowIdentity Chrome = new("Chrome_WidgetWin_1", BelongsToExplorer: false);
    private static readonly WindowIdentity ShellCore = new("Windows.UI.Core.CoreWindow", BelongsToExplorer: true);

    // ---- The wording ----

    [TestMethod]
    public void EveryLineHasAFixedWording()
    {
        var r = new Rectangle(1596, 1036, 74, 40);
        StepOutcome failed = new("uia:find-all-build-cache", false, unchecked((int)0x80004005), "E_FAIL", "unspecified");
        StepOutcome window = new("set-window-pos:show-gauge", false, 1400, "ERROR_INVALID_WINDOW_HANDLE", null);

        Assert.AreEqual("Gauge shown at 1596,1036 74x40 (first layout).", GaugeEventLog.Shown(r, GaugeEventLog.ShownFirstLayout));
        Assert.AreEqual("Gauge moved to 1596,1036 74x40.", GaugeEventLog.Moved(r));
        Assert.AreEqual("Gauge hidden (NoTaskbar).", GaugeEventLog.HiddenNoTaskbar());
        Assert.AreEqual(
            "Gauge hidden (ReadFailed Occupants E_FAIL (" + failed.Code + ") after 3 consecutive failures).",
            GaugeEventLog.HiddenReadFailed("Occupants", failed, 3));
        Assert.AreEqual("Gauge hidden (NoFreeSpace).", GaugeEventLog.HiddenNoFreeSpace());
        Assert.AreEqual("Gauge hidden (Covered by Chrome_WidgetWin_1).", GaugeEventLog.HiddenCovered(Chrome));
        Assert.AreEqual("Gauge hidden (Covered by Windows.UI.Core.CoreWindow (Explorer)).", GaugeEventLog.HiddenCovered(ShellCore));
        Assert.AreEqual("Gauge hidden (NotificationState QUNS_BUSY).", GaugeEventLog.HiddenNotificationState("QUNS_BUSY"));
        Assert.AreEqual("Gauge hidden (FullScreenNotified).", GaugeEventLog.HiddenFullScreenNotified());
        Assert.AreEqual("Gauge hidden (WindowFailed ERROR_INVALID_WINDOW_HANDLE (1400)).", GaugeEventLog.HiddenWindowFailed(window));
        Assert.AreEqual("Gauge hidden (SettingOff).", GaugeEventLog.HiddenSettingOff());
        Assert.AreEqual(
            "Gauge kept at 1596,1036 74x40 after a failed read (Occupants E_FAIL (" + failed.Code + "), 1 of 3).",
            GaugeEventLog.KeptAfterFailedRead(r, "Occupants", failed, 1, 3));
        Assert.AreEqual(
            "Gauge raised: Shell_TrayWnd (Explorer) was over it after a foreground change to Chrome_WidgetWin_1.",
            GaugeEventLog.RaisedAfterForegroundChange(new WindowIdentity("Shell_TrayWnd", true), "Chrome_WidgetWin_1"));
        Assert.AreEqual("Gauge raised: the poll found Chrome_WidgetWin_1 over it.", GaugeEventLog.RaisedByPoll(Chrome));
        Assert.AreEqual("Gauge raised: the poll found an unknown window over it.", GaugeEventLog.RaisedByPoll(null));
        Assert.AreEqual("Gauge left under Chrome_WidgetWin_1 after a foreground change: not the taskbar.", GaugeEventLog.LeftUnder("Chrome_WidgetWin_1"));
        Assert.AreEqual("Gauge raise cap reached; waiting for a poll to confirm.", GaugeEventLog.RaiseCapReached());
    }

    [TestMethod]
    public void ATitleShapedValueCannotBreakTheSentence()
    {
        string line = GaugeEventLog.HiddenCovered(new WindowIdentity("Evil class (fake).\r\nGauge hidden", BelongsToExplorer: false));

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain(" (fake)", line);
        Assert.AreEqual("Gauge hidden (Covered by Evil_class__fake_.__Gauge_hidden).", line);
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

    // ---- The controller writes them ----

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

        public GaugePosition Position { get; set; } = GaugePosition.RightEnd;

        public GaugeController Controller { get; }

        public Rig()
        {
            Controller = new GaugeController(
                () => Surface, Icon, () => new GaugeControllerSettings(Enabled, LeftClickConnects: false, Position), Log, Time);
        }

        public void Layout(TaskbarLayout layout) => Controller.OnLayout(ITaskbarReader.Result.Ok(layout));

        public void Fail(TaskbarReadFailureStep step, string name = "E_FAIL") =>
            Controller.OnLayout(ITaskbarReader.Result.Fail(new TaskbarReadFailure(step, new StepOutcome("uia:" + step, false, 1, name, null))));

        public List<string> Lines() =>
            Log.Entries.Select(e => e.Message).Where(m => m.StartsWith(GaugeEventLog.Prefix, StringComparison.Ordinal)).ToList();

        public string OnlyLine(string startsWith)
        {
            List<string> found = Lines().Where(l => l.StartsWith(startsWith, StringComparison.Ordinal)).ToList();
            Assert.HasCount(1, found, "Expected exactly one line starting '" + startsWith + "'. Log: " + string.Join(" | ", Lines()));
            return found[0];
        }

        public int Count(string startsWith) => Lines().Count(l => l.StartsWith(startsWith, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShowingTheGaugeWritesAShownLineWithItsRectangleAndWhy()
    {
        var rig = new Rig();

        rig.Layout(FreeSpace());

        Assert.AreEqual("Gauge shown at 1596,1036 74x40 (first layout).", rig.OnlyLine("Gauge shown"));
    }

    [TestMethod]
    public void ACoveredTaskbarWritesOneLineNamingTheWindowClass()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome });
        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome });

        Assert.AreEqual("Gauge hidden (Covered by Chrome_WidgetWin_1).", rig.OnlyLine("Gauge hidden"));
    }

    [TestMethod]
    public void AChangeOfCoveringWindowIsANewLine()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = Chrome });
        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = ShellCore });

        Assert.AreEqual(2, rig.Count("Gauge hidden (Covered"));
    }

    [TestMethod]
    public void AFullScreenStateNamesItsNotificationState()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { NotificationState = Shell.QUNS_RUNNING_D3D_FULL_SCREEN });

        Assert.AreEqual("Gauge hidden (NotificationState QUNS_RUNNING_D3D_FULL_SCREEN).", rig.OnlyLine("Gauge hidden"));
    }

    [TestMethod]
    public void TheAppBarNotificationFastPathHasItsOwnReasonAndTheShowAfterItSaysSo()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.NotifyFullScreenApp(opening: true);
        rig.Layout(FreeSpace());

        Assert.AreEqual("Gauge hidden (FullScreenNotified).", rig.OnlyLine("Gauge hidden"));
        Assert.AreEqual("Gauge shown at 1596,1036 74x40 (full-screen app closed).", rig.Lines().Last());
    }

    [TestMethod]
    public void NoTaskbarIsItsOwnReason()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.OnLayout(ITaskbarReader.Result.Fail(new TaskbarReadFailure(
            TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromWin32("find-window:Shell_TrayWnd", 0, ok: false))));
        rig.Layout(FreeSpace());

        Assert.AreEqual("Gauge hidden (NoTaskbar).", rig.OnlyLine("Gauge hidden"));
        Assert.AreEqual("Gauge shown at 1596,1036 74x40 (taskbar back).", rig.Lines().Last());
    }

    [TestMethod]
    public void NoFreeSpaceIsItsOwnReasonAndTheShowAfterItSaysSo()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        TaskbarLayout tight = FreeSpace() with { Occupied = [Start, new Rectangle(807, 1032, 813, 48), new Rectangle(1678, 1032, 242, 48)] };

        rig.Layout(tight);
        rig.Layout(tight);
        rig.Layout(FreeSpace());

        Assert.AreEqual("Gauge hidden (NoFreeSpace).", rig.OnlyLine("Gauge hidden"));
        Assert.AreEqual("Gauge shown at 1596,1036 74x40 (free space back).", rig.Lines().Last());
        Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Gauge placement found no room: NoRoom."), "The placement's own reason is kept at Debug.");
    }

    [TestMethod]
    public void AFailedShowIsWrittenOnceAsAnErrorWithItsCodeAndThenHidesAsWindowFailed()
    {
        var rig = new Rig();
        rig.Surface.NextShowAtResult = StepOutcomes.FromWin32("set-window-pos:show-gauge", 1400);

        rig.Layout(FreeSpace());

        Assert.IsTrue(rig.Log.Has(LogLevel.Error, "1400"), "The raw code is an Error line, as before.");
        StringAssert.StartsWith(rig.OnlyLine("Gauge hidden"), "Gauge hidden (WindowFailed ");
        StringAssert.Contains(rig.OnlyLine("Gauge hidden"), "(1400)");
    }

    [TestMethod]
    public void TurningTheSettingOffWritesOneLineAndAnIdleOffWritesNone()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Enabled = false;

        rig.Layout(FreeSpace());
        rig.Layout(FreeSpace());

        Assert.AreEqual("Gauge hidden (SettingOff).", rig.OnlyLine("Gauge hidden"));
    }

    [TestMethod]
    public void MovingTheGaugeWritesAMovedLine()
    {
        var rig = new Rig { Position = GaugePosition.NextToApps };
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { Occupied = [Start, new Rectangle(807, 1032, 88, 48), new Rectangle(1678, 1032, 242, 48)] });

        StringAssert.StartsWith(rig.OnlyLine("Gauge moved"), "Gauge moved to ");
    }

    [TestMethod]
    public void EveryLineIsOneLineAndNamesNoTitle()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(FreeSpace() with { Covered = true, CoveringWindow = new WindowIdentity("FakeClass", false) });
        rig.Layout(FreeSpace());
        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = new WindowIdentity("FakeClass", false) });
        rig.Fail(TaskbarReadFailureStep.Occupants);

        Assert.IsNotEmpty(rig.Lines());
        foreach (string line in rig.Lines())
        {
            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain("FakeTitle", line);
        }

        Assert.IsTrue(rig.Lines().Any(l => l.Contains("FakeClass", StringComparison.Ordinal)), "The class is named.");
    }

    // ---- The poll's raise ----

    [TestMethod]
    public void ThePollsRaiseNamesTheWindowItFoundOverTheGauge()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = ShellCore });

        Assert.AreEqual("Gauge raised: the poll found Windows.UI.Core.CoreWindow (Explorer) over it.", rig.OnlyLine("Gauge raised"));
        CollectionAssert.Contains(rig.Surface.Calls, "Raise");
    }

    [TestMethod]
    public void ARaiseThatFailsIsAWindowFailure()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Surface.NextRaiseResult = StepOutcomes.FromWin32("set-window-pos:raise-gauge", 5);

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false });

        Assert.AreEqual(HiddenReason.WindowFailed, ((GaugeState.Hidden)rig.Controller.State).Reason);
        StringAssert.StartsWith(rig.OnlyLine("Gauge hidden"), "Gauge hidden (WindowFailed ");
    }
}
