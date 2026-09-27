using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real execution kept for the taskbar reader. A private-desktop run (a Form of the test's own, read
// from a second MTA thread bound to the same desktop) proved correct in isolation, but was flaky when run
// in the same process after GaugeWindowTests: closing the private desktop intermittently failed with
// ERROR_BUSY (170), reproducibly, even after forcing a GC and retrying for several seconds. Rather than
// ship a test whose own cleanup is unreliable, this uses the fallback the design allows for: the same
// real UIA read, read-only, against Shell_TrayWnd on the default desktop. Nothing here shows a window,
// clicks anything, or touches a device; it only asks UI Automation what is on the real taskbar.
[TestClass]
public sealed class UiaTaskbarReaderTests
{
    private const string ShellTrayWndClass = "Shell_TrayWnd";

    [TestMethod]
    public void RealUiaTaskbarReaderReadsTheRealTaskbarReadOnly()
    {
        nint trayHandle = FindWindowW(ShellTrayWndClass, null);
        if (trayHandle == 0)
        {
            Assert.Inconclusive("No Shell_TrayWnd on this machine (a hosted runner with no taskbar): skipped, as the design allows.");
            return;
        }

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
                var reader = new UiaTaskbarReader();

                // A warm-up read first: a local probe found the first UIA call on this machine costs far
                // more than later ones, so the timed read below measures the steady-state cost.
                reader.TryReadOccupants(trayHandle, Rectangle.Empty, out _, out _, out _);

                stopwatch.Start();
                ok = reader.TryReadOccupants(trayHandle, Rectangle.Empty, out occupied, out startButton, out failure);
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
        Assert.IsTrue(readerThread.Join(TimeSpan.FromSeconds(15)), "The reader thread did not finish in time.");

        readerFailure?.Throw();
        Assert.IsTrue(ok, ok ? "" : "The real UIA read failed: " + failure!.CodeName + " " + failure.Detail);
        Assert.IsNotNull(occupied);

        // The real taskbar always has at least the notification area's buttons; an empty result would
        // mean the reader found nothing at all, which is the failure this test exists to catch.
        Assert.IsGreaterThan(0, occupied!.Count, "The real taskbar must report at least one occupant.");
        Assert.IsLessThan(5000, stopwatch.ElapsedMilliseconds, "A sanity bound on the real UIA read, not a figure the product uses.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowW(string lpClassName, string? lpWindowName);
}
