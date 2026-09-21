using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Priority-zero fix, reported by the hosted build: dotnet test's own test host crashed with an
// unhandled InvalidOperationException ("Invoke or BeginInvoke cannot be called on a control until
// the window handle has been created") thrown from a background thread. ChildRunner.ReadLoop runs
// on a ThreadPool thread (Task.Run) and its MessageReceived/TranscriptLine closures in
// MainForm.BeginRun called Control.BeginInvoke guarded only by a plain IsHandleCreated read, with
// no check for IsDisposed and no try/catch: a message that arrives while (or just after) the form
// is disposed - exactly what MainFormTestHarness's own cleanup does to a still-running child -
// throws on that background thread, which is unhandled by construction (nothing on ReadLoop's own
// call stack catches it) and takes the whole process down with it, in production as much as in a
// test: OnFormClosing kills the active run and lets the close proceed, so the same race exists
// for a real owner closing the window on a live half.
//
// Reproduced here by driving the exact delivery path BeginRun wires up, after the form has
// already been disposed. The IsHandleCreated read could not be kept even as a fast path: checking
// it and then calling BeginInvoke are two separate steps, so a synchronous test can only ever see
// it correctly read false post-dispose (proving nothing) or race it, which is not reproducible on
// demand; MainForm.SafeBeginInvoke instead relies solely on catching what BeginInvoke itself
// throws, which is deterministic and is what actually stops the crash either way. Before this
// fix, the assertion below (proving delivery is recorded, not silently dropped) itself crashed
// this repository's real dotnet test host with "Test host process crashed: Unhandled exception",
// the same failure shape as the hosted report, because nothing caught it either.
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
                // form whose handle has been destroyed. Before the fix, this line alone threw.
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

    // The stress form of the original report: real child, real start, no wait at all before the
    // harness's own cleanup kills and disposes, repeated enough times to bias the timing toward
    // the race actually landing. Measured on this machine (Priority-zero investigation,
    // 2026-09-21): 400 iterations of this exact shape took about 22 s and never crashed the host
    // even before this fix existed to catch anything (the pre-fix run's "crash" was reproduced
    // deterministically instead, in ABeginInvokeAfterDisposeIsCaughtAndRecordedNeverThrown, not by
    // timing); kept here at a smaller count so the gate does not pay for 400 child-process starts,
    // as a standing guard against the same class of race recurring.
    [TestMethod]
    public void StressStartKillDisposeNeverCrashesTheHost()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        const int iterations = 40;
        for (int i = 0; i < iterations; i++)
        {
            using var sandbox = new TempFolder();
            MainFormTestHarness.Run(sandbox.Path, form =>
            {
                Assert.IsTrue(form.SelectRowForTests("01"));
                form.ClickStartForTests();
                Assert.IsNotNull(form.ActiveRunnerForTests, "iteration " + i + ": no child started.");
                // No wait: return immediately so MainFormTestHarness's own finally block (kill,
                // then dispose) races the freshly started ChildRunner's read loop at its most
                // active point, exactly as the hosted build's crash report described.
            });
        }
    }
}
