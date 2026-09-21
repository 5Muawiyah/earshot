namespace Earshot.TestWindow.Core;

// One row of the failure list: which criterion, what was expected, what was observed.
internal sealed record FailureRow(string Id, string Expected, string Observed);

internal sealed record ResultPresentation
{
    public required IReadOnlyList<FailureRow> Failures { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    // The raw leftAtRest value and its detail (the no-on-purpose reason), never the display
    // text: that mapping is Ui.Copy.LeftAtRestText's job, kept in the Ui layer with every other
    // string the owner sees. Null means no such finding was recorded.
    public string? LeftAtRest { get; init; }
    public string? LeftAtRestDetail { get; init; }

    // A step with elevated true, ran false and an error is derived here, from result.json alone;
    // Ui.Copy.DeclinedElevatedPrompt is the one place its words live.
    public bool HasDeclinedElevatedStep { get; init; }
    public required string EvidenceFolder { get; init; }
    public required string ResultJsonPath { get; init; }
    public required string SummaryTxtPath { get; init; }
}

// Builds what ResultPanel shows, from result.json alone. For every criterion that is not a pass,
// first failed first: which, expected, observed, evidence.
internal static class ResultPresenter
{
    internal static ResultPresentation Present(ParsedResult result, string folder)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(folder);

        List<FailureRow> failures = result.Criteria
            .Where(c => c.Outcome != "pass")
            .OrderBy(c => c.Outcome == "fail" ? 0 : 1)
            .Select(c => new FailureRow(c.Id, c.Criterion, c.Detail))
            .ToList();

        FindingRecord? leftAtRestFinding = result.Findings.FirstOrDefault(f => f.Name == "leftAtRest");

        return new ResultPresentation
        {
            Failures = failures,
            Errors = result.Errors,
            LeftAtRest = leftAtRestFinding?.Value,
            LeftAtRestDetail = leftAtRestFinding?.Detail,
            HasDeclinedElevatedStep = result.HasDeclinedElevatedStep,
            EvidenceFolder = folder,
            ResultJsonPath = Path.Combine(folder, "result.json"),
            SummaryTxtPath = Path.Combine(folder, "summary.txt"),
        };
    }
}
