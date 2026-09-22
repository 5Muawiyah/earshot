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
        const int expectedMissingCount = 139;
        Assert.AreEqual(expectedMissingCount, actual.Count,
            "The set of instruction-shaped entries with no how-to block changed size. If this is a deliberate " +
            "improvement (or regression), update expectedMissingCount to match. Current list:" + Environment.NewLine +
            string.Join(Environment.NewLine, actual));
    }
}
