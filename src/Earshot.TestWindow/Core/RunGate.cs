namespace Earshot.TestWindow.Core;

// B1 (review round 1): exactly one child may exist at a time. Every start path must refuse while
// one is active, and a message from a runner that is no longer the active one must be dropped,
// never routed to whichever runner happens to be active when it arrives. Both rules are decided
// here, from identity alone, so MainForm's own start handlers and message-routing closures call
// the same two functions rather than repeating (and, as review round 1 found, sometimes omitting)
// the check.
internal static class RunGate
{
    internal static bool CanStart(object? activeRunner) => activeRunner is null;

    internal static bool ShouldProcessMessage(object? activeRunner, object sourceRunner)
    {
        ArgumentNullException.ThrowIfNull(sourceRunner);
        return ReferenceEquals(activeRunner, sourceRunner);
    }
}
