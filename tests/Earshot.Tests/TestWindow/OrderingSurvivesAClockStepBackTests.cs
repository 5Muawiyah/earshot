using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A run folder's own stamp, and result.json's own finishedUtc, are both only as trustworthy as
// the system clock was at the moment they were written. With the clock stepped back between two
// runs, a later run's own stamp can read earlier than an older run's: a later kill or a later
// not-at-rest result used to hide behind an older, clean-looking one, and a later fail used to
// read as the chosen, reported "Passed" run. RunSequence's own marker (immune to the clock) fixes
// the ordering; where it disagrees with the stamps, that disagreement is itself surfaced rather
// than silently resolved.
[TestClass]
public sealed class OrderingSurvivesAClockStepBackTests
{
    // The stamp folder names below are deliberately out of chronological order with the sequence
    // they were issued in: "T180000" (6pm) was issued sequence 1 (it ran first, while the clock
    // still read correctly), "T150000" (3pm, the very same day) was issued sequence 2 (it ran
    // second, after the clock was stepped back three hours).
    private const string EarlierBySequenceLaterByStamp = "20260920T180000Z";
    private const string LaterBySequenceEarlierByStamp = "20260920T150000Z";

    [TestMethod]
    public void FindRunFoldersOrdersByAssignedSequenceEvenWhenTheClockWasSteppedBackBetweenRuns()
    {
        using var root = new TempFolder();
        string folderRanFirst = Path.Combine(root.Path, EarlierBySequenceLaterByStamp, "01-a2dp-oneshot");
        Directory.CreateDirectory(folderRanFirst);
        RunSequence.EnsureMarker(root.Path, folderRanFirst);

        string folderRanSecond = Path.Combine(root.Path, LaterBySequenceEarlierByStamp, "01-a2dp-oneshot");
        Directory.CreateDirectory(folderRanSecond);
        RunSequence.EnsureMarker(root.Path, folderRanSecond);

        IReadOnlyList<(string Stamp, string Folder)> found = EvidenceStore.FindRunFolders(root.Path, "01-a2dp-oneshot");

        Assert.AreEqual(folderRanSecond, found[0].Folder,
            "The run that actually happened second (higher sequence) must sort first, even though its stamp reads earlier.");
    }

    // The concrete symptom this closes: a fail that happened later (by sequence) must be the
    // verdict, not an older pass that merely has a newer-looking stamp.
    [TestMethod]
    public void ALaterFailIsTheVerdictEvenThoughAnOlderPassHasTheNewerLookingStamp()
    {
        using var root = new TempFolder();
        TestRowSpec spec = TestRowSpecFixtures.OneHalf("01-a2dp-oneshot");

        string olderPassFolder = Path.Combine(root.Path, EarlierBySequenceLaterByStamp, spec.TestId);
        WriteResult(olderPassFolder, spec.TestId, "pass", "c1", "pass");
        RunSequence.EnsureMarker(root.Path, olderPassFolder);

        string laterFailFolder = Path.Combine(root.Path, LaterBySequenceEarlierByStamp, spec.TestId);
        WriteResult(laterFailFolder, spec.TestId, "fail", "c1", "fail");
        RunSequence.EnsureMarker(root.Path, laterFailFolder);

        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(root.Path, spec.TestId);
        DerivedRowState state = StateDeriver.Derive(spec, evidence, null, null);

        Assert.AreEqual(RowStateKind.Failed, state.Kind,
            "The run that actually happened later (by sequence) failed; it must be the verdict, not the older run's stale pass.");
    }

    // The other concrete symptom: a later kill must show the red banner, not hide behind an older
    // clean pass whose stamp merely reads newer.
    [TestMethod]
    public void BannerShowsRedForALaterKillEvenThoughAnOlderCleanPassHasTheNewerLookingStamp()
    {
        using var root = new TempFolder();
        string olderPassFolder = Path.Combine(root.Path, EarlierBySequenceLaterByStamp, "01-a2dp-oneshot");
        WriteResultWithLeftAtRest(olderPassFolder, "01-a2dp-oneshot", "yes");
        RunSequence.EnsureMarker(root.Path, olderPassFolder);

        string laterKilledFolder = Path.Combine(root.Path, LaterBySequenceEarlierByStamp, "02-disconnect");
        WriteResultWithLeftAtRest(laterKilledFolder, "02-disconnect", "yes"); // stale, from before the kill
        File.WriteAllText(Path.Combine(laterKilledFolder, "gui-killed.txt"), "2026-09-20T15:05:00.000Z");
        RunSequence.EnsureMarker(root.Path, laterKilledFolder);

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreEqual(BannerLevel.Red, banner.Level,
            "A later kill (by sequence) must never be hidden behind an older clean pass just because the clock reads it as newer.");
    }

    // The disagreement itself is surfaced on the row, and at-rest is treated as unknown, rather
    // than silently trusting the sequence-corrected order and saying nothing about the conflict.
    [TestMethod]
    public void ARowNotesTheDisagreementAndTreatsAtRestAsUnknownWhileKindStillReflectsTheCorrectedOrder()
    {
        using var root = new TempFolder();
        TestRowSpec spec = TestRowSpecFixtures.OneHalf("01-a2dp-oneshot");

        string olderPassFolder = Path.Combine(root.Path, EarlierBySequenceLaterByStamp, spec.TestId);
        WriteResult(olderPassFolder, spec.TestId, "pass", "c1", "pass", leftAtRest: "yes");
        RunSequence.EnsureMarker(root.Path, olderPassFolder);

        string laterFailFolder = Path.Combine(root.Path, LaterBySequenceEarlierByStamp, spec.TestId);
        WriteResult(laterFailFolder, spec.TestId, "fail", "c1", "fail", leftAtRest: "yes");
        RunSequence.EnsureMarker(root.Path, laterFailFolder);

        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(root.Path, spec.TestId);
        bool disagreement = EvidenceStore.SequenceDisagreesWithStampOrder(evidence);
        DerivedRowState state = StateDeriver.Derive(spec, evidence, null, null, disagreement);

        Assert.IsTrue(disagreement, "The fixture is deliberately built so sequence and stamp order disagree.");
        Assert.AreEqual(RowStateKind.Failed, state.Kind, "Kind must still reflect the sequence-corrected verdict.");
        Assert.AreEqual("unknown", state.LeftAtRest, "At-rest must be treated as unknown while sequence and time disagree, whatever result.json says.");
        StringAssert.Contains(state.HistoryNote, "disagree");
    }

    // No disagreement at all (both signals agree, or only one exists) leaves LeftAtRest untouched.
    [TestMethod]
    public void NoDisagreementLeavesLeftAtRestAlone()
    {
        using var root = new TempFolder();
        TestRowSpec spec = TestRowSpecFixtures.OneHalf("01-a2dp-oneshot");
        string folder = Path.Combine(root.Path, "20260920T000000Z", spec.TestId);
        WriteResult(folder, spec.TestId, "pass", "c1", "pass", leftAtRest: "yes");
        RunSequence.EnsureMarker(root.Path, folder);

        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(root.Path, spec.TestId);
        bool disagreement = EvidenceStore.SequenceDisagreesWithStampOrder(evidence);
        DerivedRowState state = StateDeriver.Derive(spec, evidence, null, null, disagreement);

        Assert.IsFalse(disagreement);
        Assert.AreEqual("yes", state.LeftAtRest);
    }

    // A row can never read as a clean green pass while its own evidence disagrees about run
    // order: IsGreen requires Qualifier to be null, so even a chosen run that itself passed
    // cleanly must never leave this row looking exactly like an ordinary, fully-confirmed pass.
    [TestMethod]
    public void ARowIsNeverGreenWhenSequenceAndTimeDisagreeEvenIfTheChosenRunPassedCleanly()
    {
        using var root = new TempFolder();
        TestRowSpec spec = TestRowSpecFixtures.OneHalf("01-a2dp-oneshot");

        string olderFolder = Path.Combine(root.Path, EarlierBySequenceLaterByStamp, spec.TestId);
        WriteResult(olderFolder, spec.TestId, "pass", "c1", "pass", leftAtRest: "yes");
        RunSequence.EnsureMarker(root.Path, olderFolder);

        string newerFolder = Path.Combine(root.Path, LaterBySequenceEarlierByStamp, spec.TestId);
        WriteResult(newerFolder, spec.TestId, "pass", "c1", "pass", leftAtRest: "yes");
        RunSequence.EnsureMarker(root.Path, newerFolder);

        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(root.Path, spec.TestId);
        bool disagreement = EvidenceStore.SequenceDisagreesWithStampOrder(evidence);
        DerivedRowState state = StateDeriver.Derive(spec, evidence, null, null, disagreement);

        Assert.IsTrue(disagreement, "The fixture is deliberately built so sequence and stamp order disagree.");
        Assert.AreEqual(RowStateKind.Passed, state.Kind, "Kind itself still reflects the sequence-corrected verdict: a clean pass.");
        Assert.IsFalse(state.IsGreen, "A row must never read as a clean green pass while run order is unconfirmed, whatever the chosen run itself says.");
    }

    private static void WriteResult(string folder, string testId, string overall, string criterionId, string outcome, string? leftAtRest = null)
    {
        var fixture = new ResultJsonFixture(testId, overall).WithCriterion(criterionId, outcome);
        if (leftAtRest is not null)
        {
            fixture = fixture.WithFinding("leftAtRest", leftAtRest);
        }

        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), fixture.Build());
    }

    private static void WriteResultWithLeftAtRest(string folder, string testId, string leftAtRest) =>
        WriteResult(folder, testId, "pass", "c1", "pass", leftAtRest);
}
