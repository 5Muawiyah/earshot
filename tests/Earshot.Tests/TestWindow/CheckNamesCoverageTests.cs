using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The "Check names" job: wording.json's Check entries are the only thing standing between a
// failing or inconclusive result and ResultPresenter's neutral fallback ("One check did not
// work. Show technical details to see which."). This file proves two things about that set of
// entries, both from expectations.psd1's own "one" case, read the same way
// AllScriptsAndHalvesThroughWindowTests does (through SelfTestFixtures.LoadExpectations, never a
// hand-kept copy):
//
//   1. every existing Check entry names a criterion id a script can really record for that test
//      (EveryCheckEntryNamesARealCriterionId, the one real, build-failing assertion this file
//      owns), and
//   2. every criterion id a script can really record and has no Check entry yet is printed, in
//      full, to the test's own output, so a writer can work through the list directly
//      (MissingCheckEntriesAreReported, which never fails the build on a gap: the neutral
//      fallback already covers a missing entry correctly, so an incomplete list is not a defect).
//
// The universal "run" id (see RunCriterionId below) is the one deliberate exception to "read
// straight off the exporter": every one of the sixteen shipped scripts' own catch block declares
// it, with the same meaning every time, confirmed by reading a sample of them (00, 01, 08, 12,
// 15), but expectations.psd1's "one" case is a clean run, so the exporter this file otherwise
// trusts as its one source of truth never reaches it and it is absent from every
// ExpectationsPlanRow.Criteria key set. Treating that absence literally would mark test 01's own
// pre-existing "run" Check entry stale on an unmodified checkout, which is not what "a script
// cannot really record this id" means for "run"; the id is named here by hand, once, rather than
// by widening what counts as "recorded" for every other id too.
[TestClass]
public sealed class CheckNamesCoverageTests
{
    private const string RunCriterionId = "run";

    private static IReadOnlyList<WordingEntry> LoadWording(string repoRoot) =>
        Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));

    // The one real assertion: a Check entry whose (test, criterion id) pair is not in the
    // recordable set below is stale or typo'd, and fails the build, naming exactly which entries
    // are wrong.
    [TestMethod]
    public void EveryCheckEntryNamesARealCriterionId()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ", so this settles nothing.");
        }

        string repoRoot = RepositoryLocator.RepositoryRoot();
        Dictionary<string, HashSet<string>> idsByRow = LoadRecordableIdsByRow(repoRoot, host);
        IReadOnlyList<WordingEntry> wording = LoadWording(repoRoot);

        var stale = new List<string>();
        foreach (WordingEntry entry in wording)
        {
            if (entry.Kind != WordingKind.Check)
            {
                continue;
            }

            bool known = idsByRow.TryGetValue(entry.Test, out HashSet<string>? ids) && ids.Contains(entry.ScriptText);
            if (!known)
            {
                stale.Add(
                    "test \"" + entry.Test + "\" names the check id \"" + entry.ScriptText + "\", which no script " +
                    "records for that test in expectations.psd1's \"one\" case (and it is not the universal \"" +
                    RunCriterionId + "\" id).");
            }
        }

        List<string> sorted = stale.OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.IsTrue(
            sorted.Count == 0,
            "wording.json has " + sorted.Count + " stale check entr" + (sorted.Count == 1 ? "y" : "ies") + ":" +
            Environment.NewLine + string.Join(Environment.NewLine, sorted));
    }

    // The coverage report: never fails on a missing entry, but always prints the full list, so a
    // normal gate run surfaces exactly what is left for the next writing pass. The one soft
    // assertion here is a regression floor, not a completion target: coverage must never go
    // backwards (an entry silently deleted), but an incomplete list is correct and must never be
    // failed on, per the spec this test implements.
    [TestMethod]
    public void MissingCheckEntriesAreReported()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ", so this settles nothing.");
        }

        string repoRoot = RepositoryLocator.RepositoryRoot();
        Dictionary<string, HashSet<string>> idsByRow = LoadRecordableIdsByRow(repoRoot, host);
        IReadOnlyList<WordingEntry> wording = LoadWording(repoRoot);

        int total = 0;
        var missing = new List<(string Row, string Id)>();
        foreach ((string row, HashSet<string> ids) in idsByRow)
        {
            foreach (string id in ids)
            {
                total++;
                if (Wording.Find(wording, row, WordingKind.Check, id) is null)
                {
                    missing.Add((row, id));
                }
            }
        }

        List<(string Row, string Id)> sortedMissing = missing
            .OrderBy(m => m.Row, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        int covered = total - missing.Count;

        Console.WriteLine(
            "CheckNamesCoverageTests: " + total + " recordable criterion id(s) across every test, " +
            covered + " with a plain check entry, " + missing.Count + " without one.");
        Console.WriteLine("Missing plain check entries (row, criterion id), for the next writing pass:");
        foreach ((string row, string id) in sortedMissing)
        {
            Console.WriteLine("\"" + row + "\" \"" + id + "\"");
        }

        // A floor, not a target: raised only when a writing pass adds entries, never asserting a
        // percentage or an exact count that would go stale the moment someone adds one more.
        Assert.IsTrue(
            covered >= MinimumCoveredCount,
            "Coverage dropped to " + covered + " covered id(s), fewer than the " + MinimumCoveredCount +
            " a previous pass already reached. Something that had a plain check entry lost it.");
    }

    // Raised each time a writing pass adds entries; never asserted as a target, only as a floor
    // (see MissingCheckEntriesAreReported's own comment). 110 is what this pass leaves in place,
    // out of 119 recordable ids in total: the 8 pre-existing entries, plus a "run" entry for every
    // one of the sixteen rows, plus a first-draft plain name for most of the remaining ids (see
    // wording.json); 9 ids, all in test 08, are left for a later pass.
    private const int MinimumCoveredCount = 110;

    // Every criterion id a script can really record, for every "<TestId>|<half>" key
    // expectations.psd1's "one" case gives (SelfTestFixtures.LoadExpectations, the same exporter
    // AllScriptsAndHalvesThroughWindowTests already trusts), grouped by the manifest row Number
    // wording.json's own "test" field is keyed on. For test 10's five variants, that is always the
    // BASE row's Number ("10"), never a variant number ("10.1"): wording.json's own existing
    // entries for test 10 already use "10" throughout (its precondition/action entries), so this
    // reads displayRow.Row.Number rather than displayRow.Number (DisplayRow.cs's own
    // variant-suffixed property) to follow that same convention rather than inventing a new one.
    // Only variant 1 is ever in this data at all (Invoke-SelfTest.ps1's own $tests table only
    // drives variant 1; AllScriptsAndHalvesThroughWindowTests' own header comment records the same
    // fact for the same reason), so variants 2 to 5 contribute nothing here regardless.
    private static Dictionary<string, HashSet<string>> LoadRecordableIdsByRow(string repoRoot, string host)
    {
        IReadOnlyDictionary<string, ExpectationsPlanRow> expectations = SelfTestFixtures.LoadExpectations(repoRoot, host);
        IReadOnlyList<ManifestRow> manifestRows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        Dictionary<string, DisplayRow> displayRowsByTestId = DisplayRow.Flatten(manifestRows)
            .GroupBy(row => row.TestId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var idsByRow = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (ManifestRow row in manifestRows)
        {
            // "run" is real and universal (see this class's own header comment) and is added for
            // every row up front, not only for rows the loop below happens to touch.
            idsByRow[row.Number] = new HashSet<string>(StringComparer.Ordinal) { RunCriterionId };
        }

        foreach (ExpectationsPlanRow planRow in expectations.Values)
        {
            int separator = planRow.Key.IndexOf('|', StringComparison.Ordinal);
            string testId = separator < 0 ? planRow.Key : planRow.Key[..separator];
            if (!displayRowsByTestId.TryGetValue(testId, out DisplayRow? displayRow))
            {
                // Nothing in the current manifest owns this TestId (a stale expectations.psd1 key
                // outliving a manifest row, or a variant the manifest no longer lists): there is no
                // row Number to file its criteria under, so it contributes nothing here. Every
                // TestId expectations.psd1 names today does resolve, so this branch is not reached
                // by the current data; it exists so a future mismatch is silently inert here
                // rather than a crash, leaving AllScriptsAndHalvesThroughWindowTests as the one
                // place that already fails loudly on exactly this mismatch.
                continue;
            }

            HashSet<string> ids = idsByRow[displayRow.Row.Number];
            foreach (string id in planRow.Criteria.Keys)
            {
                ids.Add(id);
            }
        }

        return idsByRow;
    }
}
