using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// AdvanceRunAll used to treat a declined start (StoppedBeforeAnyStep) as
// "fresh enough" to retry, bypassing RunAllHalt.ShouldHalt (already correct in isolation, per
// RunAllHaltTests.cs) and restarting the same test forever. RunAllAdvanceTests.cs proves the
// extracted decision; this drives the real "Run all, step by step" button against real evidence
// on disk and proves the caller no longer bypasses it.
[TestClass]
public sealed class B2RunAllHaltTests
{
    [TestMethod]
    public void ADeclinedStartOnDiskMakesRunAllHaltRatherThanStartARow()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // Row 01, stopped before any step: the shape a declined Show-Preconditions leaves
        // (no criteria, no steps, the fail-closed rules' own definition of "stopped before any step").
        string folder = Path.Combine(liveTestRoot, "20260920T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), new ResultJsonFixture("01-a2dp-oneshot", "inconclusive").Build());

        // An unrelated, already-settled pass, so the at-rest banner itself is not what stops Run
        // all here: this test is about the per-row halt decision, not the banner lock.
        ResultJsonFixture.WriteTo(Path.Combine(liveTestRoot, "20260920T010000Z", "15-uninstall-reversal", "result.json"),
            new ResultJsonFixture("15-uninstall-reversal", "pass").WithCriterion("delayed-deletion", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-20T01:00:00.000Z").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsNull(form.ActiveRunnerForTests,
                "Run all started a child for a row that was already declined; it must halt and wait for the owner instead.");
            Assert.IsTrue(form.CarryOnVisibleForTests, "Run all did not offer 'I have read this, carry on' after halting on a declined row.");
        });
    }

    [TestMethod]
    public void ARowWaitingForThePowerCycleMakesRunAllHaltWithoutStartingAnything()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // Rows 01 to 03 (earlier in Run all's own order) already a clean pass, so the sequence
        // reaches row 04 without trying to start any of them for real.
        foreach (string testId in new[] { "01-a2dp-oneshot", "02-disconnect", "03-allow-pages" })
        {
            ResultJsonFixture.WriteTo(Path.Combine(liveTestRoot, "20260919T000000Z", testId, "result.json"),
                new ResultJsonFixture(testId, "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "yes").Build());
        }

        // Row 04's first half, waiting for its restart: resume.txt present, first-half markers
        // recorded, matching the half-marker criteria ids row 04 uses to tell its own halves apart.
        string folder = Path.Combine(liveTestRoot, "20260920T000000Z", "04-block-and-reboot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("04-block-and-reboot", "pass")
                .WithCriterion("block", "pass").WithCriterion("disabled-now", "pass").WithCriterion("locatable", "pass")
                .WithFinding("leftAtRest", "no-on-purpose").WithFinishedUtc("2026-09-20T00:05:00.000Z")
                .Build());
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "placeholder, never executed by StateDeriver or Run all");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsNull(form.ActiveRunnerForTests,
                "Run all started a child for a row waiting at the power-cycle boundary; nothing may start there.");
            Assert.IsFalse(form.CarryOnVisibleForTests,
                "the power-cycle boundary is resumed only through the row's own second-half button, not the ordinary carry-on button.");
            StringAssert.Contains(form.RunAllStatusTextForTests, "04");
        });
    }
}
