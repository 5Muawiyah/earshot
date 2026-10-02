using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// Pins the order a security review of the data side asked for: the widget's BLE watcher is suspended
// only after the shut-down or sleep hand-back has finished, never before it and never concurrently with
// it. Drives the real TrayContext (TrayHarness, as TrayHandBackTests does) with a fake block step that
// stays in flight for a measured moment, and substitutes the widget's own Suspend/Resume/Close through
// TrayContext.SetWidgetLifecycleForTest so the assertion is about ordering, not about a real BLE watcher.
[TestClass]
public sealed class WidgetHandBackOrderTests
{
    private static SessionEndingEventArgs WmEndSession() => new(isQuery: false, ending: true, flags: 0);

    [TestMethod]
    public void SuspendRunsOnlyAfterTheSessionEndHandBackFinishes()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                time: TimeProvider.System,
                handBackBudget: TimeSpan.FromSeconds(2),
                disconnectHandBackWait: TimeSpan.FromMilliseconds(500));

            bool suspendedWhileHandBackWasStillRunning = false;
            bool handBackStillRunningAtSomePoint = false;
            tray.Block.OnBlock = _ => Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80));
                handBackStillRunningAtSomePoint = true;
                return ControllerResult.Ok("Blocked at boot");
            });

            int suspendCalls = 0;
            tray.Context.SetWidgetLifecycleForTest(
                suspend: () =>
                {
                    suspendCalls++;
                    if (!handBackStillRunningAtSomePoint)
                    {
                        // Suspend fired before the fake block step ever ran: it cannot have waited for the
                        // hand-back, which is exactly the ordering fault the review flagged.
                        suspendedWhileHandBackWasStillRunning = true;
                    }
                },
                resume: () => { },
                close: () => { });

            tray.Context.OnSessionEnding(null, WmEndSession());

            Assert.AreEqual(1, suspendCalls, "Suspend must run exactly once for one session end.");
            Assert.IsTrue(handBackStillRunningAtSomePoint, "Sanity: the fake block step must actually have run.");
            Assert.IsFalse(suspendedWhileHandBackWasStillRunning, "Suspend must never run before the hand-back has finished.");
        });
    }

    [TestMethod]
    public void SuspendRunsOnlyAfterTheSleepHandBackFinishes()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                time: TimeProvider.System,
                handBackBudget: TimeSpan.FromSeconds(2),
                disconnectHandBackWait: TimeSpan.FromMilliseconds(500));

            bool suspendedTooEarly = false;
            bool handBackRan = false;
            tray.Block.OnBlock = _ => Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80));
                handBackRan = true;
                return ControllerResult.Ok("Blocked at boot");
            });

            int suspendCalls = 0;
            tray.Context.SetWidgetLifecycleForTest(
                suspend: () =>
                {
                    suspendCalls++;
                    if (!handBackRan)
                    {
                        suspendedTooEarly = true;
                    }
                },
                resume: () => { },
                close: () => { });

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.Suspend));

            Assert.AreEqual(1, suspendCalls);
            Assert.IsTrue(handBackRan, "Sanity: the fake block step must actually have run.");
            Assert.IsFalse(suspendedTooEarly, "Suspend must never run before the sleep hand-back has finished.");
        });
    }

    // The case-open card's own gate has two legs that read the coordinator's HandBackInProgress and
    // SessionEndInProgress separately, so the notice can say which one is happening
    // (RequestShowOnUiThread's own "the session is ending" vs "a hand-back is running"). Nothing before
    // this proved which of the two each leg actually reads: a mutant swapping the two wires in
    // TrayContext.Widget.cs would leave every other test in this suite exactly as green, since a session
    // end always sets both flags together (SessionEndInProgress by definition, HandBackInProgress because
    // the hand-back itself runs inside it) and CaseOpenCardTests.cs proves each leg's own refusal against a
    // fake gate, never against the real coordinator's own two properties. A sleep is the one real case
    // where they come apart: PowerEventKind.Suspend runs the hand-back (HandBackInProgress true) without
    // ever being a session end (SessionEndInProgress stays false throughout), read here from inside the
    // fake block step while the hand-back is still genuinely in progress.
    [TestMethod]
    public void TheCaseOpenGatesTwoHandBackLegsReadTheMatchingCoordinatorFlagDuringASleepHandBack()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s =>
                {
                    s.HandBackOnShutdownAndSleep = true;
                    s.Widget = s.Widget with { CaseOpenCardOn = true, Enabled = true };
                },
                time: TimeProvider.System,
                handBackBudget: TimeSpan.FromSeconds(2),
                disconnectHandBackWait: TimeSpan.FromMilliseconds(500));
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardWiredForTest, "Sanity: the case-open card must be wired first.");

            bool? handBackLegDuringHandBack = null;
            bool? sessionEndLegDuringHandBack = null;
            tray.Block.OnBlock = _ => Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80));
                handBackLegDuringHandBack = tray.Context.WidgetCaseOpenGateHandBackInProgressForTest;
                sessionEndLegDuringHandBack = tray.Context.WidgetCaseOpenGateSessionEndInProgressForTest;
                return ControllerResult.Ok("Blocked at boot");
            });

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.Suspend));

            Assert.IsNotNull(handBackLegDuringHandBack, "Sanity: the fake block step must actually have run.");
            Assert.IsTrue(handBackLegDuringHandBack, "A sleep hand-back is in progress: the gate's HandBackInProgress leg must read true.");
            Assert.IsFalse(sessionEndLegDuringHandBack, "A sleep is never a session end: the gate's SessionEndInProgress leg must read false.");
        });
    }

    [TestMethod]
    public void ResumeAutomaticResumesTheWidget()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));

            int resumeCalls = 0;
            tray.Context.SetWidgetLifecycleForTest(suspend: () => { }, resume: () => resumeCalls++, close: () => { });

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.ResumeAutomatic));

            Assert.AreEqual(1, resumeCalls);
        });
    }
}
