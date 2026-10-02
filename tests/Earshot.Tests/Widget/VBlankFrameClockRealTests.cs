using System.Diagnostics;
using System.Runtime.InteropServices;
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
        // Whether the display is on, decided without the clock under test: read-only, from the time since the last keyboard or mouse
        // input in this session. Input within the last minute means someone is at the machine and its display is on (the shortest
        // display-off timeout Windows offers is one minute). Read again after the run, so input stopping during it does not count.
        TimeSpan? idleBefore = localSession ? InputIdle.Read() : null;

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
            // at least a refresh after the last: the product's own floor (MinBlankSpacing) is half a millisecond, the period of a 2000 Hz
            // display, so paced frames closer than that are not blanks of any display the clock could be asked to pace, which is what a
            // sleeping display gives. The floor is the product's figure, not a claim about what monitors exist.
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
                    if (stamps[i] - previous < MinBlankSpacing)
                    {
                        tooClose++;
                    }
                }

                lastPaced = stamps[i];
            }

            TimeSpan? idleAfter = localSession ? InputIdle.Read() : null;
            bool displayKnownOn = localSession && idleBefore is { } before && idleAfter is { } after && before < InputIdle.DisplayOnWithin && after < InputIdle.DisplayOnWithin;
            string displayCheck = idleBefore is null || idleAfter is null
                ? "unknown (no input time for this session)"
                : "last input " + idleBefore.Value.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " s before the run and "
                    + idleAfter.Value.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " s at its end ("
                    + (displayKnownOn ? "on" : "not within " + InputIdle.DisplayOnWithin.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " s, so unknown") + ")";
            var seen = new Seen(
                pacedFrames, unpacedFrames, tooClose, stamps.Count,
                Warned: log.Has(LogLevel.Warn, "Motion:"),
                ClockSaidOff: log.Has(LogLevel.Warn, "probably off"),
                ClockSaidStuck: log.Has(LogLevel.Warn, "has not returned"),
                ClockLoggedRawCode: log.Has(LogLevel.Warn, "HRESULT 0x"),
                LogEmpty: log.Entries.Count == 0);
            string kind = unpacedFrames == 0 && tooClose == 0
                ? "paced by the display"
                : seen.ClockSaidOff ? "unpaced because the clock says the display is off" : unpacedFrames == stamps.Count ? "unpaced (no output to wait on)" : "mixed";
            string outcome = "Real display clock: " + kind + ", " + pacedFrames + " paced (" + tooClose + " closer than " + MinBlankSpacing.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms) and " + unpacedFrames + " unpaced of "
                + stamps.Count + " frames in " + timer.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms; DXGI lists an output: " + outputListed + "; local session: " + localSession + "; hosted runner: " + hostedRunner
                + "; display check: " + displayCheck + "; branch: " + (displayKnownOn ? "display known on, so the clock must be paced with an empty log" : "display not known on, so the clock's own account is accepted")
                + "; log: " + held;
            RecordOutcome(outcome);

            string? failure = Judge(seen, mustBePaced, displayKnownOn);
            Assert.IsNull(failure, failure + " " + outcome);

            // At rest nothing is posted: a frame in flight when the last subscriber left may still arrive, and then no more.
            Application.DoEvents();
            int atRest = stamps.Count;
            Thread.Sleep(100);
            Application.DoEvents();
            Assert.AreEqual(atRest, stamps.Count, "No frames after the subscription ended.");
        });
    }

    // The least spacing between two paced stamps that is a real blank: the product's own floor for a wait that counts as one (the
    // period of a display at 2000 Hz).
    internal static readonly TimeSpan MinBlankSpacing = VBlankFrameClock.DefaultMinRealWait;

    // The most short waits the clock lets through before it calls the display off.
    private const int ShortWaitsAllowed = 2;

    // What one run of the real clock showed.
    internal sealed record Seen(
        int Paced, int Unpaced, int TooClose, int Frames,
        bool Warned, bool ClockSaidOff, bool ClockSaidStuck, bool ClockLoggedRawCode, bool LogEmpty);

    // The verdict on a run, or null when it passes. displayKnownOn is decided by the caller without the clock: when the display is known
    // to be on, nothing short of a paced run with nothing logged is acceptable, and the clock's own "probably off" is no excuse (a
    // detector that misfired on an awake display would otherwise pass). Only when the display is not known to be on may the clock's
    // account of an unpaced run be believed.
    internal static string? Judge(Seen seen, bool mustBePaced, bool displayKnownOn)
    {
        if (displayKnownOn)
        {
            // Not the clock's account but the display's: paced, every frame, with nothing logged.
            return seen.Unpaced > 0 || seen.TooClose > 0 || !seen.LogEmpty
                ? "The display is on (input within the last minute), so the clock must be paced by it with an empty log, whatever it claims: "
                    + seen.Unpaced + " unpaced, " + seen.TooClose + " too close, log empty: " + seen.LogEmpty + ", clock said off: " + seen.ClockSaidOff + "."
                : null;
        }

        if (seen.TooClose > ShortWaitsAllowed)
        {
            return "Paced frames closer together than a refresh are waits that returned at once, and the clock must not call them blanks.";
        }

        if (mustBePaced && (seen.TooClose > 0 || seen.Unpaced > 0) && !seen.ClockSaidOff)
        {
            return "A local session is paced by its display unless the clock said the display is off.";
        }

        if (seen.TooClose > 0 && !seen.ClockSaidOff)
        {
            return "Short waits come with the clock saying the display is probably off.";
        }

        if (seen.Unpaced > 0 && !seen.Warned)
        {
            return "An unpaced frame comes with the clock saying why in the log (" + seen.Paced + " paced, " + seen.Unpaced + " not).";
        }

        if (seen.Warned && !seen.ClockSaidOff && !seen.ClockSaidStuck && !seen.ClockLoggedRawCode)
        {
            return "A failure to wait is logged with its raw code.";
        }

        return null;
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

// Time since the last keyboard or mouse input in this session, read only. GetLastInputInfo counts input for the whole session, so
// it needs no window and works from a test thread. https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo
internal static class InputIdle
{
    // Input this recent means the display is on: the shortest display-off timeout Windows offers is one minute.
    internal static readonly TimeSpan DisplayOnWithin = TimeSpan.FromSeconds(60);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    // Null when Windows gives no input time. The input time and the tick count are both milliseconds since boot, wrapping at 2^32.
    internal static TimeSpan? Read()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        uint sinceMs = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(sinceMs);
    }
}

// The verdict on a run of the real clock is itself held to account: the clock's own "probably off" must not excuse a run when the
// display is independently known to be on.
[TestClass]
public sealed class VBlankFrameClockRealJudgeTests
{
    private static VBlankFrameClockRealTests.Seen Run(int paced = 5, int unpaced = 0, int tooClose = 0, bool warned = false, bool saidOff = false, bool saidStuck = false, bool rawCode = false) =>
        new(paced, unpaced, tooClose, paced + unpaced, warned, saidOff, saidStuck, rawCode, LogEmpty: !warned);

    [TestMethod]
    public void AClockThatCallsAnAwakeDisplayOffIsNotBelievedWhenUnpaced()
    {
        // The misfire: the display is known on, yet frames came unpaced with the clock's own "probably off".
        string? failure = VBlankFrameClockRealTests.Judge(Run(paced: 1, unpaced: 5, warned: true, saidOff: true), mustBePaced: true, displayKnownOn: true);
        Assert.IsNotNull(failure, "A clock that claims the display is off while it is on must fail the test.");
    }

    [TestMethod]
    public void AClockThatCallsAnAwakeDisplayOffIsNotBelievedWhenWaitsAreShort()
    {
        string? failure = VBlankFrameClockRealTests.Judge(Run(tooClose: 1, warned: true, saidOff: true), mustBePaced: true, displayKnownOn: true);
        Assert.IsNotNull(failure, "Short waits on an awake display are not excused by the clock's account.");
    }

    [TestMethod]
    public void AnAwakeDisplayMustLeaveTheLogEmpty()
    {
        string? failure = VBlankFrameClockRealTests.Judge(Run(warned: true, rawCode: true), mustBePaced: true, displayKnownOn: true);
        Assert.IsNotNull(failure, "Paced frames with a warning in the log are not a clean run on an awake display.");
    }

    [TestMethod]
    public void AnAwakeDisplayPassesWhenPacedWithAnEmptyLog()
    {
        Assert.IsNull(VBlankFrameClockRealTests.Judge(Run(), mustBePaced: true, displayKnownOn: true));
    }

    [TestMethod]
    public void AnUnknownDisplayMayBeUnpacedWhenTheClockSaysItIsOff()
    {
        Assert.IsNull(VBlankFrameClockRealTests.Judge(Run(paced: 1, unpaced: 5, warned: true, saidOff: true), mustBePaced: true, displayKnownOn: false));
        Assert.IsNull(VBlankFrameClockRealTests.Judge(Run(tooClose: 2, warned: true, saidOff: true), mustBePaced: true, displayKnownOn: false));
    }

    [TestMethod]
    public void AnUnknownDisplayStillFailsWithoutTheClockSayingWhy()
    {
        Assert.IsNotNull(VBlankFrameClockRealTests.Judge(Run(paced: 1, unpaced: 5), mustBePaced: true, displayKnownOn: false), "Unpaced on a local session with nothing logged.");
        Assert.IsNotNull(VBlankFrameClockRealTests.Judge(Run(tooClose: 1), mustBePaced: true, displayKnownOn: false), "Short waits with nothing logged.");
        Assert.IsNotNull(VBlankFrameClockRealTests.Judge(Run(tooClose: 3, warned: true, saidOff: true), mustBePaced: true, displayKnownOn: false), "More short waits than the clock lets through.");
    }

    [TestMethod]
    public void TheTooCloseBoundIsTheProductsFloorOfHalfAMillisecond()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(0.5), VBlankFrameClockRealTests.MinBlankSpacing);
        Assert.AreEqual(VBlankFrameClock.DefaultMinRealWait, VBlankFrameClockRealTests.MinBlankSpacing);
    }

    [TestMethod]
    public void TheInputTimeIsReadFromTheRealSession()
    {
        // One execution of the real call: Windows gives a time since the last input for this session, not more than the time since boot.
        TimeSpan? idle = InputIdle.Read();
        Assert.IsNotNull(idle, "GetLastInputInfo gives an input time here.");
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, idle.Value);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(Environment.TickCount64) + TimeSpan.FromSeconds(1), idle.Value);
    }
}
