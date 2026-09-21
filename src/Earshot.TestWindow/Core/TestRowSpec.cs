namespace Earshot.TestWindow.Core;

// What the required-transition table asks of a second half, per test.
internal enum PowerCycleRequirement
{
    None,
    AnyStart,
    Restart,
    FullShutDown,
}

// The facts about one manifest row that StateDeriver needs. Kept separate from Data\tests.json
// and Manifest.cs on purpose: a unit test can hand StateDeriver a spec of its own without a
// manifest file existing yet.
internal sealed class TestRowSpec
{
    public required string TestId { get; init; }
    public required int Halves { get; init; }
    public PowerCycleRequirement PowerCycleRequirement { get; init; } = PowerCycleRequirement.None;

    // The half markers. A criterion id in SecondHalfOnlyCriteriaIds is decisive: its
    // presence alone means this result.json is a second half. FirstHalfOnlyFindingNames exists
    // only for test 10's first half, which records a finding and no criteria at all.
    public IReadOnlyList<string> FirstHalfOnlyCriteriaIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SecondHalfOnlyCriteriaIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FirstHalfOnlyFindingNames { get; init; } = Array.Empty<string>();

    // 05 only: a first half with no resume.txt is the whole test, because the owner declined the
    // optional restart.
    public bool FirstHalfAloneIsCompleteWhenDeclined { get; init; }
}

internal enum RowStateKind
{
    NotRun,
    WaitingForShutDown,
    WaitingForRestart,
    Passed,
    Failed,
    Inconclusive,
    Unknown,
    StoppedBeforeAnyStep,

    // Never produced by StateDeriver.Derive (which reads a single row's own evidence only);
    // MainForm.cs overlays this on top of the ordinary derived state for the elevated launch
    // site's rows, from ElevationGate, which reads the rehearsal's own result.json and the
    // harness files' own write times.
    Locked,
}

// What StateDeriver decided for one row, from disk alone. IsGreen is the one bit tests pin: only
// a Passed row with no qualifier is ever green, and "Passed" never appears for anything else.
internal sealed record DerivedRowState
{
    public required RowStateKind Kind { get; init; }
    public string? Qualifier { get; init; }
    public string? Reason { get; init; }
    public string? LeftAtRest { get; init; }
    public string? HistoryNote { get; init; }

    public bool IsGreen => Kind == RowStateKind.Passed && Qualifier is null;
}
