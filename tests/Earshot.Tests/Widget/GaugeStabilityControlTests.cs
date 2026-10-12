using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The replays show the gauge holding still through disturbances that are not real. These show it still acts on the ones that
// are: a room that is lost, a taskbar that goes, a position that changes for good each take effect, inside the window the design
// allows and once. A hold that never gave way would pass every replay.
[TestClass]
public sealed class GaugeStabilityControlTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Onset = TimeSpan.FromSeconds(100);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ITaskbarReader.Result Ok(TaskbarLayout layout) => ITaskbarReader.Result.Ok(layout);

    private static ITaskbarReader.Result Failed(TaskbarReadFailureStep step) =>
        ITaskbarReader.Result.Fail(new TaskbarReadFailure(step, new StepOutcome("uia:" + step, false, 7, "E_FAIL", null)));

    // ----- N1: a real loss of room -----

    [TestMethod]
    public void RoomLostForGoodHidesTheGaugeOnceFiveSecondsLater()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Elapsed >= Onset ? StabilityLayouts.NoRoom() : StabilityLayouts.Free()));
        gauge.Poll(Poll);

        scene.Run(Seconds(300));

        Assert.AreEqual(1, gauge.Hides, "hides=" + gauge.Hides);
        Assert.AreEqual(Onset + Seconds(5), gauge.HideTimes[0], "Hidden when the third confirming read arrived, five seconds after the first.");
        Assert.AreEqual(2, gauge.Requests, "Two confirming reads were asked for, no more.");
        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NoFreeSpace), gauge.Controller.State);
        Assert.AreEqual(1, gauge.Shows, "It is not shown again.");
    }

    // The watcher may deliver nothing but its own poll: the verdict is then settled by the third poll, never before the window.
    [TestMethod]
    public void RoomLostForGoodHidesAtTheThirdPollWhenNoConfirmingReadComes()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Elapsed >= Onset ? StabilityLayouts.NoRoom() : StabilityLayouts.Free()), answerRequests: false);
        gauge.Poll(Poll);

        scene.Run(Seconds(300));

        Assert.AreEqual(1, gauge.Hides);
        Assert.AreEqual(Onset + Seconds(40), gauge.HideTimes[0], "Polls at 100, 120 and 140 s: the third.");
        Assert.IsGreaterThanOrEqualTo(Seconds(5), gauge.HideTimes[0] - Onset);
    }

    // ----- N3: a real move -----

    [TestMethod]
    public void ALastingMoveIsAppliedOnceFiveSecondsLater()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(StabilityLayouts.Free(read.Elapsed >= Onset ? 1650 : 1678)));
        gauge.Poll(Poll);

        scene.Run(Seconds(300));

        Assert.AreEqual(1, gauge.Moves, "moves=" + gauge.Moves);
        Assert.AreEqual(Onset + Seconds(5), gauge.MoveTimes[0]);
        Assert.AreEqual(StabilityLayouts.GaugeAt(1650), gauge.Bounds);
        Assert.AreEqual(0, gauge.Hides);
        Assert.AreEqual(1, gauge.Shows);
    }

    [TestMethod]
    public void ALastingMoveIsAppliedAtTheThirdPollWhenNoConfirmingReadComes()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(StabilityLayouts.Free(read.Elapsed >= Onset ? 1650 : 1678)), answerRequests: false);
        gauge.Poll(Poll);

        scene.Run(Seconds(300));

        Assert.AreEqual(1, gauge.Moves);
        Assert.AreEqual(Onset + Seconds(40), gauge.MoveTimes[0]);
    }

    // ----- N4: failed reads and a missing taskbar -----

    [TestMethod]
    public void ReadFailuresThatLastHideTheGaugeAfterFiveSecondsAndThreeReads()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => read.Elapsed >= Onset ? Failed(TaskbarReadFailureStep.Occupants) : Ok(StabilityLayouts.Free()));
        gauge.Poll(Poll);

        scene.Run(Seconds(200));

        Assert.AreEqual(1, gauge.Hides);
        Assert.AreEqual(Onset + Seconds(5), gauge.HideTimes[0]);
        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.ReadFailed), gauge.Controller.State);
        Assert.IsTrue(gauge.Log.Has(LogLevel.Info, "Gauge hidden (ReadFailed Occupants E_FAIL (7) after 3 consecutive failures)."));
    }

    [TestMethod]
    public void ANoTaskbarThatLastsHidesTheGaugeAfterFiveSecondsAndThreeReads()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => read.Elapsed >= Onset ? Failed(TaskbarReadFailureStep.NoTaskbar) : Ok(StabilityLayouts.Free()));
        gauge.Poll(Poll);

        scene.Run(Seconds(200));

        Assert.AreEqual(1, gauge.Hides);
        Assert.AreEqual(Onset + Seconds(5), gauge.HideTimes[0]);
        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NoTaskbar), gauge.Controller.State);
    }

    // Three failures inside a second are three reads but not five seconds.
    [TestMethod]
    public void ThreeFailedReadsInsideOneSecondDoNotHideTheGauge()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => read.Elapsed >= Onset && read.Elapsed < Onset + Seconds(1.1) ? Failed(TaskbarReadFailureStep.NoTaskbar) : Ok(StabilityLayouts.Free()));
        gauge.Poll(Poll);
        gauge.ReadAt([Onset + Seconds(0.5), Onset + Seconds(1.0)]);

        scene.Run(Seconds(200));

        Assert.AreEqual(0, gauge.Hides);
        Assert.IsTrue(gauge.IsShown);
    }

    // Time alone is not enough either: five seconds of one failed read is one read.
    [TestMethod]
    public void OneFailedReadFollowedByNothingForAWhileDoesNotHideTheGauge()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => read.Index == 3 ? Failed(TaskbarReadFailureStep.Occupants) : Ok(StabilityLayouts.Free()), answerRequests: false);
        gauge.Poll(Poll);

        scene.Run(Seconds(200));

        Assert.AreEqual(0, gauge.Hides);
    }

    // ----- N5: what is not a reading of the bar stays immediate -----

    [TestMethod]
    public void AFullScreenNoticeHidesOnTheCallAndEndsWhatWasWaiting()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 0 ? StabilityLayouts.Free() : StabilityLayouts.NoRoom()));
        gauge.Read();
        gauge.Read();
        Assert.IsTrue(gauge.IsShown, "Sanity: one no-room read does not hide it.");
        Assert.AreEqual(2, scene.Time.LiveTimers, "The two confirming reads are armed.");

        gauge.Apply(() => gauge.Controller.NotifyFullScreenApp(opening: true));

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.FullScreenNotified), gauge.Controller.State);
        Assert.AreEqual(1, gauge.Hides);
        Assert.AreEqual(0, scene.Time.LiveTimers, "Nothing left waiting.");
        int requests = gauge.Requests;
        scene.Run(Seconds(10));
        Assert.AreEqual(requests, gauge.Requests, "No confirming read is asked for a verdict that ended.");
    }

    [TestMethod]
    public void ABusyNotificationStateHidesOnThatRead()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 0 ? StabilityLayouts.Free() : StabilityLayouts.Free() with { NotificationState = Shell.QUNS_BUSY }));
        gauge.Read();

        gauge.Read();

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NotificationState), gauge.Controller.State);
        Assert.AreEqual(1, gauge.Hides);
    }

    [TestMethod]
    public void ACoveredBarHidesOnThatRead()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 0 ? StabilityLayouts.Free() : StabilityLayouts.Free() with { Covered = true }));
        gauge.Read();

        gauge.Read();

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.Covered), gauge.Controller.State);
    }

    // A change the owner made is not something to wait out, even with a move already waiting.
    [TestMethod]
    public void ATurnOfTheGaugePositionSettingMovesTheGaugeOnTheNextReadEvenWhileAMoveIsWaiting()
    {
        var scene = new StabilityScene();
        GaugePosition position = GaugePosition.RightEnd;
        var gauge = new ScriptedGauge(
            scene, read => Ok(StabilityLayouts.Free(read.Index == 1 ? 1650 : 1678)), position: () => position);
        gauge.Read();
        gauge.Read();
        Assert.AreEqual(0, gauge.Moves, "Sanity: a move on one read waits.");

        position = GaugePosition.NextToApps;
        gauge.Read();

        Assert.AreEqual(1, gauge.Moves, "The setting moved it on that read.");
        Assert.AreEqual(1163, gauge.Bounds!.Value.Left, "Four pixels after the last button, which ends at 1159.");
    }

    [TestMethod]
    public void AChangeOfTheDisplaysScaleMovesTheGaugeOnThatRead()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 0 ? StabilityLayouts.Free() : AtScale150()));
        gauge.Read();

        gauge.Read();

        Assert.AreEqual(1, gauge.Moves);
        Assert.AreEqual(111, gauge.Bounds!.Value.Width, "74 px at 100% is 111 px at 150%.");
        Assert.AreEqual(0, gauge.Hides);
    }

    // The same bar at 150%: 72 px high, the gauge 111 by 60, still 12 px left of the notification area.
    private static TaskbarLayout AtScale150()
    {
        var bar = new Rectangle(0, 1008, 1920, 72);
        var start = new Rectangle(762, 1008, 68, 72);
        var tray = new Rectangle(1678, 1008, 242, 72);
        return new TaskbarLayout(
            0, bar, TaskbarEdge.Bottom, false, StabilityLayouts.Monitor, [start, new Rectangle(830, 1008, 66, 72), tray], start,
            144, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null, NotificationArea: tray);
    }

    [TestMethod]
    public void TurningTheSetOffAndDisposingItRemoveTheGaugesAtOnce()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, _ => true);
        set.Reconcile();
        Assert.IsTrue(set.HasGauge);

        set.Set.Reconcile(wanted: false);

        Assert.AreEqual(0, set.Set.Count, "Gauge display is no longer All displays: removed on that call.");
        Assert.AreEqual(1, set.Removals);

        set.Reconcile();
        Assert.IsTrue(set.HasGauge, "It comes back when wanted.");
        set.Set.Dispose();
        Assert.AreEqual(0, set.Set.Count, "Disposed: removed on that call.");
    }

    [TestMethod]
    public void TurningTheSetOffWhileARemovalWaitsLeavesNothingToFireLater()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed > Onset + Seconds(155));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);
        scene.At(Onset + Seconds(1), () => set.Set.Reconcile(wanted: false));

        scene.Run(Seconds(400));

        Assert.AreEqual(1, set.Removals, "Only the removal the setting made.");
        Assert.AreEqual(1, set.Adds, "The confirming reconciles of the wait did not add a gauge back after the setting turned it off.");
        Assert.IsFalse(set.HasGauge);
    }

    // ----- N6: room back after a hide for no room -----

    [TestMethod]
    public void OneReadWithRoomDoesNotShowAGaugeHiddenForNoRoom()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Elapsed == Seconds(200) ? StabilityLayouts.Free() : read.Elapsed < Onset ? StabilityLayouts.Free() : StabilityLayouts.NoRoom()));
        gauge.Poll(Poll);

        scene.Run(Seconds(300));

        Assert.AreEqual(1, gauge.Hides, "Hidden for no room at 105 s.");
        Assert.AreEqual(1, gauge.Shows, "The single read with room at 200 s did not show it.");
        Assert.IsFalse(gauge.IsShown);
        Assert.IsTrue(gauge.Log.Has(LogLevel.Debug, "Gauge stays hidden: room on one read; it is shown if that lasts 2 s."));
        Assert.IsTrue(gauge.Log.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("room lasted 1 read over 0.0 s; the gauge stays hidden.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RoomThatLastsShowsAGaugeHiddenForNoRoomAtTheSecondReadTwoAndAHalfSecondsLater()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Elapsed >= Seconds(200) || read.Elapsed < Onset ? StabilityLayouts.Free() : StabilityLayouts.NoRoom()));
        gauge.Poll(Poll);

        scene.Run(Seconds(300));

        Assert.AreEqual(2, gauge.Shows);
        Assert.AreEqual(Seconds(202.5), gauge.ShowTimes[1], "The poll at 200 s, and the first confirming read.");
        Assert.IsTrue(gauge.IsShown);
    }

    [TestMethod]
    public void TwoReadsWithRoomTwoSecondsApartShowIt()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Elapsed >= Seconds(200) || read.Elapsed < Onset ? StabilityLayouts.Free() : StabilityLayouts.NoRoom()));
        gauge.Poll(Poll);
        gauge.ReadAt([Seconds(200) + Seconds(2)]);

        scene.Run(Seconds(300));

        Assert.AreEqual(Seconds(202), gauge.ShowTimes[1]);
    }

    // Only a hide for no room waits to be shown again: the others show at the next read.
    [TestMethod]
    public void AGaugeHiddenForAnythingButNoRoomIsShownAtOnceWhenTheBarIsRight()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 1 ? StabilityLayouts.Free() with { NotificationState = Shell.QUNS_BUSY } : StabilityLayouts.Free()));
        gauge.Read();
        gauge.Read();
        Assert.IsFalse(gauge.IsShown, "Hidden for a full-screen state.");

        gauge.Read();

        Assert.IsTrue(gauge.IsShown, "Shown on the first read that says so.");
        Assert.IsTrue(gauge.Log.Has(LogLevel.Info, "(full-screen app closed)"));
    }

    // ----- What the controller asks for, and what it writes -----

    [TestMethod]
    public void TheConfirmingReadsAreAskedForOnceEachPerWaitAndNotAtAllForAVerdictThatLapsedFirst()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 1 ? StabilityLayouts.NoRoom() : StabilityLayouts.Free()), answerRequests: false);
        gauge.Read();
        gauge.Read();
        Assert.AreEqual(2, scene.Time.LiveTimers);
        gauge.Read();
        Assert.AreEqual(0, scene.Time.LiveTimers, "A read that agrees with the gauge ends the wait and its timers.");

        scene.Run(Seconds(10));

        Assert.AreEqual(0, gauge.Requests);
    }

    [TestMethod]
    public void ADifferentVerdictStartsTheWaitAgainAndTheOldOnesTimersAreReplaced()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => read.Index switch
        {
            0 => Ok(StabilityLayouts.Free()),
            1 => Ok(StabilityLayouts.NoRoom()),
            _ => Failed(TaskbarReadFailureStep.Occupants),
        }, answerRequests: false);
        gauge.Read();
        gauge.Read();
        scene.Time.Advance(Seconds(2));
        gauge.Read();

        Assert.AreEqual(2, scene.Time.LiveTimers, "Two timers, those of the failure's wait; the no-room wait's are gone.");
        scene.Run(Seconds(10));
        Assert.AreEqual(2, gauge.Requests);
    }

    [TestMethod]
    public void DisposingTheControllerEndsTheWait()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Index == 0 ? StabilityLayouts.Free() : StabilityLayouts.NoRoom()));
        gauge.Read();
        gauge.Read();
        Assert.AreEqual(2, scene.Time.LiveTimers);

        gauge.Controller.Dispose();

        Assert.AreEqual(0, scene.Time.LiveTimers);
        scene.Run(Seconds(10));
        Assert.AreEqual(0, gauge.Requests);
    }

    [TestMethod]
    public void AVerdictThatIsWaitingWritesOneLineWhenItStartsAndOneWhenItLapsesNeverOnePerRead()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(read.Elapsed >= Onset && read.Elapsed < Onset + Seconds(2.1) ? StabilityLayouts.NoRoom() : StabilityLayouts.Free()), answerRequests: false);
        gauge.Poll(Poll);
        for (double second = 100.25; second <= 102.0; second += 0.25)
        {
            gauge.ReadAt([Seconds(second)]);
        }

        scene.Run(Seconds(200));

        Assert.AreEqual(1, gauge.Log.Entries.Count(e => e.Message.StartsWith("Gauge kept at 1596,1036 74x40: NoRoom on one read; it hides if that lasts 5 s.", StringComparison.Ordinal)));
        Assert.AreEqual(1, gauge.Log.Entries.Count(e => e.Message.Contains("NoRoom lasted 9 reads over 2.0 s; the gauge stays.", StringComparison.Ordinal)),
            string.Join(" | ", gauge.Log.Entries.Select(e => e.Message)));
        Assert.AreEqual(0, gauge.Hides);
    }

    [TestMethod]
    public void AMoveThatIsWaitingSaysWhereToAndWhatItWaitsFor()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => Ok(StabilityLayouts.Free(read.Index == 1 ? 1650 : 1678)));
        gauge.Read();
        gauge.Read();

        Assert.IsTrue(gauge.Log.Has(LogLevel.Debug, "Gauge kept at 1596,1036 74x40: a move to 1568,1036 74x40 on one read; it moves if that lasts 5 s."));
    }

    // ----- The gate itself -----

    [TestMethod]
    public void AHeldVerdictNeedsEnoughReadsAndEnoughTime()
    {
        var time = new TestTimeProvider();
        var gate = new HeldVerdict<int>(time, TimeSpan.FromSeconds(5), 3);

        Assert.IsFalse(gate.Observe(7));
        Assert.IsFalse(gate.Observe(7), "Two reads.");
        Assert.IsFalse(gate.Observe(7), "Three reads but no time.");
        Assert.AreEqual(3, gate.Count);
        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.IsFalse(gate.Observe(7), "Time short of the window.");
        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.IsTrue(gate.Observe(7), "Reads and time.");
    }

    [TestMethod]
    public void AHeldVerdictWithTimeButTooFewReadsHasNotHeld()
    {
        var time = new TestTimeProvider();
        var gate = new HeldVerdict<int>(time, TimeSpan.FromSeconds(5), 3);

        Assert.IsFalse(gate.Observe(7));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.IsFalse(gate.Observe(7), "Two reads, a long time.");
        Assert.IsTrue(gate.Observe(7));
    }

    [TestMethod]
    public void ADifferentValueStartsTheCountAgainAndClearEndsTheWait()
    {
        var time = new TestTimeProvider();
        var gate = new HeldVerdict<int>(time, TimeSpan.FromSeconds(5), 3);
        _ = gate.Observe(1);
        _ = gate.Observe(1);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.IsFalse(gate.Observe(2), "A different value is a new first read.");
        Assert.AreEqual(1, gate.Count);
        Assert.AreEqual(2, gate.Candidate);
        Assert.AreEqual(TimeSpan.Zero, gate.Age);

        gate.Clear();
        Assert.IsFalse(gate.Pending);
        Assert.AreEqual(0, gate.Count);
        Assert.IsFalse(gate.Observe(2));
        Assert.AreEqual(1, gate.Count);
    }

    // ----- The set -----

    // N2: a taskbar that really goes.
    [TestMethod]
    public void ATaskbarGoneForGoodRemovesItsGaugeOnceByTenAndAHalfSeconds()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed >= Onset + Seconds(155));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);

        scene.Run(Seconds(150));

        Assert.AreEqual(1, set.Removals, "removals=" + set.Removals);
        Assert.AreEqual(Onset + Seconds(10.5), set.RemovalTimes[0], "At the third look: the read at 100 s, and the confirming reconciles at 105 s and 110.5 s.");
        Assert.AreEqual(1, set.Adds);
        Assert.IsFalse(set.HasGauge);
        Assert.IsTrue(set.Log.Has(LogLevel.Debug, "Gauge: Display 2's taskbar was not seen (Hidden); its gauge is removed if that lasts 10 s."));
        Assert.IsTrue(set.Log.Has(LogLevel.Info, "Gauge: the gauge for Display 2 is removed (its display or taskbar is gone)."));
    }

    [TestMethod]
    public void ATaskbarThatComesBackIsGivenItsGaugeAtTheNextMainRead()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed >= Onset + Seconds(155));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);
        scene.At(Onset + Seconds(156), set.Reconcile);

        scene.Run(Seconds(300));

        Assert.AreEqual(2, set.Adds);
        Assert.AreEqual(Onset + Seconds(156), set.AddTimes[1]);
        Assert.AreEqual(1, set.Removals);
    }

    [TestMethod]
    public void ATaskbarThatComesBackIsGivenItsGaugeWhenTheDisplaysChange()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed >= Onset + Seconds(155));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);
        scene.At(Onset + Seconds(155), () => set.Set.OnDisplayChanged());

        scene.Run(Seconds(300));

        Assert.AreEqual(2, set.Adds);
        Assert.AreEqual(Onset + Seconds(155), set.AddTimes[1], "On the signal itself.");
    }

    [TestMethod]
    public void ATaskbarThatComesBackIsGivenItsGaugeWhenExplorerCreatesItsTaskbarAgain()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed >= Onset + Seconds(155));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);
        scene.At(Onset + Seconds(155), () => set.Set.OnTaskbarCreated());

        scene.Run(Seconds(300));

        Assert.AreEqual(2, set.Adds);
        Assert.AreEqual(Onset + Seconds(155), set.AddTimes[1]);
    }

    // With no read of the main taskbar and no signal, the minute's recheck finds it.
    [TestMethod]
    public void ATaskbarThatComesBackIsGivenItsGaugeWithinAMinuteWithNoReadAndNoSignal()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed >= Onset + Seconds(155));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);

        scene.Run(Seconds(400));

        Assert.AreEqual(2, set.Adds);
        Assert.IsLessThanOrEqualTo(Seconds(155) + GaugeStability.SetRecheckInterval, set.AddTimes[1] - Onset);
    }

    // The signal looks again at 2 s and 5 s: Explorer builds the new bar after the signal.
    [TestMethod]
    public void ASignalLooksAgainTwoAndFiveSecondsLaterForATaskbarExplorerBuildsLate()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed >= Onset + Seconds(4));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, () => set.Set.OnDisplayChanged());

        scene.Run(Seconds(110));

        Assert.AreEqual(1, set.Adds);
        Assert.AreEqual(Onset + Seconds(5), set.AddTimes[0], "Not seen at the signal or 2 s later; seen at 5 s.");
    }

    [TestMethod]
    public void ATaskbarThatReturnsInsideTheWindowKeepsItsGaugeAndSaysSo()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => elapsed < Onset || elapsed >= Onset + Seconds(3.5));
        scene.At(TimeSpan.Zero, set.Reconcile);
        scene.At(Onset, set.Reconcile);

        scene.Run(Seconds(150));

        Assert.AreEqual(0, set.Removals);
        Assert.AreEqual(1, set.Instances);
        Assert.AreEqual(1, set.Log.Lines.Count(e => e.Message.StartsWith("Gauge: Display 2's taskbar was not seen (Hidden)", StringComparison.Ordinal)));
        Assert.IsTrue(set.Log.Has(LogLevel.Debug, "Gauge: Display 2's taskbar is back after 5.0 s; its gauge stays."), string.Join(" | ", set.Log.Lines.Select(l => l.Message)));
    }

    // ----- Why a taskbar was not found -----

    [TestMethod]
    public void ExplainNamesTheFirstCauseThatAppliesAndNothingWhenOneIsFound()
    {
        DisplayInfo display = ScriptedSet.Two;
        var bounds = new Rectangle(1920, 1032, 1920, 48);

        Assert.AreEqual(TaskbarMissing.NoWindow, SecondaryTaskbarPicker.Explain([], display));
        Assert.AreEqual(TaskbarMissing.Hidden, SecondaryTaskbarPicker.Explain([new TaskbarWindowCandidate(1, bounds, false, display.Handle)], display));
        Assert.AreEqual(TaskbarMissing.OtherMonitor, SecondaryTaskbarPicker.Explain([new TaskbarWindowCandidate(1, bounds, true, display.Handle + 1)], display));
        Assert.AreEqual(TaskbarMissing.EmptyRect, SecondaryTaskbarPicker.Explain([new TaskbarWindowCandidate(1, Rectangle.Empty, true, display.Handle)], display));
        Assert.IsNull(SecondaryTaskbarPicker.Explain([new TaskbarWindowCandidate(1, bounds, true, display.Handle)], display), "Pick would find this one.");
        Assert.AreEqual(
            TaskbarMissing.Hidden,
            SecondaryTaskbarPicker.Explain(
                [new TaskbarWindowCandidate(2, bounds, true, display.Handle + 1), new TaskbarWindowCandidate(1, bounds, false, display.Handle)], display),
            "A hidden one on this monitor outranks a visible one on another.");
    }
}
