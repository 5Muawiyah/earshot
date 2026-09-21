using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The lock rule: each case is named for the part of the rule it pins. Rows 15, 00 uninstall and
// 07 plan B read Locked in a clean sandbox and unlock on a passing rehearsal file.
[TestClass]
public sealed class ElevationGateTests
{
    private static ParsedResult Result(string overall, DateTimeOffset? finishedUtc) => new()
    {
        Test = ElevationGate.RehearsalTestId,
        Overall = overall,
        Criteria = overall == "pass"
            ? new[] { new CriterionRecord("approved-exit-code", "c", "pass", string.Empty) }
            : Array.Empty<CriterionRecord>(),
        Findings = Array.Empty<FindingRecord>(),
        StepCount = 0,
        FinishedUtc = finishedUtc,
    };

    [TestMethod]
    public void NoRehearsalAtAllIsLocked()
    {
        Assert.IsFalse(ElevationGate.IsUnlocked(null, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void ARehearsalThatIsNotAPassIsLocked()
    {
        var rehearsal = Result("fail", DateTimeOffset.UtcNow);
        Assert.IsFalse(ElevationGate.IsUnlocked(rehearsal, DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [TestMethod]
    public void APassWithNoFinishedUtcIsLocked()
    {
        var rehearsal = Result("pass", null);
        Assert.IsFalse(ElevationGate.IsUnlocked(rehearsal, DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [TestMethod]
    public void APassOlderThanTheHarnessFilesIsStillLocked()
    {
        var rehearsal = Result("pass", DateTimeOffset.UtcNow.AddDays(-2));
        Assert.IsFalse(ElevationGate.IsUnlocked(rehearsal, DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [TestMethod]
    public void APassNewerThanTheHarnessFilesUnlocks()
    {
        var rehearsal = Result("pass", DateTimeOffset.UtcNow);
        Assert.IsTrue(ElevationGate.IsUnlocked(rehearsal, DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [TestMethod]
    public void HarnessNewestWriteUtcReadsTheRealThreeFilesInThisRepository()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        DateTimeOffset newest = ElevationGate.HarnessNewestWriteUtc(repoRoot);
        Assert.IsGreaterThan(DateTimeOffset.MinValue, newest, "none of LiveTest.psm1/Invoke-GuiHalf.ps1/ReadHostShim.ps1 were found in this repository.");
    }

    [TestMethod]
    public void FindNewestRehearsalPicksTheNewestStampByFinishedUtc()
    {
        using var root = new TempFolder();
        string olderFolder = Path.Combine(root.Path, "20260918T090000Z", ElevationGate.RehearsalTestId);
        string newerFolder = Path.Combine(root.Path, "20260920T090000Z", ElevationGate.RehearsalTestId);

        ResultJsonFixture.WriteTo(Path.Combine(olderFolder, "result.json"),
            new ResultJsonFixture(ElevationGate.RehearsalTestId, "pass")
                .WithCriterion("approved-exit-code", "pass").WithCriterion("declined-recorded", "pass")
                .WithFinishedUtc("2026-09-18T09:05:00.000Z").Build());

        ResultJsonFixture.WriteTo(Path.Combine(newerFolder, "result.json"),
            new ResultJsonFixture(ElevationGate.RehearsalTestId, "pass")
                .WithCriterion("approved-exit-code", "pass").WithCriterion("declined-recorded", "pass")
                .WithFinishedUtc("2026-09-20T09:05:00.000Z").Build());

        ParsedResult? found = ElevationGate.FindNewestRehearsal(root.Path);

        Assert.IsNotNull(found);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-20T09:05:00.000Z"), found!.FinishedUtc);
    }

    [TestMethod]
    public void AMalformedRehearsalFileIsSkippedFailClosedNeverReadAsAPass()
    {
        using var root = new TempFolder();
        string folder = Path.Combine(root.Path, "20260920T090000Z", ElevationGate.RehearsalTestId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), "not json at all");

        Assert.IsNull(ElevationGate.FindNewestRehearsal(root.Path));
    }

    // Newest decides, and unreadable is locked. The old FindNewestRehearsal walked every stamp
    // looking for the newest one that happened to read as a pass, so a newer attempt that crashed,
    // was killed, or wrote a malformed result.json was silently skipped past, and an older genuine
    // pass underneath it unlocked row 15 as though the newest attempt had never happened.
    [TestMethod]
    public void ANewerUnreadableRehearsalLocksEvenWithAnOlderPassUnderneath()
    {
        using var root = new TempFolder();
        string olderFolder = Path.Combine(root.Path, "20260918T090000Z", ElevationGate.RehearsalTestId);
        ResultJsonFixture.WriteTo(Path.Combine(olderFolder, "result.json"),
            new ResultJsonFixture(ElevationGate.RehearsalTestId, "pass")
                .WithCriterion("approved-exit-code", "pass").WithFinishedUtc("2026-09-18T09:05:00.000Z").Build());

        string newerFolder = Path.Combine(root.Path, "20260920T090000Z", ElevationGate.RehearsalTestId);
        Directory.CreateDirectory(newerFolder);
        File.WriteAllText(Path.Combine(newerFolder, "result.json"), "not json at all");

        Assert.IsNull(ElevationGate.FindNewestRehearsal(root.Path),
            "A newer unreadable rehearsal attempt must lock the row, not fall back to the older pass underneath it.");
    }

    [TestMethod]
    public void ANewerKilledRehearsalLocksEvenWithAnOlderPassUnderneath()
    {
        using var root = new TempFolder();
        string olderFolder = Path.Combine(root.Path, "20260918T090000Z", ElevationGate.RehearsalTestId);
        ResultJsonFixture.WriteTo(Path.Combine(olderFolder, "result.json"),
            new ResultJsonFixture(ElevationGate.RehearsalTestId, "pass")
                .WithCriterion("approved-exit-code", "pass").WithFinishedUtc("2026-09-18T09:05:00.000Z").Build());

        string newerFolder = Path.Combine(root.Path, "20260920T090000Z", ElevationGate.RehearsalTestId);
        Directory.CreateDirectory(newerFolder);
        File.WriteAllText(Path.Combine(newerFolder, "gui-killed.txt"), "2026-09-20T09:00:00.000Z");

        Assert.IsNull(ElevationGate.FindNewestRehearsal(root.Path),
            "A newer killed rehearsal attempt must lock the row, not fall back to the older pass underneath it.");
    }
}
