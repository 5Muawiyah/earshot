using Earshot.App;
using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// What the tray joins up for Moved to, proved through the real TrayContext with only the outside replaced: the leave pause on leave
// sees reaches the widget's moved-to rule, and the verdict reaches the card, which reads the setting from the tray's own settings.
[TestClass]
public sealed class MovedToTrayWiringTests
{
    private static Action<EarshotSettings> WidgetOn(bool movedTo = true) =>
        s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true, CaseOpenCardOn = true, MovedToOn = movedTo };

    [TestMethod]
    public void ALeaveSeenByPauseOnLeaveStartsTheWidgetsWatchAndItIsJudgedAtTheBound()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var worker = new AudioWorker(new CapturingLog());
            try
            {
                using var tray = new TrayHarness(
                    snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(),
                    cardEnvironmentFactory: static () => new Phase5.FakeCardEnvironment(), arrange: t => t.Registry.Worker = worker);
                tray.PumpUntilIdle();
                PauseOnLeave pause = tray.Coordinator.LeavePause!;
                Assert.IsNotNull(pause, "The tray built no pause on leave.");

                pause.OnRender(RenderState.Active, Devices.Container, tray.Time.GetUtcNow(), changeInFlight: null).GetAwaiter().GetResult();
                tray.Time.Advance(TimeSpan.FromSeconds(1));
                pause.OnRender(RenderState.NotActive, Devices.Container, tray.Time.GetUtcNow(), changeInFlight: null).GetAwaiter().GetResult();
                Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Move: leave seen, not started by Earshot"));
                tray.Time.Advance(MovedToRule.MovedBound);
                tray.PumpUntilIdle();

                Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Move: 0 messages by +10000 ms, case-known 0: not heard."), "The leave never reached the rule.");
            }
            finally
            {
                worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [TestMethod]
    public void AnOwnLeaveNeverStartsTheWatch()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var worker = new AudioWorker(new CapturingLog());
            try
            {
                using var tray = new TrayHarness(
                    snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(),
                    cardEnvironmentFactory: static () => new Phase5.FakeCardEnvironment(), arrange: t => t.Registry.Worker = worker);
                tray.PumpUntilIdle();
                PauseOnLeave pause = tray.Coordinator.LeavePause!;

                pause.OnRender(RenderState.Active, Devices.Container, tray.Time.GetUtcNow(), changeInFlight: null).GetAwaiter().GetResult();
                pause.BeforeOwnLeaveAsync("Disconnect", Devices.Container, TimeSpan.FromMilliseconds(400), CancellationToken.None).GetAwaiter().GetResult();
                pause.OnRender(RenderState.NotActive, Devices.Container, tray.Time.GetUtcNow(), changeInFlight: null).GetAwaiter().GetResult();
                tray.Time.Advance(MovedToRule.MovedBound);
                tray.PumpUntilIdle();

                Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Move: own leave (Disconnect), nothing shown."));
                Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Move: 0 messages"), "An own leave started a watch.");
            }
            finally
            {
                worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    // The card reads the setting from the tray's settings at each use: off, a verdict opens nothing; on, it goes as far as the
    // check that the AirPods are on another device (they are not in this snapshot), so the verdict did reach the presenter.
    [TestMethod]
    public void TheVerdictReachesTheCardWhichReadsTheTraysSetting()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(movedTo: false),
                cardEnvironmentFactory: static () => new Phase5.FakeCardEnvironment());
            tray.PumpUntilIdle();

            tray.Context.RaiseMovedToForTest();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "Card: not shown (moved to), its setting is off."));

            tray.Settings.Update(s => s.Widget = s.Widget with { MovedToOn = true });
            tray.Context.RaiseMovedToForTest();
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "Card: not shown (moved to), the AirPods are not on another device now."));
            Assert.IsFalse(tray.Context.WidgetCaseOpenCardIsShownForTest);
        });
    }
}
