namespace Earshot.Widget;

// The widget's whole public surface: one snapshot, two events, and the three things the owner can do from
// the UI (claim, forget, ask for an immediate retry). Everything else is read from Current.
public interface IWidgetStatus
{
    WidgetSnapshot Current { get; }

    // True once phase 0 has proved a signal threshold to claim against; false forever until it does. The
    // one thing the UI can know ahead of a claim attempt without guessing, so the claim trigger can be
    // shown disabled rather than left to fail after the owner has already opened his case.
    bool ClaimAvailable { get; }

    event EventHandler? Changed;                          // UI thread

    event EventHandler<CaseOpenedEventArgs>? CaseOpened;  // UI thread, owned advertisements only

    event EventHandler<OwnedReadingEventArgs>? OwnedReadingApplied; // UI thread, every owned reading

    Task<ClaimOutcome> ClaimAsync(CancellationToken ct);  // UI thread

    void ForgetClaim();

    Task RefreshAsync();                                  // one immediate watcher retry when stopped
}
