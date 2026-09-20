namespace Earshot.TestWindow.Core;

// One entry in Run all's fixed sequence: a row number, and for row 10 which variant.
internal sealed record RunAllItem(string RowNumber, int? Variant)
{
    // The key run-all.json's pointers dictionary and StateDeriver's per-row spec are both keyed
    // by: the row number alone, or "10v3" for variant 3, never the row's own TestId (which
    // 10's parent row does not have one of; ManifestRow.ToVariantSpec's own TestId is what
    // actually appears in a result.json, kept separate from this display/order key on purpose).
    internal string Key => Variant is null ? RowNumber : RowNumber + "v" + Variant.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

// test-gui.md section 11: "Run all: a guided sequence in launcher order, 01 to 15, with 10 as
// five items; 00 is not in it." Restore (00) is the manual escape hatch, never part of the
// sequence it might need to recover from.
internal static class RunAllOrder
{
    internal static readonly IReadOnlyList<RunAllItem> Items = BuildOrder();

    private static List<RunAllItem> BuildOrder()
    {
        var items = new List<RunAllItem>();
        foreach (string number in new[] { "01", "02", "03", "04", "05", "06", "07", "08", "09" })
        {
            items.Add(new RunAllItem(number, null));
        }

        for (int variant = 1; variant <= 5; variant++)
        {
            items.Add(new RunAllItem("10", variant));
        }

        foreach (string number in new[] { "11", "12", "13", "14", "15" })
        {
            items.Add(new RunAllItem(number, null));
        }

        return items;
    }
}
