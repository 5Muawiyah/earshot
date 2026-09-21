using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// ChildRunner's own ReadLoop already treats a broken pipe from a killed process as a clean end
// (its header comment: "MessageReceived/TranscriptLine's own subscribers ... are what is
// responsible for a child that ends without an exit message being noticed at all, not this
// loop"), but nothing was that subscriber: if the real OS process ends some other way than the
// window's own Kill() (killed externally, crashed before it could send "type": "crash", Windows
// itself tearing it down), _activeRunner stayed set forever and the row kept reading whatever it
// said before, never Unknown. Real MainForm, a real child process, killed for real by its PID
// (never through ChildRunner's own Kill(), which is what the window calling Stop would do).
[TestClass]
public sealed class SilentChildExitReadsUnknownTests
{
    [TestMethod]
    public void AChildKilledExternallyBeforeItReportsAnythingReadsUnknownLikeAForcedKill()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();

            ChildRunner? runner = form.ActiveRunnerForTests;
            Assert.IsNotNull(runner, "the Start click never became the active runner. status=" + form.StatusTextForTests);
            int pid = runner!.ProcessId;
            Assert.IsGreaterThan(0, pid, "no real child process was started.");

            // The real OS process, killed directly by its own PID: an external force the window
            // never asked for and never learns about through ChildRunner.Kill().
            using (var process = System.Diagnostics.Process.GetProcessById(pid))
            {
                process.Kill(entireProcessTree: true);
                Assert.IsTrue(process.WaitForExit(5000), "the externally killed process did not exit.");
            }

            // ReadLoop notices the closed pipe on its own ThreadPool thread and raises
            // ReadLoopEnded, marshalled onto this thread the same way MessageReceived is; pump
            // the message loop until MainForm has actually handled it.
            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(10));

            Assert.IsNull(form.ActiveRunnerForTests, "the row must stop being treated as active once its process is gone with nothing reported.");
            StringAssert.Contains(form.StatusTextForTests, "Unknown");

            string liveTestRoot = System.IO.Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
            IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(liveTestRoot, "01-a2dp-oneshot");
            Assert.IsTrue(evidence.Count > 0 && evidence[0].HasKilledMarker, "no gui-killed.txt marker was written for the silently ended run.");

            DerivedRowState derived = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), evidence, chosenExePath: null, chosenExeLastWriteUtc: null);
            Assert.AreEqual(RowStateKind.Unknown, derived.Kind, "the row does not read Unknown the way a forced kill would leave it.");
        });
    }
}
