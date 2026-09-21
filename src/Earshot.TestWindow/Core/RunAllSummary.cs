namespace Earshot.TestWindow.Core;

// The bucket Run all's own end-of-sequence summary puts one row's derived state into. Computed
// fresh from RunAllOrder's own items and StateDeriver's own answer for each, never from a separate
// record of what the sequence itself did: a row it skipped over (Locked) and a row nobody has ever
// tried read the same way here as they would from the row list itself, which already shows each
// row's own state and, for Locked, its own reason (Copy.LockedDetail) right beside it.
internal static class RunAllSummary
{
    internal static RunAllSummaryBucket Classify(RowStateKind kind) => kind switch
    {
        RowStateKind.Passed => RunAllSummaryBucket.Worked,
        RowStateKind.Failed => RunAllSummaryBucket.DidNotWork,
        RowStateKind.Inconclusive => RunAllSummaryBucket.CouldNotTell,
        RowStateKind.Unknown => RunAllSummaryBucket.CouldNotTell,
        _ => RunAllSummaryBucket.NotRunYet,
    };
}

internal enum RunAllSummaryBucket
{
    Worked,
    DidNotWork,
    CouldNotTell,
    NotRunYet,
}
