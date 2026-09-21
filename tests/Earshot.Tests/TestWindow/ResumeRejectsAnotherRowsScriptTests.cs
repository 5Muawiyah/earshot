using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// StartResumedSecondHalf used to pass every one of the 16 shipped scripts to ResumeFile.TryParse,
// not just the one this row is allowed to resume, so a resume.txt naming a different (but still
// genuine, still shipped) script was accepted. Real MainForm, real "Carry on with the second
// half" click, real files on disk; nothing here ever starts a process, since the point is that
// nothing should.
[TestClass]
public sealed class ResumeRejectsAnotherRowsScriptTests
{
    [TestMethod]
    public void ResumeTxtNamingADifferentShippedScriptIsRejectedRatherThanStarted()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string realExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // Test 10 variant 5's own first half (the sign-out variant, powerCycleRequirement
        // "none" in Data\tests.json): it records no criterion at all, only the
        // stateBeforeRestart finding, so this is the one two-half row a resumed start never has
        // to clear the power-cycle gate to reach ResumeFile.TryParse's own decision.
        // leftAtRest is given too (Amber, never locking): without it the banner reads Red (no
        // finding at all is the same fail-closed shape as "unknown"), which would block every row
        // but 00 for a reason that has nothing to do with what this test is proving.
        string folder = Path.Combine(liveTestRoot, "20260920T000000Z", "10-shutdown-messages-v5");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("10-shutdown-messages-v5", "inconclusive")
                .WithFinding("stateBeforeRestart", "Blocked").WithFinding("leftAtRest", "no-on-purpose")
                .WithFinishedUtc("2026-09-20T00:05:00.000Z")
                .Build());

        // resume.txt itself, well formed and otherwise entirely valid, but naming
        // 09-ShutdownWhileConnected.ps1: a different, real, shipped script, never
        // 10-ShutdownMessages.ps1.
        string otherScript = Path.Combine(repoRoot, "tools", "live-tests", "09-ShutdownWhileConnected.ps1");
        string runRoot = Path.Combine(liveTestRoot, "20260920T000000Z");
        string line = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + otherScript + "\" -ExePath \"" + realExePath +
            "\" -RunRoot \"" + runRoot + "\" -Resume -Variant 5";
        File.WriteAllText(Path.Combine(folder, "resume.txt"), line);

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("10.5"));

            form.ClickStartForTests();

            Assert.IsNull(form.ActiveRunnerForTests, "a resume.txt naming a different shipped script must never start anything.");
            StringAssert.Contains(form.StatusTextForTests, "resume.txt could not be used");
            StringAssert.Contains(form.StatusTextForTests, "not one of the 16 shipped scripts");
        });
    }
}
