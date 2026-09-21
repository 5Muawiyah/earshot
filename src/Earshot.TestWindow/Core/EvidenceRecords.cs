namespace Earshot.TestWindow.Core;

// One criterion as Add-Criterion wrote it: id, criterion, outcome (pass/fail/inconclusive) and a
// detail string.
internal sealed record CriterionRecord(string Id, string Criterion, string Outcome, string Detail);

// One finding as Add-Finding wrote it. Value is null when the script recorded "not measured"
// (Add-Finding's own [AllowNull] convention); RawValue keeps whatever JSON shape the value had
// (a script can write a bool or a number, not only a string) for callers that only need to show
// it, while Value gives the string a caller doing exact comparisons (leftAtRest) wants.
internal sealed record FindingRecord(string Name, string? Value, string Detail);

// One step as Invoke-Earshot/Invoke-EarshotElevated recorded it. Elevated true, Ran false and a
// non-empty Error means the owner chose No on the Windows permission box, derived from
// result.json alone, nothing this window observed while the step ran.
internal sealed record StepRecord(bool Elevated, bool Ran, string? Error);

// A result.json that passed every fail-closed check: the test id matches its folder, overall is
// exactly pass/fail/inconclusive, and it agrees with itself once recomputed from its own criteria.
internal sealed class ParsedResult
{
    public required string Test { get; init; }
    public required string Overall { get; init; }
    public required IReadOnlyList<CriterionRecord> Criteria { get; init; }
    public required IReadOnlyList<FindingRecord> Findings { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public required int StepCount { get; init; }
    public IReadOnlyList<StepRecord> Steps { get; init; } = Array.Empty<StepRecord>();

    // Derived from result.json only: a step with elevated true, ran false and an error. The
    // declined-prompt copy is Ui.Copy's own job to render; this is the fact alone.
    public bool HasDeclinedElevatedStep => Steps.Any(s => s.Elevated && !s.Ran && !string.IsNullOrEmpty(s.Error));
    public string? Exe { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }

    // One of the fail-closed rules for reading result.json: a criterion with id "run" means
    // the script stopped early.
    public bool StoppedEarly => Criteria.Any(c => string.Equals(c.Id, "run", StringComparison.Ordinal));

    // "Stopped before any step" means no criteria and no steps recorded.
    // A well-formed, honestly empty result, not a parse failure.
    public bool IsStoppedBeforeAnyStep => Criteria.Count == 0 && StepCount == 0;

    public string? LeftAtRest => Findings.FirstOrDefault(f => f.Name == "leftAtRest")?.Value;
}

// Which half a result.json's criteria belong to, decided from the manifest's own half markers,
// never from a member result.json does not have (it names no half).
internal enum HalfKind
{
    // Single-half test, or a two-half result this run could not place (do not trust it as either
    // half; it is treated the same as a run with no criteria for selection purposes).
    NotApplicable,
    First,
    Second,
}

// One run folder's evidence for one test: what result.json (if any) held, the first-half
// snapshot the window takes before shutting down or restarting at the power-cycle boundary,
// whether resume.txt and gui-set-aside.txt are present, and the power-cycle verdict recorded
// before a second half ran. ReadFailureReason is set, and Result is null, exactly when the
// fail-closed checks that read result.json could not validate it; in every such case the row
// this evidence feeds is Unknown.
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

    // The window's own marker, written only when it has forcibly killed the child
    // that owned this run folder. gui- prefixed and additive to result.json's own fields; never
    // written by any shipped script.
    public bool HasKilledMarker { get; init; }

    // True when gui-run-started.txt (written the moment BeginRun's own child actually starts,
    // removed on every path this window sees a half end) is still there and this is not the
    // window's own currently active run: a half that died together with the window itself (a
    // forced session end, a power cut) never reaches KillActiveRun or MarkUnknownAndReset to
    // write gui-killed.txt or remove this marker, so it is the only trace left that the half
    // started and this window never saw it end. Resolved here (EvidenceStore), from the raw
    // marker file and the caller's own activeFolder, into the one fact StateDeriver,
    // Banner.Compute and PendingRunFinder each need: never a live, currently running half.
    public bool HasStaleRunStartedMarker { get; init; }
    public string? PowerCycleVerdict { get; init; }

    public bool ReadSucceeded => Result is not null;
}
