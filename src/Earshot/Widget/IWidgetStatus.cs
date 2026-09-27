namespace Earshot.Widget;

// The widget's whole public surface: one snapshot, two events, and the three things the owner can do from
// the UI (claim, forget, ask for an immediate retry). Everything else is read from Current.
public interface IWidgetStatus
{
    WidgetSnapshot Current { get; }

    event EventHandler? Changed;                          // UI thread

    event EventHandler<CaseOpenedEventArgs>? CaseOpened;  // UI thread, owned advertisements only

    Task<ClaimOutcome> ClaimAsync(CancellationToken ct);  // UI thread

    void ForgetClaim();

    Task RefreshAsync();                                  // one immediate watcher retry when stopped
}
