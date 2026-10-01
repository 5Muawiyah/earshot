using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// TrayContext.Widget.cs's WireWidget/ApplyWidget: a harness-built TrayContext starts with the widget off
// (TrayHarness's own default, WidgetRealSurfaceGuardTests), so turning the setting on afterwards is the
// only way this codebase ever asks WireWidget to run a second time. Before the fix, WireWidget's
// construction only ever ran from the constructor: a widget that started disabled had no TaskbarWatcher,
// GaugeController or WidgetCardPresenter for a later "turn it on" to act on, and turning an already-running
// widget off left the UI Automation polling thread running (ApplyWidget only ever called Poke()).
//
// This used to reset and read UiaTaskbarReader.ConstructionCount to prove the rebuild, which meant
// constructing the real class (and, until the reader became injectable, starting a real UI Automation
// poll against the owner's own taskbar the moment TaskbarWatcher.Start() ran). TrayHarness now always
// injects a fake reader (TrayContextTests.TaskbarReaderFactoryCalls), so the same proof - a fresh instance
// is built on the second Enabled, not silently skipped - is read from the fake factory's own call count
// instead, and WidgetRealSurfaceGuardTests's assembly-wide check confirms no real UiaTaskbarReader was
// ever touched by this test.
[TestClass]
public sealed class WidgetRuntimeToggleTests
{
    [TestMethod]
    public void TurningTheWidgetOnAfterStartingOffBuildsTheWatcher()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            Assert.AreEqual(0, tray.TaskbarReaderFactoryCalls, "Sanity: the widget starts off, so nothing is built yet.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.TaskbarReaderFactoryCalls,
                "Turning the widget on after starting off must build a taskbar watcher: WireWidget's construction " +
                "logic must be re-runnable, not something only the constructor ever calls.");
        });
    }

    [TestMethod]
    public void TurningTheWidgetOffStopsTheWatcherAndOnStartsANewOne()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            Assert.AreEqual(1, tray.TaskbarReaderFactoryCalls, "Sanity: starting on builds one watcher.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false });
            tray.PumpUntilIdle();

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(2, tray.TaskbarReaderFactoryCalls,
                "Turning the widget off must stop the old watcher (so it is not just left running unpolled), and " +
                "turning it back on must build a fresh one, not silently do nothing because a stale non-null field " +
                "from before it was disposed made WireWidget think the UI side was still wired.");
        });
    }

    // TrayContext.OnCaseOpened only ever runs from the real IWidgetStatus.CaseOpened event, which nothing
    // in this suite raises: WidgetCaseOpenCardWiredForTest above proves a CaseOpenCardPresenter exists, but
    // a mutant that dropped the "_widgetStatus.CaseOpened += OnCaseOpened" subscription in WireWidget, or
    // that made OnCaseOpened's body do nothing, would leave every existing assertion in this file exactly
    // as green as it is now. RaiseCaseOpenedForTest drives OnCaseOpened directly, the same way
    // RequestWidgetCardForTest already drives OnWidgetCardRequested for the gauge-anchored card.
    //
    // The assertion reads the log for the presenter's own notification-state line rather than asking
    // whether the card ended up visible: unlike the gauge-anchored card (whose click-driven gate skips the
    // notification check outright for CardAnchor.NearCursor), the case-open notice's own gate always calls
    // the real SHQueryUserNotificationState, and it reports QUNS_BUSY on this machine's private test
    // desktop - a real, current fact about this environment, recorded here rather than forced around. A
    // fake ICardEnvironment can now be substituted (TrayHarness's own cardEnvironmentFactory parameter,
    // added for the two tests below that need the notice genuinely visible); this one is left as it stands,
    // since the log line already proves what it is actually for: OnCaseOpened reached RequestShowOnUiThread
    // and ran its gate for real, rather than the subscription being missing or the body doing nothing.
    [TestMethod]
    public void RaisingCaseOpenedReachesThePresentersOwnGate()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { ShowOnTaskbar = false, CaseOpenCard = true, Enabled = true });
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardWiredForTest, "Sanity: the case-open card must be wired first.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Case-open card"), "Sanity: nothing has raised CaseOpened yet.");

            tray.Context.RaiseCaseOpenedForTest();
            tray.PumpUntilIdle();

            Assert.IsTrue(
                tray.Log.Has(LogLevel.Info, "Case-open card"),
                "OnCaseOpened must actually reach the presenter's own gate, not merely a wired but inert " +
                "presenter. Log: " + string.Join(" | ", tray.Log.Entries.Select(e => e.Level + ":" + e.Message)));
        });
    }

    // Before the fix, a left click on the gauge while the case-open notice was already showing left the
    // notice sitting there: CaseOpenCardGate.OwnCardOpen already refuses to show a fresh notice over the
    // owner's own card, but nothing closed the reverse, so a click opened a second Earshot window stacked
    // on the first rather than replacing it. A fake ICardEnvironment (TrayHarness's own
    // cardEnvironmentFactory, defaulting to QUNS_ACCEPTS_NOTIFICATIONS) lets the notice genuinely reach
    // screen here, unlike RaisingCaseOpenedReachesThePresentersOwnGate above.
    [TestMethod]
    public void ClickingTheGaugeWhileTheNoticeIsShowingHidesItThenOpensTheCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { ShowOnTaskbar = true, CaseOpenCard = true, LeftClickConnects = false, Enabled = true },
                cardEnvironmentFactory: () => new Phase5.FakeCardEnvironment());
            tray.PumpUntilIdle();

            tray.Context.RaiseCaseOpenedForTest();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardIsShownForTest, "Sanity: the notice must actually be open before the gauge is clicked.");
            Assert.IsFalse(tray.Context.WidgetCardIsShownForTest, "Sanity: the gauge-anchored card must not be open yet.");

            tray.Context.RequestWidgetCardForTest();
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Context.WidgetCaseOpenCardIsShownForTest, "The notice must be hidden once the gauge-anchored card opens, not left stacked underneath it.");
            Assert.IsTrue(tray.Context.WidgetCardIsShownForTest, "The gauge-anchored card must actually open.");
        });
    }

    // Before the fix, a notice already open when a connect or disconnect started anywhere else (the tray
    // icon here, but the menu or a hotkey read the same IsBusy) kept showing the button it last rendered,
    // exactly the staleness TheOpenCardsButtonDisablesTheMomentAnyToggleStartsAndReEnablesWhenItEnds already
    // proves is fixed for the gauge-anchored card: CaseOpenCardPresenter had no Refresh() at all, so nothing
    // ever re-rendered the notice's button once it was up. Mirrors that test's own technique exactly, for
    // the notice instead of the card.
    [TestMethod]
    public void TheNoticesOwnButtonDisablesTheMomentAnyToggleStartsAndReEnablesWhenItEnds()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var connect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { ShowOnTaskbar = false, CaseOpenCard = true, LeftClickConnects = true, Enabled = true },
                cardEnvironmentFactory: () => new Phase5.FakeCardEnvironment());
            tray.Connection.OnConnect = _ => connect.Task;
            tray.PumpUntilIdle();

            tray.Context.RaiseCaseOpenedForTest();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardIsShownForTest, "Sanity: the notice must actually be open before the toggle starts.");
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardButtonEnabledForTest, "Sanity: the button starts enabled with nothing in flight.");

            // No PumpUntilIdle here: the click runs synchronously up to the coordinator's own await, which
            // never completes until connect.SetResult below, matching
            // TheOpenCardsButtonDisablesTheMomentAnyToggleStartsAndReEnablesWhenItEnds's own reasoning.
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            Assert.IsTrue(tray.Context.IsBusy, "Sanity: the click must have started a toggle still in flight.");
            TrayHarness.PumpUntil(() => tray.Context.WidgetCaseOpenCardButtonEnabledForTest == false,
                "The notice's own button must disable the moment any toggle starts, the same as the gauge-anchored card's.");

            connect.SetResult(Confirmed("Connected"));
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Context.WidgetCaseOpenCardButtonEnabledForTest, "...and re-enable once that toggle ends.");
        });
    }

    // Before the fix, ShowOnTaskbar off meant Enabled off, which stopped the data
    // pipeline outright: the low battery alert, the case-open card and auto-pause died silently while their
    // own menu items stayed checked. The case-open card in particular does not need the gauge to exist at
    // all (it places itself near the tray with no gauge to anchor above), so it must stay wired here even
    // with the gauge fully off.
    [TestMethod]
    public void TheCaseOpenCardStaysWiredWithTheGaugeOffWhenAnotherConsumerWantsTheWatcher()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = (s.Widget with { ShowOnTaskbar = false, CaseOpenCard = true, LowBatteryAlert = true }).WithWatcherRecomputed());
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.Widget.Enabled, "Sanity: the low battery alert alone must still want the watcher.");
            Assert.IsTrue(tray.Context.WidgetDataPipelineWiredForTest, "The data pipeline must be wired for the case-open card to have anything to show.");
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardWiredForTest, "The case-open card itself must be wired even with the gauge off.");
            Assert.AreEqual(0, tray.TaskbarReaderFactoryCalls, "The gauge itself must stay off: nothing here asked for it.");
        });
    }

    // Before the fix, ApplyWidget's ShowOnTaskbar-off branch tore down the taskbar watcher and the appbar
    // registration but never touched the card presenter it built alongside them (WireGauge's own
    // "if (_gaugeController is null)" block): a card already open above the gauge stayed open and visible
    // with nothing left to anchor to, since the gauge that placed it there was now gone.
    [TestMethod]
    public void TurningOffShowOnTaskbarHidesAnyOpenWidgetCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = (s.Widget with { ShowOnTaskbar = true, LeftClickConnects = false }).WithWatcherRecomputed());
            tray.PumpUntilIdle();

            tray.Context.RequestWidgetCardForTest();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Context.WidgetCardIsShownForTest, "Sanity: the card must actually be open before the setting turns off.");

            tray.Settings.Update(s => s.Widget = s.Widget with { ShowOnTaskbar = false });
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Context.WidgetCardIsShownForTest,
                "Turning the gauge off must close any card anchored to it, not leave it open with nothing left to anchor to.");
        });
    }

    // Before the fix, GaugeBoundsIfShown checked only IsDisposed and IsHandleCreated: TransitionHidden
    // (GaugeController, e.g. the ABN_FULLSCREENAPP fast path below) hides the surface with SetWindowPos
    // rather than disposing it, so the real handle and the gauge's last-shown Bounds both survived, and a
    // caller asking "is the gauge on screen right now" got back a stale rectangle for a gauge nobody could
    // actually see. Proving it needs the gauge genuinely Shown at least once, which every other test in
    // this file stops short of (the fake taskbar reader's own constructor-time default, Fail(NoTaskbar),
    // never clears): this one feeds it a real free-space layout instead, so it also calls
    // WidgetRealSurfaceGuardTests.AllowRealConstruction(RealWidgetSurface.GaugeWindow) for the one real GaugeWindow that puts on screen,
    // on the same private desktop GaugeWindowTests itself always runs on.
    [TestMethod]
    public void GaugeBoundsIfShownIsNullOnceAFullScreenAppHidesTheGauge()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            // The same shape GaugeControllerTests' own FreeSpaceLayout uses: a taskbar with the Start
            // button, eight pinned buttons and the tray icons rectangle, leaving a gap between the last
            // button and the tray wide enough for the gauge.
            var bar = new Rectangle(0, 1032, 1920, 48);
            var start = new Rectangle(762, 1032, 45, 48);
            List<Rectangle> buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48)).ToList();
            var layout = new TaskbarLayout(0, bar, TaskbarEdge.Bottom, AutoHide: false,
                new Rectangle(0, 0, 1920, 1080), [start, .. buttons, new Rectangle(1678, 1032, 242, 48)], start,
                Dpi: 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
                NotificationArea: new Rectangle(1678, 1032, 242, 48));
            tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(layout));
            // LeftClickConnects itself is irrelevant here: flipping it is only a way to make ApplyWidget
            // poke the watcher again, now that the fake reader has a real, free-space layout queued for the
            // next read to pick up (the harness's own first poke ran before this test set it, and saw only
            // the fake's constructor-time default of Fail(NoTaskbar)).
            tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = true });
            TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is GaugeState.Shown,
                "Sanity: a free-space layout must show the gauge.");
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsNotNull(tray.Context.GaugeBoundsIfShownForTest, "Sanity: a shown gauge must report its bounds.");

            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 1);
            tray.Context.Window.Dispatch(ref message);

            Assert.IsInstanceOfType<GaugeState.Hidden>(tray.Context.WidgetGaugeStateForTest, "Sanity: opening a full-screen app hides the gauge.");
            Assert.IsNull(tray.Context.GaugeBoundsIfShownForTest,
                "A hidden gauge's old bounds must not be handed out as though the gauge were still on screen.");
        });
    }

    // Before the fix, UpdatePresentation refreshed the icon and the tooltip on every busy-state change
    // (StartToggle's own set-true, and its finally's set-false) but never the widget card, so a card left
    // open while a toggle started anywhere else - the tray icon here, but the menu or a hotkey read the
    // same IsBusy - kept showing whatever button state it was last rendered with until the connection
    // itself actually changed and IWidgetStatus.Changed happened to fire.
    [TestMethod]
    public void TheOpenCardsButtonDisablesTheMomentAnyToggleStartsAndReEnablesWhenItEnds()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var connect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true, LeftClickConnects = true });
            tray.Connection.OnConnect = _ => connect.Task;
            tray.PumpUntilIdle();

            // LeftClickConnects on routes the tray icon's own left click (and a gauge click) straight to a
            // toggle, so RequestWidgetCardForTest is the only way left to open the card itself for this test.
            tray.Context.RequestWidgetCardForTest();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Context.WidgetCardIsShownForTest, "Sanity: the card must be open before the toggle starts.");
            Assert.IsTrue(tray.Context.WidgetCardButtonEnabledForTest, "Sanity: the button starts enabled with nothing in flight.");

            // No PumpUntilIdle here: the click runs synchronously up to the coordinator's own await, which
            // never completes until connect.SetResult below, so pumping until idle would just time out
            // waiting for a toggle this test is deliberately holding open. UpdatePresentation's own
            // Refresh() call only posts the card's re-render; PumpUntil (not a single DoEvents) waits for
            // that posted work to actually run, the same way PumpUntilIdle would if it could.
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));

            Assert.IsTrue(tray.Context.IsBusy, "Sanity: the click must have started a toggle still in flight.");
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardButtonEnabledForTest == false,
                "An open card's button must disable the moment any toggle starts, not only one started from the card's own button.");

            connect.SetResult(Confirmed("Connected"));
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Context.WidgetCardButtonEnabledForTest, "...and re-enable once that toggle ends.");
        });
    }

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    private static ConnectResult Confirmed(string message) => new(ConnectOutcome.Confirmed, message, []);
}
