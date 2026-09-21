namespace Earshot.TestWindow.Core;

// Exactly one child may exist at a time. Every start path must refuse while one is active, and a
// message from a runner that is no longer the active one must be dropped, never routed to
// whichever runner happens to be active when it arrives. Both rules are decided here, from
// identity alone, so MainForm's own start handlers and message-routing closures call the same two
// functions rather than repeating (and sometimes omitting) the check.
internal static class RunGate
{
    internal static bool CanStart(object? activeRunner) => activeRunner is null;

    // The one combined check every route that can start a child must pass, so a new route can
    // never forget either rule by only remembering one of them. A row exempt from the banner lock
    // (00 Restore, the only way off a red banner) still needs no active runner; nothing is ever
    // exempt from the single-runner rule.
    internal static bool CanStart(object? activeRunner, bool bannerRowsLockedExceptRestore, bool rowIsExemptFromBannerLock)
    {
        if (activeRunner is not null)
        {
            return false;
        }

        return !bannerRowsLockedExceptRestore || rowIsExemptFromBannerLock;
    }

    internal static bool ShouldProcessMessage(object? activeRunner, object sourceRunner)
    {
        ArgumentNullException.ThrowIfNull(sourceRunner);
        return ReferenceEquals(activeRunner, sourceRunner);
    }
}
