using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Pure-Core reproductions and a generated sweep for the same defect
// ResumedSecondHalfOutranksAnInterveningPassTests proves through the real form: a resumed second
// half reuses its first half's run folder and, unless something reissues it, its first half's own
// sequence number forever. If another run started and passed at rest in between, that stale
// number can make the second half's own eventual finish, whatever it actually says, look OLDER
// than the run in between, and the banner never shows.
[TestClass]
public sealed class ResumedSecondHalfOrderingCoreTests
{
    // The reviewer's second reproduction, direct against Banner.Compute: no real process, no
    // MainForm, just the three run folders a resumed 09 leaves on disk. The "first half" is never
    // separately modelled here (Banner does not distinguish halves, only StateDeriver does); what
    // matters is that the SAME folder's result.json is overwritten in place, the way
    // Complete-LiveTestRun really does it, while its sequence marker (written once, for the first
    // half) is never touched again.
    [TestMethod]
    public void ASecondHalfRecordingNotAtRestOutranksAnInterveningPassEvenWithAStaleSequence()
    {
        using var root = new TempFolder();
        const string resumedTestId = "09-shutdown-while-connected";
        string resumedFolder = Path.Combine(root.Path, "20260921T000000Z", resumedTestId);

        // The first half: whatever it left behind, sequence 1, assigned once.
        ResultJsonFixture.WriteTo(Path.Combine(resumedFolder, "result.json"),
            new ResultJsonFixture(resumedTestId, "inconclusive")
                .WithFinding("leftAtRest", "no-on-purpose")
                .WithFinishedUtc("2026-09-21T00:00:00.000Z")
                .Build());
        RunSequence.EnsureMarker(root.Path, resumedFolder);

        // A different test, started and passed at rest in a newer stamp, between the first half
        // and the resume below.
        string interveningFolder = Path.Combine(root.Path, "20260921T010000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(interveningFolder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass")
                .WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "yes")
                .WithFinishedUtc("2026-09-21T01:00:00.000Z")
                .Build());
        RunSequence.EnsureMarker(root.Path, interveningFolder);

        // The resumed second half finishes for real, well after the run above, but writes back
        // into its own (older-stamped) folder, and nothing here ever asks for a fresh sequence
        // number: exactly what a plain EnsureMarker call, with no reissue requested, still leaves
        // behind.
        ResultJsonFixture.WriteTo(Path.Combine(resumedFolder, "result.json"),
            new ResultJsonFixture(resumedTestId, "pass")
                .WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "no")
                .WithFinishedUtc("2026-09-21T02:00:00.000Z")
                .Build());

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreNotEqual(BannerLevel.None, banner.Level,
            "a second half that finished after an intervening pass, recording the machine not at rest, must never read as clear.");
    }

    private enum NewestOutcome { PassAtRest, PassNotAtRest, PassUnknownAtRest, MissingAtRestFinding, Killed, Unreadable }

    private enum SequenceCondition { Reissued, Stale }

    private static readonly DateTime OlderPassUtc = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TrulyNewestUtc = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);

    // Every shape the run folder that actually happened last can take (a clean pass, a pass that
    // records the machine not at rest three different ways, a kill, or a plain unreadable orphan),
    // crossed with whether its sequence number was brought up to date when it last happened
    // (Reissued, the fix in RunSequence/MainForm) or is still whatever an earlier EnsureMarker call
    // on the very same folder left behind (Stale, the shape a resumed half that nothing reissues
    // still has). The folder that truly happened last keeps the EARLIER of the two stamps
    // throughout (a resumed half never gets a new stamp, only ever a new run folder's worth of
    // sequence and content), the same shape the real bug has; its own real last-write time is
    // pinned explicitly with File.SetLastWriteTimeUtc, never left to whatever the clock reads when
    // this test happens to run. Bad must never read None, whichever way the sequence landed.
    [TestMethod]
    public void TheBannerIsNeverNoneWhenTheTrulyNewestFolderIsBadWhateverItsSequenceSays()
    {
        foreach (NewestOutcome outcome in Enum.GetValues<NewestOutcome>())
        {
            if (outcome == NewestOutcome.PassAtRest)
            {
                // Not part of this invariant: a clean pass reusing an earlier stamp than another
                // run's folder disagrees with EvidenceStore.SequenceDisagreesWithStampOrder's own,
                // separate, already-shipped stamp check whenever it is correctly reissued to a
                // later sequence than that other folder, exactly as a real resume does; that is a
                // pre-existing fail-closed behaviour this sweep does not re-litigate.
                continue;
            }

            foreach (SequenceCondition sequenceCondition in Enum.GetValues<SequenceCondition>())
            {
                using var root = new TempFolder();

                string olderFolder = Path.Combine(root.Path, "20260921T000000Z", "01-a2dp-oneshot");
                ResultJsonFixture.WriteTo(Path.Combine(olderFolder, "result.json"),
                    new ResultJsonFixture("01-a2dp-oneshot", "pass")
                        .WithCriterion("c1", "pass")
                        .WithFinding("leftAtRest", "yes")
                        .WithFinishedUtc(FormatUtc(OlderPassUtc))
                        .Build());
                WriteMarker(olderFolder, 10);

                // The truly-newest folder keeps an EARLIER stamp than olderFolder throughout, the
                // same shape a resumed half really has (it never gets a new stamp, only ever a new
                // sequence and new content): its own folder was created before olderFolder ever
                // ran, at "20260920".
                string newestFolder = Path.Combine(root.Path, "20260920T000000Z", "02-disconnect");
                BuildNewestFolder(newestFolder, outcome);
                WriteMarker(newestFolder, sequenceCondition == SequenceCondition.Reissued ? 99 : 1);

                BannerState banner = Banner.Compute(root.Path);

                Assert.AreNotEqual(BannerLevel.None, banner.Level,
                    "newest=" + outcome + " sequence=" + sequenceCondition +
                    ": a bad newest run must never read as clear, whatever its sequence number says.");
            }
        }
    }

    private static void BuildNewestFolder(string folder, NewestOutcome outcome)
    {
        string finishedUtc = FormatUtc(TrulyNewestUtc);
        switch (outcome)
        {
            case NewestOutcome.PassNotAtRest:
                ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
                    new ResultJsonFixture("02-disconnect", "pass").WithCriterion("c1", "pass")
                        .WithFinding("leftAtRest", "no").WithFinishedUtc(finishedUtc).Build());
                return;
            case NewestOutcome.PassUnknownAtRest:
                ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
                    new ResultJsonFixture("02-disconnect", "pass").WithCriterion("c1", "pass")
                        .WithFinding("leftAtRest", "unknown").WithFinishedUtc(finishedUtc).Build());
                return;
            case NewestOutcome.MissingAtRestFinding:
                ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
                    new ResultJsonFixture("02-disconnect", "pass").WithCriterion("c1", "pass")
                        .WithFinishedUtc(finishedUtc).Build());
                return;
            case NewestOutcome.Killed:
            {
                Directory.CreateDirectory(folder);
                string killedPath = Path.Combine(folder, "gui-killed.txt");
                File.WriteAllText(killedPath, finishedUtc);
                File.SetLastWriteTimeUtc(killedPath, TrulyNewestUtc);
                return;
            }

            case NewestOutcome.Unreadable:
            {
                // Plainly unreadable, not killed and not a stale gui-run-started.txt either: a
                // harmless, unrelated file (never one of the window's own named markers) is all
                // NewestWriteTimeUtc needs to be timed by, since it reads every real file write
                // time in the folder, never the folder's own stamp, once anything sits inside it.
                Directory.CreateDirectory(folder);
                string notePath = Path.Combine(folder, "note.txt");
                File.WriteAllText(notePath, finishedUtc);
                File.SetLastWriteTimeUtc(notePath, TrulyNewestUtc);
                return;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static string FormatUtc(DateTime value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteMarker(string folder, long sequence)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RunSequence.MarkerFileName), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
