namespace Earshot.TestWindow.Core;

internal enum RunAllAdvanceDecision
{
    // Never attempted at all: no evidence folder exists yet for this row. Safe to auto-start.
    StartFresh,

    // A clean, accepted pass: move on to the next item.
    Advance,

    // Anything else, including a declined start (a start that was stopped before any step) and a
    // power-cycle boundary: needs the owner's attention, never auto-retried.
    Halt,
}

// AdvanceRunAll used to treat StoppedBeforeAnyStep as "fresh enough" to start again, bypassing
// RunAllHalt.ShouldHalt entirely and restarting a declined test forever. RunAllHaltTests.cs
// already pinned ShouldHalt's own answer for StoppedBeforeAnyStep (true), but nothing tested the
// caller that skipped asking it; this class is that caller's own decision, tested directly, so a
// future change cannot bypass ShouldHalt the same way again.
internal static class RunAllAdvance
{
    internal static RunAllAdvanceDecision Decide(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Kind == RowStateKind.NotRun)
        {
            return RunAllAdvanceDecision.StartFresh;
        }

        if (state.Kind == RowStateKind.Passed && !RunAllHalt.ShouldHalt(state))
        {
            return RunAllAdvanceDecision.Advance;
        }

        return RunAllAdvanceDecision.Halt;
    }
}
