using System.Threading.Tasks;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// ChildRunner.ReadLoop runs on a ThreadPool thread and its MessageReceived/TranscriptLine
// closures used to call Control.BeginInvoke guarded only by a plain IsHandleCreated read, with no
// try/catch: a message that arrived while, or just after, this form was disposed threw straight
// out of ReadLoop's own call stack, which nothing there catches, and an exception unhandled on a
// background thread takes the whole process down with it. The same race exists for a real owner
// closing the window during a live half (OnFormClosing kills the run and lets the close proceed),
// not only for a test harness disposing the form right after its own cleanup kill.
[TestClass]
public sealed class PostDisposalDeliveryTests
{
    [TestMethod]
    public void ABeginInvokeAfterDisposeIsCaughtAndRecordedNeverThrown()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        IReadOnlyList<WordingEntry> wording = Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));
        using var sandbox = new TempFolder();
        var sandboxOptions = new SandboxOptions { Folder = sandbox.Path };

        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            var form = new Earshot.TestWindow.Ui.MainForm(repoRoot, rows, wording, sandboxOptions, @"C:\nowhere\Earshot.exe");
            form.ForceControlCreationForTests();
            form.Dispose();

            try
            {
                // The exact call MessageReceived/TranscriptLine's closures make: BeginInvoke on a
                // form whose handle has been destroyed.
                form.SafeBeginInvokeForTests(() => { });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            Assert.AreEqual(1, form.PostDisposalDeliveryFailuresForTests.Count,
                "A post-disposal delivery must be recorded, not silently dropped.");
            Assert.IsInstanceOfType<InvalidOperationException>(form.PostDisposalDeliveryFailuresForTests[0]);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "the STA thread did not finish in time.");

        Assert.IsNull(thrown, "SafeBeginInvoke must never let the exception escape to its caller.");
    }

    // The read loop itself must not crash the process either: killing a real child can break its
    // stdout pipe before ReadLine reaches a clean end of stream, and a raw IOException or
    // ObjectDisposedException from that call, unguarded, is exactly as unhandled-on-a-background-
    // thread as the BeginInvoke race above. This exercises the real child, the real kill, and
    // waits for the real read loop task, asserting it always completes (never Faulted).
    [TestMethod]
    public void TheReadLoopNeverFaultsWhenItsChildIsKilledMidStream()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string repoRoot = RepositoryLocator.RepositoryRoot();
        using var sandbox = new TempFolder();
        string driver = Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
        string script = Path.Combine(repoRoot, "tools", "live-tests", "01-A2dpOneShot.ps1");
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string runRoot = Path.Combine(liveTestRoot, "20260920T000000Z");
        var options = new SandboxOptions { Folder = sandbox.Path };
        var extra = new List<(string Name, string Value)>
        {
            ("SandboxRoot", options.Folder), ("TestId", "01-a2dp-oneshot"), ("Case", "one"),
        };

        for (int i = 0; i < 25; i++)
        {
            using var runner = new ChildRunner(
                host, driver, script, @"C:\nowhere\Earshot.exe", runRoot,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false,
                environmentOverrides: options.ChildEnvironment, extraArguments: extra);
            runner.Start();
            Thread.Sleep(30); // give the child a moment to actually be mid-stream, not just spawned.
            runner.Kill();
            bool completed = runner.WaitForReadLoopAsync().Wait(TimeSpan.FromSeconds(10));
            Assert.IsTrue(completed, "iteration " + i + ": the read loop did not finish after the child was killed.");
        }
    }

    // A real child, a real start, no wait at all before the test harness's own cleanup kills and
    // disposes, repeated enough times to bias the timing toward the same race landing again if it
    // ever came back. TaskScheduler.UnobservedTaskException and AppDomain.UnhandledException are
    // both hooked for the whole run: either one firing means some exception escaped a path this
    // class does not already know about and is asserting on directly, which is exactly the shape
    // of failure a fix proved only by a deterministic, single-shot test could still miss.
    [TestMethod]
    public void StressStartKillDisposeNeverLeavesAnUnobservedOrUnhandledException()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        var unobserved = new List<Exception>();
        var unhandled = new List<object>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            lock (unobserved) { unobserved.Add(e.Exception); }
            e.SetObserved();
        };
        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
        {
            lock (unhandled) { unhandled.Add(e.ExceptionObject); }
        };

        TaskScheduler.UnobservedTaskException += onUnobserved;
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        try
        {
            const int iterations = 60;
            for (int i = 0; i < iterations; i++)
            {
                using var sandbox = new TempFolder();
                MainFormTestHarness.Run(sandbox.Path, form =>
                {
                    Assert.IsTrue(form.SelectRowForTests("01"));
                    form.ClickStartForTests();
                    Assert.IsNotNull(form.ActiveRunnerForTests, "iteration " + i + ": no child started.");
                    // No wait: return immediately so the harness's own cleanup (kill, then
                    // dispose) races the freshly started ChildRunner's read loop at its most
                    // active point.
                });
            }

            // TaskScheduler.UnobservedTaskException only fires when a faulted, unobserved Task is
            // finalized; forcing a full collection here is what gives any such task, from any of
            // the 60 runs above, the chance to be found and reported before this method asserts.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
        }

        Assert.AreEqual(0, unobserved.Count, "an unobserved task exception escaped: " + string.Join("; ", unobserved));
        Assert.AreEqual(0, unhandled.Count, "an unhandled exception reached AppDomain: " + string.Join("; ", unhandled));
    }
}
