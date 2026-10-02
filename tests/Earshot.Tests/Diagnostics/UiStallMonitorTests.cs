using System.Runtime.InteropServices;
using Earshot.Diagnostics;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Diagnostics;

// The frame and UI stall log, on a fake clock.
[TestClass]
public sealed class UiStallMonitorTests
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(16.7);

    private static UiStallMonitor Monitor(TestTimeProvider time) => new(time, Refresh, "fallback 16.7 ms (test)");

    private static string[] Entries(UiStallMonitor monitor) => [.. monitor.Lines().Skip(1)];

    [TestMethod]
    public void TheThresholdIsTwoRefreshIntervalsAndTheHeaderSaysWhereTheIntervalCameFrom()
    {
        var monitor = Monitor(new TestTimeProvider());

        Assert.AreEqual(Refresh * 2, monitor.Threshold);
        StringAssert.Contains(monitor.Lines()[0], "2 x refresh interval 16.7 ms");
        StringAssert.Contains(monitor.Lines()[0], "fallback 16.7 ms (test)");
    }

    [TestMethod]
    public void AnActionThatRunsWithinTheThresholdIsNotRecorded()
    {
        var time = new TestTimeProvider();
        UiStallMonitor monitor = Monitor(time);

        monitor.Wrap(() => time.Advance(TimeSpan.FromMilliseconds(33)))();

        Assert.IsEmpty(Entries(monitor));
    }

    [TestMethod]
    public void AnActionThatRunsLongerThanTheThresholdIsRecordedWithItsMethodName()
    {
        var time = new TestTimeProvider();
        UiStallMonitor monitor = Monitor(time);

        monitor.Wrap(SlowAction(time))();

        string[] entries = Entries(monitor);
        Assert.HasCount(1, entries);
        StringAssert.Contains(entries[0], "UI post ran 50.0 ms");
        StringAssert.Contains(entries[0], "UiStallMonitorTests.");
    }

    [TestMethod]
    public void AnActionThatWaitsInTheQueueLongerThanTheThresholdIsRecorded()
    {
        var time = new TestTimeProvider();
        UiStallMonitor monitor = Monitor(time);
        Action posted = monitor.Wrap(static () => { });

        time.Advance(TimeSpan.FromMilliseconds(40));
        posted();

        string[] entries = Entries(monitor);
        Assert.HasCount(1, entries);
        StringAssert.Contains(entries[0], "waited 40.0 ms in the queue");
    }

    [TestMethod]
    public void AnActionThatThrowsIsStillTimedAndTheExceptionPropagates()
    {
        var time = new TestTimeProvider();
        UiStallMonitor monitor = Monitor(time);

        Assert.ThrowsExactly<InvalidOperationException>(monitor.Wrap(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(60));
            throw new InvalidOperationException();
        }));

        Assert.HasCount(1, Entries(monitor));
    }

    [TestMethod]
    public void TheCardsWindowCallsAreRecordedBesideTheStalls()
    {
        var time = new TestTimeProvider();
        UiStallMonitor monitor = Monitor(time);

        monitor.RecordCardCall(new CardWindowCall(CardWindowCallKind.Invalidate, new System.Drawing.Rectangle(1, 2, 30, 40), "invalidate"));

        string[] entries = Entries(monitor);
        Assert.HasCount(1, entries);
        StringAssert.Contains(entries[0], "card Invalidate invalidate 1,2 30x40");
    }

    [TestMethod]
    public void TheRingKeeps256EntriesAndDropsTheOldestFirst()
    {
        var ring = new FrameLogRing(FrameLogRing.DefaultCapacity);
        Assert.AreEqual(256, ring.Capacity);

        for (int i = 0; i < 300; i++)
        {
            ring.Add("line " + i);
        }

        IReadOnlyList<string> lines = ring.Snapshot();
        Assert.HasCount(256, lines);
        Assert.AreEqual("line 44", lines[0]);
        Assert.AreEqual("line 299", lines[255]);
    }

    [TestMethod]
    public void ARingNotYetFullKeepsWhatItHasInOrder()
    {
        var ring = new FrameLogRing(4);
        ring.Add("a");
        ring.Add("b");

        CollectionAssert.AreEqual(new List<string> { "a", "b" }, ring.Snapshot().ToList());
    }

    [TestMethod]
    public void TheMonitorIsAFrameLogSourceTheDiagnosticsTextCanPrint()
    {
        var time = new TestTimeProvider();
        UiStallMonitor monitor = Monitor(time);
        monitor.Wrap(SlowAction(time))();

        string text = DiagnosticsText.Build("1.4.0", "Windows", ["a log line"], monitor, userName: "someone");

        StringAssert.Contains(text, "UI post ran");
    }

    private static Action SlowAction(TestTimeProvider time) => () => time.Advance(TimeSpan.FromMilliseconds(50));

    // DWM_TIMING_INFO is declared between pshpack1.h and poppack.h in dwmapi.h, so its fields are packed to one byte, and
    // cbSize must be the size that packing gives or DwmGetCompositionTimingInfo refuses the call. The size is worked out here
    // from the documented field list, not from the struct under test.
    // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_timing_info
    [TestMethod]
    public void TheTimingInfoIsTheSizeOfTheDocumentedFieldListPackedToOneByte()
    {
        // UINT32 fields: cbSize (1); rateRefresh and rateCompose, each an UNSIGNED_RATIO of two UINT32 (2 x 2 = 4); and the six
        // UINT counters cDXRefresh, cDXPresent, cDXPresentSubmitted, cDXPresentConfirmed, cDXRefreshConfirmed, cFramesOutstanding.
        const int Uint32Fields = 1 + (2 * 2) + 6;

        // UINT64 fields (QPC_TIME and DWM_FRAME_COUNT are both unsigned 64 bit): qpcRefreshPeriod, qpcVBlank, cRefresh,
        // qpcCompose, cFrame, cRefreshFrame, cFrameSubmitted, cFrameConfirmed, cRefreshConfirmed, cFramesLate, cFrameDisplayed,
        // qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete, cFramePending, qpcFramePending,
        // cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed,
        // cRefreshNextDisplayed, cRefreshNextPresented, cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted,
        // cPixelsReceived, cPixelsDrawn, cBuffersEmpty: thirty one.
        const int Uint64Fields = 31;

        // 11 x 4 + 31 x 8 = 44 + 248 = 292 bytes.
        const int PackedSize = (Uint32Fields * 4) + (Uint64Fields * 8);

        Assert.AreEqual(PackedSize, Marshal.SizeOf<RefreshInterval.DwmTimingInfo>());
    }
}
