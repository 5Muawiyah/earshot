using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// An hour of the taskbar behaving the way the owner's logs showed it behaving, replayed on a fake clock, and what the gauge
// did with it. In those logs the gauge on Display 2 was hidden 1,373 times and removed 325 times in 47 hours, though nothing the
// owner could see had changed: the room verdict flipped with the gauge's own visibility, one enumeration that missed a taskbar
// removed its gauge, and the notification area's 28 px of growth moved the gauge back and forth.
//
// Each replay counts what the gauge did. The counts are the point: a replay that only asked "is it shown at the end" would pass
// on a gauge that hid and showed itself every twenty seconds.
[TestClass]
public sealed class GaugeStabilityReplayTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(20);

    // ----- R1: no room, then room, on every read -----

    // The verdict alternates read by read, 20 s apart, as it did when the gauge was reading itself as an occupant.
    [TestMethod]
    public void ARoomVerdictThatFlipsOnEveryReadNeverHidesTheGaugeInAnHour()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read =>
            ITaskbarReader.Result.Ok(read.Index % 2 == 0 ? StabilityLayouts.Free() : StabilityLayouts.NoRoom()));
        gauge.Poll(Poll);

        scene.Run(Hour);

        Console.WriteLine("R1 hides=" + gauge.Hides + " shows=" + gauge.Shows + " moves=" + gauge.Moves + " reads=" + gauge.Reads);
        Assert.AreEqual(0, gauge.Hides, "R1 hides=" + gauge.Hides + " shows=" + gauge.Shows + " moves=" + gauge.Moves + " reads=" + gauge.Reads);
        Assert.AreEqual(0, gauge.Moves, "R1 moves=" + gauge.Moves);
        Assert.AreEqual(1, gauge.Shows, "R1 shows=" + gauge.Shows);
        Assert.IsTrue(gauge.IsShown);
    }

    // ----- R2: the real filter, with an element of this process at the gauge's bounds while it is shown -----

    // The taskbar as UI Automation lists it, which includes the gauge's own window while it is shown (the gauge is the taskbar's
    // owned window). Read through the reader's own element filter, so the test is about that filter and not about a layout built to
    // look like its output.
    [TestMethod]
    public void ThePresenceOfTheGaugesOwnElementNeverHidesTheGaugeInAnHour()
    {
        const int Explorer = 4242;
        int own = Environment.ProcessId;
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read =>
        {
            var elements = new List<IUIAutomationElement?> { new FakeUiaElement(StabilityLayouts.Start, automationId: "StartButton", processId: Explorer) };
            for (int i = 0; i < 8; i++)
            {
                elements.Add(new FakeUiaElement(new Rectangle(807 + (i * 44), 1032, 44, 48), className: "Taskbar.TaskListButtonAutomationPeer", processId: Explorer));
            }

            elements.Add(new FakeUiaElement(new Rectangle(1678, 1032, 242, 48), className: "SystemTray.OmniButton", processId: Explorer));
            if (read.GaugeBounds is { } bounds)
            {
                elements.Add(new FakeUiaElement(bounds, className: "WindowsForms10.Window.8.app.0.1_r1_ad1", processId: own));
            }

            bool ok = GaugeStabilityApi.ReadElements(
                new FakeUiaElementArray([.. elements]), StabilityLayouts.Bar, own, out List<Rectangle> occupied, out Rectangle? area, out StepOutcome? failure);
            Assert.IsTrue(ok, failure?.CodeName);
            return ITaskbarReader.Result.Ok(StabilityLayouts.From(occupied, area));
        });
        gauge.Poll(Poll);

        scene.Run(Hour);

        Console.WriteLine("R2 hides=" + gauge.Hides + " shows=" + gauge.Shows + " moves=" + gauge.Moves + " reads=" + gauge.Reads);
        Assert.AreEqual(0, gauge.Hides, "R2 hides=" + gauge.Hides + " shows=" + gauge.Shows + " moves=" + gauge.Moves + " reads=" + gauge.Reads);
        Assert.AreEqual(1, gauge.Shows, "R2 shows=" + gauge.Shows);
        Assert.IsTrue(gauge.IsShown);
    }

    // ----- R3: a secondary taskbar missing for one enumeration -----

    // Every 25 s the taskbar window is hidden for 2.5 s, and the tray reads the main taskbar (and so matches the set to the
    // displays) at the start of that gap, a quarter second in and at 1.5 s, as well as every 10 s. The longest absence the logs
    // showed apart from the one real one was under 3 s.
    [TestMethod]
    public void ATaskbarMissingForTwoAndAHalfSecondsEveryTwentyFiveNeverRemovesItsGaugeInAnHour()
    {
        var scene = new StabilityScene();
        using var set = new ScriptedSet(scene, elapsed => (elapsed.TotalSeconds % 25) >= 2.5);
        scene.Every(TimeSpan.FromSeconds(10), TimeSpan.Zero, set.Reconcile);
        for (int cycle = 0; cycle * 25 < Hour.TotalSeconds; cycle++)
        {
            foreach (double offset in new[] { 0, 0.25, 1.5 })
            {
                scene.At(TimeSpan.FromSeconds((cycle * 25) + offset), set.Reconcile);
            }
        }

        scene.Run(Hour);

        Console.WriteLine("R3 adds=" + set.Adds + " removals=" + set.Removals + " instances=" + set.Instances + " reconciles=" + set.Reconciles);
        Assert.AreEqual(0, set.Removals, "R3 adds=" + set.Adds + " removals=" + set.Removals + " instances=" + set.Instances + " reconciles=" + set.Reconciles);
        Assert.AreEqual(1, set.Adds, "R3 adds=" + set.Adds);
        Assert.AreEqual(1, set.Instances, "The same gauge all hour. R3 instances=" + set.Instances);
        Assert.IsTrue(set.HasGauge);
    }

    // ----- R4: the notification area's 28 px -----

    // The notification area is 28 px wider for a few seconds at a time (a full-screen window exists and then does not), in runs of
    // 1.4, 2.0 and 4.5 s, read every quarter second as the taskbar's own change events ask for reads; then, for the rest of the
    // hour, once in a while one read at the wider size.
    [TestMethod]
    public void AShortLivedChangeOfThe28PixelsNeverMovesTheGaugeInAnHour()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read => ITaskbarReader.Result.Ok(StabilityLayouts.Free(NotificationXAt(read.Elapsed))));
        gauge.Poll(Poll);
        for (double second = 0.25; second < 600; second += 0.25)
        {
            gauge.ReadAt([TimeSpan.FromSeconds(second)]);
        }

        scene.Run(Hour);

        Console.WriteLine("R4 moves=" + gauge.Moves + " hides=" + gauge.Hides + " reads=" + gauge.Reads);
        Assert.AreEqual(0, gauge.Moves, "R4 moves=" + gauge.Moves + " hides=" + gauge.Hides + " reads=" + gauge.Reads);
        Assert.AreEqual(0, gauge.Hides, "R4 hides=" + gauge.Hides);
        Assert.AreEqual(StabilityLayouts.GaugeAt(1678), gauge.Bounds);
    }

    // The notification area's left edge at this time. 30 s cycles for the first ten minutes: wide for 1.4 s, narrow for 2.0 s,
    // wide for 1.4 s, narrow for 4.5 s, and narrow the rest of the time. After that it is wide for the first second after each
    // 20 s poll, which a poll sees as one read.
    private static int NotificationXAt(TimeSpan elapsed)
    {
        const int Narrow = 1678;
        const int Wide = 1650;
        double t = elapsed.TotalSeconds;
        if (t < 600)
        {
            double c = t % 30;
            bool wide = c is >= 10 and < 11.4 || c is >= 13.4 and < 14.8 || c is >= 18 and < 22.5;
            return wide ? Wide : Narrow;
        }

        return (t % 20) < 1 ? Wide : Narrow;
    }

    // ----- R5: all of it together, with a full-screen window every five minutes -----

    // The three patterns at once on the same clock, with a full-screen window opening every five minutes on the gauge's display.
    // The gauge may hide for those and for nothing else, and each hide must come on the call that told it.
    [TestMethod]
    public void AllThreePatternsTogetherHideTheGaugeOnlyForTheFullScreenNoticesAndOnTheirCall()
    {
        var scene = new StabilityScene();
        var gauge = new ScriptedGauge(scene, read =>
        {
            // The first minute of every five is clean, so the gauge is back after a notice before the patterns start again.
            double c = read.Elapsed.TotalSeconds % 300;
            bool patterns = c is >= 60 and < 290;
            int x = patterns ? NotificationXAt(read.Elapsed) : 1678;
            bool noRoom = patterns && read.Index % 2 == 1;
            return ITaskbarReader.Result.Ok(noRoom ? StabilityLayouts.NoRoom(x) : StabilityLayouts.Free(x));
        });
        gauge.Poll(Poll);
        for (double second = 0.25; second < 600; second += 0.25)
        {
            if (second % 300 is >= 60 and < 290)
            {
                gauge.ReadAt([TimeSpan.FromSeconds(second)]);
            }
        }

        using var set = new ScriptedSet(scene, elapsed => (elapsed.TotalSeconds % 25) >= 2.5);
        scene.Every(TimeSpan.FromSeconds(10), TimeSpan.Zero, set.Reconcile);
        for (int cycle = 0; cycle * 25 < Hour.TotalSeconds; cycle++)
        {
            foreach (double offset in new[] { 0, 0.25, 1.5 })
            {
                scene.At(TimeSpan.FromSeconds((cycle * 25) + offset), set.Reconcile);
            }
        }

        var hiddenOnNotice = new List<bool>();
        int notices = 0;
        for (int minute = 5; minute < 60; minute += 5)
        {
            scene.At(TimeSpan.FromMinutes(minute), () =>
            {
                notices++;
                int before = gauge.Hides;
                gauge.Apply(() => gauge.Controller.NotifyFullScreenApp(opening: true));
                hiddenOnNotice.Add(gauge.Hides == before + 1 && gauge.Controller.State is GaugeState.Hidden { Reason: HiddenReason.FullScreenNotified });
            });
            scene.At(TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(3), () => gauge.Apply(() => gauge.Controller.NotifyFullScreenApp(opening: false)));
        }

        scene.Run(Hour);

        string counts = "R5 notices=" + notices + " hides=" + gauge.Hides + " moves=" + gauge.Moves + " shows=" + gauge.Shows +
            " removals=" + set.Removals + " adds=" + set.Adds + " instances=" + set.Instances;
        Console.WriteLine(counts);
        Assert.AreEqual(notices, gauge.Hides, counts);
        Assert.IsTrue(hiddenOnNotice.All(h => h), "Every notice hid the gauge on its own call. " + counts);
        Assert.AreEqual(0, gauge.Moves, counts);
        Assert.AreEqual(0, set.Removals, counts);
        Assert.AreEqual(1, set.Instances, counts);
    }
}
