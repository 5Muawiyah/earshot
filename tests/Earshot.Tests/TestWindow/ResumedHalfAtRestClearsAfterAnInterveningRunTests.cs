using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A reissued sequence number lands in the second half's own run folder, which keeps its first
// half's original stamp forever: comparing the sequence/order disagreement check against that
// bare stamp read every resume that happened after any intervening run as a disagreement,
// permanently, since the folder's own stamp could never catch up no matter what ran afterwards.
// A resumed half legitimately carries a sequence newer than its folder's own stamp; the check
// must compare against what actually happened (each folder's own newest event), never the stamp.
[TestClass]
public sealed class ResumedHalfAtRestClearsAfterAnInterveningRunTests
{
    // The reviewer's exact reproduction: a first half, a genuine intervening pass, then the
    // resumed second half finishing at rest "yes". The banner must clear and every row must
    // unlock, not stay red forever.
    [TestMethod]
    public void AResumedHalfEndingAtRestYesClearsTheBannerAndUnlocksEveryRowEvenAfterAnInterveningPass()
    {
        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

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

        // The intervening pass, in a newer stamp than the first half but older than the resume.
        string interveningFolder = Path.Combine(liveTestRoot, "20260921T010000Z", "01-a2dp-oneshot");
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

            // The sandbox's own fake machine for 10 v1's second half completes the run and leaves
            // the AirPods connected to the phone: the real, readable pass this reproduction needs,
            // never a kill.
            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(60));
            Assert.IsNull(form.ActiveRunnerForTests, "the second half never finished on its own. status=" + form.StatusTextForTests);

            form.ShowHomeViewForTests();
            Assert.IsFalse(form.BannerVisibleForTests,
                "a resumed half that genuinely ended at rest, after a real intervening pass, must clear the banner, not lock it forever.");

            Assert.IsTrue(form.SelectRowForTests("01"));
            Assert.IsTrue(form.StartButtonEnabledForTests,
                "every row must unlock once the resumed half is known to have ended at rest.");
        });
    }

    // A genuine sequence/order disagreement (the clock stepped back between two runs, exactly the
    // fixture shape OrderingSurvivesAClockStepBackTests already proves triggers Red) is not itself
    // permanent: a fresh Restore that actually is the newest thing to happen, by both sequence and
    // real event time, clears it.
    [TestMethod]
    public void ARestoreRecordingYesAfterAGenuineDisagreementClearsTheBanner()
    {
        using var root = new TempFolder();

        string earlierBySequenceLaterByStamp = Path.Combine(root.Path, "20260920T180000Z", "01-a2dp-oneshot");
        WriteResult(earlierBySequenceLaterByStamp, "01-a2dp-oneshot", "yes");
        RunSequence.EnsureMarker(root.Path, earlierBySequenceLaterByStamp);

        string laterBySequenceEarlierByStamp = Path.Combine(root.Path, "20260920T150000Z", "02-disconnect");
        WriteResult(laterBySequenceEarlierByStamp, "02-disconnect", "yes");
        RunSequence.EnsureMarker(root.Path, laterBySequenceEarlierByStamp);

        Assert.AreEqual(BannerLevel.Red, Banner.Compute(root.Path).Level,
            "fixture sanity: the deliberately stepped-back clock must still be read as a disagreement.");

        // A real Restore afterwards: the newest sequence issued, and (since nothing here backdates
        // its own files) genuinely the newest real event too.
        string restoreFolder = Path.Combine(root.Path, "20260922T000000Z", "00-restore");
        WriteResult(restoreFolder, "00-restore", "yes");
        RunSequence.EnsureMarker(root.Path, restoreFolder);

        Assert.AreEqual(BannerLevel.None, Banner.Compute(root.Path).Level,
            "a Restore that genuinely is the newest run, by sequence and by real event time, must clear a disagreement recorded before it, not stay locked forever.");
    }

    private static void WriteResult(string folder, string testId, string leftAtRest)
    {
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture(testId, "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", leftAtRest).Build());
    }
}
