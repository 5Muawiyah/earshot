using System.Windows.Forms;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A resumed second half keeps its first half's run folder (the same stamp), but the two events
// can be far apart, with another test started and passed cleanly in between. Before this fix, the
// run folder's sequence number stayed the first half's own number forever (RunSequence.EnsureMarker
// never reissued it on resume), so a resumed half that ends not at rest, or is killed, could still
// read as OLDER than a run that passed between the two halves, and the banner stayed clear over a
// machine no run had actually finished putting at rest.
[TestClass]
public sealed class ResumedSecondHalfOutranksAnInterveningPassTests
{
    [TestMethod]
    public void AKillDuringAResumedSecondHalfShowsTheBannerEvenAfterAnInterveningPass()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // The first half, exactly as a real one leaves it: no criteria, only test 10's own
        // first-half marker, and its own sequence number (RunSequence's real job, assigned once a
        // half actually starts).
        string firstHalfRoot = Path.Combine(liveTestRoot, "20260921T000000Z");
        const string testId = "10-shutdown-messages-v1";
        string testFolder = Path.Combine(firstHalfRoot, testId);
        ResultJsonFixture.WriteTo(Path.Combine(testFolder, "result.json"),
            new ResultJsonFixture(testId, "inconclusive")
                .WithFinding("stateBeforeRestart", "connected")
                .WithFinding("leftAtRest", "no-on-purpose")
                .WithFinishedUtc("2026-09-21T00:00:00.000Z")
                .Build());
        RunSequence.EnsureMarker(liveTestRoot, testFolder);

        string scriptPath = Path.Combine(repoRoot, "tools", "live-tests", "10-ShutdownMessages.ps1");
        string exePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string resumeLine = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -ExePath \"" +
            exePath + "\" -RunRoot \"" + firstHalfRoot + "\" -Resume -Variant 1";
        File.WriteAllText(Path.Combine(testFolder, "resume.txt"), resumeLine);

        // A different test, started and passed at rest in a newer stamp, between the first half
        // and the resume below: exactly what stepping through the list rather than sitting and
        // waiting for one row's own power cycle looks like.
        string interveningRoot = Path.Combine(liveTestRoot, "20260921T010000Z");
        string interveningFolder = Path.Combine(interveningRoot, "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(interveningFolder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass")
                .WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "yes")
                .WithFinishedUtc("2026-09-21T01:00:00.000Z")
                .Build());
        RunSequence.EnsureMarker(liveTestRoot, interveningFolder);

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.PowerCycleVerdictOverrideForTests = () => PowerCycleVerdict.Restart;
            Assert.IsTrue(form.SelectRowForTests("10.1"));
            Assert.AreEqual(Copy.CarryOnSecondHalfButtonLabel, form.StartButtonTextForTests,
                "the pending first half was not recognised before the second half ever started.");

            form.ClickStartForTests();

            ChildRunner? runner = form.ActiveRunnerForTests;
            Assert.IsNotNull(runner, "the second half never became the active runner. status=" + form.StatusTextForTests);
            Assert.IsTrue(runner!.ProcessId > 0, "no real child process was started for the second half.");

            form.KillActiveRunForTests();

            form.ShowHomeViewForTests();
            Assert.IsTrue(form.BannerVisibleForTests,
                "a kill during a resumed second half, happening after another test passed at rest in between, must still show the banner.");

            Assert.IsTrue(form.SelectRowForTests("01"));
            Assert.IsFalse(form.StartButtonEnabledForTests,
                "the at-rest banner did not lock an unrelated row after the resumed half was killed.");

            int dialogCalls = 0;
            form.ConfirmDialogForTests = _ => { dialogCalls++; return DialogResult.Yes; };
            FormClosingEventArgs args = form.RaiseFormClosingForTests(CloseReason.UserClosing);
            Assert.AreEqual(1, dialogCalls,
                "closing the window must ask before letting the owner leave the machine possibly not at rest.");
            Assert.IsFalse(args.Cancel);
        });
    }
}
