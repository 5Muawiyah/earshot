namespace Earshot.TestWindow.Core;

// One variant of test 10 (test-gui.md section 6.1): its own TestId, its own plain name and its
// own two halves.
internal sealed record ManifestVariant(int Variant, string TestId, string Title, PowerCycleRequirement PowerCycleRequirement);

// One row of Data\tests.json: everything section 6.1's manifest table carries for one test,
// enough to build the TestRowSpec StateDeriver needs.
internal sealed class ManifestRow
{
    public required string Number { get; init; }
    public required string Script { get; init; }
    public required string TestId { get; init; }
    public required string Kind { get; init; }
    public bool ElevatedVariant { get; init; }
    public required int Halves { get; init; }

    // The plain name and the one line saying what the test proves, both the script's own words:
    // Title and Settles exactly as the script passes them to New-LiveTestRun. Never paraphrased
    // here (a row must show a name and a line, not the TestId alone).
    public required string Title { get; init; }
    public required string Settles { get; init; }
    public PowerCycleRequirement PowerCycleRequirement { get; init; } = PowerCycleRequirement.None;
    public int MaxSilenceSeconds { get; init; } = 900;
    public IReadOnlyList<string> FirstHalfOnlyCriteriaIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SecondHalfOnlyCriteriaIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FirstHalfOnlyFindingNames { get; init; } = Array.Empty<string>();
    public bool FirstHalfAloneIsCompleteWhenDeclined { get; init; }

    // Only test 10 has these: one sub-row per variant, section 6.1: "row 10 opens into five
    // variant rows, one per -Variant, each with its own TestId and its own two halves".
    public IReadOnlyList<ManifestVariant>? Variants { get; init; }

    // The spec StateDeriver needs for this row's own TestId. For test 10, use ToVariantSpec
    // instead: this row's own TestId ("10-shutdown-messages") never appears in any result.json.
    public TestRowSpec ToSpec() => new()
    {
        TestId = TestId,
        Halves = Halves,
        PowerCycleRequirement = PowerCycleRequirement,
        FirstHalfOnlyCriteriaIds = FirstHalfOnlyCriteriaIds,
        SecondHalfOnlyCriteriaIds = SecondHalfOnlyCriteriaIds,
        FirstHalfOnlyFindingNames = FirstHalfOnlyFindingNames,
        FirstHalfAloneIsCompleteWhenDeclined = FirstHalfAloneIsCompleteWhenDeclined,
    };

    public TestRowSpec ToVariantSpec(ManifestVariant variant) => new()
    {
        TestId = variant.TestId,
        Halves = Halves,
        PowerCycleRequirement = variant.PowerCycleRequirement,
        FirstHalfOnlyCriteriaIds = FirstHalfOnlyCriteriaIds,
        SecondHalfOnlyCriteriaIds = SecondHalfOnlyCriteriaIds,
        FirstHalfOnlyFindingNames = FirstHalfOnlyFindingNames,
        FirstHalfAloneIsCompleteWhenDeclined = FirstHalfAloneIsCompleteWhenDeclined,
    };
}
