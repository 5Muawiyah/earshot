namespace Earshot.TestWindow.Core;

// The bucket Run all's own end-of-sequence summary puts one row's derived state into. Computed
// fresh from RunAllOrder's own items and StateDeriver's own answer for each, never from a separate
// record of what the sequence itself did: a row it skipped over (Locked) and a row nobody has ever
// tried read the same way here as they would from the row list itself, which already shows each
// row's own state and, for Locked, its own reason (Copy.LockedDetail) right beside it.
internal static class RunAllSummary
{
    // A qualified pass (an unconfirmed shut down, an earlier build, and the rest) is never counted
    // as Worked here either: the row and the Result view both already refuse to read a qualified
    // pass as a clean one ("This test worked, but it does not count yet."), and the end-of-sequence
    // sentence must say the same thing about it, not fold it into "N tests worked" as though the
    // question those tests exist to settle were actually settled. CouldNotTell is where it lands,
    // the same bucket Inconclusive and Unknown already read into: the test ran and produced
    // something, but not something this window is willing to call a confirmed pass.
    internal static RunAllSummaryBucket Classify(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Kind switch
        {
            RowStateKind.Passed => state.Qualifier is null ? RunAllSummaryBucket.Worked : RunAllSummaryBucket.CouldNotTell,
            RowStateKind.Failed => RunAllSummaryBucket.DidNotWork,
            RowStateKind.Inconclusive => RunAllSummaryBucket.CouldNotTell,
            RowStateKind.Unknown => RunAllSummaryBucket.CouldNotTell,
            _ => RunAllSummaryBucket.NotRunYet,
        };
    }
}

internal enum RunAllSummaryBucket
{
    Worked,
    DidNotWork,
    CouldNotTell,
    NotRunYet,
}
