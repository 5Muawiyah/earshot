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
    [TestMethod]
    public void TheRealClockDeliversFramesToTheUiThreadOrLogsWhyItCannotAndStillDoes()
    {
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

            bool unpaced = log.Has(LogLevel.Warn, "Motion:");
            if (unpaced)
            {
                // No output to wait on here: the raw HRESULT is in the log, and each frame is stamped an hour ahead so a motion ends in one.
                Assert.IsTrue(log.Has(LogLevel.Warn, "HRESULT 0x"), "The failure is logged with its raw code.");
                for (int i = 0; i < stamps.Count; i++)
                {
                    Assert.IsGreaterThan(TimeSpan.FromMinutes(59), stamps[i] - arrivals[i], "An unpaced frame is stamped an hour ahead.");
                }
            }
            else
            {
                // Paced by the display: each frame is stamped with the time its blank returned, which is before it arrived and not long
                // before, and the stamps only go forward.
                for (int i = 0; i < stamps.Count; i++)
                {
                    Assert.IsLessThanOrEqualTo(arrivals[i], stamps[i], "A frame is stamped no later than it arrives.");
                    Assert.IsLessThan(TimeSpan.FromSeconds(1), arrivals[i] - stamps[i], "And not long before.");
                    if (i > 0)
                    {
                        Assert.IsGreaterThan(stamps[i - 1], stamps[i], "The stamps move forward, one blank at a time.");
                    }
                }
            }

            // At rest nothing is posted: a frame in flight when the last subscriber left may still arrive, and then no more.
            Application.DoEvents();
            int atRest = stamps.Count;
            Thread.Sleep(100);
            Application.DoEvents();
            Assert.AreEqual(atRest, stamps.Count, "No frames after the subscription ended.");
        });
    }
}
