using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A newer unreadable run must never let an older pass show through. Before
// this fix, StateDeriver.FindVerdictRun's loop treated an unreadable newest run as something to
// skip past on the way to an older, readable verdict; now the newest run's own read failure is
// checked first, and nothing older is ever consulted while it stands.
[TestClass]
public sealed class StateDeriverNewestUnreadableTests
{
    private static IReadOnlyList<RunEvidence> Load(string root, string testId) => EvidenceStore.LoadEvidence(root, testId);

    [TestMethod]
    public void ANewerEmptyResultFolderLowersAnOlderGenuinePassToUnknown()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));

        // A newer attempt whose result.json is missing entirely (killed before it could write
        // one, or truncated away): no criteria, no readable overall.
        Directory.CreateDirectory(Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
        Assert.IsFalse(state.IsGreen);
    }

    [TestMethod]
    public void ANewerTruncatedResultJsonLowersAnOlderGenuinePassToUnknown()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));

        string newerFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(newerFolder);
        File.WriteAllText(Path.Combine(newerFolder, "result.json"), "{\"test\":\"01-a2dp-oneshot\",\"overall\":\"p");

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
    }

    [TestMethod]
    public void ANewerResultForTheWrongTestLowersAnOlderGenuinePassToUnknown()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));

        // A stray file for the wrong test id landed in this folder: EvidenceStore's own rule 2
        // rejects it, and that rejection must not be shrugged off in favour of the older pass.
        string newerFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(newerFolder, "result.json"),
            new ResultJsonFixture("02-disconnect", "pass").WithCriterion("c1", "pass").Build());

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
    }

    [TestMethod]
    public void TheHistoryNoteNamesTheUnreadableFolder()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));
        string newerFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(newerFolder);

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);

        StringAssert.Contains(state.HistoryNote, newerFolder);
    }

    [TestMethod]
    public void AnHonestlyEmptyNewestRunStillFallsThroughToAnOlderPassUnlikeAGenuineReadFailure()
    {
        // A well-formed, honestly empty "stopped before any step" result
        // (declined at preconditions, say) is not a read failure, and is already let
        // through to an older verdict. This pins that the newest-unreadable fix did not remove that.
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "inconclusive"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);

        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsTrue(state.IsGreen);
    }

    private static string WriteResult(TempFolder root, string stamp, string testId, ResultJsonFixture fixture)
    {
        string folder = Path.Combine(root.Path, stamp, testId);
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), fixture.Build());
        return folder;
    }
}
