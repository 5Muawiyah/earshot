namespace Earshot.TestWindow.Core;

// One row of the failure list: which criterion, what was expected, what was observed.
internal sealed record FailureRow(string Id, string Expected, string Observed);

// One plain line for a check that did not pass: PlainLine (the check's own plain name, or a
// neutral fallback when wording.json has no entry for it yet) and, when wording.json wrote one,
// PlainMeaning, a second line saying what that failing (or not settling) actually means. Outcome
// ("fail" or "inconclusive", never "pass": these are only ever built for a check that is not one)
// is the criterion's own raw outcome, kept alongside the words rather than folded into them, so
// the caller that renders this line can mark it (a cross or a question mark, "Did not work:" or
// "Could not tell:") without needing to parse PlainLine's own words back apart to tell which.
internal sealed record PlainCheckLine(string CriterionId, string PlainLine, string? PlainMeaning, bool HasWordingEntry, string Outcome);

internal sealed record ResultPresentation
{
    public required IReadOnlyList<FailureRow> Failures { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    // The plain-mode summary: a short sentence (built by Ui.Copy from these three counts) followed
    // by one PlainCheckLine per check that did not pass, fail-first, the same order as Failures.
    public int TotalCriteria { get; init; }
    public int FailedCount { get; init; }
    public int InconclusiveCount { get; init; }
    public required IReadOnlyList<PlainCheckLine> PlainFailureLines { get; init; }

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
// first failed first: which, expected, observed, evidence. wording is optional (a caller with none
// to hand, or none loaded yet, still gets the technical Failures list; every PlainFailureLines
// entry then falls back to the neutral line, never throws).
internal static class ResultPresenter
{
    internal static ResultPresentation Present(
        ParsedResult result, string folder, string? testNumber = null, IReadOnlyList<WordingEntry>? wording = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(folder);

        List<CriterionRecord> notPassed = result.Criteria.Where(c => c.Outcome != "pass").ToList();
        List<FailureRow> failures = notPassed
            .OrderBy(c => c.Outcome == "fail" ? 0 : 1)
            .Select(c => new FailureRow(c.Id, c.Criterion, c.Detail))
            .ToList();

        List<PlainCheckLine> plainLines = notPassed
            .OrderBy(c => c.Outcome == "fail" ? 0 : 1)
            .Select(c => BuildPlainCheckLine(c, testNumber, wording))
            .ToList();

        FindingRecord? leftAtRestFinding = result.Findings.FirstOrDefault(f => f.Name == "leftAtRest");

        return new ResultPresentation
        {
            Failures = failures,
            Errors = result.Errors,
            TotalCriteria = result.Criteria.Count,
            FailedCount = result.Criteria.Count(c => c.Outcome == "fail"),
            InconclusiveCount = result.Criteria.Count(c => c.Outcome != "pass" && c.Outcome != "fail"),
            PlainFailureLines = plainLines,
            LeftAtRest = leftAtRestFinding?.Value,
            LeftAtRestDetail = leftAtRestFinding?.Detail,
            HasDeclinedElevatedStep = result.HasDeclinedElevatedStep,
            EvidenceFolder = folder,
            ResultJsonPath = Path.Combine(folder, "result.json"),
            SummaryTxtPath = Path.Combine(folder, "summary.txt"),
        };
    }

    // A check with no wording.json entry falls back to a neutral line rather than the script's own
    // words (the criterion text can itself be technical), so this never silently leaks jargon; the
    // outcome (fail vs anything else) still shapes which neutral sentence is used, so "could not
    // tell" and "did not work" are never conflated even without a dedicated entry.
    private static PlainCheckLine BuildPlainCheckLine(CriterionRecord criterion, string? testNumber, IReadOnlyList<WordingEntry>? wording)
    {
        WordingEntry? entry = testNumber is null || wording is null
            ? null
            : Wording.Find(wording, testNumber, WordingKind.Check, criterion.Id);

        if (entry is not null)
        {
            return new PlainCheckLine(criterion.Id, entry.Plain, entry.PlainMeaning, HasWordingEntry: true, Outcome: criterion.Outcome);
        }

        string neutral = criterion.Outcome == "fail"
            ? "One check did not work. Show technical details to see which."
            : "We could not tell for one check. Show technical details to see which.";
        return new PlainCheckLine(criterion.Id, neutral, null, HasWordingEntry: false, Outcome: criterion.Outcome);
    }
}
