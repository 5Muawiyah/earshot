namespace Earshot.TestWindow.Core;

// One criterion as Add-Criterion wrote it: id, criterion, outcome (pass/fail/inconclusive) and a
// detail string.
internal sealed record CriterionRecord(string Id, string Criterion, string Outcome, string Detail);

// One finding as Add-Finding wrote it. Value is null when the script recorded "not measured"
// (Add-Finding's own [AllowNull] convention); RawValue keeps whatever JSON shape the value had
// (a script can write a bool or a number, not only a string) for callers that only need to show
// it, while Value gives the string a caller doing exact comparisons (leftAtRest) wants.
internal sealed record FindingRecord(string Name, string? Value, string Detail);

// A result.json that passed every fail-closed check in test-gui.md section 6.2: the test id
// matches its folder, overall is exactly pass/fail/inconclusive, and it agrees with itself once
// recomputed from its own criteria.
internal sealed class ParsedResult
{
    public required string Test { get; init; }
    public required string Overall { get; init; }
    public required IReadOnlyList<CriterionRecord> Criteria { get; init; }
    public required IReadOnlyList<FindingRecord> Findings { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public required int StepCount { get; init; }
    public string? Exe { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }

    // Section 6.2 rule 5: a criterion with id "run" means the script stopped early.
    public bool StoppedEarly => Criteria.Any(c => string.Equals(c.Id, "run", StringComparison.Ordinal));

    // Section 6.2's own definition of "stopped before any step": no criteria, no steps.
    // A well-formed, honestly empty result, not a parse failure.
    public bool IsStoppedBeforeAnyStep => Criteria.Count == 0 && StepCount == 0;

    public string? LeftAtRest => Findings.FirstOrDefault(f => f.Name == "leftAtRest")?.Value;
}

// Which half a result.json's criteria belong to, decided from the half markers in
// test-gui.md section 6.1, never from a member result.json does not have (it names no half).
internal enum HalfKind
{
    // Single-half test, or a two-half result this run could not place (do not trust it as either
    // half; it is treated the same as a run with no criteria for selection purposes).
    NotApplicable,
    First,
    Second,
}

// One run folder's evidence for one test: what result.json (if any) held, the first-half
// snapshot the window takes at the power-cycle boundary (section 9.1), whether resume.txt and
// gui-set-aside.txt are present, and the power-cycle verdict recorded before a second half ran
// (section 9.3). ReadFailureReason is set, and Result is null, exactly when the fail-closed
// checks in section 6.2 could not validate result.json; in every such case the row this
// evidence feeds is Unknown.
internal sealed class RunEvidence
{
    public required string Stamp { get; init; }
    public required string Folder { get; init; }
    public ParsedResult? Result { get; init; }
    public string? ReadFailureReason { get; init; }
    public ParsedResult? FirstHalfSnapshot { get; init; }
    public bool HasFirstHalfSnapshotFile { get; init; }
    public bool HasResumeFile { get; init; }
    public bool HasSetAsideFile { get; init; }

    // section 8.3: the window's own marker, written only when it has forcibly killed the child
    // that owned this run folder. gui- prefixed, additive (section 8.4); never written by any
    // shipped script.
    public bool HasKilledMarker { get; init; }
    public string? PowerCycleVerdict { get; init; }

    public bool ReadSucceeded => Result is not null;
}
