namespace Earshot.TestWindow.Core;

// What test-gui.md section 9.3's required-transition table asks of a second half, per test.
internal enum PowerCycleRequirement
{
    None,
    AnyStart,
    Restart,
    FullShutDown,
}

// The facts about one manifest row that StateDeriver needs. Kept separate from Data\tests.json
// and Manifest.cs (slice S2) on purpose: S1 and S2 build side by side (test-gui.md section 15),
// and a unit test can hand StateDeriver a spec of its own without a manifest file existing yet.
internal sealed class TestRowSpec
{
    public required string TestId { get; init; }
    public required int Halves { get; init; }
    public PowerCycleRequirement PowerCycleRequirement { get; init; } = PowerCycleRequirement.None;

    // The half markers, section 6.1. A criterion id in SecondHalfOnlyCriteriaIds is decisive: its
    // presence alone means this result.json is a second half. FirstHalfOnlyFindingNames exists
    // only for test 10's first half, which records a finding and no criteria at all.
    public IReadOnlyList<string> FirstHalfOnlyCriteriaIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SecondHalfOnlyCriteriaIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FirstHalfOnlyFindingNames { get; init; } = Array.Empty<string>();

    // 05 only (section 6.2's exception): a first half with no resume.txt is the whole test,
    // because the owner declined the optional restart.
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

    // section 10.2: never produced by StateDeriver.Derive (which reads a single row's own
    // evidence only); MainForm.cs overlays this on top of the ordinary derived state for the
    // elevated launch site's rows, from ElevationGate, which reads the rehearsal's own result.json
    // and the harness files' own write times.
    Locked,
}

// What StateDeriver decided for one row, from disk alone. IsGreen is the one bit T4 pins: only a
// Passed row with no qualifier is ever green, and "Passed" never appears for anything else.
internal sealed record DerivedRowState
{
    public required RowStateKind Kind { get; init; }
    public string? Qualifier { get; init; }
    public string? Reason { get; init; }
    public string? LeftAtRest { get; init; }
    public string? HistoryNote { get; init; }

    public bool IsGreen => Kind == RowStateKind.Passed && Qualifier is null;
}
