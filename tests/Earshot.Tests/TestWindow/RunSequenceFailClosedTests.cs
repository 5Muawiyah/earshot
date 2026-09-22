using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Two ways RunSequence/EvidenceStore used to read "unknown" as "0" or "no disagreement" instead
// of failing closed.
[TestClass]
public sealed class RunSequenceFailClosedTests
{
    // RunSequence.ReadLastIssued used to swallow a corrupt, truncated or otherwise unreadable
    // counter file to 0, so the very next TakeNext call reissued 1: a number a marker already on
    // disk could already hold, silently. A read failure is never the same claim as "no counter
    // file has ever existed here"; it must be recorded and treated as unknown, never guessed at.
    [TestMethod]
    public void TakeNextNeverRestartsFromZeroWhenTheCounterFileCannotBeRead()
    {
        using var root = new TempFolder();
        RunSequence.TakeNext(root.Path); // 1, a real number already issued.
        RunSequence.TakeNext(root.Path); // 2.

        string counterPath = Path.Combine(root.Path, RunSequence.CounterFileName);
        File.WriteAllText(counterPath, "not json at all");

        // JsonDocument.Parse throws JsonReaderException, a subclass of JsonException, for
        // invalid JSON text: caught by its base type here (ThrowsExactly demands the exact type,
        // which JsonReaderException is not), the same contract EvidenceStore.TryReadResult's own
        // catch (JsonException) already relies on.
        try
        {
            RunSequence.TakeNext(root.Path);
            Assert.Fail("expected a JsonException (or subclass) when the counter file is not valid JSON.");
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    // The same failure reached through the real caller's shape: EnsureMarker for a brand new run
    // folder, with the counter file corrupt. A silent "start from 0" here would hand this folder
    // the number 1, exactly the one already issued above, and Banner/EvidenceStore's own ordering
    // would then have two different run folders sharing one sequence number.
    [TestMethod]
    public void EnsureMarkerNeverAssignsANumberThatCouldCollideWhenTheCounterFileCannotBeRead()
    {
        using var root = new TempFolder();
        RunSequence.TakeNext(root.Path); // 1, already issued and could be sitting on another folder.

        string counterPath = Path.Combine(root.Path, RunSequence.CounterFileName);
        File.WriteAllText(counterPath, "{\"lastIssued\": \"not a number\"}");

        string runFolder = Path.Combine(root.Path, "20260921T000000Z", "01-a2dp-oneshot");
        Assert.ThrowsExactly<InvalidDataException>(() => RunSequence.EnsureMarker(root.Path, runFolder));

        // Fail closed all the way: no marker was written for a number that could not be trusted.
        Assert.IsNull(RunSequence.TryReadMarker(runFolder));
    }

    // EvidenceStore.SequenceDisagreesWithStampOrder used to skip a pair sharing the exact same
    // sequence number rather than treating it as a disagreement: RunSequence.TakeNext never
    // legitimately hands the same value out twice, so two different run folders holding it is
    // itself a sign the count cannot be trusted, not a tie to wave through.
    [TestMethod]
    public void TwoDifferentFoldersSharingTheSameSequenceNumberIsItselfADisagreement()
    {
        var entries = new List<(long? Sequence, string Stamp)>
        {
            (5L, "20260920T000000Z"),
            (5L, "20260921T000000Z"),
        };

        Assert.IsTrue(EvidenceStore.SequenceDisagreesWithStampOrder(entries),
            "two different run folders must never be trusted to share one sequence number silently.");
    }

    // Sanity: the same, single sequence number for what is genuinely the same folder observed
    // once is not itself flagged (only ever compared across at least two distinct entries).
    [TestMethod]
    public void ASingleEntryIsNeverItsOwnDisagreement()
    {
        var entries = new List<(long? Sequence, string Stamp)> { (5L, "20260920T000000Z") };
        Assert.IsFalse(EvidenceStore.SequenceDisagreesWithStampOrder(entries));
    }
}
