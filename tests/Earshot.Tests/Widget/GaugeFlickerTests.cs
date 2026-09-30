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

    // A burst is not a loop: every change in it is looked at and the taskbar over the gauge is raised over each
    // time, up to the limit.
    [TestMethod]
    public void ABurstOfChangesIsAnsweredUpToTheSlidingLimitAndWarnsOnce()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;

        for (int i = 0; i < 8; i++)
        {
            rig.Controller.OnForegroundChanged("A" + i);
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.AreEqual(GaugeController.RaisesPerWindow, rig.Raises, "Four in one second, then the limit.");
        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message == "Gauge raise limit reached (4 in 1 s); the next check will try again."));
    }

    // The old cap stopped for good until a poll confirmed the gauge was on top, and the gauge stayed covered. The
    // window slides: a second later the same taskbar is answered again.
    [TestMethod]
    public void TheLimitSlidesSoAGaugeIsNeverLeftCovered()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;
        for (int i = 0; i < 8; i++)
        {
            rig.Controller.OnForegroundChanged("A" + i);
            rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.AreEqual(GaugeController.RaisesPerWindow, rig.Raises);
        rig.Time.Advance(GaugeController.RaiseWindow);
        int before = rig.Raises;
        rig.Controller.OnForegroundChanged("Later");

        Assert.AreEqual(before + 1, rig.Raises, "The window slid past the first raises.");
    }

    // The owner's log: the taskbar covered the gauge again a second or two after each raise, with no event and
    // often before the poll came round. While it keeps doing so every fast check puts the gauge back, and never more
    // than the limit in any second.
    [TestMethod]
    public void ATaskbarThatKeepsCoveringTheGaugeIsAnsweredEveryTimeWithinTheLimit()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;
        var raiseTimes = new List<TimeSpan>();
        rig.Controller.OnForegroundChanged("Shell_TrayWnd");
        int seen = rig.Raises;
        raiseTimes.Add(TimeSpan.Zero);

        TimeSpan elapsed = TimeSpan.Zero;
        for (int i = 0; i < 40; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(250));
            elapsed += TimeSpan.FromMilliseconds(250);
            while (seen < rig.Raises)
            {
                raiseTimes.Add(elapsed);
                seen++;
            }
        }

        Assert.IsGreaterThanOrEqualTo(30, rig.Raises, "Answered at each quarter second for ten seconds, not stuck after four.");
        foreach (TimeSpan at in raiseTimes)
        {
            Assert.IsLessThanOrEqualTo(GaugeController.RaisesPerWindow, raiseTimes.Count(t => t > at - TimeSpan.FromSeconds(1) && t <= at),
                "No more than four raises in any second.");
        }
    }

    // ---- A cover found by the poll is rechecked quickly ----

    private static TaskbarLayout PollFoundTaskbar() =>
        FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = new WindowIdentity("Shell_TrayWnd", true) };

    [TestMethod]
    public void ACoverTheStartedFromNoEventIsPutBackByTheNextFastCheckNotTheNextPoll()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(PollFoundTaskbar());
        Assert.AreEqual(1, rig.Raises, "The poll raised once.");
        Assert.IsTrue(rig.Logged(LogLevel.Info, "Gauge raised: the poll found Shell_TrayWnd (Explorer) over it."));

        // The taskbar covers it again, with no event and before the poll's next read.
        rig.Cover.Next = TaskbarOver;
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(2, rig.Raises, "One fast interval later, not one poll.");
        Assert.IsTrue(rig.Logged(LogLevel.Info, "Gauge raised: Shell_TrayWnd (Explorer) was over it on a recheck."));
    }

    [TestMethod]
    public void TheFastChecksRunEveryQuarterSecondForTenSecondsAfterTheLastCoverThenStop()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(PollFoundTaskbar());
        int before = rig.Cover.Probes;

        for (int i = 0; i < 39; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.AreEqual(39, rig.Cover.Probes - before, "One look each quarter second while it runs.");
        Assert.AreEqual(1, rig.Time.LiveTimers);

        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(0, rig.Time.LiveTimers, "Ten seconds after the cover, back to the poll alone.");
        int probes = rig.Cover.Probes;
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.AreEqual(probes, rig.Cover.Probes);
    }

    [TestMethod]
    public void AnotherCoverExtendsTheFastChecks()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(PollFoundTaskbar());
        for (int i = 0; i < 24; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        }

        rig.Cover.Next = TaskbarOver;
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        rig.Cover.Next = new GaugeCover(IsGauge: true, RootClassName: "");
        for (int i = 0; i < 39; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.AreEqual(1, rig.Time.LiveTimers, "Ten seconds are counted from the newest cover, not the first.");
    }

    [TestMethod]
    public void AFlyoutOverTheGaugeStartsTheFastChecksButIsNotRaisedOverAndIsLoggedOncePerClass()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = FlyoutOver;

        rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");
        for (int i = 0; i < 8; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.AreEqual(0, rig.Raises);
        Assert.AreEqual(1, rig.Lines("Gauge left under "), "Found by every check, logged once.");
        Assert.AreEqual(1, rig.Time.LiveTimers, "Still rechecking while it is there.");

        rig.Cover.Next = new GaugeCover(IsGauge: true, RootClassName: "");
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        rig.Cover.Next = FlyoutOver;
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(2, rig.Lines("Gauge left under "), "It went away and came back, so it is a line again.");
    }

    [TestMethod]
    public void LeavingShownStopsTheFastChecks()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(PollFoundTaskbar());
        Assert.AreEqual(1, rig.Time.LiveTimers);

        rig.Controller.NotifyFullScreenApp(opening: true);

        Assert.AreEqual(0, rig.Time.LiveTimers);
    }

    [TestMethod]
    public void DisposingStopsTheFastChecks()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Layout(PollFoundTaskbar());

        rig.Controller.Dispose();

        Assert.AreEqual(0, rig.Time.LiveTimers);
    }

    [TestMethod]
    public void TheFastChecksAreCarriedToTheUiThreadThroughThePost()
    {
        var posted = new Queue<Action>();
        var surface = new FakeGaugeSurface();
        var time = new Streaming.TestTimeProvider();
        var cover = new FakeCover { Next = TaskbarOver };
        using var controller = new GaugeController(
            () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(true, false), new CapturingLog(), time, cover, posted.Enqueue);
        controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace()));
        controller.OnLayout(ITaskbarReader.Result.Ok(PollFoundTaskbar()));
        int raises = surface.Calls.Count(c => c == "Raise");

        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(raises, surface.Calls.Count(c => c == "Raise"), "The timer's pool thread touches nothing.");
        Assert.HasCount(1, posted);
        posted.Dequeue()();
        Assert.AreEqual(raises + 1, surface.Calls.Count(c => c == "Raise"));
    }

    // ---- Earshot's own windows are never a cover ----

    private static readonly GaugeCover OwnTooltipOver = new(IsGauge: false, RootClassName: "WindowsForms10.tooltips_class32.app.0.1_r1_ad1", BelongsToExplorer: false, BelongsToThisProcess: true);

    [TestMethod]
    public void OneOfEarshotsOwnWindowsOverTheGaugeAtAForegroundChangeIsNotACover()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = OwnTooltipOver;

        rig.Controller.OnForegroundChanged("Shell_TrayWnd");
        rig.Time.Advance(TimeSpan.FromSeconds(3));

        Assert.AreEqual(0, rig.Raises);
        Assert.AreEqual(0, rig.Lines("Gauge left under "), "Not logged as a cover either.");
        Assert.AreEqual(0, rig.Time.LiveTimers, "Nothing to recheck: no cover was found.");
    }

    [TestMethod]
    public void OneOfEarshotsOwnWindowsOverTheGaugeAtThePollIsNotACoverAndIsNotRaisedOver()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = new WindowIdentity("WindowsForms10.tooltips_class32.app.0.1_r1_ad1", false, BelongsToThisProcess: true) });

        Assert.AreEqual(0, rig.Raises, "Raising the gauge above its own tooltip would hide the tooltip.");
        Assert.AreEqual(0, rig.Time.LiveTimers);
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State);
    }

    [TestMethod]
    public void AnotherProgramsTopmostWindowOverTheGaugeAtThePollIsStillRaisedOver()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());

        rig.Layout(FreeSpace() with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = new WindowIdentity("Chrome_WidgetWin_1", false) });

        Assert.AreEqual(1, rig.Raises, "The poll stays the safety net for any window that is not Earshot's own.");
    }

    // ---- A window Explorer showed or hid ----

    [TestMethod]
    public void AWindowExplorerShowedWithTheTaskbarOverTheGaugeRaisesIt()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;

        rig.Controller.OnShellWindowChanged("ControlCenterWindow", shown: true);

        Assert.AreEqual(1, rig.Raises);
        Assert.IsTrue(rig.Logged(LogLevel.Info, "Gauge raised: Shell_TrayWnd (Explorer) was over it after Explorer showed a window of class ControlCenterWindow."));
    }

    [TestMethod]
    public void AWindowExplorerHidWithTheTaskbarOverTheGaugeRaisesItToo()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Cover.Next = TaskbarOver;

        rig.Controller.OnShellWindowChanged("ControlCenterWindow", shown: false);

        Assert.AreEqual(1, rig.Raises);
        Assert.IsTrue(rig.Logged(LogLevel.Info, "after Explorer hid a window of class ControlCenterWindow."));
    }

    [TestMethod]
    public void AShellWindowChangeGetsTheSecondLookAndNothingWhileHidden()
    {
        var rig = new Rig();
        rig.Layout(FreeSpace());
        rig.Controller.OnShellWindowChanged("A", shown: true);
        rig.Cover.Next = TaskbarOver;
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(1, rig.Raises, "The bar rose late.");

        rig.Controller.NotifyFullScreenApp(opening: true);
        int probes = rig.Cover.Probes;
        rig.Controller.OnShellWindowChanged("B", shown: true);

        Assert.AreEqual(probes, rig.Cover.Probes, "While hidden there is nothing to look at.");
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
        Assert.HasCount(2, posted, "The second look, and the first fast check the found cover started.");
        while (posted.Count > 0)
        {
            posted.Dequeue()();
        }

        Assert.AreEqual(raisesBefore + 2, surface.Calls.Count(c => c == "Raise"));
    }
}
