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
using Icon = Earshot.Icons.Catalogue.Icon;

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

    // Every line printed, in order, for the tests of the guard.
    private readonly List<string> _printed = [];

    private void Measure(string line)
    {
        string text = "MEASURE " + line;
        _printed.Add(text);
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
        MeasureTrayStartAndIdle(Program.TrayInstanceName, info => Process.Start(info)!);
        MeasureCardOpen();
    }

    // ---- The guard: a measurement never starts a second tray while another Earshot tray runs in this session

    // A name of the test's own for the single-instance lock, so no test touches the lock of the tray the owner may be running.
    private static string OwnLockName() => "Earshot.test-" + Guid.NewGuid().ToString("N");

    [TestMethod]
    public void AnotherTrayIsSeenWhileItsInstanceLockIsHeldAndNotAfter()
    {
        string name = OwnLockName();
        Assert.IsFalse(AnotherTrayRuns(name), "No lock, no tray.");
        using (var held = new Mutex(initiallyOwned: true, name, Program.TrayInstanceOptions, out bool createdNew))
        {
            Assert.IsTrue(createdNew);
            Assert.IsTrue(AnotherTrayRuns(name), "The lock a tray holds is seen from this process.");
            Assert.IsTrue(AnotherTrayRuns(name), "Looking at the lock does not take it or release it.");
            held.ReleaseMutex();
        }

        Assert.IsFalse(AnotherTrayRuns(name), "The lock is gone with the tray that held it.");
    }

    [TestMethod]
    public void NoSecondTrayIsStartedWhileAnotherRunsAndTheLinesSayWhy()
    {
        string name = OwnLockName();
        int launches = 0;
        using var held = new Mutex(initiallyOwned: true, name, Program.TrayInstanceOptions, out _);
        MeasureTrayStartAndIdle(name, _ =>
        {
            launches++;
            throw new InvalidOperationException("A second tray was started.");
        });

        Assert.AreEqual(0, launches, "Earshot.exe would have found the lock, signalled the running tray and opened its card.");
        const string Why = "not measured (another Earshot tray runs in this session)";
        CollectionAssert.AreEqual(
            new[]
            {
                "MEASURE start_ms=" + Why,
                "MEASURE idle_cpu_ms=" + Why,
                "MEASURE idle_threads=" + Why,
                "MEASURE idle_context_switches=" + Why,
                "MEASURE idle_timer_wakeups=" + Why,
            },
            _printed);
        held.ReleaseMutex();
    }

    [TestMethod]
    public void TheTrayIsStartedWhenNoOtherTrayRuns()
    {
        int launches = 0;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            MeasureTrayStartAndIdle(OwnLockName(), _ =>
            {
                launches++;
                throw new InvalidOperationException("stand-in for Process.Start: nothing is started");
            }));
        Assert.AreEqual(1, launches, "With no lock held the measurement goes on to start the tray.");
        StringAssert.Contains(error.Message, "stand-in");
        Assert.IsEmpty(_printed, "Nothing was reported as skipped.");
    }

    // True when an Earshot tray holds its single-instance lock in this user's session. The tray takes that lock as the first thing it
    // does (Program.RunTray); a second Earshot.exe that finds it held signals the running tray and ends, and the signal opens the
    // running tray's card on the owner's desktop. So a measurement that is about to start Earshot.exe looks first, and does not
    // start it when the answer is yes. The name is a parameter so a test can use a lock of its own. The lock is only opened to see
    // that it is there, and the handle is closed at once; it is not waited on, so nothing is taken from the tray that holds it.
    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex.tryopenexisting
    internal static bool AnotherTrayRuns(string instanceName)
    {
        if (Mutex.TryOpenExisting(instanceName, Program.TrayInstanceOptions, out Mutex? existing))
        {
            existing.Dispose();
            return true;
        }

        return false;
    }

    private void MeasureTrayStartAndIdle(string instanceName, Func<ProcessStartInfo, Process> launch)
    {
        if (AnotherTrayRuns(instanceName))
        {
            const string Why = "not measured (another Earshot tray runs in this session)";
            Measure("start_ms=" + Why);
            Measure("idle_cpu_ms=" + Why);
            Measure("idle_threads=" + Why);
            Measure("idle_context_switches=" + Why);
            Measure("idle_timer_wakeups=" + Why);
            return;
        }

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
            process = launch(info);
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
        var row = new CompactContent(null, InkRole.Primary, TrayStatus.CardConnecting, null, null);
        card.Render(Earshot.Tests.Widget.CardKit.MainModel() with { Form = CardForm.Compact, Compact = row }, 96);
        card.PresentAnimated(new Rectangle(new Point(100, 100), card.ClientSize), 0);
        Application.DoEvents();
        clock.Stop();
        Assert.IsTrue(card.Visible, "The card was shown.");
        return clock.Elapsed;
    }
}
