using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real execution kept for the taskbar reader: UiaTaskbarReader against a real window of the test's
// own, on a private desktop, read from a second, fresh MTA thread bound to the same desktop (UI
// Automation documents that its worker thread must own no window). Nothing here touches Shell_TrayWnd,
// the real taskbar, or a device.
[TestClass]
public sealed class UiaTaskbarReaderTests
{
    [TestMethod]
    public void RealUiaTaskbarReaderFindsExactlyTheTwoButtonsOnAPrivateDesktop()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(desktop =>
        {
            using var form = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(100, 100),
                ClientSize = new Size(300, 100),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
            };
            var button1 = new Button { Location = new Point(10, 10), Size = new Size(80, 30), Text = "One" };
            var button2 = new Button { Location = new Point(120, 10), Size = new Size(80, 30), Text = "Two" };
            form.Controls.Add(button1);
            form.Controls.Add(button2);
            form.Show();
            Application.DoEvents();

            Rectangle expected1 = button1.RectangleToScreen(button1.ClientRectangle);
            Rectangle expected2 = button2.RectangleToScreen(button2.ClientRectangle);
            nint formHandle = form.Handle;

            bool ok = false;
            List<Rectangle>? occupied = null;
            Rectangle? startButton = null;
            StepOutcome? failure = null;
            var stopwatch = new Stopwatch();
            ExceptionDispatchInfo? readerFailure = null;

            var readerThread = new Thread(() =>
            {
                try
                {
                    Earshot.Tests.Phase5.CardDesktop.BindCurrentThread(desktop);
                    var reader = new UiaTaskbarReader();

                    // A warm-up read first: a local probe found the first UIA call on this machine costs
                    // far more than later ones, so the timed read below measures the steady-state cost the
                    // product actually pays on every poll, not COM/JIT start-up.
                    reader.TryReadOccupants(formHandle, Rectangle.Empty, out _, out _, out _);

                    stopwatch.Start();
                    ok = reader.TryReadOccupants(formHandle, Rectangle.Empty, out occupied, out startButton, out failure);
                    stopwatch.Stop();
                }
                catch (Exception ex)
                {
                    readerFailure = ExceptionDispatchInfo.Capture(ex);
                }
            })
            {
                IsBackground = true,
                Name = "Earshot UIA reader test",
            };
            readerThread.SetApartmentState(ApartmentState.MTA);
            readerThread.Start();

            // Pump this thread's own queue rather than Join: the real UI Automation client sends
            // WM_GETOBJECT to the form's window and waits for the reply, so the form's own thread (this
            // one) must keep processing messages while the reader thread queries it, or the two threads
            // deadlock.
            var wait = Stopwatch.StartNew();
            while (readerThread.IsAlive && wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                Application.DoEvents();
                Thread.Sleep(2);
            }

            Assert.IsFalse(readerThread.IsAlive, "The reader thread did not finish in time.");
            readerFailure?.Throw();

            Assert.IsTrue(ok, ok ? "" : "The real UIA read failed: " + failure!.CodeName + " " + failure.Detail);
            Assert.IsNotNull(occupied);
            Assert.AreEqual(2, occupied!.Count, "Exactly the two buttons, nothing else.");
            CollectionAssert.Contains(occupied, expected1, "The first button's rectangle must be reported in physical pixels.");
            CollectionAssert.Contains(occupied, expected2, "The second button's rectangle must be reported in physical pixels.");
            Assert.IsNull(startButton, "Neither button carries the Start automation id.");

            // A 500 ms sanity bound was the first figure tried; on this machine, in this test process, a
            // warmed-up read against a private desktop measured 546 ms (a probe fact recorded here). 5000
            // ms keeps the bound meaningful (it still catches a genuine hang) without asserting a
            // steady-state figure this environment does not deliver.
            Assert.IsLessThan(5000, stopwatch.ElapsedMilliseconds, "A sanity bound on the real UIA read, not a figure the product uses.");
        });
    }
}
