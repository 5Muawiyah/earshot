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
        // A local session with a display must be paced by it; only a machine with no output to wait on (a hosted runner, a remote
        // session) may answer with the unpaced fallback. Either way the outcome is recorded below.
        bool hostedRunner = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null || Environment.GetEnvironmentVariable("CI") is not null;
        bool outputListed = DxgiListsAnOutput();
        bool mustBePaced = !hostedRunner && outputListed;

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

            var timer = Stopwatch.StartNew();
            while (stamps.Count < 5 && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }

            subscription.Dispose();

            Assert.IsEmpty(wrongThread, "Every frame is delivered on the UI thread.");
            Assert.IsGreaterThanOrEqualTo(5, stamps.Count, "Frames arrive: " + stamps.Count + " in " + timer.Elapsed + ". The log held: " + string.Join(" | ", log.Entries.Select(e => e.Message)));

            // Each frame is one of two things. Paced: stamped with the time its blank returned, which is before it arrived and not long
            // before. Unpaced (no output to wait on, or a wait that failed): stamped an hour ahead of its arrival so a motion ends in one.
            // A machine can give either, or paced frames and then a failure (a display going away), so each frame is classified on its own.
            int pacedFrames = 0;
            int unpacedFrames = 0;
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
                }

                lastPaced = stamps[i];
            }

            bool warned = log.Has(LogLevel.Warn, "Motion:");
            string outcome = "Real display clock: " + (pacedFrames == stamps.Count ? "paced by the display" : unpacedFrames == stamps.Count ? "unpaced (no output to wait on)" : "mixed")
                + ", " + pacedFrames + " paced and " + unpacedFrames + " unpaced of " + stamps.Count + " frames in " + timer.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms; DXGI lists an output: " + outputListed + "; hosted runner: " + hostedRunner + "; log: "
                + (log.Entries.Count == 0 ? "empty" : string.Join(" | ", log.Entries.Select(e => e.Message)));
            RecordOutcome(outcome);
            if (mustBePaced)
            {
                Assert.AreEqual(stamps.Count, pacedFrames, "A local session with a DXGI output is paced by it. " + outcome);
                Assert.IsFalse(warned, "And logs nothing. " + outcome);
            }

            if (unpacedFrames > 0)
            {
                Assert.IsTrue(warned, "An unpaced frame comes with the clock saying why in the log (" + pacedFrames + " paced, " + unpacedFrames + " not).");
            }

            if (warned)
            {
                Assert.IsTrue(log.Has(LogLevel.Warn, "HRESULT 0x"), "The failure is logged with its raw code.");
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
