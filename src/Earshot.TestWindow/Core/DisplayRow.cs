namespace Earshot.TestWindow.Core;

// One selectable, startable entry in the list: either a whole manifest row, or one of test 10's
// five variants (test-gui.md section 6.1: "row 10 opens into five variant rows, one per
// -Variant, each with its own TestId and its own two halves"). Every row but 10 has exactly one
// DisplayRow of its own; 10 has five, never a text box asking which to run.
internal sealed class DisplayRow
{
    public required ManifestRow Row { get; init; }
    public ManifestVariant? Variant { get; init; }

    public string Number => Variant is null ? Row.Number : Row.Number + "." + Variant.Variant.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Matches RunAllItem.Key exactly (RunAllOrder.cs), so a DisplayRow and the order's own item
    // for the same test always agree on one spelling.
    public string RunAllKey => Variant is null ? Row.Number : Row.Number + "v" + Variant.Variant.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string TestId => Variant?.TestId ?? Row.TestId;

    // Name/Title and Proves/Settles: a plain line first, the script's own words as a secondary
    // line beneath it. Proves is shared across all five of 10's variants (they settle the same
    // overall question); Name is each variant's own.
    public string Name => Variant?.Name ?? Row.Name;
    public string Title => Variant?.Title ?? Row.Title;
    public string Proves => Row.Proves;
    public string Settles => Row.Settles;
    public int Halves => Row.Halves;
    public string Script => Row.Script;
    public PowerCycleRequirement PowerCycleRequirement => Variant?.PowerCycleRequirement ?? Row.PowerCycleRequirement;
    public int VariantNumber => Variant?.Variant ?? 0;

    public TestRowSpec ToSpec() => Variant is null ? Row.ToSpec() : Row.ToVariantSpec(Variant);

    // Section 6.1: "Variant 4 waits on Windows Update offering a restart; the row says so." The
    // only variant whose physical action is not fully in the owner's own hands.
    public bool WaitsOnWindowsUpdate => Variant is { Variant: 4 };

    public static IReadOnlyList<DisplayRow> Flatten(IReadOnlyList<ManifestRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<DisplayRow>();
        foreach (ManifestRow row in rows)
        {
            if (row.Variants is { Count: > 0 } variants)
            {
                foreach (ManifestVariant variant in variants)
                {
                    list.Add(new DisplayRow { Row = row, Variant = variant });
                }
            }
            else
            {
                list.Add(new DisplayRow { Row = row });
            }
        }

        return list;
    }
}
