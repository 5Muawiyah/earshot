using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Row 10 opens into five variant rows, and reads "n of 5 recorded".
// DisplayRow.Flatten is what turns the 18-row manifest into the 22 real, clickable
// entries this window shows (17 ordinary rows plus 5 variants), never a text box for picking one.
[TestClass]
public sealed class DisplayRowTests
{
    private static readonly string[] ExpectedVariantNumbers = { "10.1", "10.2", "10.3", "10.4", "10.5" };
    private static readonly string[] ExpectedVariantRunAllKeys = { "10v1", "10v2", "10v3", "10v4", "10v5" };

    private static IReadOnlyList<ManifestRow> Rows() =>
        Manifest.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "tests.json"));

    [TestMethod]
    public void FlattenProducesTwentyTwoRowsSeventeenOrdinaryPlusFiveVariants()
    {
        IReadOnlyList<DisplayRow> displayRows = DisplayRow.Flatten(Rows());
        Assert.AreEqual(22, displayRows.Count);
    }

    [TestMethod]
    public void NoDisplayRowIsEverMissingRow10ItselfNeverAppearsAlone()
    {
        IReadOnlyList<DisplayRow> displayRows = DisplayRow.Flatten(Rows());
        Assert.IsFalse(displayRows.Any(r => r.Row.Number == "10" && r.Variant is null),
            "Row 10 must never appear as a single, un-chosen entry: only its five variants do.");
        Assert.AreEqual(5, displayRows.Count(r => r.Row.Number == "10"));
    }

    [TestMethod]
    public void EveryOtherManifestRowHasExactlyOneDisplayRowOfItsOwn()
    {
        IReadOnlyList<ManifestRow> rows = Rows();
        IReadOnlyList<DisplayRow> displayRows = DisplayRow.Flatten(rows);
        foreach (ManifestRow row in rows.Where(r => r.Number != "10"))
        {
            Assert.AreEqual(1, displayRows.Count(d => d.Row.Number == row.Number), "row " + row.Number);
        }
    }

    [TestMethod]
    public void Test10sVariantsAreNumberedTenDotOneThroughTenDotFive()
    {
        IReadOnlyList<DisplayRow> variants = DisplayRow.Flatten(Rows()).Where(r => r.Row.Number == "10").OrderBy(r => r.VariantNumber).ToList();
        CollectionAssert.AreEqual(ExpectedVariantNumbers, variants.Select(r => r.Number).ToArray());
    }

    [TestMethod]
    public void Test10sVariantsRunAllKeysMatchTheKeyRunAllOrderItselfUses()
    {
        IReadOnlyList<DisplayRow> variants = DisplayRow.Flatten(Rows()).Where(r => r.Row.Number == "10").OrderBy(r => r.VariantNumber).ToList();
        CollectionAssert.AreEqual(ExpectedVariantRunAllKeys, variants.Select(r => r.RunAllKey).ToArray());

        // Every RunAllOrder item for row 10 has a matching DisplayRow by that same key, so
        // AdvanceRunAll's lookup can never silently skip one.
        foreach (RunAllItem item in RunAllOrder.Items.Where(i => i.RowNumber == "10"))
        {
            Assert.IsTrue(variants.Any(v => v.RunAllKey == item.Key), "no DisplayRow for " + item.Key);
        }
    }

    [TestMethod]
    public void EachVariantsTestIdIsItsOwnNeverTheParentRows()
    {
        IReadOnlyList<DisplayRow> variants = DisplayRow.Flatten(Rows()).Where(r => r.Row.Number == "10").ToList();
        foreach (DisplayRow variant in variants)
        {
            Assert.AreNotEqual("10-shutdown-messages", variant.TestId);
            StringAssert.StartsWith(variant.TestId, "10-shutdown-messages-v");
            StringAssert.EndsWith(variant.TestId, variant.VariantNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void OnlyVariantFourWaitsOnWindowsUpdate()
    {
        IReadOnlyList<DisplayRow> variants = DisplayRow.Flatten(Rows()).Where(r => r.Row.Number == "10").ToList();
        foreach (DisplayRow variant in variants)
        {
            Assert.AreEqual(variant.VariantNumber == 4, variant.WaitsOnWindowsUpdate, "variant " + variant.VariantNumber);
        }
    }

    [TestMethod]
    public void ANonVariantRowsToSpecMatchesItsOwnToSpec()
    {
        ManifestRow row = Rows().Single(r => r.Number == "01");
        DisplayRow display = DisplayRow.Flatten(new[] { row }).Single();
        TestRowSpec expected = row.ToSpec();
        TestRowSpec actual = display.ToSpec();
        Assert.AreEqual(expected.TestId, actual.TestId);
        Assert.AreEqual(expected.Halves, actual.Halves);
    }

    [TestMethod]
    public void AVariantsToSpecMatchesToVariantSpecForThatSameVariant()
    {
        ManifestRow row = Rows().Single(r => r.Number == "10");
        DisplayRow display = DisplayRow.Flatten(new[] { row }).Single(d => d.VariantNumber == 3);
        TestRowSpec expected = row.ToVariantSpec(row.Variants!.Single(v => v.Variant == 3));
        TestRowSpec actual = display.ToSpec();
        Assert.AreEqual(expected.TestId, actual.TestId);
        Assert.AreEqual("10-shutdown-messages-v3", actual.TestId);
    }
}
