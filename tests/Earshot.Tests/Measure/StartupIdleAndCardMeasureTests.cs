using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase5;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Measure;

// Measurement only: prints figures, each on a line starting "MEASURE ", and never asserts on a number. The
// only assertions are that the thing being measured ran at all. Nothing here is a limit or a target; the
// figures are for reading after a run on a CI machine.
[TestClass]
public sealed class StartupIdleAndCardMeasureTests
{
    // A waiting budget for the start-up readiness line, not a measured figure.
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);

    // Time for the tray to settle after it reports ready, before the idle window begins. A design choice:
    // start-up work that continues after the "Tray started" line should not be counted as idle.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(30);

    // The line Program.Tray writes just before the message loop runs.
    private const string ReadyLine = "Tray started";

    public TestContext TestContext { get; set; } = null!;

    private void Measure(string line)
    {
        string text = "MEASURE " + line;
        Console.WriteLine(text);
        TestContext.WriteLine(text);
    }

    private static string Ms(TimeSpan span) => span.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    [TestMethod]
    [TestCategory("Measure")]
    public void MeasureStartIdleAndFirstCardOpen()
    {
        Measure("machine processors=" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) +
            " os=" + Environment.OSVersion.Version);
        MeasureTrayStartAndIdle();
        MeasureCardOpen();
    }

    private void MeasureTrayStartAndIdle()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        string root = Path.Combine(Path.GetTempPath(), "earshot-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // The same two variables tools/check.ps1 sets, given here explicitly so the child never reads the
            // owner's data or touches a device whatever the caller's environment holds.
            info.Environment[Paths.SafeModeVariable] = "1";
            info.Environment[Paths.DataRootVariable] = root;
            string logFile = Paths.FromEnvironment(
                name => name == Paths.DataRootVariable ? root : name == Paths.SafeModeVariable ? "1" : null,
                folder => Path.Combine(root, "unused", folder.ToString())).LogFile;

            var clock = Stopwatch.StartNew();
            process = Process.Start(info)!;
            bool ready = false;
            while (clock.Elapsed < StartLimit && !process.HasExited)
            {
                if (LogHolds(logFile, ReadyLine))
                {
                    ready = true;
                    break;
                }

                Thread.Sleep(10);
            }

            TimeSpan startTime = clock.Elapsed;
            if (!ready)
            {
                string why = process.HasExited
                    ? "the process ended with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                    : "no '" + ReadyLine + "' log line within " + StartLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
                Measure("start_ms=not measured (" + why + ")");
                Measure("idle_cpu_ms=not measured (the tray did not start)");
                return;
            }

            // Process creation to the "Tray started" line in the tray's own log, polled every 10 ms, so the
            // figure has about 10 ms of granularity. Real Earshot.exe, safe mode, redirected data root.
            Measure("start_ms=" + Ms(startTime) + " (Process.Start to the log line '" + ReadyLine + "', safe mode, 10 ms poll)");

            Thread.Sleep(SettleTime);
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            var idle = Stopwatch.StartNew();
            Thread.Sleep(IdleWindow);
            process.Refresh();
            TimeSpan cpuAfter = process.TotalProcessorTime;
            Measure("idle_cpu_ms=" + Ms(cpuAfter - cpuBefore) + " (Process.TotalProcessorTime delta over " +
                Ms(idle.Elapsed) + " ms of wall time, starting " + SettleTime.TotalSeconds.ToString(CultureInfo.InvariantCulture) +
                " s after the ready line)");
            Measure("idle_threads=" + process.Threads.Count.ToString(CultureInfo.InvariantCulture) + " (Process.Threads.Count at the end of the window)");

            // No documented per-process context switch count or timer wake-up count is read here: the
            // per-thread counters need ETW or a performance counter set this test does not create.
            Measure("idle_context_switches=not measured (no documented per-process API used)");
            Measure("idle_timer_wakeups=not measured (no documented per-process API used)");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(TimeSpan.FromSeconds(10));
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // The temp folder is left behind; the figures are already printed.
            }
        }
    }

    // True when the log file holds the text. The log appends with FileShare.ReadWrite, so reading it open is allowed.
    private static bool LogHolds(string file, string text)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void MeasureCardOpen()
    {
        // The same private desktop the card tests use, never the owner's.
        CardDesktop.Run(() =>
        {
            TimeSpan first;
            TimeSpan second;
            using (WidgetCard card = NewCompactCard())
            {
                first = OpenOnce(card);
                card.Hide();
                Application.DoEvents();
                second = OpenOnce(card);
            }

            TimeSpan freshInstance;
            using (WidgetCard card = NewCompactCard())
            {
                freshInstance = OpenOnce(card);
            }

            // From the request (Render, then Show, which creates the window on first use) to the card painted. Stopwatch, the compact
            // card directly on a private desktop; the presenter's own placement and animation are not included.
            Measure("card_first_open_ms=" + Ms(first) + " (new card, first Render + Show, until painted)");
            Measure("card_second_open_ms=" + Ms(second) + " (same card after HideCard, Render + Show, until painted)");
            Measure("card_new_instance_open_ms=" + Ms(freshInstance) + " (a second new card in the same process, Render + Show, until painted)");
        });
    }

    private static WidgetCard NewCompactCard() => new(new CapturingLog(), CardOpening.Unasked);

    private static TimeSpan OpenOnce(WidgetCard card)
    {
        var clock = Stopwatch.StartNew();
        var row = new CompactContent('\0', InkRole.Primary, TrayStatus.CardConnecting, null, null);
        card.Render(Earshot.Tests.Widget.CardKit.MainModel() with { Form = CardForm.Compact, Compact = row }, 96);
        card.PresentAnimated(new Rectangle(new Point(100, 100), card.ClientSize), 0);
        Application.DoEvents();
        clock.Stop();
        Assert.IsTrue(card.Visible, "The card was shown.");
        return clock.Elapsed;
    }
}
