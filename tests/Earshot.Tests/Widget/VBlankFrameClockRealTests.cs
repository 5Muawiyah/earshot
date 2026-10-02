using System.Diagnostics;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// One execution of the real display clock: the real DXGI outputs and the real clock thread, delivering through a real UI message
// post to a thread that pumps messages. Every other test of the clock runs it on a fake display system (VBlankFrameClockTests), which
// proves its logic and not the boundary: that DXGI can be listed and waited on from the clock's own thread, and that a frame comes
// back to the UI thread. A machine with no output to wait on (a remote session, a runner with no display) is a legitimate answer
// too, but then the clock must say so with the raw HRESULT in the log and stamp its frames an hour ahead; it must never hang, or
// deliver nothing, or log nothing.
[TestClass]
public sealed class VBlankFrameClockRealTests
{
    public TestContext? TestContext { get; set; }

    // Whether DXGI lists an output for the primary display here, asked of the real outputs on a thread configured as the clock's is.
    private static bool DxgiListsAnOutput()
    {
        bool has = false;
        var outputs = new DxgiVBlankOutputs();
        var probe = new Thread(() =>
        {
            int listed = outputs.ListOutputs();
            has = listed >= 0 && outputs.Has(outputs.MonitorFor(0));
            outputs.Release();
        });
        outputs.ConfigureThread(probe);
        probe.Start();
        probe.Join();
        return has;
    }

    [TestMethod]
    public void TheRealClockDeliversFramesToTheUiThreadOrLogsWhyItCannotAndStillDoes()
    {
        // A local session with a screen must be paced by it; only a machine without one (a hosted runner, a remote session) may answer
        // with the unpaced fallback. Either way the outcome is recorded below.
        bool hostedRunner = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null || Environment.GetEnvironmentVariable("CI") is not null;
        bool outputListed = DxgiListsAnOutput();
        // Decided without the component under test: DXGI's own listing is only recorded. A local session is one that is not a hosted
        // runner, not a remote (terminal server) session, and has a screen.
        bool localSession = !hostedRunner && !SystemInformation.TerminalServerSession && Screen.AllScreens.Length > 0;
        bool mustBePaced = localSession;

        StaThread.Run(() =>
        {
            var ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            int uiThread = Environment.CurrentManagedThreadId;
            var log = new CapturingLog();
            var stamps = new List<TimeSpan>();
            var arrivals = new List<TimeSpan>();
            var wrongThread = new List<int>();

            using var clock = new VBlankFrameClock(() => 0, action => ui.Post(static state => ((Action)state!)(), action), log, new DxgiVBlankOutputs());
            using IDisposable subscription = clock.Subscribe(at =>
            {
                if (Environment.CurrentManagedThreadId != uiThread)
                {
                    wrongThread.Add(Environment.CurrentManagedThreadId);
                }

                stamps.Add(at);
                arrivals.Add(clock.Now);
            });

            // The wait is bounded, and not by the message pump returning: a clock that floods the UI thread with posts (a display that is
            // asleep makes WaitForVBlank return at once) would keep DoEvents from ever returning, so a timer thread stops the clock after
            // eight seconds, which ends the flood and lets the assertions below say what happened.
            using var stopper = new System.Threading.Timer(_ => clock.Dispose(), null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
            var timer = Stopwatch.StartNew();
            while (stamps.Count < 5 && timer.Elapsed < TimeSpan.FromSeconds(6))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }

            subscription.Dispose();
            string held = log.Entries.Count == 0 ? "empty" : string.Join(" | ", log.Entries.Select(e => e.Message));

            Assert.IsEmpty(wrongThread, "Every frame is delivered on the UI thread.");
            Assert.IsGreaterThanOrEqualTo(5, stamps.Count, "Frames arrive: " + stamps.Count + " in " + timer.Elapsed + ". The log held: " + held);

            // Each frame is paced (stamped with the time its blank returned: before it arrived and not long before) or unpaced (stamped an
            // hour ahead of its arrival so a motion ends in one: no output to wait on, a wait that failed, a display that is off). A machine
            // can give either, or paced and then not (a display going to sleep), so each frame is classified on its own. A blank is
            // at least a refresh after the last: two milliseconds is under the period of any display there is (500 Hz), so paced frames
            // closer than that were not blanks, which is what a sleeping display gives.
            int pacedFrames = 0;
            int unpacedFrames = 0;
            int tooClose = 0;
            TimeSpan? lastPaced = null;
            for (int i = 0; i < stamps.Count; i++)
            {
                TimeSpan ahead = stamps[i] - arrivals[i];
                if (ahead > TimeSpan.FromMinutes(59))
                {
                    unpacedFrames++;
                    continue;
                }

                pacedFrames++;
                Assert.IsLessThanOrEqualTo(TimeSpan.Zero, ahead, "A paced frame is stamped no later than it arrives.");
                Assert.IsLessThan(TimeSpan.FromSeconds(1), -ahead, "And not long before.");
                if (lastPaced is { } previous)
                {
                    Assert.IsGreaterThan(previous, stamps[i], "The paced stamps move forward, one blank at a time.");
                    if (stamps[i] - previous < TimeSpan.FromMilliseconds(2))
                    {
                        tooClose++;
                    }
                }

                lastPaced = stamps[i];
            }

            bool warned = log.Has(LogLevel.Warn, "Motion:");
            bool displayOff = log.Has(LogLevel.Warn, "probably off");
            string kind = unpacedFrames == 0 && tooClose == 0
                ? "paced by the display"
                : displayOff ? "unpaced because the display is off (the clock said so)" : unpacedFrames == stamps.Count ? "unpaced (no output to wait on)" : "mixed";
            string outcome = "Real display clock: " + kind + ", " + pacedFrames + " paced (" + tooClose + " closer than 2 ms) and " + unpacedFrames + " unpaced of "
                + stamps.Count + " frames in " + timer.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms; DXGI lists an output: " + outputListed + "; local session: " + localSession + "; hosted runner: " + hostedRunner + "; log: " + held;
            RecordOutcome(outcome);

            // No more than the two short waits the clock lets through before it calls the display off count as paced.
            Assert.IsLessThanOrEqualTo(2, tooClose, "Paced frames closer together than a refresh are waits that returned at once, and the clock must not call them blanks. " + outcome);
            if (tooClose > 0 || unpacedFrames > 0 && mustBePaced)
            {
                Assert.IsTrue(displayOff || !mustBePaced, "A local session is paced by its display unless the clock said the display is off. " + outcome);
            }

            if (tooClose > 0)
            {
                Assert.IsTrue(displayOff, "Short waits come with the clock saying the display is probably off. " + outcome);
            }

            if (unpacedFrames > 0)
            {
                Assert.IsTrue(warned, "An unpaced frame comes with the clock saying why in the log (" + pacedFrames + " paced, " + unpacedFrames + " not). " + outcome);
            }

            if (warned && !displayOff && !log.Has(LogLevel.Warn, "has not returned"))
            {
                Assert.IsTrue(log.Has(LogLevel.Warn, "HRESULT 0x"), "A failure to wait is logged with its raw code. " + outcome);
            }

            // At rest nothing is posted: a frame in flight when the last subscriber left may still arrive, and then no more.
            Application.DoEvents();
            int atRest = stamps.Count;
            Thread.Sleep(100);
            Application.DoEvents();
            Assert.AreEqual(atRest, stamps.Count, "No frames after the subscription ended.");
        });
    }

    // The outcome goes to the test output and, where the gate gives a data folder (check.ps1 does, inside its log folder), to a file
    // there, so the gate's logs record whether display pacing ran on the machine.
    private void RecordOutcome(string outcome)
    {
        Console.WriteLine(outcome);
        TestContext?.WriteLine(outcome);
        string? root = Environment.GetEnvironmentVariable("EARSHOT_DATA_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "real-vblank-clock-outcome.txt"), outcome + Environment.NewLine);
        }
    }
}
