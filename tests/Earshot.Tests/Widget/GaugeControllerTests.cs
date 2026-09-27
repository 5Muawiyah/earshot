using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A fake IGaugeSurface: records every call, never touches a window.
internal sealed class FakeGaugeSurface : IGaugeSurface
{
    public List<string> Calls { get; } = new();

    public StepOutcome NextShowAtResult { get; set; } = new("show-at", true, 0, "S_OK", null);

    public bool IsDisposed { get; private set; }

    public event EventHandler? LeftClicked;

    public event EventHandler<Point>? RightClicked;

    public StepOutcome ShowAt(Rectangle bounds)
    {
        Calls.Add("ShowAt " + bounds);
        return NextShowAtResult;
    }

    public StepOutcome MoveTo(Rectangle bounds)
    {
        Calls.Add("MoveTo " + bounds);
        return new StepOutcome("move-to", true, 0, "S_OK", null);
    }

    public StepOutcome Raise()
    {
        Calls.Add("Raise");
        return new StepOutcome("raise", true, 0, "S_OK", null);
    }

    public void HideWindow() => Calls.Add("HideWindow");

    public void Dispose() => IsDisposed = true;

    public void RaiseLeftClicked() => LeftClicked?.Invoke(this, EventArgs.Empty);

    public void RaiseRightClicked(Point point) => RightClicked?.Invoke(this, point);
}

internal sealed class FakeTrayIcon : ITrayIconVisibility
{
    public List<bool> VisibilityChanges { get; } = new();

    public bool Visible
    {
        get;
        set
        {
            field = value;
            VisibilityChanges.Add(value);
        }
    }
}

[TestClass]
public sealed class GaugeControllerTests
{
    private static readonly Rectangle Bar = new(0, 1032, 1920, 48);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);

    private static List<Rectangle> Buttons(int count) =>
        Enumerable.Range(0, count).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48)).ToList();

    private static TaskbarLayout FreeSpaceLayout(int buttonCount = 8) =>
        new(0, Bar, TaskbarEdge.Bottom, false, new Rectangle(0, 0, 1920, 1080),
            [Start, .. Buttons(buttonCount), new Rectangle(1678, 1032, 242, 48)], Start,
            96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null);

    private static (GaugeController Controller, FakeGaugeSurface Surface, FakeTrayIcon Icon, Streaming.TestTimeProvider Time, CapturingLog Log) Build(bool enabled = true, bool leftClickConnects = false)
    {
        var surface = new FakeGaugeSurface();
        var icon = new FakeTrayIcon();
        var time = new Streaming.TestTimeProvider();
        var log = new CapturingLog();
        bool settingsEnabled = enabled;
        bool settingsLeftClickConnects = leftClickConnects;
        var controller = new GaugeController(() => surface, icon, () => new GaugeControllerSettings(settingsEnabled, settingsLeftClickConnects), log, time);
        return (controller, surface, icon, time, log);
    }

    [TestMethod]
    public void AttachShowsTheGaugeAndHidesTheIconAfterTwoSeconds()
    {
        (GaugeController controller, FakeGaugeSurface surface, FakeTrayIcon icon, Streaming.TestTimeProvider time, _) = Build();

        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);
        CollectionAssert.Contains(surface.Calls, "ShowAt " + new Rectangle(1183, 1032, 88, 48).ToString());
        Assert.IsTrue(icon.Visible, "The icon stays visible until the debounce elapses.");

        time.Advance(TimeSpan.FromSeconds(1));
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        Assert.IsTrue(icon.Visible, "Not yet 2 s.");
        Assert.IsFalse(surface.Calls.Contains("MoveTo " + new Rectangle(1183, 1032, 88, 48).ToString()), "An identical layout must not move the gauge.");

        time.Advance(TimeSpan.FromSeconds(1.5));
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        Assert.IsFalse(icon.Visible, "After 2 s continuously shown, the icon hides.");
    }

    [TestMethod]
    public void OneMoreButtonMovesTheGaugeRatherThanShowingItAgain()
    {
        (GaugeController controller, FakeGaugeSurface surface, _, _, _) = Build();

        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout(8)));
        Assert.AreEqual(1, surface.Calls.Count(c => c.StartsWith("ShowAt", StringComparison.Ordinal)));

        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout(9)));
        Assert.AreEqual(1, surface.Calls.Count(c => c.StartsWith("ShowAt", StringComparison.Ordinal)), "Never a second ShowAt.");
        Assert.AreEqual(1, surface.Calls.Count(c => c.StartsWith("MoveTo", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AttachingFailsHidesWithTheIconVisibleAndRecreatesTheSurfaceNextTime()
    {
        (GaugeController controller, FakeGaugeSurface surface, FakeTrayIcon icon, _, CapturingLog log) = Build();
        surface.NextShowAtResult = StepOutcomes.FromWin32("set-window-pos:show-gauge", 1400);

        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        Assert.IsInstanceOfType<GaugeState.Hidden>(controller.State);
        Assert.AreEqual(HiddenReason.WindowFailed, ((GaugeState.Hidden)controller.State).Reason);
        Assert.IsTrue(icon.Visible);
        Assert.IsTrue(surface.IsDisposed, "The failed surface is disposed.");
        Assert.IsTrue(log.Entries.Any(e => e.Level == LogLevel.Error), "One Error line with the code.");
    }

    [TestMethod]
    public void NoFreeSpaceHidesWithTheIconVisibleThenShowsAgainWhenSpaceReturns()
    {
        (GaugeController controller, FakeGaugeSurface surface, FakeTrayIcon icon, _, _) = Build();

        // 300 pixels of buttons leaves no run wide enough (24 + 88 + 24) before the tray at 1678.
        var tight = new TaskbarLayout(0, Bar, TaskbarEdge.Bottom, false, new Rectangle(0, 0, 1920, 1080),
            [Start, .. Enumerable.Range(0, (1580 - 807) / 44).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48)), new Rectangle(1678, 1032, 242, 48)],
            Start, 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null);

        controller.OnLayout(ITaskbarReader.Result.Ok(tight));
        Assert.IsInstanceOfType<GaugeState.Hidden>(controller.State);
        Assert.AreEqual(HiddenReason.NoFreeSpace, ((GaugeState.Hidden)controller.State).Reason);
        Assert.IsTrue(icon.Visible);

        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);
        CollectionAssert.Contains(surface.Calls, "ShowAt " + new Rectangle(1183, 1032, 88, 48).ToString());
    }

    [TestMethod]
    public void ACoveredTaskbarHidesTheGauge()
    {
        (GaugeController controller, FakeGaugeSurface surface, FakeTrayIcon icon, _, _) = Build();
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State, "The gauge must already be on screen for hiding it to mean anything.");

        TaskbarLayout covered = FreeSpaceLayout() with { Covered = true };
        controller.OnLayout(ITaskbarReader.Result.Ok(covered));

        Assert.IsInstanceOfType<GaugeState.Hidden>(controller.State);
        Assert.AreEqual(HiddenReason.Covered, ((GaugeState.Hidden)controller.State).Reason);
        Assert.IsTrue(icon.Visible);
        CollectionAssert.Contains(surface.Calls, "HideWindow", "A covered taskbar must hide the window itself, not just show the tray icon.");
    }

    [TestMethod]
    public void QunsBusyHidesTheGauge()
    {
        (GaugeController controller, FakeGaugeSurface surface, _, _, _) = Build();
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        TaskbarLayout busy = FreeSpaceLayout() with { NotificationState = Shell.QUNS_BUSY };
        controller.OnLayout(ITaskbarReader.Result.Ok(busy));

        Assert.IsInstanceOfType<GaugeState.Hidden>(controller.State);
        Assert.AreEqual(HiddenReason.NotificationState, ((GaugeState.Hidden)controller.State).Reason);
        CollectionAssert.Contains(surface.Calls, "HideWindow", "A full-screen or presentation notification state must hide the window itself.");
    }

    // The general regression for the defect the two tests above narrow to one reason each: leaving Shown
    // for any Hidden reason must hide the real window, not just flip the tray icon back on. Before the fix,
    // TransitionHiddenNoLog never called IGaugeSurface.HideWindow, so the gauge stayed topmost and visible
    // over whatever caused the transition (a full-screen app, a taskbar button appearing underneath it).
    [TestMethod]
    public void LeavingShownForAnyHiddenReasonHidesTheWindow()
    {
        (GaugeController controller, FakeGaugeSurface surface, _, _, _) = Build();
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);
        surface.Calls.Clear();

        var failure = new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromWin32("find-window:Shell_TrayWnd", 0, ok: false));
        controller.OnLayout(ITaskbarReader.Result.Fail(failure));

        Assert.IsInstanceOfType<GaugeState.Hidden>(controller.State);
        CollectionAssert.Contains(surface.Calls, "HideWindow");
        Assert.IsFalse(surface.IsDisposed, "Hidden keeps the surface for reuse; only Off disposes it.");
    }

    [TestMethod]
    public void EnabledFalseGoesOffAndDisposesTheSurface()
    {
        (GaugeController controller, FakeGaugeSurface surface, FakeTrayIcon icon, _, _) = Build();
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);

        (GaugeController offController, FakeGaugeSurface offSurface, FakeTrayIcon offIcon, _, _) = Build(enabled: false);
        offController.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        Assert.IsInstanceOfType<GaugeState.Off>(offController.State);
        Assert.IsTrue(offIcon.Visible);
        Assert.AreEqual(0, offSurface.Calls.Count, "Off never shows a window.");
    }

    [TestMethod]
    public void LeftClickOpensTheCardByDefault()
    {
        (GaugeController controller, FakeGaugeSurface surface, _, _, _) = Build(leftClickConnects: false);
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        bool cardRequested = false;
        bool toggleRequested = false;
        controller.CardRequested += (_, _) => cardRequested = true;
        controller.ToggleRequested += (_, _) => toggleRequested = true;
        surface.RaiseLeftClicked();

        Assert.IsTrue(cardRequested);
        Assert.IsFalse(toggleRequested);
    }

    [TestMethod]
    public void LeftClickConnectsStraightAwayWhenTheSettingIsOn()
    {
        (GaugeController controller, FakeGaugeSurface surface, _, _, _) = Build(leftClickConnects: true);
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        bool toggleRequested = false;
        controller.ToggleRequested += (_, _) => toggleRequested = true;
        surface.RaiseLeftClicked();

        Assert.IsTrue(toggleRequested);
    }

    [TestMethod]
    public void RightClickRequestsTheMenuAtTheClickPoint()
    {
        (GaugeController controller, FakeGaugeSurface surface, _, _, _) = Build();
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpaceLayout()));

        Point? menuPoint = null;
        controller.MenuRequested += (_, p) => menuPoint = p;
        surface.RaiseRightClicked(new Point(1200, 1050));

        Assert.AreEqual(new Point(1200, 1050), menuPoint);
    }

    [TestMethod]
    public void AReadFailureHidesWithTheIconVisibleAndLogsOnce()
    {
        (GaugeController controller, _, FakeTrayIcon icon, _, CapturingLog log) = Build();
        var failure = new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromWin32("find-window:Shell_TrayWnd", 0, ok: false));

        controller.OnLayout(ITaskbarReader.Result.Fail(failure));
        controller.OnLayout(ITaskbarReader.Result.Fail(failure));

        Assert.IsInstanceOfType<GaugeState.Hidden>(controller.State);
        Assert.AreEqual(HiddenReason.NoTaskbar, ((GaugeState.Hidden)controller.State).Reason);
        Assert.IsTrue(icon.Visible);
        Assert.AreEqual(1, log.Entries.Count(e => e.Level == LogLevel.Error), "The repeat is Debug, not a second Error.");
    }
}
