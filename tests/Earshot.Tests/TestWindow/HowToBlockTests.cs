using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Wording.Load resolves every entry's "howTo" array of names against "howToBlocks" once, at load
// time, so a name that does not exist is caught then rather than read as silently empty the first
// time someone actually reaches that prompt.
[TestClass]
public sealed class HowToBlockTests
{
    private static IReadOnlyList<WordingEntry> LoadRealWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    [TestMethod]
    public void AnEntryNamingAMissingHowToBlockFailsToLoad()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "bad-wording.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "howToBlocks": {
                "real-block": { "picture": null, "steps": ["Do the thing."] }
              },
              "entries": [
                { "test": "01", "kind": "instruction", "scriptText": "Some instruction.", "plain": "Some instruction.", "howTo": ["not-a-real-block"] }
              ]
            }
            """);

        FormatException ex = Assert.ThrowsExactly<FormatException>(() => Wording.Load(path));
        StringAssert.Contains(ex.Message, "not-a-real-block");
    }

    [TestMethod]
    public void RealWordingJsonLoadsWithEveryHowToNameResolved()
    {
        // Loading alone proves every "howTo" name in the shipped data resolves (Load throws
        // otherwise): a name that stops existing, or is misspelled, fails this the moment it is
        // introduced rather than only when a live run happens to reach that exact prompt.
        IReadOnlyList<WordingEntry> wording = LoadRealWording();
        Assert.IsTrue(wording.Count > 0);
    }

    [TestMethod]
    public void EveryPictureNamedInWordingJsonExistsOnDisk()
    {
        string picturesFolder = HowToPictures.FolderPath();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (WordingEntry entry in LoadRealWording())
        {
            foreach (HowToBlock block in entry.HowTo)
            {
                if (block.Picture is null || !seen.Add(block.Picture))
                {
                    continue;
                }

                string path = Path.Combine(picturesFolder, block.Picture + ".png");
                if (!File.Exists(path))
                {
                    missing.Add(block.Name + " names picture '" + block.Picture + "', not found at " + path);
                }
            }
        }

        Assert.IsEmpty(missing, string.Join(Environment.NewLine, missing));
    }

    // Not a failure: a report for the writer of which instruction-shaped entries (question, note,
    // instruction, precondition, action) have no how-to block yet, the same "list what is missing"
    // shape ChecksWithNoPlainNameYetAreListedForTheWriter uses for check plain names. Snapshotted
    // against a fixed expected list, so a genuinely new gap is a deliberate update to this test,
    // never a silent, unnoticed drift.
    [TestMethod]
    public void InstructionsWithNoHowToBlockYetAreListedForTheWriter()
    {
        var actual = new List<string>();
        foreach (WordingEntry entry in LoadRealWording())
        {
            bool instructionShaped = entry.Kind is WordingKind.Question or WordingKind.Note or WordingKind.Instruction
                or WordingKind.Precondition or WordingKind.Action;
            if (instructionShaped && entry.HowTo.Count == 0)
            {
                actual.Add(entry.Test + " (" + entry.Kind + "): " + entry.ScriptText);
            }
        }

        // A first-draft pass, not full coverage: most preconditions and most fit-and-finish
        // questions read fine without a picture (StoredCount below is this file's own record of
        // how many, checked in so a future pass narrowing the gap updates this number on purpose).
        // Test 19 added 38 instruction-shaped entries, 5 with a how-to block (display scaling,
        // light and dark mode, and the three "find the icon or gauge" instructions), so 33 more
        // with none: 139 + 33 = 172. The claim trigger question was later split into two (whether
        // it is there, then whether it reads disabled), one more with no how-to block: 173. Test 22 (the
        // background hand-back) and the hand-back line of test 17 add 11: five preconditions, the listening
        // and waiting actions and three questions of test 22, and the one new precondition of test 17: 184.
        // Test 20 (Exit) adds 11 (five preconditions, three actions, one instruction and two questions) and
        // test 21 (pause on leave) adds 16 (five preconditions, three actions, two instructions and six
        // questions): 211. Test 16 (switching) adds 19 (six preconditions, three actions, four instructions and
        // six questions): 230. Test 19's set-up questions replace two claim questions and add a step and two more
        // questions, three more with no how-to block: 233. Test 19's two gauge positions, the gauge staying on top,
        // the second set-up, the ring and its colour and the one-hour rule add thirteen rows, three of which carry the
        // find-the-icon block, so ten with none, and test 18's first-sleep question one more: 244.
        const int expectedMissingCount = 244;
        Assert.AreEqual(expectedMissingCount, actual.Count,
            "The set of instruction-shaped entries with no how-to block changed size. If this is a deliberate " +
            "improvement (or regression), update expectedMissingCount to match. Current list:" + Environment.NewLine +
            string.Join(Environment.NewLine, actual));
    }
}
