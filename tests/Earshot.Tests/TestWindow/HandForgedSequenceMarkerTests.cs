using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Two ways a hand-forged gui-sequence.txt used to defeat the sequence-based ordering the counter
// file exists to make trustworthy in the first place.
[TestClass]
public sealed class HandForgedSequenceMarkerTests
{
    // A marker above anything RunSequence.TakeNext has ever actually issued was never handed out
    // by a real half. With the counter at 3, a folder claiming marker 4 (and a future finishedUtc,
    // so it would also win on real event time alone) must never become the chosen, trusted run: it
    // reads Unknown, and the banner shows, never cleared by its own forged "yes".
    [TestMethod]
    public void AMarkerAboveTheCounterNeverClearsARealNo()
    {
        using var root = new TempFolder();

        RunSequence.TakeNext(root.Path); // 1
        RunSequence.TakeNext(root.Path); // 2
        RunSequence.TakeNext(root.Path); // 3, the counter's own real ceiling.

        string realNoFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        WriteMarkedResult(realNoFolder, "01-a2dp-oneshot", "no", sequence: 2, finishedUtc: "2026-09-20T00:00:00.000Z");

        string forgedFolder = Path.Combine(root.Path, "20260921T000000Z", "02-disconnect");
        WriteMarkedResult(forgedFolder, "02-disconnect", "yes", sequence: 4, finishedUtc: "2099-01-01T00:00:00.000Z");

        Assert.AreEqual(BannerLevel.Red, Banner.Compute(root.Path).Level,
            "a marker the counter never issued, however clean its own result and however far in the future its finishedUtc, must never clear a real not-at-rest result.");
    }

    // The same fact, read through StateDeriver for the forged row itself: it settles nothing, the
    // same fail-closed shape a kill already reads as.
    [TestMethod]
    public void ARowWithAForgedMarkerReadsUnknownNeverPassed()
    {
        using var root = new TempFolder();
        RunSequence.TakeNext(root.Path); // counter ceiling: 1.

        string forgedFolder = Path.Combine(root.Path, "20260921T000000Z", "02-disconnect");
        WriteMarkedResult(forgedFolder, "02-disconnect", "yes", sequence: 5, finishedUtc: "2099-01-01T00:00:00.000Z");

        TestRowSpec spec = TestRowSpecFixtures.OneHalf("02-disconnect");
        var evidence = EvidenceStore.LoadEvidence(root.Path, spec.TestId);
        DerivedRowState state = StateDeriver.Derive(spec, evidence, chosenExePath: null, chosenExeLastWriteUtc: null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind,
            "a row whose only evidence carries a marker the counter never issued must read Unknown, never Passed.");
    }

    // Two folders sharing the exact same number lock the banner while the collision stands, with
    // its own distinct plain text naming the cause, but a later run that unambiguously outranks
    // both (by sequence and by real event time) clears it: the warning does genuinely clear when a
    // test ends with this computer leaving the AirPods alone, even after a collision.
    [TestMethod]
    public void ACollisionShowsTheDuplicateRecordTextAndClearsOnceALaterRunOutranksBoth()
    {
        using var root = new TempFolder();

        // Both markers legitimately issued (never above the counter, so item 1's own forged-marker
        // check never fires here): folderB's own gui-sequence.txt is a copy of folderA's, the exact
        // shape "a copied or duplicated record" describes.
        RunSequence.TakeNext(root.Path); // 1
        RunSequence.TakeNext(root.Path); // 2, both folders below claim this same number.

        string folderA = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        WriteMarkedResult(folderA, "01-a2dp-oneshot", "yes", sequence: 2, finishedUtc: "2026-09-20T00:00:00.000Z");

        string folderB = Path.Combine(root.Path, "20260920T010000Z", "02-disconnect");
        WriteMarkedResult(folderB, "02-disconnect", "no", sequence: 2, finishedUtc: "2026-09-20T01:00:00.000Z");

        BannerState collided = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Red, collided.Level, "a genuine sequence collision must show red.");
        Assert.IsTrue(collided.IsDuplicateRecordCause, "the collision must be named as the cause.");
        StringAssert.Contains(Copy.BannerPlainText(BannerLevel.Red, collided.IsDuplicateRecordCause), "copied or duplicated");

        // A later Restore, unambiguously the newest thing by both sequence and real event time:
        // its own marker is legitimately issued too, past both colliding folders.
        RunSequence.TakeNext(root.Path); // 3
        string restoreFolder = Path.Combine(root.Path, "20260921T000000Z", "00-restore");
        WriteMarkedResult(restoreFolder, "00-restore", "yes", sequence: 3, finishedUtc: "2026-09-21T00:00:00.000Z");

        BannerState cleared = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.None, cleared.Level,
            "a later run that unambiguously outranks a colliding pair by sequence and real event time must clear the banner, not lock it forever.");
    }

    private static void WriteMarkedResult(string folder, string testId, string leftAtRest, long sequence, string finishedUtc)
    {
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture(testId, "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", leftAtRest)
                .WithFinishedUtc(finishedUtc).Build());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RunSequence.MarkerFileName), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
