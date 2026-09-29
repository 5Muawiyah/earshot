using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The gauge that vanishes: a failed read no longer takes it away, and a change of the foreground window puts it
// back on top when the shell has raised the taskbar over it, without waiting for the poll.
[TestClass]
public sealed class GaugeFlickerTests
{
    private static readonly Rectangle Bar = new(0, 1032, 1920, 48);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);
    private static readonly Rectangle Gauge = new(1678 - 8 - 74, 1036, 74, 40);

    private static TaskbarLayout FreeSpace() =>
        new(0, Bar, TaskbarEdge.Bottom, false, new Rectangle(0, 0, 1920, 1080),
            [Start, new Rectangle(807, 1032, 44, 48), new Rectangle(1678, 1032, 242, 48)], Start,
            96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new Rectangle(1678, 1032, 242, 48));

    private sealed class FakeCover : IGaugeCoverProbe
    {
        public GaugeCover Next { get; set; } = new(IsGauge: true, RootClassName: "");

        public int Probes { get; private set; }

        public ShownGauge? Last { get; private set; }

        public GaugeCover Probe1(ShownGauge gauge) => Probe(gauge);

        public GaugeCover Probe(ShownGauge gauge)
        {
            Probes++;
            Last = gauge;
            return Next;
        }
    }

    private static readonly GaugeCover TaskbarOver = new(IsGauge: false, RootClassName: "Shell_TrayWnd", BelongsToExplorer: true);
    private static readonly GaugeCover FlyoutOver = new(IsGauge: false, RootClassName: "Windows.UI.Core.CoreWindow", BelongsToExplorer: true);

    private sealed class Rig
    {
        public FakeGaugeSurface Surface { get; } = new();

        public FakeTrayIcon Icon { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public FakeCover Cover { get; } = new();

        public GaugeController Controller { get; }

        public Rig(bool withProbe = true)
        {
            Controller = new GaugeController(
                () => Surface, Icon, () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false), Log, Time,
                withProbe ? Cover : null);
        }

        public int Raises => Surface.Calls.Count(c => c == "Raise");

        public void Layout(TaskbarLayout layout) => Controller.OnLayout(ITaskbarReader.Result.Ok(layout));

        public void Fail(TaskbarReadFailureStep step = TaskbarReadFailureStep.Occupants) =>
            Controller.OnLayout(ITaskbarReader.Result.Fail(new TaskbarReadFailure(step, new StepOutcome("uia:" + step, false, 7, "E_FAIL", null))));

        public bool Logged(LogLevel level, string fragment) => Log.Has(level, fragment);

        public int Lines(string startsWith) => Log.Entries.Count(e => e.Message.StartsWith(startsWith, StringComparison.Ordinal));
    }

    // ---- A failed read keeps the last good position ----

    [TestMethod]
    public void AFailedReadKeepsTheGaugeAtItsLastGoodPosition()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Surface.Calls.Clear();

        rig.Fail();

        Assert.AreEqual(new GaugeState.Shown(Gauge), rig.Controller.State);
        Assert.IsEmpty(rig.Surface.Calls, "Nothing is hidden, moved or shown again.");
        Assert.IsTrue(rig.Logged(LogLevel.Debug, "Gauge kept at 1596,1036 74x40 after a failed read (Occupants E_FAIL (7), 1 of 3)."));
    }

    [TestMethod]
    public void ThreeConsecutiveFailedReadsHideTheGauge()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Fail();
        rig.Fail();
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State, "Two failures are still tolerated.");
        rig.Fail();

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.ReadFailed), rig.Controller.State);
        CollectionAssert.Contains(rig.Surface.Calls, "HideWindow");
        Assert.IsTrue(rig.Logged(LogLevel.Info, "Gauge hidden (ReadFailed Occupants E_FAIL (7) after 3 consecutive failures)."));
        Assert.IsTrue(rig.Icon.Visible, "The tray icon is back.");
    }

    [TestMethod]
    public void ASuccessfulReadResetsTheFailureCount()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Fail();
        rig.Fail();
        rig.Layout(FreeSpace());
        rig.Fail();
        rig.Fail();

        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State);
    }

    [TestMethod]
    public void NoTaskbarHidesAtOnce()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Fail(TaskbarReadFailureStep.NoTaskbar);

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NoTaskbar), rig.Controller.State);
    }

    [TestMethod]
    public void AThrownReadIsNotADefiniteSignal()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Fail(TaskbarReadFailureStep.Exception);
        rig.Fail(TaskbarReadFailureStep.Exception);

        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State, "A read that threw says nothing about whether the bar is there.");
    }

    [TestMethod]
    public void AFailedReadWhileTheGaugeIsNotShownIsReportedAtOnce()
    {
        var rig = new Rig();

        rig.Fail();

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.ReadFailed), rig.Controller.State);
        Assert.IsTrue(rig.Icon.Visible);
    }

    [TestMethod]
    public void ARecoveredReadShowsTheGaugeAgainAndSaysSo()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Fail();
        rig.Fail();
        rig.Fail();

        rig.Layout(FreeSpace());

        Assert.IsTrue(rig.Logged(LogLevel.Info, "Gauge shown at 1596,1036 74x40 (read recovered)."));
    }

    // ---- A foreground change puts the gauge back on top ----

    [TestMethod]
    public void AForegroundChangeWithTheTaskbarOverTheGaugeRaisesOnce()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;

        rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");

        Assert.AreEqual(1, rig.Raises);
        Assert.IsTrue(rig.Logged(LogLevel.Info, "Gauge raised: Shell_TrayWnd (Explorer) was over it after a foreground change to Windows.UI.Core.CoreWindow."));
        Assert.AreEqual(new ShownGauge(Gauge, rig.Surface.WindowHandle), rig.Cover.Last);
    }

    [TestMethod]
    public void AForegroundChangeWithAnotherWindowOverTheGaugeDoesNotRaise()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = FlyoutOver;

        rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");

        Assert.AreEqual(0, rig.Raises);
        Assert.IsTrue(rig.Logged(LogLevel.Debug, "Gauge left under Windows.UI.Core.CoreWindow after a foreground change: not the taskbar."));
    }

    [TestMethod]
    public void AForegroundChangeWhileTheGaugeIsOnTopDoesNothing()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.OnForegroundChanged("Chrome_WidgetWin_1");

        Assert.AreEqual(0, rig.Raises);
        Assert.AreEqual(1, rig.Cover.Probes);
    }

    [TestMethod]
    public void AForegroundChangeWhileHiddenDoesNothing()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Controller.NotifyFullScreenApp(opening: true);
        rig.Cover.Next = TaskbarOver;

        rig.Controller.OnForegroundChanged("Chrome_WidgetWin_1");
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, rig.Cover.Probes);
        Assert.AreEqual(0, rig.Raises);
    }

    [TestMethod]
    public void AForegroundChangeWithNoProbeDoesNothing()
    {
        var rig = new Rig(withProbe: false);
        rig.Layout(FreeSpace());

        rig.Controller.OnForegroundChanged("Chrome_WidgetWin_1");

        Assert.AreEqual(0, rig.Raises);
    }

    [TestMethod]
    public void TheSecondLookAt250msRaisesWhenTheBarRoseLate()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");
        Assert.AreEqual(0, rig.Raises, "The bar had not risen yet.");
        rig.Cover.Next = TaskbarOver;
        rig.Time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.AreEqual(0, rig.Raises, "Not yet 250 ms.");
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, rig.Raises);
        Assert.AreEqual(2, rig.Cover.Probes);
    }

    [TestMethod]
    public void ManyForegroundChangesInsideOneWindowShareOneSecondLook()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Controller.OnForegroundChanged("A");
        rig.Controller.OnForegroundChanged("B");
        rig.Controller.OnForegroundChanged("C");
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(4, rig.Cover.Probes, "Three looks now and one shared look 250 ms later.");
        Assert.AreEqual(1, rig.Time.TimersCreated);
    }

    [TestMethod]
    public void RaisesAreRateLimitedTo250ms()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;

        rig.Controller.OnForegroundChanged("A");
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Controller.OnForegroundChanged("B");

        Assert.AreEqual(1, rig.Raises, "The second change came 100 ms after the raise.");
        Assert.IsTrue(rig.Logged(LogLevel.Debug, "Gauge raise skipped"));

        rig.Time.Advance(TimeSpan.FromMilliseconds(150));

        Assert.AreEqual(2, rig.Raises, "The shared second look is 250 ms after the first raise, so it may raise.");
    }

    [TestMethod]
    public void FourRaisesWithoutAConfirmingPollStopAndWarnOnce()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;

        for (int i = 0; i < 7; i++)
        {
            rig.Controller.OnForegroundChanged("A" + i);
            rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        }

        Assert.AreEqual(GaugeController.RaiseCap, rig.Raises);
        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message == "Gauge raise cap reached; waiting for a poll to confirm."));

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = true });
        rig.Controller.OnForegroundChanged("After");

        Assert.AreEqual(GaugeController.RaiseCap + 1, rig.Raises, "A poll that finds the gauge on top lifts the cap.");
    }

    [TestMethod]
    public void ThePollsOwnRaiseStillWorksAfterTheCapIsReached()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;
        for (int i = 0; i < 6; i++)
        {
            rig.Controller.OnForegroundChanged("A" + i);
            rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        }

        int before = rig.Raises;
        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = new WindowIdentity("Shell_TrayWnd", true) });

        Assert.AreEqual(before + 1, rig.Raises, "The poll is the safety net and is never capped.");
    }

    [TestMethod]
    public void ARaiseThatFailsHidesTheGaugeAsAWindowFailure()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;
        rig.Surface.NextRaiseResult = StepOutcomes.FromWin32("set-window-pos:raise-gauge", 1400);

        rig.Controller.OnForegroundChanged("A");

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.WindowFailed), rig.Controller.State);
        Assert.IsTrue(rig.Surface.IsDisposed);
    }

    [TestMethod]
    public void DisposingCancelsTheSecondLook()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;
        rig.Controller.OnForegroundChanged("A");
        int probes = rig.Cover.Probes;

        rig.Controller.Dispose();
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(probes, rig.Cover.Probes);
        Assert.AreEqual(0, rig.Time.LiveTimers);
    }

    // The timer's callback runs on a pool thread in production: it must go back to the UI thread through
    // the post it was given before it touches the window.
    [TestMethod]
    public void TheSecondLookIsPostedToTheUiThread()
    {
        var posted = new Queue<Action>();
        var surface = new FakeGaugeSurface();
        var time = new Streaming.TestTimeProvider();
        var cover = new FakeCover { Next = TaskbarOver };
        using var controller = new GaugeController(
            () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(true, false), new CapturingLog(), time, cover, posted.Enqueue);
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace()));
        controller.OnForegroundChanged("A");
        int raisesBefore = surface.Calls.Count(c => c == "Raise");

        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(raisesBefore, surface.Calls.Count(c => c == "Raise"), "Nothing touches the window until the UI thread runs it.");
        Assert.HasCount(1, posted);
        posted.Dequeue()();
        Assert.AreEqual(2, surface.Calls.Count(c => c == "Raise"));
    }
}
