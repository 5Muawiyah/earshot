using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Every row carries a plain row name, with the script header's own title kept as a secondary
// line, and a "what it proves" line in plain words with the facts unchanged. Proves is not
// invented: the manifest's own table already carries this exact "What it proves (row copy)"
// column, marked binding copy, so every row's Proves value here is that literal text, checked
// against it. Name has no such table (it is hand-written), so it is checked only for being
// present, plain, and never the bare TestId.
[TestClass]
public sealed class ManifestNameProvesTests
{
    // The manifest's own table, column "What it proves (row copy)", copied exactly.
    private static readonly Dictionary<string, string> ExpectedProves = new(StringComparer.Ordinal)
    {
        ["00"] = "Puts this PC back to a known state. Run it whenever a test stops early.",
        ["01"] = "Earshot can connect the AirPods to this PC.",
        ["02"] = "Earshot can hand the AirPods back to the phone.",
        ["03"] = "Unblocking alone does not pull the AirPods off the phone.",
        ["04"] = "The block is still there after a restart.",
        ["05"] = "Unblocking works and the sound devices come back.",
        ["06"] = "Call protection can be switched on and off.",
        ["07"] = "The tray can start its background task with no administrator prompt.",
        ["08"] = "ACCEPTANCE. After a full shut down and start, the AirPods stay on the phone.",
        ["09"] = "Shutting down while connected does not make this PC grab the AirPods at the next start.",
        ["10"] = "What Windows tells Earshot during each kind of restart.",
        ["11"] = "There is still no battery reading, connected or not.",
        ["12"] = "How Windows reports sound device changes to Earshot.",
        ["13"] = "The wait before Earshot blocks again is about right.",
        ["14"] = "Earshot refuses to treat a phone as the AirPods.",
        ["15"] = "Uninstall puts everything back, and install sets it up again.",
    };

    private static IReadOnlyList<ManifestRow> LoadManifest() =>
        Manifest.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "tests.json"));

    [TestMethod]
    public void EveryRowsProvesMatchesTheSpecsOwnBindingCopyExactly()
    {
        foreach (ManifestRow row in LoadManifest())
        {
            Assert.IsTrue(ExpectedProves.TryGetValue(row.Number, out string? expected), "no expected Proves fixture for row " + row.Number);
            Assert.AreEqual(expected, row.Proves, "row " + row.Number);
        }
    }

    [TestMethod]
    public void EveryRowHasAPlainNameThatIsNeverTheBareTestIdOrScriptFileName()
    {
        foreach (ManifestRow row in LoadManifest())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Name), "row " + row.Number);
            Assert.AreNotEqual(row.TestId, row.Name, "row " + row.Number);
            Assert.AreNotEqual(row.Script, row.Name, "row " + row.Number);
        }
    }

    [TestMethod]
    public void Row01sNameMatchesTheCoordinatorsOwnExample()
    {
        ManifestRow row01 = LoadManifest().Single(r => r.Number == "01");
        Assert.AreEqual("Connect with one click", row01.Name);
    }

    [TestMethod]
    public void EveryOneOfTensFiveVariantsHasItsOwnDistinctPlainName()
    {
        ManifestRow row10 = LoadManifest().Single(r => r.Number == "10");
        Assert.IsNotNull(row10.Variants);
        var names = row10.Variants!.Select(v => v.Name).ToList();
        Assert.AreEqual(5, names.Count);
        Assert.AreEqual(5, names.Distinct(StringComparer.Ordinal).Count(), "two variants share a plain name");
        Assert.IsTrue(names.All(n => !string.IsNullOrWhiteSpace(n)));
    }

    [TestMethod]
    public void ADisplayRowsNameAndProvesAreNeverEmptyForAnyOfTheTwentyRows()
    {
        foreach (DisplayRow row in DisplayRow.Flatten(LoadManifest()))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Name), row.Number);
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Proves), row.Number);
        }
    }
}
