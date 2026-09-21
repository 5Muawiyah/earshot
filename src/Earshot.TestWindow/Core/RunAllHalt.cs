namespace Earshot.TestWindow.Core;

// The halt rule, decided purely from the row's own derived state, never from which button was
// clicked: the halt is decided from result.json, never from the click.
internal static class RunAllHalt
{
    internal static bool ShouldHalt(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // "A first half always stops at the power-cycle boundary": both waiting kinds are a halt,
        // never an auto-advance, whatever the eventual second half turns out to say.
        if (state.Kind is RowStateKind.WaitingForShutDown or RowStateKind.WaitingForRestart)
        {
            return true;
        }

        // "A second or only half that is not a pass halts the sequence."
        if (state.Kind != RowStateKind.Passed)
        {
            return true;
        }

        // "So does leftAtRest of no or unknown." A missing finding reads the same as unknown
        // (Copy.LeftAtRestText's own AtRestNoSuchFinding: "treat it as not known"), so it halts
        // here too rather than being read as silently fine.
        return state.LeftAtRest is null or "no" or "unknown";
    }
}
